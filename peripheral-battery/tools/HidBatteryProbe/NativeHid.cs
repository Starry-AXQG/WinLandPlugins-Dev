using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HidBatteryProbe;

/// <summary>
/// Win32 SetupAPI + hid.dll 底层探测引擎：
/// 支持全量枚举、队列刷新、Output->Input、Output->GetFeature、SetFeature->GetFeature、纯 GetFeature 与纯被动监听。
/// </summary>
internal static class NativeHid
{
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FlushQueue(SafeFileHandle h);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int numBytesToWrite, out int numBytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int numBytesToRead, out int numBytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr lpOverlapped);

    public static List<HidDeviceInfo> EnumerateAll()
    {
        var list = new List<HidDeviceInfo>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set.ToInt64() == -1)
            return list;

        try
        {
            for (var index = 0; ; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data))
                    break;

                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required <= 0) continue;

                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero))
                        continue;

                    var path = Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + 4));
                    if (string.IsNullOrEmpty(path)) continue;

                    var info = DescribeInterface(path);
                    if (info is not null) list.Add(info);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return list
            .OrderBy(d => d.VendorIdValue)
            .ThenBy(d => d.ProductIdValue)
            .ThenBy(d => d.InterfaceSlot, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HidDeviceInfo? DescribeInterface(string path)
    {
        using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(handle, ref attrs)) return null;

        var caps = new HIDP_CAPS();
        var hasCaps = false;
        if (HidD_GetPreparsedData(handle, out var preparsed))
        {
            try
            {
                hasCaps = HidP_GetCaps(preparsed, ref caps) == HIDP_STATUS_SUCCESS;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        using var rwHandle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        var rwOk = !rwHandle.IsInvalid;
        var rwErr = rwOk ? 0 : Marshal.GetLastWin32Error();

        var usagePage = hasCaps ? caps.UsagePage : (ushort)0;
        var usage = hasCaps ? caps.Usage : (ushort)0;

        return new HidDeviceInfo
        {
            RawPath = path,
            VendorIdValue = attrs.VendorID,
            ProductIdValue = attrs.ProductID,
            UsagePageValue = usagePage,
            UsageValue = usage,
            InterfaceSlot = ExtractPrivacySafeSlot(path),
            VendorId = $"0x{attrs.VendorID:X4}",
            ProductId = $"0x{attrs.ProductID:X4}",
            VersionNumber = $"0x{attrs.VersionNumber:X4}",
            Manufacturer = ReadHidString(handle, isProduct: false),
            ProductName = ReadHidString(handle, isProduct: true),
            UsagePage = $"0x{usagePage:X4}",
            Usage = $"0x{usage:X4}",
            InputReportLength = hasCaps ? caps.InputReportByteLength : 0,
            OutputReportLength = hasCaps ? caps.OutputReportByteLength : 0,
            FeatureReportLength = hasCaps ? caps.FeatureReportByteLength : 0,
            CanOpenReadWrite = rwOk,
            OpenReadWriteWin32Error = rwErr,
        };
    }

    /// <summary>
    /// 仅提取形如 "vid_24ae&amp;pid_185a&amp;mi_01&amp;col02" 的硬件拓扑槽位名，剥离本机实例序列号。
    /// </summary>
    private static string ExtractPrivacySafeSlot(string path)
    {
        var firstHash = path.IndexOf('#');
        if (firstHash < 0 || firstHash + 1 >= path.Length) return "hid";
        var secondHash = path.IndexOf('#', firstHash + 1);
        if (secondHash <= firstHash + 1) return path[(firstHash + 1)..];
        return path[(firstHash + 1)..secondHash];
    }

    private static string ReadHidString(SafeFileHandle handle, bool isProduct)
    {
        try
        {
            var buf = new byte[256];
            var ok = isProduct
                ? HidD_GetProductString(handle, buf, buf.Length)
                : HidD_GetManufacturerString(handle, buf, buf.Length);
            if (!ok) return "";
            var s = Encoding.Unicode.GetString(buf);
            var nul = s.IndexOf('\0');
            return (nul >= 0 ? s[..nul] : s).Trim();
        }
        catch
        {
            return "";
        }
    }

    public static ActionTrace ExecuteAction(HidDeviceInfo dev, ProbeAction action)
    {
        var swTotal = Stopwatch.StartNew();
        var trace = new ActionTrace
        {
            ActionId = action.Id,
            ActionKind = action.Kind,
            InterfaceSlot = dev.InterfaceSlot,
            VidPid = $"{dev.VendorId}:{dev.ProductId}",
            ProductName = dev.ProductName,
            UsagePageUsage = $"{dev.UsagePage}:{dev.Usage}",
        };

        var kind = (action.Kind ?? "outputThenInput").Trim().ToLowerInvariant();
        var expectPrefix = HexUtil.ParseHexBytes(action.ExpectPrefixHex);
        var hasBusyCheck = action.BusyByteIndex is >= 0 && HexUtil.TryParseByte(action.BusyValue, out _);
        HexUtil.TryParseByte(action.BusyValue, out var busyVal);

        using var handle = CreateFile(
            dev.RawPath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            trace.WriteMethod = "CreateFile(RW)";
            trace.WriteSucceeded = false;
            trace.WriteWin32Error = Marshal.GetLastWin32Error();
            trace.TotalElapsedMs = swTotal.ElapsedMilliseconds;
            return trace;
        }

        try
        {
            switch (kind)
            {
                case "listeninput":
                    RunListenInput(handle, dev, action, expectPrefix, trace);
                    break;

                case "getfeature":
                    RunGetFeatureOnly(handle, dev, action, expectPrefix, trace);
                    break;

                case "outputthengetinput":
                    RunOutputThenGetInput(handle, dev, action, expectPrefix, hasBusyCheck, busyVal, trace);
                    break;

                case "outputthenfeature":
                    RunOutputThenFeature(handle, dev, action, expectPrefix, hasBusyCheck, busyVal, trace);
                    break;

                case "setfeaturethengetfeature":
                    RunSetFeatureThenGetFeature(handle, dev, action, expectPrefix, hasBusyCheck, busyVal, trace);
                    break;

                default:
                    RunOutputThenInput(handle, dev, action, expectPrefix, hasBusyCheck, busyVal, trace);
                    break;
            }
        }
        catch (Exception ex)
        {
            trace.DecodedHint = $"执行异常: {ex.GetType().Name}: {ex.Message}";
        }

        trace.DecodedHint ??= TryAutoDecodeHint(trace);
        trace.TotalElapsedMs = swTotal.ElapsedMilliseconds;
        return trace;
    }

    private static byte[] BuildPaddedPacket(byte[] rawPrefix, int targetLength)
    {
        var len = Math.Max(rawPrefix.Length, targetLength);
        if (len <= 0) len = 33;
        var packet = new byte[len];
        Array.Copy(rawPrefix, packet, rawPrefix.Length);
        return packet;
    }

    private static bool SendOutputWithFallback(SafeFileHandle handle, byte[] packet, ActionTrace trace)
    {
        if (WriteFile(handle, packet, packet.Length, out var written, IntPtr.Zero) && written > 0)
        {
            trace.WriteMethod = $"WriteFile({written}B)";
            trace.WriteSucceeded = true;
            trace.WriteWin32Error = 0;
            return true;
        }

        var writeErr = Marshal.GetLastWin32Error();
        if (HidD_SetOutputReport(handle, packet, packet.Length))
        {
            trace.WriteMethod = $"HidD_SetOutputReport({packet.Length}B, WriteFileErr={writeErr})";
            trace.WriteSucceeded = true;
            trace.WriteWin32Error = 0;
            return true;
        }

        var setOutErr = Marshal.GetLastWin32Error();
        trace.WriteMethod = $"WriteFile+SetOutputReport Failed (WriteErr={writeErr}, SetOutErr={setOutErr})";
        trace.WriteSucceeded = false;
        trace.WriteWin32Error = writeErr != 0 ? writeErr : setOutErr;
        return false;
    }

    private static void RunOutputThenInput(
        SafeFileHandle handle,
        HidDeviceInfo dev,
        ProbeAction action,
        byte[] expectPrefix,
        bool hasBusyCheck,
        byte busyVal,
        ActionTrace trace)
    {
        var rawReq = HexUtil.ParseHexBytes(action.RequestHex);
        var outLen = dev.OutputReportLength > 0 ? dev.OutputReportLength : rawReq.Length;
        var packet = BuildPaddedPacket(rawReq, outLen);
        trace.RequestHex = HexUtil.ToHex(packet);

        if (action.FlushQueueBeforeWrite)
            HidD_FlushQueue(handle);

        if (!SendOutputWithFallback(handle, packet, trace))
            return;

        if (action.DelayBeforeReadMs > 0)
            Thread.Sleep(action.DelayBeforeReadMs);

        var inLen = dev.InputReportLength > 0 ? dev.InputReportLength : packet.Length;
        var maxReads = Math.Clamp(action.MaxReads, 1, 16);
        var frameIndex = 0;

        for (var i = 0; i < maxReads; i++)
        {
            var frame = ReadSingleInputWithTimeout(handle, inLen, action.TimeoutMs, ++frameIndex, expectPrefix);
            trace.Frames.Add(frame);
            if (frame.TimedOut || frame.ByteLength == 0)
                break;

            if (frame.MatchedExpectPrefix)
            {
                if (hasBusyCheck && action.BusyByteIndex is { } bIdx && bIdx < frame.ByteLength)
                {
                    var bytes = HexUtil.ParseHexBytes(frame.RawHex);
                    if (bIdx < bytes.Length && bytes[bIdx] == busyVal)
                    {
                        Thread.Sleep(Math.Max(10, action.RetryDelayMs));
                        continue;
                    }
                }
                break;
            }
        }
    }

    private static void RunOutputThenGetInput(
        SafeFileHandle handle,
        HidDeviceInfo dev,
        ProbeAction action,
        byte[] expectPrefix,
        bool hasBusyCheck,
        byte busyVal,
        ActionTrace trace)
    {
        var rawReq = HexUtil.ParseHexBytes(action.RequestHex);
        var outLen = dev.OutputReportLength > 0 ? dev.OutputReportLength : rawReq.Length;
        var packet = BuildPaddedPacket(rawReq, outLen);
        trace.RequestHex = HexUtil.ToHex(packet);

        if (HidD_SetOutputReport(handle, packet, packet.Length))
        {
            trace.WriteMethod = $"HidD_SetOutputReport({packet.Length}B)";
            trace.WriteSucceeded = true;
        }
        else if (!SendOutputWithFallback(handle, packet, trace))
        {
            return;
        }

        if (action.DelayBeforeReadMs > 0)
            Thread.Sleep(action.DelayBeforeReadMs);

        var reportId = HexUtil.TryParseByte(action.ResponseReportId, out var rid)
            ? rid
            : (rawReq.Length > 0 ? rawReq[0] : (byte)0);
        var inLen = dev.InputReportLength > 0 ? dev.InputReportLength : packet.Length;
        var retries = Math.Clamp(action.MaxRetries, 1, 10);

        for (var attempt = 1; attempt <= retries; attempt++)
        {
            var buf = new byte[inLen];
            buf[0] = reportId;
            var sw = Stopwatch.StartNew();
            var ok = HidD_GetInputReport(handle, buf, buf.Length);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            sw.Stop();

            trace.Frames.Add(new ReceivedFrame
            {
                Index = attempt,
                Source = $"GetInputReport(0x{reportId:X2})",
                ElapsedMs = sw.ElapsedMilliseconds,
                TimedOut = false,
                Win32Error = err,
                ByteLength = ok ? buf.Length : 0,
                MatchedExpectPrefix = ok && HexUtil.StartsWithPrefix(buf, expectPrefix),
                RawHex = ok ? HexUtil.ToHex(buf) : "",
            });

            if (!ok) break;

            if (hasBusyCheck && action.BusyByteIndex is { } bIdx && bIdx < buf.Length && buf[bIdx] == busyVal)
            {
                Thread.Sleep(Math.Max(10, action.RetryDelayMs));
                continue;
            }

            break;
        }
    }

    private static void RunOutputThenFeature(
        SafeFileHandle handle,
        HidDeviceInfo dev,
        ProbeAction action,
        byte[] expectPrefix,
        bool hasBusyCheck,
        byte busyVal,
        ActionTrace trace)
    {
        var rawReq = HexUtil.ParseHexBytes(action.RequestHex);
        var outLen = dev.OutputReportLength > 0 ? dev.OutputReportLength : rawReq.Length;
        var packet = BuildPaddedPacket(rawReq, outLen);
        trace.RequestHex = HexUtil.ToHex(packet);

        if (!SendOutputWithFallback(handle, packet, trace))
            return;

        if (action.DelayBeforeReadMs > 0)
            Thread.Sleep(action.DelayBeforeReadMs);

        var featId = HexUtil.TryParseByte(action.ResponseReportId, out var fid)
            ? fid
            : (rawReq.Length > 0 ? rawReq[0] : (byte)0);

        PollFeatureReport(handle, dev, featId, action, expectPrefix, hasBusyCheck, busyVal, trace);
    }

    private static void RunSetFeatureThenGetFeature(
        SafeFileHandle handle,
        HidDeviceInfo dev,
        ProbeAction action,
        byte[] expectPrefix,
        bool hasBusyCheck,
        byte busyVal,
        ActionTrace trace)
    {
        var rawReq = HexUtil.ParseHexBytes(action.RequestHex);
        var featLen = dev.FeatureReportLength > 0 ? dev.FeatureReportLength : rawReq.Length;
        var packet = BuildPaddedPacket(rawReq, featLen);
        trace.RequestHex = HexUtil.ToHex(packet);

        if (!HidD_SetFeature(handle, packet, packet.Length))
        {
            trace.WriteMethod = $"HidD_SetFeature({packet.Length}B)";
            trace.WriteSucceeded = false;
            trace.WriteWin32Error = Marshal.GetLastWin32Error();
            return;
        }

        trace.WriteMethod = $"HidD_SetFeature({packet.Length}B)";
        trace.WriteSucceeded = true;

        if (action.DelayBeforeReadMs > 0)
            Thread.Sleep(action.DelayBeforeReadMs);

        var featId = HexUtil.TryParseByte(action.ResponseReportId, out var fid)
            ? fid
            : (rawReq.Length > 0 ? rawReq[0] : (byte)0);

        PollFeatureReport(handle, dev, featId, action, expectPrefix, hasBusyCheck, busyVal, trace);
    }

    private static void RunGetFeatureOnly(
        SafeFileHandle handle,
        HidDeviceInfo dev,
        ProbeAction action,
        byte[] expectPrefix,
        ActionTrace trace)
    {
        var featId = HexUtil.TryParseByte(action.ResponseReportId, out var fid) ? fid : (byte)0;
        trace.WriteMethod = "None(ReadOnly)";
        trace.WriteSucceeded = true;
        trace.RequestHex = $"GetFeature(ReportId=0x{featId:X2})";
        PollFeatureReport(handle, dev, featId, action, expectPrefix, hasBusyCheck: false, busyVal: 0, trace);
    }

    private static void PollFeatureReport(
        SafeFileHandle handle,
        HidDeviceInfo dev,
        byte reportId,
        ProbeAction action,
        byte[] expectPrefix,
        bool hasBusyCheck,
        byte busyVal,
        ActionTrace trace)
    {
        var len = dev.FeatureReportLength > 0 ? dev.FeatureReportLength : 65;
        var retries = Math.Clamp(action.MaxRetries, 1, 10);

        for (var attempt = 1; attempt <= retries; attempt++)
        {
            var buf = new byte[len];
            buf[0] = reportId;
            var sw = Stopwatch.StartNew();
            var ok = HidD_GetFeature(handle, buf, buf.Length);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            sw.Stop();

            trace.Frames.Add(new ReceivedFrame
            {
                Index = attempt,
                Source = $"GetFeature(0x{reportId:X2})",
                ElapsedMs = sw.ElapsedMilliseconds,
                TimedOut = false,
                Win32Error = err,
                ByteLength = ok ? buf.Length : 0,
                MatchedExpectPrefix = ok && HexUtil.StartsWithPrefix(buf, expectPrefix),
                RawHex = ok ? HexUtil.ToHex(buf) : "",
            });

            if (!ok) break;

            if (hasBusyCheck && action.BusyByteIndex is { } bIdx && bIdx < buf.Length && buf[bIdx] == busyVal)
            {
                Thread.Sleep(Math.Max(10, action.RetryDelayMs));
                continue;
            }

            break;
        }
    }

    private static void RunListenInput(
        SafeFileHandle handle,
        HidDeviceInfo dev,
        ProbeAction action,
        byte[] expectPrefix,
        ActionTrace trace)
    {
        trace.WriteMethod = "None(PassiveListen)";
        trace.WriteSucceeded = true;
        trace.RequestHex = $"ListenInput({action.TimeoutMs}ms)";

        var inLen = dev.InputReportLength > 0 ? dev.InputReportLength : 65;
        if (inLen <= 0) return;

        var maxFrames = Math.Clamp(action.MaxReads, 1, 10);
        var deadline = Stopwatch.StartNew();

        for (var idx = 1; idx <= maxFrames; idx++)
        {
            var remain = action.TimeoutMs - (int)deadline.ElapsedMilliseconds;
            if (remain <= 20) break;

            var frame = ReadSingleInputWithTimeout(handle, inLen, remain, idx, expectPrefix);
            trace.Frames.Add(frame);
            if (frame.TimedOut || frame.ByteLength == 0)
                break;
        }
    }

    private static ReceivedFrame ReadSingleInputWithTimeout(
        SafeFileHandle handle,
        int length,
        int timeoutMs,
        int index,
        byte[] expectPrefix)
    {
        var buffer = new byte[length];
        var bytesRead = 0;
        var win32Err = 0;
        var ok = false;
        var sw = Stopwatch.StartNew();

        var task = Task.Run(() =>
        {
            try
            {
                ok = ReadFile(handle, buffer, buffer.Length, out bytesRead, IntPtr.Zero);
                if (!ok) win32Err = Marshal.GetLastWin32Error();
            }
            catch
            {
                ok = false;
            }
        });

        if (!task.Wait(Math.Max(30, timeoutMs)))
        {
            try { CancelIoEx(handle, IntPtr.Zero); } catch { }
            try { task.Wait(200); } catch { }
            sw.Stop();
            return new ReceivedFrame
            {
                Index = index,
                Source = "InputReport",
                ElapsedMs = sw.ElapsedMilliseconds,
                TimedOut = true,
                Win32Error = 1460, // ERROR_TIMEOUT
                ByteLength = 0,
                MatchedExpectPrefix = false,
                RawHex = "",
            };
        }

        sw.Stop();
        var actualLen = ok && bytesRead > 0 ? bytesRead : 0;
        var slice = actualLen > 0 ? buffer[..actualLen] : Array.Empty<byte>();

        return new ReceivedFrame
        {
            Index = index,
            Source = "InputReport",
            ElapsedMs = sw.ElapsedMilliseconds,
            TimedOut = false,
            Win32Error = win32Err,
            ByteLength = actualLen,
            MatchedExpectPrefix = actualLen > 0 && HexUtil.StartsWithPrefix(slice, expectPrefix),
            RawHex = actualLen > 0 ? HexUtil.ToHex(slice) : "",
        };
    }

    /// <summary>
    /// 对已知常见回包格式给出直观预览提示（不影响原始 Hex 完整记录）。
    /// </summary>
    private static string? TryAutoDecodeHint(ActionTrace trace)
    {
        var last = trace.Frames.LastOrDefault(f => f.ByteLength >= 4 && !string.IsNullOrEmpty(f.RawHex));
        if (last is null) return null;

        var rx = HexUtil.ParseHexBytes(last.RawHex);
        // 1. 雷柏一代 0xBA 回包（带或不带首字节 0xBA）：ACK(1成功/0离线/2忙), 充电状态(1未充/2充电/0离线), 电量(0..100)
        var baOff = rx.Length >= 4 && rx[0] == 0xBA ? 1 : 0;
        if (rx.Length >= baOff + 3 && (baOff == 1 || last.Source.StartsWith("GetInputReport(0xBA)", StringComparison.Ordinal)))
        {
            if (rx[baOff] == 0x01 && rx[baOff + 1] is 1 or 2 && rx[baOff + 2] <= 100)
            {
                var chg = rx[baOff + 1] == 2 ? "充电中" : "未充电";
                return $"疑似雷柏一代电量回包 -> ACK=0x01, 状态={chg}(0x{rx[baOff + 1]:X2}), 电量={rx[baOff + 2]}%";
            }
            if (rx[baOff] == 0x00 || rx[baOff + 1] == 0x00)
            {
                return $"疑似雷柏一代离线/休眠回包 -> ACK=0x{rx[baOff]:X2}, 状态=0x{rx[baOff + 1]:X2}, byte[{baOff + 2}]={rx[baOff + 2]}";
            }
        }

        // 2. 雷柏二代 0x07 Input Report：[0]=0x07, [7]=batStatus(1放电/2充电/3满电/0离线), [8]=batPercent
        if (rx.Length >= 9 && rx[0] == 0x07 && rx[8] <= 100)
        {
            return $"疑似雷柏二代状态上报(0x07) -> batStatus={rx[7]}, batPercent={rx[8]}%";
        }

        return null;
    }
}
