using DownloadManager.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DownloadManager.Desktop;

public sealed class MainWindow : Window
{
    private readonly App _app;
    private readonly LocaleCatalog _locale;
    private readonly ObservableCollection<DownloadRow> _rows = [];
    private readonly Dictionary<string, DownloadRow> _index = [];
    private readonly Dictionary<string, Button> _tools = [];
    private readonly Dictionary<string, ProgressWindow> _progressWindows = [];
    private readonly List<(DataGridColumn Column, string Key)> _headers = [];
    private readonly DataGrid _downloads = new() { SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow };
    private TabControl _inspector = null!;
    private readonly MenuItem _moveQueueMenu = new();
    private readonly Menu _menu = new();
    private readonly WrapPanel _ribbon = new() { Margin = new Thickness(10, 7, 10, 7) };
    private readonly TextBlock _sectionTitle = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _emptyTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _emptyPanel = new();
    private readonly Dictionary<string, TextBlock> _categoryCounts = [];
    private readonly TreeView _tree = new();
    private readonly TextBox _search = new() { Width = 220, Margin = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _count = new() { Margin = new Thickness(9, 5, 9, 5) };
    private readonly TextBlock _status = new() { Margin = new Thickness(9, 6, 9, 6) };
    private readonly StackPanel _information = new() { Margin = new Thickness(15, 11, 15, 11) };
    private readonly TextBlock _connections = new() { Margin = new Thickness(15), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _events = new() { Margin = new Thickness(15), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string,string> _queueMembers = [];
    private string _queueTreeSignature="";
    private string _filter = "all";
    private CompletionWindow? _completionWindow;
    private bool _updating;
    private bool _closing;
    private bool _canClose;

    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private uint _clipboardSequence;
    private bool _clipboardEnabled;
    private bool _clipboardDialog;


    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public MainWindow(App app)
    {
        _app = app; _locale = app.Locale;
        Width = 1320; Height = 830; MinWidth = 1060; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _locale.Bind(this, TitleProperty, "ui.download_manager"); BindingOperations.SetBinding(this, FlowDirectionProperty, new Binding(nameof(LocaleCatalog.Direction)) { Source = _locale });
        var layout = new DockPanel(); layout.SetResourceReference(Panel.BackgroundProperty, "Chrome");
        var identity = new DockPanel { Margin = new Thickness(14, 9, 14, 9) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; brand.Children.Add(DesktopIcons.Create("unfinished", 25)); var title = Ui.Label(_locale, "ui.download_manager"); title.FontSize = 17; title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(10, 0, 0, 0); brand.Children.Add(title); identity.Children.Add(brand); var identityFrame = Ui.Panel(identity); DockPanel.SetDock(identityFrame, Dock.Top); layout.Children.Add(identityFrame);
        _menu.SetResourceReference(BackgroundProperty, "Chrome"); DockPanel.SetDock(_menu, Dock.Top); layout.Children.Add(_menu);
        var ribbon = Ui.Panel(_ribbon, false); DockPanel.SetDock(ribbon, Dock.Top); layout.Children.Add(ribbon);
        var bottom = Ui.Panel(_status, false); DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        var workspace = new Grid { Margin = new Thickness(7) }; workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) }); workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var navigation = new DockPanel(); var navigationTitle = Ui.Label(_locale, "ui.categories", true); navigationTitle.Margin = new Thickness(12, 9, 12, 9); var navHeading = Ui.Panel(navigationTitle, false); DockPanel.SetDock(navHeading, Dock.Top); navigation.Children.Add(navHeading); navigation.Children.Add(_tree); workspace.Children.Add(Ui.Panel(navigation));
        var right = new DockPanel(); Grid.SetColumn(right, 2); workspace.Children.Add(right);
        var strip = new DockPanel { Margin = new Thickness(11, 7, 11, 7) };
        var find = new DockPanel { Width = 232 }; var searchIcon = DesktopIcons.Create("search", 15); searchIcon.Margin = new Thickness(7, 0, 2, 0); find.Children.Add(searchIcon); _search.Width = double.NaN; _search.Margin = new Thickness(0); _search.BorderThickness = new Thickness(0); _search.Background = Brushes.Transparent; _locale.Bind(_search, ToolTipProperty, "ui.search_files"); _locale.Bind(_search, TagProperty, "ui.search_files"); find.Children.Add(_search); var findBorder = Ui.Panel(find); findBorder.CornerRadius = new CornerRadius(4); DockPanel.SetDock(findBorder, Dock.Right); strip.Children.Add(findBorder); _sectionTitle.VerticalAlignment = VerticalAlignment.Center; strip.Children.Add(_sectionTitle); var stripBorder = Ui.Panel(strip, false); DockPanel.SetDock(stripBorder, Dock.Top); right.Children.Add(stripBorder);
        var tabs = _inspector = new TabControl { Height = 170, Visibility=Visibility.Collapsed, Margin = new Thickness(0, 7, 0, 0) };
        foreach (var (key, content) in new (string Key, FrameworkElement Content)[] { ("ui.file_information", _information), ("ui.connections", _connections), ("ui.event_log", _events) }) { var tab = new TabItem { Header = Ui.Label(_locale, key), Content = content }; tabs.Items.Add(tab); }
        DockPanel.SetDock(tabs, Dock.Bottom); right.Children.Add(tabs);
        var countBorder = Ui.Panel(_count, false); DockPanel.SetDock(countBorder, Dock.Bottom); right.Children.Add(countBorder);
        BuildColumns(); _downloads.SizeChanged += (_, _) => ResizeNameColumn(); _downloads.ItemsSource = _rows; var listLayer = new Grid(); listLayer.Children.Add(_downloads);
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; var emptyIcon = DesktopIcons.Create("unfinished", 42); emptyIcon.Opacity = 0.6; emptyIcon.Margin = new Thickness(0, 0, 0, 16); empty.Children.Add(emptyIcon); _locale.Bind(_emptyTitle, TextBlock.TextProperty, "native.empty_title"); empty.Children.Add(_emptyTitle); var emptyHint = Ui.Label(_locale, "native.empty_hint", true); emptyHint.Margin = new Thickness(0, 7, 0, 16); emptyHint.HorizontalAlignment = HorizontalAlignment.Center; empty.Children.Add(emptyHint); var emptyAdd = Ui.Button(_locale, "ui.add_url", async (_, _) => await ExecuteAsync("add")); emptyAdd.HorizontalAlignment = HorizontalAlignment.Center; empty.Children.Add(emptyAdd); _emptyPanel.Child = empty; _emptyPanel.Margin = new Thickness(1, 39, 1, 19); _emptyPanel.SetResourceReference(Border.BackgroundProperty, "Surface"); listLayer.Children.Add(_emptyPanel); right.Children.Add(listLayer); layout.Children.Add(workspace); Content = layout;
        _downloads.SelectionChanged += (_, _) => { UpdateSelection(); UpdateEnabled(); };
        _downloads.MouseDoubleClick += (_, _) => { if (_downloads.SelectedItem is DownloadRow row) ActivateDownload(row.Job); };
        _search.TextChanged += (_, _) => RefreshFilter();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { _timer.Start(); _clipboardTimer.Start(); await RefreshAsync(); };
        _clipboardTimer.Tick += async (_, _) => await CheckClipboardAsync();
        AllowDrop = true;
        DragOver += (_, args) => { args.Effects = args.Data.GetDataPresent(DataFormats.UnicodeText) || args.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; };
        Drop += async (_, args) => { if (args.Data.GetData(DataFormats.UnicodeText) is string text || args.Data.GetData(DataFormats.Text) is string) { var dropped = args.Data.GetData(DataFormats.UnicodeText) as string ?? args.Data.GetData(DataFormats.Text) as string ?? ""; await ImportLinksAsync(dropped); args.Handled = true; } };
        Closing += ClosingAsync;
        Application.Current.SessionEnding += (_,_)=>_exitRequested=true;
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == Key.V && (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) || Keyboard.Modifiers == ModifierKeys.Control && _downloads.IsKeyboardFocusWithin)) { args.Handled = true; await ExecuteAsync("clipboard"); }
            else if (args.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { args.Handled = true; await ExecuteAsync("add"); }
            else if (args.Key == Key.Delete && _downloads.IsKeyboardFocusWithin) { args.Handled = true; await ExecuteAsync("delete"); }
            else if (args.Key == Key.Enter && _downloads.IsKeyboardFocusWithin && _downloads.SelectedItem is DownloadRow row) {args.Handled=true;ActivateDownload(row.Job);}
        };
        _locale.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(LocaleCatalog.Direction)) { BuildChrome(); foreach (var row in _rows) row.RefreshLanguage(); UpdateSelection(); } };
        BuildChrome();
    }

    internal void SelectFirstForRender() { if (_rows.Count > 0) _downloads.SelectedItem = _rows[0]; }

    private void ResizeNameColumn()
    {
        if (_downloads.Columns.Count == 0 || _downloads.Columns[0].Visibility != Visibility.Visible) return;
        var other = _downloads.Columns.Skip(1).Where(column => column.Visibility == Visibility.Visible).Sum(column => column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth);
        _downloads.Columns[0].Width = Math.Max(250, _downloads.ActualWidth - other - 2);
    }

    private void BuildColumns()
    {
        var fileTemplate = new DataTemplate(); var filePanel = new FrameworkElementFactory(typeof(DockPanel)); filePanel.SetValue(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight);
        var fileGlyph = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path)); fileGlyph.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M5,2 L14,2 L20,8 L20,22 L5,22 Z M14,2 L14,8 L20,8")); fileGlyph.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.5d); fileGlyph.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.Uniform); fileGlyph.SetValue(FrameworkElement.WidthProperty, 14d); fileGlyph.SetValue(FrameworkElement.HeightProperty, 16d); fileGlyph.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 9, 0)); fileGlyph.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "Accent"); filePanel.AppendChild(fileGlyph);
        var fileName = new FrameworkElementFactory(typeof(TextBlock)); fileName.SetBinding(TextBlock.TextProperty, new Binding(nameof(DownloadRow.Name))); fileName.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); filePanel.AppendChild(fileName); fileTemplate.VisualTree = filePanel;
        var nameColumn = new DataGridTemplateColumn { CellTemplate = fileTemplate, Width = 350, MinWidth = 250 }; _locale.Bind(nameColumn, DataGridColumn.HeaderProperty, "ui.file_name"); _headers.Add((nameColumn, "ui.file_name")); _downloads.Columns.Add(nameColumn);
        Add("ui.queue", nameof(DownloadRow.Queue), 125);
        Add("ui.size", nameof(DownloadRow.Size), 86);
        Add("ui.status", nameof(DownloadRow.StateText), 112);
        var template = new DataTemplate(); var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var bar = new FrameworkElementFactory(typeof(ProgressBar)); bar.SetValue(ProgressBar.WidthProperty, 70d); bar.SetValue(ProgressBar.HeightProperty, 6d); bar.SetValue(ProgressBar.MarginProperty, new Thickness(3, 0, 7, 0)); bar.SetBinding(ProgressBar.ValueProperty, new Binding(nameof(DownloadRow.Progress)) { Mode = BindingMode.OneWay }); panel.AppendChild(bar);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(DownloadRow.ProgressText))); panel.AppendChild(label); template.VisualTree = panel;
        var progress = new DataGridTemplateColumn { CellTemplate = template, Width = 128 }; _locale.Bind(progress, DataGridColumn.HeaderProperty, "ui.progress"); _headers.Add((progress, "ui.progress")); _downloads.Columns.Add(progress);
        Add("ui.transfer_rate", nameof(DownloadRow.Speed), 98); Add("ui.time_left", nameof(DownloadRow.TimeLeft), 100); Add("ui.date_added", nameof(DownloadRow.Added), 140); Add("ui.description", nameof(DownloadRow.Description), 220); Add("ui.save_to", nameof(DownloadRow.Path), 400, true);
        foreach (var column in _downloads.Columns.Skip(7)) column.Visibility = Visibility.Collapsed;
        void Add(string key, string path, double width, bool leftToRight = false)
        {
            var column = new DataGridTextColumn { Binding = new Binding(path), Width = width };
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(DownloadRow.Description)))); style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center)); if (leftToRight) style.Setters.Add(new Setter(FlowDirectionProperty, FlowDirection.LeftToRight)); column.ElementStyle = style;
            _locale.Bind(column, DataGridColumn.HeaderProperty, key); _headers.Add((column, key)); _downloads.Columns.Add(column);
        }
    }

    private void BuildChrome()
    {
        if(_filter.StartsWith("category:",StringComparison.Ordinal)&&!((_app.Settings.Preferences["categories"] as JsonArray)?.OfType<JsonObject>().Any(row=>row["id"]?.ToString()==_filter[9..])??false))_filter="all";
        _downloads.ContextMenu?.Items.Clear();
        _menu.Items.Clear(); _ribbon.Children.Clear(); _tools.Clear(); _tree.SelectedItemChanged -= FilterChanged; _tree.Items.Clear(); _categoryCounts.Clear();
        AddMenu("ui.tasks", [("add", "ui.add_url"), ("clipboard", "native.clipboard_import"), ("batch", "ui.add_multiple_urls"), ("importFile", "native.import_file"), ("export", "native.export_links"), ("exit", "native.exit")]);
        AddMenu("ui.file", [("openFile","files.open"),("progress", "ui.download_window"), ("folder", "native.open_folder"), ("copyLinks", "native.copy_links"), ("queue", "ui.add_remove_from_queue"), ("delete", "ui.remove_from_list")]);
        AddMenu("ui.downloads", [("resume", "ui.resume"), ("stop", "ui.stop"), ("stopAll", "ui.stop_all"), ("schedule", "ui.scheduler"), ("startQueue", "ui.start_queue"), ("stopQueue", "ui.stop_queue"), ("clearDone", "ui.clear_completed")]);
        AddMenu("ui.view", [("columns", "ui.table_columns"), ("appearance", "ui.appearance_language")]);
        AddMenu("ui.tools", [("grabber", "grabber.title"), ("options", "ui.options"), ("schedule", "ui.scheduler"), ("browser", "browser.integration"), ("batch", "ui.add_multiple_urls")]);
        AddMenu("ui.help", [("about", "ui.about")]);
        foreach (var (id, key, glyph, color) in new[]
        {
            ("add","ui.add_url","\uE710","#1671C2"),("resume","ui.resume","\uE768","#36884A"),("stop","ui.stop","\uE71A","#C4403B"),("stopAll","ui.stop_all","\uE769","#C4403B"),("delete","ui.delete","\uE74D","#647387"),("clearDone","ui.clear_completed","\uE8B3","#B47A25"),("options","ui.options","\uE713","#647387"),("schedule","ui.scheduler","\uE823","#B47A25"),("startQueue","ui.start_queue","\uE8E5","#36884A"),("stopQueue","ui.stop_queue","\uE8E4","#B47A25"),("clipboard","native.clipboard_short","\uE8A5","#1671C2"),("batch","native.batch_short","\uE774","#6761AB")
        })
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };
            var topColor = color switch { "#1671C2" => "#65BAFF", "#36884A" => "#86C862", "#C4403B" => "#F09383", "#B47A25" => "#E7B864", "#6761AB" => "#A49EDC", _ => "#A7B0BC" };
            var iconTile = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(7), Child = DesktopIcons.Create(id, 25, "#FFFFFF"), Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(topColor), (Color)ColorConverter.ConvertFromString(color), 65), BorderBrush = new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.18 } };
            panel.Children.Add(iconTile);
            var label = Ui.Label(_locale, key); label.HorizontalAlignment = HorizontalAlignment.Center; label.FontSize = 11; label.TextWrapping = TextWrapping.NoWrap; label.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(label);
            var button = new Button { Content = panel, Width = 89, Height = 68, BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Padding = new Thickness(3), Margin = new Thickness(1) }; button.Click += async (_, _) => await ExecuteAsync(id); _ribbon.Children.Add(button); _tools[id] = button;
            if (id is "stopAll" or "clearDone" or "schedule") { var divider = new Border { Width = 1, Height = 46, Margin = new Thickness(5, 10, 5, 0) }; divider.SetResourceReference(Border.BackgroundProperty, "Line"); _ribbon.Children.Add(divider); }
        }
        var all = Node("all", "ui.all_downloads"); all.IsExpanded = true;
        foreach (var (filter, key) in new[] { ("archive", "ui.compressed"), ("document", "ui.documents"), ("music", "ui.music"), ("software", "ui.programs"), ("video", "ui.video") }) all.Items.Add(Node(filter, key));
        foreach(var category in (_app.Settings.Preferences["categories"] as JsonArray)?.OfType<JsonObject>()??[]){
            var id=category["id"]?.ToString()??"";if(id.Length==0||id is "general" or "archive" or "document" or "music" or "software" or "video")continue;
            var tag="category:"+id;all.Items.Add(Node(tag,"ui.category_2",category["name"]?.ToString()??id));
        }
        _tree.Items.Add(all); _tree.Items.Add(Node("unfinished", "ui.unfinished")); _tree.Items.Add(Node("done", "ui.complete")); _tree.Items.Add(Node("failed", "ui.failed")); _tree.Items.Add(Node("queue", "scheduler.queues"));
        all.IsSelected = _filter == "all"; _tree.SelectedItemChanged -= FilterChanged; _tree.SelectedItemChanged += FilterChanged;
        var context = new ContextMenu(); foreach (var (id, key) in new[] { ("openFile","files.open"),("progress", "ui.download_window"), ("resume", "ui.resume"), ("stop", "ui.stop"), ("folder", "native.open_folder"), ("copyLinks", "native.copy_links"), ("queue", "ui.add_remove_from_queue"), ("delete", "ui.remove_from_list") }) context.Items.Add(ActionMenu(id, key)); _locale.Bind(_moveQueueMenu,HeaderedItemsControl.HeaderProperty,"scheduler.move");context.Items.Add(_moveQueueMenu);_downloads.ContextMenu = context;
        UpdateEnabled();
        TreeViewItem Node(string tag, string key, string? customName = null)
        {
            var header = new DockPanel { LastChildFill = true }; var count = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 20, TextAlignment = TextAlignment.Right }; count.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk"); DockPanel.SetDock(count, Dock.Right); header.Children.Add(count); _categoryCounts[tag] = count; var icon = DesktopIcons.Create(tag, 16, tag is "done" ? "#25884B" : tag is "failed" ? "#C34B41" : tag is "archive" or "queue" ? "#B47A25" : null); icon.Margin = new Thickness(0, 0, 8, 0); header.Children.Add(icon); var label = customName is null?Ui.Label(_locale, key):new TextBlock{Text=customName,ToolTip=customName}; label.TextWrapping = TextWrapping.NoWrap;if(customName is not null){label.MaxWidth=135;label.TextTrimming=TextTrimming.CharacterEllipsis;} header.Children.Add(label); return new TreeViewItem { Header = header, Tag = tag, IsSelected = tag == _filter };
        }
        void AddMenu(string key, (string Id, string Key)[] items) { var parent = new MenuItem { Header = _locale.S(key) }; foreach (var item in items) parent.Items.Add(ActionMenu(item.Id, item.Key)); _menu.Items.Add(parent); }
    }

    private MenuItem ActionMenu(string id, string key) {
        var item = new MenuItem { Header = _locale.S(key) };
        if(id is "startQueue" or "stopQueue") {item.Items.Add(new MenuItem{Header=_locale.S("ui.main_queue")});item.SubmenuOpened+=async(_,_)=>{item.Items.Clear();foreach(var queue in await _app.Scheduler.SnapshotAsync()){var queueId=queue.Id;var child=new MenuItem{Header=string.IsNullOrEmpty(queue.Name)?_locale.S("ui.main_queue"):queue.Name};child.Click+=async(_,args)=>{args.Handled=true;await (id=="startQueue"?_app.Scheduler.StartAsync(queueId):_app.Scheduler.StopAsync(queueId));await RefreshAsync();};item.Items.Add(child);}};}
        else item.Click += async (_, _) => await ExecuteAsync(id);return item;
    }
    private void FilterChanged(object sender, RoutedPropertyChangedEventArgs<object> args) { if (_tree.SelectedItem is TreeViewItem { Tag: string tag }) { _filter = tag; RefreshFilter(); } }
    private void RefreshFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_rows); view.Filter = item => item is DownloadRow row && row.Name.Contains(_search.Text, StringComparison.OrdinalIgnoreCase) && (_filter switch { "all" => true, "unfinished" => row.Job.State != DownloadState.Completed, "done" => row.Job.State == DownloadState.Completed, "failed" => row.Job.State == DownloadState.Failed, "queue" => row.Job.InQueue, _ => _filter.StartsWith("queue:",StringComparison.Ordinal) ? _queueMembers.GetValueOrDefault(row.Job.Id)==_filter[6..] : Classify(row.Name) == _filter }); view.Refresh();
        _count.Text = _locale.S("downloads.file_count", ("count", view.Cast<object>().Count()));
        _emptyPanel.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        _sectionTitle.Text = _locale.S(_filter switch { "all" => "ui.all_downloads", "unfinished" => "ui.unfinished", "done" => "ui.complete", "failed" => "ui.failed", "queue" => "ui.main_queue", "archive" => "ui.compressed", "document" => "ui.documents", "music" => "ui.music", "video" => "ui.video", _ => "ui.programs" });
        if(_filter.StartsWith("category:",StringComparison.Ordinal)) _sectionTitle.Text=(_app.Settings.Preferences["categories"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(row=>row["id"]?.ToString()==_filter[9..])?["name"]?.ToString()??_locale.S("ui.all_downloads");
        if(_filter.StartsWith("queue:",StringComparison.Ordinal)) _sectionTitle.Text = _rows.FirstOrDefault(row=>_queueMembers.GetValueOrDefault(row.Job.Id)==_filter[6..])?.Queue ?? _locale.S("ui.main_queue");
        foreach (var (category, label) in _categoryCounts) label.Text = _rows.Count(row => category switch { "all" => true, "unfinished" => row.Job.State != DownloadState.Completed, "done" => row.Job.State == DownloadState.Completed, "failed" => row.Job.State == DownloadState.Failed, "queue" => row.Job.InQueue, _ => Classify(row.Name) == category }).ToString(_locale.Culture);
    }

    public async Task RefreshAsync()
    {
        if (_updating || _closing) return; _updating = true;
        try
        {
            var jobs = await _app.Engine.SnapshotAsync(); var ids = jobs.Select(job => job.Id).ToHashSet();
            foreach (var row in _rows.Where(row => !ids.Contains(row.Job.Id)).ToArray()) { _rows.Remove(row); _index.Remove(row.Job.Id); }
            foreach (var job in jobs) { if (_index.TryGetValue(job.Id, out var row)) row.Apply(job); else { var created = new DownloadRow(job, _locale); _index[job.Id] = created; _rows.Insert(0, created); } }
            await RefreshQueuesAsync(); RefreshFilter(); UpdateSelection(); UpdateEnabled();
            foreach (var ticket in await _app.Scheduler.DrainCompletionsAsync()) { _completionWindow?.Close(); _completionWindow = new CompletionWindow(_app, ticket) { Owner = this }; _completionWindow.Show(); }
            _status.Text = _locale.S("native.live_status", ("count", jobs.Count(job => job.State == DownloadState.Running)), ("speed", DownloadRow.FormatBytes((long)jobs.Sum(job => job.BytesPerSecond))));

        }
        catch (ObjectDisposedException) when (_closing) { }
        finally { _updating = false; }
    }

    private DownloadRow[] Selection() => _downloads.SelectedItems.OfType<DownloadRow>().ToArray();
    private void UpdateEnabled()
    {
        var selection = Selection(); _moveQueueMenu.IsEnabled=selection.Length>0;
        foreach (var pair in _tools) pair.Value.IsEnabled = pair.Key switch { "resume" => selection.Any(row => row.Job.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Queued), "stop" => selection.Any(row => row.Job.State == DownloadState.Running), "delete" => selection.Length > 0, "stopAll" => _rows.Any(row => row.Job.State == DownloadState.Running), "clearDone" => _rows.Any(row => row.Job.State == DownloadState.Completed), _ => true };
    }
    private void UpdateSelection()
    {
        if (_downloads.SelectedItem is not DownloadRow row) { _inspector.Visibility=Visibility.Collapsed; _information.Children.Clear(); _information.Children.Add(Ui.Label(_locale, "ui.select_a_file", true)); _connections.Text = _locale.S("native.single_connection"); _events.Text = ""; return; }
        _inspector.Visibility=Visibility.Visible; _information.Children.Clear(); var heading = new TextBlock { Text = row.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 0, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis }; _information.Children.Add(heading);
        var properties = new Grid(); properties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) }); properties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (key, value) in new[] { ("ui.url", row.Job.Url), ("ui.save_to", row.Path), ("ui.downloaded", DownloadRow.FormatBytes(row.Job.ReceivedBytes) + " / " + row.Size) })
        {
            var index = properties.RowDefinitions.Count; properties.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var label = Ui.Label(_locale, key, true); label.Margin = new Thickness(0, 3, 10, 3); Grid.SetRow(label, index); properties.Children.Add(label); var text = new TextBlock { Text = value, ToolTip = value, TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 3, 0, 3) }; Grid.SetRow(text, index); Grid.SetColumn(text, 1); properties.Children.Add(text);
        }
        _information.Children.Add(properties);
        _connections.Text = _locale.S(row.Job.Segments.Count > 1 ? "native.segment_mode" : "native.single_connection", ("count", row.Job.Segments.Count)) + "\n\n" + _locale.S("ui.resume_capability") + " " + _locale.S(row.Job.ResumeSupported ? "ui.supported" : "native.unsupported") + "\n" + _locale.S("ui.transfer_rate") + ": " + row.Speed;
        _events.Text = row.Job.ErrorKey is { } error ? _locale.S(error, ("detail", row.Job.ErrorDetail)) : row.StateText;
    }

    private readonly ClipboardUrlMemory _clipboardUrl = new();
    private async Task ExecuteAsync(string id)
    {
        try
        {
            var selection = Selection();
            switch (id)
            {
                case "add":
                    var add = new AddDownloadWindow(_locale, _app.Settings.DownloadDirectory, await QueueChoicesAsync()) { Owner = this };
                    add.Inspector = (url, token) => LinkInspector.InspectAsync(url, _app.Settings.TransferPolicy(), cancellation: token);
                    var autoFolder=_app.Settings.DownloadDirectory;var categoryPolicy=_app.Settings.TransferPolicy();
                    add.FileNameBox.TextChanged+=(_,_)=>{if(add.FolderBox.Text.Equals(autoFolder,StringComparison.OrdinalIgnoreCase)){autoFolder=DownloadDestination.Resolve(_app.Settings.DownloadDirectory,add.FileNameBox.Text,categoryPolicy);add.FolderBox.Text=autoFolder;}};
                    var clipboardUrl=_clipboardUrl.Read(ImportLinksWindow.ReadClipboardText);
                    if(clipboardUrl is not null){add.UrlBox.Text=clipboardUrl;add.UrlBox.ToolTip=_locale.S(_clipboardUrl.UsedCached?"clipboard.last_url":"clipboard.current_url");}
                    if (add.ShowDialog() == true) { var job = await _app.Engine.AddAsync(add.UrlBox.Text.Trim(), add.FolderBox.Text.Trim(), add.FileNameBox.Text, false, add.DescriptionBox.Text,adaptive:add.Adaptive); await _app.Engine.SetCompletionOptionsAsync(job.Id, Preference("showComplete", true), Preference("closeProgress", false)); await _app.Scheduler.AssignAsync([job.Id],add.QueueId);if(add.StartImmediately)await _app.Scheduler.ResumeSelectedAsync([job.Id]); await RefreshAsync(); _downloads.SelectedItem = _index.GetValueOrDefault(job.Id); if (add.StartImmediately) OpenProgressForStart(job.Id); }
                    break;
                case "batch": await ImportLinksAsync(mode:LinkImportMode.Batch); break;
                case "clipboard":
                    try { var clipboardLinks=ImportLinksWindow.ReadClipboardText();if(clipboardLinks.Length==0)AppDialog.Show(this,_locale.S("import.no_links"),Title);else await ImportLinksAsync(clipboardLinks,mode:LinkImportMode.Clipboard); }
                    catch (ExternalException) { AppDialog.Show(this, _locale.S("native.clipboard_busy"), Title); }
                    break;
                case "importFile":
                    var picker = new OpenFileDialog { Filter = _locale.S("native.text_file_filter") };
                    if (picker.ShowDialog(this) == true) { if (new FileInfo(picker.FileName).Length > LinkImport.MaximumTextLength) throw new ArgumentException("native.import_too_large"); await ImportLinksAsync(await File.ReadAllTextAsync(picker.FileName)); }
                    break;
                case "export":
                    var save = new SaveFileDialog { Filter = _locale.S("native.text_file_filter"), DefaultExt = ".txt", FileName = "downloads.txt" };
                    if (save.ShowDialog(this) == true) await File.WriteAllLinesAsync(save.FileName, (selection.Length > 0 ? selection : _rows.ToArray()).Select(row => row.Job.Url));
                    break;
                case "copyLinks": if (selection.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, selection.Select(row => row.Job.Url))); break;
                case "resume": await _app.Scheduler.ResumeSelectedAsync(selection.Select(row=>row.Job.Id)); break;
                case "stop": foreach (var row in selection) if (row.Job.State == DownloadState.Running) await _app.Engine.PauseAsync(row.Job.Id); break;
                case "stopAll": await _app.Scheduler.StopAllAsync(); foreach (var row in _rows.ToArray()) if (row.Job.State == DownloadState.Running) await _app.Engine.PauseAsync(row.Job.Id); break;
                case "delete":
                    if(selection.Length>0){var jobs=selection.Select(r=>r.Job).ToArray();if(!DownloadRemoval.NeedsDiskChoice(jobs))await DownloadRemoval.ExecuteAsync(_app.Engine,jobs,false);else{var remove=new RemoveDownloadsWindow(_locale,jobs){Owner=this};if(remove.ShowDialog()==true)await DownloadRemoval.ExecuteAsync(_app.Engine,jobs,remove.DeleteFiles);}}break;
                case "clearDone": foreach (var row in _rows.Where(row => row.Job.State == DownloadState.Completed).ToArray()) await _app.Engine.RemoveAsync(row.Job.Id); break;
                case "queue": foreach (var row in selection) { if(row.Job.InQueue)await _app.Scheduler.UnassignAsync([row.Job.Id]);else await _app.Scheduler.AssignAsync([row.Job.Id],"main"); } break;
                case "progress": if (selection.FirstOrDefault() is { } current) OpenProgress(current.Job.Id); break;
                case "openFile": if(selection.FirstOrDefault() is {} chosenFile)DownloadFileActions.Open(this,_locale,chosenFile.Job,false);break;
                case "folder": if (selection.FirstOrDefault() is { } chosen) DownloadFileActions.Open(this,_locale,chosen.Job,true); break;
                case "startQueue": await _app.Scheduler.StartAsync("main"); break;
                case "stopQueue": await _app.Scheduler.StopAsync("main"); break;
                case "options": OpenSettings("general"); break;
                case "schedule": new SchedulerWindow(_app) { Owner = this }.ShowDialog(); break;
                case "browser": new BrowserIntegrationWindow(_app) { Owner = this }.ShowDialog(); break;
                case "appearance": OpenSettings("appearance"); break;
                case "columns": OpenColumns(); break;
                case "grabber": new GrabberWindow(_app,text=>ImportLinksAsync(text)){Owner=this}.ShowDialog(); break;
                case "about": new AboutWindow(_locale) { Owner = this }.ShowDialog(); break;
                case "exit": RequestExit(); break;
            }
            await RefreshAsync();
        }
        catch (Exception exception) { AppDialog.Show(this, _locale.S(exception is ArgumentException ? "errors.invalid_url" : "errors.operation", ("detail", exception.GetType().Name)), Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task<IReadOnlyList<QueueChoice>> QueueChoicesAsync() => (await _app.Scheduler.SnapshotAsync()).Select(q=>new QueueChoice(q.Id,string.IsNullOrEmpty(q.Name)?_locale.S("ui.main_queue"):q.Name)).ToArray();
    private async Task RefreshQueuesAsync()
    {
        var queues=await _app.Scheduler.SnapshotAsync();_queueMembers.Clear();foreach(var queue in queues)foreach(var id in queue.JobIds)_queueMembers[id]=queue.Id;
        foreach(var row in _rows)row.SetQueueName(queues.FirstOrDefault(q=>q.Id==_queueMembers.GetValueOrDefault(row.Job.Id)) is { } queue ? string.IsNullOrEmpty(queue.Name)?_locale.S("ui.main_queue"):queue.Name : "");
        var signature=string.Join("|",queues.Select(q=>q.Id+":"+q.Name+":"+q.JobIds.Count+":"+q.IsRunning))+_locale.Code;
        if(signature==_queueTreeSignature)return;
        _moveQueueMenu.Items.Clear();foreach(var queue in queues){var move=new MenuItem{Header=string.IsNullOrEmpty(queue.Name)?_locale.S("ui.main_queue"):queue.Name};move.Click+=async(_,_)=>{await _app.Scheduler.AssignAsync(Selection().Select(row=>row.Job.Id).ToArray(),queue.Id);await RefreshAsync();};_moveQueueMenu.Items.Add(move);}_queueTreeSignature=signature;
        if(_tree.Items.OfType<TreeViewItem>().FirstOrDefault(node=>Equals(node.Tag,"queue")) is { } root){root.Items.Clear();foreach(var queue in queues){var label=(string.IsNullOrEmpty(queue.Name)?_locale.S("ui.main_queue"):queue.Name)+" ("+queue.JobIds.Count+")";var node=new TreeViewItem{Header=label,Tag="queue:"+queue.Id};var menu=new ContextMenu();menu.Items.Add(QueueAction("ui.start_queue",()=>_app.Scheduler.StartAsync(queue.Id)));menu.Items.Add(QueueAction("ui.stop_queue",()=>_app.Scheduler.StopAsync(queue.Id)));var edit=new MenuItem{Header=_locale.S("ui.scheduler")};edit.Click+=(_,_)=>new SchedulerWindow(_app,queue.Id){Owner=this}.ShowDialog();menu.Items.Add(edit);node.ContextMenu=menu;root.Items.Add(node);}root.IsExpanded=true;}
    }
    private MenuItem QueueAction(string key,Func<Task> action){var item=new MenuItem{Header=_locale.S(key)};item.Click+=async(_,_)=>{try{await action();await RefreshAsync();}catch(Exception error){AppDialog.Show(this,_locale.S("errors.operation",("detail",error.GetType().Name)),Title);}};return item;}
    private bool Preference(string key, bool fallback) => bool.TryParse(_app.Settings.Preferences[key]?.ToString(), out var value) ? value : fallback;
    private void ActivateDownload(DownloadJob job){if(job.State==DownloadState.Completed)DownloadFileActions.Open(this,_locale,job,false);else OpenProgress(job.Id);}
    private void OpenProgressForStart(string id)
    {
        var mode = _app.Settings.Preferences["progressStart"]?.ToString() ?? "normal";
        if (mode is "hidden" or "tray") return;
        OpenProgress(id); if (mode == "minimized" && _progressWindows.TryGetValue(id, out var window)) window.WindowState = WindowState.Minimized;
    }
    internal void ShowDownloadProgress(string id){if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;OpenProgress(id); }
    private void OpenProgress(string id)
    {
        if (_progressWindows.TryGetValue(id, out var existing)) { existing.Show(); if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
        var progress = new ProgressWindow(_app.Engine, _locale, id, new ProgressViewOptions(Preference("progressSpeedTab", true), Preference("progressCompletionTab", true), Preference("progressHideTab", true)),resumeId=>_app.Scheduler.ResumeSelectedAsync([resumeId])) { Owner = this }; _progressWindows[id] = progress; progress.Closed += (_, _) => _progressWindows.Remove(id); progress.Show();
    }
    private void OpenSettings(string section) => new SettingsWindow(_app.Settings, _locale, _app.Schema, _app.ApplySettings, section) { Owner = this }.ShowDialog();

    private async Task<int> ImportLinksAsync(string? text = null, BrowserRequest? handoff = null, CancellationToken cancellation = default, LinkImportMode mode = LinkImportMode.Review)
    {
        var window = new ImportLinksWindow(_locale, _app.Settings.DownloadDirectory, _rows.Select(row => row.Job.Url), text, await QueueChoicesAsync(),mode) { Owner = this };
        window.Inspector = (url, token) => LinkInspector.InspectAsync(url, _app.Settings.TransferPolicy(), cancellation: token);
        if (handoff is not null) BrowserWindowActivation.Prepare(window);
        using var registration=cancellation.Register(()=>Dispatcher.BeginInvoke(()=>{if(window.IsVisible)window.DialogResult=false;}));
        if(cancellation.IsCancellationRequested || window.ShowDialog() != true)return 0;
        if(handoff is not null){var count=await _app.BrowserTransfers.PrepareAsync(handoff,window.SelectedUrls,window.Folder,window.QueueId,window.StartImmediately,Preference("showComplete",true),Preference("closeProgress",false),cancellation);if(handoff.Action=="import")await _app.BrowserTransfers.ExistingAsync(new BrowserRequest{Id=handoff.Id,Action="commit"});await RefreshAsync();return count;}
        var added = new List<string>();
        try
        {
            foreach(var url in window.SelectedUrls){cancellation.ThrowIfCancellationRequested();var job=await _app.Engine.AddAsync(url,window.Folder,null,false,holdForBrowser:false);await _app.Engine.SetCompletionOptionsAsync(job.Id,Preference("showComplete",true),Preference("closeProgress",false));added.Add(job.Id);}
            cancellation.ThrowIfCancellationRequested();
            if(added.Count>0){await _app.Scheduler.AssignAsync(added,window.QueueId);if(window.StartImmediately)await _app.Scheduler.StartSelectedAsync(window.QueueId,added);}
        }
        catch { foreach(var id in added)await _app.Engine.RemoveAsync(id);throw; }
        await RefreshAsync(); return added.Count;
    }
    private async Task<int> ReviewSingleBrowserAsync(BrowserRequest request,CancellationToken cancellation)
    {
        var window=BrowserDownloadReview.CreateSingle(_locale,_app.Settings,await QueueChoicesAsync(),request);window.Owner=this;
        using var registration=cancellation.Register(()=>Dispatcher.BeginInvoke(()=>{if(window.IsVisible)window.DialogResult=false;}));
        if(cancellation.IsCancellationRequested||window.ShowDialog()!=true)return 0;
        var accepted=BrowserDownloadReview.AcceptedRequest(request,window);
        var count=await _app.BrowserTransfers.PrepareAsync(accepted,accepted.Urls,window.FolderBox.Text.Trim(),window.QueueId,window.StartImmediately,Preference("showComplete",true),Preference("closeProgress",false),cancellation,window.FileNameBox.Text,window.DescriptionBox.Text);
        if(request.Action=="import")await _app.BrowserTransfers.ExistingAsync(new BrowserRequest{Id=request.Id,Action="commit"});
        await RefreshAsync();if(window.StartImmediately&&request.Action=="import"&&_app.BrowserHandoffs.Find(request.Id)?.JobIds.FirstOrDefault() is {} id)OpenProgressForStart(id);return count;
    }
    private readonly Dictionary<string, BrowserReply> _browserReplies = [];
    public async Task<BrowserReply> HandleBrowserAsync(BrowserRequest request, CancellationToken cancellation = default)
    {
        request.Validated();
        if (_closing) return new(request.Id, false, "closing");
        if (request.Action == "ping") return new(request.Id, true, "ready");
        if(request.Action is "prepare" or "commit" or "abort") { if(await _app.BrowserTransfers.ExistingAsync(request) is { } existing)return existing; }
        if (_browserReplies.TryGetValue(request.Action+request.Id, out var cached)) return cached;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        if (request.Action == "activate") { BrowserWindowActivation.BringToFront(this); return new(request.Id, true, "ready"); }
        var count = BrowserDownloadReview.IsSingle(request)?await ReviewSingleBrowserAsync(request,cancellation):await ImportLinksAsync(string.Join(Environment.NewLine,request.Urls),request,cancellation);
        var reply = new BrowserReply(request.Id, count > 0, count > 0 ? "accepted" : "cancelled", count);
        if (_browserReplies.Count >= 200) _browserReplies.Remove(_browserReplies.Keys.First()); _browserReplies[request.Action+request.Id] = reply; return reply;
    }
    private async Task CheckClipboardAsync()
    {
        var enabled = bool.TryParse(_app.Settings.Preferences["clipboard"]?.ToString(), out var active) && active;
        if (!enabled) { _clipboardEnabled = false; return; }
        var sequence = GetClipboardSequenceNumber();
        if (!_clipboardEnabled) { _clipboardEnabled = true; _clipboardSequence = sequence; return; }
        if (_clipboardDialog || !IsActive || sequence == 0 || sequence == _clipboardSequence) return;
        try
        {
            var text = ImportLinksWindow.ReadClipboardText(); _clipboardSequence = sequence;
            var extensions = (_app.Settings.Preferences["extensions"]?.ToString() ?? "").Split([' ', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(value => value.TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existing = _rows.Select(row => LinkImport.Normalize(row.Job.Url)).ToHashSet(StringComparer.Ordinal);
            var links = LinkImport.Extract(text).Links.Where(link => !existing.Contains(link.Url) && extensions.Contains(Path.GetExtension(new Uri(link.Url).AbsolutePath).TrimStart('.'))).ToArray();
            if (links.Length == 0) return;
            _clipboardDialog = true; await ImportLinksAsync(string.Join(Environment.NewLine, links.Select(link => link.Url)));
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException) { }
        finally { _clipboardDialog = false; }
    }
    private void OpenColumns()
    {
        var panel = new StackPanel { Margin = new Thickness(18) }; var window = new Window { Owner = this, Title = _locale.S("ui.table_columns"), Width = 340, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, FlowDirection = _locale.Direction };
        foreach (var (column, key) in _headers) { var check = new CheckBox { Content = Ui.Label(_locale, key), IsChecked = column.Visibility == Visibility.Visible }; check.Checked += (_, _) => { column.Visibility = Visibility.Visible; ResizeNameColumn(); }; check.Unchecked += (_, _) => { column.Visibility = Visibility.Collapsed; ResizeNameColumn(); }; panel.Children.Add(check); } panel.Children.Add(Ui.Button(_locale, "ui.close", (_, _) => window.Close())); window.ShowDialog();
    }
    private bool _exitRequested;
    public void RequestExit(){_exitRequested=true;Close();}
    private async void ClosingAsync(object? sender, CancelEventArgs args)
    {
        if (_canClose || _app.EngineIsDisposed) return;
        if(!_exitRequested&&(_app.Settings.Preferences["closeToTray"]?.ToString().Equals("false",StringComparison.OrdinalIgnoreCase)!=true)){
            args.Cancel=true;if(!_app.HideMainToTray())WindowState=WindowState.Minimized;return;
        }
        args.Cancel = true; if (_closing) return; _closing = true; _timer.Stop(); _clipboardTimer.Stop(); IsEnabled = false;
        _completionWindow?.Close(); foreach (var progress in _progressWindows.Values.ToArray()) progress.Close();
        await Task.Yield();
        await _app.DisposeEngineAsync(); _canClose = true; Close(); Application.Current.Shutdown();
    }
    private string Classify(string name)
    {
        var extension=Path.GetExtension(name).TrimStart('.');
        foreach(var row in (_app.Settings.Preferences["categories"] as JsonArray)?.OfType<JsonObject>()??[]){
            if(!(row["extensions"]?.ToString()??"").Split([' ',',',';'],StringSplitOptions.RemoveEmptyEntries).Any(value=>value.TrimStart('*','.').Equals(extension,StringComparison.OrdinalIgnoreCase)))continue;
            var id=row["id"]?.ToString()??"";if(id.Length>0)return id is "general" or "archive" or "document" or "music" or "software" or "video"?id:"category:"+id;
        }
        return ClassifyDefault(name);
    }
    private static string ClassifyDefault(string name) => Path.GetExtension(name).ToLowerInvariant() switch { ".zip" or ".rar" or ".7z" => "archive", ".pdf" or ".doc" or ".docx" or ".txt" or ".xlsx" => "document", ".mp3" or ".flac" or ".wav" or ".m4a" => "music", ".mp4" or ".mkv" or ".webm" or ".avi" => "video", _ => "software" };
}


