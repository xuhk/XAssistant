using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace XAssistant.Services;

public sealed class ProcessUsageTracker : IDisposable
{
    private readonly ILogger<ProcessUsageTracker> _logger;
    private readonly string _dbPath;
    private const string DbFile = "app_usage.db";
    private const double MinimumSessionSeconds = 1.0;
    private const double PendingProcessTimeoutSeconds = 3.0;
    private bool _isStopped;

    private readonly ConcurrentDictionary<string, AppSessionState> _appSessions = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<uint, string> _pidToAppName = new();
    private readonly ConcurrentDictionary<uint, PendingProcessInfo> _pendingProcesses = new();

    /// <summary>
    /// 「可以续接」的会话：键 = 规范化进程名 + "|" + 开始时间，值 = 那行会话的 Id。
    ///
    /// <para>
    /// 装的都是**今天**、且**已经被关掉**（EndTime 非空）的行 —— 也就是上一次正常退出
    /// （<see cref="Stop"/> 会把活跃会话按结束写库）留下的那些。下次启动时如果同一个进程还在跑
    /// （OS 层的 StartTime 没变），就该**续上这一行**，而不是再插一条新行。
    /// </para>
    ///
    /// <para>
    /// 不这么做会怎样（2026-09-29 实测）：chrome 08:23:40 启动 → 10:41:35 退出程序时把这一行
    /// 按 8,274 秒关掉 → 10:41:35 重新启动后 <see cref="CaptureExistingProcesses"/> 又按
    /// "进程的 08:23:40 启动时间"插一条新行 → 同一个 StartTime 有两行，当天时长按两遍算
    /// （界面显示 5.16h，真实 2.30h；当天 7 个常驻进程全部翻倍）。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, long> _resumableSessions = new(StringComparer.Ordinal);

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Timer? _titleRefreshTimer;
    private CancellationTokenSource? _cts;

    private static readonly string SelfProcessName = NormalizeProcessName(
        Process.GetCurrentProcess().ProcessName
    );

    public ProcessUsageTracker(ILogger<ProcessUsageTracker> logger)
    {
        _logger = logger;
        _dbPath = Path.Combine(AppDataPathHelper.GetAppDataFolder(), DbFile);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        InitializeDatabase();
        RecoverUnfinishedSessions(); // 恢复未完成的会话（上次是被杀掉的情况）
        SnapshotResumableSessions(); // 上次是**正常退出**留下的行（见方法注释）
        CaptureExistingProcesses();
        StartWatchers();

        // 每5秒刷新一次（同时更新窗口标题、检查待确认进程、累计时长）
        _titleRefreshTimer = new Timer(
            _ => RefreshAndAccumulate(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5)
        );

        _logger.LogInformation(
            "进程使用追踪已启动（数据库：{Db}，最小会话时长：{MinSec}s）",
            _dbPath,
            MinimumSessionSeconds
        );
    }

    public void Stop()
    {
        if (_isStopped)
            return;
        _isStopped = true;
        _titleRefreshTimer?.Dispose();
        StopWatchers();

        // 立即关闭所有活跃会话，写入最终时长和结束时间
        CloseAllRunningSessions();

        _appSessions.Clear();
        _pidToAppName.Clear();
        _pendingProcesses.Clear();
    }

    private void CloseAllRunningSessions()
    {
        var now = DateTime.Now;
        foreach (var kvp in _appSessions)
        {
            var state = kvp.Value;
            if (state.ProcessCount <= 0)
                continue;

            double delta = (now - state.LastUpdateTime).TotalSeconds;
            double total = state.AccumulatedSeconds + (delta > 0 ? delta : 0);

            if (total < MinimumSessionSeconds)
                DeleteSession(state.SessionId);
            else
                CloseSession(state.SessionId, now, total, state.LastWindowTitle);
        }
    }

    public void Dispose()
    {
        _titleRefreshTimer?.Dispose();
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==================== 数据库初始化与迁移 ====================
    private void InitializeDatabase()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        Directory.CreateDirectory(dir);
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS ProcessSession (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProcessName TEXT NOT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT,
                    WindowTitle TEXT,
                    Date TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // 添加新增列（忽略重复错误）
        foreach (
            var col in new[] { "AccumulatedSeconds REAL NOT NULL DEFAULT 0", "LastUpdateTime TEXT" }
        )
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"ALTER TABLE ProcessSession ADD COLUMN {col}";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column")) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "添加列 {Col} 异常", col);
            }
        }

        // 数据迁移：为旧记录填充累计时长
        try
        {
            using var cmd = conn.CreateCommand();
            // 有 EndTime 的记录：用差值更新 AccumulatedSeconds，LastUpdateTime 设为 EndTime
            cmd.CommandText = """
                UPDATE ProcessSession
                SET AccumulatedSeconds = (julianday(EndTime) - julianday(StartTime)) * 86400,
                    LastUpdateTime = EndTime
                WHERE EndTime IS NOT NULL AND (AccumulatedSeconds = 0 OR AccumulatedSeconds IS NULL);
                """;
            cmd.ExecuteNonQuery();

            // 无 EndTime 的旧未完成会话（正常情况下不存在，但之前 bug 可能残留）：直接关闭
            cmd.CommandText = """
                UPDATE ProcessSession
                SET EndTime = StartTime,
                    AccumulatedSeconds = 0,
                    LastUpdateTime = StartTime
                WHERE EndTime IS NULL AND AccumulatedSeconds = 0;
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "数据迁移失败");
        }

        // 索引
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_processsession_date ON ProcessSession(Date);";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_processsession_process ON ProcessSession(ProcessName);";
            cmd.ExecuteNonQuery();
        }
    }

    // ==================== 会话恢复 ====================
    private void RecoverUnfinishedSessions()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, ProcessName, StartTime, Date, AccumulatedSeconds, LastUpdateTime FROM ProcessSession WHERE EndTime IS NULL";
            using var reader = cmd.ExecuteReader();

            var recovered =
                new List<(
                    long id,
                    string name,
                    string startStr,
                    string dateStr,
                    double acc,
                    string? lastUpdStr
                )>();
            while (reader.Read())
            {
                recovered.Add(
                    (
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetDouble(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5)
                    )
                );
            }

            if (recovered.Count == 0)
                return;

            var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
            var allProcs = Process.GetProcesses();

            foreach (var item in recovered)
            {
                if (!DateTime.TryParse(item.startStr, out var startTime))
                    startTime = DateTime.Now;

                DateTime lastUpdate =
                    item.lastUpdStr != null && DateTime.TryParse(item.lastUpdStr, out var lu)
                        ? lu
                        : startTime;
                double totalSec = item.acc; // 已记录的累计秒数

                // 理论结束时间 = 开始时间 + 累计秒数（即进程实际退出的时间点）
                var expectedEndTime = startTime.AddSeconds(totalSec);

                // 查找仍在运行的匹配进程（排除自身和 Session 0）
                var matchingProcs = allProcs
                    .Where(p =>
                    {
                        try
                        {
                            if (p.Id == Environment.ProcessId)
                                return false;
                            if (p.SessionId == 0)
                                return false;
                            return NormalizeProcessName(p.ProcessName) == item.name;
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();

                // 如果会话日期不是今天 → 跨天场景
                if (item.dateStr != todayStr)
                {
                    DateTime oldDate = DateTime.Parse(item.dateStr);
                    DateTime endOfOldDay = oldDate.Date.AddDays(1).AddMilliseconds(-1);

                    if (matchingProcs.Count > 0)
                    {
                        // 获取匹配进程中最小的启动时间（通常就是进程真正的启动时间）
                        DateTime earliestStart = matchingProcs.Min(p => p.StartTime);
                        double gap = (earliestStart - lastUpdate).TotalSeconds;

                        if (gap <= 300) // 间隔 ≤ 5分钟，认为是连续运行（未关机场景）
                        {
                            // 原有跨天逻辑：旧会话结束于昨天午夜
                            double finalAcc = (endOfOldDay - startTime).TotalSeconds;
                            if (finalAcc < MinimumSessionSeconds)
                                DeleteSession(item.id);
                            else
                                CloseSession(item.id, endOfOldDay, finalAcc, null);

                            // 为今天创建新会话
                            var todayStart = DateTime.Today;
                            long newSessionId = InsertAppSession(item.name, todayStart);
                            var newState = new AppSessionState
                            {
                                ProcessCount = matchingProcs.Count,
                                SessionId = newSessionId,
                                StartTime = todayStart,
                                AccumulatedSeconds = (DateTime.Now - todayStart).TotalSeconds,
                                LastUpdateTime = DateTime.Now,
                                LastWindowTitle = GetBestWindowTitle(matchingProcs),
                            };
                            _appSessions[item.name] = newState;
                            foreach (var proc in matchingProcs)
                                _pidToAppName[(uint)proc.Id] = item.name;
                        }
                        else
                        {
                            // 间隔过大 → 进程是今天重新启动的，旧会话不应延续
                            // 使用理论结束时间关闭旧会话
                            if (totalSec < MinimumSessionSeconds)
                                DeleteSession(item.id);
                            else
                                CloseSession(item.id, expectedEndTime, totalSec, null);
                            // 不创建新会话，让 CaptureExistingProcesses 稍后为这些进程创建正确起始时间的会话
                        }
                    }
                    else
                    {
                        // 无匹配进程 → 进程已退出，使用理论结束时间关闭
                        if (totalSec < MinimumSessionSeconds)
                            DeleteSession(item.id);
                        else
                            CloseSession(item.id, expectedEndTime, totalSec, null);
                    }
                    continue;
                }

                // 日期相同（今天）
                if (matchingProcs.Count > 0)
                {
                    // 进程仍在运行，沿用原有会话
                    var state = new AppSessionState
                    {
                        ProcessCount = matchingProcs.Count,
                        SessionId = item.id,
                        StartTime = startTime,
                        AccumulatedSeconds = totalSec,
                        LastUpdateTime = lastUpdate,
                        LastWindowTitle = GetBestWindowTitle(matchingProcs),
                    };
                    _appSessions[item.name] = state;
                    foreach (var proc in matchingProcs)
                        _pidToAppName[(uint)proc.Id] = item.name;
                }
                else
                {
                    // 进程已退出，使用理论结束时间关闭
                    if (totalSec < MinimumSessionSeconds)
                        DeleteSession(item.id);
                    else
                        CloseSession(item.id, expectedEndTime, totalSec, null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复未完成会话失败");
        }
    }

    private static string? GetBestWindowTitle(List<Process> procs)
    {
        foreach (var proc in procs)
        {
            try
            {
                var title = proc.MainWindowTitle?.Trim();
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
            catch { }
        }
        return null;
    }

    // ==================== 续接上次正常退出留下的会话 ====================

    /// <summary>
    /// 记下今天那些**已经结束**的会话（键 = 进程名 + 开始时间），供
    /// <see cref="OpenSessionFor"/> 判断"这个进程是不是该续上老行"。
    ///
    /// <para>
    /// 判据是"进程名 + **精确到毫秒**的 OS 启动时间"。同一个进程实例只可能有一条这样的记录，
    /// 所以只要还能枚举到这个启动时间相同的进程，就说明那次运行从没真正结束过 —— 不需要任何
    /// "多久算近"的时间启发式。同组里若已经有老 bug 留下的重复行，取累计最大的那条续接。
    /// </para>
    /// </summary>
    private void SnapshotResumableSessions()
    {
        _resumableSessions.Clear();
        try
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            var found = new Dictionary<string, (long Id, double Acc)>(StringComparer.Ordinal);

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, ProcessName, StartTime, AccumulatedSeconds
                FROM ProcessSession
                WHERE EndTime IS NOT NULL AND Date = $today
                ORDER BY AccumulatedSeconds
                """;
            cmd.Parameters.AddWithValue("$today", today);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string name = NormalizeProcessName(reader.GetString(1));
                string start = reader.GetString(2);
                double acc = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);

                // 升序读进来、后写覆盖先写 → 留下的就是累计最大的那条
                found[$"{name}|{start}"] = (id, acc);
            }

            foreach (var pair in found)
                _resumableSessions[pair.Key] = pair.Value.Id;

            if (_resumableSessions.Count > 0)
                _logger.LogInformation(
                    "可续接的会话 {Count} 条（今天已结束、进程可能还在跑的那些）",
                    _resumableSessions.Count
                );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取可续接会话失败");
        }
    }

    /// <summary>
    /// 第一次见到这个进程时开一条会话：能续上上次正常退出留下的老行就续，否则新插一条。
    ///
    /// <para>
    /// 续接时把 <c>EndTime</c> 清回 NULL、累计时长按"进程启动到现在"重算 —— 因为那条老行记的是
    /// 同一次连续运行的前半段，被程序退出硬切了一刀，合起来才是完整的一段。
    /// </para>
    ///
    /// <para>
    /// **为什么"精确匹配启动时间"就够了，不需要"多久算近"的启发式**：能走到这里说明这个进程
    /// 通过了 <see cref="IsUserApplication"/>（有主窗口、不是 Session 0、不在 Windows 目录、
    /// 不在系统进程名单里）。同一个进程名通常只有一个这样的进程，而它的 OS 启动时间在同一日期下
    /// 是唯一的 —— 能匹配上，就说明那次运行确实没结束过。
    /// </para>
    /// </summary>
    private AppSessionState OpenSessionFor(string appName, DateTime startTime, string? windowTitle)
    {
        string? title = string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle.Trim();
        string key = $"{appName.ToLowerInvariant()}|{startTime:yyyy-MM-dd HH:mm:ss.fff}";

        if (_resumableSessions.Remove(key, out long existingId))
        {
            var now = DateTime.Now;
            double accumulated = Math.Max(0, (now - startTime).TotalSeconds);
            ResumeSession(existingId, accumulated, now);

            _logger.LogInformation(
                "续接会话：{App}（{Start:HH:mm:ss} 起，累计 {Minutes:0} 分钟），不另建新行 Id={Id}",
                appName,
                startTime,
                accumulated / 60,
                existingId
            );

            return new AppSessionState
            {
                ProcessCount = 1,
                SessionId = existingId,
                StartTime = startTime,
                LastUpdateTime = now,
                AccumulatedSeconds = accumulated,
                LastWindowTitle = title,
            };
        }

        long sessionId = InsertAppSession(appName, startTime);
        return new AppSessionState
        {
            ProcessCount = 1,
            SessionId = sessionId,
            StartTime = startTime,
            LastUpdateTime = startTime,
            AccumulatedSeconds = 0,
            LastWindowTitle = title,
        };
    }

    /// <summary>把一条已结束的会话重新打开（EndTime 清空、累计重算、最后更新设为现在）。</summary>
    private void ResumeSession(long sessionId, double accumulatedSeconds, DateTime updateTime)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE ProcessSession
                SET EndTime = NULL, AccumulatedSeconds = $acc, LastUpdateTime = $time
                WHERE Id = $id
                """;
            cmd.Parameters.AddWithValue("$acc", accumulatedSeconds);
            cmd.Parameters.AddWithValue("$time", updateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "续接会话失败 Id={Id}", sessionId);
        }
    }

    // ==================== 现有进程捕获 ====================
    private void CaptureExistingProcesses()
    {
        try
        {
            var allProcs = Process.GetProcesses();
            _logger.LogInformation("开始捕获现有进程，共 {Count} 个", allProcs.Length);
            foreach (var proc in allProcs)
            {
                // 新增：跳过已经在追踪列表中的进程
                if (_pidToAppName.ContainsKey((uint)proc.Id))
                    continue;

                string procName = "unknown";
                try
                {
                    procName = proc.ProcessName;
                }
                catch { }

                if (!IsUserApplication(proc))
                    continue;

                DateTime startTime;
                try
                {
                    startTime = proc.StartTime;
                }
                catch
                {
                    startTime = DateTime.Now;
                }
                var appName = NormalizeProcessName(procName);
                string? title = null;
                try
                {
                    title = proc.MainWindowTitle;
                }
                catch { }

                AddProcessToAppSession((uint)proc.Id, appName, startTime, title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举现有进程失败");
        }
    }

    // ==================== 会话管理（插入/更新/关闭） ====================
    private long InsertAppSession(string processName, DateTime startTime)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ProcessSession (ProcessName, StartTime, Date, AccumulatedSeconds, LastUpdateTime)
                VALUES ($name, $start, $date, 0, $start);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$name", processName);
            cmd.Parameters.AddWithValue("$start", startTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("$date", startTime.ToString("yyyy-MM-dd"));
            var result = cmd.ExecuteScalar();
            return result is long id ? id : -1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "插入会话失败: {Process}", processName);
            return -1;
        }
    }

    private void CloseSession(
        long sessionId,
        DateTime endTime,
        double finalAccumulatedSeconds,
        string? windowTitle
    )
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE ProcessSession
                SET EndTime = $end, WindowTitle = $title,
                    AccumulatedSeconds = $acc, LastUpdateTime = $end
                WHERE Id = $id
                """;
            cmd.Parameters.AddWithValue("$end", endTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("$title", (object?)windowTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$acc", finalAccumulatedSeconds);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭会话失败 Id={Id}", sessionId);
        }
    }

    private void DeleteSession(long sessionId)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ProcessSession WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除短时会话失败 Id={Id}", sessionId);
        }
    }

    // ==================== 定时刷新与累计 ====================
    private void RefreshAndAccumulate()
    {
        RefreshWindowTitles(); // 包含窗口标题更新 + 待确认进程检查
        AccumulateRunningSessions(); // 更新累计时长
    }

    private void AccumulateRunningSessions()
    {
        var now = DateTime.Now;
        foreach (var kvp in _appSessions)
        {
            var state = kvp.Value;
            if (state.ProcessCount <= 0)
                continue;
            double delta = (now - state.LastUpdateTime).TotalSeconds;
            if (delta > 0)
            {
                state.AccumulatedSeconds += delta;
                state.LastUpdateTime = now;
                _ = UpdateAccumulatedTimeAsync(state.SessionId, state.AccumulatedSeconds, now);
            }
        }
    }

    private async Task UpdateAccumulatedTimeAsync(
        long sessionId,
        double accumulatedSeconds,
        DateTime updateTime
    )
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE ProcessSession SET AccumulatedSeconds = @sec, LastUpdateTime = @time WHERE Id = @id";
            cmd.Parameters.AddWithValue("@sec", accumulatedSeconds);
            cmd.Parameters.AddWithValue("@time", updateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("@id", sessionId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新累计时长失败 SessionId={Id}", sessionId);
        }
    }

    // ==================== 进程添加/移除 ====================
    private void AddProcessToAppSession(
        uint processId,
        string appName,
        DateTime startTime,
        string? windowTitle
    )
    {
        _pidToAppName[processId] = appName;
        _appSessions.AddOrUpdate(
            appName,
            _ => OpenSessionFor(appName, startTime, windowTitle),
            (_, state) =>
            {
                Interlocked.Increment(ref state.ProcessCount);
                if (!string.IsNullOrWhiteSpace(windowTitle))
                    state.LastWindowTitle = windowTitle.Trim();
                return state;
            }
        );
    }

    private void RemoveProcessFromAppSession(uint processId)
    {
        if (!_pidToAppName.TryRemove(processId, out var appName))
            return;
        if (_appSessions.TryGetValue(appName, out var state))
        {
            int newCount = Interlocked.Decrement(ref state.ProcessCount);
            if (newCount == 0)
            {
                if (_appSessions.TryRemove(appName, out _))
                {
                    var now = DateTime.Now;
                    double delta = (now - state.LastUpdateTime).TotalSeconds;
                    double totalSeconds = state.AccumulatedSeconds + (delta > 0 ? delta : 0);

                    if (totalSeconds < MinimumSessionSeconds)
                    {
                        DeleteSession(state.SessionId);
                    }
                    else
                    {
                        CloseSession(state.SessionId, now, totalSeconds, state.LastWindowTitle);
                    }
                }
            }
        }
    }

    // ==================== WMI 监视 ====================
    private void StartWatchers()
    {
        try
        {
            _startWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace")
            );
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace")
            );
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "无法启动 WMI 进程监视");
        }
    }

    private void StopWatchers()
    {
        try
        {
            _startWatcher?.Stop();
            _stopWatcher?.Stop();
        }
        catch
        { /* ignore */
        }
        finally
        {
            _startWatcher?.Dispose();
            _stopWatcher?.Dispose();
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        var rawName = e.NewEvent.Properties["ProcessName"]?.Value?.ToString();
        var pidRaw = e.NewEvent.Properties["ProcessID"]?.Value;
        if (string.IsNullOrEmpty(rawName) || pidRaw == null)
        {
            // _logger.LogDebug("WMI 启动事件缺少名称或 PID，忽略");
            return;
        }

        var pid = Convert.ToUInt32(pidRaw);
        var appName = NormalizeProcessName(rawName);

        // _logger.LogInformation("检测到进程启动：{App} (PID {Pid})", appName, pid);

        if (!IsPotentialUserProcess(pid, rawName))
        {
            // _logger.LogDebug("进程 {App} (PID {Pid}) 被 IsPotentialUserProcess 过滤", appName, pid);
            return;
        }

        DateTime startTime = DateTime.Now;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            startTime = proc.StartTime;
        }
        catch
        {
            // _logger.LogDebug(ex, "获取进程 {App} 启动时间失败，使用当前时间", appName);
        }

        bool hasWindow = false;
        string? title = null;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            hasWindow = proc.MainWindowHandle != IntPtr.Zero;
            if (hasWindow)
                title = proc.MainWindowTitle;
        }
        catch
        {
            // _logger.LogDebug(ex, "检查进程 {App} 窗口句柄失败", appName);
        }

        if (hasWindow && !string.IsNullOrWhiteSpace(title))
        {
            _logger.LogInformation(
                "进程 {App} (PID {Pid}) 已有窗口，直接添加会话，标题：{Title}",
                appName,
                pid,
                title
            );
            AddProcessToAppSession(pid, appName, startTime, title);
        }
        else
        {
            // _logger.LogInformation(
            //     "进程 {App} (PID {Pid}) 暂未出现窗口，加入等待队列（当前队列 {Count}）",
            //     appName,
            //     pid,
            //     _pendingProcesses.Count + 1
            // );
            _pendingProcesses[pid] = new PendingProcessInfo
            {
                AppName = appName,
                StartTime = startTime,
                AddedAt = DateTime.UtcNow,
            };
            // 之前的 3 秒超时丢弃代码删除，或至少把超时日志化
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        var pidRaw = e.NewEvent.Properties["ProcessID"]?.Value;
        if (pidRaw == null)
            return;
        var pid = Convert.ToUInt32(pidRaw);
        _pendingProcesses.TryRemove(pid, out _);
        RemoveProcessFromAppSession(pid);
    }

    // ==================== 窗口标题刷新 & 待确认进程检查 ====================
    private void RefreshWindowTitles()
    {
        try
        {
            foreach (var kvp in _appSessions)
            {
                var state = kvp.Value;
                if (state.ProcessCount <= 0)
                    continue;
                uint? pid = _pidToAppName
                    .FirstOrDefault(x =>
                        x.Value.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)
                    )
                    .Key;
                if (pid != null)
                {
                    var title = GetWindowTitle(pid.Value);
                    if (!string.IsNullOrWhiteSpace(title))
                        state.LastWindowTitle = title.Trim();
                }
            }

            CheckPendingProcessesForWindow();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "刷新窗口标题出错");
        }
    }

    private void CheckPendingProcessesForWindow()
    {
        // int pendingCount = _pendingProcesses.Count;
        // if (pendingCount > 0)
        // _logger.LogDebug("检查 {Count} 个待确认进程的窗口状态...", pendingCount);

        foreach (var kvp in _pendingProcesses)
        {
            var pid = kvp.Key;
            var pending = kvp.Value;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    var title = proc.MainWindowTitle;
                    if (
                        !string.IsNullOrWhiteSpace(title) && _pendingProcesses.TryRemove(pid, out _)
                    )
                    {
                        _logger.LogInformation(
                            "待确认进程 {App} (PID {Pid}) 窗口已出现，转移到追踪，标题：{Title}",
                            pending.AppName,
                            pid,
                            title
                        );
                        AddProcessToAppSession(pid, pending.AppName, pending.StartTime, title);
                    }
                }
            }
            catch
            {
                // _logger.LogDebug(
                //     ex,
                //     "检查待确认进程 {App} (PID {Pid}) 时出错，可能已退出",
                //     pending.AppName,
                //     pid
                // );
                _pendingProcesses.TryRemove(pid, out _); // 进程已死，移除
            }
        }
    }

    private static string? GetWindowTitle(uint processId)
    {
        try
        {
            using var proc = Process.GetProcessById((int)processId);
            return proc.MainWindowTitle?.Trim();
        }
        catch
        {
            return null;
        }
    }

    // ==================== 过滤逻辑 ====================
    private static readonly HashSet<string> SystemProcessNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "explorer",
        "taskmgr",
        "sihost",
        "applicationframehost",
        "svchost",
        "conhost",
        "dwm",
        "tabtip",
        "phoneexperiencehost",
        "shellexperiencehost",
        "searchapp",
        "startmenuexperiencehost",
    };

    private bool IsPotentialUserProcess(uint processId, string rawProcessName)
    {
        if (processId == Environment.ProcessId)
            return false;
        var normalized = NormalizeProcessName(rawProcessName);
        // 排除所有同名的自身进程
        if (normalized == SelfProcessName)
            return false;
        if (SystemProcessNames.Contains(normalized))
            return false;
        try
        {
            using var proc = Process.GetProcessById((int)processId);
            if (proc.SessionId == 0)
                return false;
            string? path = null;
            try
            {
                path = proc.MainModule?.FileName;
            }
            catch { }
            if (!string.IsNullOrEmpty(path))
            {
                var dir = Path.GetDirectoryName(path) ?? "";
                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (dir.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    private bool IsUserApplication(Process proc)
    {
        if (proc.Id == Environment.ProcessId)
        {
            // _logger.LogDebug("跳过自身进程 PID {Pid}", proc.Id);
            return false;
        }
        string procName;
        try
        {
            procName = proc.ProcessName;
        }
        catch
        {
            return false;
        }
        if (NormalizeProcessName(procName) == SelfProcessName)
        {
            // _logger.LogDebug("跳过同名自身进程 {Proc}", procName);
            return false;
        }
        try
        {
            if (proc.SessionId == 0)
            {
                // _logger.LogDebug("跳过 Session 0 进程 {Proc}", procName);
                return false;
            }
            if (proc.MainWindowHandle == IntPtr.Zero)
            {
                // _logger.LogDebug("跳过无窗口进程 {Proc}", procName);
                return false;
            }
            string? path = null;
            try
            {
                path = proc.MainModule?.FileName;
            }
            catch { }
            if (!string.IsNullOrEmpty(path))
            {
                var dir = Path.GetDirectoryName(path) ?? "";
                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (dir.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                {
                    // _logger.LogDebug("跳过 Windows 目录进程 {Proc}，路径 {Path}", procName, path);
                    return false;
                }
            }
            if (SystemProcessNames.Contains(NormalizeProcessName(procName)))
            {
                // _logger.LogDebug("跳过系统进程名 {Proc}", procName);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeProcessName(string rawName)
    {
        if (rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            rawName = rawName[..^4];
        return rawName.ToLowerInvariant();
    }

    // ==================== 内部类 ====================
    private class AppSessionState
    {
        public int ProcessCount;
        public long SessionId;
        public DateTime StartTime;
        public string? LastWindowTitle;
        public double AccumulatedSeconds;
        public DateTime LastUpdateTime;
    }

    private class PendingProcessInfo
    {
        public string AppName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime AddedAt { get; set; }
    }
}
