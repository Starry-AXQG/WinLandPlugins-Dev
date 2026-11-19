using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PeripheralBattery.Devices;

/// <summary>
/// MCHOSE（迈从）2.4G 外设（G87 V2 等 GLW 方案键盘 + HUB 缓存兜底）。
///
/// 逆向自迈从网页驱动（https://www.mchose.com.cn/#/connectDevice，`CZ_SHARED_DATA / GLWDeviceSDK`）：
///   1. **GLW 键盘 HID 直读（优先，无需运行 MCHOSE HUB，且可与 MCHOSE HUB 共存）**：
///      · 接收器 VID 为 <c>0x3837</c>（14391）或 <c>0x41E4</c>（16868），通信接口为
///        <c>UsagePage = 0x0001, Usage = 0x0000</c>，Input / Output 报告长度均为 65 字节（ReportId = <c>0x00</c>）。
///      · 65 字节请求帧（tx[0..64]）：
///        - <c>tx[0] = 0x00</c>（ReportId）
///        - <c>tx[1] = 0x55</c>（GLW 帧头 flag）
///        - <c>tx[2] = 0x05</c>（<c>getFuncConfig</c> 命令）
///        - <c>tx[3] = 0x00</c>
///        - <c>tx[4] = (byte)((tx[5] + tx[6] + tx[7] + tx[8]) &amp; 0xFF)</c>（头部校验和）
///        - <c>tx[5] = 0x38</c>（读取长度 56 字节）
///        - <c>tx[6..7] = 0x0000</c>（offset LE = 0）
///        - <c>tx[8] = 0x00</c>
///      · 65 字节响应帧（rx[0..64]）：
///        - 若 <c>rx[1] == 0xFA &amp;&amp; rx[2] == 0xFB &amp;&amp; rx[3] == 0x06 &amp;&amp; rx[4] == 0x00</c>，表示无线键盘当前休眠/离线（<c>stopCommunication</c>）。
///        - 若 <c>rx[1] == 0xAA &amp;&amp; rx[2] == 0x05 &amp;&amp; rx[5] &lt;= 0x38 &amp;&amp; rx[6] == 0x00 &amp;&amp; rx[7] == 0x00</c>，有效载荷从 <c>rx[9]</c> 开始（对应 JS <c>e = n.slice(8)</c>）：
///          * <c>rx[41]</c>（即 <c>e[32]</c>）：低 7 位 <c>rx[41] &amp; 0x7F</c> 为实时电量百分比（1..100），最高位 <c>(rx[41] &gt;&gt; 7) &amp; 1</c> 为充电状态（1 = 充电中）。
///          * <c>rx[43]</c>（即 <c>e[34]</c>）：<c>work_mode</c>（2 = 2.4G 无线，1 = 有线）。
///
///   2. **MCHOSE HUB 本地缓存兜底（仅对非 GLW 通道的其他迈从设备）**：
///      · 注意：MCHOSE HUB 运行时的 <c>trayService.syncDeviceList</c> 调用的是 <c>setStore("webIndexList", ..., false)</c>（非持久化），
///        因此 <c>%APPDATA%\MCHOSEHUB\files\config\mc_main_store_key.json</c> 只在 HUB 启动时写入一次，运行期间不会随电量变化落盘。
///      · 凡是本机已匹配到 GLW HID 通道的设备，一律以 HID 直读为准，不再回退到陈旧的 JSON 缓存。
/// </summary>
internal sealed class MchoseDeviceSource : IDeviceSource
{
    private const ushort VendorIdMchose1 = 0x3837;
    private const ushort VendorIdMchose2 = 0x41E4;
    private const ushort GlwUsagePage = 0x0001;
    private const ushort GlwUsage = 0x0000;
    private const int GlwReportLength = 65;

    private const byte GlwRequestFlag = 0x55;
    private const byte GlwResponseFlag = 0xAA;
    private const byte GlwCmdGetFuncConfig = 0x05;
    private const byte GlwFuncConfigSize = 0x38;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public string Brand => "MCHOSE";

    public List<DeviceBattery> Collect(DeviceSourceContext context)
    {
        var result = new List<DeviceBattery>();
        var handledHidProducts = new HashSet<(int Vid, int Pid)>();

        // 1. 优先通过 GLW HID 协议直读实时电量
        CollectViaGlwHid(context, result, handledHidProducts);

        // 2. 对未命中 GLW HID 通道的其他迈从设备，保留 HUB 配置文件兜底
        CollectViaHubCache(context, result, handledHidProducts);

        return result;
    }

    private void CollectViaGlwHid(
        DeviceSourceContext context,
        List<DeviceBattery> result,
        HashSet<(int Vid, int Pid)> handledHidProducts)
    {
        var all = context.HidInterfaces;
        var candidates = all
            .Where(h => (h.VendorId == VendorIdMchose1 || h.VendorId == VendorIdMchose2)
                        && h.UsagePage == GlwUsagePage
                        && h.Usage == GlwUsage
                        && h.InputLength >= GlwReportLength
                        && h.OutputLength >= GlwReportLength)
            .ToList();

        context.Debug($"{Brand}：HID 共 {all.Count} 条，GLW 通道（0001:0000）命中 {candidates.Count} 条");

        foreach (var hid in candidates)
        {
            var key = ((int)hid.VendorId, (int)hid.ProductId);
            if (handledHidProducts.Contains(key)) continue;

            // 记录该 VID:PID 已由 GLW HID 通道接管（即使键盘休眠也不回退到过期的 HUB 缓存）
            handledHidProducts.Add(key);

            var req = BuildGlwGetFuncConfigPacket(offset: 0, size: GlwFuncConfigSize);
            var resp = Native.HidNative.Transact(
                hid.Path,
                req,
                Math.Max(hid.InputLength, GlwReportLength),
                IsGlwFuncConfigOrStopFrame,
                timeoutMs: 300);

            if (resp is null)
            {
                context.Debug($"{Brand}：GLW getFuncConfig 无响应（{hid.VendorId:X4}:{hid.ProductId:X4}）");
                continue;
            }

            if (IsGlwStopFrame(resp))
            {
                context.Debug($"{Brand}：键盘当前处于休眠/离线状态（stopCommunication FA FB 06 00）");
                continue;
            }

            // payload 从 rx[9] 开始（rx[0] 为 ReportId 0x00，rx[1..8] 为 8 字节 GLW 帧头）
            // e[32] -> rx[9 + 32] = rx[41]
            // e[34] -> rx[9 + 34] = rx[43]
            byte rawBattery = resp[41];
            int level = rawBattery & 0x7F;
            bool charging = ((rawBattery >> 7) & 1) == 1;
            byte workMode = resp[43];

            if (level <= 0 || level > 100)
            {
                context.Debug($"{Brand}：GLW 返回电量无效 raw=0x{rawBattery:X2} (level={level})");
                continue;
            }

            var rawName = string.IsNullOrWhiteSpace(hid.ProductName) ? "MCHOSE 无线键盘" : hid.ProductName.Trim();
            var cleanName = CleanProductName(rawName);

            context.Debug($"{Brand}：{cleanName} 直读成功，电量={level}%，充电={(charging ? "是" : "否")}，workMode={workMode}");

            result.Add(new DeviceBattery
            {
                Id = $"mchose:{hid.VendorId}:{hid.ProductId}:{cleanName}",
                Name = cleanName,
                Level = level,
                Charging = charging ? true : null,
                Kind = DeviceBattery.GuessKind(cleanName) is var guess && guess != PeripheralKind.Other
                    ? guess
                    : PeripheralKind.Keyboard,
                Link = workMode == 1 ? PeripheralLink.Wired : PeripheralLink.Usb24G,
                Source = "HID 直读",
                Stale = false,
            });
        }
    }

    private static byte[] BuildGlwGetFuncConfigPacket(ushort offset, byte size)
    {
        var packet = new byte[GlwReportLength];
        packet[0] = 0x00; // Report ID
        packet[1] = GlwRequestFlag;
        packet[2] = GlwCmdGetFuncConfig;
        packet[3] = 0x00;
        packet[5] = size;
        packet[6] = (byte)(offset & 0xFF);
        packet[7] = (byte)((offset >> 8) & 0xFF);
        packet[8] = 0x00;
        packet[4] = (byte)((packet[5] + packet[6] + packet[7] + packet[8]) & 0xFF);
        return packet;
    }

    private static bool IsGlwStopFrame(byte[] rx) =>
        rx.Length >= 5
        && rx[0] == 0x00
        && rx[1] == 0xFA
        && rx[2] == 0xFB
        && rx[3] == 0x06
        && rx[4] == 0x00;

    private static bool IsGlwFuncConfigOrStopFrame(byte[] rx)
    {
        if (IsGlwStopFrame(rx)) return true;
        return rx.Length >= 44
               && rx[0] == 0x00
               && rx[1] == GlwResponseFlag
               && rx[2] == GlwCmdGetFuncConfig
               && rx[5] <= GlwFuncConfigSize
               && rx[6] == 0x00
               && rx[7] == 0x00;
    }

    private static string CleanProductName(string name)
    {
        if (name.EndsWith(" 2.4G", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = name[..^5].Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return name;
    }

    private void CollectViaHubCache(
        DeviceSourceContext context,
        List<DeviceBattery> result,
        HashSet<(int Vid, int Pid)> handledHidProducts)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MCHOSEHUB", "files", "config", "mc_main_store_key.json");

        if (!File.Exists(path))
        {
            return;
        }

        string json;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            context.Warn($"读取 {Brand} 缓存失败：{ex.Message}");
            return;
        }

        var stale = DateTime.Now - File.GetLastWriteTime(path) > StaleAfter;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("webIndexList", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in list.EnumerateArray())
            {
                var vendorId = ReadInt(item, "vendorId");
                var productId = ReadInt(item, "productId");

                // 已经由 GLW HID 直读处理过的设备不再读取可能过期的 HUB 缓存
                if (handledHidProducts.Contains((vendorId, productId))) continue;

                if (!item.TryGetProperty("batteryLevel", out var levelElement)) continue;
                if (levelElement.ValueKind != JsonValueKind.Number) continue;

                var level = levelElement.GetInt32();
                if (level < 0 || level > 100) continue;

                var productName = ReadString(item, "productName")
                                  ?? ReadString(item, "deviceName")
                                  ?? "MCHOSE 无线外设";

                var supportsBattery = !item.TryGetProperty("featBattery", out var feat)
                                      || feat.ValueKind != JsonValueKind.False;

                var chargeStatus = item.TryGetProperty("chargeStatus", out var cs) && cs.ValueKind == JsonValueKind.Number
                    ? cs.GetInt32()
                    : 0;

                var kind = item.TryGetProperty("deviceType", out var dt) && dt.ValueKind == JsonValueKind.Number
                    ? MapDeviceType(dt.GetInt32())
                    : DeviceBattery.GuessKind(productName);

                result.Add(new DeviceBattery
                {
                    Id = $"mchose:{vendorId}:{productId}:{productName}",
                    Name = productName,
                    Level = supportsBattery ? level : null,
                    Charging = chargeStatus > 0 ? true : null,
                    Kind = kind,
                    Link = PeripheralLink.Usb24G,
                    Source = stale ? $"{Brand} HUB 缓存（较旧）" : $"{Brand} HUB",
                    Stale = stale,
                });
            }
        }
        catch (JsonException ex)
        {
            context.Warn($"解析 {Brand} 缓存失败：{ex.Message}");
        }
    }

    private static string? ReadString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    /// <summary>HUB 自己的 deviceType 编号（1 键盘 / 2 鼠标 / 3 耳机）。</summary>
    private static PeripheralKind MapDeviceType(int deviceType) => deviceType switch
    {
        1 => PeripheralKind.Keyboard,
        2 => PeripheralKind.Mouse,
        3 => PeripheralKind.Headset,
        _ => PeripheralKind.Other,
    };
}
