using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace DownloadManager.Desktop;

public sealed class AboutWindow : Window
{
    public const string RepositoryUrl = "https://github.com/withcerAi/AAA-Download-Manager";
    public AboutWindow(LocaleCatalog locale)
    {
        Title = locale.S("ui.about_download_manager"); Width = 650; Height = 590;
        MinWidth = 560; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = locale.Direction;
        SetResourceReference(BackgroundProperty, "Chrome"); SetResourceReference(ForegroundProperty, "Ink");
        var layout = new DockPanel { Margin = new Thickness(26) };
        var close = Ui.Button(locale, "ui.close", (_, _) => Close()); close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);
        var body = new StackPanel();
        var brand = Ui.Label(locale, "ui.download_manager"); brand.FontSize = 26; brand.FontWeight = FontWeights.SemiBold;
        brand.FlowDirection = FlowDirection.LeftToRight; body.Children.Add(brand);
        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        body.Children.Add(new TextBlock { Text = locale.S("about.version", ("version", version)), Margin = new Thickness(0, 7, 0, 18) });
        foreach (var key in new[] { "about.description", "about.features", "about.platform", "about.languages", "about.notice" })
        {
            var text = Ui.Label(locale, key, true); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 0, 0, 14); body.Children.Add(text);
        }
        var repository = Ui.Button(locale, "about.github", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                new AppDialog(locale, locale.S("about.github_error"), locale.S("ui.download_manager"), icon: MessageBoxImage.Error) { Owner = this }.ShowDialog();
            }
        });
        repository.HorizontalAlignment = HorizontalAlignment.Left; repository.ToolTip = RepositoryUrl;
        body.Children.Add(repository);
        body.Children.Add(new TextBlock { Text = RepositoryUrl, FlowDirection = FlowDirection.LeftToRight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) });
        var documents = new WrapPanel();
        AddDocument("about.terms", "about.legal_title", "legal/TERMS-OF-USE.txt");
        AddDocument("about.third_party", "about.notices_title", "media-tools/THIRD-PARTY-NOTICES.txt");
        AddDocument("about.licenses", "about.licenses", "media-tools/LICENSE.txt");
        void AddDocument(string caption, string title, string path) => documents.Children.Add(Ui.Button(locale, caption,
            (_, _) => new LegalDocumentWindow(locale, locale.S(title), path) { Owner = this }.ShowDialog()));
        body.Children.Add(documents);
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = layout;
    }
}

public sealed class LegalDocumentWindow : Window
{
    public LegalDocumentWindow(LocaleCatalog locale, string title, string relativePath)
    {
        Title = title; Width = 850; Height = 650; MinWidth = 580; MinHeight = 590;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FlowDirection = locale.Direction;
        SetResourceReference(BackgroundProperty, "Chrome"); SetResourceReference(ForegroundProperty, "Ink");
        var root = new DockPanel { Margin = new Thickness(22) };
        var close = Ui.Button(locale, "ui.close", (_, _) => Close()); close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FlowDirection = FlowDirection.LeftToRight, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12), FontSize = 13 };
        try { text.Text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { text.Text = locale.S("about.file_error"); text.FlowDirection = locale.Direction; }
        root.Children.Add(text); Content = root;
    }
}
