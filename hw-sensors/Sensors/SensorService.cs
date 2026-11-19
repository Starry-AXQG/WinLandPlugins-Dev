using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using WinIsland.Core;

namespace HwSensors.Sensors;

/// <summary>
/// 后台采样器：一条独立线程，按固定间隔采一次，把结果做成 <see cref="SensorSnapshot"/> 丢给 UI。
///
/// 数据来源分工（都是免管理员能拿到的）：
///   • CPU 名字 / 占用率 —— LibreHardwareMonitor（WMI 性能计数器，普通权限可读）
///   • CPU 温度        —— PDH「Thermal Zone Information」ACPI 热区（LHM 的 Tctl 要 PawnIO 驱动）
///   • CPU 功耗        —— PDH「Energy Meter」RAPL（LHM 的 SMU 通道同样要驱动）
///   • GPU 温度/功耗/占用 —— LibreHardwareMonitor（NVAPI / ADL，普通权限可读）
///   • 电池容量/损耗/充放功率 —— LibreHardwareMonitor 的 Battery 组
///   • 交流/充电状态   —— kernel32 GetSystemPowerStatus（不依赖 WMI，最稳）
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

    private Computer? _computer;
    private PdhSet? _pdh;
    private bool _openFailed;

    /// <summary>充放电趋势判定用的历史（判断「到底在充还是在放」不依赖驱动给的正负号）。</summary>
    private readonly Queue<double> _chargeHistory = new();

    public SensorService(IPluginLogger log) => _log = log;

    /// <summary>采样结果（后台线程触发）。</summary>
    public event Action<SensorSnapshot>? SnapshotReady;

    /// <summary>发现到的 GPU 列表（首次采样后才有内容）。</summary>
    public event Action<IReadOnlyList<GpuChoice>>? GpusDiscovered;

    public SensorSnapshot Latest => _latest;

    public IReadOnlyList<GpuChoice> Gpus => _gpus;

    public int IntervalMs { get; set; } = 2000;

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

    /// <summary>让采样线程立刻采一次（改设置、点刷新时用）。</summary>
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
                IsBatteryEnabled = true,
                IsMotherboardEnabled = false,
                IsMemoryEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsPsuEnabled = false,
            };

            computer.Open();
            _computer = computer;

            // 打开后立刻枚举一次 GPU，设置页的下拉菜单要用
            RefreshGpuList();

            _log.Info($"传感器已就绪（PDH {( _pdh.IsOpen ? "可用" : "不可用")}，GPU {_gpus.Count} 个）");
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

        // 独显排前面，设置页里默认就是它
        list.Sort((a, b) => Rank(a.Type).CompareTo(Rank(b.Type)));

        if (list.Count == 0 || list.Select(x => x.Id).SequenceEqual(_gpus.Select(x => x.Id))) return;

        _gpus = list;
        GpusDiscovered?.Invoke(list);
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

        // PDH 的速率型计数器要「两次收集」才有值：本轮先收，再用上一轮到这一轮的差值
        _pdh?.Collect();

        var power = ReadSystemPower();
        snapshot.IsOnAc = power.OnAc;
        snapshot.IsCharging = power.Charging;

        IHardware? gpu = null;
        IHardware? battery = null;
        var gpuCandidates = new List<IHardware>();

        if (_computer is not null)
        {
            foreach (var hw in _computer.Hardware)
            {
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        if (options.ShowCpu) ReadCpu(hw, snapshot);
                        break;

                    case HardwareType.Battery:
                        battery ??= hw;
                        break;

                    default:
                        // 遍历顺序不保证，先把所有显卡收起来，最后按「指定的 > 独显优先」挑一张
                        if (IsGpu(hw.HardwareType)) gpuCandidates.Add(hw);
                        break;
                }
            }

            gpu = PickGpu(gpuCandidates, options);

            if (options.ShowGpu && gpu is not null) ReadGpu(gpu, snapshot);

            if (battery is not null) ReadBattery(battery, snapshot);
        }

        // ---- CPU 温度/功耗：PDH ----
        if (options.ShowCpu && _pdh is not null)
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

        // ---- 整机功耗 ----
        if (options.ShowTotal)
        {
            snapshot.TotalPowerW = EstimateTotal(snapshot, options);
            snapshot.TotalIsMeasured = snapshot.IsOnAc ? false : snapshot.BatteryDischargeW is > 0.5;
        }

        snapshot.HasData = snapshot.CpuTempC is not null || snapshot.CpuPowerW is not null
                           || snapshot.GpuTempC is not null || snapshot.GpuPowerW is not null
                           || snapshot.BatteryPercent is not null;

        snapshot.Timestamp = DateTime.Now;
        return snapshot;
    }

    /// <summary>
    /// 整机功耗：优先用电池实测（放电时电池输出的功率就是整机的真实功耗），
    /// 拿不到就按 CPU + GPU + 固定基线估算。
    /// </summary>
    private static double? EstimateTotal(SensorSnapshot s, SensorOptions options)
    {
        if (s.BatteryDischargeW is > 1.0) return s.BatteryDischargeW;

        var sum = 0.0;
        var any = false;

        if (s.CpuPowerW is > 0) { sum += s.CpuPowerW.Value; any = true; }
        if (s.GpuPowerW is > 0) { sum += s.GpuPowerW.Value; any = true; }

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
                // 「CPU Total」是整颗 CPU 的占用率；分核心的项不要
                case SensorType.Load when sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase):
                    s.CpuLoadPct = value;
                    break;

                // LHM 的 Tctl/Tdie 在非管理员下恒为 0（要内核驱动），只在真的读到值时兜底用
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

    private static void ReadGpu(IHardware gpu, SensorSnapshot s)
    {
        gpu.Update();

        s.GpuName = gpu.Name;

        foreach (var sensor in gpu.Sensors)
        {
            if (sensor.Value is not { } value) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                        s.GpuTempC = value;
                    else if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                        s.GpuHotSpotC = value;
                    break;

                case SensorType.Power:
                    // NVIDIA 叫「GPU Package」，AMD 核显叫「GPU Core」
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        s.GpuPowerW = value;
                    }
                    break;

                case SensorType.Load when sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase):
                    s.GpuLoadPct = value;
                    break;
            }
        }
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

        // 容量单位是 mWh
        if (designed is > 0) s.BatteryDesignWh = designed / 1000.0;
        if (full is > 0) s.BatteryFullWh = full / 1000.0;

        // 库没给损耗率时自己算：(设计 - 满充) / 设计
        if (s.BatteryDegradationPct is null && designed is > 0 && full is > 0)
            s.BatteryDegradationPct = Math.Clamp((designed.Value - full.Value) / designed.Value * 100.0, 0, 100);

        // 充电功率：库的正负号在不同机型上不一致，用「电量在涨还是在掉」判断方向
        var rising = IsChargeLevelRising(s.BatteryPercent);

        if (rate is > 0.1)
        {
            if (rising == true)
            {
                // 电量在涨 = 一定在充电
                s.BatteryChargeW = rate;
            }
            else if (rising == false)
            {
                // 电量在掉：插着电也说明没充进去（比如限充到 80% 后掉电），算放电
                s.BatteryDischargeW = rate;
            }
            else if (s.IsCharging)
            {
                // 趋势还看不出来，但系统明确说在充电
                s.BatteryChargeW = rate;
            }
            else if (!s.IsOnAc)
            {
                // 没插电就是放电
                s.BatteryDischargeW = rate;
            }
            // 插着电、系统没说在充、电量也没涨：多半是已经充满在涓流，不显示功率
        }

        // 预计充满：(满充容量 - 剩余容量) / 充电功率
        if (s.BatteryChargeW is > 0.5 && full is > 0 && remaining is > 0 && full > remaining)
        {
            var hours = (full.Value - remaining.Value) / 1000.0 / s.BatteryChargeW.Value;
            if (hours is > 0 and < 24) s.BatteryMinutesToFull = hours * 60.0;
        }
    }

    /// <summary>用最近几次采样的电量趋势判断在充还是在放（返回 null = 还看不出来）。</summary>
    private bool? IsChargeLevelRising(double? percent)
    {
        if (percent is null) return null;

        lock (_stateGate)
        {
            _chargeHistory.Enqueue(percent.Value);
            while (_chargeHistory.Count > 5) _chargeHistory.Dequeue();

            if (_chargeHistory.Count < 3) return null;

            var first = _chargeHistory.First();
            var last = _chargeHistory.Last();
            var delta = last - first;

            if (delta > 0.4) return true;
            if (delta < -0.4) return false;
            return null;
        }
    }

    private static bool MatchesGpu(IHardware hw, SensorOptions options)
    {
        if (string.IsNullOrEmpty(options.GpuId)) return true;   // 空 = 自动，取列表第一个（独显优先）
        return string.Equals(hw.Identifier.ToString(), options.GpuId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>挑要读的那张卡：设置里指定了就听设置的，否则独显优先。</summary>
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

        // 自动：NVIDIA 独显 > AMD > Intel，同类型取列表第一个
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
        var charging = (status.BatteryFlag & 0x08) != 0;   // bit3 = 正在充电
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

/// <summary>设置页下拉菜单里的一个 GPU 选项。</summary>
public sealed record GpuChoice(string Id, string Name, HardwareType Type);

