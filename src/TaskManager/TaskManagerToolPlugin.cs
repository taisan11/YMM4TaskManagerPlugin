using YukkuriMovieMaker.Plugin;

namespace taisan11.TaskManager;

internal sealed class TaskManagerToolPlugin : IToolPlugin
{
    public Type ViewModelType => typeof(TaskManagerViewModel);

    public Type ViewType => typeof(TaskManagerView);

    public string Name => "タスクマネージャー";

    public bool AllowMultipleInstances => false;

    public string DefaultGroupName => YukkuriMovieMaker.Resources.Localization.Texts.ToolGroupUtilityName;

    public int DefaultOrder => 590;
}
