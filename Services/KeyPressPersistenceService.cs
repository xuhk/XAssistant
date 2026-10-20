using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

/// <summary>
/// Moves key-record persistence off the low-level keyboard hook thread.  The hook only
/// performs a non-blocking channel write; SQLite work is serialized on this worker.
/// </summary>
public sealed class KeyPressPersistenceService : IKeyPressPersistenceService, IDisposable
{
    private const int BatchSize = 64;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    private readonly IKeyDatabaseService _database;
    private readonly ILogger<KeyPressPersistenceService> _logger;
    private readonly Channel<QueueItem> _queue = Channel.CreateUnbounded<QueueItem>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false }
    );
    private readonly Task _workerTask;
    private int _acceptingRecords = 1;
    private int _pendingRecords;

    public KeyPressPersistenceService(
        IKeyDatabaseService database,
        ILogger<KeyPressPersistenceService> logger
    )
    {
        _database = database;
        _logger = logger;
        _workerTask = Task.Run(PersistLoopAsync);
    }

    public bool TryEnqueue(KeyPressRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (Volatile.Read(ref _acceptingRecords) == 0)
            return false;

        Interlocked.Increment(ref _pendingRecords);
        if (!_queue.Writer.TryWrite(new KeyPressItem(record)))
        {
            Interlocked.Decrement(ref _pendingRecords);
            return false;
        }

        return true;
    }

    public bool Flush(TimeSpan timeout)
    {
        if (Volatile.Read(ref _acceptingRecords) == 0)
            return WaitForWorker(timeout);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new FlushRequest(completion)))
            return false;

        try
        {
            if (completion.Task.Wait(timeout))
                return completion.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "冲刷按键记录时发生异常");
            return false;
        }

        _logger.LogWarning("按键记录在 {Timeout} 内未能完成冲刷；仍有 {PendingCount} 条待写入", timeout, Volatile.Read(ref _pendingRecords));
        return false;
    }

    public bool FlushAndStop(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _acceptingRecords, 0) != 0)
            _queue.Writer.TryComplete();

        return WaitForWorker(timeout);
    }

    private bool WaitForWorker(TimeSpan timeout)
    {
        try
        {
            if (_workerTask.Wait(timeout))
                return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "按键持久化工作线程异常结束");
            return false;
        }

        _logger.LogWarning("按键持久化工作线程在 {Timeout} 内未结束；仍有 {PendingCount} 条待写入", timeout, Volatile.Read(ref _pendingRecords));
        return false;
    }

    private async Task PersistLoopAsync()
    {
        var batch = new List<KeyPressRecord>(BatchSize);
        DateTime? batchStartedAt = null;

        try
        {
            while (true)
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    switch (item)
                    {
                        case KeyPressItem keyPress:
                            if (batch.Count == 0)
                                batchStartedAt = DateTime.UtcNow;

                            batch.Add(keyPress.Record);
                            if (batch.Count >= BatchSize)
                            {
                                await PersistBatchWithRetryAsync(batch).ConfigureAwait(false);
                                batchStartedAt = null;
                            }
                            break;

                        case FlushRequest flush:
                            await PersistBatchWithRetryAsync(batch).ConfigureAwait(false);
                            batchStartedAt = null;
                            flush.Completion.TrySetResult(true);
                            break;
                    }
                }

                if (_queue.Reader.Completion.IsCompleted)
                    break;

                if (batch.Count > 0)
                {
                    var elapsed = DateTime.UtcNow - batchStartedAt!.Value;
                    var remaining = BatchInterval - elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        await PersistBatchWithRetryAsync(batch).ConfigureAwait(false);
                        batchStartedAt = null;
                        continue;
                    }

                    var nextItem = _queue.Reader.WaitToReadAsync().AsTask();
                    if (await Task.WhenAny(nextItem, Task.Delay(remaining)).ConfigureAwait(false) != nextItem)
                    {
                        await PersistBatchWithRetryAsync(batch).ConfigureAwait(false);
                        batchStartedAt = null;
                        continue;
                    }

                    if (!await nextItem.ConfigureAwait(false))
                        break;
                }
                else if (!await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    break;
                }
            }

            // A completed channel may still have a partial in-memory batch.
            if (batch.Count > 0)
                await PersistBatchWithRetryAsync(batch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "按键持久化工作线程意外终止；仍有 {PendingCount} 条待写入", Volatile.Read(ref _pendingRecords));
            throw;
        }
    }

    private async Task PersistBatchWithRetryAsync(List<KeyPressRecord> batch)
    {
        if (batch.Count == 0)
            return;

        var recordCount = batch.Count;
        var attempt = 0;
        while (true)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                _database.SaveKeyPressBatch(batch);
                stopwatch.Stop();
                Interlocked.Add(ref _pendingRecords, -recordCount);
                _logger.LogDebug("已批量持久化 {RecordCount} 条按键记录，耗时 {ElapsedMilliseconds} ms", recordCount, stopwatch.ElapsedMilliseconds);
                batch.Clear();
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                var retryDelay = attempt <= 3
                    ? TimeSpan.FromMilliseconds(100 * attempt)
                    : TimeSpan.FromSeconds(5);
                _logger.LogError(ex, "批量写入 {RecordCount} 条按键记录失败（第 {Attempt} 次），将在 {RetryDelay} 后重试", recordCount, attempt, retryDelay);
                await Task.Delay(retryDelay).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => FlushAndStop(TimeSpan.FromSeconds(3));

    private abstract record QueueItem;
    private sealed record KeyPressItem(KeyPressRecord Record) : QueueItem;
    private sealed record FlushRequest(TaskCompletionSource<bool> Completion) : QueueItem;
}
