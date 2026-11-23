using YukkuriMovieMaker.Commons;

namespace taisan11.TaskManager;

internal sealed class PluginItemViewModel(PluginGroup group) : Bindable
{
    public PluginGroup Group { get; } = group;
    public string Name => Group.Name;
    public string Kind => Group.Kind switch
    {
        PluginGroupKind.Plugin => "プラグイン",
        PluginGroupKind.Host => "本体",
        PluginGroupKind.Runtime => "ランタイム",
        _ => "その他",
    };
    public int SortOrder => (int)Group.Kind;

    public double CpuUsage { get; set => Set(ref field, value, nameof(CpuUsage), nameof(CpuUsageText)); }
    public string CpuUsageText => $"{CpuUsage:0.0} %";
    public TimeSpan CpuTime { get; set => Set(ref field, value, nameof(CpuTime), nameof(CpuTimeText)); }
    public string CpuTimeText => $"{(int)CpuTime.TotalMinutes}:{CpuTime.Seconds:00}.{CpuTime.Milliseconds / 100}";
    public long HeapBytes { get; set => Set(ref field, value, nameof(HeapBytes), nameof(HeapText)); }
    public string HeapText => ByteFormatter.Format(HeapBytes);
    public long Objects { get; set => Set(ref field, value); }
    public int Threads { get; set => Set(ref field, value); }
    public int Assemblies { get; set => Set(ref field, value); }
}
