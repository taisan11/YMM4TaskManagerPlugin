using System.Windows.Controls;

namespace taisan11.TaskManager;

public partial class TaskManagerView : UserControl
{
    public TaskManagerView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as TaskManagerViewModel)?.Start();
        Unloaded += (_, _) => Detach(DataContext as TaskManagerViewModel);
        DataContextChanged += (_, e) =>
        {
            Detach(e.OldValue as TaskManagerViewModel);
            if (IsLoaded)
                (e.NewValue as TaskManagerViewModel)?.Start();
        };
    }

    private static void Detach(TaskManagerViewModel? viewModel)
    {
        viewModel?.EndRecording();
        viewModel?.Stop();
    }
}
