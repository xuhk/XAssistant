using System.Collections.ObjectModel;

namespace XAssistant.Models;

/// <summary>「关于」页里的一段更新日志：一个版本标题 + 若干分类。</summary>
public sealed class ChangelogSection
{
    public ChangelogSection(string title) => Title = title;

    public string Title { get; }

    public ObservableCollection<ChangelogCategory> Categories { get; } = new();
}

/// <summary>版本下的一组变更，例如「新增」「修复」「改进」「其他」。</summary>
public sealed class ChangelogCategory
{
    public ChangelogCategory(string name) => Name = name;

    public string Name { get; }

    public ObservableCollection<string> Items { get; } = new();
}
