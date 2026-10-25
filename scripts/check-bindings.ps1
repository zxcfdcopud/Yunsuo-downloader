<#
    check-bindings.ps1 —— 守住"XAML 里绑定的命令，程序集里真的存在"这条底线。

    背景：CommunityToolkit.Mvvm 的 [RelayCommand] 只对**返回 Task 的方法**剥掉 "Async"
    后缀来生成命令名。所以 `private void RemoveAsync(...)` 生成的是 `RemoveAsyncCommand`，
    而 XAML 里写的是 `RemoveCommand` —— 绑定静默失败，按钮点了没反应，编译期也不报错。
    这个脚本就是把这类问题在构建后立刻报出来。

    用法：
        powershell -ExecutionPolicy Bypass -File scripts\check-bindings.ps1
        powershell -ExecutionPolicy Bypass -File scripts\check-bindings.ps1 -Configuration Release
    退出码：0 全部通过；1 有绑定找不到对应命令。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$XamlDir = Join-Path $Root 'src\YunsuoDownloader.App'
$Dll = Join-Path $Root "src\YunsuoDownloader.App\bin\$Configuration\net8.0-windows\YunsuoDownloader.dll"

Write-Host ''
Write-Host '==> 检查 XAML 命令绑定与程序集是否一致' -ForegroundColor Cyan

if (-not (Test-Path $Dll)) {
    Write-Host "找不到 $Dll ，请先构建（dotnet build YunsuoDownloader.sln -c $Configuration）" -ForegroundColor Yellow
    exit 2
}

# 程序集元数据里的字符串（#Strings 堆是 UTF-8），用它来判断某个命令属性是否存在
$meta = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($Dll))

$xamlFiles = Get-ChildItem $XamlDir -Recurse -Filter *.xaml
$bindings = @{}
foreach ($f in $xamlFiles) {
    $text = Get-Content -Raw -Encoding UTF8 $f.FullName
    # {Binding DataContext.XxxCommand, RelativeSource=...} 和 {Binding XxxCommand}
    foreach ($m in [regex]::Matches($text, '(?:DataContext\.)?([A-Za-z_][A-Za-z0-9_]*Command)\b')) {
        $name = $m.Groups[1].Value
        if (-not $bindings.ContainsKey($name)) { $bindings[$name] = @() }
        $bindings[$name] += $f.Name
    }
}

if ($bindings.Count -eq 0) { Write-Host '没有找到任何命令绑定。' -ForegroundColor Yellow; exit 0 }

$missing = @()
foreach ($name in ($bindings.Keys | Sort-Object)) {
    $ok = $meta.Contains($name)
    $files = ($bindings[$name] | Select-Object -Unique) -join ', '
    if ($ok) {
        Write-Host ("  [OK]   {0,-32} ({1})" -f $name, $files) -ForegroundColor Green
    } else {
        Write-Host ("  [缺失] {0,-32} ({1})" -f $name, $files) -ForegroundColor Red
        $missing += $name
    }
}

Write-Host ''
if ($missing.Count -gt 0) {
    Write-Host ("有 {0} 个绑定在程序集里找不到对应命令：" -f $missing.Count) -ForegroundColor Red
    foreach ($m in $missing) { Write-Host "    $m" }
    Write-Host ''
    Write-Host '常见原因：ViewModel 里 [RelayCommand] 标注的方法名带了 Async 后缀但返回 void ——' -ForegroundColor Yellow
    Write-Host '  返回 Task 的方法：XxxAsync → 生成 XxxCommand（会剥掉 Async）' -ForegroundColor Yellow
    Write-Host '  返回 void 的方法：XxxAsync → 生成 XxxAsyncCommand（不剥）；改名成 Xxx 即可' -ForegroundColor Yellow
    exit 1
}

Write-Host ("全部 {0} 个命令绑定都有效。" -f $bindings.Count) -ForegroundColor Green
exit 0
