<#
.SYNOPSIS
    安装或升级 XAssistant。
.DESCRIPTION
    Release 包内运行时：
      不带参数        安装/升级主程序和后台服务
      -AppOnly        只安装/升级主程序（不改动后台服务）
      -ServiceOnly    只安装/更新后台服务（不改动主程序）
    源码目录运行时：构建并部署主程序；后台服务请用 XAssistant.Service\publish-and-install.ps1。
.PARAMETER InstallDir
    指定安装目录。不指定时优先从运行中的 XAssistant、自启任务或后台服务反查。
.PARAMETER AppOnly
    只处理主程序。
.PARAMETER ServiceOnly
    只处理后台服务。
.PARAMETER CheckOnly
    只打印识别结果，不做任何修改。
#>

[CmdletBinding()]
param(
    [string]$InstallDir = "",
    [switch]$AppOnly,
    [switch]$ServiceOnly,
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"
$processName = "XAssistant"
$serviceName = "XAssistant.UsageTracker"
$scriptDir = $PSScriptRoot
$hasSource = Test-Path (Join-Path $scriptDir "XAssistant.csproj")
if ($hasSource) {
    $packageDir = $scriptDir
    $packageAppDir = $null
} else {
    $packageAppDir = $scriptDir
    $packageDir = Split-Path -Parent $scriptDir
}
$isSelfContained = -not $hasSource -and (Test-Path (Join-Path $packageAppDir "coreclr.dll"))
$runtimeUrl = "https://dotnet.microsoft.com/download/dotnet/10.0"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-DotNet10DesktopRuntime {
    $roots = @()
    if ($env:ProgramFiles) {
        $roots += Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.WindowsDesktop.App"
    }
    if (${env:ProgramFiles(x86)}) {
        $roots += Join-Path ${env:ProgramFiles(x86)} "dotnet\shared\Microsoft.WindowsDesktop.App"
    }
    foreach ($root in $roots) {
        if ((Test-Path $root) -and (Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^10\.' })) {
            return $true
        }
    }
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $runtimes = & dotnet --list-runtimes 2>$null
        if ($runtimes -match 'Microsoft\.WindowsDesktop\.App 10\.') {
            return $true
        }
    }
    return $false
}

function Resolve-InstallDir {
    param([string]$Override)
    if (-not [string]::IsNullOrWhiteSpace($Override)) {
        return $Override
    }
    $running = Get-Process -Name $processName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running -and $running.Path) {
        return Split-Path -Parent $running.Path
    }
    try {
        $task = Get-ScheduledTask -TaskName $processName -ErrorAction SilentlyContinue
        if ($task) {
            $exe = ($task.Actions | Select-Object -First 1).Execute
            if ($exe -and (Test-Path $exe)) {
                return Split-Path -Parent $exe
            }
        }
    } catch { }
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if ($service -and $service.PathName) {
        $serviceExe = $service.PathName.Trim('"')
        $candidate = Split-Path -Parent (Split-Path -Parent $serviceExe)
        if (Test-Path (Join-Path $candidate "XAssistant.Service\XAssistant.Service.exe")) {
            return $candidate
        }
    }
    return $null
}

# 处理范围
if ($hasSource) {
    if ($ServiceOnly) {
        throw "源码模式请用 XAssistant.Service\publish-and-install.ps1 更新后台服务。"
    }
    $doApp = $true
    $doService = $false
} else {
    if ($AppOnly -and $ServiceOnly) {
        throw "不能同时指定 -AppOnly 和 -ServiceOnly。"
    }
    $doApp = -not $ServiceOnly
    $doService = -not $AppOnly
}
if ($doApp -and $doService) {
    $modeText = "主程序 + 后台服务"
} elseif ($doApp) {
    $modeText = "仅主程序"
} else {
    $modeText = "仅后台服务"
}

if (-not $CheckOnly -and -not (Test-IsAdmin)) {
    Write-Host "需要管理员权限，正在请求提权..." -ForegroundColor Yellow
    $argumentLine = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if (-not [string]::IsNullOrWhiteSpace($InstallDir)) {
        $argumentLine += " -InstallDir `"$InstallDir`""
    }
    if ($AppOnly) { $argumentLine += " -AppOnly" }
    if ($ServiceOnly) { $argumentLine += " -ServiceOnly" }
    try {
        Start-Process powershell.exe -Verb RunAs -ArgumentList $argumentLine
    } catch {
        Write-Host "提权被取消，安装未进行。" -ForegroundColor Red
        exit 1
    }
    exit
}

# 源目录：源码模式先构建；Release 包模式直接用当前目录
if ($hasSource) {
    $sourceDir = Join-Path $packageDir "bin\Release\net10.0-windows\win-x64\publish"
    $sourceServiceDir = $null
} else {
    $sourceDir = $packageAppDir
    $sourceServiceDir = Join-Path $packageDir "XAssistant.Service"
}

$appSource = Join-Path $sourceDir "XAssistant.exe"
if ($doApp -and -not $hasSource -and -not (Test-Path $appSource)) {
    throw "没有找到 XAssistant.exe：$sourceDir"
}
if ($doService -and -not (Test-Path (Join-Path $sourceServiceDir "XAssistant.Service.exe"))) {
    throw "安装包缺少后台服务文件：$sourceServiceDir"
}

$runtimeOk = $isSelfContained -or (Test-DotNet10DesktopRuntime)
if (-not $CheckOnly -and -not $runtimeOk) {
    Write-Host "缺少 .NET 10 Desktop Runtime，程序无法运行。" -ForegroundColor Red
    Write-Host "下载地址：$runtimeUrl"
    $open = Read-Host "是否现在打开下载页？(Y/n)"
    if ($open -notmatch '^[nN]') {
        Start-Process $runtimeUrl
    }
    Read-Host "安装完成后重新运行本脚本。按回车退出"
    exit 1
}

# 安装目录
$targetDir = Resolve-InstallDir $InstallDir
if ([string]::IsNullOrWhiteSpace($targetDir)) {
    if ($CheckOnly) {
        Write-Host "未找到现有安装（正式运行会提示输入目录，默认 C:\XAssistant）。" -ForegroundColor Yellow
        $targetDir = "C:\XAssistant"
    } else {
        $entered = Read-Host "没有找到现有安装。请输入安装目录（直接回车使用 C:\XAssistant）"
        if ([string]::IsNullOrWhiteSpace($entered)) {
            $entered = "C:\XAssistant"
        }
        $targetDir = $entered
    }
}
if (-not [IO.Path]::IsPathRooted($targetDir)) {
    $targetDir = Join-Path $packageDir $targetDir
}
$targetDir = [IO.Path]::GetFullPath($targetDir)
if ($targetDir -match '^[A-Za-z]:\\?$') {
    throw "安装目录不能是盘符根目录：$targetDir"
}
$targetDir = $targetDir.TrimEnd('\')
$appExe = Join-Path $targetDir "XAssistant.exe"
$targetServiceDir = Join-Path $targetDir "XAssistant.Service"
$installInPlace = $targetDir -ieq $packageDir.TrimEnd('\')
if ($installInPlace) {
    throw "不要直接安装到解压目录；请换一个安装目录（例如 C:\XAssistant）。"
}
if ($packageAppDir -and ($targetDir -ieq $packageAppDir.TrimEnd('\'))) {
    throw "不要直接安装到 app 目录；请换一个安装目录（例如 C:\XAssistant）。"
}

$looksLikeInstall = (Test-Path $appExe) -or (Test-Path (Join-Path $targetServiceDir "XAssistant.Service.exe"))
if ((Test-Path $targetDir) -and -not $installInPlace -and -not $looksLikeInstall) {
    $existing = @(Get-ChildItem -Path $targetDir -Force -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        throw "目标目录不是空的，也不是 XAssistant 安装目录：$targetDir"
    }
}

Write-Host "安装目录: $targetDir" -ForegroundColor Cyan
Write-Host "文件来源: $sourceDir" -ForegroundColor Cyan
Write-Host "模式: $modeText" -ForegroundColor Cyan
if ($isSelfContained) {
    Write-Host ".NET 运行时: 包内自带" -ForegroundColor Cyan
} elseif ($runtimeOk) {
    Write-Host ".NET 10 Desktop Runtime: 已安装" -ForegroundColor Cyan
} else {
    Write-Host ".NET 10 Desktop Runtime: 缺失" -ForegroundColor Red
}

if ($CheckOnly) {
    Write-Host "检查完成，未做任何修改。" -ForegroundColor Green
    if (-not $runtimeOk) { exit 1 }
    exit 0
}

$failed = $false
try {
    if ($hasSource -and $doApp) {
        Write-Host "[1/5] 构建主程序..." -ForegroundColor Cyan
        dotnet publish (Join-Path $packageDir "XAssistant.csproj") -r win-x64 -c Release --self-contained false
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish 失败。"
        }
        if (-not (Test-Path $appSource)) {
            throw "构建后仍找不到 XAssistant.exe：$sourceDir"
        }
    } else {
        Write-Host "[1/5] 使用安装包里的文件。" -ForegroundColor Cyan
    }

    if ($doApp) {
        Write-Host "[2/5] 结束主程序..." -ForegroundColor Cyan
        Stop-Process -Name $processName -Force -ErrorAction SilentlyContinue
        Get-Process -Name $processName -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }

    if ($doService) {
        Write-Host "[3/5] 停止后台服务..." -ForegroundColor Cyan
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Get-Process -Name "XAssistant.Service" -ErrorAction SilentlyContinue | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }

    Write-Host "[4/5] 备份并替换文件..." -ForegroundColor Cyan
    if (-not $installInPlace) {
        if (Test-Path $targetDir) {
            $backupParent = Split-Path -Parent $targetDir
            $backupDir = Join-Path $backupParent ("XAssistant_Backup_" + (Get-Date -Format "yyyyMMdd_HHmmss"))
            Write-Host "    备份到 $backupDir ..."
            Copy-Item -Path $targetDir -Destination $backupDir -Recurse -Force
            Get-ChildItem -Path $backupParent -Directory -Filter "XAssistant_Backup_*" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -Skip 3 |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        }
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        if ($doApp) {
            Get-ChildItem -Path $targetDir -Force |
                Where-Object { $_.Name -ne "XAssistant.Service" } |
                Remove-Item -Recurse -Force
            Copy-Item -Path (Join-Path $sourceDir "*") -Destination $targetDir -Recurse -Force
            # 安装实现只服务于解压目录里的 install*.cmd，装进目标目录没有用处
            $installedInstaller = Join-Path $targetDir "install-core.ps1"
            if (Test-Path $installedInstaller) {
                Remove-Item $installedInstaller -Force
            }
        }
        if ($doService) {
            New-Item -ItemType Directory -Force -Path $targetServiceDir | Out-Null
            Get-ChildItem -Path $targetServiceDir -Force | Remove-Item -Recurse -Force
            Copy-Item -Path (Join-Path $sourceServiceDir "*") -Destination $targetServiceDir -Recurse -Force
        }
    } else {
        Write-Host "    当前就在安装目录里，跳过复制。"
    }

    Write-Host "[5/5] 收尾..." -ForegroundColor Cyan
    if ($doService) {
        $installServiceScript = Join-Path $targetServiceDir "install-service.ps1"
        if (-not (Test-Path $installServiceScript)) {
            throw "找不到后台服务安装脚本：$installServiceScript"
        }
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installServiceScript
        if ($LASTEXITCODE -ne 0) {
            throw "后台服务安装失败。"
        }
    }
    if ($doApp) {
        if (-not (Test-Path $appExe)) {
            throw "安装后找不到 $appExe"
        }
        explorer.exe "$appExe"
    }

    Write-Host "安装完成。" -ForegroundColor Green
    if ($doApp) { Write-Host "主程序已启动。" -ForegroundColor Green }
    if ($doService) { Write-Host "后台服务已更新。" -ForegroundColor Green }
    Write-Host "数据目录：%APPDATA%\XAssistant 和 C:\ProgramData\XAssistant\UsageTracker" -ForegroundColor Gray
} catch {
    $failed = $true
    Write-Host "安装失败：$($_.Exception.Message)" -ForegroundColor Red
    if ($doApp -and (Test-Path $appExe)) {
        Write-Host "安装目录：$targetDir" -ForegroundColor Yellow
        Write-Host "可以手动双击 XAssistant.exe 启动旧版本。" -ForegroundColor Yellow
    }
}

Read-Host "按回车退出"
if ($failed) {
    exit 1
}
