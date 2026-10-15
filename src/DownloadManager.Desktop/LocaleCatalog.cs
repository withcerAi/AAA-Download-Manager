using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;

namespace DownloadManager.Desktop;

public sealed record LocaleInfo(string Code, string NativeName, string Direction);

public sealed class LocaleCatalog : INotifyPropertyChanged
{
    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = [];
    private readonly Dictionary<string, LocaleInfo> _metadata = [];
    private string _current = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<LocaleInfo> Languages => _metadata.Values.OrderBy(info => info.Code).ToArray();
    public string Code => _current;
    public FlowDirection Direction => _metadata[_current].Direction == "rtl" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public CultureInfo Culture { get { try { return CultureInfo.GetCultureInfo(_current); } catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; } } }
    public string this[string key] => S(key);

    public LocaleCatalog(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("meta", out var meta)) continue;
            var code = meta.GetProperty("code").GetString()!;
            var direction = meta.GetProperty("direction").GetString()!;
            if (direction is not ("ltr" or "rtl") || _catalogs.ContainsKey(code)) throw new InvalidDataException("errors.locale_configuration");
            var messages = JsonSerializer.Deserialize<Dictionary<string, string>>(document.RootElement.GetProperty("messages"))!;
            _catalogs.Add(code, messages);
            _metadata.Add(code, new LocaleInfo(code, meta.GetProperty("nativeName").GetString()!, direction));
        }
        if (!_catalogs.ContainsKey("en")) throw new InvalidDataException("errors.locale_configuration");
    }

    public string S(string key, params (string Name, object? Value)[] values)
    {
        var text = _catalogs[_current].GetValueOrDefault(key) ?? _catalogs["en"].GetValueOrDefault(key) ?? key;
        foreach (var (name, value) in values) text = text.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.InvariantCulture));
        return text;
    }
    public void Select(string code)
    {
        _current = _catalogs.ContainsKey(code) ? code : "en";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Direction)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code)));
    }
    public void Bind(DependencyObject target, DependencyProperty property, string key) => BindingOperations.SetBinding(target, property, new Binding("[" + key + "]") { Source = this });
}
