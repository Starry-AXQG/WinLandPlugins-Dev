using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MediaDeck.Services;

public sealed record ResolvedAppInfo(string DisplayName, byte[]? IconPngBytes);

/// <summary>
/// Windows Core Audio 默认设备切换（IPolicyConfig COM）、Shell AppsFolder AUMID 解析（与系统媒体管理器同源）、
/// Win32 窗口激活、定向 WM_APPCOMMAND 媒体控制及图标提取辅助。
/// </summary>
internal static class PolicyConfigInterop
{
    public const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    public const byte VK_MEDIA_PREV_TRACK = 0xB1;
    public const byte VK_MEDIA_STOP = 0xB2;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    public const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    public const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    public const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;

    private const uint WM_APPCOMMAND = 0x0319;

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClientComObject { }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    [ComImport]
    [Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    public static bool SetDefaultAudioEndpoint(string deviceId)
    {
        try
        {
            var comObj = new PolicyConfigClientComObject();
            if (comObj is IPolicyConfig client)
            {
                // 优先切换 Multimedia 角色（系统枚举默认设备以此为准），再切 Console 与 Communications
                int hr1 = client.SetDefaultEndpoint(deviceId, ERole.Multimedia);
                int hr2 = client.SetDefaultEndpoint(deviceId, ERole.Console);
                int hr3 = client.SetDefaultEndpoint(deviceId, ERole.Communications);
                if (hr1 == 0 || hr2 == 0 || hr3 == 0) return true;
            }
            if (comObj is IPolicyConfigVista vista)
            {
                int hr1 = vista.SetDefaultEndpoint(deviceId, ERole.Multimedia);
                int hr2 = vista.SetDefaultEndpoint(deviceId, ERole.Console);
                int hr3 = vista.SetDefaultEndpoint(deviceId, ERole.Communications);
                return hr1 == 0 || hr2 == 0 || hr3 == 0;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ---- Shell AppsFolder AUMID 解析（解析 Zen/Firefox 哈希 AUMID、Spotify Store 应用等）----

    private static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private const uint SIIGBF_RESIZETOFIT = 0x00000000;
    private const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const uint SIIGBF_ICONONLY = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemInKnownFolder(
        ref Guid kfid,
        uint dwKFFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string pszItem,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    /// <summary>
    /// 通过 Windows Shell AppsFolder 直接解析任意 AUMID（含 Firefox/Zen 16位哈希 AUMID、UWP/MSIX 应用）
    /// 为官方显示名与图标 PNG 字节流，与 Windows 系统媒体管理器同源。
    /// </summary>
    public static async Task<ResolvedAppInfo?> ResolveFromAppsFolderAsync(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;

        try
        {
            var folderId = FolderIdAppsFolder;
            var iid = IidIShellItem;
            int hr = SHCreateItemInKnownFolder(ref folderId, 0, aumid, ref iid, out var obj);
            if (hr != 0 || obj is not IShellItem item) return null;

            string displayName = "";
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var name) == 0 && !string.IsNullOrWhiteSpace(name))
            {
                displayName = name.Trim();
            }

            byte[]? iconPng = null;
            if (obj is IShellItemImageFactory factory)
            {
                var sz = new SIZE { cx = 48, cy = 48 };
                int hrImg = factory.GetImage(sz, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hbm);
                if (hrImg != 0 || hbm == IntPtr.Zero)
                {
                    hrImg = factory.GetImage(sz, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out hbm);
                }

                if (hrImg == 0 && hbm != IntPtr.Zero)
                {
                    try
                    {
                        iconPng = await HBitmapToPngAsync(hbm).ConfigureAwait(false);
                    }
                    finally
                    {
                        DeleteObject(hbm);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(displayName) && iconPng is null) return null;
            return new ResolvedAppInfo(displayName, iconPng);
        }
        catch
        {
            return null;
        }
    }

    // ---- 全局媒体按键 & 定向播放器控制 ----

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12; // Alt
    private const byte VK_LEFT = 0x25;
    private const byte VK_RIGHT = 0x27;
    private const byte VK_P = 0x50;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    public static void SendMediaKey(byte vk)
    {
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void SendCtrlAltKey(byte vk, bool extended = false)
    {
        uint extFlag = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, extFlag, UIntPtr.Zero);
        keybd_event(vk, 0, extFlag | KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// 控制非 SMTC 兜底播放器：
    ///   - 网易云音乐（cloudmusic）若收到未处理的 WM_APPCOMMAND，会由 DefWindowProc 转发给系统 Shell Hook (HSHELL_APPCOMMAND)，
    ///     从而错误唤醒当前活跃的 SMTC 播放器（如 QQ 音乐）！
    ///   - 因此对网易云直接触发其内置全局热键（Ctrl+Alt+P 播放/暂停，Ctrl+Alt+Left/Right 上/下一首），由系统 RegisterHotKey
    ///     直接路由给网易云，绝不触发 QQ 音乐。
    /// </summary>
    public static bool ControlFallbackPlayer(string processName, int processId, int appCommand)
    {
        if (processName.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
        {
            switch (appCommand)
            {
                case APPCOMMAND_MEDIA_PLAY_PAUSE:
                    SendCtrlAltKey(VK_P, extended: false);
                    return true;
                case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                    SendCtrlAltKey(VK_LEFT, extended: true);
                    return true;
                case APPCOMMAND_MEDIA_NEXTTRACK:
                    SendCtrlAltKey(VK_RIGHT, extended: true);
                    return true;
            }
        }

        if (processId <= 0) return false;
        IntPtr hwnd = FindMainWindowForProcessGroup(processId, processName);
        if (hwnd == IntPtr.Zero) return false;

        IntPtr lParam = (IntPtr)(appCommand << 16);
        return PostMessage(hwnd, WM_APPCOMMAND, hwnd, lParam);
    }

    // ---- 激活来源程序窗口 & 任务栏/窗口闪烁提醒 ----

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const uint FLASHW_ALL = 0x00000003;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private static IntPtr _lastActivatedHwnd = IntPtr.Zero;
    private static DateTimeOffset _lastActivatedAt = DateTimeOffset.MinValue;

    public static void FlashWindowAndTaskbar(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hWnd,
            dwFlags = FLASHW_ALL,
            uCount = 3,
            dwTimeout = 75,
        };
        FlashWindowEx(ref info);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    private const uint GW_OWNER = 4;

    public static string GetWindowClassName(IntPtr hWnd)
    {
        var buf = new char[256];
        int len = GetClassName(hWnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : string.Empty;
    }

    private static bool IsDummySystemWindow(IntPtr hWnd, string title, string className)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        if (title.Equals("Default IME", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("GDI+ Window", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("GDI+ Window", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("MS_WebcheckMonitor", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("OleMainThreadWndName", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("DWM Notification Window", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (className.Equals("IME", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("CiceroUIWndFrame", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("DummyDWMListenerWindow", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Chrome_SystemMessageWindow", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Chrome_MessageWindow", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("OleMainThreadWndClass", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// 过滤掉 0x0 辅助/托盘/消息窗口与 ToolWindow，避免软件主窗口被关闭后错误命中隐藏假窗口。
    /// </summary>
    private static bool IsRealCandidateWindow(IntPtr hWnd, string className, bool isVisible)
    {
        // 已知播放器主窗口类名直接放行
        if (className.Equals("OrpheusBrowserHost", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("TXGuiFoundation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (GetWindowRect(hWnd, out var rc))
        {
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (!isVisible)
            {
                // 隐藏窗口必须具备合理的主窗口尺寸，过滤 0x0 / 1x1 后台辅助窗
                if (w < 120 || h < 80) return false;
            }
            else if (!IsIconic(hWnd))
            {
                if (w < 80 || h < 60) return false;
            }
        }
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    public static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var buf = new char[len + 1];
        int copied = GetWindowText(hWnd, buf, buf.Length);
        return copied > 0 ? new string(buf, 0, copied) : string.Empty;
    }

    public static bool ActivateWindowByProcessIdOrName(
        int processId,
        string? hintProcessName,
        string? displayName = null,
        string? exePath = null,
        bool hasAppsFolderEntry = false)
    {
        IntPtr targetHwnd = IntPtr.Zero;
        IntPtr fallbackHiddenHwnd = IntPtr.Zero;

        // 1) 构建所有候选进程名列表（兼容 Zen/Firefox/Spotify/网易云/Edge 等多进程应用）
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(hintProcessName))
        {
            candidates.Add(CleanProcessHint(hintProcessName));
        }
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var low = displayName.Trim().ToLowerInvariant();
            if (low.Contains("zen")) candidates.Add("zen");
            if (low.Contains("firefox")) candidates.Add("firefox");
            if (low.Contains("spotify")) candidates.Add("Spotify");
            if (low.Contains("edge")) candidates.Add("msedge");
            if (low.Contains("chrome")) candidates.Add("chrome");
            if (low.Contains("qq")) candidates.Add("QQMusic");
            if (low.Contains("网易")) candidates.Add("cloudmusic");
            candidates.Add(displayName.Trim().Replace(" ", ""));
        }

        // 2) 优先按已知主窗口类名精确定位（如网易云音乐 OrpheusBrowserHost）
        if (candidates.Any(c => c.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)))
        {
            IntPtr orpheus = FindWindow("OrpheusBrowserHost", null);
            if (orpheus != IntPtr.Zero)
            {
                if (IsWindowVisible(orpheus)) targetHwnd = orpheus;
                else fallbackHiddenHwnd = orpheus;
            }
        }

        // 3) 先在指定 PID 上找可见主窗口（绝不把子进程的隐藏 Default IME 窗口当成主窗口）
        if (targetHwnd == IntPtr.Zero && processId > 0)
        {
            targetHwnd = FindMainWindowForPid((uint)processId, visibleOnly: true);
            if (fallbackHiddenHwnd == IntPtr.Zero)
            {
                fallbackHiddenHwnd = FindMainWindowForPid((uint)processId, visibleOnly: false);
            }
        }

        // 4) 若指定 PID 是无界面的音频/渲染子进程，遍历同名所有进程寻找真正的可见主窗口
        if (targetHwnd == IntPtr.Zero)
        {
            foreach (var name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var vis = FindMainWindowForPid((uint)proc.Id, visibleOnly: true);
                        if (vis != IntPtr.Zero)
                        {
                            targetHwnd = vis;
                            break;
                        }
                        if (fallbackHiddenHwnd == IntPtr.Zero)
                        {
                            fallbackHiddenHwnd = FindMainWindowForPid((uint)proc.Id, visibleOnly: false);
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
                if (targetHwnd != IntPtr.Zero) break;
            }
        }

        // 5) 命中可见主窗口：直接激活或闪烁提醒
        if (targetHwnd != IntPtr.Zero)
        {
            IntPtr currentFg = GetForegroundWindow();
            bool alreadyActive = !IsIconic(targetHwnd) && IsWindowVisible(targetHwnd) &&
                (currentFg == targetHwnd ||
                 (targetHwnd == _lastActivatedHwnd && (DateTimeOffset.UtcNow - _lastActivatedAt).TotalSeconds < 6));

            if (alreadyActive)
            {
                BringWindowToFront(targetHwnd);
                FlashWindowAndTaskbar(targetHwnd);
                _lastActivatedHwnd = targetHwnd;
                _lastActivatedAt = DateTimeOffset.UtcNow;
                return true;
            }

            bool ok = BringWindowToFront(targetHwnd);
            _lastActivatedHwnd = targetHwnd;
            _lastActivatedAt = DateTimeOffset.UtcNow;
            return ok;
        }

        // 6) 若只有隐藏主窗口（如最小化到托盘），尝试恢复并校验是否真的变为可见
        if (fallbackHiddenHwnd != IntPtr.Zero)
        {
            BringWindowToFront(fallbackHiddenHwnd);
            if (IsWindowVisible(fallbackHiddenHwnd))
            {
                _lastActivatedHwnd = fallbackHiddenHwnd;
                _lastActivatedAt = DateTimeOffset.UtcNow;
                return true;
            }
        }

        // 7) 窗口已被关闭/销毁（或隐藏窗口拒绝直接 ShowWindow）：通过 Shell AppsFolder AUMID 或可执行文件重新拉起/唤醒单实例主窗口
        return TryRelaunchOrActivateApp(hintProcessName, hasAppsFolderEntry, exePath, candidates);
    }

    private static bool TryRelaunchOrActivateApp(
        string? rawAppId,
        bool hasAppsFolderEntry,
        string? exePath,
        List<string> candidateProcessNames)
    {
        // 7.1 优先通过 Windows Shell AppsFolder 激活（原生支持 UWP/MSIX、Store 版 Spotify、Zen/Firefox 哈希 AUMID 等）
        if (!string.IsNullOrWhiteSpace(rawAppId) && (hasAppsFolderEntry || rawAppId.Contains('!')))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{rawAppId}",
                    UseShellExecute = true,
                });
                return true;
            }
            catch { }
        }

        // 7.2 桌面单实例程序（网易云、QQ音乐、PotPlayer 等）：重新拉起主 exe 即可唤醒托盘实例或新开窗口
        string? resolvedExe = exePath;
        if (string.IsNullOrWhiteSpace(resolvedExe) || !File.Exists(resolvedExe))
        {
            resolvedExe = FindExePathFromRunningProcesses(candidateProcessNames);
        }

        if (!string.IsNullOrWhiteSpace(resolvedExe) && File.Exists(resolvedExe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = resolvedExe,
                    UseShellExecute = true,
                });
                return true;
            }
            catch { }
        }

        return false;
    }

    private static string? FindExePathFromRunningProcesses(List<string> candidates)
    {
        foreach (var name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var path = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            return path;
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>针对多进程架构播放器（如网易云音乐音频子进程与 UI 主进程分离），按 PID + 进程名联合查找主窗口。</summary>
    public static IntPtr FindMainWindowForProcessGroup(int pid, string? processName)
    {
        if (!string.IsNullOrWhiteSpace(processName) &&
            processName.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr orpheus = FindWindow("OrpheusBrowserHost", null);
            if (orpheus != IntPtr.Zero) return orpheus;
        }

        if (pid > 0)
        {
            IntPtr byPid = FindMainWindowForPid((uint)pid);
            if (byPid != IntPtr.Zero) return byPid;
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            foreach (var p in Process.GetProcessesByName(CleanProcessHint(processName)))
            {
                try
                {
                    IntPtr h = p.MainWindowHandle != IntPtr.Zero
                        ? p.MainWindowHandle
                        : FindMainWindowForPid((uint)p.Id);
                    if (h != IntPtr.Zero) return h;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        return IntPtr.Zero;
    }

    public static IntPtr FindMainWindowForPid(uint pid, bool visibleOnly = false)
    {
        IntPtr bestVisible = IntPtr.Zero;
        IntPtr bestHidden = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint winPid);
            if (winPid != pid) return true;

            var title = GetWindowTitle(hWnd);
            var cls = GetWindowClassName(hWnd);
            if (IsDummySystemWindow(hWnd, title, cls)) return true;

            bool visible = IsWindowVisible(hWnd);
            if (!IsRealCandidateWindow(hWnd, cls, visible)) return true;

            if (visible)
            {
                // 优先无 Owner 的顶层主窗口
                if (GetWindow(hWnd, GW_OWNER) == IntPtr.Zero)
                {
                    bestVisible = hWnd;
                    return false;
                }
                if (bestVisible == IntPtr.Zero) bestVisible = hWnd;
                return true;
            }

            if (!visibleOnly && bestHidden == IntPtr.Zero && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero)
            {
                bestHidden = hWnd;
            }
            return true;
        }, IntPtr.Zero);

        return bestVisible != IntPtr.Zero ? bestVisible : (visibleOnly ? IntPtr.Zero : bestHidden);
    }

    public static bool BringWindowToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        else ShowWindow(hWnd, SW_SHOW);

        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint curThread = GetCurrentThreadId();

        bool attached = false;
        try
        {
            // 发送一次无副作用的 Alt 释放事件，解除 Windows 对非激活窗口 (WS_EX_NOACTIVATE) 进程的前台焦点锁定
            keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);

            if (fgThread != 0 && fgThread != curThread)
            {
                attached = AttachThreadInput(curThread, fgThread, true);
            }

            BringWindowToTop(hWnd);
            bool ok = SetForegroundWindow(hWnd);
            SwitchToThisWindow(hWnd, true);
            return ok || GetForegroundWindow() == hWnd;
        }
        finally
        {
            if (attached) AttachThreadInput(curThread, fgThread, false);
        }
    }

    public static string CleanProcessHint(string raw)
    {
        var s = raw.Trim();
        int bang = s.IndexOf('!');
        if (bang >= 0 && bang + 1 < s.Length) s = s[(bang + 1)..];
        int slash = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        if (slash >= 0 && slash + 1 < s.Length) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s;
    }

    // ---- 图标 HBITMAP / EXE 提取为 PNG ----

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public short bmPlanes;
        public short bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    private static async Task<byte[]?> HBitmapToPngAsync(IntPtr hBitmap)
    {
        if (hBitmap == IntPtr.Zero) return null;
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;

        int width = bmp.bmWidth;
        int height = bmp.bmHeight;
        if (width <= 0 || height <= 0) return null;

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        byte[] pixels = new byte[width * height * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            int lines = GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref bmi, 0);
            if (lines == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        using var mem = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        mem.Seek(0);
        var bytes = new byte[mem.Size];
        await mem.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    public static async Task<byte[]?> ExtractExeIconPngAsync(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;

        SHFILEINFO shinfo = new();
        IntPtr res = SHGetFileInfo(exePath, 0, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero) return null;

        try
        {
            if (!GetIconInfo(shinfo.hIcon, out var iconInfo)) return null;
            try
            {
                if (iconInfo.hbmColor == IntPtr.Zero) return null;
                return await HBitmapToPngAsync(iconInfo.hbmColor).ConfigureAwait(false);
            }
            finally
            {
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }
}
