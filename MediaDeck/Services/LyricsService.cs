using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDeck.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed record LyricTriplet(string Previous, string Current, string Next, bool HasSyncedLyrics);

/// <summary>
/// 同步歌词获取与匹配服务：支持 LRCLIB 与网易云公开接口双源兜底，带内存缓存。
/// </summary>
public sealed partial class LyricsService : IDisposable
{
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, List<LyricLine>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TimeSpan> _durationCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LrcTimestampRegex();

    public LyricsService(Action<string> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDeck-WinIsland/1.0");
    }

    public static string BuildCacheKey(string title, string artist)
        => $"{artist.Trim()}||{title.Trim()}";

    public bool TryGetCachedDuration(string title, string artist, out TimeSpan duration)
        => _durationCache.TryGetValue(BuildCacheKey(title, artist), out duration);

    public bool TryGetCachedLines(string title, string artist, out List<LyricLine>? lines)
        => _cache.TryGetValue(BuildCacheKey(title, artist), out lines);

    /// <summary>后台拉取歌词并在完成后回调 onLoaded。</summary>
    public void EnsureLyricsAsync(string title, string artist, TimeSpan duration, Action onLoaded)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var key = BuildCacheKey(title, artist);
        if (_cache.ContainsKey(key)) return;
        if (!_inFlight.TryAdd(key, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var lrc = await FetchFromNetEaseAsync(title, artist).ConfigureAwait(false)
                          ?? await FetchFromLrclibAsync(title, artist, duration).ConfigureAwait(false);

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

    public LyricTriplet GetTriplet(string title, string artist, TimeSpan position)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new LyricTriplet("", "暂无媒体播放", "", false);
        }

        var key = BuildCacheKey(title, artist);
        if (!_cache.TryGetValue(key, out var lines))
        {
            return new LyricTriplet("", "正在匹配歌词…", "", false);
        }

        if (lines.Count == 0)
        {
            return new LyricTriplet("", "暂无同步歌词", "", false);
        }

        // 查找最后一个 <= position 的歌词行
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
            double bestDiff = double.MaxValue;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("syncedLyrics", out var syncedProp) ||
                    syncedProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var synced = syncedProp.GetString();
                if (string.IsNullOrWhiteSpace(synced)) continue;

                double diff = 0;
                if (duration > TimeSpan.Zero && item.TryGetProperty("duration", out var durProp) && durProp.TryGetDouble(out var d))
                {
                    diff = Math.Abs(d - duration.TotalSeconds);
                }

                if (bestSynced is null || diff < bestDiff)
                {
                    bestSynced = synced;
                    bestDiff = diff;
                }
            }

            return bestSynced;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> FetchFromNetEaseAsync(string title, string artist)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&limit=3";
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

            long songId = songs[0].GetProperty("id").GetInt64();
            if (songs[0].TryGetProperty("duration", out var durMsProp) && durMsProp.TryGetInt64(out var durMs) && durMs > 0)
            {
                _durationCache[BuildCacheKey(title, artist)] = TimeSpan.FromMilliseconds(durMs);
            }
            var lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
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
