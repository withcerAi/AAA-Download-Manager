using DownloadManager.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace DownloadManager.Desktop;
public sealed class SchedulerWindow : Window
{
    private sealed record QueueItem(string Id, string Name) { public override string ToString() => Name; }
    private sealed record JobItem(string Id, string Name, string Status);
    private readonly App _app;
    private readonly LocaleCatalog _locale;
    private readonly ListBox _queues = new();
    private readonly TextBox _name = new(), _parallel = new(), _start = new(), _duration = new();
    private readonly DatePicker _first = new(), _last = new();
    private readonly CheckBox _enabled = new(), _stop = new(), _catchup = new();
    private readonly ComboBox _kind = new(), _zone = new(), _target = new(), _completion = new();
    private readonly TextBox _delay = new(), _file = new(), _connection = new(), _retryCount = new(), _retryDelay = new(), _interval = new();
    private readonly CheckBox _sync = new();
    private readonly CheckBox _force = new();
    private readonly Dictionary<DayOfWeek, CheckBox> _days = [];
    private readonly DataGrid _jobs = new() { SelectionMode = DataGridSelectionMode.Extended, IsReadOnly = true, AutoGenerateColumns = false, Height = 210 };
    private FrameworkElement? _fileField, _connectionField;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap }, _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _events = new() { Height = 120 };
    private readonly string _initialQueue;
    private DownloadQueue? _draft;
    private bool _loading, _dirty;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public SchedulerWindow(App app,string initialQueue="main")
    {
        _initialQueue=initialQueue; _app = app; _locale = app.Locale; Width = 1110; Height = 850; MinWidth = 940; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner; FlowDirection = _locale.Direction;
        _locale.Bind(this, TitleProperty, "ui.scheduler");
        var root = new DockPanel { Margin = new Thickness(16) }; root.SetResourceReference(BackgroundProperty, "Chrome"); Content = root;
        var title = Ui.Label(_locale, "scheduler.title"); title.FontSize = 21; title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 8); DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var note = Ui.Label(_locale, "scheduler.residency", true); note.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) }; footer.Children.Add(Ui.Button(_locale, "ui.close", (_, _) => Close())); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(210) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); root.Children.Add(grid);
        var navigation = new DockPanel(); var actions = new WrapPanel(); actions.Children.Add(Ui.Button(_locale, "scheduler.new", async (_, _) => await Guard(NewAsync))); actions.Children.Add(Ui.Button(_locale, "ui.delete", async (_, _) => await Guard(DeleteAsync))); DockPanel.SetDock(actions, Dock.Bottom); navigation.Children.Add(actions); navigation.Children.Add(_queues); grid.Children.Add(Ui.Panel(navigation));
        var body = new StackPanel { Margin = new Thickness(16) }; var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; var contentFrame=Ui.Panel(scroll); Grid.SetColumn(contentFrame, 2); grid.Children.Add(contentFrame);
        var bar = new WrapPanel(); bar.Children.Add(Ui.Button(_locale, "ui.save", async (_, _) => await Guard(async () => { await SaveAsync(); }))); bar.Children.Add(Ui.Button(_locale, "ui.start_queue", async (_, _) => await Guard(async () => { if (await SaveAsync() && _draft is { } queue) { await _app.Scheduler.StartAsync(queue.Id); await RefreshStatusAsync(); } }))); bar.Children.Add(Ui.Button(_locale, "ui.stop_queue", async (_, _) => await Guard(async () => { if (_draft is { } queue) { await _app.Scheduler.StopAsync(queue.Id); await RefreshStatusAsync(); } }))); body.Children.Add(bar); _error.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); body.Children.Add(_error); body.Children.Add(_state);
        body.Children.Add(Ui.Field(_locale, "scheduler.name", _name)); body.Children.Add(Ui.Field(_locale, "scheduler.parallel", _parallel));
        body.Children.Add(Ui.Field(_locale,"scheduler.retries",_retryCount));body.Children.Add(Ui.Field(_locale,"scheduler.retry_delay",_retryDelay));_locale.Bind(_sync,ContentControl.ContentProperty,"scheduler.sync");body.Children.Add(_sync);body.Children.Add(Ui.Field(_locale,"scheduler.interval",_interval));body.Children.Add(Ui.Label(_locale,"scheduler.sync_notice",true));
        _locale.Bind(_enabled, ContentControl.ContentProperty, "scheduler.enable"); body.Children.Add(_enabled);
        foreach (var kind in Enum.GetValues<ScheduleKind>()) _kind.Items.Add(new ComboBoxItem { Content = _locale.S("scheduler." + kind.ToString().ToLowerInvariant()), Tag = kind });
        body.Children.Add(Ui.Field(_locale, "scheduler.recurrence", _kind)); body.Children.Add(Ui.Field(_locale, "scheduler.first_date", _first)); body.Children.Add(Ui.Field(_locale, "scheduler.last_date", _last));
        _start.FlowDirection = _duration.FlowDirection = _parallel.FlowDirection = FlowDirection.LeftToRight;
        body.Children.Add(Ui.Field(_locale, "scheduler.start_time", _start)); body.Children.Add(Ui.Field(_locale, "scheduler.duration", _duration));
        var days = new WrapPanel(); foreach (var day in Enum.GetValues<DayOfWeek>()) { var check = new CheckBox { Content = _locale.Culture.DateTimeFormat.GetDayName(day), Margin = new Thickness(0, 4, 12, 4) }; _days[day] = check; days.Children.Add(check); }
        body.Children.Add(Ui.Field(_locale, "scheduler.days", days)); _zone.ItemsSource = TimeZoneInfo.GetSystemTimeZones(); _zone.DisplayMemberPath = nameof(TimeZoneInfo.DisplayName); _zone.SelectedValuePath = nameof(TimeZoneInfo.Id); body.Children.Add(Ui.Field(_locale, "scheduler.timezone", _zone));
        _locale.Bind(_stop, ContentControl.ContentProperty, "scheduler.stop_end"); body.Children.Add(_stop); _locale.Bind(_catchup, ContentControl.ContentProperty, "scheduler.catch_up"); body.Children.Add(_catchup);
        var completionStart=body.Children.Count;
        foreach (var action in Enum.GetValues<CompletionAction>()) _completion.Items.Add(new ComboBoxItem { Content = _locale.S("completion." + action.ToString().ToLowerInvariant()), Tag = action });
        body.Children.Add(Ui.Field(_locale, "completion.action", _completion)); body.Children.Add(Ui.Field(_locale, "completion.delay", _delay));
        var fileLine = new DockPanel(); var browse = Ui.Button(_locale, "native.browse", (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog(); if (picker.ShowDialog(this) == true) _file.Text = picker.FileName; }); DockPanel.SetDock(browse, Dock.Right); fileLine.Children.Add(browse); fileLine.Children.Add(_file); body.Children.Add(Ui.Field(_locale, "completion.file", fileLine)); body.Children.Add(Ui.Field(_locale, "completion.connection", _connection)); _locale.Bind(_force, ContentControl.ContentProperty, "completion.force"); body.Children.Add(_force); body.Children.Add(Ui.Label(_locale, "completion.only_success", true));
        var jobsStart=body.Children.Count;
        var jobsTitle = Ui.Label(_locale, "scheduler.members"); jobsTitle.FontWeight = FontWeights.SemiBold; jobsTitle.Margin = new Thickness(0, 18, 0, 8); body.Children.Add(jobsTitle);
        _jobs.Columns.Add(new DataGridTextColumn { Header = _locale.S("ui.file_name"), Binding = new Binding(nameof(JobItem.Name)), MinWidth=280, Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); _jobs.Columns.Add(new DataGridTextColumn { Header = _locale.S("ui.status"), Binding = new Binding(nameof(JobItem.Status)), Width = 130 }); body.Children.Add(_jobs);
        var members = new WrapPanel(); members.Children.Add(Ui.Button(_locale, "scheduler.up", async (_, _) => await Guard(() => MoveAsync(-1)))); members.Children.Add(Ui.Button(_locale, "scheduler.down", async (_, _) => await Guard(() => MoveAsync(1)))); _target.Width = 170; members.Children.Add(_target); members.Children.Add(Ui.Button(_locale, "scheduler.move", async (_, _) => await Guard(MoveToAsync))); members.Children.Add(Ui.Button(_locale, "scheduler.remove_member", async (_, _) => await Guard(async () => { await _app.Scheduler.UnassignAsync(_jobs.SelectedItems.OfType<JobItem>().Select(job=>job.Id).ToArray()); await _app.Scheduler.TickAsync(DateTimeOffset.UtcNow); await RefreshStatusAsync(); }))); body.Children.Add(members);
        var log = Ui.Label(_locale, "ui.event_log", true); log.Margin = new Thickness(0, 14, 0, 6); body.Children.Add(log); body.Children.Add(_events);
        body.Children.Clear();contentFrame.Child=BuildCompactEditor(bar);
        foreach (var text in new[] { _name, _parallel, _start, _duration, _delay, _file, _connection, _retryCount, _retryDelay, _interval }) text.TextChanged += (_, _) => Dirty();
        foreach (var check in new[] { _enabled, _stop, _catchup, _force, _sync }.Concat(_days.Values)) { check.Checked += (_, _) => { Dirty(); UpdateDependencies(); }; check.Unchecked += (_, _) => { Dirty(); UpdateDependencies(); }; }
        _completion.SelectionChanged += (_, _) => { Dirty(); UpdateDependencies(); };
        _kind.SelectionChanged += (_, _) => { Dirty(); UpdateDependencies(); }; _zone.SelectionChanged += (_, _) => Dirty(); _first.SelectedDateChanged += (_, _) => Dirty(); _last.SelectedDateChanged += (_, _) => Dirty();
        _jobs.SizeChanged+=(_,_)=>{if(_jobs.Columns.Count>1&&_jobs.ActualWidth>420)_jobs.Columns[0].Width=Math.Max(280,_jobs.ActualWidth-134);};
        _queues.SelectionChanged += async (_, _) => { if (_loading || _queues.SelectedItem is not QueueItem item) return; if (_dirty && AppDialog.Show(this, _locale.S("scheduler.discard"), Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) { _loading = true; _queues.SelectedItem = _queues.Items.OfType<QueueItem>().FirstOrDefault(q => q.Id == _draft?.Id); _loading = false; return; } await Guard(() => SelectAsync(item.Id)); };
        Loaded += async (_, _) => { await Guard(LoadAsync); _timer.Start(); }; _timer.Tick += async (_, _) => await Guard(RefreshStatusAsync); Closed += (_, _) => _timer.Stop();
        Closing += (_, e) => { if (_dirty && AppDialog.Show(this, _locale.S("scheduler.discard"), Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true; };
    }
    private DockPanel BuildCompactEditor(WrapPanel actions)
    {
        static void Detach(FrameworkElement control){if(control.Parent is Panel panel)panel.Children.Remove(control);else if(control.Parent is Decorator decorator)decorator.Child=null;}
        FrameworkElement Field(string key,FrameworkElement control){Detach(control);if(control is TextBox or ComboBox or DatePicker){control.MinHeight=36;control.Height=double.NaN;}var stack=new StackPanel{Margin=new Thickness(0,0,0,12)};var label=Ui.Label(_locale,key,true);label.FontSize=11;label.Margin=new Thickness(0,0,0,6);stack.Children.Add(label);stack.Children.Add(control);return stack;}
        Border Card(string key,UIElement content){var panel=new StackPanel();var title=Ui.Label(_locale,key);title.FontWeight=FontWeights.SemiBold;title.FontSize=14;title.Margin=new Thickness(0,0,0,12);panel.Children.Add(title);panel.Children.Add(content);var frame=Ui.Panel(panel);frame.Padding=new Thickness(14);frame.CornerRadius=new CornerRadius(6);return frame;}
        Grid Columns(params (UIElement Element,double Weight)[] items){var grid=new Grid();for(var i=0;i<items.Length;i++){grid.ColumnDefinitions.Add(new(){Width=new GridLength(items[i].Weight,GridUnitType.Star)});var wrap=new Border{Child=items[i].Element,Margin=new Thickness(0,0,i+1<items.Length?12:0,0)};Grid.SetColumn(wrap,i);grid.Children.Add(wrap);}return grid;}
        foreach(var box in new[]{_parallel,_retryCount,_retryDelay,_interval,_start,_duration,_delay})box.FlowDirection=FlowDirection.LeftToRight;
        foreach(var (check,key) in new[]{(_enabled,"scheduler.enable"),(_sync,"scheduler.sync"),(_stop,"scheduler.stop_end"),(_catchup,"scheduler.catch_up"),(_force,"completion.force")}){Detach(check);BindingOperations.ClearBinding(check,ContentControl.ContentProperty);check.Content=Ui.Label(_locale,key);check.Margin=new Thickness(0,4,0,8);}
        var host=new DockPanel();var header=new StackPanel{Margin=new Thickness(16,12,16,12)};var commandLine=new DockPanel();DockPanel.SetDock(actions,Dock.Right);commandLine.Children.Add(actions);Detach(_state);_state.VerticalAlignment=VerticalAlignment.Center;_state.FontSize=12;_state.SetResourceReference(TextBlock.ForegroundProperty,"MutedInk");commandLine.Children.Add(_state);header.Children.Add(commandLine);Detach(_error);header.Children.Add(_error);var name=Field("scheduler.name",_name);name.Margin=new Thickness(0,10,0,0);header.Children.Add(name);DockPanel.SetDock(header,Dock.Top);host.Children.Add(header);
        var tabs=new TabControl();host.Children.Add(tabs);
        var schedule=new StackPanel{Margin=new Thickness(14)};schedule.Children.Add(_enabled);
        var timing=new StackPanel();timing.Children.Add(Field("scheduler.recurrence",_kind));timing.Children.Add(Columns((Field("scheduler.first_date",_first),1),(Field("scheduler.last_date",_last),1)));timing.Children.Add(Columns((Field("scheduler.start_time",_start),1),(Field("scheduler.duration",_duration),1)));timing.Children.Add(_stop);
        var repeat=new StackPanel();var dayLabel=Ui.Label(_locale,"scheduler.days",true);dayLabel.FontSize=11;dayLabel.Margin=new Thickness(0,0,0,6);repeat.Children.Add(dayLabel);var days=new WrapPanel{Margin=new Thickness(0,0,0,12)};foreach(var (day,check) in _days){Detach(check);check.Content=_locale.Culture.DateTimeFormat.GetAbbreviatedDayName(day);check.Margin=new Thickness(0,4,13,5);days.Children.Add(check);}repeat.Children.Add(days);repeat.Children.Add(Field("scheduler.timezone",_zone));repeat.Children.Add(_catchup);
        schedule.Children.Add(Columns((Card("scheduler.timing_card",timing),1),(Card("scheduler.repeat_card",repeat),1)));
        var sync=new StackPanel{Margin=new Thickness(0,12,0,0)};sync.Children.Add(_sync);_interval.Width=140;_interval.MaxWidth=140;_interval.HorizontalAlignment=HorizontalAlignment.Left;sync.Children.Add(Field("scheduler.interval",_interval));var syncNote=Ui.Label(_locale,"scheduler.sync_notice",true);syncNote.FontSize=11;sync.Children.Add(syncNote);schedule.Children.Add(Card("scheduler.sync_card",sync));
        var files=new StackPanel{Margin=new Thickness(14)};files.Children.Add(Columns((Field("scheduler.parallel",_parallel),1),(Field("scheduler.retries",_retryCount),1),(Field("scheduler.retry_delay",_retryDelay),1)));files.Children.Add(Ui.Label(_locale,"scheduler.parallel_hint",true));Detach(_jobs);_jobs.Height=300;files.Children.Add(_jobs);var memberActions=new WrapPanel{Margin=new Thickness(0,8,0,10)};memberActions.Children.Add(Ui.Button(_locale,"scheduler.up",async(_,_)=>await Guard(()=>MoveAsync(-1))));memberActions.Children.Add(Ui.Button(_locale,"scheduler.down",async(_,_)=>await Guard(()=>MoveAsync(1))));Detach(_target);_target.Height=36;_target.Width=170;memberActions.Children.Add(_target);memberActions.Children.Add(Ui.Button(_locale,"scheduler.move",async(_,_)=>await Guard(MoveToAsync)));memberActions.Children.Add(Ui.Button(_locale,"scheduler.remove_member",async(_,_)=>await Guard(async()=>{await _app.Scheduler.UnassignAsync(_jobs.SelectedItems.OfType<JobItem>().Select(j=>j.Id).ToArray());await RefreshStatusAsync();})));files.Children.Add(memberActions);Detach(_events);files.Children.Add(new Expander{Header=Ui.Label(_locale,"ui.event_log"),Content=_events,IsExpanded=false});
        var completion=new StackPanel{Margin=new Thickness(14)};var choices=new StackPanel();choices.Children.Add(Columns((Field("completion.action",_completion),3),(Field("completion.delay",_delay),1)));var fileRow=new DockPanel();var browse=Ui.Button(_locale,"native.browse",(_,_)=>{var picker=new Microsoft.Win32.OpenFileDialog();if(picker.ShowDialog(this)==true)_file.Text=picker.FileName;});DockPanel.SetDock(browse,Dock.Right);fileRow.Children.Add(browse);Detach(_file);_file.Height=36;fileRow.Children.Add(_file);_fileField=Field("completion.file",fileRow);choices.Children.Add(_fileField);_connectionField=Field("completion.connection",_connection);choices.Children.Add(_connectionField);choices.Children.Add(_force);completion.Children.Add(Card("completion.title",choices));var safety=Ui.Panel(Ui.Label(_locale,"completion.only_success",true),false);safety.Padding=new Thickness(14);safety.CornerRadius=new CornerRadius(6);safety.Margin=new Thickness(0,12,0,0);completion.Children.Add(safety);
        foreach(var(key,page) in new[]{("ui.scheduler",schedule),("scheduler.members",files),("completion.title",completion)})tabs.Items.Add(new TabItem{Header=Ui.Label(_locale,key),Content=new ScrollViewer{Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        return host;
    }
    private void Dirty() { if (!_loading) _dirty = true; }
    private void UpdateDependencies() { if(_fileField is not null)_fileField.Visibility=(_completion.SelectedItem as ComboBoxItem)?.Tag is CompletionAction.OpenFile?Visibility.Visible:Visibility.Collapsed; if(_connectionField is not null)_connectionField.Visibility=(_completion.SelectedItem as ComboBoxItem)?.Tag is CompletionAction.Disconnect?Visibility.Visible:Visibility.Collapsed; _force.Visibility=(_completion.SelectedItem as ComboBoxItem)?.Tag is CompletionAction.ShutDown?Visibility.Visible:Visibility.Collapsed; var action = (_completion.SelectedItem as ComboBoxItem)?.Tag; _interval.IsEnabled=_sync.IsChecked==true; _file.IsEnabled = action is CompletionAction.OpenFile; _connection.IsEnabled = action is CompletionAction.Disconnect; _force.IsEnabled = action is CompletionAction.ShutDown; _delay.IsEnabled = action is CompletionAction and not CompletionAction.None; var enabled = _enabled.IsChecked == true; foreach (var control in new Control[] { _kind, _first, _last, _start, _duration, _zone, _stop, _catchup }) control.IsEnabled = enabled; foreach (var check in _days.Values) check.IsEnabled = enabled && (_kind.SelectedItem as ComboBoxItem)?.Tag is ScheduleKind.Weekly; }
    private async Task Guard(Func<Task> action) { try { await action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.IOException or TimeZoneNotFoundException or FormatException) { _error.Text = _locale.S("scheduler.invalid"); } }
    public async Task LoadAsync() { await ReloadQueuesAsync(_draft?.Id ?? _initialQueue); }
    private string QueueName(DownloadQueue queue) => string.IsNullOrEmpty(queue.Name) ? _locale.S("ui.main_queue") : queue.Name;
    private async Task ReloadQueuesAsync(string selected)
    {
        var queues = await _app.Scheduler.SnapshotAsync(); _loading = true; _queues.ItemsSource = queues.Select(q => new QueueItem(q.Id, QueueName(q))).ToArray(); _queues.SelectedItem = _queues.Items.OfType<QueueItem>().FirstOrDefault(q => q.Id == selected); _target.ItemsSource = queues.Select(q => new QueueItem(q.Id, QueueName(q))).ToArray(); _loading = false; await SelectAsync(selected);
    }
    private async Task SelectAsync(string id)
    {
        _draft = (await _app.Scheduler.SnapshotAsync()).First(q => q.Id == id); var schedule = _draft.Schedule; _loading = true;
        _name.Text = QueueName(_draft); _parallel.Text = _draft.Parallel.ToString(CultureInfo.InvariantCulture); _enabled.IsChecked = schedule.Enabled; _kind.SelectedItem = _kind.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, schedule.Kind)); _first.SelectedDate = schedule.FirstDate.ToDateTime(TimeOnly.MinValue); _last.SelectedDate = schedule.LastDate?.ToDateTime(TimeOnly.MinValue); _start.Text = schedule.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture); _duration.Text = schedule.DurationMinutes.ToString(CultureInfo.InvariantCulture); _stop.IsChecked = schedule.StopAtEnd; _catchup.IsChecked = schedule.CatchUp; _zone.SelectedValue = schedule.TimeZoneId; foreach (var (day, check) in _days) check.IsChecked = schedule.Days.Contains(day);
        _retryCount.Text=_draft.RetryLimit.ToString();_retryDelay.Text=_draft.RetryDelaySeconds.ToString();_interval.Text=_draft.IntervalMinutes.ToString();_sync.IsChecked=_draft.Synchronization;
        _completion.SelectedItem = _completion.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, _draft.Completion.Action)); _delay.Text = _draft.Completion.DelaySeconds.ToString(CultureInfo.InvariantCulture); _file.Text = _draft.Completion.FilePath; _connection.Text = _draft.Completion.ConnectionName; _force.IsChecked = _draft.Completion.Force;
        _loading = false; _dirty = false; _error.Text = ""; UpdateDependencies(); await RefreshStatusAsync();
    }
    private async Task<bool> SaveAsync()
    {
        if (_draft is null) return false;
        if (!int.TryParse(_parallel.Text, out var parallel) || !int.TryParse(_duration.Text, out var duration) || !TimeOnly.TryParseExact(_start.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) || _first.SelectedDate is not { } first || _zone.SelectedValue is not string zone || string.IsNullOrWhiteSpace(_name.Text)) { _error.Text = _locale.S("scheduler.invalid"); return false; }
        var next = _draft.Copy(); next.Name = _name.Text.Trim(); next.Parallel = parallel; next.Schedule = new() { Enabled = _enabled.IsChecked == true, Kind = (ScheduleKind)((ComboBoxItem)_kind.SelectedItem).Tag, FirstDate = DateOnly.FromDateTime(first), LastDate = _last.SelectedDate is { } last ? DateOnly.FromDateTime(last) : null, StartTime = start, DurationMinutes = duration, Days = _days.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList(), StopAtEnd = _stop.IsChecked == true, CatchUp = _catchup.IsChecked == true, TimeZoneId = zone };
        if(!int.TryParse(_retryCount.Text,out var retryCount)||!int.TryParse(_retryDelay.Text,out var retryDelay)||!int.TryParse(_interval.Text,out var interval)) { _error.Text=_locale.S("scheduler.invalid");return false;}next.RetryLimit=retryCount;next.RetryDelaySeconds=retryDelay;next.Synchronization=_sync.IsChecked==true;next.IntervalMinutes=interval;
        if (!int.TryParse(_delay.Text, out var delay)) { _error.Text = _locale.S("completion.invalid"); return false; }
        next.Completion = new() { Action = (CompletionAction)((ComboBoxItem)_completion.SelectedItem).Tag, DelaySeconds = delay, FilePath = _file.Text.Trim(), ConnectionName = _connection.Text.Trim(), Force = _force.IsChecked == true };
        await _app.Scheduler.SaveQueueAsync(next); _dirty = false; await ReloadQueuesAsync(next.Id); return true;
    }
    private async Task NewAsync() { if (_dirty && !await SaveAsync()) return; var queue = new DownloadQueue { Name = _locale.S("scheduler.new_queue") }; await _app.Scheduler.SaveQueueAsync(queue); await ReloadQueuesAsync(queue.Id); }
    private async Task DeleteAsync() { if (_draft is not { Id: not "main" } queue) return; if (AppDialog.Show(this, _locale.S("scheduler.delete_confirm"), Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; await _app.Scheduler.DeleteAsync(queue.Id); _dirty = false; await ReloadQueuesAsync("main"); }
    private async Task MoveAsync(int delta) { if(_draft is null)return;await _app.Scheduler.MoveManyAsync(_draft.Id,_jobs.SelectedItems.OfType<JobItem>().Select(job=>job.Id).ToArray(),delta);await RefreshStatusAsync(); }
    private async Task MoveToAsync() { if (_target.SelectedItem is not QueueItem queue) return; await _app.Scheduler.AssignAsync(_jobs.SelectedItems.OfType<JobItem>().Select(item => item.Id).ToArray(), queue.Id); await RefreshStatusAsync(); }
    private async Task RefreshStatusAsync()
    {
        if (_draft is null) return; var queue = (await _app.Scheduler.SnapshotAsync()).FirstOrDefault(q => q.Id == _draft.Id); if (queue is null) return;
        var jobs = (await _app.Engine.SnapshotAsync()).ToDictionary(job => job.Id); var selected = _jobs.SelectedItems.OfType<JobItem>().Select(j => j.Id).ToHashSet(); _jobs.ItemsSource = queue.JobIds.Where(jobs.ContainsKey).Select(id => new JobItem(id, jobs[id].FileName, _locale.S(new DownloadRow(jobs[id], _locale).Job.PhaseKey))).ToArray(); foreach (var item in _jobs.Items.OfType<JobItem>().Where(j => selected.Contains(j.Id))) _jobs.SelectedItems.Add(item);
        var next = queue.NextRun?.ToLocalTime().ToString("g",_locale.Culture) ?? (queue.Schedule.Enabled ? queue.Schedule.Next(DateTimeOffset.UtcNow)?.ToLocalTime().ToString("g", _locale.Culture) ?? _locale.S("scheduler.no_next") : _locale.S("scheduler.disabled"));
        _state.Text = _locale.S(queue.IsRunning ? "scheduler.running_info" : "scheduler.idle_info", ("next", next), ("count", queue.JobIds.Count));
        _events.ItemsSource = (await _app.Scheduler.EventsAsync()).Where(e => e.QueueId == queue.Id || e.QueueId == "").Reverse().Take(50).Select(e => e.Time.ToLocalTime().ToString("HH:mm:ss") + "  " + _locale.S(e.Key) + (e.JobId is { } id && jobs.TryGetValue(id, out var job) ? " · " + job.FileName : "")).ToArray();
    }
}
