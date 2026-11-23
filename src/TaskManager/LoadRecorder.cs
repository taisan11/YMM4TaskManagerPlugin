using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace taisan11.TaskManager;

internal sealed record RecordedSample(
    TimeSpan Time, double Cpu, double SystemCpu, long PrivateBytes, long ManagedBytes,
    double Gpu, ulong GpuDedicated, double UiLatencyMs);

internal sealed record RecordedAction(TimeSpan Time, string Kind, string Target, string Window);

internal sealed record RecordingResult(DateTime StartedAt, TimeSpan Duration, IReadOnlyList<RecordedSample> Samples, IReadOnlyList<RecordedAction> Actions);

/// <summary>
/// 記録中の操作 (クリック・ボタン・メニュー・ショートカット) とアプリ全体の負荷を記録する。文字入力の内容は記録しない。
/// </summary>
internal sealed class LoadRecorder
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly Dispatcher dispatcher;
    private readonly Stopwatch stopwatch = new();
    private readonly List<RecordedSample> samples = [];
    private readonly List<RecordedAction> actions = [];
    private readonly GpuMonitor gpuMonitor = new();
    private readonly CancellationTokenSource cts = new();
    private readonly DateTime startedAt = DateTime.Now;
    private Thread? worker;
    private long probeStarted;
    private double lastLatencyMs;

    public LoadRecorder(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
    }

    public void Start()
    {
        stopwatch.Start();
        InputManager.Current.PostProcessInput += OnPostProcessInput;
        worker = new Thread(Run) { IsBackground = true, Name = "YMM4TaskManager LoadRecorder", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    public RecordingResult Stop()
    {
        InputManager.Current.PostProcessInput -= OnPostProcessInput;
        cts.Cancel();
        worker?.Join(TimeSpan.FromSeconds(3));
        stopwatch.Stop();
        lock (samples)
            return new RecordingResult(startedAt, stopwatch.Elapsed, [.. samples], [.. actions]);
    }

    private void Run()
    {
        using var process = Process.GetCurrentProcess();
        var lastCpu = process.TotalProcessorTime;
        var lastTime = stopwatch.Elapsed;
        var lastSystem = NativeMethods.GetSystemCpuTimes();
        var token = cts.Token;
        while (!token.WaitHandle.WaitOne(Interval))
        {
            try
            {
                process.Refresh();
                var now = stopwatch.Elapsed;
                var cpu = process.TotalProcessorTime;
                var cpuUsage = Math.Clamp((cpu - lastCpu).TotalMilliseconds / (now - lastTime).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
                lastCpu = cpu;
                lastTime = now;

                var system = NativeMethods.GetSystemCpuTimes();
                double systemUsage = 0;
                if (system is { } s && lastSystem is { } l && (s.Idle - l.Idle) + (s.Busy - l.Busy) > 0)
                    systemUsage = (s.Busy - l.Busy) * 100.0 / ((s.Idle - l.Idle) + (s.Busy - l.Busy));
                lastSystem = system;

                var gpu = gpuMonitor.Sample();
                var sample = new RecordedSample(
                    now, cpuUsage, systemUsage, process.PrivateMemorySize64, GC.GetTotalMemory(false),
                    gpu.SelfUtilization, gpu.SelfDedicated, MeasureUiLatency());
                lock (samples)
                    samples.Add(sample);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    /// <summary>UI スレッドに Input 優先度で投げた処理が実行されるまでの遅延を測る。</summary>
    private double MeasureUiLatency()
    {
        var started = Interlocked.Read(ref probeStarted);
        if (started != 0)
            return Math.Max(lastLatencyMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        var latency = lastLatencyMs;
        var begin = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref probeStarted, begin);
        dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            lastLatencyMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            Interlocked.Exchange(ref probeStarted, 0);
        });
        return latency;
    }

    // KeyDown/MouseDown は Preview が処理済みだと発生しないため Preview を記録する
    private void OnPostProcessInput(object sender, ProcessInputEventArgs e)
    {
        var input = e.StagingItem.Input;
        if (input.RoutedEvent == Mouse.PreviewMouseDownEvent && input is MouseButtonEventArgs mouse)
            RecordMouse(mouse);
        else if (input.RoutedEvent == Keyboard.PreviewKeyDownEvent && input is KeyEventArgs key)
            RecordShortcut(key);
    }

    private void RecordMouse(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInsideTaskManager(source))
            return;
        var button = e.ChangedButton switch
        {
            MouseButton.Right => "右クリック",
            MouseButton.Middle => "中クリック",
            MouseButton.Left when e.ClickCount >= 2 => "ダブルクリック",
            MouseButton.Left => "クリック",
            _ => "クリック",
        };
        var (kind, target) = Describe(source);
        Add(kind is null ? button : $"{button} ({kind})", target, source);
    }

    private void RecordShortcut(KeyEventArgs e)
    {
        if (e.IsRepeat)
            return;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.DeadCharProcessed or Key.None)
            return;
        var modifiers = Keyboard.Modifiers;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var isTextInput = focused is TextBoxBase or PasswordBox || (focused is ComboBox { IsEditable: true });
        var isShortcut = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0
            || key is >= Key.F1 and <= Key.F24
            || (!isTextInput && key is Key.Space or Key.Delete or Key.Escape or Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown);
        if (!isShortcut || (focused is not null && IsInsideTaskManager(focused)))
            return;

        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        var target = focused is null ? string.Empty : Describe(focused).Target;
        Add("ショートカット", string.IsNullOrEmpty(target) ? string.Join("+", parts) : $"{string.Join("+", parts)} → {target}", focused);
    }

    private void Add(string kind, string target, DependencyObject? source)
    {
        var window = source is null ? null : Window.GetWindow(source);
        var title = (window ?? Application.Current?.MainWindow)?.Title ?? string.Empty;
        lock (samples)
            actions.Add(new RecordedAction(stopwatch.Elapsed, kind, target, title));
    }

    private static (string? Kind, string Target) Describe(DependencyObject source)
    {
        string? fallback = null;
        for (var current = source; current is not null; current = Parent(current))
        {
            var kind = current switch
            {
                MenuItem => "メニュー",
                TabItem => "タブ",
                ToggleButton => "トグル",
                ButtonBase => "ボタン",
                ComboBox => "コンボボックス",
                Slider => "スライダー",
                DataGridRow or ListBoxItem or TreeViewItem => "項目",
                ScrollBar => "スクロールバー",
                _ => null,
            };
            var label = Label(current);
            if (kind is not null)
                return (kind, label ?? current.GetType().Name);
            if (fallback is null && current is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                fallback = $"{fe.GetType().Name} ({fe.Name})";
            if (fallback is null && current is UserControl)
                fallback = label is null ? current.GetType().Name : $"{current.GetType().Name} ({label})";
        }
        return (null, fallback ?? source.GetType().Name);
    }

    private static string? Label(DependencyObject element)
    {
        var name = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        object? content = element switch
        {
            HeaderedItemsControl h => h.Header,
            HeaderedContentControl h => h.Header,
            ContentControl c => c.Content,
            _ => null,
        };
        if (content is string text && !string.IsNullOrWhiteSpace(text))
            return text.Replace("_", string.Empty, StringComparison.Ordinal);
        if (content is DependencyObject visual && FindText(visual, 0) is { } inner)
            return inner;
        if (element is FrameworkElement { ToolTip: string tip } && !string.IsNullOrWhiteSpace(tip))
            return tip;
        if (element is ButtonBase { Command: RoutedUICommand command })
            return command.Text;
        return null;
    }

    private static string? FindText(DependencyObject element, int depth)
    {
        if (element is TextBlock { Text: { Length: > 0 } t })
            return t;
        if (depth > 6 || element is not Visual)
            return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindText(VisualTreeHelper.GetChild(element, i), depth + 1) is { } found)
                return found;
        }
        return null;
    }

    private static bool IsInsideTaskManager(DependencyObject element)
    {
        for (var current = element; current is not null; current = Parent(current))
        {
            if (current is TaskManagerView)
                return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject element)
        => element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
