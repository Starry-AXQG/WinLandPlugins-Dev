using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MediaDeck.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed record LyricTriplet(string Previous, string Current, string Next, bool HasSyncedLyrics);

/// <summary>
/// 同步歌词获取与匹配服务：
///   1. PotPlayer 本地歌词优先：读取 PotPlayer 播放列表 (.dpl) 定位当前音频文件路径，优先加载同目录 .lrc（支持 UTF-8/UTF-16/GBK）及 FLAC 内嵌 LYRICS 标签；
///   2. 网易云精准 songId 直查；
///   3. 在线双源（网易云 + LRCLIB）兜底：自动清洗文件名后缀与编号，综合歌名、歌手与时长校验择优，杜绝盲取第 1 条导致张冠李戴。
/// </summary>
public sealed partial class LyricsService : IDisposable
{
    private static readonly string[] MediaExtensions =
        [".flac", ".mp3", ".wav", ".m4a", ".ogg", ".ape", ".aac", ".wma", ".opus", ".dsf", ".dff", ".alac", ".mkv", ".mp4"];

    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, List<LyricLine>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TimeSpan> _durationCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"^\s*\d{1,3}\s*[.\-_、]\s*")]
    private static partial Regex LeadingTrackNumberRegex();

    static LyricsService()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public LyricsService(Action<string> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDeck-WinIsland/1.0");
    }

    public static string BuildCacheKey(string title, string artist, string? localFilePath = null)
        => string.IsNullOrWhiteSpace(localFilePath)
            ? $"{artist.Trim()}||{title.Trim()}"
            : $"file||{localFilePath.Trim()}";

    public bool TryGetCachedDuration(string title, string artist, out TimeSpan duration)
        => _durationCache.TryGetValue(BuildCacheKey(title, artist), out duration);

    public bool TryGetCachedLines(string title, string artist, out List<LyricLine>? lines)
        => _cache.TryGetValue(BuildCacheKey(title, artist), out lines);

    /// <summary>后台拉取歌词并在完成后回调 onLoaded（支持精确本地文件路径、PotPlayer 本地歌词、网易云精准 songId 及校验式在线匹配）。</summary>
    public void EnsureLyricsAsync(
        string title,
        string artist,
        TimeSpan duration,
        Action onLoaded,
        string? neteaseSongId = null,
        string? sourceAppId = null,
        string? localFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(localFilePath)) return;
        var key = BuildCacheKey(title, artist, localFilePath);
        if (_cache.ContainsKey(key)) return;
        if (!_inFlight.TryAdd(key, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                string? lrc = null;

                // 1) 若已有精确本地音频文件路径（如 PotPlayer WM_USER 0x6020 直读），直接读取该文件的同目录 .lrc / FLAC 内嵌歌词
                if (!string.IsNullOrWhiteSpace(localFilePath) && File.Exists(localFilePath))
                {
                    lrc = TryReadLocalLyricsForFile(localFilePath, title);
                    if (!string.IsNullOrWhiteSpace(lrc))
                    {
                        _log($"已直读本地歌词：{Path.GetFileName(localFilePath)}");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(sourceAppId) &&
                         sourceAppId.Contains("PotPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    lrc = TryReadPotPlayerLocalLyrics(title, artist);
                    if (!string.IsNullOrWhiteSpace(lrc))
                    {
                        _log($"已加载 PotPlayer 本地歌词：{title}");
                    }
                }

                // 2) 网易云精准 songId
                if (string.IsNullOrWhiteSpace(lrc) && !string.IsNullOrWhiteSpace(neteaseSongId))
                {
                    lrc = await FetchFromNetEaseByIdAsync(neteaseSongId).ConfigureAwait(false);
                }

                // 3) 清洗歌名/歌手后进行带时长与标题校验的在线匹配
                if (string.IsNullOrWhiteSpace(lrc))
                {
                    var (cleanTitle, cleanArtist) = NormalizeTitleAndArtist(title, artist);
                    lrc = await FetchFromNetEaseAsync(cleanTitle, cleanArtist, duration).ConfigureAwait(false)
                          ?? await FetchFromLrclibAsync(cleanTitle, cleanArtist, duration).ConfigureAwait(false);

                    // 若歌名形如 "曲名 - 歌手" 而此前按 "歌手 - 曲名" 拆分未命中，尝试反转再匹配一次
                    if (string.IsNullOrWhiteSpace(lrc) &&
                        string.IsNullOrWhiteSpace(artist) &&
                        !string.IsNullOrWhiteSpace(cleanArtist))
                    {
                        lrc = await FetchFromNetEaseAsync(cleanArtist, cleanTitle, duration).ConfigureAwait(false)
                              ?? await FetchFromLrclibAsync(cleanArtist, cleanTitle, duration).ConfigureAwait(false);
                    }
                }

                var parsed = string.IsNullOrWhiteSpace(lrc) ? [] : ParseLrc(lrc);
                _cache[key] = parsed;
                onLoaded();
            }
            catch (Exception ex)
            {
                _log($"歌词拉取失败（{title}）：{ex.Message}");
                _cache[key] = [];
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        });
    }

    // ==================== PotPlayer 本地歌词读取（多开安全：严格匹配当前实例文件名 -> 同目录 .lrc -> FLAC 内嵌 LYRICS） ====================

    private string? TryReadPotPlayerLocalLyrics(string smtcTitle, string smtcArtist)
    {
        try
        {
            string? audioPath = FindPotPlayerMatchingFilePath(smtcTitle, smtcArtist);
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) return null;
            return TryReadLocalLyricsForFile(audioPath, smtcTitle);
        }
        catch { }
        return null;
    }

    private static string? TryReadLocalLyricsForFile(string audioPath, string smtcTitle)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) return null;

            string? dir = Path.GetDirectoryName(audioPath);
            string stem = Path.GetFileNameWithoutExtension(audioPath);

            // 1) 优先同目录同名 .lrc
            var candidates = new List<string>
            {
                Path.ChangeExtension(audioPath, ".lrc"),
            };
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string cleanStem = StripMediaExtension(smtcTitle).Trim();
                if (!string.IsNullOrWhiteSpace(cleanStem) && !string.Equals(cleanStem, stem, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(dir, cleanStem + ".lrc"));
                }
            }

            foreach (var lrcPath in candidates)
            {
                if (!File.Exists(lrcPath)) continue;
                var text = ReadTextSmartEncoding(lrcPath);
                if (!string.IsNullOrWhiteSpace(text) && LrcTimestampRegex().IsMatch(text))
                {
                    return text;
                }
            }

            // 2) 若无外部 .lrc，且是 FLAC 文件，尝试读取内嵌 Vorbis Comment LYRICS 标签
            if (audioPath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            {
                var embedded = TryReadFlacEmbeddedLyrics(audioPath);
                if (!string.IsNullOrWhiteSpace(embedded) && LrcTimestampRegex().IsMatch(embedded))
                {
                    return embedded;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 多开安全的 PotPlayer 本地文件路径定位：
    ///   依次从全部 .dpl 播放列表、注册表 RememberFiles 以及最近播放目录中检索，
    ///   **严格校验文件名与当前 SMTC 标题匹配**，绝不盲目回退到另一个 PotPlayer 窗口的 playname。
    /// </summary>
    private static string? FindPotPlayerMatchingFilePath(string smtcTitle, string smtcArtist)
    {
        string cleanTitle = StripMediaExtension(smtcTitle).Trim();
        if (cleanTitle.Length < 2) return null;

        string cleanArtist = smtcArtist.Trim();
        var candidatePaths = new List<string>();
        var knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 来源 A：全部 PotPlayer 播放列表目录中的所有 .dpl 文件
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] playlistDirs =
        [
            Path.Combine(appData, "PotPlayerMini64", "Playlist"),
            Path.Combine(appData, "PotPlayerMini", "Playlist"),
            Path.Combine(appData, "PotPlayer64", "Playlist"),
            Path.Combine(appData, "PotPlayer", "Playlist"),
        ];

        foreach (var pDir in playlistDirs)
        {
            if (!Directory.Exists(pDir)) continue;
            try
            {
                foreach (var dpl in Directory.EnumerateFiles(pDir, "*.dpl"))
                {
                    var content = ReadTextSmartEncoding(dpl);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var line = rawLine.Trim();
                        string? path = null;
                        if (line.StartsWith("playname=", StringComparison.OrdinalIgnoreCase))
                        {
                            path = line["playname=".Length..].Trim();
                        }
                        else
                        {
                            int marker = line.IndexOf("*file*", StringComparison.OrdinalIgnoreCase);
                            if (marker > 0)
                            {
                                path = line[(marker + 6)..].Trim();
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            candidatePaths.Add(path);
                            var d = Path.GetDirectoryName(path);
                            if (!string.IsNullOrWhiteSpace(d)) knownDirs.Add(d);
                        }
                    }
                }
            }
            catch { }
        }

        // 来源 B：PotPlayer 注册表 RememberFiles（多开窗口打开的各自文件均会记录于此）
        string[] regPaths =
        [
            @"Software\DAUM\PotPlayerMini64\RememberFiles",
            @"Software\DAUM\PotPlayerMini\RememberFiles",
            @"Software\DAUM\PotPlayer64\RememberFiles",
            @"Software\DAUM\PotPlayer\RememberFiles",
        ];
        foreach (var rp in regPaths)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(rp);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not string val || string.IsNullOrWhiteSpace(val)) continue;
                    int eq = val.IndexOf('=');
                    string path = (eq >= 0 ? val[(eq + 1)..] : val).Trim();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        candidatePaths.Add(path);
                    }
                }
            }
            catch { }
        }

        // 在候选路径中严格匹配文件名与当前 SMTC 标题，优先精确匹配和带歌手匹配
        string? bestMatch = PickBestMatchingLocalPath(candidatePaths, cleanTitle, cleanArtist);
        if (bestMatch is not null) return bestMatch;

        // 来源 C：在最近播放的同目录中查找与当前 SMTC 标题匹配的本地音频文件（覆盖多开同文件夹不同曲目）
        foreach (var dir in knownDirs.Take(4))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var dirFiles = Directory.EnumerateFiles(dir)
                    .Where(f => MediaExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                bestMatch = PickBestMatchingLocalPath(dirFiles, cleanTitle, cleanArtist);
                if (bestMatch is not null) return bestMatch;
            }
            catch { }
        }

        // 严禁回退到不匹配的 playname（多开时会导致 B 窗口误读 A 窗口的本地歌词）
        return null;
    }

    private static string? PickBestMatchingLocalPath(IEnumerable<string> paths, string cleanTitle, string cleanArtist)
    {
        string? best = null;
        int bestScore = 0;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            string stem = Path.GetFileNameWithoutExtension(path).Trim();
            if (string.IsNullOrWhiteSpace(stem)) continue;

            int score = 0;
            if (string.Equals(stem, cleanTitle, StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
            }
            else if (stem.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase))
            {
                score = 70;
            }
            else if (stem.Length >= 2 && cleanTitle.Contains(stem, StringComparison.OrdinalIgnoreCase))
            {
                score = 55;
            }

            if (score <= 0) continue;

            if (!string.IsNullOrWhiteSpace(cleanArtist) &&
                stem.Contains(cleanArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = path;
            }
        }

        return best;
    }

    private static string ReadTextSmartEncoding(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) return "";

        // BOM 检测
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 严格 UTF-8 验证，失败则回退到 GB18030（兼容中文 Windows 常见 ANSI/GBK .lrc）
        try
        {
            var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8Strict.GetString(bytes);
        }
        catch
        {
            try { return Encoding.GetEncoding("GB18030").GetString(bytes); }
            catch { return Encoding.UTF8.GetString(bytes); }
        }
    }

    /// <summary>轻量读取 FLAC 元数据块中的 Vorbis Comment LYRICS / UNSYNCEDLYRICS 标签（无外部依赖）。</summary>
    private static string? TryReadFlacEmbeddedLyrics(string flacPath)
    {
        try
        {
            using var fs = new FileStream(flacPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) < 4 || header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C')
                return null;

            bool isLast = false;
            while (!isLast && fs.Position < fs.Length)
            {
                if (fs.Read(header) < 4) break;
                isLast = (header[0] & 0x80) != 0;
                int blockType = header[0] & 0x7F;
                int blockLen = (header[1] << 16) | (header[2] << 8) | header[3];
                if (blockLen <= 0 || fs.Position + blockLen > fs.Length) break;

                if (blockType == 4 && blockLen <= 2 * 1024 * 1024) // VORBIS_COMMENT
                {
                    byte[] buf = new byte[blockLen];
                    if (fs.Read(buf, 0, blockLen) < blockLen) break;

                    int pos = 0;
                    if (pos + 4 > buf.Length) break;
                    int vendorLen = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4));
                    pos += 4 + vendorLen;
                    if (pos + 4 > buf.Length) break;
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
                        string tag = entry[..eq].Trim();
                        if (tag.Equals("LYRICS", StringComparison.OrdinalIgnoreCase) ||
                            tag.Equals("UNSYNCEDLYRICS", StringComparison.OrdinalIgnoreCase))
                        {
                            return entry[(eq + 1)..];
                        }
                    }
                    break;
                }

                fs.Seek(blockLen, SeekOrigin.Current);
            }
        }
        catch { }
        return null;
    }

    // ==================== 歌名/歌手清洗 ====================

    private static string StripMediaExtension(string raw)
    {
        var s = raw.Trim();
        foreach (var ext in MediaExtensions)
        {
            if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^ext.Length].Trim();
                break;
            }
        }
        return s;
    }

    private static (string Title, string Artist) NormalizeTitleAndArtist(string rawTitle, string rawArtist)
    {
        string title = StripMediaExtension(rawTitle);
        title = LeadingTrackNumberRegex().Replace(title, "").Trim();
        string artist = rawArtist.Trim();

        if (string.IsNullOrWhiteSpace(artist) && title.Contains(" - ", StringComparison.Ordinal))
        {
            int idx = title.IndexOf(" - ", StringComparison.Ordinal);
            string left = title[..idx].Trim();
            string right = title[(idx + 3)..].Trim();
            if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
            {
                // 中文音乐文件常见 "歌名 - 歌手" 或 "歌手 - 歌名"，先按 "歌名 - 歌手"（如 PotPlayer 的 "封神朝歌赋 - 泽国同学"），若未命中会在外层反转重试
                title = left;
                artist = right;
            }
        }

        return (title, artist);
    }

    private async Task<string?> FetchFromNetEaseByIdAsync(string songId)
    {
        try
        {
            var lyricUrl = $"https://music.163.com/api/song/lyric?id={Uri.EscapeDataString(songId)}&lv=1&kv=1&tv=-1";
            using var lrcReq = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
            lrcReq.Headers.Referrer = new Uri("https://music.163.com/");

            using var lrcResp = await _http.SendAsync(lrcReq).ConfigureAwait(false);
            if (!lrcResp.IsSuccessStatusCode) return null;

            var lrcJson = await lrcResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var lrcDoc = JsonDocument.Parse(lrcJson);
            if (lrcDoc.RootElement.TryGetProperty("lrc", out var lrcObj) &&
                lrcObj.TryGetProperty("lyric", out var lyricProp) &&
                lyricProp.ValueKind == JsonValueKind.String)
            {
                var lrcText = lyricProp.GetString();
                if (!string.IsNullOrWhiteSpace(lrcText) && LrcTimestampRegex().IsMatch(lrcText))
                {
                    return lrcText;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public LyricTriplet GetTriplet(string title, string artist, TimeSpan position, string? localFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(localFilePath))
        {
            return new LyricTriplet("", "暂无媒体播放", "", false);
        }

        var key = BuildCacheKey(title, artist, localFilePath);
        if (!_cache.TryGetValue(key, out var lines))
        {
            return new LyricTriplet("", "正在匹配歌词…", "", false);
        }

        if (lines.Count == 0)
        {
            return new LyricTriplet("", "暂无同步歌词", "", false);
        }

        int idx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time <= position + TimeSpan.FromMilliseconds(250))
            {
                idx = i;
            }
            else
            {
                break;
            }
        }

        if (idx < 0)
        {
            return new LyricTriplet("", lines[0].Text, lines.Count > 1 ? lines[1].Text : "", true);
        }

        string prev = idx > 0 ? lines[idx - 1].Text : "";
        string cur = lines[idx].Text;
        string next = idx + 1 < lines.Count ? lines[idx + 1].Text : "";
        return new LyricTriplet(prev, cur, next, true);
    }

    private async Task<string?> FetchFromLrclibAsync(string title, string artist, TimeSpan duration)
    {
        try
        {
            var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}"
                    + (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            string? bestSynced = null;
            double bestScore = double.MinValue;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("syncedLyrics", out var syncedProp) ||
                    syncedProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var synced = syncedProp.GetString();
                if (string.IsNullOrWhiteSpace(synced)) continue;

                string candTitle = item.TryGetProperty("trackName", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() ?? "" : "";
                string candArtist = item.TryGetProperty("artistName", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() ?? "" : "";
                double candDur = item.TryGetProperty("duration", out var durProp) && durProp.TryGetDouble(out var d) ? d : 0;

                double score = ScoreCandidate(title, artist, duration, candTitle, candArtist, candDur);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSynced = synced;
                }
            }

            return bestScore >= 25.0 ? bestSynced : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> FetchFromNetEaseAsync(string title, string artist, TimeSpan duration)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&limit=5";
            using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            req.Headers.Referrer = new Uri("https://music.163.com/");

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var res) ||
                !res.TryGetProperty("songs", out var songs) ||
                songs.ValueKind != JsonValueKind.Array ||
                songs.GetArrayLength() == 0)
            {
                return null;
            }

            long bestSongId = -1;
            long bestDurMs = 0;
            double bestScore = double.MinValue;

            foreach (var song in songs.EnumerateArray())
            {
                if (!song.TryGetProperty("id", out var idProp) || !idProp.TryGetInt64(out var sid)) continue;
                string candTitle = song.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String ? np.GetString() ?? "" : "";
                string candArtist = "";
                if (song.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array && arts.GetArrayLength() > 0)
                {
                    var names = new List<string>();
                    foreach (var a in arts.EnumerateArray())
                    {
                        if (a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                        {
                            var s = an.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) names.Add(s);
                        }
                    }
                    candArtist = string.Join(" / ", names);
                }

                long durMs = song.TryGetProperty("duration", out var dm) && dm.TryGetInt64(out var dVal) ? dVal : 0;
                double score = ScoreCandidate(title, artist, duration, candTitle, candArtist, durMs / 1000.0);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSongId = sid;
                    bestDurMs = durMs;
                }
            }

            // 严控门槛：标题或时长明显不符时宁可不显示，绝不张冠李戴
            if (bestSongId <= 0 || bestScore < 25.0) return null;

            if (bestDurMs > 0)
            {
                _durationCache[BuildCacheKey(title, artist)] = TimeSpan.FromMilliseconds(bestDurMs);
            }

            return await FetchFromNetEaseByIdAsync(bestSongId.ToString()).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static double ScoreCandidate(
        string wantTitle,
        string wantArtist,
        TimeSpan wantDuration,
        string candTitle,
        string candArtist,
        double candDurationSec)
    {
        double score = 0;
        string wt = wantTitle.Trim();
        string ct = candTitle.Trim();

        if (string.Equals(wt, ct, StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (ct.Contains(wt, StringComparison.OrdinalIgnoreCase) || wt.Contains(ct, StringComparison.OrdinalIgnoreCase))
            score += 35;
        else
            return -100; // 歌名完全不沾边直接淘汰

        if (!string.IsNullOrWhiteSpace(wantArtist) && !string.IsNullOrWhiteSpace(candArtist))
        {
            string wa = wantArtist.Trim();
            string ca = candArtist.Trim();
            if (string.Equals(wa, ca, StringComparison.OrdinalIgnoreCase))
                score += 35;
            else if (ca.Contains(wa, StringComparison.OrdinalIgnoreCase) || wa.Contains(ca, StringComparison.OrdinalIgnoreCase))
                score += 22;
            else
                score -= 18;
        }

        if (wantDuration > TimeSpan.Zero && candDurationSec > 0)
        {
            double diff = Math.Abs(candDurationSec - wantDuration.TotalSeconds);
            if (diff <= 3.0) score += 30;
            else if (diff <= 8.0) score += 15;
            else if (diff > 20.0) score -= 45; // 时长差超过 20 秒基本不是同一首或同版本
        }

        return score;
    }

    public static List<LyricLine> ParseLrc(string lrcContent)
    {
        var list = new List<LyricLine>();
        var lines = lrcContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var matches = LrcTimestampRegex().Matches(raw);
            if (matches.Count == 0) continue;

            var text = LrcTimestampRegex().Replace(raw, "").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    var frac = m.Groups[3].Value;
                    ms = frac.Length switch
                    {
                        1 => int.Parse(frac) * 100,
                        2 => int.Parse(frac) * 10,
                        _ => int.Parse(frac[..3]),
                    };
                }
                list.Add(new LyricLine(new TimeSpan(0, 0, min, sec, ms), text));
            }
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    public void Dispose() => _http.Dispose();
}
