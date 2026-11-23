using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Principal;
using Microsoft.Win32;

namespace HwSensors.Sensors;

/// <summary>
/// 检测管理员权限与 PawnIO 驱动状态，并提供官方安装包的一键下载安装与一键干净卸载。
/// 临时下载的安装包统一写入系统 TEMP 目录并在完成后自动删除。
/// </summary>
internal static class PawnIoHelper
{
    public const string OfficialHomepage = "https://pawnio.eu";
    public const string OfficialDownloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    private const int ErrorCancelled = 1223; // 用户取消了 UAC 提权弹窗

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    static PawnIoHelper()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("HwSensors-WinIslandPlugin/1.0");
    }

    /// <summary>当前 WinIsland 进程是否以管理员权限运行。</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>系统是否已安装 PawnIO 内核驱动服务（普通权限可读）。</summary>
    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>一键从官方 GitHub Releases 下载 PawnIO_setup.exe 并静默安装（会触发标准 UAC 确认）。</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(Action<string> reportProgress, CancellationToken ct = default)
    {
        var tempExe = Path.Combine(Path.GetTempPath(), $"PawnIO_setup_{Guid.NewGuid():N}.exe");

        try
        {
            reportProgress("正在从官方源下载 PawnIO 安装包…");
            await DownloadSetupAsync(tempExe, ct);

            reportProgress("正在安装 PawnIO（请在弹出的系统管理员授权窗口点「是」）…");
            var (code, err) = await RunElevatedAsync(tempExe, "-install -silent", ct);
            if (err is not null) return (false, err);

            if (IsPawnIoInstalled())
            {
                var adminTip = IsRunningAsAdmin()
                    ? "当前已是管理员权限，传感器已重新加载。"
                    : "驱动已装好；请以管理员身份重启 WinIsland（或在 WinIsland「通用」设置开启「管理员权限自启动」）即可读取精确 CPU 温度。";
                return (true, $"PawnIO 安装成功！{adminTip}");
            }

            return (false, $"安装程序已退出（退出码 {code}），但未检测到 PawnIO 服务，请重试或前往官网手动安装。");
        }
        catch (OperationCanceledException)
        {
            return (false, "操作已取消。");
        }
        catch (Exception ex)
        {
            return (false, $"下载或安装失败：{ex.Message}（可点击「打开官网」手动下载安装）");
        }
        finally
        {
            TryDelete(tempExe);
        }
    }

    /// <summary>一键干净卸载 PawnIO 驱动并恢复原状（会触发标准 UAC 确认）。</summary>
    public static async Task<(bool Ok, string Message)> UninstallAsync(Action<string> reportProgress, CancellationToken ct = default)
    {
        string? downloadedTemp = null;

        try
        {
            var exePath = FindLocalUninstaller();
            if (exePath is null)
            {
                downloadedTemp = Path.Combine(Path.GetTempPath(), $"PawnIO_setup_{Guid.NewGuid():N}.exe");
                reportProgress("本地未找到卸载程序，正在获取官方安装包以执行卸载…");
                await DownloadSetupAsync(downloadedTemp, ct);
                exePath = downloadedTemp;
            }

            reportProgress("正在卸载 PawnIO（请在弹出的系统管理员授权窗口点「是」）…");
            var (code, err) = await RunElevatedAsync(exePath, "-uninstall -silent", ct);
            if (err is not null) return (false, err);

            // 等待服务注销落盘
            await Task.Delay(500, ct);

            if (!IsPawnIoInstalled())
            {
                return (true, "PawnIO 已彻底卸载，已恢复原有免驱动行为。");
            }

            return (false, $"卸载程序已退出（退出码 {code}），但系统仍检测到 PawnIO 服务（可能被其他硬件监控软件占用，请先关闭后重试）。");
        }
        catch (OperationCanceledException)
        {
            return (false, "操作已取消。");
        }
        catch (Exception ex)
        {
            return (false, $"卸载失败：{ex.Message}");
        }
        finally
        {
            if (downloadedTemp is not null) TryDelete(downloadedTemp);
        }
    }

    /// <summary>查找系统已安装的 PawnIO 卸载程序。</summary>
    private static string? FindLocalUninstaller()
    {
        // 1. 常见安装路径
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIO_setup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "setup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "uninstall.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        // 2. 注册表卸载项
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            var raw = key?.GetValue("UninstallString") as string;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var trimmed = raw.Trim();
                string exe;
                if (trimmed.StartsWith('"'))
                {
                    var endQuote = trimmed.IndexOf('"', 1);
                    exe = endQuote > 1 ? trimmed[1..endQuote] : trimmed.Trim('"');
                }
                else
                {
                    var firstSpace = trimmed.IndexOf(' ');
                    exe = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
                }

                if (File.Exists(exe)) return exe;
            }
        }
        catch
        {
            // 忽略注册表读取异常
        }

        return null;
    }

    private static async Task DownloadSetupAsync(string destPath, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(OfficialDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, ct);
    }

    private static async Task<(int ExitCode, string? Error)> RunElevatedAsync(string exePath, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (-1, "已取消管理员授权，未做任何更改。");
        }

        if (proc is null) return (-1, "未能启动安装程序。");

        using (proc)
        {
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, null);
        }
    }

    public static void OpenOfficialHomepage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OfficialHomepage,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 忽略打开浏览器异常
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略临时文件清理失败
        }
    }
}
