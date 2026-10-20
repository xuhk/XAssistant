using System.Diagnostics;

namespace XAssistant.Services;

/// <summary>
/// 运行时版本号。值取自 exe 的 ProductVersion，形如 <c>1.1.0+f6c80c7f29dc9339ea095da3b77a5c56dedba8b4</c>
/// —— .NET SDK 会自动把 git 提交 sha 接在后面，所以「装的是哪个提交」本来就能回答，
/// 缺的只是**可做大小比较的语义化版本号**（来自 csproj 的 &lt;Version&gt;）。检查更新靠它。
/// </summary>
internal static class AppVersion
{
    private static readonly (string Version, string Commit) Info = Parse();

    /// <summary>语义化版本，例如 <c>1.1.0</c>。取不到时退化为 <c>0.0.0</c>。</summary>
    public static string SemVer => Info.Version;

    /// <summary>git 提交短 sha（7 位）。取不到时为空串。</summary>
    public static string Commit => Info.Commit;

    /// <summary>给人看的形式：<c>1.1.0（f6c80c7）</c>。</summary>
    public static string Display => string.IsNullOrEmpty(Commit) ? SemVer : $"{SemVer}（{Commit}）";

    private static (string Version, string Commit) Parse()
    {
        try
        {
            string? raw = Environment.ProcessPath is { } exe
                ? FileVersionInfo.GetVersionInfo(exe).ProductVersion
                : null;
            if (string.IsNullOrWhiteSpace(raw))
                return ("0.0.0", "");

            int plus = raw.IndexOf('+');
            if (plus < 0)
                return (raw.Trim(), "");

            string version = raw[..plus].Trim();
            string sha = raw[(plus + 1)..].Trim();
            return (version, sha.Length > 7 ? sha[..7] : sha);
        }
        catch
        {
            // 取不到版本不该影响任何功能，退回 0.0.0
            return ("0.0.0", "");
        }
    }
}
