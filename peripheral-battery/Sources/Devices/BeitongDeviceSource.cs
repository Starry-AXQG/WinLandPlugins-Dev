using System;
using System.Collections.Generic;
using System.Linq;

namespace PeripheralBattery.Devices;

/// <summary>
/// BEITONG（北通）鲲鹏 20 / 40 / 50 / 70 全系与盘古（PG01A）2.4G 无线接收器及有线模式电量直读。
///
/// 逆向自本地「北通智控（BetopGame）」客户端（<c>kp70c.js</c> / Vue 状态栏组件 / <c>aipan70.exe</c>）
/// 并经本机北通鲲鹏70 6D（<c>BTP-KP70C</c>，2.4G 接收器 <c>0x20BC:0x5174</c>）真机实测验证：
///
/// 1. <b>硬件接口</b>：
///    · 厂商 VID 为 <c>0x20BC</c>（备用 VID <c>0x20DD</c>），专用配置/状态通道为
///      <c>UsagePage = 0x00FF, Usage = 0x0003</c>，Input / Output 报告长度均为 64 字节（<c>ReportId = 0x02</c>）。
///    · 覆盖北通 <c>0x51xx</c> 新一代智控协议手柄：鲲鹏70（KP70A / KP70B / KP70C 6D / 午马限定 / 异环联名）、
///      鲲鹏50（KP50B / KP50C / 解限机联名）、鲲鹏40（KP40A / KP40B / KP40D / KP40E / KP40F / 鸣潮联名）、
///      鲲鹏20（KP20A / KP20D / KP20EB / 战双联名）以及北通盘古（PG01A）。
///
/// 2. <b>只读状态查询协议（无副作用，不改动模式/配置/灯效/震动）</b>：
///    · 帧头结构：<c>byte[0] = 0x02</c>（ReportId），<c>byte[1] = cmd | (subcmd &lt;&lt; 4)</c>。
///    · <b>步骤 A · 在线与固件状态查询（<c>getFWInfo</c>，仅 2.4G 接收器模式需要先判在线）</b>：
///      - 请求：<c>[0x02, 0x55, 0x00, ...]</c>（64 字节，<c>cmd = 0x5, subcmd = 0x5</c>）
///      - 响应：<c>rx[0] == 0x02 &amp;&amp; rx[1] == 0x55</c>
///        * <c>rx[2..4]</c> = 接收器固件版本（如 <c>1.0.5</c>）
///        * <c>rx[5]</c> = <c>fw_conn</c>（<c>1</c> = 无线手柄已唤醒连接；<c>0</c> = 手柄关机、休眠或切至蓝牙/NS 模式）
///    · <b>步骤 B · 基础电量与充电状态查询（<c>getBaseInfo</c>）</b>：
///      - 请求：<c>[0x02, 0x15, 0x00, ...]</c>（64 字节，<c>cmd = 0x5, subcmd = 0x1</c>）
///      - 响应：<c>rx[0] == 0x02 &amp;&amp; rx[1] == 0x15</c>
///        * <c>rx[2]</c> = <c>bat_level</c>：实时精确电量百分比（<c>1..100</c>，例如 <c>0x2B = 43%</c>）
///        * <c>rx[4] &amp; 0x0F</c> = <c>charge_state</c>（低 4 位：<c>1</c> = 充电中，<c>0</c> = 未充电；高 4 位为其他标志位需掩码剥离）
/// </summary>
internal sealed class BeitongDeviceSource : IDeviceSource
{
    private const ushort VendorIdBeitong = 0x20BC;
    private const ushort VendorIdBeitongAlt = 0x20DD;
    private const ushort ControlUsagePage = 0x00FF;
    private const ushort ControlUsage = 0x0003;

    private const byte ConfigReportId = 0x02;
    private const byte HeaderGetFwInfo = 0x55;   // cmd = 0x05, subcmd = 0x05
    private const byte HeaderGetBaseInfo = 0x15; // cmd = 0x05, subcmd = 0x01
    private const int DefaultReportLength = 64;

    /// <summary>
    /// 按接收器 (VID, PID) 缓存最近一次唤醒态读到的有效电量，
    /// 避免手柄短暂搁置进入省电休眠（fw_conn=0）时从灵动岛列表消失；接收器物理拔出时立即清理。
    /// </summary>
    private readonly Dictionary<(ushort Vid, ushort Pid), DeviceBattery> _lastKnownByProduct = new();

    public string Brand => "Beitong";

    public List<DeviceBattery> Collect(DeviceSourceContext context)
    {
        var result = new List<DeviceBattery>();
        var all = context.HidInterfaces;

        var candidates = all
            .Where(h => (h.VendorId == VendorIdBeitong || h.VendorId == VendorIdBeitongAlt)
                        && h.UsagePage == ControlUsagePage
                        && h.Usage == ControlUsage
                        && h.InputLength >= 16
                        && h.OutputLength >= 16
                        && IsSupportedKunpengProtocolPid(h.ProductId))
            .ToList();

        context.Debug($"{Brand}：HID 共 {all.Count} 条，北通智控通道（00FF:0003）命中 {candidates.Count} 条");

        // 清理已从 USB 口拔出的接收器/手柄缓存
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

            var isWired = IsWiredPid(hid.ProductId, hid.ProductName);
            var outLen = Math.Max(hid.OutputLength, DefaultReportLength);
            var inLen = Math.Max(hid.InputLength, DefaultReportLength);
            var displayName = ResolveFriendlyName(hid.ProductId, hid.ProductName);

            // 1. 2.4G 接收器模式下先通过 getFWInfo (0x55) 检查无线手柄是否在线唤醒（fw_conn == 1）
            if (!isWired)
            {
                var fwReq = BuildCommandPacket(HeaderGetFwInfo, outLen);
                var fwResp = Native.HidNative.Transact(
                    hid.Path,
                    fwReq,
                    inLen,
                    rx => rx.Length >= 6 && rx[0] == ConfigReportId && rx[1] == HeaderGetFwInfo,
                    timeoutMs: 250,
                    maxReads: 6);

                if (fwResp is null)
                {
                    if (_lastKnownByProduct.TryGetValue(productKey, out var cachedOnTimeout))
                    {
                        context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）在线查询超时，沿用最近一次已知电量={cachedOnTimeout.Level}%（标记为缓存）");
                        result.Add(cachedOnTimeout.AsStale());
                    }
                    else
                    {
                        context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）getFWInfo 无响应");
                    }
                    continue;
                }

                var fwConn = fwResp[5];
                if (fwConn == 0)
                {
                    if (_lastKnownByProduct.TryGetValue(productKey, out var cachedOnSleep))
                    {
                        context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）无线手柄当前休眠或关机（fw_conn=0），暂留休眠前电量={cachedOnSleep.Level}%（标记为缓存）");
                        result.Add(cachedOnSleep.AsStale());
                    }
                    else
                    {
                        context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）无线手柄未开机或处于休眠态（fw_conn=0，尚无历史电量快照）");
                    }
                    continue;
                }
            }

            // 2. 发送 getBaseInfo (0x15) 读取实时电量百分比与充电状态
            var baseReq = BuildCommandPacket(HeaderGetBaseInfo, outLen);
            var baseResp = Native.HidNative.Transact(
                hid.Path,
                baseReq,
                inLen,
                rx => rx.Length >= 5 && rx[0] == ConfigReportId && rx[1] == HeaderGetBaseInfo,
                timeoutMs: 250,
                maxReads: 6);

            if (baseResp is null)
            {
                if (!isWired && _lastKnownByProduct.TryGetValue(productKey, out var cachedOnBaseTimeout))
                {
                    context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）电量查询偶发未响应，沿用最近一次已知电量={cachedOnBaseTimeout.Level}%（标记为缓存）");
                    result.Add(cachedOnBaseTimeout.AsStale());
                }
                else
                {
                    context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）getBaseInfo 无响应");
                }
                continue;
            }

            int level = baseResp[2];
            byte rawChargeByte = baseResp[4];
            byte chargeState = (byte)(rawChargeByte & 0x0F);

            if (level <= 0 || level > 100)
            {
                if (!isWired && _lastKnownByProduct.TryGetValue(productKey, out var cachedOnInvalid))
                {
                    context.Debug($"{Brand}：{displayName} 返回瞬态无效电量 level={level}，沿用最近一次已知电量={cachedOnInvalid.Level}%（标记为缓存）");
                    result.Add(cachedOnInvalid.AsStale());
                }
                else
                {
                    context.Debug($"{Brand}：{displayName} 返回无效电量 level={level}（rawCharge=0x{rawChargeByte:X2}）");
                }
                continue;
            }

            bool charging = chargeState == 0x01;
            context.Debug($"{Brand}：{displayName}（{hid.VendorId:X4}:{hid.ProductId:X4}）直读成功，电量={level}%，充电={(charging ? "是" : "否")}（state=0x{rawChargeByte:X2}）");

            var entry = new DeviceBattery
            {
                Id = BuildStableDeviceId(displayName, hid.VendorId, hid.ProductId),
                Name = displayName,
                Level = level,
                Charging = charging ? true : null,
                Kind = PeripheralKind.Gamepad,
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

    private static byte[] BuildCommandPacket(byte header, int length)
    {
        var packet = new byte[length];
        packet[0] = ConfigReportId;
        packet[1] = header;
        return packet;
    }

    /// <summary>
    /// 匹配采用新一代 0x00FF:0x0003（ReportId 0x02, getBaseInfo 0x15）协议的北通 0x51xx/0x52xx PID 段，
    /// 避开早期阿修罗2 NS 等旧协议型号（0x50xx，电量字节偏移不同）。
    /// </summary>
    private static bool IsSupportedKunpengProtocolPid(ushort pid) =>
        pid is >= 0x5120 and <= 0x52FF;

    private static bool IsWiredPid(ushort pid, string? productName)
    {
        if (KnownWiredPids.Contains(pid)) return true;
        if (KnownDonglePids.Contains(pid)) return false;

        if (!string.IsNullOrWhiteSpace(productName)
            && productName.Contains("DONGLE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return false;
    }

    private static string BuildStableDeviceId(string friendlyName, ushort vid, ushort pid)
    {
        var norm = DeviceBattery.NormalizeName(friendlyName);
        return !string.IsNullOrEmpty(norm)
            ? $"beitong:{norm}"
            : $"hid:{vid:x4}:{pid:x4}";
    }

    private static string ResolveFriendlyName(ushort pid, string? usbProductName)
    {
        if (KnownModelsByPid.TryGetValue(pid, out var known))
        {
            return known;
        }

        if (!string.IsNullOrWhiteSpace(usbProductName))
        {
            var cleaned = usbProductName.Trim();
            foreach (var suffix in new[]
                     {
                         " XINPUT DONGLE",
                         " DONGLE",
                         " GAMEPAD",
                         " CONTROLLER",
                         " 2.4G",
                     })
            {
                if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && cleaned.Length > suffix.Length)
                {
                    cleaned = cleaned[..^suffix.Length].Trim();
                }
            }

            if (cleaned.Length > 0) return cleaned;
        }

        return "北通无线手柄";
    }

    private static readonly HashSet<ushort> KnownWiredPids = new()
    {
        0x5125, 0x5126, 0x512F, 0x5133, 0x5145, 0x5149,
        0x5150, 0x5152, 0x5154, 0x5158, 0x515B, 0x515D, 0x515F,
        0x5161, 0x5163, 0x5165, 0x5169, 0x516D, 0x516F,
        0x5173, 0x518A, 0x518C, 0x5190, 0x5194, 0x519B, 0x519D,
    };

    private static readonly HashSet<ushort> KnownDonglePids = new()
    {
        0x5127, 0x5128, 0x5130, 0x5134, 0x5146, 0x514A,
        0x5151, 0x5153, 0x5155, 0x5159, 0x515C, 0x515E,
        0x5160, 0x5162, 0x5164, 0x5166, 0x516A, 0x516E,
        0x5170, 0x5174, 0x518B, 0x518D, 0x5191, 0x5195, 0x519C, 0x519E,
    };

    private static readonly Dictionary<ushort, string> KnownModelsByPid = new()
    {
        // 北通鲲鹏70 系列（含 AI旗舰 / 6D升级款 / 午马限定 / 异环联名）
        [0x512F] = "北通鲲鹏70",
        [0x5130] = "北通鲲鹏70",
        [0x515F] = "北通鲲鹏70",
        [0x5160] = "北通鲲鹏70",
        [0x516D] = "北通鲲鹏70 限定",
        [0x516E] = "北通鲲鹏70 限定",
        [0x5173] = "北通鲲鹏70 6D",
        [0x5174] = "北通鲲鹏70 6D",
        [0x518A] = "北通鲲鹏70 异环",
        [0x518B] = "北通鲲鹏70 异环",
        [0x518C] = "北通鲲鹏70 异环",
        [0x518D] = "北通鲲鹏70 异环",
        [0x519B] = "北通鲲鹏70 异环",
        [0x519C] = "北通鲲鹏70 异环",
        [0x519D] = "北通鲲鹏70 异环",
        [0x519E] = "北通鲲鹏70 异环",

        // 北通盘古（BTP-PG01A）
        [0x5161] = "北通盘古",
        [0x5162] = "北通盘古",

        // 北通鲲鹏50 系列（云翼白 / 星穹紫 / 解限机联名）
        [0x5133] = "北通鲲鹏50",
        [0x5134] = "北通鲲鹏50",
        [0x5149] = "北通鲲鹏50",
        [0x514A] = "北通鲲鹏50",
        [0x5150] = "北通鲲鹏50",
        [0x5151] = "北通鲲鹏50",
        [0x5152] = "北通鲲鹏50",
        [0x5153] = "北通鲲鹏50",

        // 北通鲲鹏40 系列（标准 / 极光蓝 / 御铠白 / 风驰白 / 电掣黑 / 鸣潮联名 / 精英版）
        [0x5125] = "北通鲲鹏40",
        [0x5127] = "北通鲲鹏40",
        [0x5145] = "北通鲲鹏40",
        [0x5146] = "北通鲲鹏40",
        [0x5154] = "北通鲲鹏40",
        [0x5155] = "北通鲲鹏40",
        [0x515B] = "北通鲲鹏40",
        [0x515C] = "北通鲲鹏40",
        [0x515D] = "北通鲲鹏40",
        [0x515E] = "北通鲲鹏40",
        [0x5163] = "北通鲲鹏40 鸣潮",
        [0x5164] = "北通鲲鹏40 鸣潮",
        [0x5165] = "北通鲲鹏40 鸣潮",
        [0x5166] = "北通鲲鹏40 鸣潮",
        [0x5169] = "北通鲲鹏40",
        [0x516A] = "北通鲲鹏40",
        [0x5194] = "北通鲲鹏40 精英",
        [0x5195] = "北通鲲鹏40 精英",

        // 北通鲲鹏20 系列（标准 / 绝尘白 / 战双联名 / 精英版）
        [0x5126] = "北通鲲鹏20",
        [0x5128] = "北通鲲鹏20",
        [0x5158] = "北通鲲鹏20",
        [0x5159] = "北通鲲鹏20",
        [0x516F] = "北通鲲鹏20",
        [0x5170] = "北通鲲鹏20",
        [0x5190] = "北通鲲鹏20 精英",
        [0x5191] = "北通鲲鹏20 精英",
    };
}
