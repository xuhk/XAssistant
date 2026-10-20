<#
.SYNOPSIS
    构建并打包两种 XAssistant Release 包（带运行时 / 不带运行时）。
.DESCRIPTION
    两种包内容一致（主程序 + 后台服务 + 三个安装脚本 + README），
    唯一区别是带运行时的包自带 .NET 运行时。
.NOTES
    需要 .NET 10 SDK；不会推送 GitHub。
#>

param(
    [string]$Version = "",
    [string]$OutputDir = "release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $match = Select-String -Path (Join-Path $repoRoot "XAssistant.csproj") -Pattern "<Version>(.*?)</Version>" | Select-Object -First 1
    if (-not $match) {
        throw "读不到版本号。"
    }
    $Version = $match.Matches[0].Groups[1].Value.Trim()
}

$mainProject = Join-Path $repoRoot "XAssistant.csproj"
$serviceProject = Join-Path $repoRoot "XAssistant.Service\XAssistant.Service.csproj"
$publishRoot = Join-Path $repoRoot "temp\package-publish"
$outputDirFull = Join-Path $repoRoot $OutputDir
if (-not (Test-Path $outputDirFull)) {
    New-Item -ItemType Directory -Force -Path $outputDirFull | Out-Null
}

function Invoke-Publish {
    param(
        [string]$Project,
        [string]$Output,
        [bool]$SelfContained,
        [bool]$ExcludeLocal
    )
    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
    $publishArgs = @(
        $Project,
        "-r", "win-x64",
        "-c", "Release",
        "-o", $Output,
        "--self-contained", $SelfContained.ToString().ToLower()
    )
    if ($ExcludeLocal) {
        $publishArgs += "-p:IncludeLocalModules=false"
    }
    & dotnet publish @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "发布失败：$Project"
    }
}

function Assert-CleanPackage {
    param(
        [string]$MainPublish,
        [string]$ServicePublish,
        [bool]$ExpectRuntime
    )
    $mainExe = Join-Path $MainPublish "XAssistant.exe"
    $mainAssembly = Join-Path $MainPublish "XAssistant.dll"
    $serviceExe = Join-Path $ServicePublish "XAssistant.Service.exe"
    if (-not (Test-Path $mainExe)) { throw "主程序产物不完整：$mainExe" }
    if (-not (Test-Path $serviceExe)) { throw "服务产物不完整：$serviceExe" }
    if (Test-Path (Join-Path $MainPublish "Npgsql.dll")) {
        throw "主程序里仍有 Npgsql.dll，Local 私有模块没被排除。"
    }
    if ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($mainAssembly)).Contains("QuickNote")) {
        throw "主程序里仍有 QuickNote 私有模块，停止打包。"
    }
    $runtimeFiles = @("coreclr.dll", "hostfxr.dll", "System.Private.CoreLib.dll")
    $found = @()
    foreach ($file in $runtimeFiles) {
        if ((Test-Path (Join-Path $MainPublish $file)) -or (Test-Path (Join-Path $ServicePublish $file))) {
            $found += $file
        }
    }
    if ($ExpectRuntime -and $found.Count -eq 0) {
        throw "这是带运行时的包，但产物里没有运行时文件。"
    }
    if (-not $ExpectRuntime -and $found.Count -gt 0) {
        throw "这是不带运行时的包，但产物里有运行时文件：$($found -join ', ')"
    }
}

function New-ReleasePackage {
    param(
        [string]$Variant,
        [string]$MainPublish,
        [string]$ServicePublish,
        [string]$ZipName,
        [string]$RuntimeLine,
        [bool]$ExpectRuntime
    )
    Assert-CleanPackage -MainPublish $MainPublish -ServicePublish $ServicePublish -ExpectRuntime $ExpectRuntime

    $stage = Join-Path $publishRoot "stage-$Variant"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $stageApp = Join-Path $stage "app"
    New-Item -ItemType Directory -Force -Path $stageApp | Out-Null
    Copy-Item -Path (Join-Path $MainPublish "*") -Destination $stageApp -Recurse -Force

    $stageService = Join-Path $stage "XAssistant.Service"
    New-Item -ItemType Directory -Force -Path $stageService | Out-Null
    Copy-Item -Path (Join-Path $ServicePublish "*") -Destination $stageService -Recurse -Force
    Copy-Item -Path (Join-Path $repoRoot "XAssistant.Service\install-service.ps1") -Destination $stageService -Force

    Copy-Item -Path (Join-Path $repoRoot "install.ps1") -Destination (Join-Path $stageApp "install-core.ps1") -Force
    foreach ($scriptName in "install.cmd", "install-app.cmd", "install-service.cmd") {
        Copy-Item -Path (Join-Path $repoRoot $scriptName) -Destination $stage -Force
    }
    Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $stage -Force

    $packageReadme = @"
XAssistant 安装 / 升级说明

先双击 install.cmd（安装主程序 + 后台服务）。
不要直接双击 app\XAssistant.exe：那样不会安装后台服务，也不会走升级流程。

运行环境
- Windows 10 / 11 64 位
$RuntimeLine

包内布局
- app\                  主程序本体和安装实现，不用打开
- XAssistant.Service\   后台服务，不用打开
- install.cmd           安装/升级主程序和后台服务（推荐）
- install-app.cmd       只安装/升级主程序，不动后台服务
- install-service.cmd   只安装/更新后台服务，不动主程序
- README.txt            本说明

首次安装会让你选择安装目录（直接回车使用 C:\XAssistant）；升级会自动找到原目录。
不需要先手动退出程序，脚本会自己结束进程并备份旧版本（最多保留 3 份）。
数据保存在 %APPDATA%\XAssistant 和 C:\ProgramData\XAssistant\UsageTracker，升级不会删除。
如果 Windows 提示"未知发布者"，是未签名程序，选择"仍要运行"即可。
"@

    $changelog = Get-Content -Path (Join-Path $repoRoot "CHANGELOG.md") -Raw -Encoding UTF8
    $versionPattern = "(?ms)^##\s+$([regex]::Escape($Version))\b.*?(?=^##\s+|\z)"
    $versionMatch = [regex]::Match($changelog, $versionPattern)
    if (-not $versionMatch.Success) {
        throw "CHANGELOG.md 里找不到版本 $Version 的更新记录。"
    }
    $versionNotes = $versionMatch.Value.Trim()
    $versionNotes = $versionNotes -replace '^##\s+[^\r\n]+', "## 本次更新（$Version）"
    $packageReadme = $packageReadme.TrimEnd() + "`r`n`r`n" + $versionNotes + "`r`n"

    [IO.File]::WriteAllText((Join-Path $stage "README.txt"), $packageReadme, (New-Object Text.UTF8Encoding($true)))

    $zip = Join-Path $outputDirFull $ZipName
    $tempZip = $zip + ".tmp"
    if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $tempZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    try {
        if (Test-Path $zip) { Remove-Item $zip -Force -ErrorAction Stop }
        Move-Item -Path $tempZip -Destination $zip -Force -ErrorAction Stop
        $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
        Write-Host "打包完成: $zip ($size MB)" -ForegroundColor Green
    } catch {
        $fallback = $zip -replace '\.zip$', '-new.zip'
        if (Test-Path $fallback) { Remove-Item $fallback -Force }
        Move-Item -Path $tempZip -Destination $fallback -Force
        $size = [math]::Round((Get-Item $fallback).Length / 1MB, 1)
        Write-Warning "目标 zip 被占用，已写到 $fallback ($size MB)；关闭占用后改名即可。"
    }
    Remove-Item $stage -Recurse -Force
}

if (Test-Path $publishRoot) { Remove-Item $publishRoot -Recurse -Force }

$frameworkMain = Join-Path $publishRoot "framework\main"
$frameworkService = Join-Path $publishRoot "framework\service"
$selfMain = Join-Path $publishRoot "selfcontained\main"
$selfService = Join-Path $publishRoot "selfcontained\service"

Write-Host "发布不带运行时的版本..." -ForegroundColor Cyan
Invoke-Publish -Project $mainProject -Output $frameworkMain -SelfContained $false -ExcludeLocal $true
Invoke-Publish -Project $serviceProject -Output $frameworkService -SelfContained $false -ExcludeLocal $false

Write-Host "发布带运行时的版本..." -ForegroundColor Cyan
Invoke-Publish -Project $mainProject -Output $selfMain -SelfContained $true -ExcludeLocal $true
Invoke-Publish -Project $serviceProject -Output $selfService -SelfContained $true -ExcludeLocal $false

New-ReleasePackage -Variant "with-runtime" -MainPublish $selfMain -ServicePublish $selfService -ZipName "XAssistant-win-x64-v$Version.zip" -RuntimeLine "- 本包已自带 .NET 运行时，无需另外安装" -ExpectRuntime $true
New-ReleasePackage -Variant "no-runtime" -MainPublish $frameworkMain -ServicePublish $frameworkService -ZipName "XAssistant-win-x64-noruntime-v$Version.zip" -RuntimeLine "- 需要 .NET 10 Desktop Runtime（本包不包含运行时）`n  下载：https://dotnet.microsoft.com/download/dotnet/10.0" -ExpectRuntime $false

Remove-Item $publishRoot -Recurse -Force
Write-Host "版本 $Version 的两个包都已生成。" -ForegroundColor Green
