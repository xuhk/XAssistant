namespace XAssistant.Services;

/// <summary>
/// 版本号比较。抽成独立文件是因为它是**纯函数** —— 不碰 HTTP、不碰界面，
/// 可以被 <c>temp/version-math-probe/</c> 那个探针项目直接编译进去跑用例
/// （手写比较最典型的错就是 <c>1.10.0</c> 被字符串比较判成小于 <c>1.9.0</c>）。
/// </summary>
public static class VersionMath
{
    /// <summary>
    /// 语义化版本比较：返回 &gt;0 表示 <paramref name="a"/> 更新。
    /// 主 / 次 / 修订三段，缺的按 0 算；带预发布后缀的（<c>1.2.0-rc1</c>）排在同名正式版之前。
    /// 段里出现非数字时取前面的数字部分（版本号畸形也不让整件事失败）。
    /// </summary>
    public static int Compare(string a, string b)
    {
        (string an, string apre) = SplitPrerelease(a ?? "");
        (string bn, string bpre) = SplitPrerelease(b ?? "");

        int[] ap = ParseParts(an);
        int[] bp = ParseParts(bn);
        for (int i = 0; i < 3; i++)
        {
            int c = ap[i].CompareTo(bp[i]);
            if (c != 0)
                return c;
        }

        if (apre == bpre)
            return 0;
        if (apre.Length == 0)
            return 1; // 正式版 > 预发布版
        if (bpre.Length == 0)
            return -1;
        return string.CompareOrdinal(apre, bpre);
    }

    private static (string Numbers, string Pre) SplitPrerelease(string version)
    {
        int dash = version.IndexOf('-');
        return dash < 0 ? (version, "") : (version[..dash], version[(dash + 1)..]);
    }

    private static int[] ParseParts(string numbers)
    {
        var parts = new int[3];
        string[] segs = numbers.Split('.');
        for (int i = 0; i < 3 && i < segs.Length; i++)
        {
            string s = segs[i].Trim();
            int digits = 0;
            while (digits < s.Length && char.IsAsciiDigit(s[digits]))
                digits++;
            if (digits == 0)
                continue;
            _ = int.TryParse(s[..digits], out parts[i]);
        }
        return parts;
    }
}
