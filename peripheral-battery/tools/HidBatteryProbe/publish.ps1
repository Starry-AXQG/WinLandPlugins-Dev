param(
    [string]$ConfigFile = "configs\rapoo-vt9pro.json",
    [string]$PackageSuffix = "Rapoo-VT9PRO"
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { "$env:USERPROFILE\.dotnet\dotnet.exe" } else { "dotnet" }
$projectDir = $PSScriptRoot
$distRoot = Join-Path $projectDir "dist"
$bundleName = "HidBatteryProbe-$PackageSuffix"
$bundleDir = Join-Path $distRoot $bundleName
$zipPath = Join-Path $distRoot "$bundleName.zip"

if (Test-Path $bundleDir) { Remove-Item $bundleDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null

Write-Host "--- 正在编译单文件免安装版 HidBatteryProbe.exe ---" -ForegroundColor Cyan
Push-Location $projectDir
try {
    & $dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $bundleDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }
} finally {
    Pop-Location
}

# 清理多余文件（只留单文件 exe）
Get-ChildItem -Path $bundleDir -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

# 复制探测规则为默认 probe-config.json
$resolvedCfg = Join-Path $projectDir $ConfigFile
if (Test-Path $resolvedCfg) {
    Copy-Item $resolvedCfg (Join-Path $bundleDir "probe-config.json") -Force
}

# 附带浏览器 WebHID 抓包脚本备用
Copy-Item (Join-Path $projectDir "webhid-sniffer.js") (Join-Path $bundleDir "webhid-sniffer.js") -Force

# 生成面向小白用户的超简使用说明
$userGuide = @"
============================================================
  WinIsland 外设电量适配 · 用户侧日志抓取步骤（约 1 分钟）
============================================================

感谢协助测试！本工具完全离线运行、无需安装、不修改任何鼠标设置。

【使用方法】
1. 请先将压缩包完整解压到一个普通文件夹中（不要直接在压缩包内双击，保持 HidBatteryProbe.exe 与 probe-config.json 在同一文件夹）。
2. 如果后台开着雷柏官方驱动（RapooGameDev），可以先保持正常状态不用关（如果第 1 轮提示占用，再退出官方驱动重跑一次即可）。
3. 双击运行 HidBatteryProbe.exe，按屏幕上的中文提示按 3 次【回车键】：
   · 第 1 步（无线唤醒态）：打开鼠标无线开关，晃动几下鼠标后按回车；
   · 第 2 步（插线充电态）：保持接收器插着，同时给鼠标插上充电线等 2 秒后按回车（不方便插线可输入 s 跳过）；
   · 第 3 步（关机离线态）：拔掉充电线（2.4G 接收器留在电脑上别拔），把鼠标底部电源拨到 OFF 关闭，等 2 秒按回车。
4. 运行结束后，同文件夹下会自动生成一个名为：
   hid-probe-report-rapoo-xxxxxxxx-xxxxxx.json
   的文件（纯文本协议日志，几 KB 大小，不含任何个人隐私信息）。
5. 请把这个 .json 文件直接发回给开发者即可！
"@

[System.IO.File]::WriteAllText((Join-Path $bundleDir "使用说明（请先解压再运行）.txt"), $userGuide, [System.Text.Encoding]::UTF8)

Compress-Archive -Path "$bundleDir\*" -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "=== 打包完成 ===" -ForegroundColor Green
Write-Host "目录: $bundleDir"
Write-Host "压缩包（可直接发给用户）: $zipPath"
