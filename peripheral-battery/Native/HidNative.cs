using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBattery.Native;

/// <summary>
/// HID 的 SetupAPI / hid.dll 互操作：
/// 1. 枚举所有 HID 顶层集合（VID/PID、UsagePage/Usage、Report 长度、产品名）；
/// 2. 读取 Feature 报告；
/// 3. 执行带超时取消（CancelIoEx）的 Output + Input Report 报文收发事务（用于 VXE、MCHOSE 等 2.4G 私有协议）。
/// 全部调用都容忍失败，失败返回 null，绝不挂起后台线程。
/// </summary>
internal static class HidNative
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
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);

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

    /// <summary>一个 HID 集合的关键信息。</summary>
    internal sealed record HidInterface(
        string Path,
        ushort VendorId,
        ushort ProductId,
        ushort UsagePage,
        ushort Usage,
        int InputLength,
        int OutputLength,
        int FeatureLength,
        string ProductName);

    /// <summary>枚举失败时把原因记下来（默认静默吞掉，出问题就没法排查）。</summary>
    internal static string? LastError { get; private set; }

    /// <summary>上一次枚举看到的接口总数 / Describe 失败数，用于诊断。</summary>
    internal static int LastInterfaceCount { get; private set; }
    internal static int LastDescribeFailures { get; private set; }

    /// <summary>逐步诊断信息，只在出问题时看。</summary>
    internal static List<string> LastTrace { get; } = new();

    /// <summary>枚举所有 HID 接口（拿不到就返回空列表，不抛）。</summary>
    internal static List<HidInterface> Enumerate()
    {
        LastError = null;
        LastInterfaceCount = 0;
        LastDescribeFailures = 0;
        LastTrace.Clear();
        var result = new List<HidInterface>();
        try
        {
            HidD_GetHidGuid(out var hidGuid);
            LastTrace.Add($"hidGuid={hidGuid} err={Marshal.GetLastWin32Error()}");
            var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            LastTrace.Add($"set={set.ToInt64()} err={Marshal.GetLastWin32Error()}");
            if (set == IntPtr.Zero || set.ToInt64() == -1)
            {
                LastError = $"SetupDiGetClassDevs 失败，Win32 错误码 {Marshal.GetLastWin32Error()}";
                return result;
            }

            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                for (var index = 0; ; index++)
                {
                    data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data))
                    {
                        LastTrace.Add($"enum stop idx={index} err={Marshal.GetLastWin32Error()}");
                        break;
                    }

                    // 第一次调用只用来问长度：会返回 false + ERROR_INSUFFICIENT_BUFFER(122)，属正常，
                    // 只要 required > 0 就算成功。
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                    if (required <= 0)
                    {
                        if (LastTrace.Count < 8) LastTrace.Add($"idx {index}: detail-size 拿不到 err={Marshal.GetLastWin32Error()} req={required}");
                        continue;
                    }

                    var buffer = Marshal.AllocHGlobal(required);
                    try
                    {
                        LastInterfaceCount++;
                        // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize 在 x64 上是 8（含 4 字节对齐填充）
                        Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero)) continue;

                        var path = Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        var entry = Describe(path);
                        if (entry is not null) result.Add(entry);
                        else LastDescribeFailures++;
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
        }
        catch (Exception ex)
        {
            LastError = $"枚举异常：{ex.GetType().Name} {ex.Message}";
        }

        return result;
    }

    private static HidInterface? Describe(string path)
    {
        var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        try
        {
            var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attributes)) return null;

            var caps = new HIDP_CAPS();
            var hasCaps = false;
            if (HidD_GetPreparsedData(handle, out var preparsed))
            {
                try
                {
                    caps = new HIDP_CAPS();
                    hasCaps = HidP_GetCaps(preparsed, ref caps) == HIDP_STATUS_SUCCESS;
                }
                finally
                {
                    HidD_FreePreparsedData(preparsed);
                }
            }

            return new HidInterface(
                path,
                attributes.VendorID,
                attributes.ProductID,
                hasCaps ? caps.UsagePage : (ushort)0,
                hasCaps ? caps.Usage : (ushort)0,
                hasCaps ? caps.InputReportByteLength : 0,
                hasCaps ? caps.OutputReportByteLength : 0,
                hasCaps ? caps.FeatureReportByteLength : 0,
                ReadProductString(handle));
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static string ReadProductString(SafeFileHandle handle)
    {
        try
        {
            var buffer = new byte[256];
            if (!HidD_GetProductString(handle, buffer, buffer.Length)) return "";
            var text = Encoding.Unicode.GetString(buffer);
            var end = text.IndexOf('\0');
            return (end >= 0 ? text[..end] : text).Trim();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 读一条 Feature 报告。reportId 写进 buffer[0]。
    /// 失败返回 null（设备被独占、或不支持该报告都算失败）。
    /// </summary>
    internal static byte[]? GetFeature(string path, byte reportId, int length)
    {
        if (length <= 0 || length > 4096) return null;

        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        try
        {
            var buffer = new byte[length];
            buffer[0] = reportId;
            return HidD_GetFeature(handle, buffer, length) ? buffer : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// 向指定 HID 接口发送一条 Output Report（含首字节 Report ID），并等待读取满足 <paramref name="match"/> 的 Input Report。
    /// 若在 <paramref name="timeoutMs"/> 内未收到响应，立即通过 <c>CancelIoEx</c> 取消阻塞的 <c>ReadFile</c>，
    /// 保证在无线设备休眠或关机时绝不挂起线程。
    /// </summary>
    internal static byte[]? Transact(
        string path,
        byte[] request,
        int responseLength,
        Func<byte[], bool>? match = null,
        int timeoutMs = 450,
        int maxReads = 4)
    {
        if (request.Length == 0 || responseLength <= 0 || responseLength > 4096) return null;

        var handle = CreateFile(
            path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        try
        {
            if (!WriteFile(handle, request, request.Length, out var written, IntPtr.Zero) || written <= 0)
            {
                return null;
            }

            for (var attempt = 0; attempt < maxReads; attempt++)
            {
                var rx = ReadWithTimeout(handle, responseLength, timeoutMs);
                if (rx is null) return null;
                if (match is null || match(rx)) return rx;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// 通过控制管道或中断管道发送 Output Report，延时后通过 <c>HidD_GetInputReport</c>（USB 控制端点 GET_REPORT(Input)）
    /// 轮询读取指定 <paramref name="responseReportId"/> 的响应帧。
    /// 适用于雷柏（Rapoo）VT9PRO / V2M 等不在中断 IN 端点推送响应、而是通过控制传输读取 Input Report 的外设。
    /// </summary>
    internal static byte[]? TransactControlInput(
        string path,
        byte[] request,
        byte responseReportId,
        int responseLength,
        bool preferControlOutput,
        Func<byte[], bool>? isReady = null,
        int initialDelayMs = 50,
        int retryDelayMs = 50,
        int maxAttempts = 3)
    {
        if (request.Length == 0 || responseLength <= 0 || responseLength > 4096) return null;

        using var handle = CreateFile(
            path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid) return null;

        try
        {
            bool sent;
            if (preferControlOutput)
            {
                sent = HidD_SetOutputReport(handle, request, request.Length)
                       || (WriteFile(handle, request, request.Length, out var w1, IntPtr.Zero) && w1 > 0);
            }
            else
            {
                sent = (WriteFile(handle, request, request.Length, out var w2, IntPtr.Zero) && w2 > 0)
                       || HidD_SetOutputReport(handle, request, request.Length);
            }

            if (!sent) return null;

            if (initialDelayMs > 0)
            {
                System.Threading.Thread.Sleep(initialDelayMs);
            }

            byte[]? lastBuffer = null;
            var attempts = Math.Clamp(maxAttempts, 1, 8);
            for (var i = 0; i < attempts; i++)
            {
                var buffer = new byte[responseLength];
                buffer[0] = responseReportId;
                if (!HidD_GetInputReport(handle, buffer, buffer.Length))
                {
                    return lastBuffer;
                }

                lastBuffer = buffer;
                if (isReady is null || isReady(buffer))
                {
                    return buffer;
                }

                if (i + 1 < attempts && retryDelayMs > 0)
                {
                    System.Threading.Thread.Sleep(retryDelayMs);
                }
            }

            return lastBuffer;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadWithTimeout(SafeFileHandle handle, int length, int timeoutMs)
    {
        var buffer = new byte[length];
        var read = 0;
        var ok = false;

        var task = Task.Run(() =>
        {
            try
            {
                ok = ReadFile(handle, buffer, buffer.Length, out read, IntPtr.Zero);
            }
            catch
            {
                ok = false;
            }
        });

        if (!task.Wait(timeoutMs))
        {
            try { CancelIoEx(handle, IntPtr.Zero); } catch { }
            try { task.Wait(200); } catch { }
            return null;
        }

        return ok && read > 0 ? buffer : null;
    }
}
