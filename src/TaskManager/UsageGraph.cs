using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace taisan11.TaskManager;

/// <summary>タスクマネージャー風の使用率グラフ</summary>
public sealed class UsageGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(UsageGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(UsageGraph),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(UsageGraph),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(UsageGraph),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<double>), typeof(UsageGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightMarkerProperty = DependencyProperty.Register(
        nameof(HighlightMarker), typeof(double), typeof(UsageGraph),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(UsageGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>操作などを示す縦線の位置 (サンプル番号)</summary>
    public IReadOnlyList<double>? Markers
    {
        get => (IReadOnlyList<double>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public double HighlightMarker
    {
        get => (double)GetValue(HighlightMarkerProperty);
        set => SetValue(HighlightMarkerProperty, value);
    }

    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var stroke = Stroke;
        var fill = stroke.CloneCurrentValue();
        fill.Opacity = 0.2;
        var gridPen = new Pen(stroke.CloneCurrentValue(), 1) { Brush = { Opacity = 0.15 } };
        var borderPen = new Pen(stroke, 1);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        for (var i = 1; i < 4; i++)
        {
            var y = Math.Round(height * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }
        for (var i = 1; i < 6; i++)
        {
            var x = Math.Round(width * i / 6) + 0.5;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, height));
        }

        var values = Values;
        var max = Maximum;
        if (values is { Count: > 0 } && max > 0)
        {
            var capacity = Math.Max(2, Capacity);
            var step = width / (capacity - 1);
            var startX = width - ((values.Count - 1) * step);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(startX, height), true, true);
                for (var i = 0; i < values.Count; i++)
                {
                    var v = Math.Clamp(values[i] / max, 0, 1);
                    ctx.LineTo(new Point(startX + (i * step), height - (v * height)), true, true);
                }
                ctx.LineTo(new Point(width, height), true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(fill, null, geometry);

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                for (var i = 0; i < values.Count; i++)
                {
                    var v = Math.Clamp(values[i] / max, 0, 1);
                    var p = new Point(startX + (i * step), height - (v * height));
                    if (i == 0)
                        ctx.BeginFigure(p, false, false);
                    else
                        ctx.LineTo(p, true, true);
                }
            }
            line.Freeze();
            dc.DrawGeometry(null, new Pen(stroke, 1.5), line);
        }

        var markers = Markers;
        if (markers is { Count: > 0 })
        {
            var capacity = Math.Max(2, Capacity);
            var step = width / (capacity - 1);
            var startX = width - (((values?.Count ?? capacity) - 1) * step);
            var markerPen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xE6, 0x7E, 0x22)), 1);
            markerPen.Freeze();
            foreach (var marker in markers)
            {
                var x = Math.Round(startX + (marker * step)) + 0.5;
                dc.DrawLine(markerPen, new Point(x, 0), new Point(x, height));
            }
        }
        if (!double.IsNaN(HighlightMarker))
        {
            var capacity = Math.Max(2, Capacity);
            var step = width / (capacity - 1);
            var startX = width - (((values?.Count ?? capacity) - 1) * step);
            var x = Math.Round(startX + (HighlightMarker * step)) + 0.5;
            dc.DrawLine(new Pen(Brushes.OrangeRed, 2), new Point(x, 0), new Point(x, height));
        }

        dc.DrawRectangle(null, borderPen, new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)));

        var label = new FormattedText(
            Label ?? $"{Capacity}サンプル", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Yu Gothic UI"), 10, stroke, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(4, height - label.Height - 2));
    }
}
