<#
.SYNOPSIS
    发布并安装 XAssistant.UsageTracker Windows 服务
.DESCRIPTION
    1. 使用 dotnet publish 发布项目到指定目录
    2. 以管理员权限新创建 Windows 服务（自动处理“服务已标记为删除”问题）
    3. 设置服务描述
    4. 启动服务
.NOTES
    需要以管理员身份运行此脚本
#>

param(
    [string]$ServiceName = "XAssistant.UsageTracker",
    [string]$DisplayName = "XAssistant 使用时长追踪服务",
    [string]$Description = "负责记录每日 PC 使用时长，通过 Named Pipe 向客户端提供数据。",
    [string]$PublishOutput = ".\publish",
    [string]$ProjectPath = ".\XAssistant.Service.csproj"  # 根据实际位置调整
)

# 遇到错误立即停止
$ErrorActionPreference = "Stop"

# 确保使用管理员权限
if (-NOT ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Error "请以管理员身份运行此脚本！"
    exit 1
}

# ===== 1. 先处理已存在的服务（确保文件解锁） =====
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "发现已安装的服务，正在停止..." -ForegroundColor Yellow
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue

    # 等待服务进程退出（关键！）
    Write-Host "等待服务进程退出..." -ForegroundColor Yellow
    $serviceProcess = Get-Process -Name "XAssistant.Service" -ErrorAction SilentlyContinue
    if ($serviceProcess) {
        $serviceProcess.WaitForExit(10000)  # 最多等 10 秒
        # 若仍未退出，可强制结束
        if (-not $serviceProcess.HasExited) {
            Write-Warning "服务进程未正常退出，尝试强制终止..."
            $serviceProcess.Kill()
            $serviceProcess.WaitForExit(5000)
        }
    }

    Write-Host "正在删除服务..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null

    # 等待服务注册表项完全移除（避免"标记为删除"）
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

# ===== 2. 现在可以安全发布（文件不会被占用） =====
Write-Host ">>> 正在发布项目(自包含,win-x64)..." -ForegroundColor Cyan
dotnet publish $ProjectPath -c Release -o $PublishOutput --self-contained true -r win-x64
if ($LASTEXITCODE -ne 0) {
    Write-Error "发布失败，请检查错误。"
    exit $LASTEXITCODE
}
Write-Host ">>> 发布完成，输出目录: $PublishOutput" -ForegroundColor Green

# 转换为绝对路径
$PublishOutput = (Resolve-Path $PublishOutput).Path
$exePath = Join-Path $PublishOutput "XAssistant.Service.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "未找到服务可执行文件: $exePath"
    exit 1
}
$binPath = "`"$exePath`""

# ===== 3. 创建新服务 =====
Write-Host ">>> 正在创建服务..." -ForegroundColor Cyan
New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $DisplayName -StartupType Automatic
if ($? -eq $false) {  # 注意 New-Service 成功时不设置 $LASTEXITCODE，改用 $?
    Write-Error "服务创建失败。"
    exit 1
}

# 设置描述
Write-Host ">>> 设置服务描述..." -ForegroundColor Cyan
sc.exe description $ServiceName $Description
if ($LASTEXITCODE -ne 0) {
    Write-Error "设置服务描述失败。"
    exit $LASTEXITCODE
}

# 失败自动重启：开机瞬间盘还没就绪时服务会起不来（事件日志 7000），
# 没有恢复动作就会一直缺席到下次手动启动。
Write-Host ">>> 设置失败自动重启..." -ForegroundColor Cyan
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "设置失败自动重启失败，服务仍可手动启动。"
}

# ===== 4. 启动服务 =====
Write-Host ">>> 正在启动服务..." -ForegroundColor Cyan
Start-Service $ServiceName
Write-Host ">>> 服务 $ServiceName 已成功安装并启动。" -ForegroundColor Green