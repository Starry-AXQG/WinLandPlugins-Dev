using System.Text;
using System.Text.Json;

namespace HidBatteryProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var autoMode = args.Any(a => string.Equals(a, "--auto", StringComparison.OrdinalIgnoreCase));
        var configPath = ResolveConfigPath(args);

        PrintBanner();

        ProbeConfig? config = null;
        if (configPath is not null && File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath, Encoding.UTF8);
                config = JsonSerializer.Deserialize(json, ProbeJsonContext.Default.ProbeConfig);
                WriteColored(ConsoleColor.Cyan, $"[配置] 已加载探测规则：{Path.GetFileName(configPath)}");
                WriteColored(ConsoleColor.Cyan, $"       目标品牌：{config?.Brand} | 型号参考：{config?.ModelHint}");
                if (!string.IsNullOrWhiteSpace(config?.Notes))
                    Console.WriteLine($"       说明：{config.Notes}");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                WriteColored(ConsoleColor.Yellow, $"[警告] 读取配置文件失败（{ex.Message}），将退回纯被动硬件枚举模式。");
            }
        }
        else
        {
            WriteColored(ConsoleColor.Yellow, "[模式] 未找到 probe-config.json，将以「纯只读硬件快照模式」运行（不发送任何报文）。");
            Console.WriteLine();
        }

        // 1. 枚举本机全部 HID 接口并匹配候选通道
        List<HidDeviceInfo> allDevices;
        List<HidDeviceInfo> matchedDevices;

        while (true)
        {
            allDevices = NativeHid.EnumerateAll();
            matchedDevices = MatchTargets(allDevices, config);

            Console.WriteLine($"[扫描] 本机共枚举到 {allDevices.Count} 个 HID 接口。");

            if (config is not null && config.Targets.Count > 0)
            {
                if (matchedDevices.Count > 0)
                {
                    WriteColored(ConsoleColor.Green, $"[命中] 发现 {matchedDevices.Count} 个属于「{config.Brand}」的目标接口：");
                    foreach (var d in matchedDevices)
                    {
                        Console.WriteLine($"  · {d.VendorId}:{d.ProductId} | Usage={d.UsagePage}:{d.Usage} | In={d.InputReportLength} Out={d.OutputReportLength} Feat={d.FeatureReportLength} | RW={d.CanOpenReadWrite} | \"{d.ProductName}\" ({d.InterfaceSlot})");
                    }
                    Console.WriteLine();
                    break;
                }

                WriteColored(ConsoleColor.Yellow, $"[提示] 当前尚未检测到「{config.Brand}」接收器或设备接口。");
                if (autoMode) break;

                Console.Write("请确认 2.4G 接收器已插在电脑 USB 口上，按【回车键】重新扫描（或输入 c 回车直接导出全机快照）：");
                var retryInput = (Console.ReadLine() ?? "").Trim();
                if (string.Equals(retryInput, "c", StringComparison.OrdinalIgnoreCase))
                    break;
                Console.WriteLine();
            }
            else
            {
                break;
            }
        }

        var report = new ProbeReport
        {
            CapturedAtUtc = DateTime.UtcNow.ToString("O"),
            OsVersion = Environment.OSVersion.VersionString,
            TargetBrand = config?.Brand ?? "",
            TargetModel = config?.ModelHint ?? "",
            DriverSource = config?.DriverSource ?? "",
            AllHidDevices = allDevices,
            MatchedTargetDevices = matchedDevices,
        };

        if (!autoMode)
        {
            if (string.IsNullOrWhiteSpace(report.TargetBrand))
            {
                Console.Write("请输入待适配的外设品牌与型号（例如 雷柏 VT9PRO）：");
                report.TargetModel = (Console.ReadLine() ?? "").Trim();
                report.TargetBrand = report.TargetModel;
            }

            Console.Write("请输入当前在官方驱动中看到的电量百分比（如 85，方便核对数据位；不确定可直接回车）：");
            report.UserReportedBattery = (Console.ReadLine() ?? "").Trim();

            Console.Write("补充说明（例如：原装 1K 接收器 / V2M 4K 接收器，可留空直接回车）：");
            report.UserNotes = (Console.ReadLine() ?? "").Trim();
            Console.WriteLine();
        }

        // 2. 若存在主动探测规则且有匹配目标（或后续插线可能出现目标），执行三态抓取
        if (config is { Actions.Count: > 0 })
        {
            if (autoMode)
            {
                if (matchedDevices.Count > 0)
                {
                    report.Phases.Add(RunPhase("auto_snapshot", "自动单轮探测", config, userNote: "auto"));
                }
            }
            else
            {
                // 阶段 1：无线唤醒态
                WritePhaseHeader("阶段 1 / 3 ：无线正常唤醒状态");
                Console.WriteLine("操作指引：");
                Console.WriteLine("  1. 确保 2.4G 接收器插在电脑上，鼠标底部电源开关已打开；");
                Console.WriteLine("  2. 先晃动几下鼠标（确保它处于唤醒未休眠状态），然后立即按【回车键】（抓包期间也可轻轻晃动鼠标）。");
                Console.Write("准备好后请按【回车键】开始抓取（输入 s 回车可跳过）：");
                var p1 = (Console.ReadLine() ?? "").Trim();
                if (!string.Equals(p1, "s", StringComparison.OrdinalIgnoreCase))
                    report.Phases.Add(RunPhase("awake_wireless", "无线正常唤醒状态", config, p1));
                else
                    report.Phases.Add(SkippedPhase("awake_wireless", "无线正常唤醒状态"));

                // 阶段 2：插线充电态
                WritePhaseHeader("阶段 2 / 3 ：插线充电状态（用于锁定充电标志位，强烈建议执行）");
                Console.WriteLine("操作指引：");
                Console.WriteLine("  1. 保持 2.4G 接收器插在电脑上，同时拿一根数据/充电线插到鼠标上使其进入充电状态；");
                Console.WriteLine("  2. 插上等待约 2～3 秒稳定后，按【回车键】开始抓取。");
                Console.Write("插好线后请按【回车键】开始抓取（如不方便插线，输入 s 回车可跳过）：");
                var p2 = (Console.ReadLine() ?? "").Trim();
                if (!string.Equals(p2, "s", StringComparison.OrdinalIgnoreCase))
                    report.Phases.Add(RunPhase("charging", "插线充电状态", config, p2));
                else
                    report.Phases.Add(SkippedPhase("charging", "插线充电状态"));

                // 阶段 3：关机 / 离线态
                WritePhaseHeader("阶段 3 / 3 ：外设关机 / 离线状态（用于防闪烁与离线判据）");
                Console.WriteLine("操作指引：");
                Console.WriteLine("  1. 如果刚才插了充电线请先拔掉充电线（注意：2.4G 接收器继续留在电脑 USB 口上不要拔！）；");
                Console.WriteLine("  2. 将鼠标底部的电源开关拨到【关闭 OFF】，等待 2 秒后按【回车键】开始抓取。");
                Console.Write("关掉鼠标电源后请按【回车键】开始抓取（输入 s 回车可跳过）：");
                var p3 = (Console.ReadLine() ?? "").Trim();
                if (!string.Equals(p3, "s", StringComparison.OrdinalIgnoreCase))
                    report.Phases.Add(RunPhase("powered_off", "外设关机/离线状态", config, p3));
                else
                    report.Phases.Add(SkippedPhase("powered_off", "外设关机/离线状态"));
            }
        }

        // 3. 输出报告文件
        var safeBrand = SanitizeFileToken(string.IsNullOrWhiteSpace(report.TargetBrand) ? "device" : report.TargetBrand);
        var fileName = $"hid-probe-report-{safeBrand}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var outputPath = Path.Combine(AppContext.BaseDirectory, fileName);

        var outJson = JsonSerializer.Serialize(report, ProbeJsonContext.Default.ProbeReport);
        File.WriteAllText(outputPath, outJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine();
        WriteColored(ConsoleColor.Green, "================================================================");
        WriteColored(ConsoleColor.Green, "  抓取完成！报告已保存到本程序所在文件夹：");
        WriteColored(ConsoleColor.White, $"  {outputPath}");
        WriteColored(ConsoleColor.Green, "  请将这个 .json 文件直接发回给插件开发者即可（内含纯协议报文，无个人隐私信息）。");
        WriteColored(ConsoleColor.Green, "================================================================");

        if (!autoMode)
        {
            Console.WriteLine();
            Console.Write("按【回车键】退出窗口...");
            Console.ReadLine();
        }

        return 0;
    }

    private static CapturePhaseResult SkippedPhase(string id, string title) => new()
    {
        PhaseId = id,
        PhaseTitle = title,
        Skipped = true,
        CapturedAtUtc = DateTime.UtcNow.ToString("O"),
    };

    private static CapturePhaseResult RunPhase(string phaseId, string phaseTitle, ProbeConfig config, string userNote)
    {
        Console.WriteLine();
        WriteColored(ConsoleColor.Cyan, $"  -> 正在枚举并执行「{phaseTitle}」抓包，请稍候...");

        var currentAll = NativeHid.EnumerateAll();
        var targets = MatchTargets(currentAll, config);

        var phase = new CapturePhaseResult
        {
            PhaseId = phaseId,
            PhaseTitle = phaseTitle,
            Skipped = false,
            CapturedAtUtc = DateTime.UtcNow.ToString("O"),
            UserNote = userNote,
            PresentTargetDevices = targets,
        };

        if (targets.Count == 0)
        {
            WriteColored(ConsoleColor.Yellow, "  [提示] 本阶段未检测到匹配的目标接口。");
            return phase;
        }

        foreach (var dev in targets)
        {
            foreach (var action in config.Actions)
            {
                if (action.Filter is not null && !action.Filter.Matches(dev))
                    continue;

                var isListen = string.Equals(action.Kind, "listenInput", StringComparison.OrdinalIgnoreCase);
                if (isListen)
                {
                    Console.WriteLine($"     [{dev.VendorId}:{dev.ProductId} {dev.UsagePage}:{dev.Usage}] 正在执行 {action.Id}（监听 {action.TimeoutMs}ms，可轻轻移动鼠标）...");
                }

                var trace = NativeHid.ExecuteAction(dev, action);
                phase.Traces.Add(trace);

                var validFrames = trace.Frames.Where(f => !f.TimedOut && f.ByteLength > 0).ToList();
                if (validFrames.Count > 0)
                {
                    var preview = validFrames[^1].RawHex;
                    if (preview.Length > 72) preview = preview[..72] + " ...";
                    WriteColored(ConsoleColor.Green, $"     ✓ [{dev.VendorId}:{dev.ProductId} {dev.UsagePage}:{dev.Usage}] {action.Id} -> 收到 {validFrames.Count} 帧: {preview}");
                    if (!string.IsNullOrEmpty(trace.DecodedHint))
                        WriteColored(ConsoleColor.Yellow, $"       解析预览: {trace.DecodedHint}");
                }
                else if (!trace.WriteSucceeded)
                {
                    WriteColored(ConsoleColor.DarkGray, $"     - [{dev.VendorId}:{dev.ProductId} {dev.UsagePage}:{dev.Usage}] {action.Id} -> 写入跳过/不支持 (Win32Err={trace.WriteWin32Error})");
                }
                else
                {
                    WriteColored(ConsoleColor.DarkGray, $"     - [{dev.VendorId}:{dev.ProductId} {dev.UsagePage}:{dev.Usage}] {action.Id} -> 超时/无回包 ({trace.TotalElapsedMs}ms)");
                }
            }
        }

        return phase;
    }

    private static List<HidDeviceInfo> MatchTargets(List<HidDeviceInfo> all, ProbeConfig? config)
    {
        if (config is null || config.Targets.Count == 0)
            return new List<HidDeviceInfo>();

        return all
            .Where(dev => config.Targets.Any(t => t.Matches(dev)))
            .ToList();
    }

    private static string? ResolveConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i + 1]);
        }

        var localConfig = Path.Combine(AppContext.BaseDirectory, "probe-config.json");
        if (File.Exists(localConfig)) return localConfig;

        var cwdConfig = Path.Combine(Environment.CurrentDirectory, "probe-config.json");
        if (File.Exists(cwdConfig)) return cwdConfig;

        return null;
    }

    private static string SanitizeFileToken(string input)
    {
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.Length > 0 ? sb.ToString() : "device";
    }

    private static void PrintBanner()
    {
        WriteColored(ConsoleColor.Cyan, "================================================================");
        WriteColored(ConsoleColor.Cyan, "   WinIsland 外设电量插件 · HID 协议日志抓取工具 (HidBatteryProbe)");
        WriteColored(ConsoleColor.Cyan, "================================================================");
        Console.WriteLine("安全性说明：");
        Console.WriteLine("  · 完全离线运行，不访问任何网络，不需要管理员权限；");
        Console.WriteLine("  · 仅枚举 USB/HID 硬件参数并发送只读电量查询包，绝不修改 DPI、宏或固件；");
        Console.WriteLine("  · 自动剥离本机序列号路径，生成的 .json 日志可直接打开审阅。");
        Console.WriteLine();
    }

    private static void WritePhaseHeader(string title)
    {
        Console.WriteLine();
        WriteColored(ConsoleColor.Magenta, $"━━━━━━━━━━━━━━━━━━ {title} ━━━━━━━━━━━━━━━━━━");
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }
}
