using System;
using System.Collections.Generic;

namespace PeripheralBattery.Devices;

/// <summary>
/// 蓝牙 / 蓝牙 LE 设备，数据来自 Windows PnP 的电池属性。
///
/// 两条关键事实（都是实测踩出来的，改这里之前务必先读）：
///
/// 1. <b>PnP 树分不出"连着"还是"只是配对过"</b>。
///    <c>BTHENUM\...Hands-Free AG</c> 这类服务节点即使设备没开机也保持 present，
///    并且一直回放最后一次读到的电量（经常是 100%），Get-PnpDevice 里全是 Status=OK。
///    所以必须再查一次 WinRT AEP 的 <c>System.Devices.Aep.IsConnected</c> 才能过滤掉没开机的设备。
///
/// 2. <b>一台设备会有多条服务节点</b>。
///    例如手机走 BLE 连着、它的经典 Hands-Free 节点却报未连接；
///    只按 MAC 过滤会把这台真正在线的设备误杀。所以保留条件是
///    <c>macConnected || nameConnected</c> —— 名字也得认一次。
///
/// 设备类型优先用 Class of Device（COD）判定：中文昵称（「浓韵」「沉香」）靠名字永远猜不出来，
/// 而 COD 会老实报 Audio/Video。
/// </summary>
internal sealed class BluetoothDeviceSource : IDeviceSource
{
    public string Brand => "蓝牙";

    public List<DeviceBattery> Collect(DeviceSourceContext context)
    {
        var result = new List<DeviceBattery>();
        var scan = context.Bluetooth;

        var raw = Native.PnpNative.ReadBluetoothBatteries();
        context.Info($"{Brand} PnP 命中 {raw.Count} 条");

        // 连接状态索引：MAC → 是否已连接 / COD。查不到就把过滤关掉（宁可多显示，不要全丢）
        var connected = new Dictionary<string, bool>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, PeripheralKind>(StringComparer.Ordinal);
        var connectedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in scan.Devices)
        {
            if (d.Mac.Length == 0) continue;

            // 同一台设备可能同时出现在经典与 LE 两条 AEP 上，任一边报已连接就算连上了
            connected[d.Mac] = connected.TryGetValue(d.Mac, out var already) ? already || d.Connected : d.Connected;

            var kind = Native.BluetoothNative.KindFromCod(d.CodMajor, d.CodMinor);
            if (kind != PeripheralKind.Other && !kinds.ContainsKey(d.Mac)) kinds[d.Mac] = kind;

            if (d.Connected)
            {
                var connectedName = DeviceBattery.NormalizeName(CleanName(d.Name));
                if (connectedName.Length > 0) connectedNames.Add(connectedName);
            }
        }

        var filterByConnection = scan.Succeeded && connected.Count > 0;
        var skipped = 0;

        foreach (var device in raw)
        {
            var mac = Native.BluetoothNative.MacFromInstanceId(device.InstanceId);
            var name = CleanName(device.FriendlyName);
            if (string.IsNullOrWhiteSpace(name)) name = device.InstanceId;

            if (filterByConnection)
            {
                var macConnected = mac.Length > 0 && connected.TryGetValue(mac, out var c) && c;
                var nameConnected = connectedNames.Contains(DeviceBattery.NormalizeName(name));

                if (!macConnected && !nameConnected)
                {
                    skipped++;
                    context.Debug($"{Brand}：跳过未连接设备 {name}（{mac}）");
                    continue;
                }
            }

            var link = device.InstanceId.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase)
                ? PeripheralLink.BluetoothLE
                : PeripheralLink.Bluetooth;

            // 图标：先用 COD（系统认定的设备类别），看不出来再用名字猜
            var kind = kinds.TryGetValue(mac, out var byCod) && byCod != PeripheralKind.Other
                ? byCod
                : DeviceBattery.GuessKind(name);

            context.Debug($"{Brand}：{name} = {device.Battery}%（{link}，{device.InstanceId}）");

            result.Add(new DeviceBattery
            {
                Id = "bt:" + device.InstanceId,
                Name = name,
                Level = device.Battery,
                Kind = kind,
                Link = link,
                Source = "系统蓝牙",
            });
        }

        if (filterByConnection)
        {
            context.Info($"{Brand}：已连接 {raw.Count - skipped} 台，跳过未连接/已配对未开机 {skipped} 台");
        }

        return result;
    }

    /// <summary>
    /// 把服务节点名收干净：
    /// "一加 15 Hands-Free HF" → "一加 15"
    /// "OPPO Enco Free4（丹拿版） Hands-Free AG" → "OPPO Enco Free4（丹拿版）"
    /// </summary>
    internal static string CleanName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var name = raw.Trim();
        foreach (var suffix in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length].Trim();
            }
        }

        if (name.StartsWith("Bluetooth ", StringComparison.OrdinalIgnoreCase))
        {
            name = name["Bluetooth ".Length..].Trim();
        }

        return name;
    }

    private static readonly string[] Suffixes =
    {
        " Hands-Free HF", " Hands-Free AG", " Hands-Free", " Avrcp 传输", " A2DP SNK",
        " A2DP SRC", " 设备标识服务", " 耳机音频网关服务", " 个人区域网服务",
        " 电话簿访问 PSE 服务", " 对象推送服务", " 个人区域网 NAP 服务",
    };
}
