using System;
using System.Collections.Generic;

namespace PeripheralBattery.Devices;

/// <summary>
/// 所有数据源的登记处。新增一个 2.4G 品牌 = 在 <c>Sources/Devices/</c> 下写一个
/// <see cref="IDeviceSource"/> 实现，然后在这里的数组里加一行。
/// </summary>
internal static class DeviceSourceRegistry
{
    /// <summary>
    /// 采集顺序即数组顺序。蓝牙放第一位是因为它覆盖的设备最多，
    /// 后面的 2.4G 源读到同一台设备时会靠去重逻辑胜出（2.4G 直读比蓝牙回放值更新）。
    /// </summary>
    public static IReadOnlyList<IDeviceSource> All { get; } = new IDeviceSource[]
    {
        new BluetoothDeviceSource(),
        new VxeDeviceSource(),
        new MchoseDeviceSource(),
        new RapooDeviceSource(),
    };
}
