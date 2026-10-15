using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DownloadManager.Core;
namespace DownloadManager.Desktop;

public sealed class AppDialog : Window
{
    private MessageBoxResult _answer=MessageBoxResult.Cancel;
    public AppDialog(LocaleCatalog locale,string message,string title,MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage icon=MessageBoxImage.None,MessageBoxResult defaultResult=MessageBoxResult.None)
    {
        Title=title;Width=540;SizeToContent=SizeToContent.Height;MinHeight=210;MaxHeight=700;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;FlowDirection=locale.Direction;
        SetResourceReference(BackgroundProperty,"Chrome");SetResourceReference(ForegroundProperty,"Ink");
        var root=new StackPanel{Margin=new Thickness(24)};root.Margin=new Thickness(0);var frame=new Border{Padding=new Thickness(24),Child=root};frame.SetResourceReference(Border.BackgroundProperty,"Chrome");Content=frame;
        var heading=Ui.Label(locale,icon==MessageBoxImage.Error?"dialog.error":icon==MessageBoxImage.Question?"dialog.confirm":"dialog.info");heading.FontSize=20;heading.FontWeight=FontWeights.SemiBold;root.Children.Add(heading);
        var text=new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,24)};text.SetResourceReference(TextBlock.ForegroundProperty,"Ink");root.Children.Add(new ScrollViewer{Content=text,MaxHeight=420,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};root.Children.Add(footer);
        var choices=buttons switch{MessageBoxButton.YesNo=>new[]{MessageBoxResult.No,MessageBoxResult.Yes},MessageBoxButton.YesNoCancel=>new[]{MessageBoxResult.Cancel,MessageBoxResult.No,MessageBoxResult.Yes},MessageBoxButton.OKCancel=>new[]{MessageBoxResult.Cancel,MessageBoxResult.OK},_=>new[]{MessageBoxResult.OK}};
        foreach(var result in choices){var key=result switch{MessageBoxResult.Yes=>"dialog.yes",MessageBoxResult.No=>"dialog.no",MessageBoxResult.Cancel=>"ui.cancel",_=>"ui.ok"};var button=Ui.Button(locale,key,(_,_)=>{_answer=result;DialogResult=true;});button.Margin=new Thickness(6,0,0,0);button.IsDefault=result==(defaultResult==MessageBoxResult.None?(buttons==MessageBoxButton.OK?MessageBoxResult.OK:choices[0]):defaultResult);button.IsCancel=result==MessageBoxResult.Cancel;footer.Children.Add(button);}
    }
    public static MessageBoxResult Show(Window owner,string message,string title,MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage icon=MessageBoxImage.None,MessageBoxResult defaultResult=MessageBoxResult.None)
    {
        var app=(App)Application.Current;
        var dialog=new AppDialog(app.Locale,message,title,buttons,icon,defaultResult);if(owner.IsVisible)dialog.Owner=owner;dialog.ShowDialog();return dialog._answer;
    }
    public static MessageBoxResult Show(string message,string title="")
    {
        var app=(App)Application.Current;
        if(app.Locale is null)return MessageBox.Show(message,title);
        var dialog=new AppDialog(app.Locale,message,string.IsNullOrEmpty(title)?app.Locale.S("ui.download_manager"):title,icon:MessageBoxImage.Error);if(app.MainWindow?.IsVisible==true)dialog.Owner=app.MainWindow;dialog.ShowDialog();return dialog._answer;
    }
}

public sealed class RemoveDownloadsWindow : Window
{
    public bool DeleteFiles{get;private set;}
    public RemoveDownloadsWindow(LocaleCatalog locale,IReadOnlyList<DownloadJob> jobs)
    {
        Resources.Add(typeof(RadioButton),System.Windows.Markup.XamlReader.Parse("""
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton"><Setter Property="Foreground" Value="{DynamicResource Ink}"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton"><Grid Background="Transparent"><Grid.ColumnDefinitions><ColumnDefinition Width="22"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Border x:Name="Ring" Width="14" Height="14" VerticalAlignment="Top" Margin="0,3,0,0" CornerRadius="7" BorderThickness="1" BorderBrush="{DynamicResource MutedInk}" Background="{DynamicResource Chrome}"><Ellipse x:Name="Dot" Margin="3" Fill="{DynamicResource Accent}" Visibility="Collapsed"/></Border><ContentPresenter Grid.Column="1"/></Grid><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Dot" Property="Visibility" Value="Visible"/><Setter TargetName="Ring" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Ring" Property="BorderThickness" Value="2"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
"""));
        Title=locale.S("delete.title");Width=650;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;FlowDirection=locale.Direction;SetResourceReference(BackgroundProperty,"Chrome");SetResourceReference(ForegroundProperty,"Ink");
        var root=new StackPanel{Margin=new Thickness(24)};root.Margin=new Thickness(0);var frame=new Border{Padding=new Thickness(24),Child=root};frame.SetResourceReference(Border.BackgroundProperty,"Chrome");Content=frame;
        root.Children.Add(new TextBlock{Text=locale.S("delete.heading",("count",jobs.Count)),FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)});
        var files=new StackPanel{Margin=new Thickness(12)};foreach(var job in jobs.Take(5)){files.Children.Add(new TextBlock{Text=job.FileName,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=job.FinalPath,FlowDirection=FlowDirection.LeftToRight,Margin=new Thickness(0,3,0,3)});}if(jobs.Count>5)files.Children.Add(new TextBlock{Text=locale.S("delete.more",("count",jobs.Count-5))});root.Children.Add(Ui.Panel(files));
        if(jobs.Any(job=>job.State!=DownloadState.Completed)){var notice=new TextBlock{Text=locale.S("delete.mixed_notice",("count",jobs.Count(job=>job.State==DownloadState.Completed))),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};notice.SetResourceReference(TextBlock.ForegroundProperty,"MutedInk");root.Children.Add(notice);}
        var list=Option("delete.list","delete.list_hint");list.IsChecked=true;var disk=Option("delete.disk","delete.disk_hint");
        RadioButton Option(string title,string hint){var stack=new StackPanel();var label=Ui.Label(locale,title);label.FontWeight=FontWeights.SemiBold;label.FontSize=14;stack.Children.Add(label);var description=Ui.Label(locale,hint,true);description.Margin=new Thickness(0,6,0,0);description.MaxWidth=520;stack.Children.Add(description);var radio=new RadioButton{Content=stack,GroupName="removal",Margin=new Thickness(12),VerticalContentAlignment=VerticalAlignment.Top};var card=Ui.Panel(radio);card.Margin=new Thickness(0,12,0,0);root.Children.Add(card);return radio;}
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,22,0,0)};var cancel=Ui.Button(locale,"ui.cancel",(_,_)=>{DialogResult=false;});cancel.IsCancel=true;cancel.IsDefault=true;footer.Children.Add(cancel);var remove=Ui.Button(locale,"delete.confirm",(_,_)=>{DeleteFiles=disk.IsChecked==true;DialogResult=true;});remove.Margin=new Thickness(8,0,0,0);footer.Children.Add(remove);root.Children.Add(footer);
        disk.Checked+=(_,_)=>remove.Content=locale.S("delete.permanent");list.Checked+=(_,_)=>remove.Content=locale.S("delete.confirm");
    }
}
