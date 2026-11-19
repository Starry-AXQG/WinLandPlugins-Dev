# 本地部署脚本：编译 → 停宿主 → 拷插件 → 起宿主
# 用法（在 HwSensors 目录下）：pwsh -File deploy.ps1
# 之所以要停宿主：WinIsland 加载插件后会锁住 HwSensors.dll，直接覆盖会 MSB3027。

param(
    [string]$Configuration = "Debug",
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$hostExe = "C:\Users\Starry\AppData\Local\Programs\WinIsland\WinIsland.exe"
$pluginsRoot = "C:\Users\Starry\AppData\Local\Programs\WinIsland\plugins"
$pluginId = "hw-sensors"
$target = Join-Path $pluginsRoot $pluginId

Push-Location $PSScriptRoot
try {
    Write-Host "== 编译 ==" -ForegroundColor Cyan
    & $dotnet build -c $Configuration -p:SkipDeploy=true | Select-Object -Last 6
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }

    $out = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
    if (-not (Test-Path $out)) { throw "找不到输出目录：$out" }

    Write-Host "== 停宿主 ==" -ForegroundColor Cyan
    Get-Process WinIsland -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    Write-Host "== 部署到 $target ==" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    $skip = @(
        "WinIsland.Core.dll", "Microsoft.WinUI.dll", "Microsoft.Windows.SDK.NET.dll",
        "WinRT.Runtime.dll", "System.Management.dll", "WebView2Loader.dll"
    )

    Get-ChildItem $out -File | Where-Object {
        $_.Extension -ne ".pdb" -and $skip -notcontains $_.Name -and $_.Name -notlike "Microsoft.Windows.*" -and $_.Name -notlike "Microsoft.Web.WebView2*"
    } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $target $_.Name) -Force
        Write-Host "  + $($_.Name)"
    }

    if (-not $NoRestart) {
        Write-Host "== 起宿主 ==" -ForegroundColor Cyan
        Start-Process $hostExe
        Start-Sleep -Seconds 8
        $log = "C:\Users\Starry\AppData\Local\WinIsland\logs\plugin.$pluginId.log"
        if (Test-Path $log) { Get-Content $log -Tail 8 }
    }
}
finally {
    Pop-Location
}
