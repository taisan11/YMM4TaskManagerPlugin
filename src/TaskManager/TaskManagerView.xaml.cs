using System.Windows.Controls;

namespace taisan11.TaskManager;

public partial class TaskManagerView : UserControl
{
    public TaskManagerView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as TaskManagerViewModel)?.Start();
        Unloaded += (_, _) => (DataContext as TaskManagerViewModel)?.Stop();
        DataContextChanged += (_, e) =>
        {
            (e.OldValue as TaskManagerViewModel)?.Stop();
            if (IsLoaded)
                (e.NewValue as TaskManagerViewModel)?.Start();
        };
    }
}
