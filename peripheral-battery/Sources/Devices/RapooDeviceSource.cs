using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery.Devices;

/// <summary>
/// Rapoo（雷柏）VT9PRO / V2M 4K / VT 系列 2.4G 无线接收器与有线模式电量直读。
///
/// 逆向自雷柏官方桌面驱动 <c>RapooGameDev 1.6.29</c>（<c>KDeviceVT950_Nordic::getBatteryValue</c> @ <c>0x01177060</c>）
/// 并经用户真机（VT9PRO + V2M 4K 接收器 <c>0x24AE:0x1205</c> / 有线 <c>0x24AE:0x4405</c>）抓包验证：
///
/// 1. <b>硬件接口</b>：
///    · 厂商 VID 恒为 <c>0x24AE</c>，控制通道为 <c>UsagePage = 0xFF00, Usage = 0x000E</c>（典型报告长度 32 字节，ReportId = <c>0xBA</c>）。
///    · 覆盖型号包括：VT9PRO（1K <c>0x185A</c> / 4K <c>0x1601</c> / V2M <c>0x1600</c> / HSDM <c>0x1205</c> / 有线 <c>0x418A</c>, <c>0x4405</c>）、
///      VT9PRO Mini、VT9、VT9S、VT9 Air、VT1PRO (MAX)、VT1Air MAX、VT2PRO、VT3PRO (MAX)、VT3S、VT0PRO、VT0、
///      VT960PRO、VT950PRO、V300PRO、V200PRO、V20PRO、V30PRO 等全系 <c>0xBA</c> 协议鼠标。
///
/// 2. <b>通信机制（关键避坑：走 USB 控制传输 HidD_GetInputReport，而非中断管道 ReadFile）</b>：
///    · 请求帧（长度取接口 <c>OutputLength</c>，通常 32 字节）：
///      - <c>tx[0] = 0xBA</c>（ReportId）
///      - <c>tx[1] = target</c>（2.4G 无线接收器为 <c>0xA5</c>，USB 有线直连为 <c>0xFF</c>）
///      - <c>tx[2] = 0xAA</c>（<c>getBatteryValue</c> 命令字）
///      - <c>tx[3..N-1] = 0x00</c>
///      HSDM 系列（<c>0x12xx</c> / <c>0x44xx</c>）优先通过 <c>HidD_SetOutputReport</c> 下发，经典 Nordic 系列（<c>0x16xx</c> / <c>0x18xx</c> / <c>0x41xx</c>）
///      优先通过 <c>WriteFile</c> 下发（底层互为自动回退）。
///    · 响应帧：下发后等待 50ms，调用 <c>HidD_GetInputReport</c>（<c>rx[0] = 0xBA</c>）读取（绝不在中断 IN 管道空等超时）：
///      Windows <c>hidusb.sys</c> 控制传输返回的数据直接从 <c>rx[0]</c> 起存放（若存在过滤驱动保留首字节 <c>0xBA</c>，则整体右移 1 字节）：
///      - <c>ack = rx[offset]</c>：<c>0x01</c> = 成功；<c>0x02</c> = 设备忙（延时 50ms 重读）；<c>0x00</c> = 无线休眠或关机离线
///      - <c>chargeState = rx[offset + 1]</c>：<c>0x01</c> = 未充电（放电中）；<c>0x02</c> = 充电中；<c>0x00</c> = 离线/无效
///      - <c>percent = rx[offset + 2]</c>：实时电量百分比（<c>1..100</c>）
/// </summary>
internal sealed class RapooDeviceSource : IDeviceSource
{
    private const ushort VendorIdRapoo = 0x24AE;
    private const ushort ControlUsagePage = 0xFF00;
    private const ushort ControlUsage = 0x000E;

    private const byte ReportIdBa = 0xBA;
    private const byte TargetWireless = 0xA5;
    private const byte TargetWired = 0xFF;
    private const byte CmdGetBattery = 0xAA;
    private const int DefaultReportLength = 32;

    /// <summary>
    /// 按 (VID, PID) 缓存最近一次唤醒态读到的有效电量，
    /// 防止无线鼠标静止进入省电休眠（返回 ack=0x00）时从灵动岛列表闪烁消失。
    /// </summary>
    private readonly Dictionary<(ushort Vid, ushort Pid), DeviceBattery> _lastKnownByProduct = new();

    public string Brand => "Rapoo";

    public List<DeviceBattery> Collect(DeviceSourceContext context)
    {
        var result = new List<DeviceBattery>();
        var all = context.HidInterfaces;

        var candidates = all
            .Where(h => h.VendorId == VendorIdRapoo
                        && h.UsagePage == ControlUsagePage
                        && h.Usage == ControlUsage
                        && h.InputLength >= 8
                        && h.OutputLength >= 8
                        && IsSupportedBaProtocolPid(h.ProductId))
            .ToList();

        context.Debug($"{Brand}：HID 共 {all.Count} 条，0xBA 控制通道（FF00:000E）命中 {candidates.Count} 条");

        // 清理已从 USB 口拔出的接收器/设备缓存
        var presentProducts = candidates.Select(h => (h.VendorId, h.ProductId)).ToHashSet();
        foreach (var cachedKey in _lastKnownByProduct.Keys.Where(k => !presentProducts.Contains(k)).ToList())
        {
            _lastKnownByProduct.Remove(cachedKey);
        }

        var seenProducts = new HashSet<(ushort Vid, ushort Pid)>();

        foreach (var hid in candidates)
        {
            var productKey = (hid.VendorId, hid.ProductId);
            if (!seenProducts.Add(productKey)) continue;

            var isWired = IsWiredPid(hid.ProductId);
            var preferControlOut = IsHsdmControlOutPid(hid.ProductId);
            var outLen = Math.Max(hid.OutputLength, DefaultReportLength);
            var inLen = Math.Max(hid.InputLength, DefaultReportLength);

            var req = BuildGetBatteryRequest(isWired ? TargetWired : TargetWireless, outLen);
            var resp = Native.HidNative.TransactControlInput(
                hid.Path,
                req,
                ReportIdBa,
                inLen,
                preferControlOutput: preferControlOut,
                isReady: IsAckSuccessFrame,
                initialDelayMs: 50,
                retryDelayMs: 50,
                maxAttempts: 3);

            // 极少数 Telink 0xBA 方案若不支持 GetInputReport，回退一次中断管道 Transact
            if (resp is null)
            {
                resp = Native.HidNative.Transact(
                    hid.Path,
                    req,
                    inLen,
                    rx => rx.Length >= 4 && (rx[0] == ReportIdBa ? rx[1] != 0x02 : rx[0] != 0x02),
                    timeoutMs: 220,
                    maxReads: 3);
            }

            var displayName = ResolveFriendlyName(hid.ProductId, hid.ProductName);

            if (resp is null || !TryParseBatteryResponse(resp, out var ack, out var chargeState, out var level))
            {
                var headHex = resp is null ? "null" : FormatPreviewHex(resp, 6);
                if (!isWired && _lastKnownByProduct.TryGetValue(productKey, out var cached))
                {
                    context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）当前休眠或离线（rx={headHex}），暂留休眠前电量={cached.Level}%（标记为缓存）");
                    result.Add(cached.AsStale());
                }
                else
                {
                    context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）当前休眠、未开机或无响应（rx={headHex}）");
                }
                continue;
            }

            bool charging = chargeState == 0x02;
            context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）直读成功，电量={level}%，充电={(charging ? "是" : "否")}（ack=0x{ack:X2}, state=0x{chargeState:X2}）");

            var entry = new DeviceBattery
            {
                Id = BuildStableDeviceId(displayName, hid.VendorId, hid.ProductId),
                Name = displayName,
                Level = level,
                Charging = charging ? true : null,
                Kind = DeviceBattery.GuessKind(displayName) is var guess && guess != PeripheralKind.Other
                    ? guess
                    : PeripheralKind.Mouse,
                Link = isWired ? PeripheralLink.Wired : PeripheralLink.Usb24G,
                Source = "HID 直读",
                Stale = false,
            };

            if (!isWired)
            {
                _lastKnownByProduct[productKey] = entry;
            }

            result.Add(entry);
        }

        return result;
    }

    private static byte[] BuildGetBatteryRequest(byte targetDevice, int length)
    {
        var packet = new byte[length];
        packet[0] = ReportIdBa;
        packet[1] = targetDevice;
        packet[2] = CmdGetBattery;
        return packet;
    }

    private static bool IsAckSuccessFrame(byte[] rx)
    {
        if (rx.Length < 4) return false;
        var offset = rx[0] == ReportIdBa ? 1 : 0;
        return rx.Length >= offset + 3 && rx[offset] == 0x01;
    }

    private static bool TryParseBatteryResponse(
        byte[] rx,
        out byte ack,
        out byte chargeState,
        out int level)
    {
        ack = 0;
        chargeState = 0;
        level = 0;

        if (rx.Length < 3) return false;
        var offset = rx[0] == ReportIdBa ? 1 : 0;
        if (rx.Length < offset + 3) return false;

        ack = rx[offset];
        chargeState = rx[offset + 1];
        int rawPercent = rx[offset + 2];

        // RapooGameDev 逻辑：ack == 1 且 chargeState 为 1(未充电) 或 2(充电中) 时有效，超过 100 截断为 100
        if (ack != 0x01 || chargeState is not (0x01 or 0x02))
        {
            return false;
        }

        if (rawPercent <= 0) return false;
        level = Math.Min(rawPercent, 100);
        return true;
    }

    /// <summary>
    /// 仅匹配确认采用 0xBA 协议的雷柏 PID 段，避开二代 nRF54L 网页驱动款（0x14xx/0x1Bxx/0x46xx/0x4Bxx，
    /// 其 FF00:000E 接口用于 Nordic SMP 固件升级）。
    /// </summary>
    private static bool IsSupportedBaProtocolPid(ushort pid) =>
        pid is (>= 0x1200 and <= 0x12FF)
            or (>= 0x1600 and <= 0x16FF)
            or (>= 0x1800 and <= 0x18FF)
            or (>= 0x4100 and <= 0x41FF)
            or (>= 0x4400 and <= 0x44FF);

    private static bool IsWiredPid(ushort pid) =>
        pid is (>= 0x4100 and <= 0x41FF and not 0x4133)
            or (>= 0x4400 and <= 0x44FF);

    private static bool IsHsdmControlOutPid(ushort pid) =>
        pid is (>= 0x1200 and <= 0x12FF)
            or (>= 0x4400 and <= 0x44FF);

    private static string BuildStableDeviceId(string friendlyName, ushort vid, ushort pid)
    {
        var norm = DeviceBattery.NormalizeName(friendlyName);
        return !string.IsNullOrEmpty(norm) && !norm.Equals("rapoogamingdevice", StringComparison.Ordinal)
            ? $"rapoo:{norm}"
            : $"hid:{vid:x4}:{pid:x4}";
    }

    /// <summary>
    /// 雷柏接收器（如 V2M / HSDM 接收器）的 USB ProductString 常统一写为 "Rapoo Gaming Device"，
    /// 根据官方驱动 PID 映射表还原为具体鼠标型号名，确保界面直观且有线/无线去重一致。
    /// </summary>
    private static string ResolveFriendlyName(ushort pid, string? usbProductName)
    {
        if (KnownModelsByPid.TryGetValue(pid, out var known))
        {
            return known;
        }

        if (!string.IsNullOrWhiteSpace(usbProductName)
            && !usbProductName.Equals("Rapoo Gaming Device", StringComparison.OrdinalIgnoreCase)
            && !usbProductName.Equals("USB Dongle", StringComparison.OrdinalIgnoreCase))
        {
            return usbProductName.Trim();
        }

        return "Rapoo 无线鼠标";
    }

    private static string FormatPreviewHex(byte[] data, int maxBytes)
    {
        var count = Math.Min(data.Length, maxBytes);
        return BitConverter.ToString(data, 0, count).Replace('-', ' ');
    }

    private static readonly Dictionary<ushort, string> KnownModelsByPid = new()
    {
        // VT9PRO 系列（含 1K / 4K / HSDM / 欧版 / 有线）
        [0x185A] = "Rapoo VT9PRO",
        [0x418A] = "Rapoo VT9PRO",
        [0x1601] = "Rapoo VT9PRO",
        [0x1205] = "Rapoo VT9PRO",
        [0x4405] = "Rapoo VT9PRO",
        [0x1221] = "Rapoo VT9PRO",
        [0x4421] = "Rapoo VT9PRO",
        [0x1211] = "Rapoo VT9PRO",
        [0x4411] = "Rapoo VT9PRO",
        [0x1227] = "Rapoo VT9PRO",
        [0x4427] = "Rapoo VT9PRO",

        // VT9PRO Mini
        [0x1861] = "Rapoo VT9PRO Mini",
        [0x4146] = "Rapoo VT9PRO Mini",
        [0x1616] = "Rapoo VT9PRO Mini",

        // VT9 / VT9S / VT9 Air 系列
        [0x1849] = "Rapoo VT9",
        [0x4191] = "Rapoo VT9",
        [0x1610] = "Rapoo VT9",
        [0x1851] = "Rapoo VT9S",
        [0x4199] = "Rapoo VT9S",
        [0x1611] = "Rapoo VT9S",
        [0x1200] = "Rapoo VT9 Air",
        [0x4400] = "Rapoo VT9 Air",
        [0x1217] = "Rapoo VT9 Air",
        [0x4417] = "Rapoo VT9 Air",
        [0x1867] = "Rapoo VT9 Air Lite",
        [0x4155] = "Rapoo VT9 Air Lite",
        [0x1618] = "Rapoo VT9 Air Lite",

        // VT1 / VT2 / VT3 / VT0 系列
        [0x1214] = "Rapoo VT1PRO",
        [0x4414] = "Rapoo VT1PRO",
        [0x1230] = "Rapoo VT1PRO",
        [0x4430] = "Rapoo VT1PRO",
        [0x1233] = "Rapoo VT1PRO MAX",
        [0x4433] = "Rapoo VT1PRO MAX",
        [0x1235] = "Rapoo VT1PRO MAX",
        [0x4435] = "Rapoo VT1PRO MAX",
        [0x1234] = "Rapoo VT1Air MAX",
        [0x4434] = "Rapoo VT1Air MAX",
        [0x1236] = "Rapoo VT1Air MAX",
        [0x4436] = "Rapoo VT1Air MAX",
        [0x1204] = "Rapoo VT2PRO",
        [0x4404] = "Rapoo VT2PRO",
        [0x1212] = "Rapoo VT2PRO",
        [0x4412] = "Rapoo VT2PRO",
        [0x1220] = "Rapoo VT2PRO",
        [0x4420] = "Rapoo VT2PRO",
        [0x1228] = "Rapoo VT2PRO",
        [0x4428] = "Rapoo VT2PRO",
        [0x1215] = "Rapoo VT3PRO",
        [0x4415] = "Rapoo VT3PRO",
        [0x1231] = "Rapoo VT3PRO",
        [0x4431] = "Rapoo VT3PRO",
        [0x1244] = "Rapoo VT3PRO MAX",
        [0x4444] = "Rapoo VT3PRO MAX",
        [0x184C] = "Rapoo VT3S",
        [0x4194] = "Rapoo VT3S",
        [0x1609] = "Rapoo VT3S",
        [0x1202] = "Rapoo VT0PRO",
        [0x4402] = "Rapoo VT0PRO",
        [0x1209] = "Rapoo VT0PRO",
        [0x4409] = "Rapoo VT0PRO",
        [0x1225] = "Rapoo VT0PRO",
        [0x4425] = "Rapoo VT0PRO",
        [0x1208] = "Rapoo VT0PRO Mini",
        [0x4408] = "Rapoo VT0PRO Mini",
        [0x1855] = "Rapoo VT0",
        [0x419E] = "Rapoo VT0",
        [0x1612] = "Rapoo VT0",
        [0x1860] = "Rapoo VT0 Mini",
        [0x419F] = "Rapoo VT0 Mini",
        [0x1615] = "Rapoo VT0 Mini",
        [0x1203] = "Rapoo VT0 Mini",
        [0x4403] = "Rapoo VT0 Mini",
        [0x1224] = "Rapoo VT0 Mini",
        [0x4424] = "Rapoo VT0 Mini",

        // VT950 / VT960 / V300 / V200 / V20 / V30 & V2M 通用接收器
        [0x1600] = "Rapoo V2M 鼠标",
        [0x184A] = "Rapoo VT950PRO",
        [0x4192] = "Rapoo VT950PRO",
        [0x1607] = "Rapoo VT950PRO",
        [0x184B] = "Rapoo VT960PRO",
        [0x4193] = "Rapoo VT960PRO",
        [0x1608] = "Rapoo VT960PRO",
        [0x185C] = "Rapoo V300PRO",
        [0x418C] = "Rapoo V300PRO",
        [0x1603] = "Rapoo V300PRO",
        [0x1210] = "Rapoo V200PRO",
        [0x4410] = "Rapoo V200PRO",
        [0x1216] = "Rapoo V200PRO",
        [0x4416] = "Rapoo V200PRO",
        [0x1226] = "Rapoo V200PRO",
        [0x4426] = "Rapoo V200PRO",
        [0x1232] = "Rapoo V200PRO",
        [0x4432] = "Rapoo V200PRO",
        [0x1207] = "Rapoo V20PRO",
        [0x4407] = "Rapoo V20PRO",
        [0x1223] = "Rapoo V20PRO",
        [0x4423] = "Rapoo V20PRO",
        [0x1213] = "Rapoo V30PRO",
        [0x4413] = "Rapoo V30PRO",
        [0x1229] = "Rapoo V30PRO",
        [0x4429] = "Rapoo V30PRO",
    };
}
