using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using XAssistant.Services;
using XAssistant.Services.Interfaces;
using XAssistant.ViewModels;
using XAssistant.Views;

namespace XAssistant;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// 自启用的应用名 —— 同时是注册表 Run 值名与计划任务名。
    /// Debug 构建的数据目录是 XAssistant_Dev，自启也走这个名字，开发版与正式版互不覆盖。
    /// </summary>
#if DEBUG
    private const string StartupAppName = "XAssistant_Dev";
#else
    private const string StartupAppName = "XAssistant";
#endif

    public static IServiceProvider Services { get; private set; } = null!;
    private NotifyIcon? _notifyIcon;
    internal static bool IsShuttingDown { get; private set; }

    /// <summary>--register-startup / --unregister-startup 这条维护路径，不建容器。</summary>
    private bool _isStartupMaintenance;

    /// <summary>单实例锁（按数据目录区分）。进程活着就持有，结束后由内核释放。</summary>
    private Mutex? _singleInstance;

    /// <summary>
    /// 托盘右键菜单建好后触发（尚未挂到托盘图标上），供本机私有模块往菜单里插条目。
    /// 订阅必须在 InitializeNotifyIcon 之前完成，见 OnStartup 里 LocalModules.AttachAll 的调用位置。
    /// </summary>
    public static event Action<ContextMenuStrip>? TrayMenuCreated;

    private ILogger<App>? _appLogger;
    private bool _isDataSaved;

    public App()
    {
        // 托盘应用：只有托盘「退出」才结束进程。静默启动时没有可见窗口，
        // 依赖 OnLastWindowClose 的语义会很含糊，显式声明更稳。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 全局 UI 线程异常
        DispatcherUnhandledException += (_, e) =>
        {
            _appLogger?.LogCritical(e.Exception, "未处理的 UI 线程异常");
            e.Handled = true; // 防止进程崩溃，但记录日志
        };

        // 应用程序域未处理异常（通常导致进程退出）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _appLogger?.LogCritical(e.ExceptionObject as Exception, "未处理的应用程序域异常");
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自启注册的维护入口（部署脚本 / 迁移用）：改完注册状态立刻退出，
        // 不建 DI、不装钩子、不开窗。必须排在单实例锁之前，否则托盘里有实例时迁移做不了。
        if (TryHandleStartupMaintenance(e.Args) is int maintenanceExitCode)
        {
            Shutdown(maintenanceExitCode);
            return;
        }

        // 单实例保护，放在一切装配之前：两个实例会把同一次按键各记一遍。
        bool silentLaunch = e.Args.Any(a =>
            string.Equals(a, StartupService.SilentArgument, StringComparison.OrdinalIgnoreCase)
        );
        if (!TryAcquireSingleInstance(silentLaunch))
        {
            Shutdown();
            return;
        }

        // 准备日志目录
        string logDir = AppDataPathHelper.GetAppDataFolder();
        string logPath = System.IO.Path.Combine(logDir, "logs", "xassistant-.log");

        var services = new ServiceCollection();

        // 创建 LogBufferService 实例并提前注册
        // 这样 Serilog 配置和 DI 都使用同一个实例，且无需提前 Build 容器
        var logBuffer = new LogBufferService();
        services.AddSingleton<ILogBufferService>(logBuffer);

        // 注册其他应用服务
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<IMouseClickHookService, MouseClickHookService>();
        services.AddSingleton<IClickDatabaseService, ClickDatabaseService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton(_ => new StartupService(StartupAppName));
        services.AddSingleton<IStartupService>(sp => sp.GetRequiredService<StartupService>());
        services.AddSingleton<IKeyboardHookService, KeyboardHookService>();
        services.AddSingleton<IKeyDatabaseService, KeyDatabaseService>();
        services.AddSingleton<ClickCounterViewModel>();
        services.AddSingleton<KeyCounterViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<UsageViewModel>();
        services.AddSingleton<AppUsageViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<LogViewerViewModel>();

        services.AddSingleton<ProcessUsageTracker>();

        // 检查更新：只在用户点按钮时查一次，两个端点并发，失败一律返回状态、不抛
        services.AddSingleton<UpdateService>();

        // Toast：各页面的轻量确认提示
        services.AddSingleton<ToastService>();

        // 本机私有模块（Local/ 目录）；开源版没有实现，这里什么都不会发生
        LocalModules.RegisterAll(services);

        // 配置 Serilog Logger
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
            )
            .WriteTo.Sink(new UiLogSink(logBuffer))
            .CreateLogger();

        // 添加日志服务到 DI
        services.AddLogging(builder => builder.AddSerilog());

        // 构建容器
        var provider = services.BuildServiceProvider();
        Services = provider;

        // 启动进程追踪
        var processTracker = provider.GetRequiredService<ProcessUsageTracker>();
        processTracker.Start();

        // 获取系统日志记录器
        _appLogger = provider.GetRequiredService<ILogger<App>>();
        _appLogger.LogInformation("═══════ XAssistant 启动成功 ═══════");
        _appLogger.LogInformation(
            "版本 {Version}，数据目录 {Dir}",
            AppVersion.Display,
            AppDataPathHelper.GetAppDataFolder()
        );

        // 老版本只挂 Run 键（要等 explorer 处理，登录后 30~55 秒才起来）。
        // 这一版起改成「登录触发的计划任务 + Run 键兜底」，这里给已经开着自启的用户自动升级一次。
        var startup = provider.GetRequiredService<StartupService>();
        if (startup.IsStartWithWindowsEnabled() && !startup.IsLogonTaskRegistered())
        {
            _appLogger.LogInformation("开机自启还只有 Run 键，正在升级为登录计划任务…");
            startup.SetAutoStart(true);
            _appLogger.LogInformation(
                "开机自启升级完成：计划任务已注册={Registered}（登录后约 8 秒拉起）",
                startup.IsLogonTaskRegistered()
            );
        }

        // 采集不能依赖用户是否访问过统计页：这两个 VM 的构造就是钩子的启动点。
        // 显式解析一次，保证即使主窗没显示 / 从未点开统计页，采集也已经装上。
        var config = provider.GetRequiredService<IConfigurationService>();
        _appLogger.LogInformation(
            "采集配置：键盘自动启动={Keyboard}，鼠标自动启动={Mouse}",
            config.GetKeyRecordingAutoStart(),
            config.GetRecordingAutoStart()
        );
        provider.GetRequiredService<KeyCounterViewModel>();
        provider.GetRequiredService<ClickCounterViewModel>();

        // 后续主窗口
        var mainVM = provider.GetRequiredService<MainWindowViewModel>();

        var mainWindow = new MainWindow { DataContext = mainVM };
        MainWindow = mainWindow;

        // 开机自启（--silent）只驻留托盘、不显示主窗；常驻期间也就不为界面花代价。
        if (silentLaunch)
        {
            _appLogger.LogInformation("以静默方式启动（不显示主窗）；从托盘「显示」即可打开");
        }
        else
        {
            mainWindow.Show();
        }

        // 必须在 InitializeNotifyIcon 之前：私有模块要在这里订阅 TrayMenuCreated 才能插进托盘菜单
        LocalModules.AttachAll(provider);

        InitializeNotifyIcon(mainWindow);
    }

    /// <summary>
    /// 处理自启注册的维护参数：--register-startup / --unregister-startup。
    /// 命中返回退出码（0 成功 / 1 失败），没命中返回 null 表示照常启动。
    /// </summary>
    private int? TryHandleStartupMaintenance(string[] args)
    {
        bool register = args.Any(a =>
            string.Equals(
                a,
                StartupService.RegisterStartupArgument,
                StringComparison.OrdinalIgnoreCase
            )
        );
        bool unregister = args.Any(a =>
            string.Equals(
                a,
                StartupService.UnregisterStartupArgument,
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (!register && !unregister)
            return null;

        _isStartupMaintenance = true;

        try
        {
            var startup = new StartupService(StartupAppName);
            startup.SetAutoStart(register);

            if (register)
            {
                // 严格判据：计划任务真的注册上才算成功；只写成 Run 键不算「提前」
                return startup.IsLogonTaskRegistered() ? 0 : 1;
            }

            return startup.IsStartWithWindowsEnabled() ? 1 : 0;
        }
        catch (Exception ex)
        {
            AppendInstanceNote(
                $"自启维护（{(register ? "注册" : "取消")}）失败：{ex.GetType().Name}：{ex.Message}"
            );
            return 1;
        }
    }

    /// <summary>
    /// 同一份数据目录只允许一个进程：键盘 / 鼠标是全局钩子，开两个实例会把同一次按键各写一条。
    /// 名字按数据目录区分，所以开发版与正式版仍可并存。
    /// </summary>
    private bool TryAcquireSingleInstance(bool silent)
    {
        string dataDir = AppDataPathHelper.GetAppDataFolder();
        string leaf = Path.GetFileName(dataDir.TrimEnd(Path.DirectorySeparatorChar));
        try
        {
            _singleInstance = new Mutex(
                initiallyOwned: true,
                $"Local\\XAssistant.SingleInstance.{leaf}",
                out bool createdNew
            );
            if (createdNew)
                return true;
        }
        catch (Exception ex)
        {
            // 保护机制本身不能把应用挡在门外
            AppendInstanceNote(
                $"单实例锁创建失败（{ex.GetType().Name}）：{ex.Message} —— 本次按允许启动处理"
            );
            return true;
        }

        AppendInstanceNote(
            $"启动被拒：数据目录 {dataDir} 已有实例在运行（多个实例会把同一次按键各记一遍）"
        );
        if (!silent)
        {
            System.Windows.MessageBox.Show(
                $"XAssistant 已经在运行。\n\n数据目录：{dataDir}\n\n"
                    + "键盘和鼠标是全局钩子，多个实例会把同一次按键重复记进同一个库，所以这次启动已取消。\n"
                    + "要换成新版本：先把托盘里那个退掉（右键 → 退出），再启动这一次。",
                "XAssistant 已在运行",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
        return false;
    }

    /// <summary>给日志文件补一行备注。此刻 Serilog 可能还没建，用最朴素的追加方式，失败就算了。</summary>
    private static void AppendInstanceNote(string message)
    {
        try
        {
            string dir = Path.Combine(AppDataPathHelper.GetAppDataFolder(), "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"xassistant-{DateTime.Now:yyyyMMdd}.log");
            using var stream = new FileStream(
                file,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite
            );
            using var writer = new StreamWriter(stream);
            writer.WriteLine(
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WRN] XAssistant.App: {message}"
            );
        }
        catch
        {
            // 留痕失败无所谓
        }
    }

    private void InitializeNotifyIcon(Window mainWindow)
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = GetAppIcon(),
            Visible = true,
            Text = "XAssistant",
        };

        // 左键单击托盘图标 → 显示主窗口
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ShowMainWindow(mainWindow);
            }
        };

        // 右键菜单
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("显示", null, (_, _) => ShowMainWindow(mainWindow));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("退出", null, (_, _) => ShutdownApplication());

        // 本机私有模块在这里插菜单项（开源版无订阅者）
        try
        {
            TrayMenuCreated?.Invoke(contextMenu);
        }
        catch (Exception ex)
        {
            _appLogger?.LogWarning(ex, "托盘菜单扩展失败，已忽略");
        }

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private static void ShowMainWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private void ShutdownApplication()
    {
        if (_isDataSaved)
            return;
        IsShuttingDown = true;
        _notifyIcon!.Visible = false;
        _notifyIcon.Dispose();

        SaveDataAndStopTracker(); // 复用统一逻辑
        Shutdown();
    }

    private static Icon GetAppIcon()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (icon != null)
                return icon;
        }
        catch
        {
            // 忽略错误，使用默认图标
        }
        return SystemIcons.Application;
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        SaveDataAndStopTracker();
    }

    private void SaveDataAndStopTracker()
    {
        if (_isDataSaved)
            return;

        // 维护入口没建容器，Services 还是 null —— 这条路上也没有采集要停
        if (_isStartupMaintenance)
            return;

        _isDataSaved = true;

        _appLogger?.LogInformation("系统正在关闭/注销，保存进程使用数据...");
        try
        {
            var tracker = Services.GetRequiredService<ProcessUsageTracker>();
            tracker.Stop();
            tracker.Dispose();
        }
        catch (Exception ex)
        {
            _appLogger?.LogError(ex, "停止进程追踪器失败");
        }

        Log.CloseAndFlush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 维护入口走的是建 DI 之前的路径：没有采集、托盘、日志器，直接退
        if (_isStartupMaintenance)
        {
            base.OnExit(e);
            return;
        }

        // 防止重复保存（如果已经通过 SessionEnding 或 ShutdownApplication 保存过）
        SaveDataAndStopTracker();

        _notifyIcon?.Dispose();
        // 移除事件订阅，避免内存泄漏
        SystemEvents.SessionEnding -= OnSessionEnding;
        base.OnExit(e);
    }
}
