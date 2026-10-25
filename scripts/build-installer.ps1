<#
    build-installer.ps1 —— 生成可双击安装的安装程序（Inno Setup）

    用法：
        powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
        powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Verify
        powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -SkipPublish -IsccPath "C:\...\ISCC.exe"

    它会依次完成：
        1) 找到 Inno Setup 的命令行编译器 ISCC.exe（找不到可以自动按用户安装，不需要管理员）
        2) 准备发布目录 artifacts\publish（没准备好就调用 scripts\build.ps1）
        3) 准备图标 installer\app.ico、中文语言包 installer\ChineseSimplified.isl
        4) 用 installer\YunsuoDownloader.iss 编译出
               artifacts\YunsuoDownloader-Setup-<版本>.exe
    加 -Verify 还会做一次静默安装 / 校验 / 卸载的往返测试。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$IsccPath,
    [switch]$SkipPublish,
    [switch]$InstallInnoSetup,
    [switch]$Verify,
    # 默认【自包含】：把 .NET 运行时一起打进安装包，装到"没装过 .NET 8 桌面运行时"的
    # 电脑上也不会出现「缺少 .NET」打不开。代价是安装包大约多 60~80 MB。
    # 确实想减体积（目标机器已装运行时）时加 -FrameworkDependent。
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root        = Split-Path -Parent $PSScriptRoot
$InstallerDir = Join-Path $Root 'installer'
$IssFile     = Join-Path $InstallerDir 'YunsuoDownloader.iss'
$PublishDir  = Join-Path $Root 'artifacts\publish'
$ArtifactsDir = Join-Path $Root 'artifacts'

function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "    OK  $m" -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "    !!  $m" -ForegroundColor Yellow }

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '   云梭下载器 安装包构建' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path $IssFile)) { throw "找不到安装脚本：$IssFile" }

# ---------------------------------------------------------------- 1) 找 ISCC.exe
function Find-Iscc {
    if ($IsccPath -and (Test-Path $IsccPath)) { return $IsccPath }

    $candidates = @()
    if ($env:ISCC_PATH) { $candidates += $env:ISCC_PATH }
    $candidates += @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    foreach ($hive in 'HKCU', 'HKLM') {
        foreach ($sub in 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                         'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1') {
            try {
                $loc = (Get-ItemProperty -Path "${hive}:\$sub" -ErrorAction Stop).InstallLocation
                if ($loc) { $candidates += (Join-Path $loc 'ISCC.exe') }
            } catch { }
        }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

function Install-InnoSetup {
    Write-Step '未找到 Inno Setup，按当前用户静默安装（不需要管理员）'

    $aria = Join-Path $Root 'tools\aria2c.exe'
    $cache = Join-Path $Root 'tools\.cache'
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $setup = Join-Path $cache 'innosetup.exe'

    # Inno Setup 官方仓库（jrsoftware.org/download.php 只是跳转页，这里直接取 release 资源）
    $url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'

    if (-not (Test-Path $setup) -or (Get-Item $setup).Length -lt 5MB) {
        if (Test-Path $aria) {
            & $aria -x8 -s8 --file-allocation=none --console-log-level=warn --summary-interval=0 `
                --check-certificate=false -d $cache -o innosetup.exe $url | Out-Null
        } else {
            Invoke-WebRequest $url -OutFile $setup -UseBasicParsing -TimeoutSec 300
        }
    }
    if (-not (Test-Path $setup) -or (Get-Item $setup).Length -lt 5MB) { throw 'Inno Setup 安装包下载失败' }
    Write-Ok "已下载 Inno Setup ($([math]::Round((Get-Item $setup).Length/1MB,1)) MB)"

    $dest = "$env:LOCALAPPDATA\Programs\Inno"
    $log = Join-Path $cache 'innosetup-install.log'
    $p = Start-Process -FilePath $setup -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER',
        "/DIR=$dest", "/LOG=$log"
    ) -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Inno Setup 安装失败（exit $($p.ExitCode)），日志：$log" }
    Write-Ok "已按用户安装到 $dest"
}

$iscc = Find-Iscc
if (-not $iscc) {
    Install-InnoSetup
    $iscc = Find-Iscc
}
if (-not $iscc) {
    throw '仍然找不到 ISCC.exe。请安装 Inno Setup 6（https://jrsoftware.org/isdl.php）或用 -IsccPath 指定。'
}
Write-Step "使用 Inno Setup 编译器"
Write-Ok $iscc

# ---------------------------------------------------------------- 2) 准备发布目录
if (-not $SkipPublish) {
    $selfContained = -not $FrameworkDependent
    Write-Step "准备发布目录（$Configuration，自包含=$selfContained）"
    $buildArgs = @('-Configuration', $Configuration, '-SkipTools')
    if ($selfContained) { $buildArgs += '-SelfContained' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 失败（exit $LASTEXITCODE）" }
}

if (-not (Test-Path (Join-Path $PublishDir 'YunsuoDownloader.exe'))) {
    throw "发布目录不完整：$PublishDir 里没有 YunsuoDownloader.exe。先运行 scripts\build.ps1。"
}
$publishSize = (Get-ChildItem $PublishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Ok ("发布目录 {0:N1} MB" -f ($publishSize / 1MB))

# ---------------------------------------------------------------- 3) 准备图标 / 语言包
Write-Step '准备附属文件（图标 / 中文语言包 / 文本编码）'

function Convert-ToUtf8Bom($path) {
    if (-not (Test-Path $path)) { return }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return }
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $true))
}

# 图标
$icoPath = Join-Path $InstallerDir 'app.ico'
$appIco  = Join-Path $Root 'src\YunsuoDownloader.App\Resources\app.ico'
if (-not (Test-Path $icoPath) -or -not (Test-Path $appIco)) {
    $py = $null
    foreach ($c in @('python', "$env:LOCALAPPDATA\..\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe")) {
        $x = Get-Command $c -ErrorAction SilentlyContinue
        if ($x) { $py = $x.Source; break }
    }
    if ($py) {
        & $py (Join-Path $InstallerDir 'make_icon.py')
        Write-Ok '已重新生成图标'
    } else {
        Write-Warn2 '找不到 python，跳过图标生成（使用仓库里已有的 app.ico）'
    }
}
if (-not (Test-Path $icoPath)) { throw "缺少图标文件：$icoPath" }
Write-Ok "图标 $icoPath"

# 简体中文语言包（Inno Setup 官方源码仓库里带、但安装包不自带）
$islPath = Join-Path $InstallerDir 'ChineseSimplified.isl'
if (-not (Test-Path $islPath)) {
    $url = 'https://raw.githubusercontent.com/jrsoftware/issrc/main/Files/Languages/ChineseSimplified.isl'
    Invoke-WebRequest $url -OutFile $islPath -UseBasicParsing -TimeoutSec 60
    Write-Ok '已获取简体中文语言包'
}
Convert-ToUtf8Bom $islPath

# Inno 读 UTF-8 文本需要 BOM，否则中文会乱码（.iss 里的中文字面量同理）
foreach ($t in @(
    $IssFile,
    (Join-Path $InstallerDir '安装前必读.txt'),
    (Join-Path $InstallerDir '安装后请看这里.txt'),
    (Join-Path $Root 'docs\EULA.txt'),
    (Join-Path $InstallerDir 'ChineseSimplified.isl')
)) { Convert-ToUtf8Bom $t }
Write-Ok '文本文件已统一为 UTF-8(BOM)'

# ---------------------------------------------------------------- 4) 编译
Write-Step '编译安装包（LZMA2 ultra 压缩，这一步比较慢）'
New-Item -ItemType Directory -Force -Path $ArtifactsDir | Out-Null

# 版本号从 iss 里读，避免和脚本硬编码不同步
$verMatch = Select-String -Path $IssFile -Pattern '#define\s+AppVersion\s+"([^"]+)"' | Select-Object -First 1
$appVersion = if ($verMatch) { $verMatch.Matches[0].Groups[1].Value } else { '1.0.0' }
$targetSetup = Join-Path $ArtifactsDir "YunsuoDownloader-Setup-$appVersion.exe"

# 上一个安装包如果还被占用（多半是上次运行的安装向导没关），ISCC 只会报一句很难懂的
# "Error 32: 另一个程序正在使用此文件"，这里提前给出人话提示。
if (Test-Path $targetSetup) {
    try {
        $fs = [System.IO.File]::Open($targetSetup, 'Open', 'ReadWrite', 'None')
        $fs.Close()
    } catch {
        throw "安装包被占用，无法覆盖：$targetSetup`n请先关掉正在运行的安装向导（或在任务管理器里结束 YunsuoDownloader-Setup-* 进程），然后重试。"
    }
}

& $iscc "/Q" $IssFile
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（exit $LASTEXITCODE）" }

$setupExe = Get-ChildItem $ArtifactsDir -Filter "YunsuoDownloader-Setup-$appVersion.exe" |
            Select-Object -First 1
if (-not $setupExe) { throw '没有找到编译产物' }

Write-Host ''
Write-Step '安装包已生成'
Write-Host ("    路径：{0}" -f $setupExe.FullName)
Write-Host ("    大小：{0:N1} MB" -f ($setupExe.Length / 1MB))
Write-Host ''
Write-Host '分发与合规提示：' -ForegroundColor Cyan
Write-Host '    · tools\ 内的 aria2c.exe 与 ffmpeg.exe 是 GPL 系许可证；本安装包已把它们的内容打进包里，'
Write-Host '      再分发时请同时提供 docs\第三方开源项目清单.txt（内含源码获取地址）。'
Write-Host '    · 安装包默认按当前用户安装（无需管理员）；管理员运行时可在向导里切换为全机器安装。'

# ---------------------------------------------------------------- 5) 可选：静默安装往返测试
if ($Verify) {
    Write-Host ''
    Write-Step '往返测试：静默安装 → 校验 → 静默卸载'
    $testDir = Join-Path $env:TEMP ('ysd-setup-test-' + (Get-Date -Format 'HHmmss'))
    $logFile = "$testDir-install.log"
    try {
        $ip = Start-Process -FilePath $setupExe.FullName -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$testDir",
            '/MERGETASKS=!desktopicon,!startup', "/LOG=$logFile"
        ) -PassThru -Wait
        Write-Ok "安装程序退出码 $($ip.ExitCode)"

        $need = @('YunsuoDownloader.exe', 'ysd.exe', 'tools\aria2c.exe', 'tools\yt-dlp.exe',
                  'tools\ffmpeg.exe', 'extension\manifest.json', 'docs\使用手册.txt',
                  'docs\EULA.txt', 'unins000.exe')
        $missing = @()
        foreach ($f in $need) { if (-not (Test-Path (Join-Path $testDir $f))) { $missing += $f } }
        if ($missing.Count -gt 0) { Write-Warn2 ('安装后缺少：' + ($missing -join ', ')) }
        else { Write-Ok '安装后的文件齐全（程序 / 命令行 / 三件套 / 插件 / 文档 / 卸载器）' }

        $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\云梭下载器'
        Write-Ok ("开始菜单项：{0} 个" -f (Get-ChildItem $startMenu -ErrorAction SilentlyContinue).Count)

        $uninst = Join-Path $testDir 'unins000.exe'
        if (Test-Path $uninst) {
            $up = Start-Process -FilePath $uninst -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru -Wait
            Write-Ok "卸载程序退出码 $($up.ExitCode)"
            Start-Sleep -Seconds 2
            $left = Test-Path (Join-Path $testDir 'YunsuoDownloader.exe')
            if ($left) { Write-Warn2 "卸载后仍残留文件：$testDir" } else { Write-Ok '卸载干净（用户数据按设计保留）' }
        }
    } finally {
        if (Test-Path $testDir) { Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue }
        if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ''
Write-Host '完成。' -ForegroundColor Cyan
