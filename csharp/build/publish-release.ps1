<#
.SYNOPSIS
    在 GitHub 上创建（或更新）发行版并上传二进制资产。

.DESCRIPTION
    从 Windows 凭据管理器读取 git 凭据（不会写入任何文件），
    然后创建或更新指定标签的发行版，并把 dist 目录中的压缩包、校验和与截图作为资产上传。

    注意：必须以 UTF-8 字节发送 JSON 正文，否则 Windows PowerShell 5.1 会把中文替换为问号。

.PARAMETER Tag
    发行版标签，默认 v3.0.0。

.PARAMETER NotesFile
    发行说明的 Markdown 文件。默认使用 build/release-notes-v3.0.0.md。

.PARAMETER Owner
    GitHub 账号或组织名。

.PARAMETER Repository
    仓库名。
#>
[CmdletBinding()]
param(
    [string]$Tag = 'v3.0.0',
    [string]$NotesFile,
    [string]$Owner = 'LemonStudio-OpenSource',
    [string]$Repository = 'iOpenPod-CSharp'
)

$ErrorActionPreference = 'Stop'
$env:GCM_INTERACTIVE = 'never'
$env:GIT_TERMINAL_PROMPT = '0'

$root = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path (Split-Path -Parent $root) 'dist'

if (-not $NotesFile) {
    $NotesFile = Join-Path $PSScriptRoot "release-notes-$Tag.md"
}

$credential = ("protocol=https`nhost=github.com`n`n" | git credential fill 2>&1)
$token = (($credential | Select-String '^password=').Line).Substring(9)
if (-not $token) { throw '未能从凭据管理器取得 GitHub 令牌。' }

$headers = @{
    Authorization = "Bearer $token"
    Accept        = 'application/vnd.github+json'
    'User-Agent'  = 'iOpenPod-Publisher'
}

function Send-Json {
    param([string]$Method, [string]$Uri, [hashtable]$Payload)
    $json = $Payload | ConvertTo-Json -Depth 6
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers `
        -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 300
}

$notes = ''
if (Test-Path $NotesFile) {
    $notes = Get-Content $NotesFile -Raw -Encoding UTF8
}
else {
    Write-Warning "找不到发行说明文件：$NotesFile，将使用空说明。"
}

$title = "iOpenPod 管理器 $Tag"

# 已存在则更新，否则创建。
$existing = $null
try {
    $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repository/releases/tags/$Tag" `
        -Headers $headers -TimeoutSec 60
}
catch {
    $existing = $null
}

if ($existing) {
    Write-Host "更新已有发行版 $Tag …"
    $release = Send-Json -Method Patch `
        -Uri "https://api.github.com/repos/$Owner/$Repository/releases/$($existing.id)" `
        -Payload @{ name = $title; body = $notes }
}
else {
    Write-Host "创建发行版 $Tag …"
    $release = Send-Json -Method Post `
        -Uri "https://api.github.com/repos/$Owner/$Repository/releases" `
        -Payload @{
            tag_name         = $Tag
            target_commitish = 'main'
            name             = $title
            body             = $notes
            draft            = $false
            prerelease       = $false
        }
}

Write-Host "发行版地址：$($release.html_url)"

$uploadUrl = $release.upload_url -replace '\{\?name,label\}', ''
$already = @{}
foreach ($asset in $release.assets) { $already[$asset.name] = $true }

$wanted = @(
    @{ File = "iOpenPod-$Tag-win-x64.zip"; Name = "iOpenPod-$Tag-win-x64.zip"; Type = 'application/zip' }
    @{ File = 'SHA256SUMS.txt';            Name = 'SHA256SUMS.txt';               Type = 'text/plain' }
    @{ File = 'iOpenPod-界面截图.png';      Name = 'iOpenPod-screenshot.png';      Type = 'image/png' }
)

foreach ($item in $wanted) {
    if ($already.ContainsKey($item.Name)) {
        Write-Host ("已存在，跳过：{0}" -f $item.Name)
        continue
    }

    $path = Join-Path $distDir $item.File
    if (-not (Test-Path $path)) {
        Write-Warning "缺少文件：$path"
        continue
    }

    Write-Host ("正在上传 {0}（{1:N1} MB）…" -f $item.Name, ((Get-Item $path).Length / 1MB))
    $uri = "$uploadUrl?name=" + [uri]::EscapeDataString($item.Name)
    $result = Invoke-RestMethod -Method Post -Uri $uri -Headers $headers `
        -InFile $path -ContentType $item.Type -TimeoutSec 3600
    Write-Host ("  完成：{0}" -f $result.name)
}

Write-Host ''
Write-Host '发行版发布完成。' -ForegroundColor Green
