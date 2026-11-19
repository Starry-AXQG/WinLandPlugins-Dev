param(
    [switch]$NoStart
)

$ErrorActionPreference = 'Continue'
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$projectDir = $PSScriptRoot
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
$pluginLog = "$env:LOCALAPPDATA\WinIsland\logs\plugin.peripheral-battery.log"

Write-Host '--- 停止 WinIsland ---'
$procs = Get-Process -Name WinIsland -ErrorAction SilentlyContinue
if ($procs) {
    foreach ($p in $procs) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Write-Host "  已停止 PID $($p.Id)" }
        catch { Write-Host "  停止 PID $($p.Id) 失败：$($_.Exception.Message)" }
    }
    Start-Sleep -Seconds 3
} else {
    Write-Host '  未在运行'
}

Write-Host '--- 编译 ---'
Push-Location $projectDir
try {
    & $dotnet build -c Release -t:Rebuild --nologo 2>&1 | Select-Object -Last 25
} finally {
    Pop-Location
}

if ($NoStart) { Write-Host '--- 按参数不启动 ---'; exit 0 }

Write-Host '--- 启动 WinIsland ---'
$exe = "$env:LOCALAPPDATA\Programs\WinIsland\WinIsland.exe"
$dir = Split-Path -Parent $exe
$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine = "`"$exe`""
    CurrentDirectory = $dir
}
Write-Host "  已启动 PID $($created.ProcessId)"
Start-Sleep -Seconds 8

Write-Host '--- 插件日志 ---'
if (Test-Path $pluginLog) {
    Get-Content $pluginLog -Tail 60 -Encoding UTF8
} else {
    Write-Host "  无日志：$pluginLog"
}
