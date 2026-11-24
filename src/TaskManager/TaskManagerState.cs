namespace taisan11.TaskManager;

internal sealed class TaskManagerState
{
    public double IntervalSeconds { get; set; } = 1.0;
    public bool IsPaused { get; set; }
}
