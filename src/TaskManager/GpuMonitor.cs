using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Vortice.DXGI;

namespace taisan11.TaskManager;

internal sealed record GpuEngineUsage(string Adapter, string EngineType, double Self, double System)
{
    public string SelfText => $"{Self:0.0} %";
    public string SystemText => $"{System:0.0} %";
}

internal sealed record GpuAdapterUsage(string Name, ulong DedicatedTotal, ulong DedicatedUsed, ulong SharedUsed, ulong SelfDedicated, ulong SelfShared, double Utilization)
{
    public string UtilizationText => $"{Utilization:0.0} %";
    public string DedicatedText => DedicatedTotal > 0 ? $"{ByteFormatter.Format(DedicatedUsed)} / {ByteFormatter.Format(DedicatedTotal)}" : ByteFormatter.Format(DedicatedUsed);
    public string SharedText => ByteFormatter.Format(SharedUsed);
    public string SelfDedicatedText => ByteFormatter.Format(SelfDedicated);
    public string SelfSharedText => ByteFormatter.Format(SelfShared);
}

internal sealed record GpuSnapshot(
    double SelfUtilization,
    double SystemUtilization,
    ulong SelfDedicated,
    ulong SelfShared,
    IReadOnlyList<GpuAdapterUsage> Adapters,
    IReadOnlyList<GpuEngineUsage> Engines,
    string? Error);

/// <summary>
/// Windows の GPU パフォーマンスカウンター (タスクマネージャーと同じ情報源) と DXGI からGPUの使用状況を取得する。
/// </summary>
internal sealed partial class GpuMonitor
{
    private readonly string selfPrefix = $"pid_{Environment.ProcessId}_";
    private Dictionary<string, CounterSample> lastEngineSamples = [];
    private Dictionary<string, (string Name, ulong Dedicated)>? adapters;

    [GeneratedRegex(@"^pid_(\d+)_(luid_0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.*)$")]
    private static partial Regex EngineInstanceRegex();

    [GeneratedRegex(@"(luid_0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)")]
    private static partial Regex LuidRegex();

    public GpuSnapshot Sample()
    {
        try
        {
            adapters ??= LoadAdapters();
            return SampleCore();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or FormatException)
        {
            return new GpuSnapshot(0, 0, 0, 0, [], [], ex.Message);
        }
    }

    private GpuSnapshot SampleCore()
    {
        var engineData = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
        var current = new Dictionary<string, CounterSample>();
        var selfPerEngine = new Dictionary<(string Luid, string Engine, string Type), double>();
        var systemPerEngine = new Dictionary<(string Luid, string Engine, string Type), double>();
        if (engineData is not null)
        {
            foreach (InstanceData instance in engineData.Values)
            {
                var name = instance.InstanceName;
                current[name] = instance.Sample;
                if (!lastEngineSamples.TryGetValue(name, out var previous))
                    continue;
                var match = EngineInstanceRegex().Match(name);
                if (!match.Success)
                    continue;
                var value = CounterSample.Calculate(previous, instance.Sample);
                if (!double.IsFinite(value) || value <= 0)
                    continue;
                var type = match.Groups[5].Value;
                var key = (match.Groups[2].Value, match.Groups[3].Value + "_" + match.Groups[4].Value, string.IsNullOrEmpty(type) ? "Other" : type);
                systemPerEngine[key] = systemPerEngine.GetValueOrDefault(key) + value;
                if (name.StartsWith(selfPrefix, StringComparison.Ordinal))
                    selfPerEngine[key] = selfPerEngine.GetValueOrDefault(key) + value;
            }
        }
        lastEngineSamples = current;

        var engines = systemPerEngine.Keys
            .GroupBy(x => (x.Luid, x.Type))
            .Select(g => new GpuEngineUsage(
                AdapterName(g.Key.Luid),
                g.Key.Type,
                Math.Min(100, g.Max(k => selfPerEngine.GetValueOrDefault(k))),
                Math.Min(100, g.Max(k => systemPerEngine[k]))))
            .OrderByDescending(x => x.System)
            .ToList();

        var selfMemory = ReadMemory("GPU Process Memory", selfPrefix);
        var adapterMemory = ReadMemory("GPU Adapter Memory", null);
        var adapterList = adapters!
            .Select(a =>
            {
                var used = adapterMemory.GetValueOrDefault(a.Key);
                var self = selfMemory.GetValueOrDefault(a.Key);
                var utilization = systemPerEngine.Where(x => x.Key.Luid == a.Key).Select(x => x.Value).DefaultIfEmpty(0).Max();
                return new GpuAdapterUsage(a.Value.Name, a.Value.Dedicated, used.Dedicated, used.Shared, self.Dedicated, self.Shared, Math.Min(100, utilization));
            })
            .ToList();

        return new GpuSnapshot(
            Math.Min(100, selfPerEngine.Values.DefaultIfEmpty(0).Max()),
            Math.Min(100, systemPerEngine.Values.DefaultIfEmpty(0).Max()),
            (ulong)selfMemory.Values.Sum(x => (decimal)x.Dedicated),
            (ulong)selfMemory.Values.Sum(x => (decimal)x.Shared),
            adapterList,
            engines,
            null);
    }

    private static Dictionary<string, (ulong Dedicated, ulong Shared)> ReadMemory(string category, string? prefix)
    {
        var result = new Dictionary<string, (ulong Dedicated, ulong Shared)>();
        var data = new PerformanceCounterCategory(category).ReadCategory();
        var dedicated = data["Dedicated Usage"];
        var shared = data["Shared Usage"];
        if (dedicated is null || shared is null)
            return result;
        foreach (InstanceData instance in dedicated.Values)
        {
            var name = instance.InstanceName;
            if (prefix is not null && !name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var luid = LuidRegex().Match(name);
            if (!luid.Success)
                continue;
            var sharedValue = shared[name]?.RawValue ?? 0;
            var current = result.GetValueOrDefault(luid.Value);
            result[luid.Value] = (current.Dedicated + (ulong)Math.Max(0, instance.RawValue), current.Shared + (ulong)Math.Max(0, sharedValue));
        }
        return result;
    }

    private string AdapterName(string luid)
        => adapters is not null && adapters.TryGetValue(luid, out var adapter) ? adapter.Name : luid;

    private static Dictionary<string, (string Name, ulong Dedicated)> LoadAdapters()
    {
        var result = new Dictionary<string, (string Name, ulong Dedicated)>(StringComparer.OrdinalIgnoreCase);
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (var i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                var luid = string.Create(CultureInfo.InvariantCulture, $"luid_0x{desc.Luid.HighPart:X8}_0x{desc.Luid.LowPart:X8}");
                result.TryAdd(luid, (desc.Description, (ulong)(long)desc.DedicatedVideoMemory));
            }
        }
        return result;
    }
}
