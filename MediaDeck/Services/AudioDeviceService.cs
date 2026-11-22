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
/// 独立音频后台线程中枢（借鉴 EarTrumpet / SoundSwitch 架构）：
///   - 所有 Core Audio COM 对象（MMDeviceEnumerator / AudioSessionManager / IPolicyConfig / SpatialAudio）
///     完全隔离在独立的后台 MTA 线程中创建和调用，彻底杜绝 COM 跨套间封送回 WinUI 3 UI 线程导致的卡顿；
///   - UI 线程只读取 volatile 不可变快照与实时电平浮点值，耗时 &lt; 0.01ms，零锁、零阻塞。
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    private static readonly PropertyKey PkeyEndpointFormFactor =
        new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    private static readonly PropertyKey PkeyDeviceEnumeratorName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    private static readonly PropertyKey PkeyDeviceInterfaceFriendlyName =
        new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    private readonly Action<string> _log;
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly Thread _audioThread;
    private readonly Thread _meterThread;
    private readonly HashSet<string> _knownActiveDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]?> _mixerIconCache = new(StringComparer.OrdinalIgnoreCase);

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

        // 1. 专属 Core Audio 命令与枚举线程（MTA，不与 WinUI STA 线程纠缠）
        _audioThread = new Thread(AudioWorkerLoop)
        {
            Name = "MediaDeck.AudioWorker",
            IsBackground = true,
        };
        _audioThread.SetApartmentState(ApartmentState.MTA);
        _audioThread.Start();

        // 2. 轻量音频电平采样线程（每 33ms 采样一次，零 UI 阻塞）
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

    /// <summary>调整系统主音量（0..100），乐观更新 UI，后台异步写入 CoreAudio。</summary>
    public int SetMasterVolume(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        _masterVolumePercent = clamped;
        if (clamped > 0) _masterMuted = false;
        Interlocked.Exchange(ref _masterVolLockUntilTicks, DateTimeOffset.UtcNow.AddMilliseconds(600).UtcTicks);

        EnqueueWork(() =>
        {
            if (_enumerator is null) return;
            try
            {
                using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                def.AudioEndpointVolume.MasterVolumeLevelScalar = clamped / 100f;
                if (clamped > 0 && def.AudioEndpointVolume.Mute)
                {
                    def.AudioEndpointVolume.Mute = false;
                }
            }
            catch { }
        });
        return clamped;
    }

    /// <summary>按步进（如 ±2）调整系统主音量。</summary>
    public int StepMasterVolume(int deltaPercent)
        => SetMasterVolume(_masterVolumePercent + deltaPercent);

    /// <summary>向后台音频线程投递全量缓存刷新（立即返回，不阻塞调用方）。</summary>
    public void RequestRefreshAll()
    {
        if (_disposed) return;
        EnqueueWork(RefreshAllOnWorker);
    }

    /// <summary>向后台音频线程投递音量合成器刷新。</summary>
    public void RequestRefreshMixer()
    {
        if (_disposed) return;
        EnqueueWork(RefreshMixerOnWorker);
    }

    /// <summary>
    /// 异步切换默认音频输出设备：
    ///   - 立即乐观更新快照并设置 1.5s 目标设备锁（防止切换 3 个 Role 期间系统中间态通知把 UI 刷回原设备）；
    ///   - 独立高优先级线程直接调用 IPolicyConfig，不排在音量合成器枚举队列后面，消除切换延迟。
    /// </summary>
    public void SwitchOutputDeviceAsync(string deviceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(deviceId)) return;

        _lockedTargetDeviceId = deviceId;
        Interlocked.Exchange(ref _lockedTargetUntilTicks, DateTimeOffset.UtcNow.AddMilliseconds(1500).UtcTicks);

        // 乐观更新，UI 零等待
        _cachedDevices = _cachedDevices
            .Select(d => d with { IsDefault = string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase) })
            .ToList();
        Interlocked.Increment(ref _devicesVersion);
        DevicesChanged?.Invoke();

        _ = Task.Run(() =>
        {
            bool ok = PolicyConfigInterop.SetDefaultAudioEndpoint(deviceId);
            if (!ok)
            {
                // 失败则立刻解除锁并回刷真实设备状态
                _lockedTargetDeviceId = null;
                Interlocked.Exchange(ref _lockedTargetUntilTicks, 0);
            }
            _resetMeterFlag = true;
            EnqueueWork(() =>
            {
                RefreshAllOnWorker();
                DevicesChanged?.Invoke();
            });
        });
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

        EnqueueWork(() =>
        {
            if (_enumerator is null) return;
            try
            {
                using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = def.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    bool match = processId == 0 ? s.IsSystemSoundsSession : ((int)s.GetProcessID == processId && !s.IsSystemSoundsSession);
                    if (match)
                    {
                        s.SimpleAudioVolume.Volume = clamped;
                    }
                }
            }
            catch { }
        });
    }

    public void SetSessionMute(int processId, bool muted)
    {
        if (_disposed) return;

        _cachedMixer = _cachedMixer
            .Select(s => s.ProcessId == processId ? s with { IsMuted = muted, IsGeneratingSound = muted ? false : s.IsGeneratingSound } : s)
            .ToList();
        Interlocked.Increment(ref _mixerVersion);

        EnqueueWork(() =>
        {
            if (_enumerator is null) return;
            try
            {
                using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = def.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    bool match = processId == 0 ? s.IsSystemSoundsSession : ((int)s.GetProcessID == processId && !s.IsSystemSoundsSession);
                    if (match)
                    {
                        s.SimpleAudioVolume.Mute = muted;
                    }
                }
            }
            catch { }
        });
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

        EnqueueWork(() =>
        {
            try
            {
                var winrtDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                if (string.IsNullOrWhiteSpace(winrtDeviceId)) return;

                var config = SpatialAudioDeviceConfiguration.GetForDeviceId(winrtDeviceId);
                if (config is null) return;

                config.SetDefaultSpatialAudioFormatAsync(subtype ?? string.Empty).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log($"切换空间音效失败：{ex.Message}");
            }
            finally
            {
                RefreshSpatialOnWorker();
                CacheUpdated?.Invoke();
            }
        });
    }

    private void EnqueueWork(Action action)
    {
        if (_disposed) return;
        try { _workQueue.Add(action); } catch { }
    }

    private void AudioWorkerLoop()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _notifyClient = new DeviceNotificationClient(this);

            // 启动预热所有缓存
            RefreshAllOnWorker();
            foreach (var d in _cachedDevices)
            {
                _knownActiveDeviceIds.Add(d.Id);
            }

            try
            {
                _enumerator.RegisterEndpointNotificationCallback(_notifyClient);
            }
            catch (Exception ex)
            {
                _log($"注册音频设备热插拔监听失败：{ex.Message}");
            }

            DevicesChanged?.Invoke();

            foreach (var work in _workQueue.GetConsumingEnumerable())
            {
                if (_disposed) break;
                try { work(); }
                catch (Exception ex) { _log($"音频工作线程异常：{ex.Message}"); }
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

                    activeDevice ??= meterEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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

    private void RefreshAllOnWorker()
    {
        if (_disposed || _enumerator is null) return;
        RefreshDevicesOnWorker();
        RefreshMixerOnWorker();
        RefreshSpatialOnWorker();
        CacheUpdated?.Invoke();
    }

    private void RefreshDevicesOnWorker()
    {
        if (_disposed || _enumerator is null) return;
        var next = QueryOutputDevicesInternal(_enumerator);
        if (!DevicesEqual(_cachedDevices, next))
        {
            _cachedDevices = next;
            Interlocked.Increment(ref _devicesVersion);
        }
    }

    private void RefreshMixerOnWorker()
    {
        if (_disposed || _enumerator is null) return;
        var next = QueryVolumeMixerInternal(_enumerator);
        if (!MixersEqual(_cachedMixer, next))
        {
            _cachedMixer = next;
            Interlocked.Increment(ref _mixerVersion);
            CacheUpdated?.Invoke();
        }
    }

    private void RefreshSpatialOnWorker()
    {
        if (_disposed) return;
        var next = QuerySpatialSoundInternal();
        if (!SpatialsEqual(_cachedSpatial, next))
        {
            _cachedSpatial = next;
            Interlocked.Increment(ref _spatialVersion);
        }
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
                using (dev)
                {
                    list.Add(BuildDeviceInfo(dev, string.Equals(dev.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        catch (Exception ex)
        {
            _log($"枚举音频输出设备失败：{ex.Message}");
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
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        procName = proc.ProcessName;
                        // 过滤 Windows 音频设备图形隔离系统守护进程 audiodg
                        if (procName.Equals("audiodg", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        try { exePath = proc.MainModule?.FileName; } catch { }
                        if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("@", StringComparison.Ordinal))
                        {
                            displayName = FriendlyProcessName(procName);
                        }
                    }
                    catch
                    {
                        continue;
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
                            _mixerIconCache[procName] = iconBytes;
                        }
                        catch { }
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

    private List<SpatialSoundOption> QuerySpatialSoundInternal()
    {
        var list = new List<SpatialSoundOption>();
        try
        {
            var winrtDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (string.IsNullOrWhiteSpace(winrtDeviceId)) return list;

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

    private AudioDeviceInfo BuildDeviceInfo(MMDevice dev, bool isDefault)
    {
        string fullName = dev.FriendlyName;
        string shortName = fullName;
        int paren = fullName.IndexOf('(');
        if (paren > 1)
        {
            shortName = fullName[..paren].Trim();
        }

        bool isHeadphone = false;
        bool isDisplayAudio = false;
        bool isBluetooth = false;

        try
        {
            var props = dev.Properties;
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
            Id: dev.ID,
            Name: fullName,
            ShortName: shortName,
            Kind: kind,
            IsBluetooth: isBluetooth,
            IsDefault: isDefault);
    }

    private void OnDeviceStateChangedInternal(string deviceId, DeviceState newState)
    {
        if (_disposed) return;

        EnqueueWork(() =>
        {
            if (_disposed || _enumerator is null) return;

            if (newState == DeviceState.Active)
            {
                bool isNewlyActive = _knownActiveDeviceIds.Add(deviceId);
                if (isNewlyActive)
                {
                    try
                    {
                        using var dev = _enumerator.GetDevice(deviceId);
                        if (dev.DataFlow == DataFlow.Render)
                        {
                            var info = BuildDeviceInfo(dev, isDefault: false);
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
                _knownActiveDeviceIds.Remove(deviceId);
            }

            RefreshAllOnWorker();
            DevicesChanged?.Invoke();
        });
    }

    private void OnDefaultDeviceChangedInternal()
    {
        if (_disposed) return;
        _resetMeterFlag = true;
        EnqueueWork(() =>
        {
            RefreshAllOnWorker();
            DevicesChanged?.Invoke();
        });
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
        try { _workQueue.CompleteAdding(); } catch { }
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
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                owner.OnDefaultDeviceChangedInternal();
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
