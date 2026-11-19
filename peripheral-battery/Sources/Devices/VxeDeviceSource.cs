using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery.Devices;

/// <summary>
/// VXE / ATK（COMPX 方案）2.4G 无线鼠标接收器。
///
/// 逆向自 ATK Hub 网页驱动（https://hub.atk.pro/）：
///   · 接收器 VID 为 <c>0x3554</c>（或 <c>0x373B</c>），通信接口为
///     <c>UsagePage = 0xFF02, Usage = 0x0002</c>，Input / Output 报告长度均为 17 字节（ReportId = <c>0x08</c>）。
///   · 注意：原先读取的 <c>UsagePage = 0xFF04</c> Feature Report <c>0x06</c> 返回的 <c>06 FF 00 64 64 64 A9 97</c>
///     并非电量，而是固件静态参数与 RF 地址（<c>A9-97-D3</c>），其中 <c>0x64</c> 恒为 100。
///
/// COMPX 17 字节帧结构（tx[0..16]）：
///   · <c>tx[0] = 0x08</c>（ReportId）
///   · <c>tx[1] = commandId</c>（<c>0x03</c> = GetWirelessMouseOnline，<c>0x04</c> = GetBatteryLevel）
///   · <c>tx[2] = commandStatus</c>（请求为 <c>0x00</c>；响应 <c>0x00</c> = Success）
///   · <c>tx[3..4] = eepromAddress</c>（uint16 BE，电量/在线查询为 0）
///   · <c>tx[5] = dataValidLen</c>（请求为 0，响应为有效载荷字节数）
///   · <c>tx[6..15] = data[0..9]</c>（有效载荷）
///   · <c>tx[16] = checkSum</c>，满足 <c>(0x08 + sum(tx[1..16])) &amp; 0xFF == 0x55</c>
///
/// 读取流程：
///   1. 发送 <c>cmd = 0x03</c>（GetWirelessMouseOnline）：若响应 <c>rx[6] != 1</c>，说明鼠标休眠或关机，跳过不显示。
///   2. 发送 <c>cmd = 0x04</c>（GetBatteryLevel）：
///      - <c>rx[6]</c> = 电量百分比（0..100）
///      - <c>rx[7]</c> = 充电状态（1 = 充电中，0 = 未充电）
///      - <c>(rx[8] &lt;&lt; 8) | rx[9]</c> = 电池电压（mV，例如 3682mV）
/// </summary>
internal sealed class VxeDeviceSource : IDeviceSource
{
    private const ushort VendorIdCompx = 0x3554;
    private const ushort VendorIdAtk = 0x373B;
    private const ushort CompxUsagePage = 0xFF02;
    private const ushort CompxUsage = 0x0002;
    private const byte CompxReportId = 0x08;
    private const int CompxReportLength = 17;

    private const byte CmdGetWirelessMouseOnline = 0x03;
    private const byte CmdGetBatteryLevel = 0x04;

    public string Brand => "VXE";

    public List<DeviceBattery> Collect(DeviceSourceContext context)
    {
        var result = new List<DeviceBattery>();

        var all = context.HidInterfaces;
        var candidates = all
            .Where(h => (h.VendorId == VendorIdCompx || h.VendorId == VendorIdAtk)
                        && h.UsagePage == CompxUsagePage
                        && h.Usage == CompxUsage
                        && h.InputLength >= CompxReportLength
                        && h.OutputLength >= CompxReportLength)
            .ToList();

        context.Debug($"{Brand}：HID 共 {all.Count} 条，COMPX 通道（FF02:0002）命中 {candidates.Count} 条");

        var seenProducts = new HashSet<(ushort Vid, ushort Pid)>();

        foreach (var hid in candidates)
        {
            if (seenProducts.Contains((hid.VendorId, hid.ProductId))) continue;

            // 1. 先检查无线鼠标是否在线（开机且未深度断连）
            var onlineReq = BuildCompxPacket(CmdGetWirelessMouseOnline);
            var onlineResp = Native.HidNative.Transact(
                hid.Path,
                onlineReq,
                Math.Max(hid.InputLength, CompxReportLength),
                rx => rx.Length >= 7 && rx[0] == CompxReportId && rx[1] == CmdGetWirelessMouseOnline,
                timeoutMs: 250);

            if (onlineResp is null || onlineResp[2] != 0x00)
            {
                context.Debug($"{Brand}：在线状态查询无响应（{hid.VendorId:X4}:{hid.ProductId:X4}）");
                continue;
            }

            var isOnline = onlineResp[6] == 1;
            if (!isOnline)
            {
                context.Debug($"{Brand}：无线鼠标当前离线或休眠（onlineStatus={onlineResp[6]}）");
                continue;
            }

            // 2. 查询实时电量、充电状态与电压
            var batteryReq = BuildCompxPacket(CmdGetBatteryLevel);
            var batteryResp = Native.HidNative.Transact(
                hid.Path,
                batteryReq,
                Math.Max(hid.InputLength, CompxReportLength),
                rx => rx.Length >= 10 && rx[0] == CompxReportId && rx[1] == CmdGetBatteryLevel,
                timeoutMs: 250);

            if (batteryResp is null || batteryResp[2] != 0x00)
            {
                context.Debug($"{Brand}：电量查询无响应或状态失败（{hid.VendorId:X4}:{hid.ProductId:X4}）");
                continue;
            }

            int level = batteryResp[6];
            bool charging = batteryResp[7] == 1;
            int voltageMv = (batteryResp[8] << 8) | batteryResp[9];

            if (level <= 0 || level > 100)
            {
                context.Debug($"{Brand}：返回电量无效 level={level}, voltage={voltageMv}mV");
                continue;
            }

            context.Debug($"{Brand}：{hid.ProductName} 在线，电量={level}%，充电={(charging ? "是" : "否")}，电压={voltageMv}mV");

            var name = string.IsNullOrWhiteSpace(hid.ProductName) ? "VXE 无线鼠标" : hid.ProductName;

            result.Add(new DeviceBattery
            {
                Id = $"hid:{hid.VendorId:x4}:{hid.ProductId:x4}",
                Name = name,
                Level = level,
                Charging = charging ? true : null,
                Kind = DeviceBattery.GuessKind(name) is var guess && guess != PeripheralKind.Other
                    ? guess
                    : PeripheralKind.Mouse,
                Link = PeripheralLink.Usb24G,
                Source = "HID 直读",
            });

            seenProducts.Add((hid.VendorId, hid.ProductId));
        }

        return result;
    }

    private static byte[] BuildCompxPacket(byte commandId)
    {
        var packet = new byte[CompxReportLength];
        packet[0] = CompxReportId;
        packet[1] = commandId;

        int sum = CompxReportId;
        for (int i = 1; i < CompxReportLength - 1; i++)
        {
            sum += packet[i];
        }

        packet[CompxReportLength - 1] = (byte)((0x55 - (sum & 0xFF)) & 0xFF);
        return packet;
    }
}
