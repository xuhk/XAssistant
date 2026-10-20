<#
.SYNOPSIS
    构建 XAssistant 并部署到 C:\XAssistant
.DESCRIPTION
    1. 使用 Release 配置发布项目
    2. 若目标应用正在运行，强制结束进程
    3. 备份 C:\XAssistant 下的旧文件（如有）
    4. 将发布输出复制到 C:\XAssistant
    5. 启动应用
.NOTES
    脚本需在项目根目录（XAssistant.csproj 所在目录）以管理员身份运行，
    因为 C:\XAssistant 可能需要管理员权限。
#>

$ErrorActionPreference = "Stop"

# ---------- 配置 ----------
$scriptPath   = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile  = Join-Path $scriptPath "XAssistant.csproj"
$targetDir    = "C:\XAssistant"
$processName  = "XAssistant"
$publishDir   = Join-Path $scriptPath "bin\Release\net10.0-windows\win-x64\publish"

# ---------- 1. 构建发布 ----------
Write-Host "[1/5] 正在发布项目 (Release)..." -ForegroundColor Cyan
dotnet publish $projectFile -r win-x64 -c Release
if ($LASTEXITCODE -ne 0) {
    throw "发布失败，请检查错误信息。"
}
Write-Host "    发布完成: $publishDir" -ForegroundColor Green

# ---------- 2. 停止正在运行的实例 ----------
Write-Host "[2/5] 检查是否正在运行..." -ForegroundColor Cyan
$runningProcess = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($runningProcess) {
    Write-Host "    发现运行中的进程，正在停止..." -ForegroundColor Yellow
    Stop-Process -Name $processName -Force
    Start-Sleep -Seconds 2
    Write-Host "    已停止。" -ForegroundColor Green
} else {
    Write-Host "    未检测到运行实例。" -ForegroundColor Green
}

# ---------- 3. 备份旧版本 ----------
Write-Host "[3/5] 备份旧版本..." -ForegroundColor Cyan
if (Test-Path $targetDir) {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupDir = "C:\XAssistant_Backup_$timestamp"
    Write-Host "    正在备份到 $backupDir ..." -ForegroundColor Yellow
    Copy-Item -Path $targetDir -Destination $backupDir -Recurse -Force
    Write-Host "    备份完成。" -ForegroundColor Green
} else {
    Write-Host "    目标目录不存在，无需备份。" -ForegroundColor Green
}

# ---------- 4. 复制新文件 ----------
Write-Host "[4/5] 复制新文件到 $targetDir ..." -ForegroundColor Cyan
# 确保目标目录存在
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

# 清空目标目录（避免残留旧文件），但保留后台服务目录：
# XAssistant.Service 由服务项目单独发布/安装，不属于主程序产物。
Get-ChildItem -Path $targetDir -Force |
    Where-Object { $_.Name -ne "XAssistant.Service" } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# 复制所有发布文件
Copy-Item -Path "$publishDir\*" -Destination $targetDir -Recurse -Force
Write-Host "    复制完成。" -ForegroundColor Green

# ---------- 5. 启动应用 ----------
Write-Host "[5/5] 启动 XAssistant..." -ForegroundColor Cyan
$appExe = Join-Path $targetDir "XAssistant.exe"
if (Test-Path $appExe) {
    Start-Process $appExe
    Write-Host "    应用已启动。" -ForegroundColor Green
} else {
    throw "未找到 $appExe ，部署可能不完整。"
}

Write-Host "`n部署成功！" -ForegroundColor Green