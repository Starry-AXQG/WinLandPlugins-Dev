using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using WinIsland.Core;

namespace HwSensors.Sensors;

/// <summary>
/// 后台采样器：独立线程按固定间隔采一次，把结果做成 <see cref="SensorSnapshot"/> 推给 UI。
///
/// 数据来源分工（全部免管理员）：
///   • CPU 名字 / 占用率        —— LibreHardwareMonitor（WMI 性能计数器）
///   • CPU 温度 / 功耗          —— PDH「Thermal Zone Information」与「Energy Meter」RAPL
///   • 内存 占用率 / 已用总量   —— LibreHardwareMonitor（Total Memory /ram）
///   • 多 GPU 温度/功耗/占用/显存 —— LibreHardwareMonitor（NVAPI / ADL / WDDM D3D 引擎计数）
///     ※ GPU 占用率优先取所有 D3D 引擎负载的最大值（与 Windows 任务管理器算法一致），无 D3D 时回退 GPU Core
///   • 网卡上传 / 下载速率      —— .NET NetworkInterface 字节增量（过滤虚拟与重复过滤驱动）
///   • 电池与交流状态           —— LibreHardwareMonitor Battery + kernel32 GetSystemPowerStatus
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly IPluginLogger _log;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _stateGate = new();

    private Thread? _thread;
    private volatile bool _stopping;
    private volatile SensorOptions _options = new();
    private volatile SensorSnapshot _latest = new();
    private IReadOnlyList<GpuChoice> _gpus = Array.Empty<GpuChoice>();
    private IReadOnlyList<NicChoice> _nics = Array.Empty<NicChoice>();

    private Computer? _computer;
    private PdhSet? _pdh;
    private bool _openFailed;

    /// <summary>充放电趋势判定用的历史。</summary>
    private readonly Queue<double> _chargeHistory = new();

    /// <summary>网卡速率计算用的上一轮字节计数。</summary>
    private long _lastNetTimestamp;
    private long _lastNetSent;
    private long _lastNetRecv;
    private string _lastNetSelection = string.Empty;

    public SensorService(IPluginLogger log) => _log = log;

    public event Action<SensorSnapshot>? SnapshotReady;
    public event Action<IReadOnlyList<GpuChoice>>? GpusDiscovered;

    public SensorSnapshot Latest => _latest;
    public IReadOnlyList<GpuChoice> Gpus => _gpus;
    public IReadOnlyList<NicChoice> Nics => _nics;

    public int IntervalMs { get; set; } = 1000;

    public void SetOptions(SensorOptions options) => _options = options;

    public void Start()
    {
        if (_thread is not null) return;

        _stopping = false;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "hw-sensors-sampler",
        };
        _thread.Start();
    }

    public void RequestRefresh() => _wake.Set();

    private void Loop()
    {
        Open();

        while (!_stopping)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var snapshot = Sample();
                _latest = snapshot;
                SnapshotReady?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _log.Warn($"采样失败（已跳过这一轮）：{ex.Message}");
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            var wait = (int)Math.Clamp(IntervalMs - elapsedMs, 250, 60_000);
            _wake.WaitOne(wait);
        }

        Close();
    }

    private void Open()
    {
        try
        {
            _pdh = new PdhSet();
            _pdh.Open();

            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsBatteryEnabled = true,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false, // 网卡使用 .NET NetworkInterface，避免 LHM 重复计算 WFP/QoS 过滤驱动
                IsControllerEnabled = false,
                IsPsuEnabled = false,
            };

            computer.Open();
            _computer = computer;

            RefreshGpuList();
            RefreshNicList();

            _log.Info($"传感器已就绪（PDH {(_pdh.IsOpen ? "可用" : "不可用")}，GPU {_gpus.Count} 个，网卡 {_nics.Count} 个）");
        }
        catch (Exception ex)
        {
            _openFailed = true;
            _log.Error("传感器初始化失败", ex);
        }
    }

    private void Close()
    {
        try { _computer?.Close(); } catch { /* 退出阶段忽略 */ }
        _computer = null;

        try { _pdh?.Dispose(); } catch { /* 退出阶段忽略 */ }
        _pdh = null;
    }

    private void RefreshGpuList()
    {
        if (_computer is null) return;

        var list = new List<GpuChoice>();
        foreach (var hw in _computer.Hardware)
        {
            if (!IsGpu(hw.HardwareType)) continue;
            list.Add(new GpuChoice(hw.Identifier.ToString(), hw.Name, hw.HardwareType));
        }

        list.Sort((a, b) => Rank(a.Type).CompareTo(Rank(b.Type)));

        if (list.Count == 0 || list.Select(x => x.Id).SequenceEqual(_gpus.Select(x => x.Id))) return;

        _gpus = list;
        GpusDiscovered?.Invoke(list);
    }

    private void RefreshNicList()
    {
        try
        {
            var list = new List<NicChoice>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                list.Add(new NicChoice(nic.Id, nic.Name, nic.Description));
            }
            _nics = list;
        }
        catch
        {
            // 枚举网卡失败不影响其它传感器
        }
    }

    private SensorSnapshot Sample()
    {
        var snapshot = new SensorSnapshot();
        var options = _options;

        if (_openFailed)
        {
            snapshot.Error = "传感器初始化失败，详见插件日志";
            return snapshot;
        }

        _pdh?.Collect();

        var power = ReadSystemPower();
        snapshot.IsOnAc = power.OnAc;
        snapshot.IsCharging = power.Charging;

        IHardware? battery = null;
        IHardware? physicalMem = null;
        var gpuCandidates = new List<IHardware>();

        if (_computer is not null)
        {
            foreach (var hw in _computer.Hardware)
            {
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        ReadCpu(hw, snapshot);
                        break;

                    case HardwareType.Memory:
                        // 仅取物理内存（/ram 或 Total Memory），忽略虚拟内存（/vram）
                        if (hw.Identifier.ToString().Equals("/ram", StringComparison.OrdinalIgnoreCase)
                            || hw.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase))
                        {
                            physicalMem = hw;
                        }
                        break;

                    case HardwareType.Battery:
                        battery ??= hw;
                        break;

                    default:
                        if (IsGpu(hw.HardwareType)) gpuCandidates.Add(hw);
                        break;
                }
            }

            // 内存
            if (physicalMem is not null)
                ReadMemory(physicalMem, snapshot);

            // 所有 GPU 均采样（聚光卡展示多 GPU，卡片形态取选中主卡）
            gpuCandidates.Sort((a, b) => Rank(a.HardwareType).CompareTo(Rank(b.HardwareType)));
            var selectedHw = PickGpu(gpuCandidates, options);
            var gpuList = new List<GpuSnapshot>(gpuCandidates.Count);

            foreach (var hw in gpuCandidates)
            {
                var g = ReadGpu(hw);
                g.IsSelected = ReferenceEquals(hw, selectedHw);
                gpuList.Add(g);

                if (g.IsSelected)
                {
                    snapshot.GpuName = g.Name;
                    snapshot.GpuTempC = g.TempC;
                    snapshot.GpuHotSpotC = g.HotSpotC;
                    snapshot.GpuPowerW = g.PowerW;
                    snapshot.GpuLoadPct = g.LoadPct;
                    snapshot.GpuCoreLoadPct = g.CoreLoadPct;

                    snapshot.VramLoadPct = g.VramLoadPct;
                    snapshot.VramUsedMb = g.VramUsedMb;
                    snapshot.VramTotalMb = g.VramTotalMb;
                    snapshot.VramTempC = g.VramTempC;
                    snapshot.VramPowerW = g.VramPowerW;
                }
            }

            snapshot.Gpus = gpuList;

            // 电池
            if (battery is not null)
                ReadBattery(battery, snapshot);
        }

        // ---- CPU 温度 / 功耗：PDH ----
        if (_pdh is not null)
        {
            var pdhTemp = _pdh.ThermalZoneC();
            if (pdhTemp is not null)
            {
                snapshot.CpuTempC = pdhTemp;
                snapshot.CpuTempIsApprox = true;
            }

            var pdhPower = _pdh.PackagePowerW();
            if (pdhPower is not null) snapshot.CpuPowerW = pdhPower;
        }

        // ---- 网卡上传 / 下载速率 ----
        ReadNetwork(snapshot, options);

        // ---- 整机功耗 ----
        snapshot.TotalPowerW = EstimateTotal(snapshot, options);
        snapshot.TotalIsMeasured = !snapshot.IsOnAc && snapshot.BatteryDischargeW is > 0.5;

        snapshot.HasData = snapshot.CpuTempC is not null || snapshot.CpuPowerW is not null
                           || snapshot.CpuLoadPct is not null || snapshot.MemLoadPct is not null
                           || snapshot.GpuTempC is not null || snapshot.GpuLoadPct is not null
                           || snapshot.NetDownBytesPerSec is not null || snapshot.BatteryPercent is not null;

        snapshot.Timestamp = DateTime.Now;
        return snapshot;
    }

    private static double? EstimateTotal(SensorSnapshot s, SensorOptions options)
    {
        if (s.BatteryDischargeW is > 1.0) return s.BatteryDischargeW;

        var sum = 0.0;
        var any = false;

        if (s.CpuPowerW is > 0) { sum += s.CpuPowerW.Value; any = true; }
        // 汇总所有 GPU 的功耗（笔记本常见独显+核显同时耗电）
        if (s.Gpus.Count > 0)
        {
            foreach (var g in s.Gpus)
            {
                if (g.PowerW is > 0) { sum += g.PowerW.Value; any = true; }
            }
        }
        else if (s.GpuPowerW is > 0)
        {
            sum += s.GpuPowerW.Value;
            any = true;
        }

        if (!any) return null;

        return sum + Math.Max(0, options.BaselineW);
    }

    private static void ReadCpu(IHardware cpu, SensorSnapshot s)
    {
        cpu.Update();
        s.CpuName = cpu.Name;

        foreach (var sensor in cpu.Sensors)
        {
            if (sensor.Value is not { } value) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Load when sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase):
                    s.CpuLoadPct = value;
                    break;

                case SensorType.Temperature when value > 0 && s.CpuTempC is null &&
                                                 sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase):
                    s.CpuTempC = value;
                    break;

                case SensorType.Power when value > 0 && s.CpuPowerW is null &&
                                           sensor.Name.Equals("Package", StringComparison.OrdinalIgnoreCase):
                    s.CpuPowerW = value;
                    break;
            }
        }
    }

    private static void ReadMemory(IHardware mem, SensorSnapshot s)
    {
        mem.Update();

        double? used = null, avail = null;

        foreach (var sensor in mem.Sensors)
        {
            if (sensor.Value is not { } value) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Load when sensor.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase):
                    s.MemLoadPct = value;
                    break;

                case SensorType.Data when sensor.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase):
                    used = value;
                    break;

                case SensorType.Data when sensor.Name.Equals("Memory Available", StringComparison.OrdinalIgnoreCase):
                    avail = value;
                    break;

                case SensorType.Power when value > 0 && s.MemPowerW is null:
                    s.MemPowerW = value;
                    break;

                case SensorType.Temperature when value > 0 && s.MemTempC is null:
                    s.MemTempC = value;
                    break;
            }
        }

        s.MemUsedGb = used;
        if (used is not null && avail is not null)
        {
            s.MemTotalGb = used.Value + avail.Value;
            if (s.MemLoadPct is null && s.MemTotalGb > 0)
                s.MemLoadPct = used.Value / s.MemTotalGb.Value * 100.0;
        }
    }

    /// <summary>
    /// 读取单张显卡的全部指标：
    /// 关键修复：Windows 任务管理器的 GPU 占用率取所有 WDDM D3D 引擎（3D、Video Decode、Encode、Copy、Compute 等）
    /// 负载的最大值。LibreHardwareMonitor 通过 D3D * 传感器暴露这些引擎负载，优先取 max(D3D *) 即可与任务管理器对齐；
    /// 仅当硬件无 D3D 传感器时才回退至 GPU Core。
    /// </summary>
    private static GpuSnapshot ReadGpu(IHardware gpu)
    {
        gpu.Update();

        var g = new GpuSnapshot
        {
            Id = gpu.Identifier.ToString(),
            Name = gpu.Name,
            Type = gpu.HardwareType,
        };

        double? d3dMaxLoad = null;
        double? fallbackTemp = null;
        double? d3dDedUsed = null;
        double? d3dDedTotal = null;

        foreach (var sensor in gpu.Sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value)) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                        g.TempC = value;
                    else if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                        g.HotSpotC = value;
                    else if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                        g.VramTempC = value;
                    else if (fallbackTemp is null && value > 0)
                        fallbackTemp = value; // 例如 AMD 核显的 GPU VR SoC
                    break;

                case SensorType.Power:
                    if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                    {
                        g.VramPowerW = value;
                    }
                    else if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                             sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        g.PowerW = value;
                    }
                    break;

                case SensorType.Load:
                    if (sensor.Name.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase))
                    {
                        var clamped = Math.Clamp((double)value, 0.0, 100.0);
                        if (d3dMaxLoad is null || clamped > d3dMaxLoad.Value)
                            d3dMaxLoad = clamped;
                    }
                    else if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        g.CoreLoadPct = Math.Clamp((double)value, 0.0, 100.0);
                    }
                    else if (sensor.Name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase))
                    {
                        g.VramLoadPct = Math.Clamp((double)value, 0.0, 100.0);
                    }
                    break;

                case SensorType.SmallData:
                    if (sensor.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase))
                        g.VramUsedMb = value;
                    else if (sensor.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                        g.VramTotalMb = value;
                    else if (sensor.Name.Equals("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase))
                        d3dDedUsed = value;
                    else if (sensor.Name.Equals("D3D Dedicated Memory Total", StringComparison.OrdinalIgnoreCase))
                        d3dDedTotal = value;
                    break;
            }
        }

        // 温度兜底（如 AMD 核显无 GPU Core 温度，只有 GPU VR SoC）
        g.TempC ??= fallbackTemp;

        // GPU 占用率：优先采用与任务管理器一致的 max(D3D *)，无 D3D 传感器时回退 GPU Core
        g.LoadPct = d3dMaxLoad ?? g.CoreLoadPct;

        // 显存容量兜底与占用率推算
        g.VramUsedMb ??= d3dDedUsed;
        g.VramTotalMb ??= d3dDedTotal;
        if (g.VramLoadPct is null && g.VramUsedMb is >= 0 && g.VramTotalMb is > 0)
        {
            g.VramLoadPct = Math.Clamp(g.VramUsedMb.Value / g.VramTotalMb.Value * 100.0, 0.0, 100.0);
        }

        return g;
    }

    private void ReadNetwork(SensorSnapshot s, SensorOptions options)
    {
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces();
            var selected = new List<NetworkInterface>();

            if (!string.IsNullOrEmpty(options.NicId))
            {
                foreach (var nic in all)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (string.Equals(nic.Id, options.NicId, StringComparison.OrdinalIgnoreCase))
                    {
                        selected.Add(nic);
                        break;
                    }
                }
            }

            if (selected.Count == 0)
            {
                // 自动模式：仅选取活动物理网卡，避免代理 TUN / VMware 虚拟网卡重复计费
                foreach (var nic in all)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (!IsPhysicalAdapter(nic)) continue;
                    selected.Add(nic);
                }

                // 若当前没有任何标准物理网卡（如纯虚拟化环境），退回全部非回环活动网卡
                if (selected.Count == 0)
                {
                    foreach (var nic in all)
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        selected.Add(nic);
                    }
                }
            }

            long totalSent = 0;
            long totalRecv = 0;
            var names = new List<string>(selected.Count);

            foreach (var nic in selected)
            {
                var stats = nic.GetIPStatistics();
                totalSent += stats.BytesSent;
                totalRecv += stats.BytesReceived;
                names.Add(nic.Name);
            }

            var selectionKey = string.Join("|", selected.Select(n => n.Id));
            var now = Stopwatch.GetTimestamp();

            s.NetAdapterName = names.Count > 0 ? string.Join(" + ", names) : "无活动网卡";

            if (_lastNetTimestamp != 0 && _lastNetSelection == selectionKey)
            {
                var dt = (now - _lastNetTimestamp) / (double)Stopwatch.Frequency;
                if (dt > 0.05)
                {
                    var dSent = totalSent - _lastNetSent;
                    var dRecv = totalRecv - _lastNetRecv;
                    s.NetUpBytesPerSec = dSent >= 0 ? dSent / dt : 0.0;
                    s.NetDownBytesPerSec = dRecv >= 0 ? dRecv / dt : 0.0;
                }
            }
            else
            {
                s.NetUpBytesPerSec = 0.0;
                s.NetDownBytesPerSec = 0.0;
            }

            _lastNetTimestamp = now;
            _lastNetSent = totalSent;
            _lastNetRecv = totalRecv;
            _lastNetSelection = selectionKey;
        }
        catch
        {
            // 单次网卡读取异常忽略
        }
    }

    private static bool IsPhysicalAdapter(NetworkInterface nic)
    {
        var type = nic.NetworkInterfaceType;
        if (type is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel
            or NetworkInterfaceType.Unknown)
        {
            return false;
        }

        // IF_TYPE_PROP_VIRTUAL (53) 常用于 Clash/Wintun/VPN 虚拟隧道
        if ((int)type == 53) return false;

        var desc = nic.Description ?? string.Empty;
        if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Pseudo", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void ReadBattery(IHardware battery, SensorSnapshot s)
    {
        battery.Update();
        s.HasBattery = true;

        double? designed = null, full = null, remaining = null, rate = null;

        foreach (var sensor in battery.Sensors)
        {
            if (sensor.Value is not { } value) continue;

            switch (sensor.Name)
            {
                case "Designed Capacity": designed = value; break;
                case "Fully-Charged Capacity": full = value; break;
                case "Remaining Capacity": remaining = value; break;
                case "Degradation Level": s.BatteryDegradationPct = value; break;
                case "Charge Level": s.BatteryPercent = value; break;
                case "Voltage": s.BatteryVoltageV = value; break;
                case "Charge/Discharge Rate": rate = Math.Abs(value); break;
            }
        }

        if (designed is > 0) s.BatteryDesignWh = designed / 1000.0;
        if (full is > 0) s.BatteryFullWh = full / 1000.0;

        if (s.BatteryDegradationPct is null && designed is > 0 && full is > 0)
            s.BatteryDegradationPct = Math.Clamp((designed.Value - full.Value) / designed.Value * 100.0, 0, 100);

        var rising = IsChargeLevelRising(s.BatteryPercent);

        if (rate is > 0.1)
        {
            if (rising == true) s.BatteryChargeW = rate;
            else if (rising == false) s.BatteryDischargeW = rate;
            else if (s.IsCharging) s.BatteryChargeW = rate;
            else if (!s.IsOnAc) s.BatteryDischargeW = rate;
        }

        if (s.BatteryChargeW is > 0.5 && full is > 0 && remaining is > 0 && full > remaining)
        {
            var hours = (full.Value - remaining.Value) / 1000.0 / s.BatteryChargeW.Value;
            if (hours is > 0 and < 24) s.BatteryMinutesToFull = hours * 60.0;
        }
    }

    private bool? IsChargeLevelRising(double? percent)
    {
        if (percent is null) return null;

        lock (_stateGate)
        {
            _chargeHistory.Enqueue(percent.Value);
            while (_chargeHistory.Count > 5) _chargeHistory.Dequeue();

            if (_chargeHistory.Count < 3) return null;

            var delta = _chargeHistory.Last() - _chargeHistory.First();
            if (delta > 0.4) return true;
            if (delta < -0.4) return false;
            return null;
        }
    }

    private static bool MatchesGpu(IHardware hw, SensorOptions options)
    {
        if (string.IsNullOrEmpty(options.GpuId)) return true;
        return string.Equals(hw.Identifier.ToString(), options.GpuId, StringComparison.OrdinalIgnoreCase);
    }

    private static IHardware? PickGpu(List<IHardware> candidates, SensorOptions options)
    {
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrEmpty(options.GpuId))
        {
            foreach (var hw in candidates)
            {
                if (MatchesGpu(hw, options)) return hw;
            }
        }

        IHardware? best = null;
        var bestRank = int.MaxValue;

        foreach (var hw in candidates)
        {
            var rank = Rank(hw.HardwareType);
            if (rank < bestRank) { best = hw; bestRank = rank; }
        }

        return best ?? candidates[0];
    }

    internal static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static int Rank(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => 0,
        HardwareType.GpuAmd => 1,
        HardwareType.GpuIntel => 2,
        _ => 9,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private static (bool OnAc, bool Charging) ReadSystemPower()
    {
        if (!GetSystemPowerStatus(out var status)) return (false, false);

        var onAc = status.AcLineStatus == 1;
        var charging = (status.BatteryFlag & 0x08) != 0;
        return (onAc, charging && onAc);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();

        try { _thread?.Join(2000); } catch { /* 退出阶段忽略 */ }
        _thread = null;

        _wake.Dispose();
    }
}
