using System.Text.Json;
using YukkuriMovieMaker.Plugin;

namespace taisan11.TaskManager;

internal sealed partial class TaskManagerViewModel
{
    public void LoadState(ToolState stateData)
    {
        if (string.IsNullOrEmpty(stateData.SavedState))
            return;
        try
        {
            var state = JsonSerializer.Deserialize<TaskManagerState>(stateData.SavedState);
            if (state is null)
                return;
            IntervalSeconds = IntervalOptions.Contains(state.IntervalSeconds) ? state.IntervalSeconds : 1;
            IsPaused = state.IsPaused;
        }
        catch (JsonException)
        {
        }
    }

    public ToolState SaveState() => new()
    {
        Title = Title,
        SavedState = JsonSerializer.Serialize(new TaskManagerState { IntervalSeconds = IntervalSeconds, IsPaused = IsPaused }),
    };
}
