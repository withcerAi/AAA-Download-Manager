using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace DownloadManager.Desktop;
public sealed class BrowserIntegrationWindow : Window
{
    private readonly App _app;
    private readonly ComboBox _browser = new() { ItemsSource = new[] { "Chrome", "Edge", "Firefox" }, SelectedIndex = 0 };
    private readonly TextBox _id = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly string _root = AppContext.BaseDirectory;
    private string _chromium = "";
    public BrowserIntegrationWindow(App app)
    {
        _app=app;Width=800;Height=690;MinWidth=680;MinHeight=580;WindowStartupLocation=WindowStartupLocation.CenterOwner;FlowDirection=app.Locale.Direction;app.Locale.Bind(this,TitleProperty,"browser.integration");
        var root=new DockPanel{Margin=new Thickness(20)};root.SetResourceReference(BackgroundProperty,"Chrome");Content=root;
        var heading=new StackPanel{Margin=new Thickness(0,0,0,16)};var title=Ui.Label(app.Locale,"browser.integration");title.FontSize=22;title.FontWeight=FontWeights.SemiBold;heading.Children.Add(title);var subtitle=Ui.Label(app.Locale,"browser.subtitle",true);subtitle.Margin=new Thickness(0,6,0,0);heading.Children.Add(subtitle);DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var footer=new DockPanel{Margin=new Thickness(0,12,0,0)};var close=Ui.Button(app.Locale,"ui.close",(_,_)=>Close());close.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();root.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        Border Card(string key,UIElement content){var stack=new StackPanel();var label=Ui.Label(app.Locale,key);label.FontSize=14;label.FontWeight=FontWeights.SemiBold;label.Margin=new Thickness(0,0,0,10);stack.Children.Add(label);stack.Children.Add(content);var card=Ui.Panel(stack);card.Padding=new Thickness(16);card.Margin=new Thickness(0,0,0,12);card.CornerRadius=new CornerRadius(6);return card;}
        var browserLine=new DockPanel();_browser.Width=180;_browser.Height=32;DockPanel.SetDock(_browser,Dock.Right);browserLine.Children.Add(_browser);_status.VerticalAlignment=VerticalAlignment.Center;_status.Margin=new Thickness(0,0,12,0);_status.SetResourceReference(TextBlock.ForegroundProperty,"MutedInk");browserLine.Children.Add(_status);body.Children.Add(Card("browser.status_card",browserLine));
        var install=new StackPanel();var installNote=Ui.Label(app.Locale,"browser.install_short",true);installNote.Margin=new Thickness(0,0,0,10);install.Children.Add(installNote);var folder=Ui.Button(app.Locale,"browser.open_folder",(_,_)=>OpenFolder());folder.HorizontalAlignment=HorizontalAlignment.Left;install.Children.Add(folder);var how=new Expander{Header=Ui.Label(app.Locale,"browser.show_steps"),Content=Ui.Label(app.Locale,"browser.install_steps",true),Margin=new Thickness(0,10,0,0)};install.Children.Add(how);body.Children.Add(Card("browser.step_install",install));
        var connect=new StackPanel();var line=new DockPanel();var register=Ui.Button(app.Locale,"browser.connect",async(_,_)=>await RegisterAsync(false));register.SetResourceReference(BackgroundProperty,"Selection");register.SetResourceReference(BorderBrushProperty,"Accent");DockPanel.SetDock(register,Dock.Right);line.Children.Add(register);var connectHint=Ui.Label(app.Locale,"browser.connect_short",true);connectHint.Margin=new Thickness(0,0,14,0);line.Children.Add(connectHint);connect.Children.Add(line);
        var advanced=new StackPanel{Margin=new Thickness(0,10,0,0)};_id.Height=32;advanced.Children.Add(Ui.Field(app.Locale,"browser.extension_id",_id));var remove=Ui.Button(app.Locale,"browser.unregister",async(_,_)=>await RegisterAsync(true));remove.HorizontalAlignment=HorizontalAlignment.Left;advanced.Children.Add(remove);advanced.Children.Add(Ui.Label(app.Locale,"browser.native_notice",true));connect.Children.Add(new Expander{Header=Ui.Label(app.Locale,"browser.advanced"),Content=advanced,Margin=new Thickness(0,10,0,0)});body.Children.Add(Card("browser.step_connect",connect));
        body.Children.Add(Card("browser.step_test",Ui.Label(app.Locale,"browser.test_short",true)));
        var info = Path.Combine(_root, "browser-extension", "extension-info.json"); if (File.Exists(info)) { using var document = JsonDocument.Parse(File.ReadAllText(info)); _chromium = document.RootElement.GetProperty("chromiumId").GetString()!; } _id.Text = _chromium;
        _browser.SelectionChanged += (_, _) => { _id.Text = _browser.SelectedIndex == 2 ? "downloadmanager@local.invalid" : _chromium; _id.IsReadOnly = _browser.SelectedIndex == 2; CheckRegistration(); }; CheckRegistration();
    }
    private string Browser => _browser.SelectedIndex switch { 1 => "edge", 2 => "firefox", _ => "chrome" };
    private void CheckRegistration()
    {
        var registry = Browser switch { "edge" => @"Software\Microsoft\Edge\NativeMessagingHosts\", "firefox" => @"Software\Mozilla\NativeMessagingHosts\", _ => @"Software\Google\Chrome\NativeMessagingHosts\" };
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registry + DownloadManager.Core.BrowserProtocol.HostName); var registered = key?.GetValue("") is string path && File.Exists(path);
        _status.Text = _app.Locale.S(registered ? "browser.registered" : "browser.not_registered");
    }
    private async Task RegisterAsync(bool remove)
    {
        try
        {
            var host = Path.Combine(_root, "browser-host", "DownloadManager.BrowserHost.exe"); if (!File.Exists(host)) { _status.Text = _app.Locale.S("browser.package_missing"); return; }
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true }; start.ArgumentList.Add(remove ? "--unregister" : "--register"); start.ArgumentList.Add(Browser); start.ArgumentList.Add(_id.Text.Trim());
            using var process = Process.Start(start)!; await process.WaitForExitAsync(); if (process.ExitCode != 0) { _status.Text = _app.Locale.S("browser.registration_failed"); return; } CheckRegistration();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { _status.Text = _app.Locale.S("browser.registration_failed"); }
    }
    private void OpenFolder()
    {
        var folder = Path.Combine(_root, "browser-extension", Browser == "firefox" ? "firefox" : "chromium"); if (!Directory.Exists(folder)) { _status.Text = _app.Locale.S("browser.package_missing"); return; }
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true }; start.ArgumentList.Add(folder); Process.Start(start);
    }
}
