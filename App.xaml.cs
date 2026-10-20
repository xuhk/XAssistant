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
    public static IServiceProvider Services { get; private set; } = null!;
    private NotifyIcon? _notifyIcon;
    internal static bool IsShuttingDown { get; private set; }

    /// <summary>
    /// 托盘右键菜单建好后触发（尚未挂到托盘图标上），供本机私有模块往菜单里插条目。
    /// 订阅必须在 InitializeNotifyIcon 之前完成，见 OnStartup 里 LocalModules.AttachAll 的调用位置。
    /// </summary>
    public static event Action<ContextMenuStrip>? TrayMenuCreated;

    private ILogger<App>? _appLogger;
    private bool _isDataSaved;

    public App()
    {
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
#if DEBUG
        services.AddSingleton<IStartupService>(_ => new StartupService("XAssistant_Dev"));
#else
        services.AddSingleton<IStartupService>(_ => new StartupService("XAssistant"));
#endif
        services.AddSingleton<IKeyboardHookService, KeyboardHookService>();
        services.AddSingleton<IKeyDatabaseService, KeyDatabaseService>();
        services.AddSingleton<ClickCounterViewModel>();
        services.AddSingleton<KeyCounterViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<UsageViewModel>();
        services.AddSingleton<AppUsageViewModel>();
        services.AddSingleton<LogViewerViewModel>();

        services.AddSingleton<ProcessUsageTracker>();

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
        mainWindow.Show();

        // 必须在 InitializeNotifyIcon 之前：私有模块要在这里订阅 TrayMenuCreated 才能插进托盘菜单
        LocalModules.AttachAll(provider);

        InitializeNotifyIcon(mainWindow);
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
        // 防止重复保存（如果已经通过 SessionEnding 或 ShutdownApplication 保存过）
        SaveDataAndStopTracker();

        _notifyIcon?.Dispose();
        // 移除事件订阅，避免内存泄漏
        SystemEvents.SessionEnding -= OnSessionEnding;
        base.OnExit(e);
    }
}
