# CLAUDE.md

本文件为 Claude Code（claude.ai/code）在本仓库中编写代码时提供指引。

## 项目概述

XAssistant 是一款 Windows 桌面活动监控应用（WPF、C#、.NET 10），负责记录：

- **鼠标点击**（左/中/右键），通过低级 Win32 钩子实现
- **键盘按键**，通过低级 Win32 钩子实现
- **各应用使用时长**（哪些进程被使用了多久），通过 WMI 进程事件实现
- **电脑整体使用时长**（开机/睡眠/唤醒）——这部分数据来自**本仓库之外的独立外部 "UsageTracker" 服务**

UI 采用基于 CommunityToolkit.Mvvm 的 WPF MVVM 架构。项目没有自动化测试。

## 常用命令

- 构建：`dotnet build`（需要 .NET 10 SDK）
- 运行（调试）：`dotnet run`
- 发布（Release，win-x64）：`dotnet publish -r win-x64 -c Release` —— 输出位于 `bin\Release\net10.0-windows\win-x64\publish`（`build.bat` 封装了这条命令）
- 部署到 `C:\XAssistant` 并重新启动：`powershell -ExecutionPolicy Bypass -File deploy.ps1` —— 必须以管理员身份运行；该脚本会停止正在运行的实例、备份旧版本、复制新构建并启动应用
- 格式化代码：`dotnet tool restore && dotnet csharpier .`（CSharpier 1.2.6 是 `dotnet-tools.json` 中固定的格式化工具）

## 数据存储

所有应用数据存放在 `%APPDATA%\XAssistant`（Release）或 `%APPDATA%\XAssistant_Dev`（Debug）。该区分由 `AppDataPathHelper` 中的 `#if DEBUG` 控制，**同时**影响数据文件夹和开机自启注册表键（开发版使用 `XAssistant_Dev`），因此 Debug 构建永远不会触碰生产数据。在该文件夹内：

- `click_data.db` —— 表 `ClickRecords(Id, Button, ClickTime)`
- `key_data.db` —— 表 `KeyPressRecords(Id, Key, PressTime)`
- `app_usage.db` —— 表 `ProcessSession(Id, ProcessName, StartTime, EndTime, WindowTitle, Date, AccumulatedSeconds, LastUpdateTime)`；WAL 模式；由 `ProcessUsageTracker` 写入
- `appsettings.json` —— `AppSettings`（录制自动启动开关、窗口大小、日志面板状态），由 `ConfigurationService` 负责
- `logs/xassistant-<日期>.log` —— Serilog 滚动文件日志（保留 31 天）

时间戳以字符串形式存储：点击/键盘数据库使用 ISO 8601（`"o"`）格式，`ProcessSession` 使用 `yyyy-MM-dd HH:mm:ss.fff`（另有独立的 `Date` 列，格式为 `yyyy-MM-dd`，用于按天分桶）。

## 外部依赖（重要）

“电脑使用统计”页面（`UsageViewModel`）**不读取**本仓库的数据。它通过命名管道 `UsageTrackerPipe` 和位于 `C:\ProgramData\XAssistant\UsageTracker\pc_usage.db` 的 SQLite 数据库来消费独立的 “UsageTracker” 后台服务。如果该服务未运行，它会回退到数据库，最后显示“无法获取”。管道服务器和 `pc_usage.db` 的写入方都不在本仓库中。

## 架构

### 启动引导与导航
- 所有装配都在 `App.xaml.cs` 的 `OnStartup` 中手动完成，使用 `Microsoft.Extensions.DependencyInjection`（不使用 generic host）。`LogBufferService` 在启动时先创建一个实例，同时供 Serilog 的 `UiLogSink` 和 DI 使用，这样在容器构建完成之前应用内日志面板就能工作。
- `ProcessUsageTracker` 在启动时立即启动，在关机、会话结束或托盘菜单退出（`App.IsShuttingDown`）时停止并保存。
- View→ViewModel 的映射通过 `App.xaml` 中的 `DataTemplate`（每个 VM 一个）完成。`MainWindow` 显示一个绑定到 `MainWindowViewModel.CurrentViewModel` 的 `ContentControl`；左侧菜单通过 `Navigate` 中继命令切换页面。`MainWindowViewModel` 还负责底部日志面板、日志过滤和窗口大小持久化。
- `HomeViewModel` 聚合四个模块的 ViewModel，并将其 `PropertyChanged` 事件转发到自身同名的属性。所有模块 ViewModel 都是单例，因此无论当前显示哪个页面，实时数据（钩子、定时器）都会持续流动。

### 四个模块
每个模块 = 钩子服务 + SQLite 仓库 + ViewModel + View：
1. **鼠标点击** —— `MouseClickHookService`（WH_MOUSE_LL）→ `ClickDatabaseService` → `ClickCounterViewModel`
2. **键盘** —— `KeyboardHookService`（WH_KEYBOARD_LL，通过 `_pressedKeys` 集合对按键自动重复去重）→ `KeyDatabaseService` → `KeyCounterViewModel`
3. **各应用使用时长** —— `ProcessUsageTracker` → `app_usage.db` → `AppUsageViewModel`（2 秒 `DispatcherTimer` 轮询）
4. **电脑使用时长** —— 外部服务（见上文）→ `UsageViewModel`（5 秒 `DispatcherTimer` 轮询）

实时 UI 更新全部通过 `DispatcherTimer` 轮询或事件驱动计数器实现——没有发布/订阅总线。

### 本机私有模块（Local/）
`Services/LocalModules.cs` 是通用挂载点：反射扫当前程序集里实现 `ILocalModule` 的类型，容器构建前调 `Register(services)`、启动后调 `Attach(provider)`（实现类需有公开无参构造；单个模块失败只记警告）。**开源版没有任何实现**，因此整段是空转的；私有代码放在被 gitignore 的 `Local/`（指向仓库外私有仓库的目录联结），编译进同一程序集即被自动发现。

共用代码只有三处挂钩，改启动流程或托盘菜单时别删：`App.xaml.cs` 的 `LocalModules.RegisterAll` 与 `AttachAll`（**`AttachAll` 必须在 `InitializeNotifyIcon` 之前**，私有模块要在这里订上事件），以及静态事件 `App.TrayMenuCreated`（托盘菜单建好、尚未挂到图标上时触发）。

### ProcessUsageTracker（复杂部分）
最大的服务（约 950 行），也是 bug 最多的地方。关键设计要点：

- 按**规范化后的进程名**（小写、去除 `.exe` 后缀）分组跟踪应用会话，因此一个应用的所有窗口/实例共享一个会话；`ProcessCount` 记录该会话中有多少个进程。
- 通过两种方式发现进程：初始的 `Process.GetProcesses()` 枚举，外加 WMI `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` 监视器。
- 过滤系统进程：Session 0、位于 Windows 目录下的可执行文件、硬编码的 `SystemProcessNames` 集合，以及应用自身（按名称——`SelfProcessName`）。
- 一个 5 秒 `Timer` 累计每个会话的经过时长（写入 `AccumulatedSeconds`/`LastUpdateTime`）并刷新窗口标题。
- 时长低于 `MinimumSessionSeconds`（1 秒）的会话会被删除，而不是存储。
- `RecoverUnfinishedSessions` 修复崩溃或重启后遗留的 `EndTime IS NULL` 会话，包括跨天处理——跨午夜仍在运行的会话会被拆成两段：前一天在 23:59:59.999 结束，新会话从 00:00 开始。
- 窗口尚未出现的进程会暂存在 `_pendingProcesses` 中，一旦 `MainWindowHandle` 变为非零就会晋升为正式会话。
- 所有状态都保存在以 PID/应用名为主键的 `ConcurrentDictionary` 中——这种线程安全设计是刻意的。

### 日志
Serilog 同时写入滚动文件日志和 `UiLogSink`，后者把 `LogEntry` 推入 `LogBufferService` 的 `ObservableCollection`（通过 `BindingOperations.EnableCollectionSynchronization` 保证线程安全）。`MainWindowViewModel` 将过滤后的日志暴露给应用内面板。`ILogger<T>` 注入到所有地方。

## 约定

- 代码注释一律用**中文**编写——新注释请保持一致。
- 录制自动启动开关（鼠标/键盘）通过 `ConfigurationService` 在重启后保持。
- 应用最小化到系统托盘（Windows Forms `NotifyIcon`；项目在 WPF 之外启用了 `UseWindowsForms`）。关闭窗口是隐藏而不是退出——只有托盘“退出”菜单才会真正退出。
- `temp/` 目录是已加入 gitignore 的临时空间，csproj 里另有显式的 `temp\**` 通配排除——**gitignore 不影响 MSBuild 通配**，往里放带自己 csproj 的小项目否则会被主项目收进来、直接编译失败。
