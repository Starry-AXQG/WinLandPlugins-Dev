using System;
using System.Collections.Generic;

namespace PeripheralBattery.Devices;

/// <summary>
/// 采集一轮时交给各数据源的共享上下文。
/// HID 枚举一次要打开几十个设备句柄、蓝牙连接状态是异步 WinRT 查询 ——
/// 两者都在这里按需查一次并缓存，所有数据源共用，避免每个适配器各扫一遍设备树。
/// </summary>
internal sealed class DeviceSourceContext
{
    private readonly Func<IReadOnlyList<Native.HidNative.HidInterface>> _hidFactory;
    private IReadOnlyList<Native.HidNative.HidInterface>? _hidInterfaces;

    public DeviceSourceContext(
        Action<string, string> log,
        Func<IReadOnlyList<Native.HidNative.HidInterface>> hidFactory,
        Native.BluetoothNative.BtScanResult bluetooth)
    {
        Log = log;
        _hidFactory = hidFactory;
        Bluetooth = bluetooth;
    }

    /// <summary>写日志：(级别, 内容)。级别取 "Info" / "Debug" / "Warn"。</summary>
    public Action<string, string> Log { get; }

    /// <summary>
    /// 本轮枚举到的全部 HID 集合。第一次访问时才真正枚举，之后复用同一份 ——
    /// 2.4G 适配器从这里挑自己认识的接收器，多个适配器不会重复枚举。
    /// </summary>
    public IReadOnlyList<Native.HidNative.HidInterface> HidInterfaces => _hidInterfaces ??= _hidFactory();

    /// <summary>本轮的蓝牙扫描结果（连接状态 + Class of Device）。</summary>
    public Native.BluetoothNative.BtScanResult Bluetooth { get; }

    public void Info(string message) => Log("Info", message);

    public void Debug(string message) => Log("Debug", message);

    public void Warn(string message) => Log("Warn", message);
}
