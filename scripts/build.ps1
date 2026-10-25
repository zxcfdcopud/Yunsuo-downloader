<#
    build.ps1  ——  一键构建 + 发布
    用法：
        powershell -ExecutionPolicy Bypass -File scripts\build.ps1
        powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Release -SelfContained
        powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SkipTools     # 不重复下载三件套

    产物：
        artifacts\publish\YunsuoDownloader.exe   桌面程序（自包含）
        artifacts\publish\ysd.exe                命令行工具
        artifacts\publish\tools\                 aria2c / yt-dlp / ffmpeg / ffprobe
        artifacts\publish\extension\             浏览器插件
        artifacts\publish\docs\                  文档
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SelfContained,
    [switch]$SkipTools,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = Split-Path -Parent $PSScriptRoot
$OutDir = Join-Path $Root 'artifacts\publish'

function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok($m) { Write-Host "    OK  $m" -ForegroundColor Green }
function Write-Warn2($m) { Write-Host "    !!  $m" -ForegroundColor Yellow }

# ---------------------------------------------------------------- 定位 dotnet
function Resolve-Dotnet {
    if ($DotnetPath -and (Test-Path $DotnetPath)) { return $DotnetPath }

    $candidates = @()
    $userSdk = Join-Path $env:USERPROFILE '.dotnet-sdk\dotnet.exe'
    if (Test-Path $userSdk) { $candidates += $userSdk }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $default = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path $default) { $candidates += $default }

    foreach ($c in $candidates) {
        try {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks) { return $c }
        } catch { }
    }
    throw '没有找到可用的 .NET SDK。请先安装 .NET 8 SDK（https://dotnet.microsoft.com/download），或用 -DotnetPath 指定 dotnet.exe。'
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '   云梭下载器 构建脚本' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

$dotnet = Resolve-Dotnet
Write-Step "使用 dotnet：$dotnet"
Write-Host "    $(& $dotnet --version)"

# ---------------------------------------------------------------- 三件套
if (-not $SkipTools) {
    $need = @()
    foreach ($t in 'aria2c.exe', 'yt-dlp.exe', 'ffmpeg.exe') {
        if (-not (Test-Path (Join-Path $Root "tools\$t"))) { $need += $t }
    }
    if ($need.Count -gt 0) {
        Write-Step "缺少 $($need -join ', ')，开始下载（首次会比较慢）"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch-tools.ps1')
    } else {
        Write-Step '三件套已就绪，跳过下载'
    }
} else {
    Write-Warn2 '按参数要求跳过三件套检查'
}

# ---------------------------------------------------------------- 构建
Write-Step "构建解决方案（$Configuration）"
Set-Location $Root
& $dotnet build 'YunsuoDownloader.sln' -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }
Write-Ok '构建成功'

# ---------------------------------------------------------------- 自检：XAML 命令绑定
# [RelayCommand] 生成的命令名很容易和 XAML 里的绑定对不上（而且编译期不报错、
# 运行时静默失效），所以每次构建后都跑一遍一致性检查。
Write-Step '检查 XAML 命令绑定'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'check-bindings.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw '有 XAML 命令绑定与 ViewModel 不匹配，详见上面的输出。' }
Write-Ok '命令绑定检查通过'

# ---------------------------------------------------------------- 发布
if (Test-Path $OutDir) {
    try {
        Remove-Item $OutDir -Recurse -Force -ErrorAction Stop
    } catch {
        # 常见原因：上一次跑起来的 YunsuoDownloader / aria2c / yt-dlp / ffmpeg 还在占用这些 exe
        Write-Warn2 "旧发布目录清理不完整：$($_.Exception.Message)"
        Write-Warn2 '请先关掉正在运行的 YunsuoDownloader.exe 以及它拉起的 aria2c.exe / yt-dlp.exe / ffmpeg.exe，然后重试。'
        Write-Warn2 '本次将改为增量覆盖（旧的多余文件可能残留）。'
    }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$publishArgs = @('publish', 'src\YunsuoDownloader.App\YunsuoDownloader.App.csproj',
    '-c', $Configuration, '-o', $OutDir, '--nologo')
if ($SelfContained) {
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'true')
    $publishArgs += '-p:PublishSingleFile=false'
} else {
    $publishArgs += @('-r', 'win-x64', '--self-contained', 'false')
}

Write-Step '发布桌面程序'
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "发布桌面程序失败（exit $LASTEXITCODE）" }

Write-Step '发布命令行工具'
# CLI 必须和桌面程序用同一套自包含设置：
# 否则安装包装到没装 .NET 的电脑上时，YunsuoDownloader.exe 能开、ysd.exe 却报
# "You must install or update .NET to run this application"（踩过）。
$cliArgs = @('publish', 'src\YunsuoDownloader.Cli\YunsuoDownloader.Cli.csproj',
    '-c', $Configuration, '-o', $OutDir, '--nologo',
    '-r', 'win-x64', '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }))
& $dotnet @cliArgs
if ($LASTEXITCODE -ne 0) { throw "发布命令行工具失败（exit $LASTEXITCODE）" }

# ---------------------------------------------------------------- 附带资源
Write-Step '复制三件套 / 插件 / 文档'
$toolsSrc = Join-Path $Root 'tools'
$toolsDst = Join-Path $OutDir 'tools'
New-Item -ItemType Directory -Force -Path $toolsDst | Out-Null
foreach ($t in 'aria2c.exe', 'yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe') {
    $src = Join-Path $toolsSrc $t
    if (Test-Path $src) { Copy-Item $src $toolsDst -Force; Write-Ok $t } else { Write-Warn2 "$t 缺失" }
}
# 许可证文本（GPL 合规：随包附带）
$cache = Join-Path $toolsSrc '.cache'
if (Test-Path $cache) {
    Get-ChildItem $cache -Recurse -Include 'COPYING', 'LICENSE*', 'AUTHORS' -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            try { Copy-Item $_.FullName (Join-Path $toolsDst ("LICENSE-" + $_.Name)) -Force } catch { }
        }
}

# 插件：publish 已经通过 App.csproj 的 CopyToPublishDirectory 带了一份，
# 这里只做「内容级」补齐 —— 注意必须用 extension\* 而不是 extension，
# 否则目标目录已存在时 Copy-Item -Recurse 会把整个 extension 文件夹塞进去，
# 变成发布目录里的 extension\extension\ 双层（之前就踩过）。
$extDst = Join-Path $OutDir 'extension'
New-Item -ItemType Directory -Force -Path $extDst | Out-Null
Copy-Item -Path (Join-Path $Root 'extension\*') -Destination $extDst -Recurse -Force

# 插件同时被内置进 YunsuoDownloader.Core.dll；这里再确认发布目录里那份没漏
# （就算漏了，「浏览器插件」页也会退回到程序内置的副本，并支持一键导出）
$extCount = (Get-ChildItem $extDst -Recurse -File -ErrorAction SilentlyContinue).Count
$extDuped = -not (Test-Path (Join-Path $extDst 'manifest.json')) -or
            (Test-Path (Join-Path $extDst 'extension'))      # 双层嵌套是错的
if ($extCount -gt 0 -and -not $extDuped) {
    Write-Ok "extension\（$extCount 个文件，含 manifest.json，无嵌套）"
} elseif ($extDuped) {
    Write-Warn2 "发布目录里的 extension\ 结构不对（文件数 $extCount）：不能有 extension\extension\ 嵌套，请检查 build.ps1 的复制方式"
} else {
    Write-Warn2 "发布目录里的 extension\ 不完整（文件数 $extCount）；程序运行时会用内置副本兜底"
}
$docsDst = Join-Path $OutDir 'docs'
New-Item -ItemType Directory -Force -Path $docsDst | Out-Null
Copy-Item -Path (Join-Path $Root 'docs\*') -Destination $docsDst -Recurse -Force
Copy-Item (Join-Path $Root 'README.md') $OutDir -Force

# 一份默认更新源示例
$updateSample = @'
{
  "version": "1.0.0",
  "url": "",
  "notes": "把 url 换成本次发布安装包的下载地址；版本号高于当前程序版本时，程序会提示更新。",
  "sha256": ""
}
'@
$updateSample | Out-File -FilePath (Join-Path $OutDir 'update.json') -Encoding UTF8

# ---------------------------------------------------------------- 汇总
Write-Host ''
Write-Step '发布完成'
$size = (Get-ChildItem $OutDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("    目录：{0}" -f $OutDir)
Write-Host ("    大小：{0:N1} MB" -f ($size / 1MB))
Write-Host ''
Write-Host '运行：' -ForegroundColor Cyan
Write-Host ("    {0}" -f (Join-Path $OutDir 'YunsuoDownloader.exe'))
Write-Host ''
Write-Host '提示：' -ForegroundColor Cyan
Write-Host '    1) tools\ 内的 aria2c.exe 与 ffmpeg.exe 是 GPL 系许可证，再分发时请一并附带许可证文本。'
Write-Host '    2) 浏览器插件在 extension\ 目录，需要手动在浏览器里"加载已解压的扩展程序"。'
Write-Host '    3) 发布包默认自包含；如果目标机器已装 .NET 8 桌面运行时，可去掉 -SelfContained 减小体积。'
