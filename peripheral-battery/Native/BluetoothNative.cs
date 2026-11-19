using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace PeripheralBattery.Native;

/// <summary>
/// 蓝牙设备的「此刻是否真的连着」以及设备类别（Class of Device）。
///
/// 为什么需要这个：只看 PnP 树分不出「配对过但没连上」的设备 —— 那些服务节点
/// （BTHENUM\...Hands-Free AG 之类）在系统里仍然保持 present，并且会一直回放
/// 最后一次读到的电量（常见就是 100%），于是界面上会出现一堆根本没开机的耳机。
/// 这里用的 System.Devices.Aep.IsConnected 正是系统「设置 → 蓝牙」里
/// 显示「已连接」所用的同一个标志，实测与本机真实状态完全一致。
/// </summary>
internal static class BluetoothNative
{
    /// <summary>一台已配对蓝牙设备的实时状态。</summary>
    internal sealed record BtDevice(string Mac, string Name, bool Connected, int CodMajor, int CodMinor);

    /// <summary>Succeeded=false 表示枚举本身失败（此时不要用连接状态做过滤，否则会把设备全藏起来）。</summary>
    internal sealed record BtScanResult(bool Succeeded, List<BtDevice> Devices);

    private const string PropConnected = "System.Devices.Aep.IsConnected";
    private const string PropAddress = "System.Devices.Aep.DeviceAddress";
    private const string PropCodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string PropCodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";

    private static readonly string[] Properties = { PropConnected, PropAddress, PropCodMajor, PropCodMinor };

    /// <summary>
    /// 枚举经典蓝牙与 BLE 两套 AEP（同一台设备可能在两边各有一条，例如手机），
    /// 汇总成 (MAC, 名称, 是否连接, COD) 列表。
    /// </summary>
    internal static async Task<BtScanResult> ScanAsync()
    {
        var devices = new List<BtDevice>();
        var succeeded = false;

        try
        {
            await CollectAsync(BluetoothDevice.GetDeviceSelector(), devices);
            succeeded = true;
        }
        catch
        {
            // 经典蓝牙不可用（没有适配器 / 系统策略），继续试 BLE
        }

        try
        {
            await CollectAsync(BluetoothLEDevice.GetDeviceSelector(), devices);
            succeeded = true;
        }
        catch
        {
            // BLE 不可用
        }

        return new BtScanResult(succeeded, devices);
    }

    private static async Task CollectAsync(string aqs, List<BtDevice> into)
    {
        var found = await DeviceInformation.FindAllAsync(aqs, Properties);

        foreach (var info in found)
        {
            var mac = NormalizeMac(ReadString(info, PropAddress));
            if (mac.Length == 0) continue;

            into.Add(new BtDevice(
                mac,
                (info.Name ?? "").Trim(),
                ReadBool(info, PropConnected),
                ReadUShort(info, PropCodMajor),
                ReadUShort(info, PropCodMinor)));
        }
    }

    private static object? Raw(DeviceInformation info, string key) =>
        info.Properties.TryGetValue(key, out var value) ? value : null;

    private static string ReadString(DeviceInformation info, string key) => Raw(info, key) as string ?? "";

    private static bool ReadBool(DeviceInformation info, string key) => Raw(info, key) is bool b && b;

    private static int ReadUShort(DeviceInformation info, string key) => Raw(info, key) switch
    {
        ushort v => v,
        int v => v,
        uint v => (int)v,
        _ => 0,
    };

    /// <summary>把 "a0:5a:5e:7c:8d:ab" 归一成 "A05A5E7C8DAB"（与 PnP InstanceId 里那串一致）。</summary>
    internal static string NormalizeMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return "";

        var chars = new char[12];
        var count = 0;
        foreach (var c in mac)
        {
            if (!IsHex(c)) continue;
            if (count >= 12) break;
            chars[count++] = char.ToUpperInvariant(c);
        }

        return count == 12 ? new string(chars) : "";
    }

    /// <summary>
    /// 从 PnP InstanceId 里抠出 12 位 MAC（大写无分隔），抠不到返回空串。
    /// 覆盖三种写法：
    ///   BTHLE\DEV_C818F67774A0\...
    ///   BTHENUM\DEV_00001308026F\...
    ///   BTHENUM\{0000111E-...}_LOCALMFG&0046\9&BFCBF6&0&00A41C2FF71B_C00000000
    /// </summary>
    internal static string MacFromInstanceId(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return "";

        var dev = instanceId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        if (dev >= 0)
        {
            var start = dev + 4;
            var end = start;
            while (end < instanceId.Length && IsHex(instanceId[end])) end++;
            if (end - start >= 12) return instanceId.Substring(start, 12).ToUpperInvariant();
        }

        var marker = instanceId.LastIndexOf("&0&", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var start = marker + 3;
            var end = instanceId.IndexOf('_', start);
            if (end < 0) end = instanceId.Length;

            var segment = instanceId.Substring(start, end - start);
            if (segment.Length >= 12)
            {
                var allHex = true;
                for (var i = 0; i < 12; i++)
                {
                    if (!IsHex(segment[i])) { allHex = false; break; }
                }

                if (allHex) return segment.Substring(0, 12).ToUpperInvariant();
            }
        }

        return "";
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// Class of Device 的 Major/Minor 段 → 外设类别。
    /// 只用来兜底「名字完全看不出来」的设备（例如中文昵称「浓韵」「沉香」）；
    /// 名字里已经写明类型的（mouse / keyboard / xbox…）优先用名字，见 BatteryProvider。
    /// </summary>
    internal static PeripheralKind KindFromCod(int major, int minor) => major switch
    {
        4 => PeripheralKind.Headset,        // Audio/Video：耳机、头戴、音箱
        2 => PeripheralKind.Phone,          // Phone
        // Peripheral：bit7-6 是设备大类，bit5-4 细分键鼠。
        // 本机实测 minor=32(0x20)=鼠标、minor=2=手柄 —— 直接比相等会把两者都判错，必须按位取。
        5 => minor == 1
            ? PeripheralKind.Keyboard      // 旧式：minor 直接写 1 表示键盘
            : (minor & 0x30) switch
            {
                0x10 => PeripheralKind.Keyboard,
                0x20 => PeripheralKind.Mouse,
                0x30 => PeripheralKind.Keyboard,   // 键鼠一体
                // 认不出细分就交给名字兜底（"Xbox Wireless Controller" 会被 GuessKind 判成手柄）
                _ => PeripheralKind.Other,
            },
        _ => PeripheralKind.Other,
    };

    /// <summary>调试用：把一组设备摊成一行文本。</summary>
    internal static string Describe(IEnumerable<BtDevice> devices) =>
        string.Join("；", devices.Select(d => $"{d.Name}{(d.Connected ? "[已连]" : "[未连]")}"));
}
