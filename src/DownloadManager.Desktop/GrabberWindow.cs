using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DownloadManager.Core;
using Microsoft.Win32;

namespace DownloadManager.Desktop;
public sealed class GrabberWindow:Window
{
    private readonly App _app;
    private readonly TextBox _url=new(){FlowDirection=FlowDirection.LeftToRight};
    private readonly TextBox _extensions=new(){Text="zip pdf mp4 mp3",FlowDirection=FlowDirection.LeftToRight};
    private readonly TextBox _exclude=new(){FlowDirection=FlowDirection.LeftToRight};
    private readonly TextBox _depth=new(){Text="1",Width=70};
    private readonly TextBox _pages=new(){Text="50",Width=90};
    private readonly CheckBox _folder;
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button _scan,_review;
    private readonly DataGrid _results=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false};
    private readonly StackPanel _inputs=new();
    private CancellationTokenSource? _cancellation;
    private GrabberResult? _result;
    private readonly Func<string,Task> _import;
    public GrabberWindow(App app,Func<string,Task> import)
    {
        _app=app;_import=import;Title=app.Locale.S("grabber.title");Width=880;Height=700;MinWidth=700;MinHeight=560;FlowDirection=app.Locale.Direction;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(18)};var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        actions.Children.Add(Ui.Button(app.Locale,"ui.cancel",(_,_)=>{if(_cancellation is not null)_cancellation.Cancel();else Close();}));
        _review=Ui.Button(app.Locale,"grabber.review",async(_,_)=>{if(_result is null)return;var text=string.Join(Environment.NewLine,_result.Links.Select(l=>l.Url));Close();await _import(text);});_review.IsEnabled=false;actions.Children.Add(_review);DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
        var header=new StackPanel();var heading=Ui.Label(app.Locale,"grabber.title");heading.FontSize=20;heading.FontWeight=FontWeights.SemiBold;header.Children.Add(heading);header.Children.Add(Ui.Label(app.Locale,"grabber.hint",true));
        _inputs.Children.Add(Ui.Field(app.Locale,"ui.url",_url));_inputs.Children.Add(Ui.Field(app.Locale,"grabber.extensions",_extensions));_inputs.Children.Add(Ui.Field(app.Locale,"grabber.exclude",_exclude));
        var limits=new WrapPanel();limits.Children.Add(Ui.Field(app.Locale,"grabber.depth",_depth));limits.Children.Add(Ui.Field(app.Locale,"grabber.pages",_pages));_folder=new CheckBox{Content=Ui.Label(app.Locale,"grabber.folder"),IsChecked=true,Margin=new Thickness(15,20,0,0)};limits.Children.Add(_folder);_inputs.Children.Add(limits);
        var tools=new StackPanel{Orientation=Orientation.Horizontal};tools.Children.Add(Ui.Button(app.Locale,"grabber.load",(_,_)=>Load()));tools.Children.Add(Ui.Button(app.Locale,"grabber.save",(_,_)=>Save()));_scan=Ui.Button(app.Locale,"grabber.scan",async(_,_)=>await ScanAsync());tools.Children.Add(_scan);_inputs.Children.Add(tools);header.Children.Add(_inputs);header.Children.Add(_status);DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        foreach(var (key,path) in new[]{("ui.file_name",nameof(ImportedLink.FileName)),("ui.url",nameof(ImportedLink.Url))}){var column=new DataGridTextColumn{Header=app.Locale.S(key),Binding=new System.Windows.Data.Binding(path),MinWidth=key=="ui.url"?300:180,Width=new DataGridLength(1,DataGridLengthUnitType.Star)};_results.Columns.Add(column);}_results.SizeChanged+=(_,_)=>{if(_results.Columns.Count==2&&_results.ActualWidth>500){var first=Math.Max(180,_results.ActualWidth*.32);_results.Columns[0].Width=first;_results.Columns[1].Width=Math.Max(300,_results.ActualWidth-first-4);}};_results.Margin=new Thickness(0,12,0,0);root.Children.Add(_results);Content=root;Closing+=(_,_)=>_cancellation?.Cancel();
    }
    private GrabberOptions Read()=>new(){StartUrl=_url.Text.Trim(),Extensions=_extensions.Text,ExcludedPatterns=_exclude.Text,Depth=int.Parse(_depth.Text),MaximumPages=int.Parse(_pages.Text),StayInStartFolder=_folder.IsChecked==true};
    private async Task ScanAsync()
    {
        try{var options=Read();options.Validate();_inputs.IsEnabled=false;_review.IsEnabled=false;_cancellation=new();var policy=_app.Settings.TransferPolicy();_result=await WebsiteGrabber.ScanAsync(options,policy.Network,policy.UserAgent,new Progress<GrabberProgress>(p=>_status.Text=_app.Locale.S("grabber.progress",("pages",p.Pages),("count",p.Links))),_cancellation.Token);_results.ItemsSource=_result.Links;_review.IsEnabled=_result.Links.Count>0;_status.Text=_app.Locale.S("grabber.done",("pages",_result.Pages),("count",_result.Links.Count),("skipped",_result.Skipped));}
        catch(OperationCanceledException){_status.Text=_app.Locale.S("grabber.cancelled");}
        catch(Exception error)when(error is ArgumentException or FormatException or OverflowException or IOException){_status.Text=_app.Locale.S("grabber.invalid");}
        finally{_cancellation?.Dispose();_cancellation=null;_inputs.IsEnabled=true;}
    }
    private void Save(){try{var options=Read();options.Validate();var picker=new SaveFileDialog{Filter=_app.Locale.S("native.json_file_filter"),DefaultExt=".json"};if(picker.ShowDialog(this)==true)File.WriteAllText(picker.FileName,JsonSerializer.Serialize(options,new JsonSerializerOptions{WriteIndented=true}));}catch(Exception e)when(e is IOException or ArgumentException or FormatException or OverflowException){_status.Text=_app.Locale.S("grabber.invalid");}}
    private void Load(){try{var picker=new OpenFileDialog{Filter=_app.Locale.S("native.json_file_filter")};if(picker.ShowDialog(this)!=true)return;if(new FileInfo(picker.FileName).Length>65536)throw new ArgumentException();var value=JsonSerializer.Deserialize<GrabberOptions>(File.ReadAllText(picker.FileName))??throw new ArgumentException();value.Validate();_url.Text=value.StartUrl;_extensions.Text=value.Extensions;_exclude.Text=value.ExcludedPatterns;_depth.Text=value.Depth.ToString();_pages.Text=value.MaximumPages.ToString();_folder.IsChecked=value.StayInStartFolder;}catch(Exception e)when(e is IOException or ArgumentException or JsonException){_status.Text=_app.Locale.S("grabber.invalid");}}
}
