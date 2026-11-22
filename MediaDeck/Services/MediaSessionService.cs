using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MediaDeck.Services;

public sealed record MediaInstanceInfo
{
    public required string Id { get; init; }
    public required string SourceAppId { get; init; }
    public required string SourceDisplayName { get; init; }
    public int ProcessId { get; init; }
    public bool IsFallback { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string? NetEaseSongId { get; init; }
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset TimelineUpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public byte[]? ThumbnailBytes { get; init; }
    public byte[]? AppIconBytes { get; init; }
    internal GlobalSystemMediaTransportControlsSession? SmtcSession { get; init; }

    public TimeSpan EstimatedPosition
    {
        get
        {
            if (!IsPlaying) return Position;
            var elapsed = DateTimeOffset.UtcNow - TimelineUpdatedAt;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            var pos = Position + elapsed;
            return Duration > TimeSpan.Zero && pos > Duration ? Duration : pos;
        }
    }
}

/// <summary>
/// 重构后的多实例媒体会话服务：
///   1. 与系统媒体管理器同源：通过 Shell AppsFolder 解析 Zen/Firefox 哈希 AUMID、Spotify 等应用名与真图标；
///   2. 稳定多实例排序：
///      · 新实例加入时插入首位（原有实例依次后移）；
///      · 某实例从暂停变为播放时，与第 1 位交换位置；
///      · 选中态按稳定实例 ID 跟踪，点击播放绝不跳卡或乱序；
///   3. 网易云等非 SMTC 播放器兜底：内置本地播放计时器驱动歌词滚动 + 定向 WM_APPCOMMAND 控制，不误触 QQ 音乐。
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private static readonly string[] FallbackPlayerProcesses =
    [
        "cloudmusic",
        "QQMusic",
        "KuGou",
        "kwmusic",
        "foobar2000",
    ];

    private sealed class FallbackClock
    {
        public string TrackKey { get; set; } = "";
        public TimeSpan Accumulated { get; set; } = TimeSpan.Zero;
        public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
        public bool WasPlaying { get; set; }
    }

    private readonly AudioDeviceService _audioService;
    private readonly LyricsService _lyricsService;
    private readonly NetEaseElogService _netEaseElog;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ResolvedAppInfo?> _appsFolderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]?> _exeIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FallbackClock> _fallbackClocks = new(StringComparer.OrdinalIgnoreCase);

    // 稳定的有序实例表与播放状态记忆
    private readonly List<string> _orderedKeys = [];
    private readonly Dictionary<string, bool> _wasPlayingMap = new(StringComparer.OrdinalIgnoreCase);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private List<MediaInstanceInfo> _instances = [];
    private string? _selectedKey;
    private string _lastLoggedSummary = "";
    private bool _disposed;

    public bool EnableFallbackPlayers { get; set; } = true;

    public event Action? SessionsUpdated;

    public IReadOnlyList<MediaInstanceInfo> Instances => _instances;

    public bool HasAnyPlaying => _instances.Any(x => x.IsPlaying);

    public int SelectedIndex
    {
        get
        {
            var list = _instances;
            if (list.Count == 0) return -1;
            if (!string.IsNullOrEmpty(_selectedKey))
            {
                int idx = list.FindIndex(x => string.Equals(x.Id, _selectedKey, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) return idx;
            }
            return 0;
        }
    }

    public MediaInstanceInfo? CurrentInstance
    {
        get
        {
            var list = _instances;
            if (list.Count == 0) return null;
            int idx = SelectedIndex;
            return idx >= 0 && idx < list.Count ? list[idx] : list[0];
        }
    }

    public MediaSessionService(AudioDeviceService audioService, LyricsService lyricsService, Action<string> log)
    {
        _audioService = audioService;
        _lyricsService = lyricsService;
        _netEaseElog = new NetEaseElogService(log);
        _log = log;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ = RefreshAsync();
            _manager.CurrentSessionChanged += (_, _) => _ = RefreshAsync();
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"初始化 SMTC 会话管理器失败：{ex.Message}");
        }
    }

    public void SwitchInstance(int delta)
    {
        var list = _instances;
        if (list.Count <= 1) return;
        int cur = SelectedIndex;
        int next = (cur + delta % list.Count + list.Count) % list.Count;
        _selectedKey = list[next].Id;
        SessionsUpdated?.Invoke();
    }

    public void SelectInstance(int index)
    {
        var list = _instances;
        if (list.Count == 0) return;
        int clamped = Math.Clamp(index, 0, list.Count - 1);
        _selectedKey = list[clamped].Id;
        SessionsUpdated?.Invoke();
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;

        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            var snapshotMap = new Dictionary<string, MediaInstanceInfo>(StringComparer.OrdinalIgnoreCase);
            var smtcCoveredHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dupCounter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // 1) 主通道：枚举全部 SMTC 会话（不因暂停或暂缺标题而丢弃 Spotify 等有效会话）
            if (_manager is not null)
            {
                var sessions = _manager.GetSessions();
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        var appId = s.SourceAppUserModelId ?? $"unknown-{i}";
                        dupCounter.TryGetValue(appId, out int count);
                        dupCounter[appId] = count + 1;
                        string stableId = count == 0 ? $"smtc:{appId}" : $"smtc:{appId}#{count}";

                        var info = await BuildSmtcInfoAsync(s, stableId, appId).ConfigureAwait(false);
                        if (info is null) continue;

                        snapshotMap[info.Id] = info;
                        smtcCoveredHints.Add(PolicyConfigInterop.CleanProcessHint(appId));
                        if (!string.IsNullOrWhiteSpace(info.SourceDisplayName))
                        {
                            smtcCoveredHints.Add(info.SourceDisplayName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"读取 SMTC 会话失败：{ex.Message}");
                    }
                }
            }

            // 2) 兜底通道：未接入 SMTC 的网易云等播放器（先触发一次后台合成器刷新，保证即使聚光卡未打开也能拿到实时发声态）
            if (EnableFallbackPlayers)
            {
                try
                {
                    _audioService.RequestRefreshMixer();
                    var mixerSessions = _audioService.GetVolumeMixerSessions();
                    foreach (var ms in mixerSessions)
                    {
                        if (!FallbackPlayerProcesses.Contains(ms.ProcessName, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (smtcCoveredHints.Contains(ms.ProcessName) || smtcCoveredHints.Contains(ms.DisplayName))
                        {
                            continue;
                        }

                        var fallback = await BuildFallbackInfoAsync(ms).ConfigureAwait(false);
                        if (fallback is not null)
                        {
                            snapshotMap[fallback.Id] = fallback;
                        }
                    }
                }
                catch { }
            }

            // 3) 维护稳定有序列表 _orderedKeys：
            //    - 移除已消失的实例
            _orderedKeys.RemoveAll(k => !snapshotMap.ContainsKey(k));

            //    - 新加入的实例插入首位（其余顺延后移）
            foreach (var kv in snapshotMap)
            {
                if (!_orderedKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    _orderedKeys.Insert(0, kv.Key);
                }
            }

            //    - 检测「从暂停 -> 播放」的状态跃迁：与第 1 位交换位置
            foreach (var kv in snapshotMap)
            {
                bool wasPlaying = _wasPlayingMap.TryGetValue(kv.Key, out var prevP) && prevP;
                bool nowPlaying = kv.Value.IsPlaying;
                _wasPlayingMap[kv.Key] = nowPlaying;

                if (!wasPlaying && nowPlaying)
                {
                    int idx = _orderedKeys.FindIndex(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (idx > 0)
                    {
                        (_orderedKeys[0], _orderedKeys[idx]) = (_orderedKeys[idx], _orderedKeys[0]);
                    }
                }
            }

            // 清理已消失实例的状态记录
            var staleKeys = _wasPlayingMap.Keys.Where(k => !snapshotMap.ContainsKey(k)).ToList();
            foreach (var sk in staleKeys) _wasPlayingMap.Remove(sk);

            // 按 _orderedKeys 组装最终列表
            var orderedList = new List<MediaInstanceInfo>(_orderedKeys.Count);
            foreach (var key in _orderedKeys)
            {
                if (snapshotMap.TryGetValue(key, out var inst))
                {
                    orderedList.Add(inst);
                }
            }

            _instances = orderedList;

            // 若当前选中实例已关闭或尚未选中，默认选中第 1 位
            if (orderedList.Count == 0)
            {
                _selectedKey = null;
            }
            else if (string.IsNullOrEmpty(_selectedKey) ||
                     !orderedList.Any(x => string.Equals(x.Id, _selectedKey, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedKey = orderedList[0].Id;
            }

            var summary = string.Join(" | ", orderedList.Select(x => $"{x.SourceDisplayName}:{(x.IsPlaying ? "▶" : "⏸")}"));
            if (!string.Equals(summary, _lastLoggedSummary, StringComparison.Ordinal))
            {
                _lastLoggedSummary = summary;
                _log($"媒体实例更新（{orderedList.Count} 个）：{(string.IsNullOrEmpty(summary) ? "无" : summary)}");
            }

            SessionsUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            _log($"刷新媒体会话失败：{ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var cur = CurrentInstance;
        if (cur is null) return;

        if (cur.SmtcSession is not null)
        {
            try
            {
                await cur.SmtcSession.TryTogglePlayPauseAsync();
                await Task.Delay(120).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
                return;
            }
            catch { }
        }

        // 兜底实例（如网易云）：避免发送未处理的 WM_APPCOMMAND 被 DefWindowProc 转发给系统 SMTC 唤醒 QQ 音乐
        if (cur.IsFallback)
        {
            if (cur.SourceAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
            {
                _netEaseElog.NotifyOptimisticTogglePlayPause();
            }
            PolicyConfigInterop.ControlFallbackPlayer(cur.SourceAppId, cur.ProcessId, PolicyConfigInterop.APPCOMMAND_MEDIA_PLAY_PAUSE);
            await RefreshAsync().ConfigureAwait(false);
            await Task.Delay(260).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    public async Task NextAsync()
    {
        var cur = CurrentInstance;
        if (cur is null) return;

        if (cur.SmtcSession is not null)
        {
            try
            {
                await cur.SmtcSession.TrySkipNextAsync();
                await Task.Delay(150).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
                return;
            }
            catch { }
        }

        if (cur.IsFallback)
        {
            PolicyConfigInterop.ControlFallbackPlayer(cur.SourceAppId, cur.ProcessId, PolicyConfigInterop.APPCOMMAND_MEDIA_NEXTTRACK);
            await Task.Delay(250).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    public async Task PreviousAsync()
    {
        var cur = CurrentInstance;
        if (cur is null) return;

        if (cur.SmtcSession is not null)
        {
            try
            {
                await cur.SmtcSession.TrySkipPreviousAsync();
                await Task.Delay(150).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
                return;
            }
            catch { }
        }

        if (cur.IsFallback)
        {
            PolicyConfigInterop.ControlFallbackPlayer(cur.SourceAppId, cur.ProcessId, PolicyConfigInterop.APPCOMMAND_MEDIA_PREVIOUSTRACK);
            await Task.Delay(250).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    /// <summary>跳转进度。返回 true 表示播放器原生支持 Seek；返回 false 表示仅校准了本地歌词进度（如网易云兜底）。</summary>
    public async Task<bool> SeekAsync(TimeSpan target)
    {
        var cur = CurrentInstance;
        if (cur is null || cur.Duration <= TimeSpan.Zero) return true;

        var clamped = TimeSpan.FromTicks(Math.Clamp(target.Ticks, 0, cur.Duration.Ticks));
        if (cur.SmtcSession is not null)
        {
            try
            {
                await cur.SmtcSession.TryChangePlaybackPositionAsync(clamped.Ticks);
                await Task.Delay(100).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
                return true;
            }
            catch { return true; }
        }

        if (cur.IsFallback)
        {
            if (cur.SourceAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
            {
                _netEaseElog.CalibratePosition(clamped);
            }
            if (_fallbackClocks.TryGetValue(cur.Id, out var clock))
            {
                clock.Accumulated = clamped;
                clock.LastUpdated = DateTimeOffset.UtcNow;
            }
            await RefreshAsync().ConfigureAwait(false);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 定时暂停到期执行：
    ///   - 若当前有任何实例正在播放，则暂停所有正在播放的实例，并返回 true；
    ///   - 若当前没有任何媒体正在播放，则按用户预期将行为替换为触发播放，并返回 false。
    /// </summary>
    public async Task<bool> ExecuteSleepTimerActionAsync()
    {
        var playingList = _instances.Where(x => x.IsPlaying).ToList();
        if (playingList.Count > 0)
        {
            foreach (var inst in playingList)
            {
                if (inst.SmtcSession is not null)
                {
                    try { await inst.SmtcSession.TryPauseAsync(); } catch { }
                }
                else if (inst.IsFallback)
                {
                    if (inst.SourceAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
                    {
                        _netEaseElog.NotifyOptimisticTogglePlayPause();
                    }
                    PolicyConfigInterop.ControlFallbackPlayer(inst.SourceAppId, inst.ProcessId, PolicyConfigInterop.APPCOMMAND_MEDIA_PLAY_PAUSE);
                }
            }
            await Task.Delay(180).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            return true;
        }

        // 当前没有任何媒体在播放：触发当前选中实例（或系统媒体键）开始播放
        var cur = CurrentInstance;
        if (cur?.SmtcSession is not null)
        {
            try { await cur.SmtcSession.TryPlayAsync(); } catch { }
        }
        else if (cur is not null && cur.IsFallback)
        {
            if (cur.SourceAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
            {
                _netEaseElog.NotifyOptimisticTogglePlayPause();
            }
            PolicyConfigInterop.ControlFallbackPlayer(cur.SourceAppId, cur.ProcessId, PolicyConfigInterop.APPCOMMAND_MEDIA_PLAY_PAUSE);
        }
        else
        {
            PolicyConfigInterop.SendMediaKey(PolicyConfigInterop.VK_MEDIA_PLAY_PAUSE);
        }

        await Task.Delay(180).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
        return false;
    }

    public bool ActivateCurrentSourceWindow()
    {
        var cur = CurrentInstance;
        if (cur is null) return false;
        return PolicyConfigInterop.ActivateWindowByProcessIdOrName(cur.ProcessId, cur.SourceAppId, cur.SourceDisplayName);
    }

    private async Task<MediaInstanceInfo?> BuildSmtcInfoAsync(
        GlobalSystemMediaTransportControlsSession session,
        string stableId,
        string appId)
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch { }

        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();

        var status = playback?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        var title = props?.Title?.Trim() ?? "";
        var artist = props?.Artist?.Trim() ?? "";
        var album = props?.AlbumTitle?.Trim() ?? "";

        // 仅当会话已关闭且无任何信息时跳过；Paused 的 Spotify 等有效会话一律保留！
        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed &&
            string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // 1) 通过 Windows Shell AppsFolder 解析真实应用名与图标（完美支持 Zen/Firefox 哈希 AUMID 和 Spotify Store 版）
        var resolved = await GetOrResolveAppsFolderAsync(appId).ConfigureAwait(false);

        byte[]? thumbBytes = null;
        if (props?.Thumbnail is not null)
        {
            try
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                if (stream.Size > 0 && stream.Size < 8 * 1024 * 1024)
                {
                    var buf = new byte[stream.Size];
                    await stream.ReadAsync(buf.AsBuffer(), (uint)buf.Length, InputStreamOptions.None);
                    thumbBytes = buf;
                }
            }
            catch { }
        }

        var hint = PolicyConfigInterop.CleanProcessHint(appId);
        string displayName = !string.IsNullOrWhiteSpace(resolved?.DisplayName)
            ? resolved.DisplayName
            : FriendlyAppName(hint);

        int pid = 0;
        string? exePath = null;
        TryFindProcessInfo(hint, displayName, out pid, out exePath);

        byte[]? iconBytes = resolved?.IconPngBytes;
        if (iconBytes is null && !string.IsNullOrWhiteSpace(exePath))
        {
            iconBytes = await GetOrLoadExeIconAsync(hint, exePath).ConfigureAwait(false);
        }

        return new MediaInstanceInfo
        {
            Id = stableId,
            SourceAppId = appId,
            SourceDisplayName = displayName,
            ProcessId = pid,
            IsFallback = false,
            Title = string.IsNullOrWhiteSpace(title) ? displayName : title,
            Artist = artist,
            Album = album,
            IsPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Position = timeline?.Position ?? TimeSpan.Zero,
            Duration = timeline?.EndTime ?? TimeSpan.Zero,
            TimelineUpdatedAt = timeline?.LastUpdatedTime ?? DateTimeOffset.UtcNow,
            ThumbnailBytes = thumbBytes,
            AppIconBytes = iconBytes,
            SmtcSession = session,
        };
    }

    private async Task<MediaInstanceInfo?> BuildFallbackInfoAsync(MixerSessionInfo ms)
    {
        IntPtr hwnd = PolicyConfigInterop.FindMainWindowForProcessGroup(ms.ProcessId, ms.ProcessName);
        string rawTitle = hwnd != IntPtr.Zero ? PolicyConfigInterop.GetWindowTitle(hwnd) : "";

        string? exePath = null;
        try
        {
            using var proc = Process.GetProcessById(ms.ProcessId);
            exePath = proc.MainModule?.FileName;
        }
        catch { }

        var iconBytes = ms.AppIconBytes ?? await GetOrLoadExeIconAsync(ms.ProcessName, exePath).ConfigureAwait(false);
        string id = $"fallback:{ms.ProcessName}";

        // 1) 网易云音乐专属 elog + playingList 深度解析（支持精准播放/暂停状态、真实封面、真实时长与 Seek 同步）
        if (ms.ProcessName.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
        {
            var elogSnap = _netEaseElog.PollSnapshot(ms.IsGeneratingSound, rawTitle);
            if (elogSnap is not null)
            {
                return new MediaInstanceInfo
                {
                    Id = id,
                    SourceAppId = ms.ProcessName,
                    SourceDisplayName = ms.DisplayName,
                    ProcessId = ms.ProcessId,
                    IsFallback = true,
                    NetEaseSongId = elogSnap.SongId,
                    Title = elogSnap.Title,
                    Artist = elogSnap.Artist,
                    Album = elogSnap.Album,
                    IsPlaying = elogSnap.IsPlaying,
                    Position = elogSnap.Position,
                    Duration = elogSnap.Duration,
                    TimelineUpdatedAt = elogSnap.UpdatedAt,
                    ThumbnailBytes = elogSnap.CoverBytes,
                    AppIconBytes = iconBytes,
                };
            }
        }

        if (string.IsNullOrWhiteSpace(rawTitle)) return null;

        string title = rawTitle;
        string artist = "";
        var parts = rawTitle.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            title = parts[0].Trim();
            artist = parts[1].Trim();
        }

        string trackKey = $"{title}||{artist}";
        var now = DateTimeOffset.UtcNow;
        bool isPlayingNow = ms.IsGeneratingSound || ms.IsActive;

        var clock = _fallbackClocks.GetOrAdd(id, _ => new FallbackClock { TrackKey = trackKey, LastUpdated = now });
        if (!string.Equals(clock.TrackKey, trackKey, StringComparison.OrdinalIgnoreCase))
        {
            clock.TrackKey = trackKey;
            clock.Accumulated = TimeSpan.Zero;
            clock.LastUpdated = now;
        }
        else if (clock.WasPlaying)
        {
            var delta = now - clock.LastUpdated;
            if (delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(15))
            {
                clock.Accumulated += delta;
            }
            clock.LastUpdated = now;
        }
        else
        {
            clock.LastUpdated = now;
        }
        clock.WasPlaying = isPlayingNow;

        TimeSpan duration = TimeSpan.Zero;
        if (_lyricsService.TryGetCachedDuration(title, artist, out var cachedDur))
        {
            duration = cachedDur;
            if (clock.Accumulated > duration) clock.Accumulated = duration;
        }

        return new MediaInstanceInfo
        {
            Id = id,
            SourceAppId = ms.ProcessName,
            SourceDisplayName = ms.DisplayName,
            ProcessId = ms.ProcessId,
            IsFallback = true,
            Title = title,
            Artist = artist,
            IsPlaying = isPlayingNow,
            Position = clock.Accumulated,
            Duration = duration,
            TimelineUpdatedAt = now,
            AppIconBytes = iconBytes,
        };
    }

    private async Task<ResolvedAppInfo?> GetOrResolveAppsFolderAsync(string aumid)
    {
        if (_appsFolderCache.TryGetValue(aumid, out var cached)) return cached;
        var resolved = await PolicyConfigInterop.ResolveFromAppsFolderAsync(aumid).ConfigureAwait(false);
        _appsFolderCache[aumid] = resolved;
        return resolved;
    }

    private async Task<byte[]?> GetOrLoadExeIconAsync(string key, string? exePath)
    {
        if (_exeIconCache.TryGetValue(key, out var cached)) return cached;
        var bytes = await PolicyConfigInterop.ExtractExeIconPngAsync(exePath).ConfigureAwait(false);
        _exeIconCache[key] = bytes;
        return bytes;
    }

    private static void TryFindProcessInfo(string hint, string displayName, out int pid, out string? exePath)
    {
        pid = 0;
        exePath = null;

        var names = new List<string> { hint };
        var low = displayName.ToLowerInvariant();
        if (low.Contains("zen")) names.Add("zen");
        if (low.Contains("firefox")) names.Add("firefox");
        if (low.Contains("spotify")) names.Add("Spotify");
        if (low.Contains("edge")) names.Add("msedge");
        if (low.Contains("chrome")) names.Add("chrome");

        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            try
            {
                var procs = Process.GetProcessesByName(n);
                foreach (var p in procs)
                {
                    try
                    {
                        if (pid == 0) pid = p.Id;
                        if (exePath is null) exePath = p.MainModule?.FileName;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                if (pid != 0) return;
            }
            catch { }
        }
    }

    private static string FriendlyAppName(string hint) => hint.ToLowerInvariant() switch
    {
        "cloudmusic" => "网易云音乐",
        "qqmusic" => "QQ音乐",
        "kugou" => "酷狗音乐",
        "kwmusic" => "酷我音乐",
        "spotify" => "Spotify",
        "msedge" => "Microsoft Edge",
        "chrome" => "Chrome",
        "firefox" => "Firefox",
        "zen" => "Zen Browser",
        "potplayermini64" or "potplayermini" => "PotPlayer",
        "music.ui" or "microsoft.zunemusic" => "媒体播放器",
        "applemusic" => "Apple Music",
        _ => string.IsNullOrWhiteSpace(hint) ? "媒体" : hint,
    };

    public void Dispose()
    {
        _disposed = true;
        _netEaseElog.Dispose();
        _refreshGate.Dispose();
    }
}
