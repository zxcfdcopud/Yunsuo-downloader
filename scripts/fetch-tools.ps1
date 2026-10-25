<#
    fetch-tools.ps1
    下载并内置三方命令行工具到 tools\ 目录：
      - aria2c.exe  多线程下载内核 (GPLv2+)
      - yt-dlp.exe  视频/HLS 解析内核 (Unlicense)
      - ffmpeg.exe / ffprobe.exe  音视频合并与转封装 (LGPL/GPL)

    这些工具由本机按需下载，不随源码仓库分发。
    用法:  powershell -ExecutionPolicy Bypass -File scripts\fetch-tools.ps1
    可选:  -Force 重新下载； -Only aria2,ytdlp,ffmpeg
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string[]]$Only
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root     = Split-Path -Parent $PSScriptRoot
$ToolsDir = Join-Path $Root 'tools'
$CacheDir = Join-Path $ToolsDir '.cache'
New-Item -ItemType Directory -Force -Path $ToolsDir, $CacheDir | Out-Null

function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "    OK  $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "    !!  $m" -ForegroundColor Yellow }

function Get-Remote {
    param([string]$Url, [string]$OutFile)
    $mirrors = @($Url)
    if ($Url -like 'https://github.com/*') {
        $mirrors += ($Url -replace '^https://github\.com/', 'https://gh-proxy.com/https://github.com/')
        $mirrors += ($Url -replace '^https://github\.com/', 'https://ghfast.top/https://github.com/')
    }
    foreach ($u in $mirrors) {
        try {
            Write-Host "    GET $u"
            Invoke-WebRequest -Uri $u -OutFile $OutFile -UseBasicParsing -TimeoutSec 300
            if ((Get-Item $OutFile).Length -gt 1024) { return $true }
        } catch {
            Write-Warn2 "失败: $($_.Exception.Message)"
        }
    }
    return $false
}

# ---------------------------------------------------------------- aria2c
function Get-Aria2 {
    $dst = Join-Path $ToolsDir 'aria2c.exe'
    if ((Test-Path $dst) -and -not $Force) { Write-Ok "aria2c.exe 已存在，跳过"; return }
    Write-Step '下载 aria2c'
    $ver = '1.37.0'
    $zip = Join-Path $CacheDir 'aria2.zip'
    $url = "https://github.com/aria2/aria2/releases/download/release-$ver/aria2-$ver-win-64bit-build1.zip"
    if (-not (Get-Remote -Url $url -OutFile $zip)) { throw 'aria2 下载失败' }
    $tmp = Join-Path $CacheDir 'aria2x'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $exe = Get-ChildItem $tmp -Recurse -Filter 'aria2c.exe' | Select-Object -First 1
    if (-not $exe) { throw '压缩包内未找到 aria2c.exe' }
    Copy-Item $exe.FullName $dst -Force
    Get-ChildItem $exe.Directory.FullName -Filter '*.dll' -ErrorAction SilentlyContinue | Copy-Item -Destination $ToolsDir -Force
    Write-Ok "aria2c.exe -> $dst"
}

# ---------------------------------------------------------------- yt-dlp
function Get-YtDlp {
    $dst = Join-Path $ToolsDir 'yt-dlp.exe'
    if ((Test-Path $dst) -and -not $Force) { Write-Ok 'yt-dlp.exe 已存在，跳过'; return }
    Write-Step '下载 yt-dlp'
    $url = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe'
    if (-not (Get-Remote -Url $url -OutFile $dst)) { throw 'yt-dlp 下载失败' }
    Write-Ok "yt-dlp.exe -> $dst"
}

# ---------------------------------------------------------------- ffmpeg
function Get-Ffmpeg {
    $dst = Join-Path $ToolsDir 'ffmpeg.exe'
    if ((Test-Path $dst) -and -not $Force) { Write-Ok 'ffmpeg.exe 已存在，跳过'; return }
    Write-Step '下载 ffmpeg (含 ffprobe)'
    $zip = Join-Path $CacheDir 'ffmpeg.zip'
    $urls = @(
        'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip',
        'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'
    )
    $ok = $false
    foreach ($u in $urls) { if (Get-Remote -Url $u -OutFile $zip) { $ok = $true; break } }
    if (-not $ok) { throw 'ffmpeg 下载失败' }
    $tmp = Join-Path $CacheDir 'ffmpegx'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    foreach ($n in 'ffmpeg.exe', 'ffprobe.exe') {
        $f = Get-ChildItem $tmp -Recurse -Filter $n | Select-Object -First 1
        if ($f) { Copy-Item $f.FullName (Join-Path $ToolsDir $n) -Force; Write-Ok "$n -> tools\$n" }
    }
}

# ---------------------------------------------------------------- main
$tasks = [ordered]@{ aria2 = ${function:Get-Aria2}; ytdlp = ${function:Get-YtDlp}; ffmpeg = ${function:Get-Ffmpeg} }
foreach ($k in $tasks.Keys) {
    if ($Only -and ($Only -notcontains $k)) { continue }
    & $tasks[$k]
}

Write-Host ''
Write-Step '校验'
foreach ($n in 'aria2c.exe', 'yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe') {
    $p = Join-Path $ToolsDir $n
    if (Test-Path $p) {
        $sz = [math]::Round((Get-Item $p).Length / 1MB, 1)
        Write-Ok ("{0,-14} {1,7} MB" -f $n, $sz)
    } else {
        Write-Warn2 "$n 缺失"
    }
}
Write-Host ''
Write-Host '完成。' -ForegroundColor Cyan
