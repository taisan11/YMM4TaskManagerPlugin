using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;

namespace taisan11.TaskManager;

internal sealed partial class TaskManagerViewModel
{
    private readonly PluginCatalog pluginCatalog = new();
    private readonly PluginUsageMonitor pluginMonitor;
    private readonly GpuMonitor gpuMonitor = new();
    private readonly Queue<double> gpuHistory = new();
    private readonly Queue<double> systemGpuHistory = new();
    private Dictionary<PluginGroup, TimeSpan> lastPluginCpu = [];
    private long lastPluginTimestamp;
    private bool isGpuSampling;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public string PluginStatusText { get; private set => Set(ref field, value); } = "計測を開始しています…";
    public string HeapStatusText { get; private set => Set(ref field, value); } = string.Empty;
    public ICommand AnalyzeHeapCommand { get; }

    public IReadOnlyList<GpuAdapterUsage> GpuAdapters { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<GpuEngineUsage> GpuEngines { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<double> GpuHistory { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<double> SystemGpuHistory { get; private set => Set(ref field, value); } = [];
    public string GpuText { get; private set => Set(ref field, value); } = "-";
    public string SystemGpuText { get; private set => Set(ref field, value); } = "-";
    public string GpuDedicatedText { get; private set => Set(ref field, value); } = "-";
    public string GpuSharedText { get; private set => Set(ref field, value); } = "-";
    public string GpuStatusText { get; private set => Set(ref field, value); } = string.Empty;

    private void UpdatePlugins()
    {
        var snapshot = pluginMonitor.GetSnapshot();
        var now = Stopwatch.GetTimestamp();
        var elapsed = lastPluginTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(lastPluginTimestamp, now).TotalMilliseconds;
        var assemblies = pluginCatalog.CountLoadedAssemblies();

        var groups = pluginCatalog.GetInstalledPlugins()
            .Concat([PluginCatalog.Host, PluginCatalog.Runtime, PluginCatalog.Native])
            .Concat(snapshot.CpuTime.Keys)
            .Concat(snapshot.Heap.Keys)
            .Concat(assemblies.Keys)
            .Distinct();
        foreach (var group in groups)
        {
            var item = Plugins.FirstOrDefault(x => x.Group == group);
            if (item is null)
            {
                item = new PluginItemViewModel(group);
                var index = 0;
                while (index < Plugins.Count
                    && (Plugins[index].SortOrder < item.SortOrder
                        || (Plugins[index].SortOrder == item.SortOrder && string.Compare(Plugins[index].Name, item.Name, StringComparison.OrdinalIgnoreCase) < 0)))
                    index++;
                Plugins.Insert(index, item);
            }
            var cpu = snapshot.CpuTime.GetValueOrDefault(group);
            if (elapsed > 0)
                item.CpuUsage = Math.Clamp((cpu - lastPluginCpu.GetValueOrDefault(group)).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100, 0, 100);
            item.CpuTime = cpu;
            item.Threads = snapshot.Threads.GetValueOrDefault(group);
            item.Assemblies = assemblies.GetValueOrDefault(group);
            if (snapshot.Heap.TryGetValue(group, out var heap))
            {
                item.HeapBytes = heap.Bytes;
                item.Objects = heap.Objects;
            }
            else if (snapshot.HeapAnalyzedAt is not null)
            {
                item.HeapBytes = 0;
                item.Objects = 0;
            }
        }
        lastPluginCpu = new Dictionary<PluginGroup, TimeSpan>(snapshot.CpuTime);
        lastPluginTimestamp = now;

        PluginStatusText = snapshot.Error is { } error
            ? $"取得エラー: {error}"
            : $"スタック採取 {snapshot.SampleCost.TotalMilliseconds:0} ms / 0.5 秒";
        HeapStatusText = snapshot.HeapAnalyzedAt is { } at
            ? $"ヒープ解析 {at:HH:mm:ss} ({snapshot.HeapCost.TotalMilliseconds:0} ms, 15 秒ごと)"
            : "ヒープ解析中…";
    }

    private void UpdateGpu()
    {
        if (isGpuSampling)
            return;
        isGpuSampling = true;
        Task.Run(gpuMonitor.Sample).ContinueWith(
            task =>
            {
                isGpuSampling = false;
                if (task.IsCompletedSuccessfully)
                    ApplyGpu(task.Result);
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyGpu(GpuSnapshot snapshot)
    {
        if (snapshot.Error is { } error)
        {
            GpuStatusText = $"GPU情報を取得できませんでした: {error}";
            return;
        }
        Push(gpuHistory, snapshot.SelfUtilization);
        Push(systemGpuHistory, snapshot.SystemUtilization);
        GpuHistory = [.. gpuHistory];
        SystemGpuHistory = [.. systemGpuHistory];
        GpuText = $"{snapshot.SelfUtilization:0.0} %";
        SystemGpuText = $"{snapshot.SystemUtilization:0.0} %";
        GpuDedicatedText = ByteFormatter.Format(snapshot.SelfDedicated);
        GpuSharedText = ByteFormatter.Format(snapshot.SelfShared);
        GpuAdapters = snapshot.Adapters;
        GpuEngines = snapshot.Engines;
        GpuStatusText = string.Empty;
    }
}
