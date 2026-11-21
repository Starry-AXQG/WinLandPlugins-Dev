using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MediaDeck.Services;

public sealed record ResolvedAppInfo(string DisplayName, byte[]? IconPngBytes);

/// <summary>
/// Windows Core Audio 默认设备切换（IPolicyConfig COM）、Shell AppsFolder AUMID 解析（与系统媒体管理器同源）、
/// Win32 窗口激活、定向 WM_APPCOMMAND 媒体控制及图标提取辅助。
/// </summary>
internal static class PolicyConfigInterop
{
    public const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    public const byte VK_MEDIA_PREV_TRACK = 0xB1;
    public const byte VK_MEDIA_STOP = 0xB2;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    public const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    public const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    public const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;

    private const uint WM_APPCOMMAND = 0x0319;

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClientComObject { }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    public static bool SetDefaultAudioEndpoint(string deviceId)
    {
        try
        {
            var client = (IPolicyConfig)new PolicyConfigClientComObject();
            int hr1 = client.SetDefaultEndpoint(deviceId, ERole.Console);
            int hr2 = client.SetDefaultEndpoint(deviceId, ERole.Multimedia);
            int hr3 = client.SetDefaultEndpoint(deviceId, ERole.Communications);
            return hr1 == 0 || hr2 == 0 || hr3 == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- Shell AppsFolder AUMID 解析（解析 Zen/Firefox 哈希 AUMID、Spotify Store 应用等）----

    private static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private const uint SIIGBF_RESIZETOFIT = 0x00000000;
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_ICONONLY = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemInKnownFolder(
        ref Guid kfid,
        uint dwKFFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string pszItem,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    /// <summary>
    /// 通过 Windows Shell AppsFolder 直接解析任意 AUMID（含 Firefox/Zen 16位哈希 AUMID、UWP/MSIX 应用）
    /// 为官方显示名与图标 PNG 字节流，与 Windows 系统媒体管理器同源。
    /// </summary>
    public static async Task<ResolvedAppInfo?> ResolveFromAppsFolderAsync(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;

        try
        {
            var folderId = FolderIdAppsFolder;
            var iid = IidIShellItem;
            int hr = SHCreateItemInKnownFolder(ref folderId, 0, aumid, ref iid, out var obj);
            if (hr != 0 || obj is not IShellItem item) return null;

            string displayName = "";
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var name) == 0 && !string.IsNullOrWhiteSpace(name))
            {
                displayName = name.Trim();
            }

            byte[]? iconPng = null;
            if (obj is IShellItemImageFactory factory)
            {
                var sz = new SIZE { cx = 48, cy = 48 };
                int hrImg = factory.GetImage(sz, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hbm);
                if (hrImg != 0 || hbm == IntPtr.Zero)
                {
                    hrImg = factory.GetImage(sz, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out hbm);
                }

                if (hrImg == 0 && hbm != IntPtr.Zero)
                {
                    try
                    {
                        iconPng = await HBitmapToPngAsync(hbm).ConfigureAwait(false);
                    }
                    finally
                    {
                        DeleteObject(hbm);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(displayName) && iconPng is null) return null;
            return new ResolvedAppInfo(displayName, iconPng);
        }
        catch
        {
            return null;
        }
    }

    // ---- 全局媒体按键 & 定向窗口媒体命令 ----

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public static void SendMediaKey(byte vk)
    {
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>向指定进程主窗口定向投递 WM_APPCOMMAND（如播放/暂停、切歌），不误触其他 SMTC 播放器。</summary>
    public static bool SendAppCommandToProcess(int processId, int appCommand)
    {
        if (processId <= 0) return false;
        IntPtr hwnd = FindMainWindowForPid((uint)processId);
        if (hwnd == IntPtr.Zero) return false;

        IntPtr lParam = (IntPtr)(appCommand << 16);
        return PostMessage(hwnd, WM_APPCOMMAND, hwnd, lParam);
    }

    // ---- 激活来源程序窗口 ----

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    public static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var buf = new char[len + 1];
        int copied = GetWindowText(hWnd, buf, buf.Length);
        return copied > 0 ? new string(buf, 0, copied) : string.Empty;
    }

    public static bool ActivateWindowByProcessIdOrName(int processId, string? hintProcessName, string? displayName = null)
    {
        IntPtr targetHwnd = IntPtr.Zero;

        if (processId > 0)
        {
            targetHwnd = FindMainWindowForPid((uint)processId);
        }

        if (targetHwnd == IntPtr.Zero)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(hintProcessName))
            {
                candidates.Add(CleanProcessHint(hintProcessName));
            }
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                var low = displayName.Trim().ToLowerInvariant();
                if (low.Contains("zen")) candidates.Add("zen");
                if (low.Contains("firefox")) candidates.Add("firefox");
                if (low.Contains("spotify")) candidates.Add("Spotify");
                if (low.Contains("edge")) candidates.Add("msedge");
                if (low.Contains("chrome")) candidates.Add("chrome");
                if (low.Contains("qq")) candidates.Add("QQMusic");
                if (low.Contains("网易")) candidates.Add("cloudmusic");
                candidates.Add(displayName.Trim().Replace(" ", ""));
            }

            foreach (var name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var hwnd = proc.MainWindowHandle != IntPtr.Zero
                            ? proc.MainWindowHandle
                            : FindMainWindowForPid((uint)proc.Id);
                        if (hwnd != IntPtr.Zero)
                        {
                            targetHwnd = hwnd;
                            break;
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
                if (targetHwnd != IntPtr.Zero) break;
            }
        }

        if (targetHwnd == IntPtr.Zero) return false;
        return BringWindowToFront(targetHwnd);
    }

    public static IntPtr FindMainWindowForPid(uint pid)
    {
        IntPtr bestVisible = IntPtr.Zero;
        IntPtr bestHidden = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint winPid);
            if (winPid != pid) return true;

            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            if (IsWindowVisible(hWnd))
            {
                bestVisible = hWnd;
                return false;
            }

            if (bestHidden == IntPtr.Zero)
            {
                bestHidden = hWnd;
            }
            return true;
        }, IntPtr.Zero);

        return bestVisible != IntPtr.Zero ? bestVisible : bestHidden;
    }

    public static bool BringWindowToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        else ShowWindow(hWnd, SW_SHOW);

        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint curThread = GetCurrentThreadId();

        bool attached = false;
        try
        {
            if (fgThread != 0 && fgThread != curThread)
            {
                attached = AttachThreadInput(curThread, fgThread, true);
            }
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }
    }

    public static string CleanProcessHint(string raw)
    {
        var s = raw.Trim();
        int bang = s.IndexOf('!');
        if (bang >= 0 && bang + 1 < s.Length) s = s[(bang + 1)..];
        int slash = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        if (slash >= 0 && slash + 1 < s.Length) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s;
    }

    // ---- 图标 HBITMAP / EXE 提取为 PNG ----

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public short bmPlanes;
        public short bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    private static async Task<byte[]?> HBitmapToPngAsync(IntPtr hBitmap)
    {
        if (hBitmap == IntPtr.Zero) return null;
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;

        int width = bmp.bmWidth;
        int height = bmp.bmHeight;
        if (width <= 0 || height <= 0) return null;

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        byte[] pixels = new byte[width * height * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            int lines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref bmi, 0);
            if (lines == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        using var mem = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        mem.Seek(0);
        var bytes = new byte[mem.Size];
        await mem.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    public static async Task<byte[]?> ExtractExeIconPngAsync(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;

        SHFILEINFO shinfo = new();
        IntPtr res = SHGetFileInfo(exePath, 0, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero) return null;

        try
        {
            if (!GetIconInfo(shinfo.hIcon, out var iconInfo)) return null;
            try
            {
                if (iconInfo.hbmColor == IntPtr.Zero) return null;
                return await HBitmapToPngAsync(iconInfo.hbmColor).ConfigureAwait(false);
            }
            finally
            {
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }
}
