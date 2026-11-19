namespace HwSensors.Sensors;

/// <summary>用户想监控哪些东西（从设置里读出来的那一份）。</summary>
public sealed record SensorOptions
{
    public bool ShowCpu { get; init; } = true;
    public bool ShowGpu { get; init; } = true;
    public bool ShowTotal { get; init; } = true;
    public bool ShowBattery { get; init; } = true;

    /// <summary>显示电池损耗程度（台式机默认关掉，因为没有电池）。</summary>
    public bool ShowBatteryHealth { get; init; } = true;

    /// <summary>选中的 GPU 标识（LibreHardwareMonitor 的 Identifier 字符串）；空 = 自动挑独显。</summary>
    public string GpuId { get; init; } = string.Empty;

    /// <summary>整机预估功耗里除 CPU/GPU 之外的固定开销（屏幕、内存、硬盘、风扇…），单位 W。</summary>
    public double BaselineW { get; init; } = 8;
}

/// <summary>一次采样的结果。所有数值都可能为 null —— 表示「这台机器/这个权限下读不到」。</summary>
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

    // ---- GPU ----
    public string? GpuName { get; set; }
    public double? GpuTempC { get; set; }
    public double? GpuHotSpotC { get; set; }
    public double? GpuPowerW { get; set; }
    public double? GpuLoadPct { get; set; }

    // ---- 整机 ----
    public double? TotalPowerW { get; set; }

    /// <summary>整机功耗是电池实测（true）还是按 CPU+GPU+基线估算（false）。</summary>
    public bool TotalIsMeasured { get; set; }

    // ---- 电池 ----
    public bool HasBattery { get; set; }
    public bool IsOnAc { get; set; }
    public bool IsCharging { get; set; }
    public double? BatteryPercent { get; set; }
    public double? BatteryDegradationPct { get; set; }
    public double? BatteryChargeW { get; set; }

    /// <summary>放电时的电池输出功率（W）。整机功耗在放电时直接用它，是实测值。</summary>
    public double? BatteryDischargeW { get; set; }

    public double? BatteryMinutesToFull { get; set; }
    public double? BatteryVoltageV { get; set; }
    public double? BatteryDesignWh { get; set; }
    public double? BatteryFullWh { get; set; }

    /// <summary>CPU 温度是从 ACPI 热区读的（整机热区，不是精确 Tctl）时为 true，界面上要标注。</summary>
    public bool CpuTempIsApprox { get; set; }
}

