using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

namespace taisan11.TaskManager;

internal sealed record PluginHeapUsage(long Bytes, long Objects);

internal sealed record PluginUsageSnapshot(
    IReadOnlyDictionary<PluginGroup, TimeSpan> CpuTime,
    IReadOnlyDictionary<PluginGroup, int> Threads,
    IReadOnlyDictionary<PluginGroup, PluginHeapUsage> Heap,
    DateTime? HeapAnalyzedAt,
    TimeSpan SampleCost,
    TimeSpan HeapCost,
    string? Error);

/// <summary>
/// 自プロセスのスナップショットを ClrMD で解析し、スレッドのスタックとヒープ上の型をプラグインごとに集計する。
/// </summary>
internal sealed class PluginUsageMonitor(PluginCatalog catalog) : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HeapInterval = TimeSpan.FromSeconds(15);

    private readonly object gate = new();
    private readonly int processId = Environment.ProcessId;
    private readonly Dictionary<PluginGroup, TimeSpan> cpuTotals = [];
    private Dictionary<int, TimeSpan> lastThreadTimes = [];
    private Dictionary<PluginGroup, int> threads = [];
    private Dictionary<PluginGroup, PluginHeapUsage> heap = [];
    private DateTime? heapAnalyzedAt;
    private DateTime nextHeapAnalysis = DateTime.MinValue;
    private TimeSpan sampleCost;
    private TimeSpan heapCost;
    private string? error;
    private CancellationTokenSource? cts;
    private Thread? worker;

    public bool IsPaused { get; set; }

    public void Start()
    {
        if (worker is not null)
            return;
        var source = new CancellationTokenSource();
        cts = source;
        worker = new Thread(() => Run(source))
        {
            IsBackground = true,
            Name = "YMM4TaskManagerPlugin sampler",
            Priority = ThreadPriority.BelowNormal,
        };
        worker.Start();
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
        worker = null;
        lock (gate)
            lastThreadTimes = [];
    }

    public void RequestHeapAnalysis() => nextHeapAnalysis = DateTime.MinValue;

    public PluginUsageSnapshot GetSnapshot()
    {
        lock (gate)
            return new PluginUsageSnapshot(
                new Dictionary<PluginGroup, TimeSpan>(cpuTotals),
                threads, heap, heapAnalyzedAt, sampleCost, heapCost, error);
    }

    private void Run(CancellationTokenSource source)
    {
        using var _ = source;
        var token = source.Token;
        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            if (!IsPaused)
            {
                try
                {
                    SampleOnce();
                }
                catch (Exception ex)
                {
                    lock (gate)
                        error = ex.Message;
                }
            }
            var wait = SampleInterval - Stopwatch.GetElapsedTime(started);
            token.WaitHandle.WaitOne(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50));
        }
    }

    private void SampleOnce()
    {
        var sampleStarted = Stopwatch.GetTimestamp();
        var threadTimes = ReadThreadTimes();

        using var dataTarget = DataTarget.CreateSnapshotAndAttach(processId);
        var clr = dataTarget.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException(".NET ランタイムが見つかりませんでした");
        using var runtime = clr.CreateRuntime();

        var owners = new Dictionary<int, PluginGroup>();
        foreach (var thread in runtime.Threads)
        {
            if (thread.IsAlive && thread.OSThreadId != 0)
                owners[(int)thread.OSThreadId] = Attribute(thread);
        }

        var analyzeHeap = DateTime.UtcNow >= nextHeapAnalysis;
        Dictionary<PluginGroup, PluginHeapUsage>? heapResult = null;
        var heapStarted = Stopwatch.GetTimestamp();
        if (analyzeHeap && runtime.Heap.CanWalkHeap)
            heapResult = AnalyzeHeap(runtime.Heap);
        var heapElapsed = Stopwatch.GetElapsedTime(heapStarted);

        var threadCounts = new Dictionary<PluginGroup, int>();
        lock (gate)
        {
            var first = lastThreadTimes.Count == 0;
            foreach (var (id, time) in threadTimes)
            {
                var owner = owners.TryGetValue(id, out var o) ? o : PluginCatalog.Native;
                threadCounts[owner] = threadCounts.GetValueOrDefault(owner) + 1;
                if (first)
                    continue;
                var delta = time - lastThreadTimes.GetValueOrDefault(id);
                if (delta > TimeSpan.Zero)
                    cpuTotals[owner] = cpuTotals.GetValueOrDefault(owner) + delta;
            }
            lastThreadTimes = threadTimes;
            threads = threadCounts;
            if (heapResult is not null)
            {
                heap = heapResult;
                heapAnalyzedAt = DateTime.Now;
                heapCost = heapElapsed;
            }
            sampleCost = Stopwatch.GetElapsedTime(sampleStarted) - (heapResult is null ? TimeSpan.Zero : heapElapsed);
            error = null;
        }
        if (analyzeHeap)
            nextHeapAnalysis = DateTime.UtcNow + HeapInterval;
    }

    private PluginGroup Attribute(ClrThread thread)
    {
        PluginGroup? host = null;
        PluginGroup? other = null;
        foreach (var frame in thread.EnumerateStackTrace(includeContext: false, maxFrames: 256))
        {
            var module = frame.Method?.Type?.Module;
            if (module is null)
                continue;
            var group = catalog.Classify(module.Name);
            switch (group.Kind)
            {
                case PluginGroupKind.Plugin:
                    return group;
                case PluginGroupKind.Host:
                    host ??= group;
                    break;
                default:
                    other ??= group;
                    break;
            }
        }
        return host ?? other ?? PluginCatalog.Native;
    }

    private Dictionary<PluginGroup, PluginHeapUsage> AnalyzeHeap(ClrHeap clrHeap)
    {
        var typeGroups = new Dictionary<ulong, PluginGroup>();
        var bytes = new Dictionary<PluginGroup, long>();
        var counts = new Dictionary<PluginGroup, long>();
        foreach (var obj in clrHeap.EnumerateObjects())
        {
            var type = obj.Type;
            if (type is null)
                continue;
            if (!typeGroups.TryGetValue(type.MethodTable, out var group))
            {
                var owner = type.IsArray ? type.ComponentType ?? type : type;
                group = catalog.Classify(owner.Module?.Name);
                typeGroups[type.MethodTable] = group;
            }
            bytes[group] = bytes.GetValueOrDefault(group) + (long)obj.Size;
            counts[group] = counts.GetValueOrDefault(group) + 1;
        }
        return bytes.ToDictionary(x => x.Key, x => new PluginHeapUsage(x.Value, counts[x.Key]));
    }

    private static Dictionary<int, TimeSpan> ReadThreadTimes()
    {
        var result = new Dictionary<int, TimeSpan>();
        using var process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            try
            {
                result[thread.Id] = thread.TotalProcessorTime;
            }
            catch (Exception ex) when (ProcessItemViewModel.IsProcessAccessException(ex))
            {
            }
            finally
            {
                thread.Dispose();
            }
        }
        return result;
    }

    public void Dispose() => Stop();
}
