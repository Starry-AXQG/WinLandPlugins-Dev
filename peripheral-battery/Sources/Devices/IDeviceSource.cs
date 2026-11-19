using System.Collections.Generic;

namespace PeripheralBattery.Devices;

/// <summary>
/// 一个 2.4G 外设品牌的电量数据源。
///
/// 新增品牌支持 = 在 <c>Sources/Devices/</c> 下新建一个类实现本接口，
/// 再在 <see cref="DeviceSourceRegistry.All"/> 里登记一行。采集主流程不需要改。
///
/// 实现要求：
/// 1. <see cref="Collect"/> 不许抛异常 —— 但为了保险，外面仍会包一层 try/catch；
/// 2. 读不到就返回空列表，不要返回电量为 null 的占位项（那会在界面上显示一台没有数字的设备）；
/// 3. <c>Id</c> 必须稳定（同一台设备每次刷新都一样），隐藏设备、低电量去重都靠它；
/// 4. 设备名尽量用产品名（"VXE Mouse 1K Dongle"），不要带 "2.4G" 之类连接方式后缀 ——
///    去重逻辑会按名字把同型号的蓝牙与 2.4G 记录合并成一条。
/// </summary>
internal interface IDeviceSource
{
    /// <summary>日志与来源说明里显示的品牌名，例如 "VXE"、"MCHOSE"。</summary>
    string Brand { get; }

    /// <summary>采一轮，返回本品牌所有能读到电量的设备。</summary>
    List<DeviceBattery> Collect(DeviceSourceContext context);
}
