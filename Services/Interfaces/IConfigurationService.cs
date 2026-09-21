// Services/Interfaces/IConfigurationService.cs
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

/// <summary>
/// 应用程序配置服务，管理各类持久化设置。
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// 当前应用配置。
    /// </summary>
    AppSettings Settings { get; }

    /// <summary>
    /// 获取上次程序退出时是否处于鼠标点击录制状态的配置。
    /// </summary>
    bool GetRecordingAutoStart();

    /// <summary>
    /// 设置鼠标点击录制状态（用于下次启动自动恢复）。
    /// </summary>
    void SetRecordingAutoStart(bool isRecording);

    /// <summary>
    /// 获取上次程序退出时是否处于键盘按键录制状态的配置。
    /// </summary>
    bool GetKeyRecordingAutoStart();

    /// <summary>
    /// 设置键盘按键录制状态。
    /// </summary>
    void SetKeyRecordingAutoStart(bool autoStart);

    // ---------- 窗口位置/状态 ----------
    double GetWindowWidth();
    void SetWindowWidth(double width);

    double GetWindowHeight();
    void SetWindowHeight(double height);

    // ---------- 日志面板状态 ----------
    bool GetIsLogExpanded();
    void SetIsLogExpanded(bool expanded);
}
