<#
  云梭下载器 官网 — 截图 / 验收包装脚本

  用法：
    powershell -ExecutionPolicy Bypass -File scripts\screenshot-website.ps1
    powershell -ExecutionPolicy Bypass -File scripts\screenshot-website.ps1 -StaticOnly
    powershell -ExecutionPolicy Bypass -File scripts\screenshot-website.ps1 -Open

  实际工作由 scripts\verify-website.mjs 完成（零依赖，走 Chrome headless + CDP）：
    - 静态检查：类名双向对账、禁用模式、链接目标、语义/无障碍、事实出处
    - 浏览器检查：控制台、外部请求、断点横向溢出、reduced-motion、字体真加载、重阴影/大圆角
    - 截图输出到 website\tools\shots\desktop-1440.png 与 mobile-390.png
#>
[CmdletBinding()]
param(
  [switch]$StaticOnly,
  [switch]$Json,
  [switch]$Open
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'verify-website.mjs'
$shots = Join-Path $root 'website\tools\shots'

if (-not (Test-Path $script)) { throw "找不到验收脚本：$script" }

$node = (Get-Command node -ErrorAction SilentlyContinue)
if (-not $node) { throw '需要 Node.js（用于验收脚本），未在 PATH 中找到 node。' }

$argv = @($script)
if ($StaticOnly) { $argv += '--no-browser' }
if ($Json) { $argv += '--json' }

Write-Host "运行：node $($argv -join ' ')" -ForegroundColor Cyan
& $node.Source @argv
$code = $LASTEXITCODE

if (-not $StaticOnly) {
  if (Test-Path $shots) {
    Write-Host "`n截图目录：$shots" -ForegroundColor Cyan
    Get-ChildItem $shots -Filter *.png | ForEach-Object {
      Write-Host ("  {0}  ({1:N0} KB)" -f $_.Name, ($_.Length / 1KB))
    }
    if ($Open) { Start-Process (Join-Path $shots 'desktop-1440.png') }
  } else {
    Write-Host "`n未生成截图目录（浏览器阶段可能失败）。" -ForegroundColor Yellow
  }
}

exit $code
