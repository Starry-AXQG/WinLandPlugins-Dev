using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Windows.Media.Audio;
using Windows.Media.Devices;

namespace MediaDeck.Services;

public enum AudioOutputKind
{
    Speaker,
    Headphone,
    DisplayAudio,
}

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    string ShortName,
    AudioOutputKind Kind,
    bool IsBluetooth,
    bool IsDefault)
{
    public string Glyph => Kind switch
    {
        AudioOutputKind.Headphone => "\uE7F6",
        AudioOutputKind.DisplayAudio => "\uE7F4",
        _ => "\uE7F5",
    };
}

public sealed record MixerSessionInfo(
    string SessionKey,
    int ProcessId,
    string DisplayName,
    string ProcessName,
    float Volume,
    bool IsMuted,
    bool IsActive,
    bool IsGeneratingSound,
    byte[]? AppIconBytes);

public sealed record SpatialSoundOption(
    string Subtype,
    string DisplayName,
    bool IsActive,
    bool IsSupported);

/// <summary>
/// 三通道分离式后台音频中枢（借鉴 EarTrumpet / SoundSwitch 架构）：
///   1. 实时控制线程（MediaDeck.AudioControl）：独立 MTA 高优线程 + 最新状态原子合并，专门处理默认设备切换、
///      系统主音量/静音、应用音量/静音，绝不与枚举任务同队列排队，彻底杜绝切换耳机后点静音延后数秒的队头阻塞；
///   2. 合并式枚举线程（MediaDeck.AudioWorker）：按位合并（Coalescing）+ 60ms 通知风暴防抖，设备切换瞬间的
///      多次系统回调自动合并为单次刷新；
///   3. 实时电平线程（MediaDeck.AudioMeter）：33ms 轻量峰值与音量同步，UI 线程零锁读取。
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    [Flags]
    private enum RefreshFlags
    {
        None = 0,
        Devices = 1 << 0,
        Mixer = 1 << 1,
        Spatial = 1 << 2,
        ForceSpatial = 1 << 3,
        CheckNewlyActive = 1 << 4,
        All = Devices | Mixer | Spatial,
    }

    private sealed record CachedPidMeta(string ProcessName, string? ExePath, long VerifiedAtTicks);

    private static readonly PropertyKey PkeyEndpointFormFactor =
        new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    private static readonly PropertyKey PkeyDeviceFriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    private static readonly PropertyKey PkeyDeviceEnumeratorName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    private static readonly PropertyKey PkeyDeviceInterfaceFriendlyName =
        new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    private readonly Action<string> _log;
    private readonly AutoResetEvent _controlSignal = new(false);
    private readonly AutoResetEvent _refreshSignal = new(false);
    private readonly object _controlLock = new();
    private readonly Thread _controlThread;
    private readonly Thread _audioThread;
    private readonly Thread _meterThread;
    private readonly HashSet<string> _knownActiveDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string DeviceId, DeviceState State)> _deviceStateEvents = new();
    private readonly ConcurrentDictionary<string, byte[]?> _mixerIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, CachedPidMeta> _pidMetaCache = new();
    private readonly ConcurrentDictionary<int, float> _pendingSessionVolumes = new();
    private readonly ConcurrentDictionary<int, bool> _pendingSessionMutes = new();

    // 实时控制待执行状态（受 _controlLock 保护，高频触发自动合并为最新值）
    private string? _pendingSwitchDeviceId;
    private int? _pendingMasterVolume;
    private bool? _pendingMasterMute;
    private string? _pendingSpatialSubtype;

    // 合并式枚举标记
    private int _pendingRefreshFlags;
    private long _refreshNotBeforeTicks;
    private long _delayedDeviceRecheckTicks;
    private string? _lastSpatialDeviceId;

    // 仅在 _audioThread 上访问的 COM 实例
    private MMDeviceEnumerator? _enumerator;
    private DeviceNotificationClient? _notifyClient;

    // UI 线程无锁只读的原子快照
    private volatile List<AudioDeviceInfo> _cachedDevices = [];
    private volatile List<MixerSessionInfo> _cachedMixer = [];
    private volatile List<SpatialSoundOption> _cachedSpatial = [];
    private long _devicesVersion;
    private long _mixerVersion;
    private long _spatialVersion;

    // 实时音频电平与系统主音量（后台电平线程写入，UI 线程无锁读取）
    private volatile float _peakMaster;
    private volatile float _peakLeft;
    private volatile float _peakRight;
    private volatile int _masterVolumePercent = 50;
    private volatile bool _masterMuted;
    private long _masterVolLockUntilTicks;
    private volatile bool _resetMeterFlag;
    private volatile string? _lockedTargetDeviceId;
    private long _lockedTargetUntilTicks;
    private volatile bool _disposed;

    public int MasterVolumePercent => _masterVolumePercent;
    public bool MasterMuted => _masterMuted;

    public IReadOnlyList<AudioDeviceInfo> CachedOutputDevices => _cachedDevices;
    public IReadOnlyList<MixerSessionInfo> CachedMixerSessions => _cachedMixer;
    public IReadOnlyList<SpatialSoundOption> CachedSpatialOptions => _cachedSpatial;

    public long DevicesVersion => Interlocked.Read(ref _devicesVersion);
    public long MixerVersion => Interlocked.Read(ref _mixerVersion);
    public long SpatialVersion => Interlocked.Read(ref _spatialVersion);

    public event Action? DevicesChanged;
    public event Action? CacheUpdated;
    public event Action<AudioDeviceInfo>? BluetoothHeadphonesConnected;

    public AudioDeviceService(Action<string> log)
    {
        _log = log;

        // 1. 专属高优实时控制线程（设备快切、静音、音量直达，零排队阻塞）
        _controlThread = new Thread(AudioControlLoop)
        {
            Name = "MediaDeck.AudioControl",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _controlThread.SetApartmentState(ApartmentState.MTA);
        _controlThread.Start();

        // 2. 专属合并式枚举线程（MTA，不与 WinUI STA 线程或实时控制线程抢锁）
        _audioThread = new Thread(AudioWorkerLoop)
        {
            Name = "MediaDeck.AudioWorker",
            IsBackground = true,
        };
        _audioThread.SetApartmentState(ApartmentState.MTA);
        _audioThread.Start();

        // 3. 轻量音频电平采样线程（每 33ms 采样一次，零 UI 阻塞）
        _meterThread = new Thread(MeterLoop)
        {
            Name = "MediaDeck.AudioMeter",
            IsBackground = true,
        };
        _meterThread.SetApartmentState(ApartmentState.MTA);
        _meterThread.Start();
    }

    /// <summary>UI 线程零阻塞读取最新音频电平。</summary>
    public (float Master, float Left, float Right) GetCurrentAudioPeak()
        => (_peakMaster, _peakLeft, _peakRight);

    /// <summary>调整系统主音量（0..100），乐观更新 UI，投递至独立高优控制线程立即写入 CoreAudio。</summary>
    public int SetMasterVolume(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        _masterVolumePercent = clamped;
        if (clamped > 0) _masterMuted = false;
        Interlocked.Exchange(ref _masterVolLockUntilTicks, DateTimeOffset.UtcNow.AddMilliseconds(1000).UtcTicks);

        lock (_controlLock)
        {
            _pendingMasterVolume = clamped;
            if (clamped > 0 && _pendingMasterMute == true)
            {
                _pendingMasterMute = false;
            }
        }
        SignalControl();
        return clamped;
    }

    /// <summary>按步进（如 ±2）调整系统主音量。</summary>
    public int StepMasterVolume(int deltaPercent)
        => SetMasterVolume(_masterVolumePercent + deltaPercent);

    /// <summary>切换系统主音量静音状态，乐观更新 UI，投递至独立高优控制线程毫秒级写入 CoreAudio。</summary>
    public bool ToggleMasterMute()
    {
        bool nextMuted = !_masterMuted;
        _masterMuted = nextMuted;
        Interlocked.Exchange(ref _masterVolLockUntilTicks, DateTimeOffset.UtcNow.AddMilliseconds(1000).UtcTicks);

        lock (_controlLock)
        {
            _pendingMasterMute = nextMuted;
        }
        SignalControl();
        return nextMuted;
    }

    /// <summary>向后台枚举线程投递合并式全量刷新（含强制空间音效重查）。</summary>
    public void RequestRefreshAll()
    {
        if (_disposed) return;
        RequestCoalescedRefresh(RefreshFlags.All | RefreshFlags.ForceSpatial, debounceMs: 0);
    }

    /// <summary>向后台枚举线程投递轻量输出设备重刷（&lt; 1ms，用于展开菜单/聚光卡时兜底校验）。</summary>
    public void RequestRefreshDevices()
    {
        if (_disposed) return;
        RequestCoalescedRefresh(RefreshFlags.Devices, debounceMs: 0);
    }

    /// <summary>向后台枚举线程投递合并式音量合成器刷新（队列已有待跑任务时自动去重）。</summary>
    public void RequestRefreshMixer()
    {
        if (_disposed) return;
        RequestCoalescedRefresh(RefreshFlags.Mixer, debounceMs: 0);
    }

    /// <summary>
    /// 异步切换默认音频输出设备：
    ///   - 立即乐观更新快照并设置 1.6s 目标设备锁；
    ///   - 由独立高优实时控制线程直接调用 IPolicyConfig 并同步新设备真实音量/静音态，零队列积压。
    /// </summary>
    public void SwitchOutputDeviceAsync(string deviceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(deviceId)) return;

        _lockedTargetDeviceId = deviceId;
        Interlocked.Exchange(ref _lockedTargetUntilTicks, DateTimeOffset.UtcNow.AddMilliseconds(1600).UtcTicks);

        // 乐观更新，UI 零等待
        _cachedDevices = _cachedDevices
            .Select(d => d with { IsDefault = string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase) })
            .ToList();
        Interlocked.Increment(ref _devicesVersion);
        DevicesChanged?.Invoke();

        lock (_controlLock)
        {
            _pendingSwitchDeviceId = deviceId;
        }
        SignalControl();
    }

    public List<AudioDeviceInfo> GetOutputDevices() => _cachedDevices;

    public List<MixerSessionInfo> GetVolumeMixerSessions() => _cachedMixer;

    public List<SpatialSoundOption> GetSpatialSoundOptions() => _cachedSpatial;

    public void SetSessionVolume(int processId, float volume)
    {
        if (_disposed) return;
        float clamped = Math.Clamp(volume, 0f, 1f);

        _cachedMixer = _cachedMixer
            .Select(s => s.ProcessId == processId ? s with { Volume = clamped, IsMuted = clamped > 0 ? false : s.IsMuted } : s)
            .ToList();

        _pendingSessionVolumes[processId] = clamped;
        SignalControl();
    }

    public void SetSessionMute(int processId, bool muted)
    {
        if (_disposed) return;

        _cachedMixer = _cachedMixer
            .Select(s => s.ProcessId == processId ? s with { IsMuted = muted, IsGeneratingSound = muted ? false : s.IsGeneratingSound } : s)
            .ToList();
        Interlocked.Increment(ref _mixerVersion);

        _pendingSessionMutes[processId] = muted;
        SignalControl();
    }

    public void SetSpatialSoundAsync(string subtype)
    {
        if (_disposed) return;

        // 乐观更新
        _cachedSpatial = _cachedSpatial
            .Select(o => o with { IsActive = string.Equals(o.Subtype, subtype ?? string.Empty, StringComparison.OrdinalIgnoreCase) })
            .ToList();
        Interlocked.Increment(ref _spatialVersion);
        CacheUpdated?.Invoke();

        lock (_controlLock)
        {
            _pendingSpatialSubtype = subtype ?? string.Empty;
        }
        SignalControl();
    }

    private void SignalControl()
    {
        if (_disposed) return;
        try { _controlSignal.Set(); } catch { }
    }

    private void RequestCoalescedRefresh(RefreshFlags flags, int debounceMs)
    {
        if (_disposed) return;
        Interlocked.Or(ref _pendingRefreshFlags, (int)flags);
        if (debounceMs > 0)
        {
            long targetTicks = DateTimeOffset.UtcNow.AddMilliseconds(debounceMs).UtcTicks;
            long cur = Interlocked.Read(ref _refreshNotBeforeTicks);
            if (targetTicks > cur)
            {
                Interlocked.Exchange(ref _refreshNotBeforeTicks, targetTicks);
            }
        }
        try { _refreshSignal.Set(); } catch { }
    }

    /// <summary>
    /// 独立高优实时控制循环：毫秒级执行设备快切、静音与音量写入，绝不被枚举或空间音效查询阻塞。
    /// </summary>
    private void AudioControlLoop()
    {
        MMDeviceEnumerator? controlEnum = null;
        try
        {
            controlEnum = new MMDeviceEnumerator();
            while (!_disposed)
            {
                _controlSignal.WaitOne(500);
                if (_disposed) break;

                while (!_disposed)
                {
                    string? switchId;
                    int? masterVol;
                    bool? masterMute;
                    string? spatialSubtype;

                    lock (_controlLock)
                    {
                        switchId = _pendingSwitchDeviceId;
                        _pendingSwitchDeviceId = null;

                        masterVol = _pendingMasterVolume;
                        _pendingMasterVolume = null;

                        masterMute = _pendingMasterMute;
                        _pendingMasterMute = null;

                        spatialSubtype = _pendingSpatialSubtype;
                        _pendingSpatialSubtype = null;
                    }

                    bool hasSessionWork = !_pendingSessionVolumes.IsEmpty || !_pendingSessionMutes.IsEmpty;
                    if (switchId is null && masterVol is null && masterMute is null && spatialSubtype is null && !hasSessionWork)
                    {
                        break;
                    }

                    // 1) 优先执行默认音频输出设备切换
                    if (switchId is not null)
                    {
                        bool ok = PolicyConfigInterop.SetDefaultAudioEndpoint(switchId);
                        if (!ok)
                        {
                            _lockedTargetDeviceId = null;
                            Interlocked.Exchange(ref _lockedTargetUntilTicks, 0);
                        }
                        else
                        {
                            // 切换成功后，若用户没有同时按下静音/调音量，立即读取新耳机/扬声器的真实音量与静音状态同步给 UI
                            bool userOverrodeVolOrMute;
                            lock (_controlLock)
                            {
                                userOverrodeVolOrMute = masterVol.HasValue || masterMute.HasValue ||
                                                        _pendingMasterVolume.HasValue || _pendingMasterMute.HasValue;
                            }
                            if (!userOverrodeVolOrMute)
                            {
                                try
                                {
                                    using var targetDev = controlEnum.GetDevice(switchId);
                                    var ep = targetDev.AudioEndpointVolume;
                                    _masterVolumePercent = Math.Clamp((int)Math.Round(ep.MasterVolumeLevelScalar * 100f), 0, 100);
                                    _masterMuted = ep.Mute;
                                    Interlocked.Exchange(ref _masterVolLockUntilTicks, 0);
                                }
                                catch { }
                            }
                        }

                        _resetMeterFlag = true;
                        RequestCoalescedRefresh(RefreshFlags.All, debounceMs: 70);
                    }

                    // 2) 立即执行系统主音量 / 主静音（同时覆盖目标锁定设备与当前默认设备，杜绝设备切换过渡期漏静音）
                    if (masterVol.HasValue || masterMute.HasValue)
                    {
                        ApplyMasterVolumeAndMuteImmediate(controlEnum, masterVol, masterMute);
                        Interlocked.Exchange(ref _masterVolLockUntilTicks, DateTimeOffset.UtcNow.AddMilliseconds(650).UtcTicks);
                    }

                    // 3) 立即执行单个应用音量 / 静音
                    if (hasSessionWork)
                    {
                        ApplySessionControlsImmediate(controlEnum);
                    }

                    // 4) 空间音效切换
                    if (spatialSubtype is not null)
                    {
                        try
                        {
                            var winrtDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                            if (!string.IsNullOrWhiteSpace(winrtDeviceId))
                            {
                                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(winrtDeviceId);
                                config?.SetDefaultSpatialAudioFormatAsync(spatialSubtype).AsTask().GetAwaiter().GetResult();
                            }
                        }
                        catch (Exception ex)
                        {
                            _log($"切换空间音效失败：{ex.Message}");
                        }
                        finally
                        {
                            RequestCoalescedRefresh(RefreshFlags.Spatial | RefreshFlags.ForceSpatial, debounceMs: 0);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log($"实时音频控制线程异常：{ex.Message}");
        }
        finally
        {
            try { controlEnum?.Dispose(); } catch { }
        }
    }

    private void ApplyMasterVolumeAndMuteImmediate(MMDeviceEnumerator controlEnum, int? vol, bool? mute)
    {
        string? appliedDeviceId = null;
        var lockedId = _lockedTargetDeviceId;
        if (!string.IsNullOrEmpty(lockedId) &&
            DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _lockedTargetUntilTicks))
        {
            try
            {
                using var targetDev = controlEnum.GetDevice(lockedId);
                WriteEndpointVolumeAndMute(targetDev, vol, mute);
                appliedDeviceId = targetDev.ID;
            }
            catch { }
        }

        try
        {
            using var def = controlEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (!string.Equals(def.ID, appliedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                WriteEndpointVolumeAndMute(def, vol, mute);
            }
        }
        catch { }
    }

    private static void WriteEndpointVolumeAndMute(MMDevice dev, int? vol, bool? mute)
    {
        var ep = dev.AudioEndpointVolume;
        if (vol.HasValue)
        {
            ep.MasterVolumeLevelScalar = vol.Value / 100f;
            if (vol.Value > 0 && mute != true && ep.Mute)
            {
                ep.Mute = false;
            }
        }
        if (mute.HasValue)
        {
            ep.Mute = mute.Value;
        }
    }

    private void ApplySessionControlsImmediate(MMDeviceEnumerator controlEnum)
    {
        var vols = new Dictionary<int, float>();
        foreach (var kv in _pendingSessionVolumes)
        {
            if (_pendingSessionVolumes.TryRemove(kv.Key, out var v))
                vols[kv.Key] = v;
        }

        var mutes = new Dictionary<int, bool>();
        foreach (var kv in _pendingSessionMutes)
        {
            if (_pendingSessionMutes.TryRemove(kv.Key, out var m))
                mutes[kv.Key] = m;
        }

        if (vols.Count == 0 && mutes.Count == 0) return;

        try
        {
            MMDevice? endpoint = null;
            var lockedId = _lockedTargetDeviceId;
            if (!string.IsNullOrEmpty(lockedId) &&
                DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _lockedTargetUntilTicks))
            {
                try { endpoint = controlEnum.GetDevice(lockedId); } catch { }
            }
            endpoint ??= controlEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            using (endpoint)
            {
                var sessions = endpoint.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    int pid = s.IsSystemSoundsSession ? 0 : (int)s.GetProcessID;
                    if (!s.IsSystemSoundsSession && pid <= 0) continue;

                    if (vols.TryGetValue(pid, out float targetVol))
                    {
                        try { s.SimpleAudioVolume.Volume = targetVol; } catch { }
                    }
                    if (mutes.TryGetValue(pid, out bool targetMute))
                    {
                        try { s.SimpleAudioVolume.Mute = targetMute; } catch { }
                    }
                }
            }
        }
        catch { }
    }

    private void ScheduleDelayedDeviceRecheck(int delayMs)
    {
        if (_disposed) return;
        long target = DateTimeOffset.UtcNow.AddMilliseconds(delayMs).UtcTicks;
        Interlocked.Exchange(ref _delayedDeviceRecheckTicks, target);
    }

    private void AudioWorkerLoop()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _notifyClient = new DeviceNotificationClient(this);

            // 1) 最优先注册系统音频端点回调，彻底消除「开机预热合成器/空间音效期间蓝牙耳机完成自动回连被漏掉」的监听盲区
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(_notifyClient);
            }
            catch (Exception ex)
            {
                _log($"注册音频设备热插拔监听失败：{ex.Message}");
            }

            // 2) 首轮枚举输出设备并立即通知 UI，不等耗时的空间音效初始化
            RefreshDevicesOnWorker();
            foreach (var d in _cachedDevices)
            {
                _knownActiveDeviceIds.Add(d.Id);
            }
            DevicesChanged?.Invoke();

            // 3) 预热音量合成器与空间音效，随后立即补做一次设备复核 + 安排开机 2.5s 蓝牙握手稳定复核
            RefreshMixerOnWorker();
            RefreshSpatialOnWorker(force: true);
            if (RefreshDevicesOnWorker())
            {
                foreach (var d in _cachedDevices) _knownActiveDeviceIds.Add(d.Id);
                DevicesChanged?.Invoke();
            }
            CacheUpdated?.Invoke();
            ScheduleDelayedDeviceRecheck(2500);

            int idleSeconds = 0;

            while (!_disposed)
            {
                bool signaled = _refreshSignal.WaitOne(1000);
                if (_disposed) break;

                if (!signaled)
                {
                    idleSeconds++;
                }

                // 若处于设备切换通知风暴防抖窗口内，短暂等待让多次回调自动合并为一次枚举
                while (!_disposed)
                {
                    long notBefore = Interlocked.Read(ref _refreshNotBeforeTicks);
                    long remainTicks = notBefore - DateTimeOffset.UtcNow.UtcTicks;
                    if (remainTicks <= 0) break;
                    int sleepMs = (int)Math.Clamp(remainTicks / TimeSpan.TicksPerMillisecond, 5, 90);
                    Thread.Sleep(sleepMs);
                }
                if (_disposed) break;

                var flags = (RefreshFlags)Interlocked.Exchange(ref _pendingRefreshFlags, 0);

                // 检查是否有到期的蓝牙/驱动延迟稳定复核
                long delayedAt = Interlocked.Read(ref _delayedDeviceRecheckTicks);
                if (delayedAt > 0 && DateTimeOffset.UtcNow.UtcTicks >= delayedAt)
                {
                    Interlocked.CompareExchange(ref _delayedDeviceRecheckTicks, 0, delayedAt);
                    flags |= RefreshFlags.Devices | RefreshFlags.CheckNewlyActive;
                }

                // 每 10 秒低频轻量自愈巡检一次设备列表（耗时 < 0.5ms，列表未变零事件开销，彻底兜底开机系统服务慢启动）
                if (idleSeconds >= 10)
                {
                    idleSeconds = 0;
                    flags |= RefreshFlags.Devices;
                }

                if (flags == RefreshFlags.None) continue;

                try
                {
                    if ((flags & RefreshFlags.CheckNewlyActive) != 0)
                    {
                        ProcessDeviceStateEventsOnWorker();
                    }

                    bool anyCacheChanged = false;
                    if ((flags & RefreshFlags.Devices) != 0)
                    {
                        if (RefreshDevicesOnWorker())
                        {
                            anyCacheChanged = true;
                        }
                        DevicesChanged?.Invoke();
                    }

                    if ((flags & RefreshFlags.Mixer) != 0)
                    {
                        if (RefreshMixerOnWorker())
                        {
                            anyCacheChanged = true;
                        }
                    }

                    if ((flags & RefreshFlags.Spatial) != 0)
                    {
                        if (RefreshSpatialOnWorker(force: (flags & RefreshFlags.ForceSpatial) != 0))
                        {
                            anyCacheChanged = true;
                        }
                    }

                    if (anyCacheChanged)
                    {
                        CacheUpdated?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    _log($"音频枚举工作线程异常：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log($"音频工作线程初始化失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (_enumerator is not null && _notifyClient is not null)
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_notifyClient);
                }
            }
            catch { }
            try { _enumerator?.Dispose(); } catch { }
            _enumerator = null;
        }
    }

    private void ProcessDeviceStateEventsOnWorker()
    {
        if (_enumerator is null) return;
        while (_deviceStateEvents.TryDequeue(out var ev))
        {
            if (ev.State == DeviceState.Active)
            {
                if (_knownActiveDeviceIds.Add(ev.DeviceId))
                {
                    try
                    {
                        using var dev = _enumerator.GetDevice(ev.DeviceId);
                        if (dev.DataFlow == DataFlow.Render)
                        {
                            var info = BuildDeviceInfo(dev, ev.DeviceId, isDefault: false);
                            if (info.IsBluetooth && info.Kind == AudioOutputKind.Headphone)
                            {
                                BluetoothHeadphonesConnected?.Invoke(info);
                            }
                        }
                    }
                    catch { }
                }
            }
            else
            {
                _knownActiveDeviceIds.Remove(ev.DeviceId);
            }
        }
    }

    private void MeterLoop()
    {
        MMDeviceEnumerator? meterEnum = null;
        MMDevice? activeDevice = null;

        try
        {
            meterEnum = new MMDeviceEnumerator();
            while (!_disposed)
            {
                try
                {
                    if (_resetMeterFlag)
                    {
                        _resetMeterFlag = false;
                        try { activeDevice?.Dispose(); } catch { }
                        activeDevice = null;
                    }

                    if (activeDevice is null)
                    {
                        var lockedId = _lockedTargetDeviceId;
                        if (!string.IsNullOrEmpty(lockedId) &&
                            DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _lockedTargetUntilTicks))
                        {
                            try { activeDevice = meterEnum.GetDevice(lockedId); } catch { }
                        }
                        activeDevice ??= meterEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    }
                    var meter = activeDevice.AudioMeterInformation;
                    float master = Math.Clamp(meter.MasterPeakValue, 0f, 1f);
                    float left = master;
                    float right = master;
                    try
                    {
                        var peaks = meter.PeakValues;
                        if (peaks.Count >= 2)
                        {
                            left = Math.Clamp(peaks[0], 0f, 1f);
                            right = Math.Clamp(peaks[1], 0f, 1f);
                        }
                    }
                    catch { }

                    _peakMaster = master;
                    _peakLeft = left;
                    _peakRight = right;

                    if (DateTimeOffset.UtcNow.UtcTicks >= Interlocked.Read(ref _masterVolLockUntilTicks))
                    {
                        try
                        {
                            var epVol = activeDevice.AudioEndpointVolume;
                            _masterVolumePercent = Math.Clamp((int)Math.Round(epVol.MasterVolumeLevelScalar * 100f), 0, 100);
                            _masterMuted = epVol.Mute;
                        }
                        catch { }
                    }
                }
                catch
                {
                    _peakMaster = 0f;
                    _peakLeft = 0f;
                    _peakRight = 0f;
                    try { activeDevice?.Dispose(); } catch { }
                    activeDevice = null;
                    Thread.Sleep(250);
                }

                Thread.Sleep(33);
            }
        }
        catch { }
        finally
        {
            try { activeDevice?.Dispose(); } catch { }
            try { meterEnum?.Dispose(); } catch { }
        }
    }

    private bool RefreshDevicesOnWorker()
    {
        if (_disposed || _enumerator is null) return false;
        var next = QueryOutputDevicesInternal(_enumerator);
        if (!DevicesEqual(_cachedDevices, next))
        {
            _cachedDevices = next;
            Interlocked.Increment(ref _devicesVersion);
            return true;
        }
        return false;
    }

    private bool RefreshMixerOnWorker()
    {
        if (_disposed || _enumerator is null) return false;
        var next = QueryVolumeMixerInternal(_enumerator);
        if (!MixersEqual(_cachedMixer, next))
        {
            _cachedMixer = next;
            Interlocked.Increment(ref _mixerVersion);
            return true;
        }
        return false;
    }

    private bool RefreshSpatialOnWorker(bool force = false)
    {
        if (_disposed) return false;
        var next = QuerySpatialSoundInternal(force);
        if (next is null) return false;
        if (!SpatialsEqual(_cachedSpatial, next))
        {
            _cachedSpatial = next;
            Interlocked.Increment(ref _spatialVersion);
            return true;
        }
        return false;
    }

    private static bool DevicesEqual(List<AudioDeviceInfo> a, List<AudioDeviceInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static bool MixersEqual(List<MixerSessionInfo> a, List<MixerSessionInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].ProcessId != b[i].ProcessId ||
                a[i].IsMuted != b[i].IsMuted ||
                a[i].IsActive != b[i].IsActive ||
                a[i].IsGeneratingSound != b[i].IsGeneratingSound ||
                Math.Abs(a[i].Volume - b[i].Volume) > 0.01f ||
                !string.Equals(a[i].DisplayName, b[i].DisplayName, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SpatialsEqual(List<SpatialSoundOption> a, List<SpatialSoundOption> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private List<AudioDeviceInfo> QueryOutputDevicesInternal(MMDeviceEnumerator enumerator)
    {
        var list = new List<AudioDeviceInfo>();
        string? defaultId = null;

        var lockedId = _lockedTargetDeviceId;
        if (!string.IsNullOrEmpty(lockedId) &&
            DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _lockedTargetUntilTicks))
        {
            defaultId = lockedId;
        }
        else
        {
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }
        }

        try
        {
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var dev in devices)
            {
                try
                {
                    using (dev)
                    {
                        string id = dev.ID;
                        list.Add(BuildDeviceInfo(dev, id, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                    }
                }
                catch
                {
                    // 单个处于蓝牙握手/驱动初始化瞬态的端点抛异常时隔离跳过，不中断其余设备枚举，并安排延迟复核
                    ScheduleDelayedDeviceRecheck(800);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"枚举音频输出设备失败：{ex.Message}");
            ScheduleDelayedDeviceRecheck(1200);
        }

        return list;
    }

    private List<MixerSessionInfo> QueryVolumeMixerInternal(MMDeviceEnumerator enumerator)
    {
        var result = new List<MixerSessionInfo>();
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = def.AudioSessionManager.Sessions;
            var seenPids = new HashSet<int>();

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                bool isSystemSounds = session.IsSystemSoundsSession;
                int pid = isSystemSounds ? 0 : (int)session.GetProcessID;
                if (!isSystemSounds && pid <= 0) continue;
                if (!seenPids.Add(pid)) continue;

                string procName;
                string displayName;
                string? exePath = null;

                if (isSystemSounds)
                {
                    procName = "__system_sounds__";
                    displayName = "系统声音";
                    var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    var sndVol = Path.Combine(sysDir, "SndVol.exe");
                    exePath = File.Exists(sndVol) ? sndVol : Path.Combine(sysDir, "imageres.dll");
                }
                else
                {
                    displayName = session.DisplayName ?? "";
                    if (!TryResolveProcessMetaFast(pid, out procName, out exePath))
                    {
                        continue;
                    }
                    // 过滤 Windows 音频设备图形隔离系统守护进程 audiodg
                    if (procName.Equals("audiodg", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("@", StringComparison.Ordinal))
                    {
                        displayName = FriendlyProcessName(procName);
                    }
                }

                float vol = 1f;
                bool muted = false;
                float peak = 0f;
                try
                {
                    vol = session.SimpleAudioVolume.Volume;
                    muted = session.SimpleAudioVolume.Mute;
                    peak = session.AudioMeterInformation.MasterPeakValue;
                }
                catch { }

                bool isActive = session.State == AudioSessionState.AudioSessionStateActive;
                bool isGeneratingSound = isActive && !muted && vol > 0.001f && peak > 0.0015f;

                byte[]? iconBytes = null;
                if (!string.IsNullOrWhiteSpace(procName))
                {
                    if (!_mixerIconCache.TryGetValue(procName, out iconBytes) && !string.IsNullOrWhiteSpace(exePath))
                    {
                        try
                        {
                            iconBytes = PolicyConfigInterop.ExtractExeIconPngAsync(exePath).GetAwaiter().GetResult();
                        }
                        catch { }
                        _mixerIconCache[procName] = iconBytes;
                    }
                }

                result.Add(new MixerSessionInfo(
                    SessionKey: $"{pid}",
                    ProcessId: pid,
                    DisplayName: displayName,
                    ProcessName: procName,
                    Volume: Math.Clamp(vol, 0f, 1f),
                    IsMuted: muted,
                    IsActive: isActive,
                    IsGeneratingSound: isGeneratingSound,
                    AppIconBytes: iconBytes));
            }
        }
        catch (Exception ex)
        {
            _log($"读取音量合成器失败：{ex.Message}");
        }

        // 「系统声音」(ProcessId == 0) 始终固定在列表顶部，与 Windows 音量合成器一致
        return result
            .OrderByDescending(x => x.ProcessId == 0)
            .ThenByDescending(x => x.IsActive)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private bool TryResolveProcessMetaFast(int pid, out string procName, out string? exePath)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (_pidMetaCache.TryGetValue(pid, out var cached) &&
            (nowTicks - cached.VerifiedAtTicks) < TimeSpan.FromSeconds(10).Ticks)
        {
            procName = cached.ProcessName;
            exePath = cached.ExePath;
            return true;
        }

        exePath = PolicyConfigInterop.TryGetProcessImagePath(pid);
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            procName = Path.GetFileNameWithoutExtension(exePath);
            if (!string.IsNullOrWhiteSpace(procName))
            {
                _pidMetaCache[pid] = new CachedPidMeta(procName, exePath, nowTicks);
                return true;
            }
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            procName = proc.ProcessName;
            _pidMetaCache[pid] = new CachedPidMeta(procName, exePath, nowTicks);
            return true;
        }
        catch
        {
            _pidMetaCache.TryRemove(pid, out _);
            procName = "";
            return false;
        }
    }

    private List<SpatialSoundOption>? QuerySpatialSoundInternal(bool force)
    {
        var list = new List<SpatialSoundOption>();
        try
        {
            var winrtDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (string.IsNullOrWhiteSpace(winrtDeviceId)) return list;

            // 默认音频设备未变且非强制刷新时，跳过耗时的 Dolby/DTS SpatialAudio COM 查询
            if (!force &&
                _cachedSpatial.Count > 0 &&
                string.Equals(winrtDeviceId, _lastSpatialDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            _lastSpatialDeviceId = winrtDeviceId;

            var config = SpatialAudioDeviceConfiguration.GetForDeviceId(winrtDeviceId);
            if (config is null) return list;

            var active = config.ActiveSpatialAudioFormat ?? string.Empty;
            var isEnabled = !string.IsNullOrEmpty(active);

            list.Add(new SpatialSoundOption(
                Subtype: string.Empty,
                DisplayName: "关",
                IsActive: !isEnabled,
                IsSupported: true));

            // 仅枚举 Windows 11 系统空间音效下拉列表展示的 4 项标准格式（排除未授权的 DTS:X Ultra 等底层引擎项）
            var candidates = new (string Subtype, string Name)[]
            {
                (SpatialAudioFormatSubtype.DolbyAtmosForSpeakers, "Dolby Atmos for Speakers"),
                (SpatialAudioFormatSubtype.WindowsSonic, "用于耳机的 Windows Sonic"),
                (SpatialAudioFormatSubtype.DolbyAtmosForHeadphones, "Dolby Atmos for Headphones"),
                (SpatialAudioFormatSubtype.DTSHeadphoneX, "DTS Headphone:X"),
            };

            foreach (var (subtype, name) in candidates)
            {
                bool supported = false;
                try { supported = config.IsSpatialAudioFormatSupported(subtype); } catch { }
                if (!supported) continue;

                bool selected = isEnabled && string.Equals(active, subtype, StringComparison.OrdinalIgnoreCase);
                list.Add(new SpatialSoundOption(subtype, name, selected, true));
            }
        }
        catch (Exception ex)
        {
            _log($"读取空间音效失败：{ex.Message}");
        }

        return list;
    }

    private AudioDeviceInfo BuildDeviceInfo(MMDevice dev, string deviceId, bool isDefault)
    {
        string fullName = "";
        try
        {
            fullName = dev.FriendlyName ?? "";
        }
        catch
        {
            // 蓝牙端点刚激活但 FriendlyName 属性尚未写入完成，先用兜底名并安排复核
            ScheduleDelayedDeviceRecheck(800);
        }

        bool isHeadphone = false;
        bool isDisplayAudio = false;
        bool isBluetooth = false;

        try
        {
            var props = dev.Properties;
            if (string.IsNullOrWhiteSpace(fullName) && props.Contains(PkeyDeviceInterfaceFriendlyName))
            {
                fullName = props[PkeyDeviceInterfaceFriendlyName].Value?.ToString() ?? "";
            }

            if (props.Contains(PkeyEndpointFormFactor))
            {
                var raw = props[PkeyEndpointFormFactor].Value;
                if (raw is uint formFactor)
                {
                    if (formFactor == 3 || formFactor == 5)
                    {
                        isHeadphone = true;
                    }
                    else if (formFactor == 8 || formFactor == 9)
                    {
                        // 8 = SPDIF, 9 = DigitalAudioDisplayDevice (HDMI / DisplayPort)
                        isDisplayAudio = true;
                    }
                }
            }

            if (props.Contains(PkeyDeviceEnumeratorName))
            {
                var enumeratorName = props[PkeyDeviceEnumeratorName].Value?.ToString() ?? "";
                if (enumeratorName.Contains("BTH", StringComparison.OrdinalIgnoreCase) ||
                    enumeratorName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
                {
                    isBluetooth = true;
                }
            }

            if (!isBluetooth && props.Contains(PkeyDeviceInterfaceFriendlyName))
            {
                var iface = props[PkeyDeviceInterfaceFriendlyName].Value?.ToString() ?? "";
                if (iface.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                    iface.Contains("蓝牙", StringComparison.OrdinalIgnoreCase))
                {
                    isBluetooth = true;
                }
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(fullName))
        {
            fullName = isBluetooth ? "蓝牙音频设备" : "音频输出设备";
            ScheduleDelayedDeviceRecheck(800);
        }

        string shortName = fullName;
        int paren = fullName.IndexOf('(');
        if (paren > 1)
        {
            shortName = fullName[..paren].Trim();
        }

        if (!isHeadphone && (
            fullName.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("Headset", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("Earbud", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("Buds", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("FreeBuds", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("耳机", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("耳麦", StringComparison.OrdinalIgnoreCase)))
        {
            isHeadphone = true;
        }

        if (!isBluetooth && (
            fullName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
            (fullName.Contains("Stereo", StringComparison.OrdinalIgnoreCase) && isHeadphone) ||
            fullName.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("FreeBuds", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("蓝牙", StringComparison.OrdinalIgnoreCase)))
        {
            isBluetooth = true;
        }

        if (!isHeadphone && !isDisplayAudio && (
            fullName.Contains("HDMI", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("DisplayPort", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("NVIDIA High Definition Audio", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("AMD High Definition Audio", StringComparison.OrdinalIgnoreCase) ||
            fullName.Contains("显示器", StringComparison.OrdinalIgnoreCase)))
        {
            isDisplayAudio = true;
        }

        var kind = isHeadphone
            ? AudioOutputKind.Headphone
            : (isDisplayAudio ? AudioOutputKind.DisplayAudio : AudioOutputKind.Speaker);

        return new AudioDeviceInfo(
            Id: deviceId,
            Name: fullName,
            ShortName: shortName,
            Kind: kind,
            IsBluetooth: isBluetooth,
            IsDefault: isDefault);
    }

    private void OnDeviceStateChangedInternal(string deviceId, DeviceState newState)
    {
        if (_disposed) return;
        _deviceStateEvents.Enqueue((deviceId, newState));
        RequestCoalescedRefresh(RefreshFlags.All | RefreshFlags.CheckNewlyActive, debounceMs: 80);
        // 蓝牙 A2DP/HFP 握手常在状态切换后 300ms~1000ms 才补齐端点与角色，安排一次延迟复核
        ScheduleDelayedDeviceRecheck(1200);
    }

    private void OnDefaultDeviceChangedInternal()
    {
        if (_disposed) return;
        _resetMeterFlag = true;
        RequestCoalescedRefresh(RefreshFlags.All, debounceMs: 70);
        ScheduleDelayedDeviceRecheck(1000);
    }

    private void OnDevicePropertyChangedInternal(PropertyKey key)
    {
        if (_disposed) return;
        if (key.formatId == PkeyDeviceFriendlyName.formatId ||
            key.formatId == PkeyDeviceInterfaceFriendlyName.formatId ||
            key.formatId == PkeyEndpointFormFactor.formatId)
        {
            RequestCoalescedRefresh(RefreshFlags.Devices, debounceMs: 220);
        }
    }

    private static string FriendlyProcessName(string procName) => procName.ToLowerInvariant() switch
    {
        "cloudmusic" => "网易云音乐",
        "qqmusic" => "QQ音乐",
        "kugou" => "酷狗音乐",
        "kwmusic" => "酷我音乐",
        "spotify" => "Spotify",
        "msedge" => "Edge",
        "chrome" => "Chrome",
        "firefox" => "Firefox",
        "potplayermini64" or "potplayermini" => "PotPlayer",
        "bilibili" => "哔哩哔哩",
        "vlc" => "VLC",
        _ => procName,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _controlSignal.Set(); } catch { }
        try { _refreshSignal.Set(); } catch { }
    }

    private sealed class DeviceNotificationClient(AudioDeviceService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
            => owner.OnDeviceStateChangedInternal(deviceId, newState);

        public void OnDeviceAdded(string pwstrDeviceId)
            => owner.OnDeviceStateChangedInternal(pwstrDeviceId, DeviceState.Active);

        public void OnDeviceRemoved(string deviceId)
            => owner.OnDeviceStateChangedInternal(deviceId, DeviceState.NotPresent);

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render)
            {
                owner.OnDefaultDeviceChangedInternal();
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
            => owner.OnDevicePropertyChangedInternal(key);
    }
}
