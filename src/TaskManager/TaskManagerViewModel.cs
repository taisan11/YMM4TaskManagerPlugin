using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;

namespace taisan11.TaskManager;

internal sealed partial class TaskManagerViewModel : Bindable, IToolViewModel
{
    private const int HistoryCapacity = 60;

    private readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    private readonly Queue<double> cpuHistory = new();
    private readonly Queue<double> memoryHistory = new();
    private readonly Queue<double> systemCpuHistory = new();
    private readonly int selfId = Environment.ProcessId;
    private readonly DateTime selfStartTime;
    private (ulong Idle, ulong Busy)? lastSystemTimes;

    public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested { add { } remove { } }

    public string Title => "タスクマネージャー";

    public ObservableCollection<ProcessItemViewModel> Processes { get; } = [];
    public ProcessItemViewModel? SelectedProcess { get; set => Set(ref field, value); }

    public IReadOnlyList<double> CpuHistory { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<double> SystemCpuHistory { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<double> MemoryHistory { get; private set => Set(ref field, value); } = [];
    public double MemoryGraphMaximum { get; private set => Set(ref field, value); } = 1;
    public int GraphCapacity => HistoryCapacity;

    public string CpuText { get; private set => Set(ref field, value); } = "-";
    public string SystemCpuText { get; private set => Set(ref field, value); } = "-";
    public string MemoryText { get; private set => Set(ref field, value); } = "-";
    public string SystemMemoryText { get; private set => Set(ref field, value); } = "-";
    public string ManagedHeapText { get; private set => Set(ref field, value); } = "-";
    public string GcCountText { get; private set => Set(ref field, value); } = "-";
    public string ThreadText { get; private set => Set(ref field, value); } = "-";
    public string HandleText { get; private set => Set(ref field, value); } = "-";
    public string AssemblyText { get; private set => Set(ref field, value); } = "-";
    public string UptimeText { get; private set => Set(ref field, value); } = "-";
    public string ChildProcessText { get; private set => Set(ref field, value); } = "-";
    public string LastUpdatedText { get; private set => Set(ref field, value); } = string.Empty;

    public IReadOnlyList<double> IntervalOptions { get; } = [0.5, 1, 2, 5];
    public double IntervalSeconds
    {
        get;
        set
        {
            if (Set(ref field, value))
                timer.Interval = TimeSpan.FromSeconds(value);
        }
    } = 1;

    public bool IsPaused
    {
        get;
        set
        {
            if (Set(ref field, value))
                OnPropertyChanged(nameof(PauseButtonText));
        }
    }
    public string PauseButtonText => IsPaused ? "▶ 再開" : "❚❚ 一時停止";

    public ICommand TogglePauseCommand { get; }
    public ICommand CollectGarbageCommand { get; }
    public ICommand KillProcessCommand { get; }
    public ICommand RefreshCommand { get; }

    public TaskManagerViewModel()
    {
        using (var self = Process.GetCurrentProcess())
            selfStartTime = self.StartTime;

        timer.Interval = TimeSpan.FromSeconds(IntervalSeconds);
        timer.Tick += (_, _) =>
        {
            if (!IsPaused)
                Sample();
        };

        TogglePauseCommand = new ActionCommand(_ => true, _ => IsPaused = !IsPaused);
        RefreshCommand = new ActionCommand(_ => true, _ => Sample());
        CollectGarbageCommand = new ActionCommand(
            _ => true,
            _ =>
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Sample();
            });
        KillProcessCommand = new ActionCommand(
            x => x is ProcessItemViewModel { CanKill: true },
            x =>
            {
                if (x is not ProcessItemViewModel { CanKill: true } item)
                    return;
                var result = MessageBox.Show(
                    $"{item.Name} (PID {item.Id}) を終了しますか？\r\n子プロセスも合わせて終了します。",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                    return;
                try
                {
                    item.Kill();
                }
                catch (Exception ex) when (ProcessItemViewModel.IsProcessAccessException(ex))
                {
                    MessageBox.Show($"プロセスを終了できませんでした。\r\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                Sample();
            });
    }

    public void Start()
    {
        if (timer.IsEnabled)
            return;
        Sample();
        timer.Start();
    }

    public void Stop() => timer.Stop();

    private void Sample()
    {
        UpdateProcessList();
        foreach (var item in Processes)
            item.Update();

        var self = Processes.FirstOrDefault(x => x.IsSelf);
        if (self is not null)
        {
            Push(cpuHistory, self.CpuUsage);
            Push(memoryHistory, self.PrivateBytes);
            CpuHistory = [.. cpuHistory];
            MemoryHistory = [.. memoryHistory];
            CpuText = self.CpuUsageText;
            MemoryText = $"{self.PrivateBytesText} (ワーキングセット {self.WorkingSetText})";
            ThreadText = self.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture);
            HandleText = self.Handles.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var (total, available, load) = NativeMethods.GetMemoryStatus();
        if (total > 0)
        {
            MemoryGraphMaximum = Math.Max(total * 0.25, memoryHistory.DefaultIfEmpty(0).Max() * 1.25);
            SystemMemoryText = $"{ByteFormatter.Format(total - available)} / {ByteFormatter.Format(total)} ({load} %)";
        }

        var systemTimes = NativeMethods.GetSystemCpuTimes();
        if (systemTimes is { } now && lastSystemTimes is { } last)
        {
            var idle = now.Idle - last.Idle;
            var busy = now.Busy - last.Busy;
            var usage = idle + busy == 0 ? 0 : busy * 100.0 / (idle + busy);
            Push(systemCpuHistory, usage);
            SystemCpuHistory = [.. systemCpuHistory];
            SystemCpuText = $"{usage:0.0} %";
        }
        lastSystemTimes = systemTimes;

        var gcInfo = GC.GetGCMemoryInfo();
        ManagedHeapText = $"{ByteFormatter.Format(GC.GetTotalMemory(false))} (ヒープ {ByteFormatter.Format(gcInfo.HeapSizeBytes)})";
        GcCountText = $"Gen0 {GC.CollectionCount(0)} / Gen1 {GC.CollectionCount(1)} / Gen2 {GC.CollectionCount(2)}";
        AssemblyText = AppDomain.CurrentDomain.GetAssemblies().Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var uptime = DateTime.Now - selfStartTime;
        UptimeText = $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        ChildProcessText = Math.Max(0, Processes.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        LastUpdatedText = $"最終更新 {DateTime.Now:HH:mm:ss}";
    }

    private void UpdateProcessList()
    {
        var table = NativeMethods.GetProcessTable();
        var tree = new List<(int Id, string Name, int Depth)>();
        var visited = new HashSet<int>();
        void Walk(int pid, string name, int depth)
        {
            if (!visited.Add(pid))
                return;
            tree.Add((pid, name, depth));
            foreach (var (childId, (parentId, exe)) in table.OrderBy(x => x.Key))
            {
                if (parentId == pid && childId != pid)
                    Walk(childId, exe, depth + 1);
            }
        }
        Walk(selfId, table.TryGetValue(selfId, out var selfEntry) ? selfEntry.ExeFile : "YukkuriMovieMaker.exe", 0);

        var existing = Processes.ToDictionary(x => x.Id);
        var next = new List<ProcessItemViewModel>();
        foreach (var (id, name, depth) in tree)
        {
            if (existing.Remove(id, out var item) && !item.HasExited && item.Depth == depth)
            {
                next.Add(item);
                continue;
            }
            item?.Dispose();
            try
            {
                var process = Process.GetProcessById(id);
                if (id != selfId && process.StartTime < selfStartTime)
                {
                    process.Dispose();
                    continue;
                }
                next.Add(new ProcessItemViewModel(process, name, id == selfId, depth));
            }
            catch (Exception ex) when (ProcessItemViewModel.IsProcessAccessException(ex))
            {
            }
        }
        foreach (var removed in existing.Values)
            removed.Dispose();

        for (var i = 0; i < next.Count; i++)
        {
            if (i < Processes.Count && ReferenceEquals(Processes[i], next[i]))
                continue;
            var oldIndex = Processes.IndexOf(next[i]);
            if (oldIndex >= 0)
                Processes.Move(oldIndex, i);
            else
                Processes.Insert(i, next[i]);
        }
        while (Processes.Count > next.Count)
            Processes.RemoveAt(Processes.Count - 1);
    }

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > HistoryCapacity)
            queue.Dequeue();
    }
}
