## 插件简介

**硬件传感器（hw-sensors）**：在灵动岛上实时看 CPU / GPU 的温度、功耗和占用，外加整机预估功耗、电池电量与损耗程度。

小岛显示图标 + 名称 + 一个最关键的值（例如 `52°C · 14W`）；展开是四行明细，每行都能单独开关；点一下弹出聚光卡看完整明细。

**和已有的 `hw-monitor` 有什么区别**：`hw-monitor` 侧重网络与帧率，本插件侧重**温度 / 功耗 / 电池**，并额外提供「整机预估功耗」「电池损耗程度」「充电功率与预计充满时间」。

## 测试环境

- WinIsland 版本：**2.4.0**（`WinIsland.Core.dll` 2.4.0.0）
- Windows 版本：Windows 11 24H2（26100）
- 测试机型：ASUS TUF Gaming A14 FA401UV（Ryzen 7 8845H + RTX 4060 Laptop + 780M 核显，笔记本带电池）
- 是否需要管理员权限：**不需要**（详见下一节）

## 权限与数据

- **是否访问网络**：否。全部数据来自本机传感器。
- **读写哪些文件**：只用插件自己的设置键（`hw-sensors.*`），不读写其他路径。
- **是否需要驱动**：不需要额外安装驱动。

### 数据来源（透明说明）

宿主进程是普通权限，所以本插件全程走**免管理员通道**：

| 指标 | 数据源 |
| --- | --- |
| GPU 温度 / 功耗 / 占用 | [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（MPL-2.0，通过显卡驱动接口读取） |
| 电池电量 / 容量 / 电压 / 充放电功率 / 损耗 | LibreHardwareMonitor 电池组 |
| CPU 封装功耗 | RAPL 能耗性能计数器（`\Energy Meter(*)\Power`，Windows 自带 PDH） |
| CPU 温度 | ACPI 热区性能计数器（`\Thermal Zone Information`，Windows 自带 PDH） |
| 交流供电状态 / 充放电方向 | `GetSystemPowerStatus`（Windows 自带） |

> **关于 CPU 温度**：LibreHardwareMonitor 读取 AMD 的 Tctl/Tdie 精确结温需要内核驱动（PawnIO），普通权限拿不到，会返回 0。因此插件对 CPU 走了 RAPL + ACPI 热区两条系统自带通道，并在聚光卡里把 CPU 温度标注为「整机热区」，不冒充核心结温。

### 降级策略

任何数据源不可用时，对应数字显示 `--`，不会让整张卡片崩掉。没有电池的台式机上，电池那一行自动不显示。

## 包内第三方依赖

- `LibreHardwareMonitorLib.dll` 及其依赖（`HidSharp` / `DiskInfoToolkit` / `RAMSPDToolkit-NDD` / `Mono.Posix.NETStandard` / `System.IO.Ports` / `System.CodeDom` / `BlackSharp.Core`）

已按规范排除宿主自带运行时（`WinIsland.Core.dll` / `Microsoft.WinUI.dll` / `Microsoft.Windows.SDK.NET.dll` / `WinRT.Runtime.dll` / `Microsoft.WindowsAppRuntime*` / `Microsoft.Web.WebView2*` / `System.Management.dll`）。包内已用 CI 同款 `build-index.ps1 -CheckOnly` 本地校验通过。

## 截图

暂未附（本机截图工具受限）。插件市场列表会显示随包提供的 `logo.png`（CPU 芯片图标）。
