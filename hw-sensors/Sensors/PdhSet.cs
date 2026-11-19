using System.Runtime.InteropServices;

namespace HwSensors.Sensors;

/// <summary>
/// 免管理员的两类 PDH 计数器：
///   1. <c>\Energy Meter(*)\Power</c> —— Intel/AMD 的 RAPL 能耗计量器，单位毫瓦，
///      <c>RAPL_Package0_PKG</c> 就是 CPU 封装功耗（本机实测空闲 17–28W，满载 47W）。
///   2. <c>\Thermal Zone Information(*)\High Precision Temperature</c> —— ACPI 热区温度，
///      单位十分之一开尔文（本机实测 65°C 上下，随负载升到 92°C）。
///
/// 这两条都不需要内核驱动、不需要管理员，是插件在 Medium 完整性下唯一能拿到的
/// CPU 功耗 / 温度来源（LibreHardwareMonitor 的 SMU 通道要走 PawnIO 驱动，普通权限打不开）。
/// </summary>
internal sealed class PdhSet : IDisposable
{
    private const uint FmtDouble = 0x00000200;
    private const uint MoreData = 0x800007D2;
    private const uint StatusValidData = 0x0;
    private const uint StatusNewData = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct FmtItemDouble
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private IntPtr _query;
    private IntPtr _raplCounter;
    private IntPtr _thermalCounter;

    public bool IsOpen => _query != IntPtr.Zero;

    public void Open()
    {
        if (_query != IntPtr.Zero) return;

        if (PdhOpenQuery(null, IntPtr.Zero, out var query) != 0) return;
        _query = query;

        PdhAddEnglishCounter(_query, @"\Energy Meter(*)\Power", IntPtr.Zero, out _raplCounter);
        PdhAddEnglishCounter(_query, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out _thermalCounter);

        // 速率型计数器要两次采样才有值：先收一次做基准
        PdhCollectQueryData(_query);
    }

    /// <summary>每个采样周期调一次（两个计数器共用同一个 query，一次收集一起更新）。</summary>
    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    /// <summary>CPU 封装功耗（W）。多路平台会把所有 Package 相加；拿不到返回 null。</summary>
    public double? PackagePowerW()
    {
        var sum = 0.0;
        var any = false;

        foreach (var (name, value) in ReadArray(_raplCounter))
        {
            if (!name.Contains("PKG", StringComparison.OrdinalIgnoreCase)) continue;
            if (value <= 0) continue;

            sum += value;
            any = true;
        }

        return any ? sum / 1000.0 : null;   // 毫瓦 → 瓦
    }

    /// <summary>ACPI 热区温度（°C），取所有热区里最高的一个；拿不到返回 null。</summary>
    public double? ThermalZoneC()
    {
        double? best = null;

        foreach (var (_, value) in ReadArray(_thermalCounter))
        {
            if (value <= 0) continue;

            var celsius = value / 10.0 - 273.15;   // 十分之一开尔文 → °C
            if (celsius is < -20 or > 150) continue;

            if (best is null || celsius > best) best = celsius;
        }

        return best;
    }

    private static List<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero) return result;

        uint size = 0;
        uint count = 0;

        var rc = PdhGetFormattedCounterArray(counter, FmtDouble, ref size, ref count, IntPtr.Zero);
        if (rc != MoreData || size == 0) return result;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = PdhGetFormattedCounterArray(counter, FmtDouble, ref size, ref count, buffer);
            if (rc != 0) return result;

            var itemSize = Marshal.SizeOf<FmtItemDouble>();
            for (uint i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<FmtItemDouble>(buffer + (int)(i * itemSize));
                if (item.Status is not (StatusValidData or StatusNewData)) continue;

                var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                result.Add((name, item.Value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;

        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _raplCounter = IntPtr.Zero;
        _thermalCounter = IntPtr.Zero;
    }
}
