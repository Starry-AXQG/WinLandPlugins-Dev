using LibreHardwareMonitor.Hardware;

namespace HwSensors.Sensors;

/// <summary>用户设置项快照。</summary>
public sealed record SensorOptions
{
    public bool ShowCpu { get; init; } = true;
    public bool ShowGpu { get; init; } = true;
    public bool ShowTotal { get; init; } = true;
    public bool ShowBattery { get; init; } = true;
    public bool ShowBatteryHealth { get; init; } = true;

    /// <summary>选中的 GPU 标识（LibreHardwareMonitor 的 Identifier 字符串）；空 = 自动挑独显。</summary>
    public string GpuId { get; init; } = string.Empty;

    /// <summary>选中的网卡 Id；空 = 自动汇总物理网卡。</summary>
    public string NicId { get; init; } = string.Empty;

    /// <summary>整机预估功耗里除 CPU/GPU 之外的固定开销（屏幕、内存、硬盘、风扇…），单位 W。</summary>
    public double BaselineW { get; init; } = 8;

    /// <summary>胶囊形态要显示的子项键列表（默认网卡上传+下载）。</summary>
    public IReadOnlyList<string> CapsuleItems { get; init; } = DefaultCapsuleItems;

    /// <summary>为每个显示实例预分配固定宽度网格容器（消除数字位数变化导致的左右抖动）。</summary>
    public bool CapsuleFixedGrid { get; init; } = true;

    /// <summary>胶囊形态统一字号（默认 13）。</summary>
    public double CapsuleFontSize { get; init; } = 13.0;

    /// <summary>胶囊形态允许双行显示（单行 2 格 / 双行 4 格）。</summary>
    public bool CapsuleTwoRows { get; init; } = false;

    /// <summary>胶囊左边距（px）。</summary>
    public double CapsulePaddingLeft { get; init; } = 10.0;

    /// <summary>胶囊右边距（px）。</summary>
    public double CapsulePaddingRight { get; init; } = 10.0;

    /// <summary>隐藏胶囊中的硬件类型描述文本（如 CPU / GPU / 内存，保留网速 ↑↓ 方向箭头）。</summary>
    public bool CapsuleHideLabels { get; init; } = false;

    public static readonly IReadOnlyList<string> DefaultCapsuleItems = new[] { "net.up", "net.down" };
}

/// <summary>单张显卡的完整采样数据（用于多 GPU 聚光卡展示与主卡选取）。</summary>
public sealed class GpuSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public HardwareType Type { get; set; }
    public bool IsSelected { get; set; }

    public double? TempC { get; set; }
    public double? HotSpotC { get; set; }
    public double? PowerW { get; set; }

    /// <summary>与任务管理器一致的 GPU 占用率（优先取所有 D3D 引擎占用最大值，无则回退 GPU Core）。</summary>
    public double? LoadPct { get; set; }

    /// <summary>硬件原始 GPU Core 占用率（供聚光卡对照参考）。</summary>
    public double? CoreLoadPct { get; set; }

    /// <summary>显存占用率（%）。</summary>
    public double? VramLoadPct { get; set; }
    public double? VramUsedMb { get; set; }
    public double? VramTotalMb { get; set; }
    public double? VramTempC { get; set; }
    public double? VramPowerW { get; set; }
}

/// <summary>一次采样的完整结果。所有数值都可能为 null —— 表示当前机器/权限下不可读。</summary>
public sealed class SensorSnapshot
{
    public bool HasData { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    // ---- CPU ----
    public string? CpuName { get; set; }
    public double? CpuTempC { get; set; }
    public double? CpuPowerW { get; set; }
    public double? CpuLoadPct { get; set; }
    public bool CpuTempIsApprox { get; set; }

    // ---- 内存 ----
    public double? MemLoadPct { get; set; }
    public double? MemUsedGb { get; set; }
    public double? MemTotalGb { get; set; }
    public double? MemPowerW { get; set; }
    public double? MemTempC { get; set; }

    // ---- 主 GPU（卡片形态展示设置中选中的那一张） ----
    public string? GpuName { get; set; }
    public double? GpuTempC { get; set; }
    public double? GpuHotSpotC { get; set; }
    public double? GpuPowerW { get; set; }
    public double? GpuLoadPct { get; set; }
    public double? GpuCoreLoadPct { get; set; }

    // ---- 主 GPU 显存 ----
    public double? VramLoadPct { get; set; }
    public double? VramUsedMb { get; set; }
    public double? VramTotalMb { get; set; }
    public double? VramTempC { get; set; }
    public double? VramPowerW { get; set; }

    // ---- 全部 GPU（展开聚光卡展示多卡信息） ----
    public IReadOnlyList<GpuSnapshot> Gpus { get; set; } = Array.Empty<GpuSnapshot>();

    // ---- 网卡 ----
    public double? NetUpBytesPerSec { get; set; }
    public double? NetDownBytesPerSec { get; set; }
    public string? NetAdapterName { get; set; }

    // ---- 整机 ----
    public double? TotalPowerW { get; set; }
    public bool TotalIsMeasured { get; set; }

    // ---- 电池 ----
    public bool HasBattery { get; set; }
    public bool IsOnAc { get; set; }
    public bool IsCharging { get; set; }
    public double? BatteryPercent { get; set; }
    public double? BatteryDegradationPct { get; set; }
    public double? BatteryChargeW { get; set; }
    public double? BatteryDischargeW { get; set; }
    public double? BatteryMinutesToFull { get; set; }
    public double? BatteryVoltageV { get; set; }
    public double? BatteryDesignWh { get; set; }
    public double? BatteryFullWh { get; set; }
}

/// <summary>设置页下拉菜单里的一个 GPU 选项。</summary>
public sealed record GpuChoice(string Id, string Name, HardwareType Type);

/// <summary>设置页下拉菜单里的一个网卡选项。</summary>
public sealed record NicChoice(string Id, string Name, string Description);
