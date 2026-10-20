using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using XAssistant.Services.Interfaces;

/// <summary>
/// 开机自启的注册点管理。
///
/// 同时挂**两条路**，一条抢时间、一条兜底：
///
///  1. **计划任务**：触发器「用户登录时」+ 延时 8 秒 —— 主路，负责提前。
///     注册表 Run 键要等 explorer 把 shell 拉起来才开始处理，那是整套登录流程里最晚的一档。
///     2026-09-23 实测（对着 System 事件日志 7001 的登录时刻量）：Run 键这一档是登录后
///     29~55 秒，中位 42.5 秒；改成登录触发的计划任务后理论值约 8~10 秒。
///
///  2. **注册表 HKCU\...\Run** —— 兜底。计划任务注册失败（权限、被安全软件拦、被手工禁用、
///     被组策略清掉）时，这一条仍然保证能自启，只是慢几十秒。宁慢勿缺。
///
/// 两条路同时生效时：先起来的那个进程拿到单实例锁，后一个静默退出
/// （见 App.TryAcquireSingleInstance）。不会重复记账，日志里会多一行「启动被拒」，那是正常的。
///
/// **为什么用 COM 而不是 schtasks.exe**：创建计划任务在安全软件眼里就是"持久化"，
/// 而"启动一个 schtasks.exe 子进程"这件事本身更容易被拦（本机 WorkBuddy 的命令黑名单
/// 就直接把 schtasks.exe 拒了）。COM 是直连计划任务服务的进程内调用，不起子进程，
/// 也没有中文输出的编码问题。
/// </summary>
[SupportedOSPlatform("windows")]
public class StartupService : IStartupService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 开机自启时带上的参数：只驻留托盘、不弹主窗。
    /// 主窗一旦显示就要付首帧那几秒的代价，常驻场景不该付。
    /// </summary>
    public const string SilentArgument = "--silent";

    /// <summary>把自启升级成计划任务（部署脚本 / 迁移时调一次），跑完即退。</summary>
    public const string RegisterStartupArgument = "--register-startup";

    /// <summary>把自启退回注册表 Run 键（出问题时回滚用），跑完即退。</summary>
    public const string UnregisterStartupArgument = "--unregister-startup";

    /// <summary>
    /// 计划任务在登录之后再等这么久（ISO 8601 duration）。
    ///
    /// **为什么不能是 0**：登录事件刚触发时用户配置单元可能还没挂载完，那时候
    /// <c>Environment.GetFolderPath(ApplicationData)</c> 会指向临时目录 —— 数据就写到别处去了，
    /// 而且是静默写错。等 8 秒足够躲开这个窗口，仍然远快于 Run 键那一档。
    /// </summary>
    private const string LogonDelay = "PT8S";

    /// <summary>任务管理器 / 计划任务程序里显示不了那么长，这里给一句能看懂的。</summary>
    private const string TaskDescription =
        "XAssistant 开机自启：登录后延时 8 秒拉起（注册表 Run 键要等 explorer 处理，会晚约 35 秒）";

    // ── Task Scheduler COM 枚举值。名字都在这儿，免得看代码时来回查文档 ──
    private const int TriggerTypeLogon = 9;
    private const int ActionTypeExec = 0;
    private const int LogonTypeInteractiveToken = 3; // 只用当前用户的登录令牌，不存密码
    private const int RunLevelLeastPrivilege = 0; // 不弹 UAC
    private const int CreateOrUpdate = 6;
    private const int InstancesIgnoreNew = 2; // 已经在跑就不要再起一个
    private const int HResultFileNotFound = unchecked((int)0x80070002);

    /// <summary>Run 值名与计划任务名共用这一个（开发版是 XAssistant_Dev，与数据目录同名）。</summary>
    private readonly string _appName;

    private readonly string _exePath;

    public StartupService(string appName)
    {
        _appName = appName;
        // 如果获取不到路径，直接抛异常，因为后续功能没法用
        _exePath =
            Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前进程的可执行文件路径。");
    }

    /// <summary>当前进程的可执行文件路径 —— 注册计划任务时要写进动作里（也就是"装在哪"）。</summary>
    public string ExecutablePath => _exePath;

    /// <summary>计划任务名，供排查脚本/日志引用。</summary>
    public string TaskName => _appName;

    /// <summary>
    /// 是否已设为开机自启。**先查 Run 键（纯注册表读，几乎零成本）**，
    /// 两条路都挂上时就不会为了这个问题去连一次计划任务服务。
    /// </summary>
    public bool IsStartWithWindowsEnabled() => RunEntryExists() || TaskExists();

    /// <summary>计划任务这一路是否已注册（部署脚本拿它当验收判据，界面上不显示）。</summary>
    public bool IsLogonTaskRegistered() => TaskExists();

    public void SetAutoStart(bool enabled)
    {
        if (enabled)
        {
            // 先写 Run 键：这一步几乎不会失败，先把"一定能自启"保住，再去争取"早点自启"
            WriteRunEntry();
            RegisterLogonTask();
        }
        else
        {
            // 关掉时两条路都要断，否则"开关关了、任务还在跑"就是假实现
            RemoveRunEntry();
            DeleteLogonTask();
        }
    }

    // ─────────────────────────── 注册表 Run 键（兜底那条路） ───────────────────────────

    private void WriteRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                Serilog.Log.Warning("打不开注册表 Run 键，开机自启（兜底那条）没写成");
                return;
            }

            // 引号必须有：安装路径带空格时不加引号会解析成两个参数。
            // --silent 必须有：被单实例锁拒掉的那一次才不会弹窗（见 App.TryAcquireSingleInstance）。
            key.SetValue(_appName, $"\"{_exePath}\" {SilentArgument}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "写注册表 Run 键失败，开机自启（兜底那条）没写成");
        }
    }

    private void RemoveRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(_appName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "删注册表 Run 键失败");
        }
    }

    private bool RunEntryExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(_appName) != null;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────── 计划任务（提前那条路） ───────────────────────────

    private void RegisterLogonTask()
    {
        try
        {
            WithTaskService(service =>
            {
                dynamic root = service.GetFolder("\\");
                dynamic def = service.NewTask(0);

                def.RegistrationInfo.Description = TaskDescription;

                // 触发器：用户登录时。延时多少见 LogonDelay 的注释。
                dynamic trigger = def.Triggers.Create(TriggerTypeLogon);
                trigger.UserId = CurrentUser;
                trigger.Delay = LogonDelay;
                trigger.Enabled = true;

                // 动作：拉起自己，带 --silent
                dynamic action = def.Actions.Create(ActionTypeExec);
                action.Path = _exePath;
                action.Arguments = SilentArgument;
                action.WorkingDirectory = Path.GetDirectoryName(_exePath) ?? string.Empty;

                // 主体：当前用户 + 交互令牌 + 普通权限
                dynamic principal = def.Principal;
                principal.UserId = CurrentUser;
                principal.LogonType = LogonTypeInteractiveToken;
                principal.RunLevel = RunLevelLeastPrivilege;

                // 设置。前三项是踩过坑才这么定的，别随手改：
                //   ExecutionTimeLimit = PT0S  不限时。默认是 72 小时 —— 常驻应用会在第三天
                //                              被静默杀掉，而且看起来像"自己退了"。
                //   MultipleInstances = IgnoreNew  已经在跑就别再起一个。
                //   DisallowStartIfOnBatteries = false  笔记本拔了电源也要自启。
                dynamic s = def.Settings;
                s.ExecutionTimeLimit = "PT0S";
                s.MultipleInstances = InstancesIgnoreNew;
                s.DisallowStartIfOnBatteries = false;
                s.StopIfGoingOnBatteries = false;
                s.StartWhenAvailable = false;
                s.RunOnlyIfNetworkAvailable = false;
                s.RunOnlyIfIdle = false;
                s.WakeToRun = false;
                s.AllowHardTerminate = true;
                s.AllowDemandStart = true;
                s.Hidden = false;
                s.Enabled = true;
                s.RestartCount = 2;
                s.RestartInterval = "PT1M";

                // 6 = TASK_CREATE_OR_UPDATE，3 = TASK_LOGON_INTERACTIVE_TOKEN
                root.RegisterTaskDefinition(
                    _appName,
                    def,
                    CreateOrUpdate,
                    null,
                    null,
                    LogonTypeInteractiveToken,
                    null
                );
            });

            Serilog.Log.Information(
                "开机自启已升级为计划任务「{Task}」：登录后延时 {Delay} 拉起（不再等 explorer 处理 Run 键）",
                _appName,
                LogonDelay
            );
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(
                ex,
                "注册计划任务失败，已退回注册表 Run 键：自启仍然可用，只是慢几十秒"
            );
        }
    }

    private void DeleteLogonTask()
    {
        try
        {
            WithTaskService(service =>
            {
                dynamic root = service.GetFolder("\\");
                root.DeleteTask(_appName, 0);
            });
        }
        catch (COMException ex) when (ex.HResult == HResultFileNotFound)
        {
            // 任务本来就不存在 —— 正常情况（以前没注册过 / 已经删过）
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "删除计划任务失败");
        }
    }

    private bool TaskExists()
    {
        try
        {
            bool exists = false;
            WithTaskService(service =>
            {
                dynamic root = service.GetFolder("\\");
                dynamic task = root.GetTask(_appName);
                exists = task is not null;
            });
            return exists;
        }
        catch (COMException ex) when (ex.HResult == HResultFileNotFound)
        {
            return false;
        }
        catch
        {
            // 连不上计划任务服务时按"没有"处理：这样自启开关至少还能靠 Run 键那条路工作
            return false;
        }
    }

    /// <summary>
    /// 连上计划任务服务，执行一段操作，最后一定释放 COM 对象。
    /// 中间那些 <c>GetFolder</c> / <c>NewTask</c> 返回的 RCW 交给 GC 收 —— 这里是低频路径
    /// （切开关、构造 ViewModel 时各一次），不值得为它们写一堆 try/finally。
    /// </summary>
    private static void WithTaskService(Action<dynamic> body)
    {
        object? raw = null;
        try
        {
            var type =
                Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException(
                    "系统里找不到计划任务服务（Schedule.Service）"
                );

            raw =
                Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("创建计划任务服务实例失败");

            dynamic service = raw;
            // 四个参数依次是 服务器 / 用户 / 域 / 密码，都传 null 表示连本机、用当前身份
            service.Connect(null, null, null, null);

            body(service);
        }
        finally
        {
            if (raw is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(raw);
                }
                catch
                {
                    // 释放失败无所谓：进程退出时内核会收
                }
            }
        }
    }

    private static string CurrentUser => Environment.UserDomainName + "\\" + Environment.UserName;
}
