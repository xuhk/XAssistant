namespace XAssistant.Services.Interfaces;

/// <summary>
/// Windows 开机自启动。
///
/// 挂两条路：计划任务「用户登录时触发」负责**提前**，注册表 HKCU\...\Run 负责**兜底**
/// （计划任务被安全软件 / 组策略拦掉时仍然能自启，只是慢几十秒）。
/// 开关是整体的：<see cref="SetAutoStart"/> 传 false 时两条一起断。
/// </summary>
public interface IStartupService
{
    /// <summary>
    /// 检查当前应用是否已设为开机自启动（两条路任意一条在，就算开着）。
    /// </summary>
    bool IsStartWithWindowsEnabled();

    /// <summary>
    /// 启用或禁用开机自启动。
    /// </summary>
    /// <param name="enable">true 为启用，false 为取消</param>
    void SetAutoStart(bool enable);
}
