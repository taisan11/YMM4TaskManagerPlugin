using System.Diagnostics;
using YukkuriMovieMaker.Commons;

namespace taisan11.TaskManager;

internal sealed class ProcessItemViewModel : Bindable, IDisposable
{
    private readonly Process process;
    private TimeSpan lastCpuTime;
    private DateTime lastSampleTime;
    private readonly DateTime? startTime;

    public int Id { get; }
    public string Name { get; }
    public bool IsSelf { get; }
    public bool CanKill => !IsSelf;
    public int Depth { get; }
    public string DisplayName => (Depth > 0 ? new string(' ', (Depth - 1) * 3) + "└ " : string.Empty) + Name;
    public string Kind => IsSelf ? "YMM4本体" : "子プロセス";

    public double CpuUsage { get; private set => Set(ref field, value, nameof(CpuUsage), nameof(CpuUsageText)); }
    public string CpuUsageText => $"{CpuUsage:0.0} %";
    public long WorkingSet { get; private set => Set(ref field, value, nameof(WorkingSet), nameof(WorkingSetText)); }
    public string WorkingSetText => ByteFormatter.Format(WorkingSet);
    public long PrivateBytes { get; private set => Set(ref field, value, nameof(PrivateBytes), nameof(PrivateBytesText)); }
    public string PrivateBytesText => ByteFormatter.Format(PrivateBytes);
    public int Threads { get; private set => Set(ref field, value); }
    public int Handles { get; private set => Set(ref field, value); }
    public bool HasExited { get; private set; }

    public ProcessItemViewModel(Process process, string name, bool isSelf, int depth)
    {
        this.process = process;
        Id = process.Id;
        Name = name;
        IsSelf = isSelf;
        Depth = depth;
        lastCpuTime = TryGetCpuTime() ?? TimeSpan.Zero;
        lastSampleTime = DateTime.UtcNow;
        try
        {
            startTime = process.StartTime;
        }
        catch (Exception ex) when (IsProcessAccessException(ex))
        {
        }
    }

    public void Update()
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                HasExited = true;
                return;
            }
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            var elapsed = (now - lastSampleTime).TotalMilliseconds;
            if (elapsed > 0)
                CpuUsage = Math.Clamp((cpu - lastCpuTime).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100, 0, 100);
            lastCpuTime = cpu;
            lastSampleTime = now;

            WorkingSet = process.WorkingSet64;
            PrivateBytes = process.PrivateMemorySize64;
            Threads = process.Threads.Count;
            Handles = process.HandleCount;
        }
        catch (Exception ex) when (IsProcessAccessException(ex))
        {
            HasExited = true;
        }
    }

    public void Kill()
    {
        using var current = Process.GetProcessById(Id);
        if (startTime is null || current.StartTime != startTime)
            throw new InvalidOperationException("プロセスは既に終了しています。");
        process.Kill(entireProcessTree: true);
    }

    public void Dispose() => process.Dispose();

    private TimeSpan? TryGetCpuTime()
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch (Exception ex) when (IsProcessAccessException(ex))
        {
            return null;
        }
    }

    internal static bool IsProcessAccessException(Exception ex)
        => ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException;
}
