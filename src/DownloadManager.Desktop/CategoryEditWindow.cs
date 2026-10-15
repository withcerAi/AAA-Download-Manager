using System.IO;
using System.Windows;
using System.Windows.Controls;
using DownloadManager.Core;
using Microsoft.Win32;

namespace DownloadManager.Desktop;

public sealed class CategoryEditWindow : Window
{
    public TextBox NameBox { get; } = new();
    public TextBox ExtensionsBox { get; } = new() { FlowDirection = FlowDirection.LeftToRight };
    public TextBox FolderBox { get; } = new() { FlowDirection = FlowDirection.LeftToRight };
    public PreferenceRecord? Result { get; private set; }
    private readonly LocaleCatalog _locale;
    private readonly string _defaultFolder;
    private readonly PreferenceRecord? _original;
    private readonly IReadOnlyList<PreferenceRecord> _others;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };

    public CategoryEditWindow(LocaleCatalog locale, string defaultFolder, PreferenceRecord? original, IReadOnlyList<PreferenceRecord> others)
    {
        _locale = locale; _defaultFolder = defaultFolder; _original = original; _others = others;
        Title = locale.S(original is null ? "category.add_title" : "category.edit_title"); Width = 700; Height = 510;
        MinWidth = 580; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner; FlowDirection = locale.Direction;
        SetResourceReference(BackgroundProperty, "Chrome"); SetResourceReference(ForegroundProperty, "Ink");
        NameBox.Text = original?.GetValueOrDefault("name") ?? "";
        ExtensionsBox.Text = original?.GetValueOrDefault("extensions") ?? "";
        FolderBox.Text = original?.GetValueOrDefault("folder") ?? "";
        var root = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = Ui.Button(locale,"ui.cancel",(_,_)=>DialogResult=false); cancel.IsCancel=true; actions.Children.Add(cancel);
        var save = Ui.Button(locale,"ui.save",(_,_)=>Finish()); save.IsDefault=true; actions.Children.Add(save);
        DockPanel.SetDock(actions,Dock.Bottom); root.Children.Add(actions);
        var fields = new StackPanel(); var heading = new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,18) }; fields.Children.Add(heading);
        fields.Children.Add(Ui.Field(locale,"category.name",NameBox));
        fields.Children.Add(Ui.Field(locale,"native.extensions",ExtensionsBox));
        var extensionsHint = Ui.Label(locale,"category.extensions_hint",true); extensionsHint.TextWrapping=TextWrapping.Wrap; fields.Children.Add(extensionsHint);
        var folderLine = new DockPanel { Margin = new Thickness(0,12,0,0) };
        var browse=Ui.Button(locale,"native.browse",(_,_)=>{var picker=new OpenFolderDialog{InitialDirectory=Directory.Exists(FolderBox.Text)?FolderBox.Text:Directory.Exists(defaultFolder)?defaultFolder:Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};if(picker.ShowDialog(this)==true)FolderBox.Text=picker.FolderName;}); DockPanel.SetDock(browse,Dock.Right); folderLine.Children.Add(browse); folderLine.Children.Add(FolderBox);
        fields.Children.Add(Ui.Field(locale,"ui.save_to",folderLine));
        fields.Children.Add(Ui.Button(locale,"category.use_default",(_,_)=>FolderBox.Clear()));
        var hint=Ui.Label(locale,"category.editor_folder_hint",true); hint.TextWrapping=TextWrapping.Wrap; fields.Children.Add(hint);
        var destination=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12),FlowDirection=FlowDirection.LeftToRight}; fields.Children.Add(destination);
        void UpdateDestination(){try{destination.Text=locale.S("category.destination",("path",DownloadDestination.CategoryFolder(defaultFolder,FolderBox.Text.Trim())));}catch(Exception error) when(error is ArgumentException or IOException){destination.Text=locale.S("errors.invalid_folder");}}
        FolderBox.TextChanged+=(_,_)=>UpdateDestination(); UpdateDestination();
        _error.SetResourceReference(TextBlock.ForegroundProperty,"Ink"); fields.Children.Add(_error);
        root.Children.Add(new ScrollViewer{Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}); Content=root;
        Loaded+=(_,_)=>{NameBox.Focus();NameBox.SelectAll();};
    }

    internal bool TryBuildResult()
    {
        var name=NameBox.Text.Trim(); var id=_original?.GetValueOrDefault("id") ?? Guid.NewGuid().ToString("N");
        if(name.Length is <1 or >80 || name.Any(char.IsControl)){_error.Text=_locale.S("category.invalid_name");return false;}
        if(_others.Any(row=>row.GetValueOrDefault("id")!=id&&string.Equals(row.GetValueOrDefault("name"),name,StringComparison.OrdinalIgnoreCase))){_error.Text=_locale.S("category.duplicate_name");return false;}
        var extensions=ExtensionsBox.Text.Split([' ', ',', ';', '\r', '\n', '\t'],StringSplitOptions.RemoveEmptyEntries).Select(value=>value.TrimStart('*','.').ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(extensions.Any(value=>value.Length is <1 or >32 || value.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'&&c!='-')) || extensions.Length==0&&id!="general") {_error.Text=_locale.S("category.invalid_extensions");return false;}
        foreach(var row in _others.Where(row=>row.GetValueOrDefault("id")!=id))
            if((row.GetValueOrDefault("extensions")??"").Split([' ',',',';'],StringSplitOptions.RemoveEmptyEntries).Any(value=>extensions.Contains(value.TrimStart('*','.'),StringComparer.OrdinalIgnoreCase))){_error.Text=_locale.S("category.overlap",("name",row.GetValueOrDefault("name")));return false;}
        var folder=FolderBox.Text.Trim();
        try{_ = DownloadDestination.CategoryFolder(_defaultFolder,folder);}catch(Exception error) when(error is ArgumentException or IOException){_error.Text=_locale.S("errors.invalid_folder");return false;}
        Result=new PreferenceRecord();if(_original is not null)foreach(var pair in _original)Result[pair.Key]=pair.Value;
        Result["id"]=id;Result["name"]=name;Result["extensions"]=string.Join(' ',extensions);Result["folder"]=folder;_error.Text="";return true;
    }
    private void Finish(){if(TryBuildResult())DialogResult=true;}
}
