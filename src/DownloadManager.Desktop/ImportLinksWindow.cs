using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DownloadManager.Core;
using Microsoft.Win32;

namespace DownloadManager.Desktop;

public sealed class ImportLinkRow : INotifyPropertyChanged
{
    private bool _selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public required ImportedLink Link { get; init; }
    public bool AlreadyAdded { get; init; }
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public string Url => Link.Url;
    public int Position { get; private set; }
    public void SetPosition(int value) { Position=value; PropertyChanged?.Invoke(this,new(nameof(Position))); }
    private string? _name;
    private string _size = "", _status = "";
    public string Name => _name ?? Link.FileName;
    public string Size => _size;
    public string Status => _status;
    public string CheckState { get; private set; } = "";
    public void Apply(LinkMetadata result, LocaleCatalog locale)
    {
        CheckState = result.State; _name = result.FileName; _size = result.Size is {} size ? DownloadRow.FormatBytes(size) : locale.S("inspect.unknown");
        _status = locale.S("inspect." + result.State) + (result.StatusCode is {} code && result.State != "ready" ? " · HTTP " + code : "");
        foreach (var property in new[] { nameof(Name), nameof(Size), nameof(Status) }) PropertyChanged?.Invoke(this, new(property));
    }
}

public enum LinkImportMode { Review, Clipboard, Batch }

public sealed class ImportLinksWindow : Window
{
    private readonly LocaleCatalog _locale;
    private readonly HashSet<string> _existing;
    private readonly TextBox _single = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _folder = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _pattern = new() { FlowDirection = FlowDirection.LeftToRight, MinWidth = 280 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _empty;
    private readonly ObservableCollection<ImportLinkRow> _links = [];
    private readonly DataGrid _list;
    private readonly Button _start, _later, _check;
    public Func<string, CancellationToken, Task<LinkMetadata>>? Inspector { get; set; }
    private CancellationTokenSource? _checkCancellation;
    private readonly ComboBox _queue = new();
    public LinkImportMode Mode { get; }
    public string QueueId => (_queue.SelectedItem as QueueChoice)?.Id ?? "main";
    public bool StartImmediately { get; private set; }
    public string Folder => _folder.Text.Trim();
    public string[] SelectedUrls => _links.Where(row => row.Selected).Select(row => row.Url).ToArray();

    public ImportLinksWindow(LocaleCatalog locale, string folder, IEnumerable<string> existing, string? text = null, IReadOnlyList<QueueChoice>? queues = null, LinkImportMode mode = LinkImportMode.Review)
    {
        _locale=locale;Mode=mode;_existing=existing.Select(LinkImport.Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);_folder.Text=folder;
        var titleKey=mode switch{LinkImportMode.Clipboard=>"import.clipboard_title",LinkImportMode.Batch=>"import.batch_title",_=>"native.import_links"};
        Title=locale.S(titleKey);Width=1000;Height=740;MinWidth=900;MinHeight=580;FlowDirection=locale.Direction;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(22)};root.SetResourceReference(Panel.BackgroundProperty,"Chrome");
        var bottom=new StackPanel();if(queues is not null){_queue.ItemsSource=queues;_queue.SelectedIndex=0;bottom.Children.Add(Ui.Field(locale,"ui.queue",_queue));}
        var folderLine=new DockPanel();var browse=Ui.Button(locale,"native.browse",(_,_)=>{var picker=new OpenFolderDialog{InitialDirectory=Directory.Exists(Folder)?Folder:Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};if(picker.ShowDialog(this)==true)_folder.Text=picker.FolderName;});DockPanel.SetDock(browse,Dock.Right);folderLine.Children.Add(browse);folderLine.Children.Add(_folder);bottom.Children.Add(Ui.Field(locale,"ui.save_to",folderLine));
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};actions.Children.Add(Ui.Button(locale,"ui.cancel",(_,_)=>DialogResult=false));_later=Ui.Button(locale,"ui.download_later",(_,_)=>Finish(false));_start=Ui.Button(locale,"ui.start_download",(_,_)=>Finish(true));_start.SetResourceReference(BackgroundProperty,"Selection");_start.SetResourceReference(BorderBrushProperty,"Accent");actions.Children.Add(_later);actions.Children.Add(_start);bottom.Children.Add(actions);DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        var top=new StackPanel();var heading=Ui.Label(locale,titleKey);heading.FontSize=21;heading.FontWeight=FontWeights.SemiBold;top.Children.Add(heading);
        var hint=Ui.Label(locale,mode==LinkImportMode.Batch?"import.batch_hint":"import.clean_hint",true);hint.TextWrapping=TextWrapping.Wrap;hint.Margin=new Thickness(0,5,0,12);top.Children.Add(hint);
        var sources=new WrapPanel();sources.Children.Add(Ui.Button(locale,"native.paste_links",(_,_)=>ReadClipboard()));sources.Children.Add(Ui.Button(locale,"native.import_file",(_,_)=>ImportFile()));top.Children.Add(sources);
        var singleLine=new DockPanel{Margin=new Thickness(0,7,0,7)};var add=Ui.Button(locale,"import.add_link",(_,_)=>AddSingle());DockPanel.SetDock(add,Dock.Right);singleLine.Children.Add(add);_single.Tag=locale.S("import.single_hint");singleLine.Children.Add(_single);top.Children.Add(singleLine);
        _single.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){AddSingle();e.Handled=true;}};
        DataObject.AddPastingHandler(_single,(_,e)=>{try{var pasted=e.DataObject.GetData(DataFormats.UnicodeText) as string??e.DataObject.GetData(DataFormats.Text) as string??"";var html=e.DataObject.GetData(DataFormats.Html) as string;AppendResult(LinkImport.FromClipboard(pasted,html));_single.Clear();}catch(Exception error)when(error is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException){_summary.Text=locale.S("native.import_too_large");}e.CancelCommand();});
        if(mode==LinkImportMode.Batch){
            var patternLine=new DockPanel{Margin=new Thickness(0,4,0,4)};var generate=Ui.Button(locale,"native.generate_links",(_,_)=>{try{AppendText(string.Join("\n",LinkImport.ExpandRange(_pattern.Text.Trim())));}catch(ArgumentException error){_summary.Text=locale.S(error.Message);}});DockPanel.SetDock(generate,Dock.Right);patternLine.Children.Add(generate);_pattern.Tag=locale.S("native.pattern_hint");patternLine.Children.Add(_pattern);top.Children.Add(Ui.Field(locale,"import.pattern",patternLine));
        }
        var selection=new WrapPanel();selection.Children.Add(Ui.Button(locale,"native.select_new_links",(_,_)=>{foreach(var row in _links)row.Selected=!row.AlreadyAdded;}));selection.Children.Add(Ui.Button(locale,"native.select_none",(_,_)=>{foreach(var row in _links)row.Selected=false;}));_check=Ui.Button(locale,"inspect.check_links",async(_,_)=>await CheckLinksAsync());selection.Children.Add(_check);top.Children.Add(selection);_summary.SetResourceReference(TextBlock.ForegroundProperty,"MutedInk");top.Children.Add(_summary);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        _list=new DataGrid{ItemsSource=_links,IsReadOnly=false,CanUserAddRows=false,CanUserDeleteRows=false,CanUserSortColumns=false,EnableColumnVirtualization=false,SelectionMode=DataGridSelectionMode.Extended};
        var tick=new FrameworkElementFactory(typeof(CheckBox));tick.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);tick.SetValue(FrameworkElement.MarginProperty,new Thickness(0));tick.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,new Binding(nameof(ImportLinkRow.Selected)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});_list.Columns.Add(new DataGridTemplateColumn{CellTemplate=new DataTemplate{VisualTree=tick},Width=34});
        void Column(string property,string key,DataGridLength width,double min=0){var column=new DataGridTextColumn{Binding=new Binding(property),IsReadOnly=true,Width=width,MinWidth=min};locale.Bind(column,DataGridColumn.HeaderProperty,key);var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(FlowDirectionProperty,FlowDirection.LeftToRight));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));style.Setters.Add(new Setter(ToolTipProperty,new Binding(property)));column.ElementStyle=style;_list.Columns.Add(column);}
        Column(nameof(ImportLinkRow.Position),"import.number",36);Column(nameof(ImportLinkRow.Name),"ui.file_name",165);Column(nameof(ImportLinkRow.Url),"ui.url",new DataGridLength(1,DataGridLengthUnitType.Star),300);Column(nameof(ImportLinkRow.Size),"inspect.size",95);Column(nameof(ImportLinkRow.Status),"inspect.status",170);
        var remove=new FrameworkElementFactory(typeof(Button));remove.SetValue(ContentControl.ContentProperty,"×");remove.SetValue(FrameworkElement.ToolTipProperty,locale.S("import.remove_link"));remove.SetValue(Control.PaddingProperty,new Thickness(0));remove.SetValue(FrameworkElement.MinWidthProperty,24d);remove.SetValue(FrameworkElement.MarginProperty,new Thickness(0));remove.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,_)=>{if(((FrameworkElement)sender).DataContext is ImportLinkRow row)RemoveRow(row);}));_list.Columns.Add(new DataGridTemplateColumn{CellTemplate=new DataTemplate{VisualTree=remove},Width=34});
        var layer=new Grid();layer.Children.Add(_list);_empty=Ui.Label(locale,"import.empty",true);_empty.TextWrapping=TextWrapping.Wrap;_empty.MaxWidth=500;_empty.HorizontalAlignment=HorizontalAlignment.Center;_empty.VerticalAlignment=VerticalAlignment.Center;_empty.IsHitTestVisible=false;layer.Children.Add(_empty);root.Children.Add(layer);Content=root;
        CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.ApplicationCommands.Paste,(_,e)=>{ReadClipboard();e.Handled=true;}));
        Closed+=(_,_)=>_checkCancellation?.Cancel();if(text is not null)AppendText(text);else UpdateButtons();
    }
    private void AddSingle()
    {
        var value=_single.Text.Trim();if(value.Length==0)return;
        if(LinkImport.Normalize(value) is null || value.Any(char.IsWhiteSpace)){_summary.Text=_locale.S("errors.invalid_url");return;}
        AppendText(value);_single.Clear();
    }
    public void ReadClipboard()
    {
        try{AppendResult(ReadClipboardLinks());}
        catch(ExternalException){_summary.Text=_locale.S("native.clipboard_busy");}
        catch(Exception error)when(error is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException){_summary.Text=_locale.S("native.import_too_large");}
    }
    internal static LinkImportResult ReadClipboardLinks()
    {
        var plain=Clipboard.ContainsText(TextDataFormat.UnicodeText)?Clipboard.GetText(TextDataFormat.UnicodeText):"";
        var html=Clipboard.ContainsText(TextDataFormat.Html)?Clipboard.GetText(TextDataFormat.Html):null;
        return LinkImport.FromClipboard(plain,html);
    }
    internal static string ReadClipboardText()=>string.Join("\n",ReadClipboardLinks().Links.Select(link=>link.Url));
    private void ImportFile()
    {
        var picker=new OpenFileDialog{Filter=_locale.S("native.text_file_filter")};if(picker.ShowDialog(this)!=true)return;
        try{if(new FileInfo(picker.FileName).Length>LinkImport.MaximumTextLength)throw new ArgumentException("native.import_too_large");AppendText(File.ReadAllText(picker.FileName));}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException){_summary.Text=_locale.S(error is ArgumentException?"native.import_too_large":"native.import_file_error");}
    }
    internal void AppendText(string text)
    {
        try{AppendResult(LinkImport.Extract(text));}
        catch(Exception error)when(error is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException){_summary.Text=_locale.S("native.import_too_large");}
    }
    private void AppendResult(LinkImportResult result)
    {
        _checkCancellation?.Cancel();var duplicates=result.Duplicates;var limited=result.Limited;var seen=_links.Select(row=>row.Url).ToHashSet(StringComparer.Ordinal);
        foreach(var link in result.Links){if(!seen.Add(link.Url)){duplicates++;continue;}if(_links.Count==LinkImport.MaximumLinks){limited=true;break;}var row=new ImportLinkRow{Link=link,AlreadyAdded=_existing.Contains(link.Url),Selected=!_existing.Contains(link.Url)};row.PropertyChanged+=(_,_)=>UpdateButtons();_links.Add(row);}
        for(var i=0;i<_links.Count;i++)_links[i].SetPosition(i+1);
        _summary.Text=result.Links.Count==0?_locale.S("import.no_links"):_locale.S("native.import_summary",("count",_links.Count),("duplicates",duplicates),("existing",_links.Count(row=>row.AlreadyAdded)),("rejected",result.Rejected));if(limited)_summary.Text+=" "+_locale.S("native.import_limit");UpdateButtons();
    }
    private void RemoveRow(ImportLinkRow row){_checkCancellation?.Cancel();_links.Remove(row);for(var i=0;i<_links.Count;i++)_links[i].SetPosition(i+1);UpdateButtons();}
    private void UpdateButtons(){_check.IsEnabled=_checkCancellation is not null||_links.Any(row=>row.Selected);_start.IsEnabled=_later.IsEnabled=_checkCancellation is null&&_links.Any(row=>row.Selected);_empty.Visibility=_links.Count==0?Visibility.Visible:Visibility.Collapsed;}
    internal async Task CheckLinksAsync()
    {
        if (_checkCancellation is not null) { _checkCancellation.Cancel(); return; }
        if (Inspector is null) return;
        var rows = _links.Where(row => row.Selected).ToArray(); if (rows.Length == 0) return;
        using var cancellation = new CancellationTokenSource(); _checkCancellation = cancellation;
        _check.Content = _locale.S("inspect.cancel_check"); UpdateButtons();
        using var slots = new SemaphoreSlim(3); var completed = 0;
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                await slots.WaitAsync(cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    row.Apply(new(null,null,null,null,"checking"),_locale);
                    var result = await Inspector(row.Url, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    row.Apply(result, _locale);
                    _summary.Text = _locale.S("inspect.progress", ("done", ++completed), ("total", rows.Length));
                }
                finally { slots.Release(); }
            }));
        }
        catch (OperationCanceledException) { foreach (var row in rows.Where(row => row.CheckState == "checking")) row.Apply(new(null,null,null,null,"cancelled"),_locale); }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or ArgumentException) { _summary.Text = _locale.S("inspect.unavailable"); }
        finally { _checkCancellation = null; _check.Content = _locale.S("inspect.check_links"); UpdateButtons(); }
    }
    private void Finish(bool start)
    {
        if (SelectedUrls.Length == 0) return;
        try { if (string.IsNullOrWhiteSpace(Folder)) throw new ArgumentException(); _ = Path.GetFullPath(Folder); }
        catch (ArgumentException) { _summary.Text = _locale.S("errors.invalid_folder"); return; }
        StartImmediately = start; DialogResult = true;
    }
}
