using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace XAssistant.Service;

public class UsageTrackingService : BackgroundService
{
    private readonly ILogger<UsageTrackingService> _logger;
    private readonly Stopwatch _stopwatch = new();
    private long _todaySeconds;
    private DateTime _todayDate;
    private bool _isRunning;
    private readonly object _lock = new(); // 线程同步锁
    private const string DbFolder = @"C:\ProgramData\XAssistant\UsageTracker";
    private const string DbFile = "pc_usage.db";
    private static readonly string DbPath = Path.Combine(DbFolder, DbFile);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(7); // 定时保存间隔

    public UsageTrackingService(ILogger<UsageTrackingService> logger)
    {
        _logger = logger;
        _todayDate = DateTime.Today;
        _todaySeconds = LoadTodaySeconds();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        StartSession();
    }

    private void InitializeDatabase()
    {
        Directory.CreateDirectory(DbFolder);
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS DailyUsage (
                Date TEXT PRIMARY KEY,
                Seconds INTEGER NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SessionEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventType TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                Date TEXT NOT NULL,
                TotalSeconds INTEGER NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        // 心跳：每次落盘时顺手记「最后一次确认还活着」的时刻与累计秒数。
        // 断电 / 强杀时进程走不到 Dispose，下次启动只能靠它补一条 ServiceStopped。
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ServiceHeartbeat (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Timestamp TEXT NOT NULL,
                Date TEXT NOT NULL,
                TotalSeconds INTEGER NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    // 线程安全的“获取今日总秒数”
    public long GetTodaySeconds()
    {
        lock (_lock)
        {
            return _todaySeconds + (_isRunning ? (long)_stopwatch.Elapsed.TotalSeconds : 0);
        }
    }

    private void StartSession()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                _stopwatch.Start();
                _isRunning = true;
                _logger.LogInformation("Session started.");
            }
        }
    }

    private void StopSessionAndSave()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                var elapsed = _stopwatch.Elapsed;
                _stopwatch.Stop();
                _todaySeconds += (long)elapsed.TotalSeconds;
                SaveTodaySeconds(_todaySeconds);
                _stopwatch.Reset();
                _isRunning = false;
            }
        }
    }

    private volatile bool _isSuspended;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _isSuspended = true;
            StopSessionAndSave();
            LogEvent("Suspend");
            _logger.LogInformation("System suspending, session stopped and saved.");
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _isSuspended = false;
            CheckDayRollover();
            LogEvent("Resume");
            StartSession();
            WriteHeartbeat(GetTodaySeconds());
            _logger.LogInformation("System resumed, new session started.");
        }
    }

    private void CheckDayRollover()
    {
        lock (_lock)
        {
            var today = DateTime.Today;
            if (_todayDate != today)
            {
                _todayDate = today;
                _todaySeconds = LoadTodaySeconds(); // 新一天的初始值
                _logger.LogInformation("Day rolled over, loaded: {Seconds}s", _todaySeconds);
            }
        }
    }

    private void SaveTodaySeconds(long seconds)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DailyUsage(Date, Seconds) VALUES ($date, $seconds)
            ON CONFLICT(Date) DO UPDATE SET Seconds = $seconds
            """;
        cmd.Parameters.AddWithValue("$date", _todayDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$seconds", seconds);
        cmd.ExecuteNonQuery();

        try
        {
            WriteHeartbeat(conn, seconds, DateTime.Now, _todayDate);
        }
        catch (Exception ex)
        {
            // 心跳只是补边界用的辅助数据，写不进去不能影响 DailyUsage
            _logger.LogWarning(ex, "写入心跳失败");
        }
    }

    private void WriteHeartbeat(long seconds)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            WriteHeartbeat(conn, seconds, DateTime.Now, _todayDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入心跳失败");
        }
    }

    private static void WriteHeartbeat(
        SqliteConnection conn,
        long seconds,
        DateTime timestamp,
        DateTime date
    )
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ServiceHeartbeat (Id, Timestamp, Date, TotalSeconds)
            VALUES (1, $timestamp, $date, $seconds)
            ON CONFLICT(Id) DO UPDATE SET
                Timestamp = $timestamp,
                Date = $date,
                TotalSeconds = $seconds
            """;
        cmd.Parameters.AddWithValue("$timestamp", timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        cmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$seconds", seconds);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 上次进程没有走到 Dispose（断电 / 强杀 / 被杀）时，最后一条 SessionEvents 会停在
    /// ServiceStarted 或 Resume。用最后一次心跳补一条 ServiceStopped 把区间闭合；
    /// 补的时间是「最后一次确认还活着」，不会把停机空档算进去。
    /// </summary>
    private void BackfillStopEventIfNeeded()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            string? lastType;
            string? lastTimestamp;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT EventType, Timestamp FROM SessionEvents ORDER BY Id DESC LIMIT 1";
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return;
                lastType = reader.GetString(0);
                lastTimestamp = reader.GetString(1);
            }

            // Suspend / ServiceStopped 本身就是闭合事件，不需要补
            if (
                lastType is null
                || lastTimestamp is null
                || lastType is "Suspend" or "ServiceStopped"
            )
                return;

            string? heartbeatTimestamp;
            string? heartbeatDate;
            long heartbeatSeconds;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT Timestamp, Date, TotalSeconds FROM ServiceHeartbeat WHERE Id = 1";
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return;
                heartbeatTimestamp = reader.GetString(0);
                heartbeatDate = reader.GetString(1);
                heartbeatSeconds = reader.GetInt64(2);
            }

            // 心跳必须晚于最后一条「开始」事件；更早的心跳属于上一轮，不能拿来补
            if (string.CompareOrdinal(heartbeatTimestamp, lastTimestamp) <= 0)
                return;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO SessionEvents (EventType, Timestamp, Date, TotalSeconds)
                    VALUES ('ServiceStopped', $timestamp, $date, $seconds)
                    """;
                cmd.Parameters.AddWithValue("$timestamp", heartbeatTimestamp);
                cmd.Parameters.AddWithValue("$date", heartbeatDate);
                cmd.Parameters.AddWithValue("$seconds", heartbeatSeconds);
                cmd.ExecuteNonQuery();
            }

            _logger.LogWarning(
                "上次未正常收尾（最后事件 {LastType} @ {LastTimestamp}），已在心跳 {Heartbeat} 补写 ServiceStopped",
                lastType,
                lastTimestamp,
                heartbeatTimestamp
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "补写上次未收尾的 ServiceStopped 失败");
        }
    }

    private long LoadTodaySeconds()
    {
        if (!File.Exists(DbPath))
            return 0;
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Seconds FROM DailyUsage WHERE Date = $date";
        cmd.Parameters.AddWithValue("$date", DateTime.Today.ToString("yyyy-MM-dd"));
        var result = cmd.ExecuteScalar();
        return result is long sec ? sec : 0;
    }

    /// <summary>
    /// 向 SessionEvents 表插入一条事件记录
    /// </summary>
    /// <param name="eventType">事件类型，如 Start、Suspend、Resume、Stop</param>
    private void LogEvent(string eventType)
    {
        try
        {
            var now = DateTime.Now;
            var totalSeconds = GetTodaySeconds(); // 线程安全调用

            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SessionEvents (EventType, Timestamp, Date, TotalSeconds)
                VALUES ($type, $timestamp, $date, $seconds)
                """;
            cmd.Parameters.AddWithValue("$type", eventType);
            cmd.Parameters.AddWithValue("$timestamp", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("$date", now.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$seconds", totalSeconds);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // 记录事件失败不应影响主流程
            _logger.LogWarning(ex, "Failed to log event {EventType}", eventType);
        }
    }

    // ---- 后台主循环：专门处理命名管道请求 ----
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 确保数据库及表结构存在
        InitializeDatabase();

        // 先补上次没收尾的边界，再写本次的 ServiceStarted（顺序不能反）
        BackfillStopEventIfNeeded();

        // 如果需要，处理跨天
        CheckDayRollover();
        if (!_isRunning)
        {
            StartSession();
        }

        LogEvent("ServiceStarted");
        WriteHeartbeat(GetTodaySeconds());

        _logger.LogInformation(
            "Service started. Listening on named pipe with periodic save every {Interval}s.",
            SaveInterval.TotalSeconds
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                "UsageTrackerPipe",
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous
            );

            // 绑定一个可取消的 token，用于定时中断等待
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            var connectionTask = server.WaitForConnectionAsync(cts.Token);
            var delayTask = Task.Delay(SaveInterval, stoppingToken);

            // 谁先完成
            var completed = await Task.WhenAny(connectionTask, delayTask);

            if (completed == connectionTask)
            {
                // 管道连接成功
                try
                {
                    var seconds = GetTodaySeconds();
                    var data = Encoding.UTF8.GetBytes(seconds.ToString());
                    await server.WriteAsync(data, 0, data.Length, stoppingToken);
                    await server.FlushAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to respond to pipe client");
                }

                // 客户端轮询同样要把秒表里的时长落库，否则只要 UI 在轮询，
                // 7 秒定时保存就永远不会触发（2026-10-10 实测库里的当日值一直滞后）。
                SaveSessionAndRestart();
            }
            else
            {
                // 定时保存触发，取消未完成的连接等待
                cts.Cancel();
                try
                {
                    await connectionTask; // 如果没抛异常，说明连接成功！
                    // 补救：发送当前数据
                    var seconds = GetTodaySeconds();
                    var data = Encoding.UTF8.GetBytes(seconds.ToString());
                    await server.WriteAsync(data, 0, data.Length, stoppingToken);
                    await server.FlushAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // 连接确实被取消了，无需处理
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "补救回复失败");
                }

                // 正常执行定时保存
                SaveSessionAndRestart();
            }

            server.Dispose();
        }
    }

    /// <summary>把秒表里的时长落库，处理跨天，再按挂起状态决定是否重新计时。</summary>
    private void SaveSessionAndRestart()
    {
        StopSessionAndSave();
        CheckDayRollover();
        if (!_isSuspended)
        {
            StartSession();
        }
        else
        {
            _logger.LogDebug("Skipping session start because system is suspended.");
        }
    }

    public override void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopSessionAndSave();
        LogEvent("ServiceStopped");
        base.Dispose();
    }
}
