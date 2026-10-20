<#
.SYNOPSIS
    从已构建产物安装 XAssistant.UsageTracker Windows 服务（无需 .NET SDK）
.DESCRIPTION
    适用于本目录下已包含 XAssistant.Service.exe 等发布产物的情况；
    需要构建源码 + 发布请使用 publish-and-install.ps1。
.NOTES
    需要以管理员身份运行此脚本
#>

param(
    [string]$ServiceName = "XAssistant.UsageTracker",
    [string]$DisplayName = "XAssistant 使用时长追踪服务",
    [string]$Description = "负责记录每日 PC 使用时长，通过 Named Pipe 向客户端提供数据。"
)

# 遇到错误立即停止
$ErrorActionPreference = "Stop"

# 确保使用管理员权限
if (-NOT ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Error "请以管理员身份运行此脚本！"
    exit 1
}

# ===== 1. 定位可执行文件 =====
$exePath = Join-Path $PSScriptRoot "XAssistant.Service.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "未找到服务可执行文件: $exePath"
    exit 1
}

# ===== 2. 处理已存在的服务（确保文件解锁） =====
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "发现已安装的服务，正在停止..." -ForegroundColor Yellow
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue

    Write-Host "等待服务进程退出..." -ForegroundColor Yellow
    $serviceProcess = Get-Process -Name "XAssistant.Service" -ErrorAction SilentlyContinue
    if ($serviceProcess) {
        $serviceProcess.WaitForExit(10000)
        if (-not $serviceProcess.HasExited) {
            Write-Warning "服务进程未正常退出，尝试强制终止..."
            $serviceProcess.Kill()
            $serviceProcess.WaitForExit(5000)
        }
    }

    Write-Host "正在删除服务..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null

    $retries = 10
    do {
        Start-Sleep -Seconds 2
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        $retries--
    } while ($existing -and $retries -gt 0)
    if ($existing) {
        Write-Error "服务删除超时，请重启计算机后再试。"
        exit 1
    }
    Write-Host "旧服务已完全移除，文件锁已解除。" -ForegroundColor Green
}

# ===== 3. 创建并启动服务 =====
Write-Host ">>> 正在创建服务..." -ForegroundColor Cyan
New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" -DisplayName $DisplayName -StartupType Automatic
if (-not $?) {
    Write-Error "服务创建失败。"
    exit 1
}

Write-Host ">>> 设置服务描述..." -ForegroundColor Cyan
sc.exe description $ServiceName $Description

# 失败自动重启：开机瞬间盘还没就绪时服务会起不来（事件日志 7000），
# 没有恢复动作就会一直缺席到下次手动启动。
Write-Host ">>> 设置失败自动重启..." -ForegroundColor Cyan
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "设置失败自动重启失败，服务仍可手动启动。"
}

Write-Host ">>> 正在启动服务..." -ForegroundColor Cyan
Start-Service $ServiceName
Write-Host ">>> 服务 $ServiceName 已成功安装并启动。" -ForegroundColor Green