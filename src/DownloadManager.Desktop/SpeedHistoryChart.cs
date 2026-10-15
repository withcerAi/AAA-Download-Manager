using System.Windows;
using System.Windows.Media;

namespace DownloadManager.Desktop;

public sealed class SpeedHistoryChart : FrameworkElement
{
    private readonly Queue<double> _samples = [];
    public double Peak => _samples.Count == 0 ? 0 : _samples.Max();
    public int Samples => _samples.Count;
    public SpeedHistoryChart() { Height = 76; FlowDirection = FlowDirection.LeftToRight; ClipToBounds = true; }
    public void AddSample(double bytesPerSecond) { _samples.Enqueue(double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) : 0); while (_samples.Count > 60) _samples.Dequeue(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing); var width = ActualWidth; var height = ActualHeight; if (width < 2 || height < 2) return;
        var background = (Brush)FindResource("Surface"); var line = (Brush)FindResource("Line"); var accent = (Brush)FindResource("Accent"); drawing.DrawRoundedRectangle(background, null, new Rect(0, 0, width, height), 4, 4);
        for (var row = 1; row < 4; row++) drawing.DrawLine(new Pen(line, 0.5), new Point(0, row * height / 4), new Point(width, row * height / 4));
        for (var column = 1; column < 10; column++) drawing.DrawLine(new Pen(line, 0.5), new Point(column * width / 10, 0), new Point(column * width / 10, height));
        if (_samples.Count < 2) return;
        var values = _samples.ToArray(); var maximum = Math.Max(1, values.Max() * 1.1); var graph = new StreamGeometry();
        using (var context = graph.Open()) { context.BeginFigure(new Point(0, height - values[0] / maximum * (height - 6)), false, false); for (var index = 1; index < values.Length; index++) context.LineTo(new Point(index * width / (values.Length - 1), height - values[index] / maximum * (height - 6)), true, false); }
        drawing.DrawGeometry(null, new Pen(accent, 1.8), graph);
    }
}
