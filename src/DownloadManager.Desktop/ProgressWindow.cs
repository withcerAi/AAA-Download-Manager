using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using DownloadManager.Core;

namespace DownloadManager.Desktop;

public sealed record ProgressViewOptions(bool ShowSpeedTab = true, bool ShowCompletionTab = true, bool ShowDetailsButton = true);

public sealed record TransferEvent(string Time, string Message);
public sealed record ConnectionDisplay(string Number, string Range, string Received, string Speed, string Status);

public sealed class ProgressWindow : Window
{
    private readonly DownloadEngine _engine;
    private readonly Func<string,Task>? _resume;
    private readonly LocaleCatalog _locale;
    private readonly string _id;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly TextBlock _name = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _source = new() { TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _percentage = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _state = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _connectionHint;
    private readonly TextBlock _serverMetadata = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _applyLimit;
    private readonly TextBlock _chartCaption = new() { FontSize = 11, Margin = new Thickness(0, 5, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 10, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 9, 0, 12) };
    private readonly Dictionary<string, TextBlock> _values = [];
    private readonly TabControl _tabs = new();
    private readonly Border _details;
    private readonly Border _completion;
    private readonly Button _pause;
    private readonly Button _stopClose;
    private readonly Button _hideClose;
    private readonly TextBlock _closeHint;
    private readonly Button _detailButton;
    private readonly Button _folder;
    private readonly TextBox _limitValue = new() { Text = "512", Width = 130, FlowDirection = FlowDirection.LeftToRight };
    private readonly ComboBox _limitUnit = new() { Width = 100, ItemsSource = new[] { "KB/s", "MB/s" }, SelectedIndex = 0 };
    private readonly CheckBox _limitEnabled;
    private readonly CheckBox _remember;
    private readonly CheckBox _showSummary;
    private readonly CheckBox _closeWhenDone;
    private readonly TextBlock _limitStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly SpeedHistoryChart _chart = new();
    private readonly ObservableCollection<TransferEvent> _events = [];
    private readonly DataGrid _connections;
    private bool _refreshing;
    private bool _synchronizing;
    private bool _initialized;
    private bool _capturedSamples;
    private bool _detailsShown;
    private bool _seenRunning;
    private string _lastEvent = "";
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private DownloadJob? _job;

    public ProgressWindow(DownloadEngine engine, LocaleCatalog locale, string id, ProgressViewOptions? viewOptions = null, Func<string,Task>? resume = null)
    {
        viewOptions ??= new ProgressViewOptions();
        _engine = engine; _locale = locale; _id = id; _resume=resume;
        Width = 690; Height = Math.Min(480, SystemParameters.WorkArea.Height - 40); MinWidth = 620; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner; FlowDirection = locale.Direction;
        System.Windows.Data.BindingOperations.SetBinding(this, FlowDirectionProperty, new System.Windows.Data.Binding(nameof(LocaleCatalog.Direction)) { Source = locale });
        TaskbarItemInfo = new TaskbarItemInfo();
        var layout = new DockPanel { Margin = new Thickness(14) }; layout.SetResourceReference(Panel.BackgroundProperty, "Chrome");
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _hideClose = Ui.Button(locale, "native.hide_window", async (_, _) => await HideToTrayAsync());
        _pause = Ui.Button(locale, "ui.pause", async (_, _) => await TogglePauseAsync()); _pause.SetResourceReference(BackgroundProperty, "Selection"); _pause.SetResourceReference(BorderBrushProperty, "Accent");
        _stopClose = Ui.Button(locale, "native.stop_and_close", async (_, _) => { try { if (_job?.State != DownloadState.Completed) { await engine.PauseAsync(id); await engine.SetQueueAsync(id, false); } Close(); } catch (Exception error) { ShowError(error); } });
        actions.Children.Add(_hideClose); actions.Children.Add(_pause); actions.Children.Add(_stopClose); DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        _detailButton = Ui.Button(locale, "native.show_details", (_, _) => ToggleDetails()); _detailButton.HorizontalAlignment = HorizontalAlignment.Left; _detailButton.Visibility = viewOptions.ShowDetailsButton ? Visibility.Visible : Visibility.Collapsed; footer.Children.Add(_detailButton); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) }; var icon = DesktopIcons.Create("document", 32); icon.Margin = new Thickness(0, 2, 13, 0); DockPanel.SetDock(icon, Dock.Left); top.Children.Add(icon); var title = new StackPanel(); title.Children.Add(_name); _source.Margin = new Thickness(0, 5, 0, 0); _source.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk"); title.Children.Add(_source); top.Children.Add(title); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var body = new StackPanel();
        var status = new StackPanel { Margin = new Thickness(16, 12, 16, 13) }; var stateLine = new DockPanel(); DockPanel.SetDock(_percentage, Dock.Right); stateLine.Children.Add(_percentage); stateLine.Children.Add(_state); status.Children.Add(stateLine); status.Children.Add(_bar);
        var metrics = new UniformGrid { Columns = 3, Rows = 2 };
        foreach (var key in new[] { "ui.downloaded", "ui.file_size", "ui.time_left", "ui.transfer_rate", "native.average_speed", "native.active_time" })
        {
            var metric = new StackPanel { Margin = new Thickness(10, 5, 10, 6) }; metric.Children.Add(Ui.Label(locale, key, true)); var value = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0), FlowDirection = FlowDirection.LeftToRight }; metric.Children.Add(value); _values[key] = value; var card = Ui.Panel(metric, false); card.CornerRadius = new CornerRadius(4); card.Margin = new Thickness(0, 0, 7, 7); metrics.Children.Add(card);
        }
        status.Children.Add(metrics);
        var facts = new Grid(); facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) }); facts.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var key in new[] { "ui.resume_capability", "native.active_connections", "native.retry_number", "ui.save_to" })
        {
            var row = facts.RowDefinitions.Count; facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var label = Ui.Label(locale, key, true); label.Margin = new Thickness(0, 3, 10, 3); Grid.SetRow(label, row); facts.Children.Add(label); var value = new TextBlock { Margin = new Thickness(0, 3, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = FlowDirection.LeftToRight }; _values[key] = value; Grid.SetRow(value, row); Grid.SetColumn(value, 1); facts.Children.Add(value);
        }
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); status.Children.Add(_error); AddTab("ui.download_status", status);
        var speed = new StackPanel { Margin = new Thickness(20) }; _limitEnabled = Check("native.enable_file_limit"); speed.Children.Add(_limitEnabled); var limitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 10) }; _limitValue.Margin = new Thickness(0, 0, 8, 0); limitRow.Children.Add(_limitValue); limitRow.Children.Add(_limitUnit); _applyLimit = Ui.Button(locale, "native.apply_file_limit", async (_, _) => await ApplyLimitAsync()); _applyLimit.Margin = new Thickness(10, 0, 0, 0); limitRow.Children.Add(_applyLimit); speed.Children.Add(limitRow); _remember = Check("native.remember_file_limit"); speed.Children.Add(_remember); var limitHint = Ui.Label(locale, "native.file_limit_hint", true); limitHint.Margin = new Thickness(0, 10, 0, 0); speed.Children.Add(limitHint); speed.Children.Add(_limitStatus); if (viewOptions.ShowSpeedTab) AddTab("ui.speed_limit", speed);
        _limitEnabled.Checked += (_, _) => UpdateLimitControls(); _limitEnabled.Unchecked += (_, _) => UpdateLimitControls();
        var completion = new StackPanel { Margin = new Thickness(20) }; _showSummary = Check("native.show_completion_summary"); _closeWhenDone = Check("native.close_window_when_done"); completion.Children.Add(_showSummary); completion.Children.Add(_closeWhenDone);
        _showSummary.Checked += async (_, _) => await SaveCompletionAsync(); _showSummary.Unchecked += async (_, _) => await SaveCompletionAsync(); _closeWhenDone.Checked += async (_, _) => await SaveCompletionAsync(); _closeWhenDone.Unchecked += async (_, _) => await SaveCompletionAsync();
        var completionHint = Ui.Label(locale, "native.completion_safe_hint", true); completionHint.Margin = new Thickness(0, 12, 0, 15); completion.Children.Add(completionHint);
        var queueActions=Ui.Button(locale,"native.queue_completion_actions",async(_,_)=>{
            if(Application.Current is App app && app.Scheduler is not null){var queues=await app.Scheduler.SnapshotAsync();var queue=queues.FirstOrDefault(q=>q.JobIds.Contains(id));new SchedulerWindow(app,queue?.Id??"main"){Owner=this}.ShowDialog();}
        });queueActions.HorizontalAlignment=HorizontalAlignment.Left;completion.Children.Add(queueActions);
        completion.Children.Add(Ui.Label(locale,"native.queue_completion_hint",true));if(viewOptions.ShowCompletionTab)AddTab("native.on_completion",completion);
        body.Children.Add(_tabs);
        var completionLine = new DockPanel(); var openFile=Ui.Button(locale,"files.open",(_,_)=>{if(_job is {} job)DownloadFileActions.Open(this,_locale,job,false);});DockPanel.SetDock(openFile,Dock.Right);completionLine.Children.Add(openFile); _folder = Ui.Button(locale, "native.open_folder", (_, _) => OpenFolder()); DockPanel.SetDock(_folder, Dock.Right); completionLine.Children.Add(_folder); _summary.VerticalAlignment = VerticalAlignment.Center; completionLine.Children.Add(_summary); _completion = Ui.Panel(completionLine); _completion.Padding = new Thickness(12, 8, 12, 8); _completion.Margin = new Thickness(0, 10, 0, 0); _completion.Visibility = Visibility.Collapsed; body.Children.Add(_completion);
        var details = new StackPanel { Margin = new Thickness(14, 10, 14, 11) }; var detailsTitle = Ui.Label(locale, "native.transfer_details"); detailsTitle.FontWeight = FontWeights.SemiBold; details.Children.Add(detailsTitle); details.Children.Add(facts);
        var detailTabs = new TabControl { Margin = new Thickness(0, 8, 0, 0) };
        var connectionPanel = new StackPanel(); _connections = new DataGrid { Height = 76, RowHeight = 30, CanUserSortColumns = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var (key, path, width) in new[] { ("native.connection_number", nameof(ConnectionDisplay.Number), 42d), ("native.byte_range", nameof(ConnectionDisplay.Range), 155d), ("ui.downloaded", nameof(ConnectionDisplay.Received), 115d), ("ui.transfer_rate", nameof(ConnectionDisplay.Speed), 115d), ("ui.status", nameof(ConnectionDisplay.Status), 195d) }) { var column = new DataGridTextColumn { Binding = new System.Windows.Data.Binding(path), Width = width }; if (path != nameof(ConnectionDisplay.Status)) { var technical = new Style(typeof(TextBlock)); technical.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight)); column.ElementStyle = technical; } locale.Bind(column, DataGridColumn.HeaderProperty, key); _connections.Columns.Add(column); }
        connectionPanel.Children.Add(_connections); _connectionHint = Ui.Label(locale, "native.single_connection", true); _connectionHint.Margin = new Thickness(0, 6, 0, 0); connectionPanel.Children.Add(_connectionHint); _serverMetadata.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk"); connectionPanel.Children.Add(_serverMetadata); detailTabs.Items.Add(new TabItem { Header = Ui.Label(locale, "ui.connections"), Content = connectionPanel });
        var history = new DataGrid { ItemsSource = _events, Height = 100, RowHeight = 27 }; var time = new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(TransferEvent.Time)), Width = 100 }; locale.Bind(time, DataGridColumn.HeaderProperty, "native.event_time"); history.Columns.Add(time); var message = new DataGridTextColumn { Binding = new System.Windows.Data.Binding(nameof(TransferEvent.Message)), Width = 540 }; locale.Bind(message, DataGridColumn.HeaderProperty, "ui.status"); history.Columns.Add(message); detailTabs.Items.Add(new TabItem { Header = Ui.Label(locale, "ui.event_log"), Content = history }); details.Children.Add(detailTabs);
        var chartContent=new StackPanel{Margin=new Thickness(10)};chartContent.Children.Add(_chart);_chartCaption.SetResourceReference(TextBlock.ForegroundProperty,"MutedInk");_chartCaption.Margin=new Thickness(0,8,0,0);chartContent.Children.Add(_chartCaption);detailTabs.Items.Add(new TabItem{Header=Ui.Label(locale,"native.speed_history"),Content=chartContent});
        _details=Ui.Panel(details);_details.Margin=new Thickness(0,10,0,0);_details.Visibility = Visibility.Collapsed; body.Children.Add(_details);
        _closeHint = Ui.Label(locale, "native.hide_continues", true); _closeHint.FontSize = 11; _closeHint.Margin = new Thickness(0, 9, 0, 0); body.Children.Add(_closeHint);
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = layout;
        _timer.Tick += async (_, _) => await RefreshForRenderAsync(); Loaded += async (_, _) => { _timer.Start(); await RefreshForRenderAsync(); }; Closed += (_, _) => _timer.Stop();
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized&&Application.Current is App app&&app.CanHideProgressToTray&&_job is {} job&&job.State!=DownloadState.Completed)app.HideDownloadToTray(this,job);};
        void AddTab(string key, UIElement content) => _tabs.Items.Add(new TabItem { Header = Ui.Label(locale, key), Content = content });
        CheckBox Check(string key) => new() { Content = Ui.Label(locale, key) };
    }
    private async Task HideToTrayAsync()
    {
        try
        {
            if(_job?.State==DownloadState.Completed){Close();return;}
            var job=(await _engine.SnapshotAsync()).FirstOrDefault(item=>item.Id==_id);
            if(job is null||job.State==DownloadState.Completed){Close();return;}
            RenderJob(job);
            if(Application.Current is App app&&app.HideDownloadToTray(this,job))return;
            WindowState=WindowState.Minimized;
        }
        catch(Exception error){ShowError(error);}
    }
    private void UpdateLimitControls() { var editable = _job?.State != DownloadState.Completed; _limitEnabled.IsEnabled = _remember.IsEnabled = _applyLimit.IsEnabled = editable; _limitValue.IsEnabled = _limitUnit.IsEnabled = editable && _limitEnabled.IsChecked == true; }
    private void ToggleDetails() { _detailsShown = !_detailsShown; _details.Visibility = _detailsShown ? Visibility.Visible : Visibility.Collapsed; Height = Math.Min(_detailsShown ? 780 : 480, SystemParameters.WorkArea.Height - 40); _detailButton.Content = _locale.S(_detailsShown ? "native.hide_details" : "native.show_details"); }
    internal async Task ApplyLimitAsync()
    {
        try
        {
            long rate = 0;
            if (_limitEnabled.IsChecked == true)
            {
                if (!decimal.TryParse(_limitValue.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) || number <= 0) throw new ArgumentException("errors.invalid_speed_limit");
                var bytes = number * (_limitUnit.SelectedIndex == 1 ? 1048576m : 1024m); if (bytes < 1024 || bytes > 1073741824) throw new ArgumentException("errors.invalid_speed_limit"); rate = (long)bytes;
            }
            await _engine.SetSpeedLimitAsync(_id, rate, _remember.IsChecked == true); _limitStatus.Text = _locale.S("native.file_limit_applied"); await RefreshForRenderAsync();
        }
        catch (Exception error) { _limitStatus.Text = _locale.S(error is ArgumentException or OverflowException ? "errors.invalid_speed_limit" : "errors.operation", ("detail", error.GetType().Name)); }
    }
    private async Task SaveCompletionAsync() { if (_synchronizing || !_initialized) return; try { await _engine.SetCompletionOptionsAsync(_id, _showSummary.IsChecked == true, _closeWhenDone.IsChecked == true); await RefreshForRenderAsync(); } catch (Exception error) { ShowError(error); } }
    private async Task TogglePauseAsync() { try { if (_job is null) return; if (_job.State == DownloadState.Running) await _engine.PauseAsync(_id); else if(_resume is not null)await _resume(_id);else await _engine.StartAsync(_id); await RefreshForRenderAsync(); } catch (Exception error) { ShowError(error); } }
    private void ShowError(Exception error) => _error.Text = _locale.S("errors.operation", ("detail", error.GetType().Name));
    private void OpenFolder() {if(_job is {} job)DownloadFileActions.Open(this,_locale,job,true); }
    internal void SetCapturedSamples(IEnumerable<double> samples) { _capturedSamples = true; foreach (var sample in samples) _chart.AddSample(sample); }
    internal void SelectTabForRender(int index) => _tabs.SelectedIndex = Math.Clamp(index, 0, _tabs.Items.Count - 1);
    internal async Task RefreshForRenderAsync()
    {
        if (_refreshing) return; _refreshing = true;
        try
        {
            var job = (await _engine.SnapshotAsync()).FirstOrDefault(item => item.Id == _id); if (job is null) { _timer.Stop(); Close(); return; }
            RenderJob(job);
        }
        catch (ObjectDisposedException) { _timer.Stop(); Close(); }
        finally { _refreshing = false; }
    }
    internal void RenderJob(DownloadJob job)
    {
        _job = job; var row = new DownloadRow(job, _locale); var running = job.State == DownloadState.Running; var complete = job.State == DownloadState.Completed;
        Title = row.ProgressText + " — " + row.Name; _name.Text = row.Name; _source.Text = job.Url; _source.ToolTip = job.Url; _percentage.Text = job.TotalBytes is null && job.MediaTotalParts==0 && !complete ? "—" : row.ProgressText;
        _state.Text = _locale.S(running ? job.PhaseKey : job.State == DownloadState.Completed ? "ui.complete" : job.State == DownloadState.Paused ? "ui.paused" : job.State == DownloadState.Failed ? "ui.connection_error" : "ui.queued");
        _values["ui.downloaded"].Text = DownloadRow.FormatBytes(job.ReceivedBytes); _values["ui.file_size"].Text = job.TotalBytes is null ? _locale.S("native.size_unknown") : row.Size; _values["ui.time_left"].Text = row.TimeLeft;
        _values["ui.transfer_rate"].Text = row.Speed; _values["native.average_speed"].Text = job.ActiveSeconds > 0 ? DownloadRow.FormatBytes((long)(job.TransferredBytes / job.ActiveSeconds)) + "/s" : "—"; _values["native.active_time"].Text = complete && job.ActiveSeconds <= 0 ? "—" : TimeSpan.FromSeconds(Math.Clamp(job.ActiveSeconds, 0, TimeSpan.MaxValue.TotalSeconds - 1)).ToString(job.ActiveSeconds >= 86400 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss", CultureInfo.InvariantCulture);
        _values["ui.resume_capability"].Text = _locale.S(job.ResumeSupported ? "ui.supported" : "native.unsupported"); _values["native.active_connections"].Text = _locale.S("native.connection_count", ("count", job.ActiveConnections), ("max", job.ConnectionLimit)); _values["native.retry_number"].Text = job.RetryNumber.ToString(_locale.Culture); _values["ui.save_to"].Text = job.FinalPath; _values["ui.save_to"].ToolTip = job.FinalPath;
        _error.Text = job.ErrorKey is { } error ? _locale.S(error == "errors.http" && job.ErrorDetail == "403" ? "errors.http_forbidden" : error, ("detail", job.ErrorDetail)) : job.MediaTotalParts>0 && !complete ? _locale.S("media.parts",("count",job.MediaCompletedParts),("total",job.MediaTotalParts)) : !job.ResumeSupported && !complete ? _locale.S("native.resume_notice") : "";
        _bar.Value = complete ? 100 : job.Progress; _bar.IsIndeterminate = job.TotalBytes is null && job.MediaTotalParts==0 && running;
        TaskbarItemInfo!.ProgressState = complete ? TaskbarItemProgressState.None : job.State == DownloadState.Failed ? TaskbarItemProgressState.Error : job.State == DownloadState.Paused ? TaskbarItemProgressState.Paused : job.TotalBytes is null && running ? TaskbarItemProgressState.Indeterminate : running ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None; TaskbarItemInfo.ProgressValue = Math.Clamp(job.Progress / 100d, 0, 1);
        _pause.Content = _locale.S(running ? "ui.pause" : "ui.resume_download"); _pause.IsEnabled = !complete; _stopClose.IsEnabled = !complete;
        _hideClose.Content=_locale.S(complete?"ui.close":"native.hide_window");
        _hideClose.IsDefault=complete;
        _pause.Visibility=_stopClose.Visibility=_closeHint.Visibility=complete?Visibility.Collapsed:Visibility.Visible;
        _completion.Visibility = complete && job.ShowCompletionSummary ? Visibility.Visible : Visibility.Collapsed; _folder.IsEnabled = complete && Directory.Exists(job.Directory); _summary.Text = _locale.S("native.completion_summary", ("size", row.Size));
        _serverMetadata.Text = _locale.S("native.server_metadata", ("status", job.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—"), ("type", job.ContentType ?? "—"));
        _source.ToolTip = job.Url + (job.EffectiveUrl is { } effective && effective != job.Url ? "\n" + _locale.S("native.resolved_url") + " " + effective : "");
        UpdateLimitControls(); _detailButton.Content = _locale.S(_detailsShown ? "native.hide_details" : "native.show_details");
        var end = job.TotalBytes is > 0 ? (job.TotalBytes.Value - 1).ToString(CultureInfo.InvariantCulture) : "—";
        _connections.ItemsSource = job.Segments.Count > 0 ? job.Segments.Select(segment => new ConnectionDisplay((segment.Index + 1).ToString(CultureInfo.InvariantCulture), segment.Start.ToString(CultureInfo.InvariantCulture) + " – " + segment.End.ToString(CultureInfo.InvariantCulture), DownloadRow.FormatBytes(segment.ReceivedBytes), segment.BytesPerSecond > 0 ? DownloadRow.FormatBytes((long)segment.BytesPerSecond) + "/s" : "—", _locale.S(segment.PhaseKey))).ToArray() : new[] { new ConnectionDisplay("1", job.ConnectionOffset.ToString(CultureInfo.InvariantCulture) + " – " + end, DownloadRow.FormatBytes(Math.Max(0, job.ReceivedBytes - job.ConnectionOffset)), row.Speed, _state.Text) };
        _connections.Height = job.Segments.Count > 0 ? Math.Min(180, 42 + job.Segments.Count * 30) : 76;
        _connectionHint.Text = _locale.S(job.Segments.Count > 1 ? "native.segment_mode" : "native.single_connection", ("count", job.Segments.Count));
        if (!complete && DateTimeOffset.UtcNow - _lastSample >= TimeSpan.FromSeconds(1)) { _chart.AddSample(running ? job.BytesPerSecond : 0); _lastSample = DateTimeOffset.UtcNow; }
        _chartCaption.Text = _locale.S(_capturedSamples ? "native.captured_chart_caption" : "native.chart_caption", ("count", _chart.Samples), ("peak", DownloadRow.FormatBytes((long)_chart.Peak)));
        var marker = job.State + ":" + job.PhaseKey + ":" + job.RetryNumber + ":" + job.ErrorKey; if (marker != _lastEvent) { _lastEvent = marker; _events.Add(new(DateTimeOffset.Now.ToString("T", _locale.Culture), _state.Text + (job.ErrorKey is null ? "" : " · " + _error.Text))); while (_events.Count > 100) _events.RemoveAt(0); }
        if (!_initialized)
        {
            _synchronizing = true; var rate = job.SpeedLimitBytesPerSecond ?? _engine.CurrentSettings.BytesPerSecond; _limitEnabled.IsChecked = rate > 0; _limitUnit.SelectedIndex = rate >= 1048576 ? 1 : 0; _limitValue.Text = rate > 0 ? (rate / (_limitUnit.SelectedIndex == 1 ? 1048576m : 1024m)).ToString("0.###", CultureInfo.InvariantCulture) : "512"; _remember.IsChecked = job.RememberSpeedLimit; _showSummary.IsChecked = job.ShowCompletionSummary; _closeWhenDone.IsChecked = job.CloseProgressOnCompletion; _initialized = true; _synchronizing = false; UpdateLimitControls();
        }
        if(complete&&IsLoaded&&!IsVisible){Close();return;}
        var justCompleted = _seenRunning && complete; if (running) _seenRunning = true; if (justCompleted) { _seenRunning = false; if (IsLoaded && job.CloseProgressOnCompletion) Close(); }
    }
}

