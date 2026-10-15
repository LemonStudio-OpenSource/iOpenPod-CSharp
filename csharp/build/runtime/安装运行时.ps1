<#
.SYNOPSIS
    安装 iOpenPod 运行所需的 Windows App Runtime 1.8 组件。

.DESCRIPTION
    WinUI 3 应用需要操作系统级的 Windows App Runtime 组件才能初始化图形合成器。
    即使应用是自包含发布的，这些系统组件仍然必须存在，否则窗口会出现但内容空白。
    本脚本会安装随发行包附带的四个微软官方 MSIX 包。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$runtimeDir = Join-Path $PSScriptRoot 'Runtime'

$packages = @(
    'Microsoft.WindowsAppRuntime.1.8.msix',
    'Microsoft.WindowsAppRuntime.Main.1.8.msix',
    'Microsoft.WindowsAppRuntime.Singleton.1.8.msix',
    'Microsoft.WindowsAppRuntime.DDLM.1.8.msix'
)

if (-not (Test-Path $runtimeDir)) {
    Write-Host "找不到 Runtime 目录：$runtimeDir" -ForegroundColor Red
    exit 1
}

$existing = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host ("已检测到 Windows App Runtime 1.8（版本 {0}），无需重复安装。" -f $existing.Version) -ForegroundColor Green
    exit 0
}

$failed = 0
foreach ($name in $packages) {
    $path = Join-Path $runtimeDir $name
    if (-not (Test-Path $path)) {
        Write-Host ("跳过（文件缺失）：{0}" -f $name) -ForegroundColor Yellow
        continue
    }

    try {
        Add-AppxPackage -Path $path -ErrorAction Stop
        Write-Host ("已安装：{0}" -f $name) -ForegroundColor Green
    }
    catch {
        $message = $_.Exception.Message
        if ($message -match '0x80073D06' -or $message -match 'higher version') {
            Write-Host ("已存在更高版本，跳过：{0}" -f $name) -ForegroundColor Yellow
        }
        else {
            Write-Host ("安装失败：{0} → {1}" -f $name, $message) -ForegroundColor Red
            $failed++
        }
    }
}

if ($failed -gt 0) {
    Write-Host ""
    Write-Host "有 $failed 个组件未能安装。" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Windows App Runtime 1.8 已就绪。" -ForegroundColor Green
exit 0
