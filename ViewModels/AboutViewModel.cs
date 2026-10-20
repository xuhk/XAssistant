using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services;

namespace XAssistant.ViewModels;

/// <summary>
/// 「关于」页：当前版本、手动检查更新、更新日志。
/// 更新不频繁也不强制，所以不占侧栏，点进这个页面才看得到。
/// </summary>
public partial class AboutViewModel : ViewModelBase
{
    private const string ChangelogResourceName = "XAssistant.CHANGELOG.md";

    private readonly UpdateService _updateService;
    private bool _updateHasNew;

    public AboutViewModel(UpdateService updateService)
    {
        _updateService = updateService;
        LoadChangelog();
    }

    /// <summary>例如 v1.0.1（f620a07）。</summary>
    public string VersionText => "v" + AppVersion.Display;

    public ObservableCollection<ChangelogSection> Changelog { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckUpdate))]
    private bool _isCheckingUpdate;

    public bool CanCheckUpdate => !IsCheckingUpdate;

    [ObservableProperty]
    private string _updateButtonText = "检查更新";

    [ObservableProperty]
    private string _updateStatusText = "";

    /// <summary>
    /// 第一次点：查 GitHub；查到新版后按钮变成「打开下载页」，再点一次打开 releases。
    /// 只做发现，不自动下载，也不在启动时联网。
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (_updateHasNew)
        {
            OpenReleasePage();
            return;
        }

        IsCheckingUpdate = true;
        UpdateButtonText = "检查中…";
        UpdateStatusText = "正在查询 GitHub…";
        try
        {
            var result = await _updateService.CheckAsync();

            if (
                result.Status == UpdateStatus.UpdateAvailable
                && result.Latest is { Length: > 0 } latest
            )
            {
                _updateHasNew = true;
                UpdateButtonText = $"打开 v{latest} 下载页";
                UpdateStatusText = $"发现新版本 v{latest}（当前 v{result.Current}）";
            }
            else if (result.Status == UpdateStatus.Failed)
            {
                _updateHasNew = false;
                UpdateButtonText = "重试检查";
                UpdateStatusText = "检查失败：" + result.Detail;
            }
            else
            {
                _updateHasNew = false;
                UpdateButtonText = "检查更新";
                UpdateStatusText = $"已是最新版本 v{result.Current}";
            }
        }
        catch (Exception ex)
        {
            _updateHasNew = false;
            UpdateButtonText = "重试检查";
            UpdateStatusText = "检查失败：" + ex.Message;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private static void OpenReleasePage()
    {
        try
        {
            Process.Start(
                new ProcessStartInfo(UpdateService.ReleasePageUrl) { UseShellExecute = true }
            );
        }
        catch
        {
            // 打不开浏览器不是致命错误：地址是常量，用户自己也能敲
        }
    }

    /// <summary>
    /// 从嵌入的 CHANGELOG.md 读。只认三种行：`## 版本 — 日期`、`### 分类`、`- 变更`，
    /// 其余（标题、空行、说明）都跳过 —— 不引 Markdown 解析库。
    /// 分类沿用 Keep a Changelog 的常见写法（新增 / 修复 / 改进 / 其他）。
    /// </summary>
    private void LoadChangelog()
    {
        try
        {
            using Stream? stream = Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream(ChangelogResourceName);
            if (stream is null)
            {
                AddFallbackChangelog();
                return;
            }

            using var reader = new StreamReader(stream);
            ChangelogSection? currentSection = null;
            ChangelogCategory? currentCategory = null;
            foreach (string raw in reader.ReadToEnd().Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    currentSection = new ChangelogSection(line[3..].Trim());
                    Changelog.Add(currentSection);
                    currentCategory = null;
                }
                else if (
                    currentSection is not null
                    && line.StartsWith("### ", StringComparison.Ordinal)
                )
                {
                    currentCategory = new ChangelogCategory(line[4..].Trim());
                    currentSection.Categories.Add(currentCategory);
                }
                else if (
                    currentSection is not null
                    && line.StartsWith("- ", StringComparison.Ordinal)
                )
                {
                    // 兼容没写分类的旧条目：归到「其他」
                    if (currentCategory is null)
                    {
                        currentCategory = new ChangelogCategory("其他");
                        currentSection.Categories.Add(currentCategory);
                    }
                    currentCategory.Items.Add(line[2..].Trim());
                }
            }

            if (Changelog.Count == 0)
                AddFallbackChangelog();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取更新日志失败");
            Changelog.Clear();
            AddFallbackChangelog();
        }
    }

    private void AddFallbackChangelog()
    {
        var section = new ChangelogSection("更新日志");
        var category = new ChangelogCategory("其他");
        category.Items.Add("暂时没有可显示的更新日志。");
        section.Categories.Add(category);
        Changelog.Add(section);
    }
}
