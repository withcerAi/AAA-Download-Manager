using System.Windows;
using System.Windows.Controls;
using DownloadManager.Core;
namespace DownloadManager.Desktop;

public sealed class SiteCredentialsWindow : Window
{
    public SiteCredentialsWindow(LocaleCatalog locale,PreferenceRecord record)
    {
        Title=locale.S("native.edit_credentials");Width=500;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;FlowDirection=locale.Direction;
        var body=new StackPanel{Margin=new Thickness(20)};
        var url=new TextBox{Text=record.GetValueOrDefault("url",""),FlowDirection=FlowDirection.LeftToRight};
        var user=new TextBox{Text=record.GetValueOrDefault("username",""),FlowDirection=FlowDirection.LeftToRight};
        var password=new PasswordBox{Padding=new Thickness(7)};if(record.TryGetValue("passwordProtected",out var secret)&&secret.Length>0)password.Password=UserSecret.Open(secret);
        var authentication=new ComboBox{ItemsSource=new[]{"Auto","Basic","Digest","NTLM","Negotiate","Kerberos"},SelectedItem=record.GetValueOrDefault("authentication","Auto")};
        body.Children.Add(Ui.Field(locale,"native.login_prefix",url));body.Children.Add(Ui.Field(locale,"ui.username_2",user));body.Children.Add(Ui.Field(locale,"ui.password_2",password));body.Children.Add(Ui.Field(locale,"native.authentication",authentication));body.Children.Add(Ui.Label(locale,"native.credentials_protected",true));
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};body.Children.Add(error);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        actions.Children.Add(Ui.Button(locale,"ui.cancel",(_,_)=>Close()));actions.Children.Add(Ui.Button(locale,"ui.ok",(_,_)=>{
            try{var rule=new SiteLoginRule(url.Text,authentication.SelectedItem?.ToString()??"Auto",new System.Net.NetworkCredential(user.Text,password.Password));new NetworkPolicy{Logins=[rule]}.Validate();record["url"]=url.Text;record["username"]=user.Text;record["passwordProtected"]=UserSecret.Seal(password.Password);record["authentication"]=rule.Authentication;DialogResult=true;}catch(ArgumentException){error.Text=locale.S("native.invalid_login_prefix");}
        }));body.Children.Add(actions);Content=body;
    }
}
