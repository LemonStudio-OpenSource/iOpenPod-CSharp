<#
.SYNOPSIS
    生成 iOpenPod 的发布版本。

.DESCRIPTION
    把 WinUI 3 应用发布为**自带 .NET 运行时**的可执行目录，并附带
    Windows App Runtime 的官方 MSIX 组件与一键安装脚本，最后复制到输出目录
    （默认 ..\..\dist\iOpenPod）并生成 ZIP 压缩包。

    为什么 Windows App SDK 不做成自包含：实测在部分 Windows 环境（旧显卡驱动 +
    虚拟显示适配器）下，WinAppSDK 自包含部署会导致 WinUI 合成器初始化失败，
    表现为"窗口出现但内容全白"。改用系统已安装的 Windows App Runtime 后稳定可用，
    因此发行包改为携带官方 MSIX 组件并提供一键安装脚本。

.PARAMETER OutputDirectory
    发布结果的输出目录。

.PARAMETER RuntimeIdentifier
    目标运行时，默认 win-x64。

.PARAMETER NoZip
    指定后不再额外生成 ZIP 压缩包。

.PARAMETER WindowsAppSdkSelfContained
    指定后把 Windows App SDK 也做成自包含（不推荐，见上文说明）。
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$NoZip,
    [switch]$WindowsAppSdkSelfContained
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $root) 'dist\iOpenPod'
}

Write-Host "iOpenPod 发布" -ForegroundColor Cyan
Write-Host "  源码根目录：$root"
Write-Host "  输出目录  ：$OutputDirectory"
Write-Host "  目标运行时：$RuntimeIdentifier"

$project = Join-Path $root 'src\iOpenPod.WinUI\iOpenPod.WinUI.csproj'
if (-not (Test-Path $project)) {
    throw "找不到 WinUI 项目：$project"
}

$publishDir = Join-Path $root "src\iOpenPod.WinUI\bin\Release\net10.0-windows10.0.19041.0\$RuntimeIdentifier\publish"

Write-Host "`n[1/4] 编译并发布…" -ForegroundColor Cyan
& dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained true `
    -p:WindowsAppSDKSelfContained=$($WindowsAppSdkSelfContained.IsPresent.ToString().ToLowerInvariant())
if ($LASTEXITCODE -ne 0) {
    throw "发布失败（退出码 $LASTEXITCODE）。"
}

Write-Host "`n[2/4] 复制到 $OutputDirectory …" -ForegroundColor Cyan
if (Test-Path $OutputDirectory) {
    Remove-Item -Recurse -Force $OutputDirectory
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $OutputDirectory -Recurse -Force

$readme = Join-Path $root 'README.md'
if (Test-Path $readme) {
    Copy-Item $readme (Join-Path $OutputDirectory 'README.md') -Force
}

$exe = Join-Path $OutputDirectory 'iOpenPod.exe'
if (-not (Test-Path $exe)) {
    throw "发布目录中缺少 iOpenPod.exe。"
}

# WinUI 3 还需要操作系统级的 Windows App Runtime 组件才能初始化图形合成器；
# 即使应用是自包含发布的，这些组件也必须存在，否则窗口会出现但内容空白。
# 因此把微软官方的四个 MSIX 包与一键安装脚本一起放进发行包。
Write-Host "`n[3/4] 附带 Windows App Runtime 安装文件…" -ForegroundColor Cyan

$runtimeSource = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk.runtime') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Select-Object -First 1

if ($null -ne $runtimeSource) {
    $msixDir = Get-ChildItem (Join-Path $runtimeSource.FullName 'tools\MSIX') -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -eq 'win10-x64' |
        Select-Object -First 1

    if ($null -ne $msixDir) {
        $target = Join-Path $OutputDirectory 'Runtime'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item (Join-Path $msixDir.FullName '*.msix') $target -Force
        Write-Host ("      已附带 {0} 个运行时组件（{1}）。" -f (Get-ChildItem $target -Filter *.msix).Count, $runtimeSource.Name)
    }
    else {
        Write-Warning "未找到 win10-x64 的运行时组件目录，发行包中将缺少 Runtime 文件夹。"
    }
}
else {
    Write-Warning "未在 NuGet 缓存中找到 Microsoft.WindowsAppSDK.Runtime，发行包中将缺少 Runtime 文件夹。"
}

$installerSource = Join-Path $root 'build\runtime'
if (Test-Path $installerSource) {
    Copy-Item (Join-Path $installerSource '*') $OutputDirectory -Force
}

$size = (Get-ChildItem $OutputDirectory -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("      完成：{0:N1} MB，共 {1} 个文件。" -f ($size / 1MB), (Get-ChildItem $OutputDirectory -Recurse -File).Count)

if (-not $NoZip) {
    Write-Host "`n[4/4] 生成压缩包…" -ForegroundColor Cyan
    $zip = "$OutputDirectory.zip"
    if (Test-Path $zip) {
        Remove-Item -Force $zip
    }

    Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("      完成：{0}（{1:N1} MB）" -f $zip, ((Get-Item $zip).Length / 1MB))
}

Write-Host "`n发布完成，入口程序：$exe" -ForegroundColor Green
Write-Host "提示：首次使用请先运行“首次运行-安装运行时.cmd”。" -ForegroundColor Yellow
