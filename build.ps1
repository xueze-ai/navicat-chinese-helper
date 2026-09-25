$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dir

$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path (Join-Path $fw "csc.exe"))) { $fw = "C:\Windows\Microsoft.NET\Framework\v4.0.30319" }
if (-not (Test-Path (Join-Path $fw "csc.exe"))) {
    Write-Host "[错误] 找不到系统自带的 C# 编译器 csc.exe，请确认已安装 .NET Framework 4.x" -ForegroundColor Red
    exit 1
}

$exe = Join-Path $dir "Navicat中文助手.exe"
Get-Process | Where-Object { try { $_.Path -eq $exe } catch { $false } } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Write-Host "正在编译 ..."
& (Join-Path $fw "csc.exe") /nologo /target:winexe /platform:x64 /codepage:65001 `
    /out:"$exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.dll /r:System.Web.Extensions.dll `
    (Join-Path $dir "NavicatChineseHelper.cs")

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[失败] 编译出错，请查看上面的提示。" -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "[完成] 已生成 Navicat中文助手.exe" -ForegroundColor Green