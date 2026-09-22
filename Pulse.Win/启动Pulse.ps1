# Pulse 一键启动 — PowerShell 版（支持中文输出）
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '[Pulse] 未找到 dotnet，请先安装 .NET 10 SDK：' -ForegroundColor Yellow
    Write-Host '  winget install Microsoft.DotNet.SDK.10'
    exit 1
}

Write-Host '[Pulse] 启动中… 快捷键 Ctrl+Alt+P 切换工具条' -ForegroundColor Cyan
dotnet run --project src/Pulse.App -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '[Pulse] 启动失败。可先完整构建查看错误：' -ForegroundColor Yellow
    Write-Host '  dotnet build Pulse.Win.slnx -c Release'
    exit 1
}
