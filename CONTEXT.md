# XAssistant Context

Windows 活动统计应用（WPF / .NET 10，常驻托盘）的领域词汇。下面几个词在代码里容易被叫混，这里定死。

## Language

**使用时长（Usage Duration）**:
电脑整体的开机 / 活跃时长。**不由本仓库采集**，来自独立的 "UsageTracker" 服务，落 `C:\ProgramData\XAssistant\UsageTracker\pc_usage.db`；本仓库只通过命名管道读取展示，取不到时回退数据库、再回退"无法获取"。
_Avoid_: 电脑使用、开机时间

**应用使用时长（App Usage）**:
单个进程被持续使用的一段连续时间，由本仓库的 `ProcessUsageTracker` 跟踪，落 `app_usage.db`。
_Avoid_: 软件使用、程序时长

**会话（Session）**:
`ProcessSession` 表里的一行，即"某应用从开始被用到结束"的一段。按**规范化进程名**分组，所以同一应用的所有窗口 / 实例共用一个会话；跨午夜会被拆成两行（前一天在 23:59:59.999 结束，新行从 00:00 开始）。
_Avoid_: 记录、条目

**采集源（Recording Source）**:
键盘与鼠标两个低级钩子（`KeyboardHookService` / `MouseClickHookService`）。注意开关字段的历史命名：**鼠标那个叫 `AutoStartRecording`，键盘那个叫 `AutoStartKeyRecording`** —— 前者并不代表"录制全部"。
_Avoid_: 录制、监控、采集器
