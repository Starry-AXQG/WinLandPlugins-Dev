using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PeripheralBattery.Devices;

namespace PeripheralBattery;

/// <summary>
/// 采集所有外设电量：把活派给 <see cref="DeviceSourceRegistry"/> 里登记的数据源，
/// 再把各家结果去重、排序。
///
/// 每个源都独立 try/catch：一个源坏了不影响其它源。
/// 新增品牌支持不需要动这个文件 —— 见 <c>Sources/Devices/IDeviceSource.cs</c>。
/// </summary>
internal sealed class BatteryProvider
{
    private readonly Action<string, string> _log;

    public BatteryProvider(Action<string, string> log)
    {
        _log = log;
    }

    /// <summary>采一轮，返回已经合并、去重、按紧急度排好序的设备列表。</summary>
    public async Task<List<DeviceBattery>> CollectAsync()
    {
        // 蓝牙连接状态是异步 WinRT 查询，必须先拿到再派活：蓝牙源要用它过滤没开机的设备。
        // 失败时 Succeeded=false，采集逻辑据此退化成"不过滤连接状态"，
        // 绝不会因为查不到就把设备全藏起来。
        var btScan = await SafeScanAsync().ConfigureAwait(false);

        var context = new DeviceSourceContext(
            _log,
            EnumerateHid,
            btScan);

        var all = new List<DeviceBattery>();

        foreach (var source in DeviceSourceRegistry.All)
        {
            var name = source.Brand;
            var items = await Task.Run(() => SafeCollect(name, () => source.Collect(context))).ConfigureAwait(false);
            all.AddRange(items);
        }

        var merged = Deduplicate(all);
        _log("Info", $"采集明细：原始 {all.Count} 条（" +
                     string.Join("，", all.GroupBy(x => x.Source).Select(g => $"{g.Key} {g.Count()}")) +
                     $")，去重后 {merged.Count} 条");
        foreach (var d in DeviceBattery.SortByUrgency(merged))
        {
            _log("Info", $"  · {d.Name} = {d.LevelText}（{d.LinkLabel} / {d.Source}{(d.Stale ? " / 旧" : "")}）"
                         + $" [图标 {d.Kind} U+{(int)d.Glyph[0]:X4}]");
        }

        return DeviceBattery.SortByUrgency(merged);
    }

    /// <summary>HID 枚举：只做一次，多个 2.4G 数据源共用（见 DeviceSourceContext.HidInterfaces）。</summary>
    private IReadOnlyList<Native.HidNative.HidInterface> EnumerateHid()
    {
        var all = Native.HidNative.Enumerate();
        if (!string.IsNullOrEmpty(Native.HidNative.LastError))
        {
            _log("Warn", $"HID 枚举出错 —— {Native.HidNative.LastError}");
        }

        _log("Debug", $"HID 枚举：接口 {Native.HidNative.LastInterfaceCount} 个，" +
                      $"打开失败 {Native.HidNative.LastDescribeFailures} 个，成功描述 {all.Count} 个");
        foreach (var trace in Native.HidNative.LastTrace) _log("Debug", "HID trace: " + trace);

        return all;
    }

    private async Task<Native.BluetoothNative.BtScanResult> SafeScanAsync()
    {
        try
        {
            var scan = await Native.BluetoothNative.ScanAsync().ConfigureAwait(false);
            _log("Info", $"蓝牙连接状态：{(scan.Succeeded ? "可用" : "不可用（跳过连接过滤）")}，" +
                         $"已配对 {scan.Devices.Count} 台 —— {Native.BluetoothNative.Describe(scan.Devices)}");
            return scan;
        }
        catch (Exception ex)
        {
            _log("Warn", $"查询蓝牙连接状态失败：{ex.Message}");
            return new Native.BluetoothNative.BtScanResult(false, new List<Native.BluetoothNative.BtDevice>());
        }
    }

    private List<DeviceBattery> SafeCollect(string name, Func<List<DeviceBattery>> source)
    {
        try
        {
            return source();
        }
        catch (Exception ex)
        {
            _log("Warn", $"采集 {name} 失败：{ex.Message}");
            return new List<DeviceBattery>();
        }
    }

    /// <summary>
    /// 同一台设备可能同时以「2.4G 接收器」和「蓝牙配对」两条记录出现（例如 G87 V2）。
    /// 名字归一后相同就只留一条，优先保留 2.4G —— 设备插着接收器时蓝牙那条是旧值。
    /// </summary>
    private static List<DeviceBattery> Deduplicate(List<DeviceBattery> items)
    {
        var result = new List<DeviceBattery>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var key = NormalizeKey(item.Name);
            if (key.Length == 0)
            {
                result.Add(item);
                continue;
            }

            if (!index.TryGetValue(key, out var existing))
            {
                index[key] = result.Count;
                result.Add(item);
                continue;
            }

            if (Rank(item) > Rank(result[existing])) result[existing] = item;
        }

        return result;
    }

    /// <summary>
    /// 去重时的优先级：实时在线通道一律优先于休眠/缓存快照；同为实时通道时 2.4G 直读 &gt; 蓝牙 LE &gt; 蓝牙。
    /// 这样当 2.4G 外设在不拔接收器的情况下切到蓝牙模式时，2.4G 的休眠缓存（Stale=true）会让位给实时蓝牙连接。
    /// </summary>
    private static int Rank(DeviceBattery item)
    {
        var score = item.Link switch
        {
            PeripheralLink.Usb24G => 30,
            PeripheralLink.BluetoothLE => 20,
            PeripheralLink.Bluetooth => 10,
            _ => 0,
        };

        if (item.Stale) score -= 25;
        if (item.Level is null) score -= 10;
        return score;
    }

    /// <summary>把 "MCHOSE G87 V2 2.4G" 与 "MCHOSE G87 V2-1" 归一成同一个键。</summary>
    private static string NormalizeKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var lower = name.ToLowerInvariant();
        var chars = lower.Where(char.IsLetterOrDigit).ToArray();
        var key = new string(chars);

        // 去掉连接方式与序号尾巴，让两种命名能对上
        foreach (var tail in new[] { "24g", "bluetooth", "wireless", "le" })
        {
            if (key.EndsWith(tail, StringComparison.Ordinal) && key.Length > tail.Length + 3)
            {
                key = key[..^tail.Length];
            }
        }

        key = key.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return key;
    }
}
