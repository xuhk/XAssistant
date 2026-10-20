<#
  看 / 切换 XAssistant 的开机自启注册点。

  为什么要有它：自启现在挂**两条路** ——
    · 计划任务「登录时触发 + 延时 8 秒」：主路，负责提前
    · 注册表 HKCU\...\Run：兜底（计划任务没注册上时仍然能自启，只是慢）

  2026-09-23 实测（拿 System 事件日志 7001 的登录时刻去对日志首条启动时间）：
  只有 Run 键那一档时是登录后 29~55 秒才起来，中位 42.5 秒 —— 因为 explorer 要先把 shell
  拉起来、再排完 Run 里前面的 5 个启动项，它才轮到。计划任务这一档理论值 8~10 秒。

  用法（默认只看不动手）：
    powershell -ExecutionPolicy Bypass -File D:\code\XAssistant\tools\xassistant-autostart.ps1
    # 把正式版升级成计划任务 + 顺手修正 Run 值（补引号、补 --silent）
    powershell -ExecutionPolicy Bypass -File D:\code\XAssistant\tools\xassistant-autostart.ps1 -Apply
    # 回滚成纯 Run 键
    powershell -ExecutionPolicy Bypass -File D:\code\XAssistant\tools\xassistant-autostart.ps1 -Rollback

  前置：-Apply / -Rollback 要求目标 exe 是**带这个功能的新版本**
  （它靠 --register-startup / --unregister-startup 干活；旧版本会把这个参数当普通启动参数、直接开窗）。
#>
param(
  [switch]$Apply,
  [switch]$Rollback,
  [string]$Exe = 'C:\XAssistant\XAssistant.exe',
  [string]$TaskName = 'XAssistant'
)

$ErrorActionPreference = 'Stop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if ($Apply -and $Rollback) { throw '-Apply 和 -Rollback 只能给一个' }

function Get-RunValue($name) {
  $v = (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue).$name
  if ($v) { $v } else { '(无)' }
}

function Show-Registration {
  Write-Host ''
  Write-Host '── 目标 exe ──' -ForegroundColor Cyan
  if (Test-Path $Exe) {
    $f = Get-Item $Exe
    Write-Host ("  {0}" -f $Exe)
    Write-Host ("  （{0:yyyy-MM-dd HH:mm} 编译，{1:N0} KB）" -f $f.LastWriteTime, ($f.Length / 1KB)) -ForegroundColor DarkGray
  }
  else {
    Write-Host ("  {0} —— 不存在" -f $Exe) -ForegroundColor Red
  }

  Write-Host '── 注册点 ──' -ForegroundColor Cyan
  $rv = Get-RunValue $TaskName
  Write-Host ("  注册表 Run 值 : {0}" -f $rv)
  if ($rv -ne '(无)' -and $rv -notmatch '^".*"\s') {
    Write-Host '                  ↑ 没有引号 / 没有 --silent，是旧版本或手工留下的畸形值，-Apply 会修正' -ForegroundColor Yellow
  }

  $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
  if ($task) {
    $delay = ($task.Triggers | Where-Object { $_.Delay } | Select-Object -First 1).Delay
    Write-Host ("  计划任务     : 存在（{0}；登录触发，延时 {1}）" -f $task.State, $delay) -ForegroundColor Green
  }
  else {
    Write-Host '  计划任务     : 不存在' -ForegroundColor Yellow
    if ($rv -ne '(无)') {
      Write-Host '                  ↑ 自启开着但只有 Run 键那一条路 —— 跑一次 -Apply 就能提前约 35 秒' -ForegroundColor Yellow
    }
  }
}

function Show-Timing {
  Write-Host '── 最近几次「登录 → 应用启动」的间隔（正式版日志）──' -ForegroundColor Cyan
  $logDir = Join-Path $env:APPDATA 'XAssistant\logs'
  if (-not (Test-Path $logDir)) {
    Write-Host ("  找不到日志目录：{0}" -f $logDir) -ForegroundColor DarkGray
    return
  }

  $logons = @{}
  Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 7001 } -MaxEvents 80 -ErrorAction SilentlyContinue |
    ForEach-Object {
      $d = $_.TimeCreated.ToString('yyyy-MM-dd')
      if (-not $logons.ContainsKey($d) -or $_.TimeCreated -lt $logons[$d]) { $logons[$d] = $_.TimeCreated }
    }
  if ($logons.Count -eq 0) { Write-Host '  读不到登录事件（需要普通用户权限即可，可能是日志已被覆盖）' -ForegroundColor DarkGray; return }

  $rows = @()
  foreach ($d in ($logons.Keys | Sort-Object -Descending)) {
    $file = Join-Path $logDir ('xassistant-{0}.log' -f ($d -replace '-', ''))
    if (-not (Test-Path $file)) { continue }

    $earliest = $null
    foreach ($line in (Get-Content $file -TotalCount 5 -ErrorAction SilentlyContinue)) {
      if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
        $ts = [datetime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
        if ($null -eq $earliest -or $ts -lt $earliest) { $earliest = $ts }
      }
    }
    if ($null -eq $earliest) { continue }

    $sec = [math]::Round(($earliest - $logons[$d]).TotalSeconds, 1)
    if ($sec -ge 0 -and $sec -lt 900) {
      $rows += [pscustomobject]@{
        日期   = $d
        登录   = $logons[$d].ToString('HH:mm:ss')
        应用启动 = $earliest.ToString('HH:mm:ss')
        延迟秒 = $sec
      }
    }
  }

  if ($rows.Count -eq 0) { Write-Host '  没有可配对的记录' -ForegroundColor DarkGray; return }
  $rows | Select-Object -First 10 | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
  $avg = ($rows | Measure-Object 延迟秒 -Average).Average
  Write-Host ("  最近 {0} 次平均 {1} 秒" -f $rows.Count, [math]::Round($avg, 1)) -ForegroundColor Yellow
  Write-Host '  （改成计划任务后重启一次，这里应该掉到 10 秒上下）' -ForegroundColor DarkGray
}

Show-Registration

if ($Apply -or $Rollback) {
  if (-not (Test-Path $Exe)) { throw "找不到 $Exe —— 先把新版本部署过去" }

  $argument = if ($Apply) { '--register-startup' } else { '--unregister-startup' }
  Write-Host ''
  Write-Host ("── 执行 {0} {1} ──" -f $Exe, $argument) -ForegroundColor Cyan

  if ($Apply -and (Get-RunValue $TaskName) -eq '(无)' -and -not (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
    Write-Host '  注意：本来没开自启，-Apply 会**顺便把它打开**' -ForegroundColor Yellow
  }

  $proc = Start-Process -FilePath $Exe -ArgumentList $argument -Wait -PassThru -WindowStyle Hidden
  if ($proc.ExitCode -eq 0) {
    Write-Host '  成功（退出码 0）' -ForegroundColor Green
  }
  else {
    Write-Host ("  失败（退出码 {0}）：计划任务没注册上。" -f $proc.ExitCode) -ForegroundColor Red
    Write-Host '  自启仍然可用（走 Run 键兜底），但不会提前。查原因看应用日志里的 WRN 行。' -ForegroundColor Yellow
  }

  Show-Registration
}

if (-not $Apply -and -not $Rollback) { Show-Timing }
