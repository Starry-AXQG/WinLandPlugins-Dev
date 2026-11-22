using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaDeck.Services;

public sealed record PotPlayerInstanceSnapshot
{
    public required IntPtr Hwnd { get; init; }
    public required int ProcessId { get; init; }
    public string? ExePath { get; init; }
    public string? FullFilePath { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public byte[]? ThumbnailBytes { get; init; }
    public byte[]? AppIconBytes { get; init; }
}

/// <summary>
/// PotPlayer 原生多实例 Win32 IPC 适配服务（绕过 PotPlayer 多开时的 Windows SMTC 冲突与元数据覆盖）：
///   - 枚举全部 PotPlayer64 / PotPlayer 顶层窗口，逐窗口独立查询：
///       · WM_USER 0x5006 (POT_GET_PLAY_STATUS): 播放/暂停/停止状态
///       · WM_USER 0x5002 (POT_GET_TOTAL_TIME): 总时长 (ms)
///       · WM_USER 0x5004 (POT_GET_CURRENT_TIME): 当前进度 (ms)
///       · WM_USER 0x6020 (POT_GET_PLAYFILE_NAME): 通过 WM_COPYDATA 同步回传该窗口精确本地文件路径
///   - 按每个窗口各自的精确文件路径独立提取 FLAC/Shell 封面与标题/歌手元数据，绝不串台；
///   - 控制指令（播放/暂停/Seek/上/下一曲）直接发送至目标 hWnd，精准隔离每个窗口。
/// </summary>
public sealed class PotPlayerService : IDisposable
{
    private const uint WM_USER = 0x0400;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_QUIT = 0x0012;

    private const nint POT_GET_TOTAL_TIME = 0x5002;
    private const nint POT_GET_CURRENT_TIME = 0x5004;
    private const nint POT_SET_CURRENT_TIME = 0x5005;
    private const nint POT_GET_PLAY_STATUS = 0x5006;
    private const nint POT_SET_PLAY_STATUS = 0x5007;
    private const nint POT_GET_PLAYFILE_NAME = 0x6020;

    private const nint CMD_PREVIOUS = 10123;
    private const nint CMD_NEXT = 10124;

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private sealed class CachedTrackMeta
    {
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string Album { get; init; } = "";
        public byte[]? CoverBytes { get; init; }
    }

    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, CachedTrackMeta> _trackMetaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]?> _exeIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Action> _ipcQueue = new();
    private readonly Thread _ipcThread;
    private readonly WndProcDelegate _wndProcDelegate;

    private IntPtr _receiverHwnd;
    private string? _capturedCopyDataPath;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public PotPlayerService(Action<string> log)
    {
        _log = log;
        _wndProcDelegate = ReceiverWndProc;

        _ipcThread = new Thread(IpcThreadLoop)
        {
            IsBackground = true,
            Name = "MediaDeck.PotPlayerIpc",
        };
        _ipcThread.Start();
    }

    private void IpcThreadLoop()
    {
        string className = $"MediaDeckPotReceiver_{Environment.ProcessId}_{Guid.NewGuid():N}";
        IntPtr hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        _receiverHwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        try
        {
            foreach (var work in _ipcQueue.GetConsumingEnumerable())
            {
                try { work(); }
                catch { }
            }
        }
        catch { }
        finally
        {
            if (_receiverHwnd != IntPtr.Zero)
            {
                DestroyWindow(_receiverHwnd);
                _receiverHwnd = IntPtr.Zero;
            }
        }
    }

    private IntPtr ReceiverWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COPYDATA && lParam != IntPtr.Zero)
        {
            try
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                if (cds.dwData == POT_GET_PLAYFILE_NAME && cds.cbData > 0 && cds.lpData != IntPtr.Zero)
                {
                    byte[] raw = new byte[cds.cbData];
                    Marshal.Copy(cds.lpData, raw, 0, cds.cbData);
                    string decoded = Encoding.UTF8.GetString(raw).TrimEnd('\0').Trim();
                    if (!string.IsNullOrWhiteSpace(decoded))
                    {
                        _capturedCopyDataPath = decoded;
                    }
                }
            }
            catch { }
            return new IntPtr(1);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private string? QueryFilePathOnIpcThread(IntPtr potHwnd)
    {
        if (_disposed || potHwnd == IntPtr.Zero) return null;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ipcQueue.Add(() =>
            {
                if (_receiverHwnd == IntPtr.Zero)
                {
                    tcs.TrySetResult(null);
                    return;
                }
                _capturedCopyDataPath = null;
                SendMessageTimeout(potHwnd, WM_USER, POT_GET_PLAYFILE_NAME, _receiverHwnd, SMTO_ABORTIFHUNG, 250, out _);
                tcs.TrySetResult(_capturedCopyDataPath);
            });
            return tcs.Task.Wait(400) ? tcs.Task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>枚举当前所有活跃的 PotPlayer 窗口，逐窗口独立读取播放状态、进度、文件路径、封面与元数据。</summary>
    public async Task<List<PotPlayerInstanceSnapshot>> GetActiveInstancesAsync()
    {
        var result = new List<PotPlayerInstanceSnapshot>();
        if (_disposed) return result;

        var hwnds = new List<(IntPtr Hwnd, int Pid, string WindowTitle)>();
        var clsSb = new StringBuilder(64);
        var titleSb = new StringBuilder(512);

        EnumWindows((hWnd, _) =>
        {
            clsSb.Clear();
            if (GetClassName(hWnd, clsSb, clsSb.Capacity) > 0)
            {
                string cls = clsSb.ToString();
                if (cls.Equals("PotPlayer64", StringComparison.OrdinalIgnoreCase) ||
                    cls.Equals("PotPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    titleSb.Clear();
                    GetWindowText(hWnd, titleSb, titleSb.Capacity);
                    hwnds.Add((hWnd, (int)pid, titleSb.ToString()));
                }
            }
            return true;
        }, IntPtr.Zero);

        foreach (var (hWnd, pid, winTitle) in hwnds)
        {
            try
            {
                if (SendMessageTimeout(hWnd, WM_USER, POT_GET_PLAY_STATUS, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out IntPtr statusRes) == IntPtr.Zero)
                    continue;

                long status = statusRes.ToInt64(); // -1/0 = Stopped, 1 = Paused, 2 = Playing
                if (status <= 0) continue; // 仅展示已加载媒体的播放/暂停窗口

                bool isPlaying = status == 2;
                long totalMs = SendMessageTimeout(hWnd, WM_USER, POT_GET_TOTAL_TIME, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out IntPtr totRes) != IntPtr.Zero
                    ? Math.Max(0, totRes.ToInt64()) : 0;
                long curMs = SendMessageTimeout(hWnd, WM_USER, POT_GET_CURRENT_TIME, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out IntPtr curRes) != IntPtr.Zero
                    ? Math.Max(0, curRes.ToInt64()) : 0;

                string? fullPath = QueryFilePathOnIpcThread(hWnd);

                string? exePath = null;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    exePath = p.MainModule?.FileName;
                }
                catch { }

                byte[]? appIcon = null;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    if (!_exeIconCache.TryGetValue(exePath, out appIcon))
                    {
                        appIcon = await PolicyConfigInterop.ExtractExeIconPngAsync(exePath).ConfigureAwait(false);
                        _exeIconCache[exePath] = appIcon;
                    }
                }

                string title = "";
                string artist = "";
                string album = "";
                byte[]? coverBytes = null;

                if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
                {
                    var meta = await GetOrLoadTrackMetaAsync(fullPath).ConfigureAwait(false);
                    title = meta.Title;
                    artist = meta.Artist;
                    album = meta.Album;
                    coverBytes = meta.CoverBytes;
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = CleanPotPlayerWindowTitle(winTitle);
                    if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(fullPath))
                    {
                        title = Path.GetFileNameWithoutExtension(fullPath);
                    }
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = "PotPlayer";
                    }
                }

                result.Add(new PotPlayerInstanceSnapshot
                {
                    Hwnd = hWnd,
                    ProcessId = pid,
                    ExePath = exePath,
                    FullFilePath = fullPath,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    IsPlaying = isPlaying,
                    Position = TimeSpan.FromMilliseconds(curMs),
                    Duration = TimeSpan.FromMilliseconds(totalMs),
                    ThumbnailBytes = coverBytes,
                    AppIconBytes = appIcon,
                });
            }
            catch { }
        }

        return result;
    }

    // ==================== 定向逐窗口控制（不抢焦点、不误触其他窗口） ====================

    public static void TogglePlayPause(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        SendMessageTimeout(hWnd, WM_USER, POT_SET_PLAY_STATUS, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out _);
    }

    public static void Pause(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        SendMessageTimeout(hWnd, WM_USER, POT_SET_PLAY_STATUS, new IntPtr(1), SMTO_ABORTIFHUNG, 200, out _);
    }

    public static void Play(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        SendMessageTimeout(hWnd, WM_USER, POT_SET_PLAY_STATUS, new IntPtr(2), SMTO_ABORTIFHUNG, 200, out _);
    }

    public static void Seek(IntPtr hWnd, TimeSpan target)
    {
        if (hWnd == IntPtr.Zero) return;
        long ms = Math.Max(0, (long)target.TotalMilliseconds);
        SendMessageTimeout(hWnd, WM_USER, POT_SET_CURRENT_TIME, (IntPtr)ms, SMTO_ABORTIFHUNG, 200, out _);
    }

    public static void Previous(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        PostMessage(hWnd, WM_COMMAND, CMD_PREVIOUS, IntPtr.Zero);
    }

    public static void Next(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        PostMessage(hWnd, WM_COMMAND, CMD_NEXT, IntPtr.Zero);
    }

    // ==================== 独立文件元数据与封面提取（按精确路径缓存，零串台） ====================

    private async Task<CachedTrackMeta> GetOrLoadTrackMetaAsync(string fullPath)
    {
        if (_trackMetaCache.TryGetValue(fullPath, out var cached))
            return cached;

        string title = "";
        string artist = "";
        string album = "";
        byte[]? cover = null;

        if (fullPath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
        {
            (title, artist, album, cover) = ReadFlacMetaAndPicture(fullPath);
        }

        // 若无内嵌 FLAC 封面，尝试同目录封面图或 Windows Shell 缩略图（支持 MP3/M4A 封面与 MP4/MKV 视频帧）
        if (cover is null)
        {
            cover = TryReadSidecarCover(fullPath)
                    ?? await PolicyConfigInterop.ExtractFileThumbnailPngAsync(fullPath, 160).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            string stem = Path.GetFileNameWithoutExtension(fullPath).Trim();
            if (stem.Contains(" - ", StringComparison.Ordinal))
            {
                int idx = stem.IndexOf(" - ", StringComparison.Ordinal);
                title = stem[..idx].Trim();
                if (string.IsNullOrWhiteSpace(artist))
                {
                    artist = stem[(idx + 3)..].Trim();
                }
            }
            else
            {
                title = stem;
            }
        }

        var meta = new CachedTrackMeta
        {
            Title = title,
            Artist = artist,
            Album = album,
            CoverBytes = cover,
        };
        _trackMetaCache[fullPath] = meta;
        return meta;
    }

    private static (string Title, string Artist, string Album, byte[]? Picture) ReadFlacMetaAndPicture(string flacPath)
    {
        string title = "", artist = "", album = "";
        byte[]? picture = null;
        try
        {
            using var fs = new FileStream(flacPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> hdr = stackalloc byte[4];
            if (fs.Read(hdr) < 4 || hdr[0] != 'f' || hdr[1] != 'L' || hdr[2] != 'a' || hdr[3] != 'C')
                return (title, artist, album, picture);

            bool isLast = false;
            while (!isLast && fs.Position < fs.Length)
            {
                if (fs.Read(hdr) < 4) break;
                isLast = (hdr[0] & 0x80) != 0;
                int blockType = hdr[0] & 0x7F;
                int blockLen = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                if (blockLen <= 0 || fs.Position + blockLen > fs.Length) break;

                if (blockType == 4 && blockLen <= 2 * 1024 * 1024) // VORBIS_COMMENT
                {
                    byte[] buf = new byte[blockLen];
                    if (fs.Read(buf, 0, blockLen) < blockLen) break;
                    int pos = 0;
                    if (pos + 4 <= buf.Length)
                    {
                        int vendorLen = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4));
                        pos += 4 + vendorLen;
                        if (pos + 4 <= buf.Length)
                        {
                            int count = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4));
                            pos += 4;
                            for (int i = 0; i < count && pos + 4 <= buf.Length; i++)
                            {
                                int len = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4));
                                pos += 4;
                                if (len <= 0 || pos + len > buf.Length) break;
                                string entry = Encoding.UTF8.GetString(buf, pos, len);
                                pos += len;
                                int eq = entry.IndexOf('=');
                                if (eq <= 0) continue;
                                string key = entry[..eq].Trim();
                                string val = entry[(eq + 1)..].Trim();
                                if (key.Equals("TITLE", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(title))
                                    title = val;
                                else if (key.Equals("ARTIST", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(artist))
                                    artist = val;
                                else if (key.Equals("ALBUM", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(album))
                                    album = val;
                            }
                        }
                    }
                    continue;
                }

                if (blockType == 6 && picture is null && blockLen <= 12 * 1024 * 1024) // METADATA_BLOCK_PICTURE
                {
                    byte[] buf = new byte[blockLen];
                    if (fs.Read(buf, 0, blockLen) < blockLen) break;
                    int pos = 4; // skip picture type (4 bytes)
                    if (pos + 4 <= buf.Length)
                    {
                        int mimeLen = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(pos, 4));
                        pos += 4 + mimeLen;
                        if (pos + 4 <= buf.Length)
                        {
                            int descLen = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(pos, 4));
                            pos += 4 + descLen + 16; // skip width/height/depth/colors (16 bytes)
                            if (pos + 4 <= buf.Length)
                            {
                                int dataLen = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(pos, 4));
                                pos += 4;
                                if (dataLen > 0 && pos + dataLen <= buf.Length)
                                {
                                    picture = new byte[dataLen];
                                    Buffer.BlockCopy(buf, pos, picture, 0, dataLen);
                                }
                            }
                        }
                    }
                    continue;
                }

                fs.Seek(blockLen, SeekOrigin.Current);
            }
        }
        catch { }
        return (title, artist, album, picture);
    }

    private static byte[]? TryReadSidecarCover(string fullPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(dir)) return null;
            string stem = Path.GetFileNameWithoutExtension(fullPath);

            string[] candidates =
            [
                Path.Combine(dir, stem + ".jpg"),
                Path.Combine(dir, stem + ".png"),
                Path.Combine(dir, "cover.jpg"),
                Path.Combine(dir, "cover.png"),
                Path.Combine(dir, "folder.jpg"),
                Path.Combine(dir, "folder.png"),
            ];
            foreach (var c in candidates)
            {
                if (File.Exists(c) && new FileInfo(c).Length <= 8 * 1024 * 1024)
                {
                    return File.ReadAllBytes(c);
                }
            }
        }
        catch { }
        return null;
    }

    private static string CleanPotPlayerWindowTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim();
        foreach (var suffix in new[] { " - PotPlayer 64-bit", " - PotPlayer", " - Daum PotPlayer" })
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^suffix.Length].Trim();
                break;
            }
        }
        if (s.Equals("PotPlayer", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("PotPlayer 64-bit", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        return s;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ipcQueue.CompleteAdding(); } catch { }
        try { if (_receiverHwnd != IntPtr.Zero) PostMessage(_receiverHwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
}
