using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using YukkuriMovieMaker.Commons;

namespace taisan11.TaskManager;

internal sealed partial class TaskManagerViewModel
{
    private static readonly TimeSpan ActionWindow = TimeSpan.FromSeconds(2);
    private LoadRecorder? recorder;
    private RecordingResult? recording;

    public bool IsRecording
    {
        get;
        private set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(NormalVisibility));
                OnPropertyChanged(nameof(RecordingVisibility));
            }
        }
    }
    public Visibility NormalVisibility => IsRecording ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RecordingVisibility => IsRecording ? Visibility.Visible : Visibility.Collapsed;

    public ICommand StartRecordingCommand => field ??= new ActionCommand(_ => !IsRecording, _ => StartRecording());
    public ICommand StopRecordingCommand => field ??= new ActionCommand(_ => IsRecording, _ => StopRecording());
    public ICommand ExportRecordingCommand => field ??= new ActionCommand(_ => true, _ => ExportRecording());

    public string RecordingSummaryText { get; private set => Set(ref field, value); } = "まだ記録していません。";
    public IReadOnlyList<RecordedActionItem> RecordedActions { get; private set => Set(ref field, value); } = [];
    public RecordedActionItem? SelectedRecordedAction
    {
        get;
        set
        {
            if (Set(ref field, value))
                OnPropertyChanged(nameof(RecordingHighlight));
        }
    }
    public double RecordingHighlight => SelectedRecordedAction?.MarkerPosition ?? double.NaN;
    public IReadOnlyList<double> RecordingMarkers { get; private set => Set(ref field, value); } = [];
    public int RecordingCapacity { get; private set => Set(ref field, value); } = 2;
    public string RecordingGraphLabel { get; private set => Set(ref field, value); } = string.Empty;
    public IReadOnlyList<double> RecordingCpu { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<double> RecordingSystemCpu { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<double> RecordingMemory { get; private set => Set(ref field, value); } = [];
    public double RecordingMemoryMaximum { get; private set => Set(ref field, value); } = 1;
    public IReadOnlyList<double> RecordingGpu { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<double> RecordingLatency { get; private set => Set(ref field, value); } = [];
    public double RecordingLatencyMaximum { get; private set => Set(ref field, value); } = 100;

    private void StartRecording()
    {
        Stop();
        recorder = new LoadRecorder(Dispatcher.CurrentDispatcher);
        IsRecording = true;
        recorder.Start();
    }

    private void StopRecording()
    {
        EndRecording();
        Start();
    }

    public void EndRecording()
    {
        if (recorder is null)
            return;
        var result = recorder.Stop();
        recorder = null;
        IsRecording = false;
        ApplyRecording(result);
    }

    private void ApplyRecording(RecordingResult result)
    {
        recording = result;
        var samples = result.Samples;
        var first = samples.Count > 0 ? samples[0].Time : TimeSpan.Zero;
        var span = samples.Count > 1 ? (samples[^1].Time - first).TotalMilliseconds : 0;
        double Position(TimeSpan time) => span <= 0 ? 0 : Math.Clamp((time - first).TotalMilliseconds / span, 0, 1) * (samples.Count - 1);

        RecordedActions = [.. result.Actions.Select(action =>
        {
            var after = samples.Where(x => x.Time > action.Time && x.Time <= action.Time + ActionWindow + LoadRecorder.Interval).ToList();
            return new RecordedActionItem(
                action,
                after.Count == 0 ? 0 : after.Average(x => x.Cpu),
                after.Count == 0 ? 0 : after.Max(x => x.Cpu),
                after.Count == 0 ? 0 : after.Max(x => x.UiLatencyMs),
                Position(action.Time));
        })];
        SelectedRecordedAction = null;
        RecordingCapacity = Math.Max(2, samples.Count);
        RecordingGraphLabel = $"{RecordedActionItem.FormatTime(result.Duration)} / {samples.Count}サンプル";
        RecordingMarkers = [.. RecordedActions.Select(x => x.MarkerPosition)];
        RecordingCpu = [.. samples.Select(x => x.Cpu)];
        RecordingSystemCpu = [.. samples.Select(x => x.SystemCpu)];
        RecordingMemory = [.. samples.Select(x => (double)x.PrivateBytes)];
        RecordingMemoryMaximum = Math.Max(1, RecordingMemory.DefaultIfEmpty(0).Max() * 1.15);
        RecordingGpu = [.. samples.Select(x => x.Gpu)];
        RecordingLatency = [.. samples.Select(x => x.UiLatencyMs)];
        RecordingLatencyMaximum = Math.Max(100, RecordingLatency.DefaultIfEmpty(0).Max() * 1.15);

        if (samples.Count == 0)
        {
            RecordingSummaryText = $"記録時間 {RecordedActionItem.FormatTime(result.Duration)} (短すぎるため負荷を取得できませんでした)";
            return;
        }
        RecordingSummaryText = string.Create(CultureInfo.CurrentCulture,
            $"{result.StartedAt:HH:mm:ss} から {RecordedActionItem.FormatTime(result.Duration)}  操作 {result.Actions.Count} 件  " +
            $"CPU 平均 {samples.Average(x => x.Cpu):0.0} % / 最大 {samples.Max(x => x.Cpu):0.0} %  " +
            $"メモリ 最大 {ByteFormatter.Format(samples.Max(x => x.PrivateBytes))}  " +
            $"GPU 最大 {samples.Max(x => x.Gpu):0.0} %  UI遅延 最大 {samples.Max(x => x.UiLatencyMs):0} ms");
    }

    private void ExportRecording()
    {
        if (recording is not { } result)
        {
            MessageBox.Show("先に記録してください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"YMM4TaskManager_{result.StartedAt:yyyyMMdd_HHmmss}.csv",
        };
        if (dialog.ShowDialog() != true)
            return;
        var builder = new StringBuilder();
        builder.AppendLine("time_sec,cpu_percent,system_cpu_percent,private_bytes,managed_bytes,gpu_percent,gpu_dedicated_bytes,ui_latency_ms,actions");
        var previous = TimeSpan.MinValue;
        foreach (var s in result.Samples)
        {
            var labels = result.Actions.Where(a => a.Time > previous && a.Time <= s.Time).Select(a => $"{a.Kind}: {a.Target}");
            var actionText = string.Join(" / ", labels).Replace("\"", "\"\"", StringComparison.Ordinal);
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{s.Time.TotalSeconds:0.000},{s.Cpu:0.00},{s.SystemCpu:0.00},{s.PrivateBytes},{s.ManagedBytes},{s.Gpu:0.00},{s.GpuDedicated},{s.UiLatencyMs:0.0},\"{actionText}\""));
            previous = s.Time;
        }
        var rest = result.Actions.Where(a => a.Time > previous).Select(a => $"{a.Kind}: {a.Target}").ToList();
        if (rest.Count > 0)
        {
            var actionText = string.Join(" / ", rest).Replace("\"", "\"\"", StringComparison.Ordinal);
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{result.Duration.TotalSeconds:0.000},,,,,,,,\"{actionText}\""));
        }
        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
    }
}
