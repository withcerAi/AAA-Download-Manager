using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace DownloadManager.Desktop;

public sealed partial class SettingsWindow
{
    private void AddCategoryTable(SettingField field)
    {
        var records=new ObservableCollection<PreferenceRecord>();
        if(_draft.Preferences[field.Key] is JsonArray array)foreach(var item in array.OfType<JsonObject>()){
            var record=new PreferenceRecord();foreach(var pair in item)record[pair.Key]=pair.Value?.ToString()??"";record.TryAdd("folder","");records.Add(record);
        }
        records.CollectionChanged+=(_,_)=>MarkDirty();
        var hint=Ui.Label(_locale,"category.folder_hint",true);hint.TextWrapping=TextWrapping.Wrap;_fieldHost.Children.Add(hint);
        var grid=new DataGrid{ItemsSource=records,IsReadOnly=true,EnableColumnVirtualization=false,MinHeight=180,MaxHeight=250,Margin=new Thickness(0,10,0,8),SelectionMode=DataGridSelectionMode.Single};
        var textStyle=new Style(typeof(TextBlock));textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
        var defaultNames=(_schema.Defaults["categories"] as JsonArray)?.OfType<JsonObject>().ToDictionary(row=>row["id"]!.ToString(),row=>row["name"]!.ToString())??[];
        foreach(var column in field.Columns??[]){
            var binding=column.Key=="name"?new Binding("."){Converter=new CategoryNameDisplay(_locale,defaultNames)}:new Binding("["+column.Key+"]");if(column.Key=="folder")binding.Converter=new DefaultFolderDisplay(_locale);
            var style=new Style(typeof(TextBlock),textStyle);style.Setters.Add(new Setter(ToolTipProperty,binding));if(column.Key!="name")style.Setters.Add(new Setter(FlowDirectionProperty,FlowDirection.LeftToRight));
            grid.Columns.Add(new DataGridTextColumn{Header=_locale.S(column.LabelKey),Binding=binding,ElementStyle=style,Width=new DataGridLength(column.Key=="folder"?2:1,DataGridLengthUnitType.Star),MinWidth=column.Key=="folder"?200:110});
        }
        _editors[field.Key]=grid;_fieldHost.Children.Add(grid);
        void Edit(PreferenceRecord? original){
            var dialog=new CategoryEditWindow(_locale,_draft.DownloadDirectory,original,records.ToArray());if(IsVisible)dialog.Owner=this;
            if(dialog.ShowDialog()!=true||dialog.Result is not {} result)return;
            if(original is null)records.Add(result);else records[records.IndexOf(original)]=result;
            grid.SelectedItem=result;grid.ScrollIntoView(result);
        }
        var actions=new WrapPanel();
        var add=Ui.Button(_locale,"category.add",(_,_)=>Edit(null));
        var edit=Ui.Button(_locale,"category.edit_action",(_,_)=>{if(grid.SelectedItem is PreferenceRecord row)Edit(row);});
        var browse=Ui.Button(_locale,"category.choose_folder",(_,_)=>{
            if(grid.SelectedItem is not PreferenceRecord row)return;
            var current=DownloadManager.Core.DownloadDestination.CategoryFolder(_draft.DownloadDirectory,row.GetValueOrDefault("folder")??"");
            var picker=new OpenFolderDialog{InitialDirectory=Directory.Exists(current)?current:Directory.Exists(_draft.DownloadDirectory)?_draft.DownloadDirectory:Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};
            if(picker.ShowDialog(this)==true){row["folder"]=picker.FolderName;grid.Items.Refresh();MarkDirty();}
        });
        var useDefault=Ui.Button(_locale,"category.default_action",(_,_)=>{if(grid.SelectedItem is PreferenceRecord row){row["folder"]="";grid.Items.Refresh();MarkDirty();}});
        var remove=Ui.Button(_locale,"ui.delete",(_,_)=>{if(grid.SelectedItem is PreferenceRecord row)records.Remove(row);});
        void Selection(){var selected=grid.SelectedItem is PreferenceRecord;edit.IsEnabled=browse.IsEnabled=useDefault.IsEnabled=remove.IsEnabled=selected;}
        grid.SelectionChanged+=(_,_)=>Selection();Selection();
        grid.MouseDoubleClick+=(_,_)=>{if(grid.SelectedItem is PreferenceRecord row)Edit(row);};
        foreach(var button in new[]{add,edit,browse,useDefault,remove}){button.MinWidth=0;button.Padding=new Thickness(10,6,10,6);actions.Children.Add(button);}
        _tableActions[field.Key]=(add,remove);_fieldHost.Children.Insert(_fieldHost.Children.IndexOf(grid),actions);
        _tableCommits.Add(()=>{var result=new JsonArray();foreach(var record in records){var row=new JsonObject();foreach(var pair in record)row[pair.Key]=pair.Value;result.Add(row);}_draft.Preferences[field.Key]=result;});
    }

    private sealed class CategoryNameDisplay(LocaleCatalog locale,IReadOnlyDictionary<string,string> defaults):IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
        {
            if(value is not PreferenceRecord row)return "";
            var name=row.GetValueOrDefault("name")??"";var id=row.GetValueOrDefault("id")??"";var key="category.name."+id;
            return defaults.TryGetValue(id,out var original)&&name==original&&locale.S(key)!=key?locale.S(key):name;
        }
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
    }

    private sealed class DefaultFolderDisplay(LocaleCatalog locale):IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>string.IsNullOrWhiteSpace(value?.ToString())?locale.S("category.default_folder"):value;
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
    }
}
