namespace taisan11.TaskManager;

internal sealed record RecordedActionItem(RecordedAction Action, double AfterCpuAverage, double AfterCpuMax, double AfterLatencyMax, double MarkerPosition)
{
    public string TimeText => FormatTime(Action.Time);
    public string Kind => Action.Kind;
    public string Target => Action.Target;
    public string Window => Action.Window;
    public string AfterCpuText => $"{AfterCpuAverage:0.0} / {AfterCpuMax:0.0} %";
    public string AfterLatencyText => $"{AfterLatencyMax:0} ms";

    public static string FormatTime(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";
}
