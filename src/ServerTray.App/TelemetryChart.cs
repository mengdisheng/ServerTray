using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ServerTray.Core;

namespace ServerTray.App;

public sealed class TelemetryChart : FrameworkElement
{
    public static long RenderCalls;
    private HistoryPoint[] points = [];
    public bool Network { get; set; }
    public int BucketSeconds { get; set; } = 60;
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;
    public string[] VisibleMetrics { get; set; } = ["cpu", "memory", "disk", "network"];
    public void SetPoints(HistoryPoint[] rows) { points = rows.Length > 1200 ? rows.Take(1200).ToArray() : rows; if (IsVisible) InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (!IsVisible || ActualWidth < 80 || ActualHeight < 60) return; Interlocked.Increment(ref RenderCalls);
        // Bound the retained composition scene to one image per chart; rebuild
        // only when samples, dimensions or the selected history range change.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) DrawGraph(drawing);
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        dc.DrawImage(bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }
    private void DrawGraph(DrawingContext dc)
    {
        var ink = (Brush?)TryFindResource("Muted") ?? Brushes.SlateGray;
        var grid = (Brush?)TryFindResource("Line") ?? Brushes.LightGray;
        var width = ActualWidth - 65; var height = ActualHeight - 36; const double left = 53, top = 8;
        var max = Network ? Math.Max(1, points.SelectMany(p => new[] { p.Rx ?? 0, p.Tx ?? 0 }).DefaultIfEmpty(1).Max() * 1.15) : 100;
        for (var i = 0; i <= 4; i++) { var y = top + height * i / 4; dc.DrawLine(new Pen(grid, .6), new(left, y), new(left + width, y)); Label(dc, Network ? Display.Bytes(max * (4 - i) / 4) : (25 * (4 - i)) + "%", new(0, y - 7), ink, 10); }
        if (points.Length == 0) { Label(dc, Text.Get("NoHistory"), new(left + 20, top + height / 2), ink, 12); return; }
        var first = points[0].Stamp; var last = Math.Max(first + BucketSeconds, points[^1].Stamp);
        if (Network) { Line(p => p.Rx, Color.FromRgb(23, 155, 111)); Line(p => p.Tx, Color.FromRgb(83, 120, 219)); }
        else { if (VisibleMetrics.Contains("cpu")) Line(p => p.Cpu, Color.FromRgb(23, 155, 111)); if (VisibleMetrics.Contains("memory")) Line(p => p.Memory, Color.FromRgb(83, 120, 219)); if (VisibleMetrics.Contains("disk")) Line(p => p.Disk, Color.FromRgb(195, 145, 56)); }
        Label(dc, Local(first), new(left, top + height + 10), ink, 10); Label(dc, Local(last), new(left + width - 100, top + height + 10), ink, 10);
        void Line(Func<HistoryPoint, double?> pick, Color color)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                long? previous = null;
                foreach (var row in points)
                {
                    if (pick(row) is not { } value) { previous = null; continue; }
                    var p = new Point(left + (row.Stamp - first) / (double)(last - first) * width, top + height * (1 - Math.Clamp(value / max, 0, 1)));
                    if (previous is null || row.Stamp - previous > BucketSeconds * 1.8) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, false);
                    previous = row.Stamp;
                }
            }
            geometry.Freeze(); var brush = new SolidColorBrush(color); brush.Freeze(); dc.DrawGeometry(null, new Pen(brush, 1.6), geometry);
        }
    }
    private string Local(long stamp) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(stamp), Zone).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    private void Label(DrawingContext dc, string text, Point at, Brush brush, double size) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);
}
