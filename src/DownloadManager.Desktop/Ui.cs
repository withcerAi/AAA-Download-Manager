using System.Windows;
using System.Windows.Controls;

namespace DownloadManager.Desktop;

internal static class Ui
{
    public static TextBlock Label(LocaleCatalog locale, string key, bool secondary = false)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        locale.Bind(text, TextBlock.TextProperty, key);
        if (secondary) text.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
        return text;
    }
    public static Button Button(LocaleCatalog locale, string key, RoutedEventHandler action)
    {
        var button = new Button { MinWidth = 85 };
        locale.Bind(button, ContentControl.ContentProperty, key);
        button.Click += action;
        return button;
    }
    public static Border Panel(UIElement child, bool paper = true)
    {
        var border = new Border { Child = child, BorderThickness = new Thickness(1), Padding = new Thickness(0) };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        border.SetResourceReference(Border.BackgroundProperty, paper ? "Surface" : "Chrome");
        return border;
    }
    public static FrameworkElement Field(LocaleCatalog locale, string key, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = Label(locale, key, true); label.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(label); Grid.SetColumn(control, 1); grid.Children.Add(control);
        return grid;
    }
}
