# HidBatteryProbe · 用户侧外设电量协议抓取工具

用于在**开发者本地没有目标实体外设**的情况下，发给持有该设备的用户运行 1 分钟，安全采集完成 `IDeviceSource` 适配所需的全部硬件证据（精确 `VID:PID`、HID 集合 `UsagePage:Usage`、报告字节长度，以及「无线唤醒 / 插线充电 / 关机离线」三态原始 Hex 回包）。

---

## 一、标准无实机适配流程

1. **第 1 步（开发者预逆向）**：
   获取目标品牌的网页驱动链接（或桌面客户端安装包），静态逆向出候选 `VID`、通信通道与只读电量查询帧，在 `configs/<brand>.json` 中编写探测规则。
2. **第 2 步（一键打包发给用户）**：
   在 `tools/HidBatteryProbe/` 目录下运行：
   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -ConfigFile configs\rapoo-vt9pro.json -PackageSuffix Rapoo-VT9PRO
   ```
   将生成的 `dist/HidBatteryProbe-<后缀>.zip` 发给用户（内置免装 `.NET` 运行时的单文件 `HidBatteryProbe.exe`、`probe-config.json`、`使用说明.txt` 与备用的 `webhid-sniffer.js`）。
3. **第 3 步（本机完成品牌适配）**：
   收到用户发回的 `hid-probe-report-*.json` 后，对照其中的三态 `rawHex` 与用户填写的真实电量百分比，在 `Sources/Devices/` 下编写 `<Brand>DeviceSource.cs` 并注册到 `DeviceSourceRegistry.cs`。

---

## 二、`probe-config.json` 支持的 5 种探测动作（`kind`）

| `kind` 值 | 底层行为 | 典型适用方案 |
| --- | --- | --- |
| `outputThenInput` | `WriteFile`（失败自动回退 `HidD_SetOutputReport`）+ 循环带超时 `ReadFile` | VXE COMPX (`0x08`)、迈从 GLW (`0x00`)、雷柏一代/Telink (`0xBA`) |
| `outputThenFeature` | 发送 Output Report，延时后轮询 `HidD_GetFeature`（支持 `busyValue` 重试） | 雷柏二代网页驱动 Nordic `VT_nrf54L` (`Out 0x06 -> Feat 0x08`) |
| `setFeatureThenGetFeature` | `HidD_SetFeature` 后轮询 `HidD_GetFeature` | 部分原相 / 瑞昱 / 经典桌面驱动 Feature 协议 |
| `getFeature` | 纯只读调用 `HidD_GetFeature(responseReportId)` | 直接通过 Feature Report 暴露电量的外设 |
| `listenInput` | 纯被动 `ReadFile` 监听指定毫秒数（不写任何数据） | 主动广播状态帧的外设（如雷柏二代 `ReportId 0x07`） |

> 若不放 `probe-config.json` 直接运行 `HidBatteryProbe.exe`，工具会自动降级为**纯只读全机 HID 硬件快照模式**（不向任何设备发送写入包）。

---

## 三、附赠：浏览器 F12 WebHID 抓包脚本（`webhid-sniffer.js`）

如果未来遇到某个品牌的**网页驱动**支持用户设备，但其前端代码使用了重度混淆或 WebAssembly 难以直接静态阅读：
1. 让用户用 Chrome / Edge 打开官方网页驱动（**先不要点连接设备**）；
2. 按 `F12` 切到 **Console（控制台）**，粘贴 `webhid-sniffer.js` 全文并回车；
3. 在网页中正常点击连接外设、查看电量；
4. 在控制台输入 `exportWebHidLogs()` 回车，即可自动下载完整的收发报文 JSON。

---

## 四、附录：雷柏（Rapoo）VT9PRO 预逆向笔记

### 1. 为什么网页驱动 `hub.rapoo.cn` 连不上一代 VT9PRO？
经逆向 `https://hub.rapoo.cn/`（AHUB 2.0 前端包）确认，网页驱动目前仅内置了二代 `VT_nrf54L` / `Telink` 架构型号（如 `VT9 PRO V2` `0x24AE:0x14A9`、`VT9` 二代 `0x24AE:0x1414` 等），未收录 2023–2024 款一代 Nordic `nRF52833` / HSDM 架构的 `VT9PRO`。

### 2. 从 `RapooGameDev 1.6.29` 桌面驱动逆向出的一代 VT9PRO 协议（待用户日志最终确认）
解包 `RapooGameDevDriver_Setup_1.6.29_20251011.exe` 并反汇编 `RapooGameDev.exe`（`KDeviceVT950_Nordic::getBatteryValue` @ `0x01177060`）：
- **厂商 VID**：`0x24AE`（9390）
- **VT9PRO 全系已知 PID**：
  - `0x418A`：`VT9PRO`（有线 USB 模式，目标设备字节 `0xFF`）
  - `0x185A`：`VT9PRO_WIRELESS`（原装 1K 2.4G 接收器，目标设备字节 `0xA5`）
  - `0x1601`：`VT9PRO_4K`（配对 4K 接收器，目标设备字节 `0xA5`）
  - `0x1600`：`V2M`（通用 4K 接收器，目标设备字节 `0xA5`）
  - `0x1205` / `0x1221` / `0x1211` / `0x1227`：`VT9PRO_HSDM_WIRELESS` / 欧版 4K 接收器（对应有线 `0x4405` / `0x4421` / `0x4411` / `0x4427`）
  - 同协议家族（可顺带一起适配）：`VT9PRO_MINI` (`0x1861`/`0x1616`)、`VT9` 一代 (`0x1849`/`0x1610`)、`VT9S` 一代 (`0x1851`/`0x1611`)、`VT9Air_HSDM` (`0x1200`)、`VT9Air_LITE` (`0x1867`/`0x1618`)、`VT960PRO` (`0x184B`/`0x1608`)、`VT950PRO` (`0x184A`/`0x1607`)、`V300PRO` (`0x1603`)。
- **查询电量请求帧（Output Report `0xBA`）**：
  - `tx[0] = 0xBA`（ReportId）
  - `tx[1] = 0xA5`（无线接收器目标地址；有线直连时为 `0xFF`）
  - `tx[2] = 0xAA`（`getBatteryValue` 命令字）
  - `tx[3..N-1] = 0x00`（按接口 `OutputReportByteLength` 补零，常见为 33 字节）
- **响应帧（Input Report `0xBA`，写后延时约 50ms 读取）**：
  - `rx[0] == 0xBA`（ReportId）
  - `rx[1]`：ACK 状态（`0x01` = 成功；`0x02` = 设备忙，需延时 50ms 重读；`0x00` = 失败/离线）
  - `rx[2]`：充电/有效状态（`0x01` = 未充电 `no charge`；`0x02` = 充电中 `charging`；`0x00` = 无效/休眠离线）
  - `rx[3]`：实时电量百分比（`0..100`，大于 `100` 时截断为 `100`）
