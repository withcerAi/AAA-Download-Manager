using DownloadManager.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DownloadManager.Desktop;

public sealed class SettingsSchema
{
    public JsonObject Defaults { get; set; } = new();
    public List<SettingSection> Sections { get; set; } = [];
    public static SettingsSchema Load(string path) => JsonSerializer.Deserialize<SettingsSchema>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}
public sealed class SettingSection
{
    public string Id { get; set; } = "";
    public string? Parent { get; set; }
    public string TitleKey { get; set; } = "";
    public List<SettingField> Fields { get; set; } = [];
}
public sealed class SettingField
{
    public string Key { get; set; } = "";
    public string LabelKey { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string? Depends { get; set; }
    public bool Secret { get; set; }
    public List<SettingChoice>? Choices { get; set; }
    public List<SettingColumn>? Columns { get; set; }
}
public sealed class SettingChoice { public string Value { get; set; } = ""; public string? LabelKey { get; set; } }
public sealed class SettingColumn { public string Key { get; set; } = ""; public string LabelKey { get; set; } = ""; }
public sealed class PreferenceRecord : Dictionary<string, string> { }
internal sealed record ChoiceItem(string Value, string Text);

public sealed partial class SettingsWindow : Window
{
    private readonly UserSettings _draft;
    private readonly LocaleCatalog _locale;
    private readonly SettingsSchema _schema;
    private readonly Action<UserSettings> _apply;
    private readonly TreeView _navigation = new() { Width = 230 };
    private readonly TextBox _search = new() { MinHeight = 34 };
    private readonly TextBlock _saveStatus = new();
    private readonly Button _applyButton;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _pageHeader = new(){Margin=new Thickness(24,14,24,8)};
    private StackPanel _fieldHost = new();
    private bool _buildingNavigation;
    private readonly Dictionary<string, FrameworkElement> _editors = [];
    private readonly Dictionary<string, (Button Add, Button Remove)> _tableActions = [];
    private readonly Dictionary<string, string> _rawInputs = [];
    private readonly HashSet<string> _invalidInputs = [];
    private bool _dirty;
    private bool _allowClose;
    private readonly StackPanel _fields = new() { Margin = new Thickness(24, 9, 24, 24) };
    private readonly List<(FrameworkElement Control, string Expression)> _dependencies = [];
    private readonly List<Action> _tableCommits = [];
    private string _selected;
    private readonly PropertyChangedEventHandler _languageChanged;

    public SettingsWindow(UserSettings original, LocaleCatalog locale, SettingsSchema schema, Action<UserSettings> apply, string initial = "general")
    {
        if(initial=="categories")initial="saveTo";
        _draft = original.Copy(); _locale = locale; _schema = schema; _apply = apply; _selected = initial;
        foreach (var item in schema.Defaults) if (!_draft.Preferences.ContainsKey(item.Key)) _draft.Preferences[item.Key] = item.Value?.DeepClone();
        Width = 1020; Height = 760; MinWidth = 900; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        locale.Bind(this, TitleProperty, "ui.application_options"); BindingOperations.SetBinding(this, FlowDirectionProperty, new Binding(nameof(LocaleCatalog.Direction)) { Source = locale });
        var layout = new DockPanel { Margin = new Thickness(16) }; layout.SetResourceReference(Panel.BackgroundProperty, "Chrome");
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var header = new StackPanel(); var title = Ui.Label(locale, "ui.application_options"); title.FontSize = 21; title.FontWeight = FontWeights.SemiBold; header.Children.Add(title); var subtitle = Ui.Label(locale, "native.settings_subtitle", true); subtitle.Margin = new Thickness(0, 5, 0, 0); header.Children.Add(subtitle);
        var searchPanel = new DockPanel { Width = 285 }; var clearSearch = new Button { Content = "×", Width = 31, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) }; locale.Bind(clearSearch, ToolTipProperty, "native.clear_search"); clearSearch.Click += (_, _) => _search.Clear(); DockPanel.SetDock(clearSearch, Dock.Right); searchPanel.Children.Add(clearSearch); locale.Bind(_search, ToolTipProperty, "native.search_settings"); locale.Bind(_search, TagProperty, "native.search_settings"); searchPanel.Children.Add(_search); DockPanel.SetDock(searchPanel, Dock.Right); top.Children.Add(searchPanel); top.Children.Add(header); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Button(locale, "ui.cancel", (_, _) => Close()); cancel.IsCancel = true; buttons.Children.Add(cancel);
        _applyButton = Ui.Button(locale, "ui.apply", (_, _) => Commit(false)); _applyButton.IsEnabled = false; buttons.Children.Add(_applyButton);
        var ok = Ui.Button(locale, "ui.ok", (_, _) => Commit(true)); ok.IsDefault = true; ok.SetResourceReference(BorderBrushProperty, "Accent"); ok.SetResourceReference(BackgroundProperty, "Selection"); buttons.Children.Add(ok); DockPanel.SetDock(buttons, Dock.Right); bottom.Children.Add(buttons); _saveStatus.VerticalAlignment = VerticalAlignment.Center; _saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk"); bottom.Children.Add(_saveStatus); DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        var side = new DockPanel { Width = 250, Margin = new Thickness(0, 0, 12, 0) }; var navigationTitle = Ui.Label(locale, "settings.sections", true); navigationTitle.Margin = new Thickness(12, 10, 12, 10); DockPanel.SetDock(navigationTitle, Dock.Top); side.Children.Add(navigationTitle); _navigation.Width = double.NaN; _navigation.Margin = new Thickness(0); side.Children.Add(_navigation); var sideBorder = Ui.Panel(side); DockPanel.SetDock(sideBorder, Dock.Left); layout.Children.Add(sideBorder);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _fields }; var editor=new DockPanel();DockPanel.SetDock(_pageHeader,Dock.Top);editor.Children.Add(_pageHeader);editor.Children.Add(_scroll);layout.Children.Add(Ui.Panel(editor)); Content = layout;
        _navigation.SelectedItemChanged += (_, _) => { if (!_buildingNavigation && _navigation.SelectedItem is TreeViewItem { Tag: SettingSection section }) { if(_schema.Sections.FirstOrDefault(s=>s.Id==_selected)?.Parent==section.Id)return;SaveTables(); _selected = section.Id; BuildFields(section); _scroll.ScrollToTop(); } };
        _search.TextChanged += (_, _) => { SaveTables(); BuildNavigation(); BuildFields(schema.Sections.First(section => section.Id == _selected)); };
        PreviewKeyDown += (_, args) => { if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); _search.SelectAll(); args.Handled = true; } else if (args.Key == Key.Escape && _search.Text.Length > 0) { _search.Clear(); args.Handled = true; } };
        Closing += (_, args) => { if (!_dirty || _allowClose) return; var answer = AppDialog.Show(this, locale.S("native.unsaved_question"), Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel); if (answer == MessageBoxResult.Cancel) args.Cancel = true; else if (answer == MessageBoxResult.Yes) args.Cancel = !Commit(false); };
        UpdateSaveStatus();
        BuildNavigation(); BuildFields(schema.Sections.First(section => section.Id == _selected));
        _languageChanged = (_, args) => { if (args.PropertyName == nameof(LocaleCatalog.Direction)) { BuildNavigation(); BuildFields(schema.Sections.First(section => section.Id == _selected)); UpdateSaveStatus(); } };
        locale.PropertyChanged += _languageChanged; Closed += (_, _) => locale.PropertyChanged -= _languageChanged;
    }

    internal void SetSearchForRender(string query) => _search.Text = query;
    internal string[] VerifyUx()
    {
        var initial = _draft.Copy(); var active = _selected; var checks = new List<string>();
        var appearance = _schema.Sections.First(section => section.Id == "appearance");
        var palette = appearance.Fields.First(field => field.Key == "palette");
        _search.Text = _locale.S(palette.LabelKey);
        if (_selected != "appearance" || _navigation.Items.Count == 0 || !Matches(appearance, palette)) throw new InvalidOperationException("Search did not find appearance preference.");
        checks.Add("localized cross-section search");
        _search.Text = "__no_matching_setting_93__";
        if (_navigation.Items.Count != 0) throw new InvalidOperationException("Empty search result is incorrect.");
        checks.Add("empty search state");
        _search.Clear();
        if (_navigation.Items.Count != _schema.Sections.Count(section => section.Parent is null)) throw new InvalidOperationException("Clear search did not restore navigation.");
        checks.Add("clear search restores navigation");
        var testParallel = initial.Preferences["parallel"]?.ToString() == "4" ? 5 : 4; Set("parallel", testParallel); _search.Text = _locale.S(palette.LabelKey); _search.Clear();
        if (Value("parallel") != testParallel.ToString() || !_applyButton.IsEnabled) throw new InvalidOperationException("Draft was lost during search.");
        checks.Add("draft retained across search and navigation");
        if (!Commit(false) || _applyButton.IsEnabled || _dirty) throw new InvalidOperationException("Apply did not clear unsaved state.");
        checks.Add("apply clears unsaved state");
        _selected = active; BuildNavigation(); BuildFields(_schema.Sections.First(section => section.Id == active)); return checks.ToArray();
    }

    private bool Matches(SettingSection section, SettingField? field = null)
    {
        var query = _search.Text.Trim(); if (query.Length == 0) return true;
        return _locale.S(section.TitleKey).Contains(query, StringComparison.CurrentCultureIgnoreCase) || (field is not null && (_locale.S(field.LabelKey).Contains(query, StringComparison.CurrentCultureIgnoreCase) || (field.Choices?.Any(choice => choice.LabelKey is { } key && _locale.S(key).Contains(query, StringComparison.CurrentCultureIgnoreCase)) ?? false)));
    }
    private bool SectionMatches(SettingSection section) => Matches(section) || section.Fields.Any(field => Matches(section, field));
    private void BuildNavigation()
    {
        _buildingNavigation = true;
        try
        {
            _navigation.Items.Clear(); var matching = _schema.Sections.Where(SectionMatches).ToArray();
            if (!matching.Any(section => section.Id == _selected) && matching.Length > 0) _selected = matching[0].Id;
            foreach (var section in _schema.Sections.Where(section => section.Parent is null))
            {
                var children = _schema.Sections.Where(child => child.Parent == section.Id && SectionMatches(child)).ToArray();
                if (!SectionMatches(section) && children.Length == 0) continue;
                var node = Node(section);_navigation.Items.Add(node);
            }
        }
        finally { _buildingNavigation = false; }
        TreeViewItem Node(SettingSection section)
        {
            var label=Ui.Label(_locale,section.TitleKey);label.FontSize=13;label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;
            return new TreeViewItem{Header=label,ToolTip=_locale.S(section.TitleKey),Tag=section,IsSelected=section.Id==_selected||_schema.Sections.Any(child=>child.Parent==section.Id&&child.Id==_selected)};
        }
    }

    private void BuildFields(SettingSection section)
    {
        _fields.Children.Clear();_pageHeader.Children.Clear(); _dependencies.Clear(); _tableCommits.Clear(); _editors.Clear(); _tableActions.Clear();
        var parent=section.Parent is {} parentId?_schema.Sections.First(s=>s.Id==parentId):section;
        var titleRow=new DockPanel{Margin=new Thickness(0,0,0,12)};var reset=Ui.Button(_locale,"native.reset_section",(_,_)=>ResetSection(section));reset.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(reset,Dock.Right);titleRow.Children.Add(reset);var heading=Ui.Label(_locale,parent.TitleKey);heading.FontSize=22;heading.FontWeight=FontWeights.SemiBold;titleRow.Children.Add(heading);_pageHeader.Children.Add(titleRow);
        var subsections=_schema.Sections.Where(s=>s.Parent==parent.Id).ToArray();
        if(subsections.Length>0){var tabs=new WrapPanel{Margin=new Thickness(-3,0,0,4)};foreach(var page in new[]{parent}.Concat(subsections)){
            var tab=Ui.Button(_locale,page.Id==parent.Id?"settings.overview":page.TitleKey,(_,_)=>{SaveTables();_selected=page.Id;BuildNavigation();BuildFields(page);_scroll.ScrollToTop();});
            tab.MinWidth=0;tab.Padding=new Thickness(12,7,12,7);tab.Margin=new Thickness(3,0,3,6);if(page.Id==section.Id){tab.SetResourceReference(BackgroundProperty,"Selection");tab.SetResourceReference(BorderBrushProperty,"Accent");tab.SetResourceReference(ForegroundProperty,"Accent");}tabs.Children.Add(tab);
        }_pageHeader.Children.Add(tabs);}
        var visibleFields = section.Fields.Where(field => Matches(section, field)).ToArray();
        
        if (visibleFields.Length == 0) { var empty = Ui.Label(_locale, "native.no_settings_results", true); empty.Margin = new Thickness(0, 20, 0, 20); _fields.Children.Add(empty); return; }
        var pending = visibleFields.Any(field => !IsLive(field.Key));
        if(pending){var notice=Ui.Label(_locale,"settings.pending_short",true);notice.Margin=new Thickness(0,0,0,14);_fields.Children.Add(notice);}
        string? currentGroup = null;
        foreach (var field in visibleFields)
        {
            var group = GroupFor(field.Key); if (group != currentGroup)
            {
                currentGroup = group; _fieldHost = new StackPanel { Margin = new Thickness(15, 9, 15, 13) }; var card = Ui.Panel(_fieldHost); card.CornerRadius = new CornerRadius(4); card.Margin = new Thickness(0, 0, 0, 12); _fields.Children.Add(card);
                var groupTitle = Ui.Label(_locale, group); groupTitle.FontWeight = FontWeights.SemiBold; groupTitle.Margin = new Thickness(0, 3, 0, 10); _fieldHost.Children.Add(groupTitle);
            }
            FrameworkElement editor;
            var value = Value(field.Key);
            if (field.Kind == "table") { AddTable(field); continue; }
            if (field.Kind == "boolean")
            {
                var check = new CheckBox { Content = Ui.Label(_locale, field.LabelKey), IsChecked = value.Equals("true", StringComparison.OrdinalIgnoreCase) };
                check.Checked += (_, _) => { Set(field.Key, true); UpdateDependencies(); }; check.Unchecked += (_, _) => { Set(field.Key, false); UpdateDependencies(); };
                editor = check; _fieldHost.Children.Add(check);
            }
            else
            {
                if (field.Kind == "locale" || field.Choices is { Count: > 0 })
                {
                    var choices = field.Kind == "locale" ? _locale.Languages.Select(info => new ChoiceItem(info.Code, info.NativeName)).ToArray() : field.Choices!.Select(choice => new ChoiceItem(choice.Value, choice.LabelKey is { } key ? _locale.S(key) : choice.Value)).ToArray();
                    var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(ChoiceItem.Text), SelectedValuePath = nameof(ChoiceItem.Value), SelectedValue = value };
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ChoiceItem item) { Set(field.Key, item.Value); UpdateDependencies(); } }; editor = combo;
                }
                else if (field.Kind == "password") { var password=new PasswordBox { Password=_draft.ReadSecret(field.Key),Padding=new Thickness(7),Margin=new Thickness(0) };password.PasswordChanged+=(_,_)=>{_draft.WriteSecret(field.Key,password.Password);MarkDirty();};editor=password; }
                else
                {
                    var box = new TextBox { Text = field.Secret ? _draft.ReadSecret(field.Key) : _rawInputs.GetValueOrDefault(field.Key, value), FlowDirection = FlowDirection.LeftToRight, MinHeight = field.Kind == "textarea" ? 70 : 28, AcceptsReturn = field.Kind == "textarea", TextWrapping = field.Kind == "textarea" ? TextWrapping.Wrap : TextWrapping.NoWrap };
                    box.TextChanged += (_, _) =>
                    {
                        if (field.Secret) { _draft.WriteSecret(field.Key,box.Text);MarkDirty();return; }
                        if (field.Kind is "number" or "date")
                        {
                            _rawInputs[field.Key] = box.Text; MarkDirty();
                            if (!ValidInput(field, box.Text)) _invalidInputs.Add(field.Key);
                            else { _invalidInputs.Remove(field.Key); if (field.Kind == "number" && box.Text.Length > 0) Set(field.Key, long.Parse(box.Text)); else Set(field.Key, box.Text); }
                            ShowInputValidation(box, field);
                        }
                        else Set(field.Key, box.Text);
                        UpdateDependencies();
                    }; editor = box; if (field.Kind is "number" or "date") ShowInputValidation(box, field);
                }
                if((field.Kind=="folder"||field.Key=="temporaryFolder")&&editor is TextBox folderInput){var line=new DockPanel();var browse=Ui.Button(_locale,"native.browse",(_,_)=>{var picker=new Microsoft.Win32.OpenFolderDialog{InitialDirectory=Directory.Exists(folderInput.Text)?folderInput.Text:Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};if(picker.ShowDialog(this)==true)folderInput.Text=picker.FolderName;});DockPanel.SetDock(browse,Dock.Right);line.Children.Add(browse);line.Children.Add(folderInput);if(field.Key=="temporaryFolder"){var pathField=new StackPanel{Margin=new Thickness(0,10,0,4)};pathField.Children.Add(Ui.Label(_locale,field.LabelKey,true));line.Margin=new Thickness(0,6,0,0);pathField.Children.Add(line);_fieldHost.Children.Add(pathField);}else _fieldHost.Children.Add(Ui.Field(_locale,field.LabelKey,line));}
                else _fieldHost.Children.Add(Ui.Field(_locale, field.LabelKey, editor));
                if(field.Key=="temporaryFolder"){var hint=Ui.Label(_locale,"settings.temp_shared",true);hint.Margin=new Thickness(0,3,0,8);_fieldHost.Children.Add(hint);}
            }
            _editors[field.Key] = editor;
            _locale.Bind(editor, ToolTipProperty, IsLive(field.Key) ? "native.active_option" : "native.pending_option");
            if (field.Kind is "number" or "date" && editor is TextBox validated) ShowInputValidation(validated, field);
            if (field.Depends is { Length: > 0 } dependency) _dependencies.Add((editor, dependency));
        }
        UpdateDependencies();
    }

    private void AddTable(SettingField field)
    {
        if(field.Key=="categories"){AddCategoryTable(field);return;}
        var records = new ObservableCollection<PreferenceRecord>();
        if (_draft.Preferences[field.Key] is JsonArray array) foreach (var item in array.OfType<JsonObject>()) { var record = new PreferenceRecord(); foreach (var pair in item) record[pair.Key] = pair.Value?.ToString() ?? ""; if(field.Key=="siteLogins" && item["usernameProtected"] is {} username)record["username"]=UserSecret.Open(username.ToString()); records.Add(record); }
        records.CollectionChanged += (_, _) => MarkDirty();
        var grid = new DataGrid { ItemsSource = records, IsReadOnly = false, MinHeight = 140, MaxHeight = 300, Margin = new Thickness(0, 9, 0, 8), FlowDirection = FlowDirection.LeftToRight };
        grid.CellEditEnding += (_, _) => MarkDirty();
        foreach (var column in field.Columns ?? []) grid.Columns.Add(new DataGridTextColumn { Header = _locale.S(column.LabelKey), Binding = new Binding("[" + column.Key + "]") { Mode = BindingMode.TwoWay }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _editors[field.Key] = grid;
        _fieldHost.Children.Add(Ui.Label(_locale, field.LabelKey)); _fieldHost.Children.Add(grid);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var add = Ui.Button(_locale, "ui.new", (_, _) => { var record = new PreferenceRecord { ["id"] = Guid.NewGuid().ToString("N") }; foreach (var column in field.Columns ?? []) record[column.Key] = ""; records.Add(record); grid.SelectedItem = record; });
        var remove = Ui.Button(_locale, "ui.delete", (_, _) => { if (grid.SelectedItem is PreferenceRecord record) records.Remove(record); }); remove.IsEnabled = false; grid.SelectionChanged += (_, _) => remove.IsEnabled = grid.SelectedItem is PreferenceRecord; actions.Children.Add(add); actions.Children.Add(remove); _tableActions[field.Key] = (add, remove); _fieldHost.Children.Add(actions);if(field.Key=="siteLogins") { var password=Ui.Button(_locale,"native.edit_credentials",(_,_)=>{ if(grid.SelectedItem is not PreferenceRecord record)return; var dialog=new SiteCredentialsWindow(_locale,record){Owner=this};if(dialog.ShowDialog()==true)MarkDirty(); });actions.Children.Add(password); }
        _tableCommits.Add(() =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            var result = new JsonArray();
            foreach (var record in records)
            {
                var item = new JsonObject(); foreach (var pair in record) if (field.Key == "siteLogins" && pair.Key == "username") item["usernameProtected"] = UserSecret.Seal(pair.Value); else item[pair.Key] = pair.Value;
                result.Add(item);
            }
            _draft.Preferences[field.Key] = result;
        });
    }

    private static bool ValidInput(SettingField field, string text)
    {
        if (field.Kind == "date") return text.Length == 0 || DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
        if (text.Length == 0 && field.Key is "proxyHTTPPort" or "proxyHTTPSPort" or "proxyFTPPort") return true;
        if (!long.TryParse(text, out var number) || number < 0) return false;
        if (field.Key == "parallel") return number is >= 1 and <= 10;
        if (field.Key == "retryCount") return number <= 10;
        if (field.Key == "timeout") return number is >= 1 and <= 3600;
        if (field.Key == "retryDelay") return number <= 3600;
        if (field.Key.EndsWith("Port", StringComparison.Ordinal)) return number is >= 1 and <= 65535;
        return true;
    }
    private bool HasValidationErrors() => _schema.Sections.SelectMany(section => section.Fields).Any(field => _invalidInputs.Contains(field.Key) && DependencyEnabled(field.Depends));
    private bool DependencyEnabled(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return true; var parts = expression.Split('=', 2); return parts.Length == 2 ? Value(parts[0]) == parts[1] : Value(parts[0]).Equals("true", StringComparison.OrdinalIgnoreCase);
    }
    private void ShowInputValidation(TextBox box, SettingField field)
    {
        if (_invalidInputs.Contains(field.Key)) { box.BorderBrush = new SolidColorBrush(Color.FromRgb(195, 75, 65)); box.ToolTip = _locale.S(field.Kind == "date" ? "native.invalid_date" : "native.invalid_number"); }
        else { box.SetResourceReference(BorderBrushProperty, "Line"); box.ToolTip = _locale.S(IsLive(field.Key) ? "native.active_option" : "native.pending_option"); }
        UpdateSaveStatus();
    }

    private static bool IsLive(string key) => key is "closeToTray" or "categories" or "useCategoryFolders" or "temporaryFolder" or "dialEnabled" or "dialConnection" or "dialUser" or "dialPassword" or "dialAttempts" or "dialSavePassword" or "dialDelay" or "ftpPassive" or "ftpUser" or "ftpPassword" or "startup" or "showTray" or "progressMinimizeToTray" or "progressStart" or "minimizeToTray" or "wakeScheduled" or "scannerEnabled" or "scannerPath" or "scannerArguments" or "soundComplete" or "soundCompleteFile" or "soundFailed" or "soundFailedFile" or "soundQueueStart" or "soundQueueStartFile" or "soundQueueEnd" or "soundQueueEndFile" or "proxyMode" or "pacUrl" or "proxyHost" or "proxyPort" or "proxyUser" or "proxyPassword" or "socksEnabled" or "socksHost" or "socksPort" or "socksVersion" or "socksUser" or "socksPassword" or "proxyExceptions" or "proxyHTTPHost" or "proxyHTTPPort" or "proxyHTTPSHost" or "proxyHTTPSPort" or "serverRules" or "siteLogins" or "quotaEnabled" or "quotaMB" or "quotaHours" or "language" or "palette" or "appearance" or "downloadDirectory" or "parallel" or "clipboard" or "extensions" or "retryCount" or "retryDelay" or "timeout" or "userAgent" or "speedLimit" or "showComplete" or "closeProgress" or "progressSpeedTab" or "progressCompletionTab" or "progressHideTab" or "maxConnections";
    private static string GroupFor(string key) => key switch
    {
        "language" or "palette" or "appearance" => "native.group_appearance",
        "browserChrome" or "browserEdge" or "browserFirefox" or "browserOpera" or "browserSafari" => "native.group_browsers",
        "startup" or "legacyMonitor" or "clipboard" or "advancedIntegration" => "native.group_integration",
        "parallel" or "maxConnections" or "connectionType" => "native.group_transfer",
        _ => "native.group_options"
    };
    private void MarkDirty() { _dirty = true; UpdateSaveStatus(); }
    private void UpdateSaveStatus() { var invalid = HasValidationErrors(); _applyButton.IsEnabled = _dirty && !invalid; _saveStatus.Text = _locale.S(invalid ? "native.settings_invalid" : _dirty ? "native.settings_unsaved" : "native.settings_saved"); }
    private void ResetSection(SettingSection section)
    {
        if (AppDialog.Show(this, _locale.S("native.reset_question", ("section", _locale.S(section.TitleKey))), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SaveTables(); ResetSectionValues(section);
    }
    private void ResetSectionValues(SettingSection section)
    {
        var defaults = new UserSettings(); foreach (var field in section.Fields)
        {
            _rawInputs.Remove(field.Key); _invalidInputs.Remove(field.Key); if(field.Secret)_draft.ProtectedSecrets.Remove(field.Key);
            switch (field.Key) { case "language": _draft.Language = defaults.Language; break; case "palette": _draft.Palette = defaults.Palette; break; case "appearance": _draft.Appearance = defaults.Appearance; break; case "downloadDirectory": _draft.DownloadDirectory = defaults.DownloadDirectory; break; default: _draft.Preferences[field.Key] = _schema.Defaults[field.Key]?.DeepClone(); break; }
        }
        MarkDirty(); BuildFields(section);
    }

    private string Value(string key) => key switch { "language" => _draft.Language, "palette" => _draft.Palette, "appearance" => _draft.Appearance, "downloadDirectory" => _draft.DownloadDirectory, "temporaryFolder" => string.IsNullOrWhiteSpace(_draft.Preferences[key]?.ToString())?UserSettings.DefaultTemporaryDirectory:_draft.Preferences[key]!.ToString(), _ => _draft.Preferences[key]?.ToString() ?? "" };
    private void Set<T>(string key, T value)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var dialPassword=key=="dialSavePassword"?_draft.ReadSecret("dialPassword"):null;
        if (!string.Equals(Value(key), text, StringComparison.OrdinalIgnoreCase)) MarkDirty();
        switch (key) { case "language": _draft.Language = text; break; case "palette": _draft.Palette = text; break; case "appearance": _draft.Appearance = text; break; case "downloadDirectory": _draft.DownloadDirectory = text; break; default: _draft.Preferences[key] = JsonValue.Create(value); break; }
        if(dialPassword is not null)_draft.WriteSecret("dialPassword",dialPassword);
    }
    private void UpdateDependencies()
    {
        foreach (var (control, expression) in _dependencies) { var parts = expression.Split('=', 2); control.IsEnabled = parts.Length == 2 ? Value(parts[0]) == parts[1] : Value(parts[0]).Equals("true", StringComparison.OrdinalIgnoreCase); }
        UpdateSaveStatus();
    }
    private void SaveTables() { foreach (var commit in _tableCommits) commit(); }
    private bool Commit(bool close)
    {
        SaveTables();
        if (HasValidationErrors()) { UpdateSaveStatus(); return false; }
        try { _ = Path.GetFullPath(_draft.DownloadDirectory); _ = _draft.TransferPolicy(); _apply(_draft.Copy()); _dirty = false; UpdateSaveStatus(); if (close) { _allowClose = true; Close(); } return true; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { AppDialog.Show(this, _locale.S(exception is ArgumentException { Message: "errors.invalid_transfer_settings" } ? "errors.invalid_transfer_settings" : "errors.invalid_folder"), Title); return false; }
    }
}

