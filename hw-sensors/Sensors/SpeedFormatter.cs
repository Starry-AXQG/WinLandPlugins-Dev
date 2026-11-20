namespace HwSensors.Sensors;

/// <summary>
/// 网卡速度格式化工具：
/// 优先保持 3 位有效数字（字符长度恒定），不足时补小数位，过长时减少小数位并升单位。
/// 例如：0.00 KB/s、0.12 KB/s、1.23 KB/s、12.1 KB/s、123 KB/s、1.23 MB/s、12.3 MB/s。
/// </summary>
public static class SpeedFormatter
{
    private const double KB = 1024.0;
    private const double MB = 1024.0 * 1024.0;
    private const double GB = 1024.0 * 1024.0 * 1024.0;

    /// <summary>
    /// 按固定 3 位数字长度规则格式化字节/秒。
    /// </summary>
    public static string Format(double? bytesPerSec, bool spaceBeforeUnit = true)
    {
        if (bytesPerSec is null || double.IsNaN(bytesPerSec.Value) || double.IsInfinity(bytesPerSec.Value))
            return "——";

        var size = Math.Max(0.0, bytesPerSec.Value);
        string valueStr;
        string unitStr;

        if (size < KB * 1000)
        {
            var kb = size / KB;
            valueStr = FormatThreeDigits(kb);
            unitStr = "KB/s";
        }
        else if (size < MB * 1000)
        {
            var mb = size / MB;
            valueStr = FormatThreeDigits(mb);
            unitStr = "MB/s";
        }
        else
        {
            var gb = size / GB;
            valueStr = FormatThreeDigits(gb);
            unitStr = "GB/s";
        }

        return spaceBeforeUnit ? $"{valueStr} {unitStr}" : $"{valueStr}{unitStr}";
    }

    /// <summary>
    /// 胶囊形态专用紧凑网速格式：
    /// 最多保留 1 位小数，数字达到 2 位（>= 10）时不保留小数，确保上下行同框不滚动。
    /// 例如：0.0KB/s、1.1KB/s、11KB/s、123KB/s、1.2MB/s、12MB/s。
    /// </summary>
    public static string FormatCapsule(double? bytesPerSec)
    {
        if (bytesPerSec is null || double.IsNaN(bytesPerSec.Value) || double.IsInfinity(bytesPerSec.Value))
            return "--";

        var size = Math.Max(0.0, bytesPerSec.Value);
        string valueStr;
        string unitStr;

        if (size < KB * 1000)
        {
            valueStr = FormatMaxTwoDigits(size / KB);
            unitStr = "KB/s";
        }
        else if (size < MB * 1000)
        {
            valueStr = FormatMaxTwoDigits(size / MB);
            unitStr = "MB/s";
        }
        else
        {
            valueStr = FormatMaxTwoDigits(size / GB);
            unitStr = "GB/s";
        }

        return $"{valueStr}{unitStr}";
    }

    private static string FormatMaxTwoDigits(double value)
    {
        if (value < 9.95)
            return value.ToString("0.0");
        return value.ToString("0");
    }

    /// <summary>
    /// 保证数字部分尽量维持 3 位数字：
    /// &lt; 10（且四舍五入后不到 10）保留 2 位小数（0.12）
    /// &lt; 100（且四舍五入后不到 100）保留 1 位小数（12.1）
    /// &gt;= 100 保留 0 位小数（123）
    /// </summary>
    private static string FormatThreeDigits(double value)
    {
        if (value < 9.995)
            return value.ToString("0.00");
        if (value < 99.95)
            return value.ToString("0.0");
        return value.ToString("0");
    }
}
