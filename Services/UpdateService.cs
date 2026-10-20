using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace XAssistant.Services;

public enum UpdateStatus
{
    /// <summary>拿到的 tag 不比当前版本新（含本地比线上还新的情况）。</summary>
    UpToDate,

    /// <summary>线上有更新的版本。</summary>
    UpdateAvailable,

    /// <summary>网络不通 / 超时 / 仓库没有 release。**不抛异常**，靠这个状态回给页面。</summary>
    Failed,
}

/// <summary>一次检查更新的结果。</summary>
public readonly record struct UpdateCheckResult(
    UpdateStatus Status,
    string Current,
    string? Latest,
    string Detail,
    bool Debounced
);

/// <summary>
/// 检查更新。**只做"发现"，不做下载安装** —— 替换正在运行的 exe 要额外的 helper 进程，
/// 还要处理 76 MB 包的进度、断点、哈希校验、失败回滚，工作量比前面所有加起来都大，
/// 收益只是省一次手动下载。发现新版就把 releases 页面给用户（这里不解析更新说明）。
///
/// **两个端点并发试，哪个通用哪个** —— 这不是保险起见，是两个端点**都间歇性不可用**。
/// 本机实测（同一台机器，原始输出存档在 <c>docs/ui-upgrade/evidence/update-endpoint-probe.txt</c>）：
///
/// | 时刻 | <c>github.com/…/releases/latest</c>（读 302 的 Location） | <c>api.github.com/repos/…/releases/latest</c>（读 JSON 的 tag_name） |
/// |---|---|---|
/// | 09-21 | 302，2.2 s | 403，间歇被拦 |
/// | 09-22 中午 | 连不上（8 s 超时；放宽到 25 s 是 10.6 s 连接失败，连 <c>github.com</c> 首页也失败） | 200，631 / 240 ms |
/// | 09-22 晚上 | **先 302 成功 679 ms，几秒后再请求就 8 s 超时** | 200，675 ms |
///
/// 看最后一行：**同一个端点会在几秒之内时通时不通** —— 所以"失败就重试同一个端点"也救不了，
/// 只有两个并发发出去、任一成功即用才稳。顺带一提，这样总耗时是较快那个，
/// 不会出现"先试的那个正好不通、白等一轮"。
///
/// 用量极低：只在用户点「检查更新」时请求，外加 5 分钟防抖，远够 API 那条 60 次/小时/IP 的匿名配额。
/// 两个端点都会自动跳过 draft 与 pre-release，正好是需要的语义。
/// </summary>
public sealed class UpdateService
{
    /// <summary>给用户看的更新说明页面，也是发现新版后打开的那个地址。</summary>
    public const string ReleasePageUrl = "https://github.com/xuhk/XAssistant/releases/latest";

    private const string OwnerRepo = "xuhk/XAssistant";

    private static readonly string ApiEndpoint =
        $"https://api.github.com/repos/{OwnerRepo}/releases/latest";

    /// <summary>两个探测的总超时（并发，所以是整体上限而不是各自累加）。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    /// <summary>防抖窗口：连着点只发一批请求。</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    // GitHub 对没有 User-Agent 的请求直接 403（实测报错信息就是这条）；
    // AllowAutoRedirect=false 是"读 Location 拿 tag"这招的前提
    private static readonly HttpClient Http = CreateClient();

    private readonly ILogger<UpdateService> _logger;

    // 只在 UI 线程用，但加锁更省心：以后从托盘或后台线程调也不会串
    private readonly object _gate = new();
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private UpdateCheckResult? _last;

    public UpdateService(ILogger<UpdateService> logger) => _logger = logger;

    /// <summary>查一次。**任何失败都变成 <see cref="UpdateStatus.Failed"/> 返回，绝不抛。</summary>
    public async Task<UpdateCheckResult> CheckAsync()
    {
        string current = AppVersion.SemVer;

        lock (_gate)
        {
            if (_last is { } cached && DateTimeOffset.UtcNow - _lastAttempt < MinInterval)
            {
                _logger.LogInformation(
                    "检查更新：{Minutes} 分钟内刚查过，直接用上次结果（防抖）",
                    MinInterval.TotalMinutes
                );
                return cached with { Debounced = true };
            }
            _lastAttempt = DateTimeOffset.UtcNow;
        }

        UpdateCheckResult result = await FetchAsync(current);
        lock (_gate)
            _last = result;
        return result;
    }

    private async Task<UpdateCheckResult> FetchAsync(string current)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(RequestTimeout);

        // 并发发两个（见类注释里那张表：可用性会反转，串行会在"先试的那个不通"时白等）
        Task<Probe> api = ProbeApiAsync(cts.Token);
        Task<Probe> redirect = ProbeRedirectAsync(cts.Token);
        Probe[] probes = await Task.WhenAll(api, redirect);
        Probe apiProbe = probes[0];
        Probe redirectProbe = probes[1];

        _logger.LogInformation(
            "检查更新：api 端点 {Api}（{ApiMs} ms）/ 302 端点 {Redirect}（{RedirectMs} ms），共 {Total} ms",
            Describe(apiProbe),
            apiProbe.Milliseconds,
            Describe(redirectProbe),
            redirectProbe.Milliseconds,
            sw.ElapsedMilliseconds
        );

        // 有一个成功就够
        foreach (Probe p in probes)
        {
            if (p.Tag is not { Length: > 0 } tag)
                continue;

            string latest = tag.TrimStart('v', 'V');
            int cmp = VersionMath.Compare(latest, current);
            if (cmp <= 0)
                return new UpdateCheckResult(UpdateStatus.UpToDate, current, latest, "", false);

            return new UpdateCheckResult(
                UpdateStatus.UpdateAvailable,
                current,
                latest,
                "要看更新说明就打开 releases 页面",
                false
            );
        }

        // 两个都说"没有 release"，那这个结论就是可信的
        if (probes.Any(p => p.NoRelease))
            return Failed(current, "还没有发布过版本");

        return Failed(current, "网络不通或服务端不可用，请稍后再试");
    }

    private static string Describe(Probe p) => p.Tag is { Length: > 0 } ? $"OK {p.Tag}" : p.Reason;

    /// <summary>
    /// 端点一：GitHub API，读 JSON 里的 <c>tag_name</c>。**不要用 <c>HttpClient</c> 的自动重定向**
    /// （这里本来也不需要，API 不重定向）。
    /// </summary>
    private async Task<Probe> ProbeApiAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(
                ApiEndpoint,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            long ms = sw.ElapsedMilliseconds;

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Probe.NotFound(ms);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
                return Probe.Failure("403（配额用完或地区限制）", ms);
            if (!resp.IsSuccessStatusCode)
                return Probe.Failure($"HTTP {(int)resp.StatusCode}", ms);

            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tagEl))
                return Probe.Failure("回包里没有 tag_name", ms);

            string? tag = tagEl.GetString();
            return string.IsNullOrWhiteSpace(tag)
                ? Probe.Failure("tag_name 是空的", ms)
                : Probe.Success(tag, ms);
        }
        catch (OperationCanceledException)
        {
            return Probe.Failure("超时", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "检查更新：api 端点失败");
            return Probe.Failure(Brief(ex.Message), sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 端点二：<c>releases/latest</c>，**禁自动重定向**后读 302 的 <c>Location</c>，末段就是 tag。
    /// 好处是不吃 API 的配额，也不受 API 端点在境内间歇性 403 的影响。
    /// </summary>
    private async Task<Probe> ProbeRedirectAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(
                ReleasePageUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            long ms = sw.ElapsedMilliseconds;

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Probe.NotFound(ms);
            if (!IsRedirect(resp.StatusCode))
                return Probe.Failure($"期望 302，实际 HTTP {(int)resp.StatusCode}", ms);

            string? tag = TagFromLocation(resp.Headers.Location);
            return string.IsNullOrEmpty(tag)
                ? Probe.Failure("302 的 Location 里读不出 tag", ms)
                : Probe.Success(tag, ms);
        }
        catch (OperationCanceledException)
        {
            return Probe.Failure("超时", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "检查更新：302 端点失败");
            return Probe.Failure(Brief(ex.Message), sw.ElapsedMilliseconds);
        }
    }

    private static UpdateCheckResult Failed(string current, string detail) =>
        new(UpdateStatus.Failed, current, null, detail, false);

    /// <summary>网络异常的 message 可能很长（带 URL），日志里有全的，回给页面就短一点。</summary>
    private static string Brief(string message) =>
        message.Length <= 60 ? message : message[..60] + "…";

    private static bool IsRedirect(HttpStatusCode code) =>
        code
            is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;

    /// <summary>从 302 的 Location 里取末段 = tag。Location 可能是相对地址，两种都吃。</summary>
    private static string? TagFromLocation(Uri? location)
    {
        if (location is null)
            return null;
        string path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        string trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string tag = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // 取消由 CancellationTokenSource 统一管（两个请求共用一个总超时）
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"XAssistant/{AppVersion.SemVer} (+{ReleasePageUrl})"
        );
        return client;
    }

    /// <summary>一个端点的探测结果：拿到 tag / 明确"没有 release" / 失败（带原因与耗时）。</summary>
    private readonly record struct Probe(
        string? Tag,
        bool NoRelease,
        string Reason,
        long Milliseconds
    )
    {
        public static Probe Success(string tag, long ms) => new(tag, false, "", ms);

        public static Probe NotFound(long ms) => new(null, true, "404 没有 release", ms);

        public static Probe Failure(string reason, long ms) => new(null, false, reason, ms);
    }
}
