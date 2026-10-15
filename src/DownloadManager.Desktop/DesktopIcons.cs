using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DownloadManager.Desktop;

internal static class DesktopIcons
{
    public static FrameworkElement Create(string name, double size = 20, string? color = null)
    {
        var geometry = name switch
        {
            "add" => "M12,4 L12,20 M4,12 L20,12",
            "resume" or "startQueue" => "M8,4 L20,12 L8,20 Z",
            "stop" => "M6,6 L18,6 L18,18 L6,18 Z",
            "stopAll" or "stopQueue" => "M8,5 L8,19 M16,5 L16,19",
            "delete" => "M4,6 L20,6 M9,6 L9,3 L15,3 L15,6 M6,6 L7,21 L17,21 L18,6 M10,10 L10,17 M14,10 L14,17",
            "options" => "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 M10,2 L14,2 L15,5 L18,6 L21,6 L22,10 L20,12 L22,15 L20,18 L17,18 L15,21 L11,22 L9,19 L6,18 L3,18 L2,14 L4,12 L2,9 L4,6 L7,6 L9,3 Z",
            "schedule" => "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M12,6 L12,12 L16,14",
            "clearDone" => "M5,3 L19,3 L19,21 L5,21 Z M8,8 L16,8 M8,12 L11,15 L16,10",
            "batch" => "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M2,12 L22,12 M12,2 C6,8 6,16 12,22 C18,16 18,8 12,2",
            "clipboard" => "M8,4 L5,4 L5,22 L21,22 L21,4 L18,4 M9,2 L17,2 L17,7 L9,7 Z M9,12 L17,12 M9,16 L17,16",
            "search" => "M10,3 A7,7 0 1 0 10,17 A7,7 0 1 0 10,3 M15,15 L22,22",
            "all" or "folder" or "queue" => "M2,6 L9,6 L11,9 L22,9 L20,20 L2,20 Z M2,9 L2,4 L9,4 L11,6 L20,6 L20,9",
            "done" => "M4,12 L9,17 L20,6",
            "failed" => "M12,3 L22,21 L2,21 Z M12,9 L12,14 M12,17 L12,18",
            "unfinished" => "M12,3 L12,12 L18,12 M5,5 A10,10 0 1 1 3,12 M2,4 L5,5 L6,2",
            "video" => "M3,5 L21,5 L21,19 L3,19 Z M10,8 L16,12 L10,16 Z",
            "music" => "M9,17 L9,5 L20,3 L20,15 M9,17 A3,3 0 1 1 6,14 L9,14 M20,15 A3,3 0 1 1 17,12 L20,12",
            "archive" => "M4,3 L20,3 L20,21 L4,21 Z M12,3 L12,15 M10,15 L14,15 L14,18 L10,18 Z",
            "software" => "M3,4 L21,4 L21,18 L3,18 Z M8,22 L16,22 M12,18 L12,22",
            _ => "M5,2 L14,2 L20,8 L20,22 L5,22 Z M14,2 L14,8 L20,8 M8,12 L17,12 M8,16 L17,16"
        };
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), StrokeThickness = 1.65, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        if (color is null) path.SetResourceReference(Shape.StrokeProperty, "Accent"); else path.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, FlowDirection = FlowDirection.LeftToRight };
    }
}
