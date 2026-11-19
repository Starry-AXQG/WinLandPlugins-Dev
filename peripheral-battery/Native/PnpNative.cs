using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PeripheralBattery.Native;

/// <summary>
/// 用 SetupAPI 读设备属性（PnP 属性），主要用来拿蓝牙设备的电量。
/// 全部调用容忍失败：拿不到就返回 null，绝不抛。
/// </summary>
internal static class PnpNative
{
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_ALLCLASSES = 0x04;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    // DEVPROP_TYPE_* 枚举值：BYTE=0x03、UINT32=0x07（之前把 BOOLEAN 的 0x11 当成了 BYTE，导致所有蓝牙电量被丢弃）
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_UINT32 = 0x00000007;

    /// <summary>DEVPKEY_Bluetooth_Battery（0~100，DEVPROP_TYPE_BYTE）。</summary>
    private static readonly Guid BluetoothBatteryGuid = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    private const uint BluetoothBatteryPid = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder instanceId, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint propertyType, byte[] buffer, int size, out int required, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType, byte[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    /// <summary>一个带 PnP 电量的蓝牙设备。</summary>
    internal sealed record PnpBluetoothDevice(string InstanceId, string FriendlyName, int Battery);

    /// <summary>枚举所有 PnP 设备里带蓝牙电量的那些（BTHLE / BTHENUM 服务项）。</summary>
    internal static List<PnpBluetoothDevice> ReadBluetoothBatteries()
    {
        var result = new List<PnpBluetoothDevice>();

        try
        {
            var set = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set.ToInt64() == -1) return result;

            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };

                for (var index = 0; ; index++)
                {
                    data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiEnumDeviceInfo(set, index, ref data)) break;

                    var instanceId = GetInstanceId(set, ref data);
                    if (string.IsNullOrEmpty(instanceId)) continue;
                    if (!IsBluetoothNode(instanceId)) continue;

                    var battery = GetBluetoothBattery(set, ref data);
                    if (battery is null) continue;

                    var name = GetFriendlyName(set, ref data);
                    result.Add(new PnpBluetoothDevice(instanceId!, name, battery.Value));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }
        catch
        {
            // 枚举失败按"没有蓝牙设备"处理
        }

        return result;
    }

    private static bool IsBluetoothNode(string instanceId)
    {
        if (instanceId.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase)) return true;

        // Hands-Free AG / HF 服务项（耳机、手机）能报电量；其它 BTHENUM 服务项一般没有
        if (instanceId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)
            && (instanceId.Contains("{0000111E-", StringComparison.OrdinalIgnoreCase)
                || instanceId.Contains("{0000111F-", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static string? GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new StringBuilder(512);
        if (!SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Capacity, out _)) return null;
        return buffer.ToString();
    }

    private static string GetFriendlyName(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_FRIENDLYNAME, out _, buffer, buffer.Length, out var required)
            || required <= 2)
        {
            return "";
        }

        var text = Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length));
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    private static int? GetBluetoothBattery(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var key = new DEVPROPKEY { fmtid = BluetoothBatteryGuid, pid = BluetoothBatteryPid };
        var buffer = new byte[16];

        if (!SetupDiGetDeviceProperty(set, ref data, ref key, out var type, buffer, buffer.Length, out var required, 0)) return null;
        if (required < 1) return null;

        // 实测 Windows 对这条属性报的是 DEVPROP_TYPE_UINT32(0x03)，但只回 1 字节；
        // 两种形态都兼容，别因为类型判断把数据丢掉。
        int value = type switch
        {
            DEVPROP_TYPE_BYTE => buffer[0],
            DEVPROP_TYPE_UINT32 when required >= 4 => BitConverter.ToInt32(buffer, 0),
            _ => buffer[0],
        };

        return value is >= 0 and <= 100 ? value : null;
    }
}
