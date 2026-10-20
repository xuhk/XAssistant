using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services.Interfaces;
using WpfApplication = System.Windows.Application;

namespace XAssistant.ViewModels;

public partial class KeyCounterViewModel : ViewModelBase
{
    private readonly IKeyboardHookService _hookService;
    private readonly IKeyDatabaseService _dbService;
    private readonly IKeyPressPersistenceService _persistenceService;
    private readonly IConfigurationService _configService;
    private readonly ConcurrentDictionary<string, int> _pendingUiCounts = new(StringComparer.Ordinal);
    private int _uiUpdateScheduled;
    private DateTime _displayDate = DateTime.Today;

    // 总计
    public ObservableCollection<KeyCountItem> KeyCounts { get; } = new();

    // 今天
    public ObservableCollection<KeyCountItem> TodayKeyCounts { get; } = new();

    // 昨天
    public ObservableCollection<KeyCountItem> YesterdayKeyCounts { get; } = new();

    // 前天
    public ObservableCollection<KeyCountItem> DayBeforeYesterdayKeyCounts { get; } = new();

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private int _selectedTabIndex;

    // ===== 新增：首页用聚合属性 =====
    public int KeyTodayPresses => TodayKeyCounts.Sum(item => item.Count);
    public int KeyTotalPresses => KeyCounts.Sum(item => item.Count);

    public string KeyRecordingStatus => IsRecording ? "记录中" : "已停止";
    public System.Windows.Media.Brush KeyRecordingColor =>
        IsRecording
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)) // 绿色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)); // 灰色

    // =============================

    public KeyCounterViewModel(
        IKeyboardHookService hookService,
        IKeyDatabaseService dbService,
        IKeyPressPersistenceService persistenceService,
        IConfigurationService configService
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _persistenceService = persistenceService;
        _configService = configService;

        _hookService.KeyPressed += OnKeyPressed;

        LoadAllCounts();

        if (_configService.GetKeyRecordingAutoStart())
        {
            StartRecording();
        }
    }

    // IsRecording 变化时通知状态属性
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(KeyRecordingStatus));
        OnPropertyChanged(nameof(KeyRecordingColor));
    }

    private void OnKeyPressed(string key)
    {
        var record = new Models.KeyPressRecord { Key = key, PressTime = DateTime.Now };

        // This must stay non-blocking: OnKeyPressed runs inside the low-level keyboard hook.
        _persistenceService.TryEnqueue(record);
        _pendingUiCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
        ScheduleUiUpdate();
    }

    private void ScheduleUiUpdate()
    {
        if (Interlocked.CompareExchange(ref _uiUpdateScheduled, 1, 0) != 0)
            return;

        _ = Task.Delay(TimeSpan.FromMilliseconds(50)).ContinueWith(
            _ => WpfApplication.Current.Dispatcher.InvokeAsync(FlushPendingUiUpdates),
            TaskScheduler.Default
        );
    }

    private void FlushPendingUiUpdates()
    {
        try
        {
            if (_displayDate != DateTime.Today)
            {
                _displayDate = DateTime.Today;
                ReplaceCollection(DayBeforeYesterdayKeyCounts, YesterdayKeyCounts);
                ReplaceCollection(YesterdayKeyCounts, TodayKeyCounts);
                TodayKeyCounts.Clear();
            }

            foreach (var entry in _pendingUiCounts.ToArray())
            {
                if (!_pendingUiCounts.TryRemove(entry.Key, out var increment))
                    continue;

                UpdateCollection(KeyCounts, entry.Key, increment);
                UpdateCollection(TodayKeyCounts, entry.Key, increment);
            }

            OnPropertyChanged(nameof(KeyTodayPresses));
            OnPropertyChanged(nameof(KeyTotalPresses));
        }
        finally
        {
            Interlocked.Exchange(ref _uiUpdateScheduled, 0);
            if (!_pendingUiCounts.IsEmpty)
                ScheduleUiUpdate();
        }
    }

    private static void ReplaceCollection(
        ObservableCollection<KeyCountItem> target,
        IEnumerable<KeyCountItem> source
    )
    {
        var snapshot = source
            .Select(item => new KeyCountItem { Key = item.Key, Count = item.Count })
            .ToList();
        target.Clear();
        foreach (var item in snapshot)
        {
            target.Add(item);
        }
    }

    private void UpdateCollection(ObservableCollection<KeyCountItem> collection, string key, int increment = 1)
    {
        var item = collection.FirstOrDefault(x => x.Key == key);
        if (item != null)
            item.Count += increment;
        else
            collection.Add(new KeyCountItem { Key = key, Count = increment });
    }

    private void LoadAllCounts()
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            // 总计
            var totalDict = _dbService.GetKeyCounts();
            KeyCounts.Clear();
            foreach (var kv in totalDict.OrderByDescending(x => x.Value))
                KeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

            // 今天
            var todayDict = _dbService.GetKeyCounts(DateTime.Today, DateTime.Today.AddDays(1));
            TodayKeyCounts.Clear();
            foreach (var kv in todayDict.OrderByDescending(x => x.Value))
                TodayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

            // 昨天
            var yesterdayDict = _dbService.GetKeyCounts(DateTime.Today.AddDays(-1), DateTime.Today);
            YesterdayKeyCounts.Clear();
            foreach (var kv in yesterdayDict.OrderByDescending(x => x.Value))
                YesterdayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

            // 前天
            var dayBeforeDict = _dbService.GetKeyCounts(
                DateTime.Today.AddDays(-2),
                DateTime.Today.AddDays(-1)
            );
            DayBeforeYesterdayKeyCounts.Clear();
            foreach (var kv in dayBeforeDict.OrderByDescending(x => x.Value))
                DayBeforeYesterdayKeyCounts.Add(
                    new KeyCountItem { Key = kv.Key, Count = kv.Value }
                );

            // 通知聚合属性更新
            OnPropertyChanged(nameof(KeyTodayPresses));
            OnPropertyChanged(nameof(KeyTotalPresses));
        });
    }

    [RelayCommand]
    private void StartRecording()
    {
        _hookService.Start();
        IsRecording = true;
        _configService.SetKeyRecordingAutoStart(true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        _hookService.Stop();
        _persistenceService.Flush(TimeSpan.FromSeconds(3));
        IsRecording = false;
        _configService.SetKeyRecordingAutoStart(false);
    }

    [RelayCommand]
    private void RefreshData()
    {
        _displayDate = DateTime.Today;
        LoadAllCounts();
    }
}

// 辅助类，用于绑定
public partial class KeyCountItem : ObservableObject
{
    private int _count;
    public string Key { get; set; } = string.Empty;

    public int Length => Key?.Length ?? 0; // 用于排序

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}
