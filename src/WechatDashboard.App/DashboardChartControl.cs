using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WechatDashboard.App;

/// <summary>
/// 使用 WPF 原生绘制的统计图控件，支持柱状图、环形占比图和折线图，避免引入第三方依赖。
/// </summary>
public sealed class DashboardChartControl : FrameworkElement
{
    private static readonly Brush[] Palette =
    {
        CreateBrush("#2563EB"), CreateBrush("#7C3AED"), CreateBrush("#DB2777"), CreateBrush("#EA580C"),
        CreateBrush("#16A34A"), CreateBrush("#0891B2"), CreateBrush("#CA8A04"), CreateBrush("#4F46E5")
    };
    private static readonly Brush Background = CreateBrush("#F8FAFC");
    private static readonly Brush Grid = CreateBrush("#E5E7EB");
    private static readonly Brush Text = CreateBrush("#374151");
    private static readonly Brush Muted = CreateBrush("#6B7280");
    private INotifyCollectionChanged? _observableItems;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(DashboardChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public static readonly DependencyProperty ChartTypeProperty = DependencyProperty.Register(
        nameof(ChartType), typeof(string), typeof(DashboardChartControl),
        new FrameworkPropertyMetadata("bar", FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string ChartType
    {
        get => (string)GetValue(ChartTypeProperty);
        set => SetValue(ChartTypeProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRoundedRectangle(Background, new Pen(Grid, 1), new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        var rows = ItemsSource?.OfType<DashboardChartRow>().Where(row => row.Count > 0).ToArray() ?? Array.Empty<DashboardChartRow>();
        if (rows.Length == 0)
        {
            DrawText(context, "暂无可用于绘图的统计数据", new Point(24, 28), 14, Muted);
            return;
        }

        if (ChartType == "pie") DrawDonut(context, rows);
        else if (ChartType == "line") DrawLine(context, rows);
        else DrawBars(context, rows);
    }

    private void DrawBars(DrawingContext context, IReadOnlyList<DashboardChartRow> rows)
    {
        var visible = rows.Take(Math.Max(1, (int)((ActualHeight - 24) / 48))).ToArray();
        var labelWidth = Math.Min(190, Math.Max(120, ActualWidth * .28));
        var countX = Math.Max(labelWidth + 80, ActualWidth - 72);
        var barWidth = Math.Max(20, countX - labelWidth - 20);
        var max = Math.Max(1, visible.Max(row => row.Count));
        for (var index = 0; index < visible.Length; index++)
        {
            var row = visible[index];
            var y = 18 + index * 48;
            DrawText(context, Trim(row.Label, 16), new Point(16, y + 2), 13, Text);
            context.DrawRoundedRectangle(Grid, null, new Rect(labelWidth, y, barWidth, 18), 4, 4);
            context.DrawRoundedRectangle(Palette[index % Palette.Length], null, new Rect(labelWidth, y, Math.Max(3, barWidth * row.Count / max), 18), 4, 4);
            DrawText(context, row.CountText, new Point(countX + 8, y + 2), 13, Text);
            DrawText(context, Trim(row.Detail, 58), new Point(labelWidth, y + 24), 11, Muted);
        }
    }

    private void DrawDonut(DrawingContext context, IReadOnlyList<DashboardChartRow> source)
    {
        var rows = Condense(source);
        var total = rows.Sum(row => row.Count);
        var legendWidth = Math.Min(260, ActualWidth * .42);
        var plotWidth = Math.Max(120, ActualWidth - legendWidth);
        var radius = Math.Max(40, Math.Min(plotWidth, ActualHeight - 32) * .34);
        var center = new Point(plotWidth / 2, ActualHeight / 2);
        var start = -90d;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var end = start + row.Count * 360d / total;
            context.DrawGeometry(Palette[index % Palette.Length], null, DonutSlice(center, radius, radius * .59, start, end));
            start = end;
            var y = 27 + index * 31;
            context.DrawRoundedRectangle(Palette[index % Palette.Length], null, new Rect(plotWidth + 10, y + 2, 10, 10), 2, 2);
            DrawText(context, $"{Trim(row.Label, 15)}  {row.Count * 100d / total:0.#}%", new Point(plotWidth + 28, y), 12, Text);
        }
        DrawCenteredText(context, total.ToString(), center, 20, Text, -8);
        DrawCenteredText(context, "消息", center, 11, Muted, 14);
    }

    private void DrawLine(DrawingContext context, IReadOnlyList<DashboardChartRow> source)
    {
        var rows = source.Take(14).ToArray();
        if (rows.Length < 2)
        {
            DrawText(context, "至少需要两项数据才能绘制趋势折线图。", new Point(24, 28), 14, Muted);
            return;
        }
        const double left = 48, top = 28, right = 26, bottom = 54;
        var width = Math.Max(1, ActualWidth - left - right);
        var height = Math.Max(1, ActualHeight - top - bottom);
        var max = Math.Max(1, rows.Max(row => row.Count));
        for (var line = 0; line <= 4; line++)
        {
            var y = top + height * line / 4;
            context.DrawLine(new Pen(Grid, 1), new Point(left, y), new Point(left + width, y));
            DrawText(context, (max * (4 - line) / 4d).ToString("0"), new Point(5, y - 7), 10, Muted);
        }
        var path = new StreamGeometry();
        using (var drawing = path.Open())
        {
            for (var index = 0; index < rows.Length; index++)
            {
                var point = PlotPoint(index, rows[index].Count, rows.Length, max, left, top, width, height);
                if (index == 0) drawing.BeginFigure(point, false, false); else drawing.LineTo(point, true, false);
            }
        }
        path.Freeze();
        context.DrawGeometry(null, new Pen(Palette[0], 2.5), path);
        for (var index = 0; index < rows.Length; index++)
        {
            var point = PlotPoint(index, rows[index].Count, rows.Length, max, left, top, width, height);
            context.DrawEllipse(Palette[0], new Pen(Brushes.White, 1.5), point, 4.5, 4.5);
            DrawText(context, rows[index].Count.ToString(), new Point(point.X - 7, point.Y - 21), 10, Text);
            DrawText(context, Trim(rows[index].Label, 8), new Point(point.X - 14, top + height + 10), 10, Muted);
        }
    }

    private static Point PlotPoint(int index, int count, int length, int max, double left, double top, double width, double height) => new(left + width * index / (length - 1d), top + height * (1 - count / (double)max));

    private static IReadOnlyList<DashboardChartRow> Condense(IReadOnlyList<DashboardChartRow> rows)
    {
        if (rows.Count <= 7) return rows;
        var visible = rows.Take(6).ToList();
        var remaining = rows.Skip(6).Sum(row => row.Count);
        visible.Add(new DashboardChartRow("其他", remaining, $"{remaining} 条", "其余统计项", 0));
        return visible;
    }

    private static Geometry DonutSlice(Point center, double outerRadius, double innerRadius, double start, double end)
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(OnCircle(center, outerRadius, start), true, true);
            drawing.ArcTo(OnCircle(center, outerRadius, end), new Size(outerRadius, outerRadius), 0, end - start > 180, SweepDirection.Clockwise, true, false);
            drawing.LineTo(OnCircle(center, innerRadius, end), true, false);
            drawing.ArcTo(OnCircle(center, innerRadius, start), new Size(innerRadius, innerRadius), 0, end - start > 180, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point OnCircle(Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (DashboardChartControl)sender;
        if (control._observableItems is not null) control._observableItems.CollectionChanged -= control.ItemsChanged;
        control._observableItems = args.NewValue as INotifyCollectionChanged;
        if (control._observableItems is not null) control._observableItems.CollectionChanged += control.ItemsChanged;
        control.InvalidateVisual();
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();
    private void DrawText(DrawingContext context, string value, Point point, double size, Brush brush) => context.DrawText(CreateText(value, size, brush), point);
    private void DrawCenteredText(DrawingContext context, string value, Point center, double size, Brush brush, double offsetY)
    {
        var formatted = CreateText(value, size, brush);
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2 + offsetY));
    }
    private FormattedText CreateText(string value, double size, Brush brush) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private static string Trim(string value, int maxLength) => value.Length <= maxLength ? value : value[..Math.Max(1, maxLength - 1)] + "…";
    private static SolidColorBrush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
