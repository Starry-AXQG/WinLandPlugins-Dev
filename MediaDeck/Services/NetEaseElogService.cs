using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDeck.Services;

public sealed record NetEaseTrackSnapshot(
    string SongId,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset UpdatedAt,
    string? CoverUrl,
    byte[]? CoverBytes);

/// <summary>
/// 网易云音乐 3.x 深度状态追踪服务（移植自 MIT 开源方案 netease-cloudmusic-detector）：
///   - 自动发现桌面版与 Microsoft Store (MSIX) 版的 cloudmusic.elog 及 webdata\file\playingList；
///   - 实时解码 cloudmusic.elog 中的播放事件流：
///     · checkPlayPrivilege / playOneTrackInPlayingList → 歌曲 ID、歌名、歌手、专辑、时长、封面 URL
///     · native播放资源load完成 → 真实出声锚点
///     · setPlayingPosition → 用户在网易云内拖动进度条的真实秒数
///     · native播放state → 1=正在播放，2=已暂停
///   - 自动下载并缓存专辑封面字节流，供灵动岛卡片直接展示。
/// </summary>
public sealed partial class NetEaseElogService : IDisposable
{
    private const int MaxInitialTailBytes = 1536 * 1024; // 首次启动读取末尾 1.5MB
    private const int MaxCarryBytes = 512 * 1024;

    private static readonly byte[] DecodeTable = BuildDecodeTable();
    private static readonly byte EncodedNewline = FindEncodedByte((byte)'\n');

    [GeneratedRegex(@"^\[(\d+):(\d+):(\d{4}/\d{6}):(\d+):([A-Z]+):([\w.\-]+)\((\d+)\)\](?:\s+\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\])?")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"""setPlayingPosition""\s*,\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex SetPositionRegex();

    [GeneratedRegex(@"""native[^""]*state""\s*,\s*(\d+)")]
    private static partial Regex NativeStateRegex();

    private sealed record TrackMeta(
        string SongId,
        string Title,
        string Artist,
        string Album,
        TimeSpan Duration,
        string? CoverUrl);

    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, byte[]?> _coverCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _coverInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackMeta> _playingListMap = new(StringComparer.OrdinalIgnoreCase);

    private string? _elogPath;
    private string? _playingListPath;
    private DateTime _playingListLastWrite = DateTime.MinValue;
    private long _offset;
    private byte[] _carry = [];
    private bool _bootstrapped;

    // 播放状态机
    private string _songId = "";
    private TrackMeta? _track;
    private bool _isPlaying;
    private double _pausedPositionSec;
    private DateTimeOffset _playingAnchorUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastEventUtc = DateTimeOffset.UtcNow;

    public NetEaseElogService(Action<string> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    private static byte[] BuildDecodeTable()
    {
        var table = new byte[256];
        for (int b = 0; b < 256; b++)
        {
            int high = b >> 4;
            int low = b & 0x0f;
            int hex = (high ^ ((low + 8) % 16)) % 16;
            table[b] = (byte)((hex * 16 + (b >> 6) * 4 + ((~high) & 3)) & 0xff);
        }
        return table;
    }

    private static byte FindEncodedByte(byte plain)
    {
        for (int b = 0; b < 256; b++)
        {
            if (DecodeTable[b] == plain) return (byte)b;
        }
        return 0x91;
    }

    /// <summary>轮询增量日志并返回当前网易云快照。若网易云未运行或无日志则返回 null。</summary>
    public NetEaseTrackSnapshot? PollSnapshot(bool audioSessionGeneratingSound, string? windowTitleFallback)
    {
        EnsurePaths();
        RefreshPlayingListIfNeeded();

        if (!string.IsNullOrEmpty(_elogPath) && File.Exists(_elogPath))
        {
            PollElogFile(_elogPath);
        }

        // 若日志里只有 songId 但缺歌名/封面，从 playingList 补齐
        if (!string.IsNullOrEmpty(_songId) &&
            (_track is null || string.IsNullOrWhiteSpace(_track.Title) || string.IsNullOrWhiteSpace(_track.CoverUrl)) &&
            _playingListMap.TryGetValue(_songId, out var fromList))
        {
            _track = fromList;
        }

        // 若窗口标题切到了新歌但 elog 尚未刷出，用窗口标题在 playingList 中匹配
        if (!string.IsNullOrWhiteSpace(windowTitleFallback))
        {
            var parts = windowTitleFallback.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
            var winTitle = parts.Length > 0 ? parts[0].Trim() : windowTitleFallback.Trim();
            if (!string.IsNullOrWhiteSpace(winTitle) &&
                (_track is null || !winTitle.Contains(_track.Title, StringComparison.OrdinalIgnoreCase)))
            {
                var matched = _playingListMap.Values.FirstOrDefault(m =>
                    !string.IsNullOrWhiteSpace(m.Title) &&
                    (string.Equals(m.Title, winTitle, StringComparison.OrdinalIgnoreCase) ||
                     winTitle.StartsWith(m.Title, StringComparison.OrdinalIgnoreCase)));
                if (matched is not null && !string.Equals(matched.SongId, _songId, StringComparison.OrdinalIgnoreCase))
                {
                    _songId = matched.SongId;
                    _track = matched;
                    _pausedPositionSec = 0;
                    _playingAnchorUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        if (_track is null && string.IsNullOrWhiteSpace(windowTitleFallback))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        // 播放态判定：elog 的 native播放state 与 CoreAudio 实际发声状态结合
        // 若 elog 最近 2.5 秒内刚切了状态，优先信 elog；否则若音频会话明确正在发声，自动修正为播放态
        bool isPlaying = _isPlaying;
        if ((now - _lastEventUtc).TotalSeconds > 2.0 && audioSessionGeneratingSound && !isPlaying)
        {
            isPlaying = true;
            _isPlaying = true;
            _playingAnchorUtc = now - TimeSpan.FromSeconds(_pausedPositionSec);
        }

        double posSec = isPlaying
            ? Math.Max(0, (now - _playingAnchorUtc).TotalSeconds)
            : Math.Max(0, _pausedPositionSec);

        TimeSpan duration = _track?.Duration ?? TimeSpan.Zero;
        if (duration > TimeSpan.Zero && posSec > duration.TotalSeconds)
        {
            posSec = duration.TotalSeconds;
        }

        string title = _track?.Title ?? "";
        string artist = _track?.Artist ?? "";
        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(windowTitleFallback))
        {
            var parts = windowTitleFallback.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
            title = parts[0].Trim();
            if (parts.Length >= 2) artist = parts[1].Trim();
        }

        if (string.IsNullOrWhiteSpace(title)) return null;

        byte[]? coverBytes = null;
        var coverUrl = _track?.CoverUrl;
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            if (!_coverCache.TryGetValue(coverUrl, out coverBytes))
            {
                EnsureCoverDownloadedAsync(coverUrl);
            }
        }

        return new NetEaseTrackSnapshot(
            SongId: _songId,
            Title: title,
            Artist: artist,
            Album: _track?.Album ?? "",
            IsPlaying: isPlaying,
            Position: TimeSpan.FromSeconds(posSec),
            Duration: duration,
            UpdatedAt: now,
            CoverUrl: coverUrl,
            CoverBytes: coverBytes);
    }

    /// <summary>用户点击播放/暂停后乐观翻转一次状态，同时等待 elog 下一帧确认。</summary>
    public void NotifyOptimisticTogglePlayPause()
    {
        var now = DateTimeOffset.UtcNow;
        if (_isPlaying)
        {
            _pausedPositionSec = Math.Max(0, (now - _playingAnchorUtc).TotalSeconds);
            _isPlaying = false;
        }
        else
        {
            _playingAnchorUtc = now - TimeSpan.FromSeconds(_pausedPositionSec);
            _isPlaying = true;
        }
        _lastEventUtc = now;
    }

    /// <summary>手动校准本地歌词进度。</summary>
    public void CalibratePosition(TimeSpan target)
    {
        var now = DateTimeOffset.UtcNow;
        double sec = Math.Max(0, target.TotalSeconds);
        _pausedPositionSec = sec;
        _playingAnchorUtc = now - TimeSpan.FromSeconds(sec);
        _lastEventUtc = now;
    }

    private void EnsurePaths()
    {
        if (!string.IsNullOrEmpty(_elogPath) && File.Exists(_elogPath)) return;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 1) 标准 Win32 版路径
        var directDir = Path.Combine(localAppData, "NetEase", "CloudMusic");
        var directElog = Path.Combine(directDir, "cloudmusic.elog");
        if (File.Exists(directElog))
        {
            _elogPath = directElog;
            _playingListPath = Path.Combine(directDir, "webdata", "file", "playingList");
            return;
        }

        // 2) Microsoft Store (MSIX) 版虚拟化路径
        var packagesDir = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesDir))
        {
            try
            {
                foreach (var pkg in Directory.EnumerateDirectories(packagesDir, "1F8B0F94*"))
                {
                    var msixDir = Path.Combine(pkg, "LocalCache", "Local", "NetEase", "CloudMusic");
                    var msixElog = Path.Combine(msixDir, "cloudmusic.elog");
                    if (File.Exists(msixElog))
                    {
                        _elogPath = msixElog;
                        _playingListPath = Path.Combine(msixDir, "webdata", "file", "playingList");
                        return;
                    }
                }
            }
            catch { }
        }
    }

    private void RefreshPlayingListIfNeeded()
    {
        if (string.IsNullOrEmpty(_playingListPath) || !File.Exists(_playingListPath)) return;

        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(_playingListPath);
            if (lastWrite == _playingListLastWrite && _playingListMap.Count > 0) return;
            _playingListLastWrite = lastWrite;

            using var fs = new FileStream(_playingListPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("list", out var listProp) || listProp.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            _playingListMap.Clear();
            foreach (var item in listProp.EnumerateArray())
            {
                var meta = ParseTrackMeta(item);
                if (meta is not null && !string.IsNullOrEmpty(meta.SongId))
                {
                    _playingListMap[meta.SongId] = meta;
                }
            }
        }
        catch { }
    }

    private void PollElogFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long size = fs.Length;

            if (!_bootstrapped || size < _offset)
            {
                long start = Math.Max(0, size - MaxInitialTailBytes);
                int len = (int)(size - start);
                var buf = new byte[len];
                fs.Seek(start, SeekOrigin.Begin);
                int read = fs.Read(buf, 0, len);
                if (read > 0)
                {
                    if (read < len) Array.Resize(ref buf, read);
                    if (start > 0)
                    {
                        int firstNl = Array.IndexOf(buf, EncodedNewline);
                        if (firstNl >= 0 && firstNl + 1 < buf.Length)
                        {
                            buf = buf[(firstNl + 1)..];
                        }
                    }
                    IngestBytes(buf);
                }
                _offset = size;
                _bootstrapped = true;
                return;
            }

            if (size == _offset) return;

            int delta = (int)Math.Min(MaxInitialTailBytes, size - _offset);
            var deltaBuf = new byte[delta];
            fs.Seek(_offset, SeekOrigin.Begin);
            int got = fs.Read(deltaBuf, 0, delta);
            _offset += got;
            if (got > 0)
            {
                if (got < delta) Array.Resize(ref deltaBuf, got);
                IngestBytes(deltaBuf);
            }
        }
        catch { }
    }

    private void IngestBytes(byte[] chunk)
    {
        byte[] joined;
        if (_carry.Length > 0)
        {
            joined = new byte[_carry.Length + chunk.Length];
            Buffer.BlockCopy(_carry, 0, joined, 0, _carry.Length);
            Buffer.BlockCopy(chunk, 0, joined, _carry.Length, chunk.Length);
        }
        else
        {
            joined = chunk;
        }

        int lastNl = Array.LastIndexOf(joined, EncodedNewline);
        if (lastNl < 0)
        {
            _carry = joined.Length > MaxCarryBytes ? joined[^MaxCarryBytes..] : joined;
            return;
        }

        int rem = joined.Length - (lastNl + 1);
        if (rem > 0)
        {
            int copyLen = Math.Min(rem, MaxCarryBytes);
            _carry = new byte[copyLen];
            Buffer.BlockCopy(joined, joined.Length - copyLen, _carry, 0, copyLen);
        }
        else
        {
            _carry = [];
        }

        int decodeLen = lastNl + 1;
        var decoded = new byte[decodeLen];
        for (int i = 0; i < decodeLen; i++)
        {
            decoded[i] = DecodeTable[joined[i]];
        }

        var text = Encoding.UTF8.GetString(decoded);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            HandleDecodedLine(line);
        }
    }

    private void HandleDecodedLine(string line)
    {
        if (!line.Contains("【playing】", StringComparison.Ordinal) &&
            !line.Contains("setPlayingPosition", StringComparison.Ordinal))
        {
            return;
        }

        var eventUtc = ParseEventTimeUtc(line);

        if (line.Contains("\"checkPlayPrivilege\"", StringComparison.Ordinal) ||
            line.Contains("\"playOneTrackInPlayingList\"", StringComparison.Ordinal))
        {
            var meta = ExtractTrackFromLine(line);
            if (meta is not null && !string.IsNullOrEmpty(meta.SongId))
            {
                bool isNewSong = !string.Equals(meta.SongId, _songId, StringComparison.OrdinalIgnoreCase);
                _songId = meta.SongId;
                _track = meta;
                if (!string.IsNullOrWhiteSpace(meta.CoverUrl))
                {
                    EnsureCoverDownloadedAsync(meta.CoverUrl);
                }

                if (isNewSong)
                {
                    bool startsPlaying = line.Contains("\"playOneTrackInPlayingList\"", StringComparison.Ordinal);
                    _isPlaying = startsPlaying;
                    _pausedPositionSec = 0;
                    _playingAnchorUtc = eventUtc;
                    _lastEventUtc = eventUtc;
                }
            }
            return;
        }

        if (line.Contains("\"native", StringComparison.Ordinal) &&
            line.Contains("load", StringComparison.Ordinal) &&
            line.Contains("\"songId\"", StringComparison.Ordinal))
        {
            var json = ExtractJsonElement(line);
            if (json.HasValue && json.Value.TryGetProperty("songId", out var sidProp))
            {
                var sid = sidProp.ValueKind == JsonValueKind.String ? sidProp.GetString() : sidProp.ToString();
                if (!string.IsNullOrWhiteSpace(sid))
                {
                    if (!string.Equals(sid, _songId, StringComparison.OrdinalIgnoreCase))
                    {
                        _songId = sid;
                        _track = _playingListMap.TryGetValue(sid, out var m) ? m : null;
                    }
                    _isPlaying = true;
                    _pausedPositionSec = 0;
                    _playingAnchorUtc = eventUtc;
                    _lastEventUtc = eventUtc;
                }
            }
            return;
        }

        if (line.Contains("\"setPlayingPosition\"", StringComparison.Ordinal))
        {
            var m = SetPositionRegex().Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seekSec))
            {
                seekSec = Math.Max(0, seekSec);
                _pausedPositionSec = seekSec;
                _playingAnchorUtc = eventUtc - TimeSpan.FromSeconds(seekSec);
                _lastEventUtc = eventUtc;
            }
            return;
        }

        if (line.Contains("\"native", StringComparison.Ordinal) &&
            line.Contains("state\"", StringComparison.Ordinal))
        {
            var m = NativeStateRegex().Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int stateCode))
            {
                double currentSec = _isPlaying
                    ? Math.Max(0, (eventUtc - _playingAnchorUtc).TotalSeconds)
                    : Math.Max(0, _pausedPositionSec);

                bool nowPlaying = stateCode == 1;
                _isPlaying = nowPlaying;
                _pausedPositionSec = currentSec;
                _playingAnchorUtc = eventUtc - TimeSpan.FromSeconds(currentSec);
                _lastEventUtc = eventUtc;
            }
        }
    }

    private static DateTimeOffset ParseEventTimeUtc(string line)
    {
        var m = HeaderRegex().Match(line);
        if (m.Success && m.Groups[8].Success &&
            DateTimeOffset.TryParse(m.Groups[8].Value, out var parsedLocal))
        {
            return parsedLocal.ToUniversalTime();
        }
        return DateTimeOffset.UtcNow;
    }

    private static TrackMeta? ExtractTrackFromLine(string line)
    {
        var el = ExtractJsonElement(line);
        return el.HasValue ? ParseTrackMeta(el.Value) : null;
    }

    private static JsonElement? ExtractJsonElement(string line)
    {
        int start = line.IndexOf('{');
        int end = line.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        var slice = line[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(slice);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static TrackMeta? ParseTrackMeta(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var t = root.TryGetProperty("track", out var trackObj) && trackObj.ValueKind == JsonValueKind.Object
            ? trackObj
            : root;

        string songId = "";
        if (t.TryGetProperty("id", out var idProp))
        {
            songId = idProp.ValueKind == JsonValueKind.String ? (idProp.GetString() ?? "") : idProp.ToString();
        }
        if (string.IsNullOrWhiteSpace(songId)) return null;

        string title = t.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? (nameProp.GetString() ?? "")
            : "";

        string artist = "";
        if (t.TryGetProperty("artists", out var artistsProp) && artistsProp.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in artistsProp.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object &&
                    a.TryGetProperty("name", out var an) &&
                    an.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(an.GetString()))
                {
                    names.Add(an.GetString()!.Trim());
                }
            }
            artist = string.Join(" / ", names);
        }

        string album = "";
        string? coverUrl = null;
        if (t.TryGetProperty("album", out var albumProp) && albumProp.ValueKind == JsonValueKind.Object)
        {
            if (albumProp.TryGetProperty("name", out var alName) && alName.ValueKind == JsonValueKind.String)
            {
                album = alName.GetString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(album) &&
                albumProp.TryGetProperty("albumName", out var alName2) &&
                alName2.ValueKind == JsonValueKind.String)
            {
                album = alName2.GetString() ?? "";
            }
            if (albumProp.TryGetProperty("picUrl", out var picProp) && picProp.ValueKind == JsonValueKind.String)
            {
                coverUrl = picProp.GetString();
            }
            if (string.IsNullOrWhiteSpace(coverUrl) &&
                albumProp.TryGetProperty("cover", out var covProp) &&
                covProp.ValueKind == JsonValueKind.String)
            {
                coverUrl = covProp.GetString();
            }
        }

        double durMs = 0;
        if (t.TryGetProperty("duration", out var durProp))
        {
            if (durProp.ValueKind == JsonValueKind.Number && durProp.TryGetDouble(out var d))
            {
                durMs = d;
            }
            else if (durProp.ValueKind == JsonValueKind.String && double.TryParse(durProp.GetString(), out var ds))
            {
                durMs = ds;
            }
        }

        return new TrackMeta(
            SongId: songId,
            Title: title,
            Artist: artist,
            Album: album,
            Duration: durMs > 0 ? TimeSpan.FromMilliseconds(durMs) : TimeSpan.Zero,
            CoverUrl: coverUrl);
    }

    private void EnsureCoverDownloadedAsync(string coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl) || _coverCache.ContainsKey(coverUrl)) return;
        if (!_coverInFlight.TryAdd(coverUrl, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var url = coverUrl.Contains('?') ? coverUrl : $"{coverUrl}?param=240y240";
                var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes.Length > 0 && bytes.Length < 4 * 1024 * 1024)
                {
                    _coverCache[coverUrl] = bytes;
                }
            }
            catch (Exception ex)
            {
                _log($"网易云封面下载失败：{ex.Message}");
            }
            finally
            {
                _coverInFlight.TryRemove(coverUrl, out _);
            }
        });
    }

    public void Dispose() => _http.Dispose();
}
