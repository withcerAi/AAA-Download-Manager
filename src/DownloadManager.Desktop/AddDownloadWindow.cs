using DownloadManager.Core;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DownloadManager.Desktop;

public sealed class AddDownloadWindow : Window
{
    public TextBox UrlBox { get; } = new() { FlowDirection = FlowDirection.LeftToRight };
    public TextBox FileNameBox { get; } = new() { FlowDirection = FlowDirection.LeftToRight };
    public TextBox FolderBox { get; } = new() { FlowDirection = FlowDirection.LeftToRight };
    public TextBox DescriptionBox { get; } = new();
    private readonly CheckBox _adaptive=new();
    public void SetAdaptive(bool value)=>_adaptive.IsChecked=value;
    public bool Adaptive=>_adaptive.IsChecked==true;
    public bool StartImmediately { get; private set; }
    private readonly ComboBox _queue = new();
    public string QueueId => (_queue.SelectedItem as QueueChoice)?.Id ?? "main";
    private bool _manualName;
    private readonly TextBlock _inspection = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private CancellationTokenSource? _inspectionCancellation;
    private bool _checking;
    private Button? _previewStart, _previewLater;
    private void Checking(bool value) { _checking = value; if (_previewStart is not null) _previewStart.IsEnabled = !value; if (_previewLater is not null) _previewLater.IsEnabled = !value; }
    public Func<string, CancellationToken, Task<LinkMetadata>>? Inspector { get; set; }
    public LinkMetadata? PreviewMetadata { get; private set; }
    private async Task InspectAsync(LocaleCatalog locale)
    {
        _inspectionCancellation?.Cancel();
        PreviewMetadata = null;
        if (Inspector is null || !Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { _inspection.Text = ""; Checking(false); return; }
        using var pending = new CancellationTokenSource(); _inspectionCancellation = pending;
        Checking(true); _inspection.Text = locale.S("inspect.checking");
        try
        {
            await Task.Delay(450, pending.Token);
            var result = await Inspector(uri.AbsoluteUri, pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            PreviewMetadata = result;
            if (!_manualName && !Adaptive && result.FileName is not null) FileNameBox.Text = result.FileName;
            _inspection.Text = locale.S("inspect." + result.State) + (result.Size is {} size ? " · " + DownloadRow.FormatBytes(size) : "") + (result.StatusCode is {} status && result.State != "ready" ? " · HTTP " + status : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or ArgumentException) { if (!pending.IsCancellationRequested) _inspection.Text = locale.S("inspect.unavailable"); }
        finally { if (ReferenceEquals(_inspectionCancellation, pending)) { _inspectionCancellation = null; Checking(false); } }
    }

    public AddDownloadWindow(LocaleCatalog locale, string folder, IReadOnlyList<QueueChoice>? queues = null)
    {
        Title = locale.S("ui.add_new_download"); Width = 700; Height = 510; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; FlowDirection = locale.Direction;
        FolderBox.Text = folder;
        var layout = new DockPanel { Margin = new Thickness(22) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        actions.Children.Add(Ui.Button(locale, "ui.cancel", (_, _) => DialogResult = false));
        _previewLater = Ui.Button(locale, "ui.download_later", (_, _) => Finish(false)); actions.Children.Add(_previewLater);
        var start = Ui.Button(locale, "ui.start_download", (_, _) => Finish(true)); _previewStart = start; start.IsDefault = true; actions.Children.Add(start);
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        start.SetResourceReference(BorderBrushProperty, "Accent"); start.SetResourceReference(BackgroundProperty, "Selection");
        var fields = new StackPanel(); var heading = Ui.Label(locale, "ui.add_new_download"); heading.FontSize = 21; heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 7); fields.Children.Add(heading); var hint = Ui.Label(locale, "native.add_notice", true); hint.Margin = new Thickness(0, 0, 0, 17); fields.Children.Add(hint); fields.Children.Add(Ui.Field(locale, "ui.url", UrlBox)); fields.Children.Add(_inspection); fields.Children.Add(Ui.Field(locale, "ui.file_name", FileNameBox));
        var path = new DockPanel(); var browse = Ui.Button(locale, "native.browse", (_, _) => { var picker = new OpenFolderDialog { InitialDirectory = System.IO.Directory.Exists(FolderBox.Text) ? FolderBox.Text : folder }; if (picker.ShowDialog(this) == true) FolderBox.Text = picker.FolderName; }); DockPanel.SetDock(browse, Dock.Right); path.Children.Add(browse); path.Children.Add(FolderBox);
        fields.Children.Add(Ui.Field(locale, "ui.save_to", path)); fields.Children.Add(Ui.Field(locale, "ui.description", DescriptionBox));
        
        if(queues is not null){Height=560;_queue.ItemsSource=queues;_queue.SelectedIndex=0;fields.Children.Add(Ui.Field(locale,"ui.queue",_queue));}
        _adaptive.Content=Ui.Label(locale,"media.adaptive_option");fields.Children.Add(_adaptive);_adaptive.Checked+=(_,_)=>{if(!_manualName)FileNameBox.Text=System.IO.Path.ChangeExtension(FileNameBox.Text,".mp4");};
        layout.Children.Add(new ScrollViewer{Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}); Content = layout;
        FileNameBox.TextChanged += (_, _) => { if (FileNameBox.IsKeyboardFocusWithin) _manualName = true; };
        UrlBox.TextChanged += (_, _) => { if (!_manualName && Uri.TryCreate(UrlBox.Text, UriKind.Absolute, out var uri)) {FileNameBox.Text = Core.DownloadEngine.SuggestedName(uri.AbsoluteUri);_adaptive.IsChecked=Core.DownloadEngine.IsAdaptiveUrl(uri.AbsoluteUri);} if (IsLoaded) _ = InspectAsync(locale); };
        Closed += (_, _) => _inspectionCancellation?.Cancel();
        Loaded += (_, _) => { UrlBox.Focus(); UrlBox.SelectAll(); _ = InspectAsync(locale); };
        void Finish(bool run)
        {
            if (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps") || string.IsNullOrWhiteSpace(FolderBox.Text)) { AppDialog.Show(this, locale.S("errors.invalid_url"), Title); return; }
            if (_checking) { _inspection.Text = locale.S("inspect.checking"); return; }
            StartImmediately = run; DialogResult = true;
        }
    }
}
