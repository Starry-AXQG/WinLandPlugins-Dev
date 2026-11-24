using System.Globalization;
using System.Text.Json.Serialization;

namespace HidBatteryProbe;

/// <summary>
/// 探测配置文件（probe-config.json）根模型。
/// 支持针对任意新外设品牌定义候选 VID/PID、接口过滤条件与多步探测动作。
/// </summary>
public sealed class ProbeConfig
{
    public string Brand { get; set; } = "";
    public string ModelHint { get; set; } = "";
    public string DriverSource { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<TargetFilter> Targets { get; set; } = new();
    public List<ProbeAction> Actions { get; set; } = new();
}

/// <summary>
/// 匹配目标 HID 接口的筛选条件（留空的字段表示不限制）。
/// Hex 字段支持 "0x24AE"、"24AE" 或十进制字符串 "9390"。
/// </summary>
public sealed class TargetFilter
{
    public string Name { get; set; } = "";
    public string? VendorId { get; set; }
    public List<string>? ProductIds { get; set; }
    public string? UsagePage { get; set; }
    public string? MinUsagePage { get; set; }
    public string? Usage { get; set; }
    public List<string>? ExcludeUsages { get; set; }
    public int? MinInputLength { get; set; }
    public int? MinOutputLength { get; set; }
    public int? MinFeatureLength { get; set; }

    public bool Matches(HidDeviceInfo dev)
    {
        if (HexUtil.TryParseUshort(VendorId, out var vid) && dev.VendorIdValue != vid)
            return false;

        if (ProductIds is { Count: > 0 })
        {
            var pidMatched = false;
            foreach (var p in ProductIds)
            {
                if (HexUtil.TryParseUshort(p, out var pid) && dev.ProductIdValue == pid)
                {
                    pidMatched = true;
                    break;
                }
            }
            if (!pidMatched) return false;
        }

        if (HexUtil.TryParseUshort(UsagePage, out var up) && dev.UsagePageValue != up)
            return false;

        if (HexUtil.TryParseUshort(MinUsagePage, out var minUp) && dev.UsagePageValue < minUp)
            return false;

        if (HexUtil.TryParseUshort(Usage, out var u) && dev.UsageValue != u)
            return false;

        if (ExcludeUsages is { Count: > 0 })
        {
            foreach (var ex in ExcludeUsages)
            {
                if (HexUtil.TryParseUshort(ex, out var exUsage) && dev.UsageValue == exUsage)
                    return false;
            }
        }

        if (MinInputLength is { } minIn && dev.InputReportLength < minIn)
            return false;

        if (MinOutputLength is { } minOut && dev.OutputReportLength < minOut)
            return false;

        if (MinFeatureLength is { } minFeat && dev.FeatureReportLength < minFeat)
            return false;

        return true;
    }
}

/// <summary>
/// 单个探测动作定义。
/// Kind 支持：
///   - "outputThenInput"         : 发送 Output Report（自动补齐到 OutputReportLength），循环读取 Input Report
///   - "outputThenFeature"       : 发送 Output Report，延时后读取指定 Feature Report（支持 busy 重试）
///   - "setFeatureThenGetFeature": HidD_SetFeature 后读取 HidD_GetFeature
///   - "getFeature"              : 纯只读 HidD_GetFeature
///   - "listenInput"             : 纯被动监听 Input Report（不发送任何写入包）
/// </summary>
public sealed class ProbeAction
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "outputThenInput";

    /// <summary>可选：仅在满足附加筛选条件的候选接口上执行此动作。</summary>
    public TargetFilter? Filter { get; set; }

    /// <summary>要发送的完整帧前缀 Hex（首字节为 ReportId，后续不足报告长度部分自动补 0x00）。</summary>
    public string? RequestHex { get; set; }

    /// <summary>getFeature / outputThenFeature / setFeatureThenGetFeature 读取的目标 Feature ReportId。</summary>
    public string? ResponseReportId { get; set; }

    /// <summary>可选：期望的响应前缀 Hex（仅用于标记 Matched，所有收到的帧都会被完整记录）。</summary>
    public string? ExpectPrefixHex { get; set; }

    /// <summary>可选：若响应某字节等于 BusyValue（如雷柏 ACK=0x02 表示设备忙），等待 RetryDelayMs 后重读。</summary>
    public int? BusyByteIndex { get; set; }
    public string? BusyValue { get; set; }

    public int TimeoutMs { get; set; } = 350;
    public int DelayBeforeReadMs { get; set; } = 25;
    public int RetryDelayMs { get; set; } = 50;
    public int MaxReads { get; set; } = 4;
    public int MaxRetries { get; set; } = 3;
    public bool FlushQueueBeforeWrite { get; set; } = true;
}

// ====================== 输出报告模型 ======================

public sealed class ProbeReport
{
    public string ToolVersion { get; set; } = "1.0.0";
    public string CapturedAtUtc { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string TargetBrand { get; set; } = "";
    public string TargetModel { get; set; } = "";
    public string DriverSource { get; set; } = "";
    public string UserReportedBattery { get; set; } = "";
    public string UserNotes { get; set; } = "";

    /// <summary>初始扫描到的全部本机 HID 接口快照（已脱敏实例序列号）。</summary>
    public List<HidDeviceInfo> AllHidDevices { get; set; } = new();

    /// <summary>按规则命中的目标品牌 HID 接口列表。</summary>
    public List<HidDeviceInfo> MatchedTargetDevices { get; set; } = new();

    /// <summary>分阶段抓取结果（唤醒态 / 充电态 / 关机离线态）。</summary>
    public List<CapturePhaseResult> Phases { get; set; } = new();
}

public sealed class HidDeviceInfo
{
    [JsonIgnore]
    public string RawPath { get; set; } = "";

    [JsonIgnore]
    public ushort VendorIdValue { get; set; }

    [JsonIgnore]
    public ushort ProductIdValue { get; set; }

    [JsonIgnore]
    public ushort UsagePageValue { get; set; }

    [JsonIgnore]
    public ushort UsageValue { get; set; }

    public string InterfaceSlot { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UsagePage { get; set; } = "";
    public string Usage { get; set; } = "";
    public int InputReportLength { get; set; }
    public int OutputReportLength { get; set; }
    public int FeatureReportLength { get; set; }
    public bool CanOpenReadWrite { get; set; }
    public int OpenReadWriteWin32Error { get; set; }
}

public sealed class CapturePhaseResult
{
    public string PhaseId { get; set; } = "";
    public string PhaseTitle { get; set; } = "";
    public bool Skipped { get; set; }
    public string CapturedAtUtc { get; set; } = "";
    public string UserNote { get; set; } = "";

    /// <summary>该阶段实时枚举到的目标品牌 HID 接口（例如插线充电时可能新增有线 PID）。</summary>
    public List<HidDeviceInfo> PresentTargetDevices { get; set; } = new();

    public List<ActionTrace> Traces { get; set; } = new();
}

public sealed class ActionTrace
{
    public string ActionId { get; set; } = "";
    public string ActionKind { get; set; } = "";
    public string InterfaceSlot { get; set; } = "";
    public string VidPid { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UsagePageUsage { get; set; } = "";
    public string WriteMethod { get; set; } = "";
    public string RequestHex { get; set; } = "";
    public bool WriteSucceeded { get; set; }
    public int WriteWin32Error { get; set; }
    public long TotalElapsedMs { get; set; }
    public string? DecodedHint { get; set; }
    public List<ReceivedFrame> Frames { get; set; } = new();
}

public sealed class ReceivedFrame
{
    public int Index { get; set; }
    public string Source { get; set; } = "";
    public long ElapsedMs { get; set; }
    public bool TimedOut { get; set; }
    public int Win32Error { get; set; }
    public int ByteLength { get; set; }
    public bool MatchedExpectPrefix { get; set; }
    public string RawHex { get; set; } = "";
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ProbeConfig))]
[JsonSerializable(typeof(ProbeReport))]
internal partial class ProbeJsonContext : JsonSerializerContext
{
}

internal static class HexUtil
{
    public static bool TryParseUshort(string? text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (s.Any(c => c is >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            return ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return ushort.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseByte(string? text, out byte value)
    {
        value = 0;
        if (!TryParseUshort(text, out var u) || u > 255) return false;
        value = (byte)u;
        return true;
    }

    public static byte[] ParseHexBytes(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        var cleaned = hex
            .Replace("0x", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("0X", " ", StringComparison.OrdinalIgnoreCase)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);

        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1 && tokens[0].Length > 2 && tokens[0].Length % 2 == 0)
        {
            var raw = tokens[0];
            var arr = new byte[raw.Length / 2];
            for (var i = 0; i < arr.Length; i++)
                arr[i] = byte.Parse(raw.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return arr;
        }

        var result = new List<byte>(tokens.Length);
        foreach (var token in tokens)
        {
            result.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return result.ToArray();
    }

    public static string ToHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return "";
        const string digits = "0123456789ABCDEF";
        var chars = new char[bytes.Length * 3 - 1];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) chars[i * 3 - 1] = ' ';
            var b = bytes[i];
            chars[i * 3] = digits[b >> 4];
            chars[i * 3 + 1] = digits[b & 0x0F];
        }
        return new string(chars);
    }

    public static bool StartsWithPrefix(byte[] data, byte[] prefix)
    {
        if (prefix.Length == 0) return true;
        if (data.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i]) return false;
        }
        return true;
    }
}
