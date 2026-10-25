@echo off
chcp 65001 >nul
REM 启动 YunsuoDownloader 测试页静态服务器（仅用 Node 内置模块，无需 npm install）
REM 用法：双击本文件，或在命令行里运行  run.bat [端口]
set PORT=%1
if "%PORT%"=="" set PORT=8000
pushd "%~dp0"
echo 启动测试页服务器： http://127.0.0.1:%PORT%/
echo 按 Ctrl+C 停止。
node serve.mjs %PORT%
popd
