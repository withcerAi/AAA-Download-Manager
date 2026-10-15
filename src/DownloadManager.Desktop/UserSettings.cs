using DownloadManager.Core;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace DownloadManager.Desktop;

public sealed class UserSettings
{
    public bool SequentialDefaultsApplied {get;set;}
    public static string DefaultTemporaryDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DownloadManager","Temporary Downloads");
    public string Language { get; set; } = "en";
    public string Palette { get; set; } = "classic";
    public string Appearance { get; set; } = "system";
    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AAA Download Manager");
    [System.Text.Json.Serialization.JsonIgnore] public Dictionary<string,string> TransientSecrets {get;set;}=[];
    public Dictionary<string,string> ProtectedSecrets { get; set; } = [];
    public string ReadSecret(string key) { if(TransientSecrets.TryGetValue(key,out var temporary))return temporary;if (!ProtectedSecrets.TryGetValue(key, out var sealedValue)) return ""; try { return UserSecret.Open(sealedValue); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or FormatException) { return ""; } }
    public void WriteSecret(string key,string value) { if(key=="dialPassword"&&Preferences["dialSavePassword"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)!=true){ProtectedSecrets.Remove(key);TransientSecrets[key]=value;return;}TransientSecrets.Remove(key);if(value.Length==0) ProtectedSecrets.Remove(key); else ProtectedSecrets[key]=UserSecret.Seal(value); }
    public JsonObject Preferences { get; set; } = new();
    public static UserSettings Load(string path)
    {
        var settings=File.Exists(path)?JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path))??new():new UserSettings();
        if(settings.Preferences["temporaryFolder"]?.ToString().Equals(@"D:\Downloads\.temp",StringComparison.OrdinalIgnoreCase)==true)settings.Preferences["temporaryFolder"]="";
        if(settings.Preferences["categories"] is JsonArray rows)foreach(var row in rows.OfType<JsonObject>()){
            var id=row["id"]?.ToString()??"";var suffix=id switch{"video"=>"Video","music"=>"Music","software"=>"Programs","archive"=>"Compressed","document"=>"Documents",_=>""};
            if(row["folder"]?.ToString().Equals(@"D:\Downloads"+(suffix.Length>0?"\\"+suffix:""),StringComparison.OrdinalIgnoreCase)==true)row["folder"]=suffix;
        }
        return settings;
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".tmp";
        using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(output, this, new JsonSerializerOptions { WriteIndented = true }); output.Flush(true); }
        File.Move(staging, path, true);
    }
    public UserSettings Copy() {var copy=JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(this))!;copy.TransientSecrets=new(TransientSecrets);return copy;}
    public DialPolicy DialPolicy() => new(Preferences["dialEnabled"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true,Preferences["dialConnection"]?.ToString()??"",ReadSecret("dialUser"),ReadSecret("dialPassword"),int.TryParse(Preferences["dialAttempts"]?.ToString(),out var count)?count:3,int.TryParse(Preferences["dialDelay"]?.ToString(),out var seconds)?seconds:30);
    public TransferSettings TransferPolicy()
    {
        int Number(string key, int fallback) => int.TryParse(Preferences[key]?.ToString(), out var value) ? value : fallback;
        var limit = Preferences["speedLimit"]?.ToString() switch { "2 MB/s" => 2L << 20, "5 MB/s" => 5L << 20, "10 MB/s" => 10L << 20, _ => 0 };
        var agent = Preferences["userAgent"]?.ToString();
        string Text(string key) => Preferences[key]?.ToString() ?? "";
        bool Enabled(string key) => Text(key).Equals("true",StringComparison.OrdinalIgnoreCase);
        var socks=Enabled("socksEnabled");
        NetworkCredential? Credential(string prefix) => ReadSecret(prefix+"User").Length==0 ? null : new NetworkCredential(ReadSecret(prefix+"User"),ReadSecret(prefix+"Password"));
        var network = new NetworkPolicy {
            Mode=socks ? ProxySelection.Socks : Text("proxyMode") switch { "none"=>ProxySelection.Direct,"manual"=>ProxySelection.Manual,"pac"=>ProxySelection.Pac,_=>ProxySelection.System },
            Host=Text(socks?"socksHost":"proxyHost"),Port=Number(socks?"socksPort":"proxyPort",socks?1080:8080),SocksVersion=Text("socksVersion") is {Length:>0} version?version:"5",PacUrl=Text("pacUrl"),
            HttpHost=Text("proxyHTTPHost"),HttpPort=Number("proxyHTTPPort",8080),HttpsHost=Text("proxyHTTPSHost"),HttpsPort=Number("proxyHTTPSPort",8080),
            ProxyCredential=Credential(socks?"socks":"proxy"),Bypass=Text("proxyExceptions").Split([';',' ', '\n','\r',','],StringSplitOptions.RemoveEmptyEntries),
            ServerRules=(Preferences["serverRules"] as JsonArray ?? []).OfType<JsonObject>().Where(row=>row["host"]?.ToString().Length>0).Select(row=>new ServerConnectionRule(row["host"]!.ToString(),int.TryParse(row["connections"]?.ToString(),out var count)?count:8)).ToArray(),
            Logins=(Preferences["siteLogins"] as JsonArray ?? []).OfType<JsonObject>().Where(row=>row["url"]?.ToString().Length>0).Select(row=>new SiteLoginRule(row["url"]!.ToString(),row["authentication"]?.ToString() ?? "Auto",new NetworkCredential(Open(row["usernameProtected"]),Open(row["passwordProtected"])))) .ToArray()
        };
        static string Open(JsonNode? node) { if(node is null)return ""; return UserSecret.Open(node.ToString()); }
        network.Validate();
        var temporary=Preferences["temporaryFolder"]?.ToString()??"";if(temporary.Equals(@"D:\Downloads\.temp",StringComparison.OrdinalIgnoreCase))temporary="";
        var categories=new List<CategoryFolderRule>();
        if(Preferences["useCategoryFolders"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)!=false&&Preferences["categories"] is JsonArray categoryRows)
        foreach(var row in categoryRows.OfType<JsonObject>()){
            var categoryFolder=row["folder"]?.ToString()??"";var id=row["id"]?.ToString()??"";
            var legacy=id switch{"video"=>"Video","music"=>"Music","software"=>"Programs","archive"=>"Compressed","document"=>"Documents",_=>""};
            if(categoryFolder.Equals(@"D:\Downloads"+(legacy.Length>0?"\\"+legacy:""),StringComparison.OrdinalIgnoreCase))categoryFolder=legacy;
            categories.Add(new(row["extensions"]?.ToString()??"",categoryFolder));
        }
        return new TransferSettings { CategoryRoot=DownloadDirectory,CategoryFolders=categories,TemporaryDirectory=temporary, MediaMuxerPath=Path.Combine(AppContext.BaseDirectory,"media-tools","bin","ffmpeg.exe"), FtpUser=ReadSecret("ftpUser"),FtpPassword=ReadSecret("ftpPassword"),FtpPassive=Preferences["ftpPassive"] is null||Enabled("ftpPassive"), Network=network, Quota = new QuotaPolicy(Preferences["quotaEnabled"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true, (long)Number("quotaMB",200) << 20, Number("quotaHours",5)), MaxConnections = Number("maxConnections", 8), TimeoutSeconds = Number("timeout", 30), RetryCount = Number("retryCount", 5), RetryDelaySeconds = Number("retryDelay", 10), BytesPerSecond = limit, UserAgent = string.IsNullOrWhiteSpace(agent) ? "DownloadManager/0.1" : agent }.Validated();
    }
    public void ApplyTheme()
    {
        var systemDark = false;
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"); systemDark = key?.GetValue("AppsUseLightTheme") is int light && light == 0; }
        catch (System.Security.SecurityException) { }
        var dark = Appearance == "dark" || Appearance == "system" && systemDark;
        var shades = Palette switch
        {
            "emerald" => dark ? new[] { "#15221F", "#1C2B28", "#E3F1EB", "#A0B9AE", "#37544A", "#6CC9A5", "#204B3E" } : new[] { "#FFFFFF", "#EEF4F2", "#203830", "#566F64", "#C7D8D0", "#146D54", "#D2EEE2" },
            "violet" => dark ? new[] { "#1E1A2B", "#292438", "#EEE9F9", "#B4A7C8", "#4A3F61", "#BFA0F1", "#483565" } : new[] { "#FFFFFF", "#F2F0F7", "#322B44", "#6E627E", "#D5CCDF", "#7051AB", "#E6DAFA" },
            "amber" => dark ? new[] { "#201D18", "#2B2721", "#F1E9DC", "#BBAE99", "#51483B", "#E4BA6C", "#544126" } : new[] { "#FFFDFA", "#F5F2EC", "#3C3327", "#746957", "#DACFBD", "#8A6015", "#F5E3BD" },
            "graphite" => dark ? new[] { "#1B1C20", "#25262A", "#ECECF0", "#A8A9B3", "#45464F", "#A6BADB", "#3A4050" } : new[] { "#FFFFFF", "#EFEFF1", "#272930", "#636570", "#CDD0D5", "#4B5D7B", "#E0E4ED" },
            _ => dark ? new[] { "#171F29", "#202833", "#E4EBF3", "#A0ADBD", "#3B4757", "#69A9F0", "#243D5B" } : new[] { "#FFFFFF", "#F0F2F5", "#202A36", "#626D7B", "#CBD1D9", "#1767BB", "#DCECFF" }
        };
        var keys = new[] { "Surface", "Chrome", "Ink", "MutedInk", "Line", "Accent", "Selection" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(shades[i]));
        Application.Current.Resources[SystemColors.WindowBrushKey] = Application.Current.Resources["Surface"];
        Application.Current.Resources[SystemColors.WindowTextBrushKey] = Application.Current.Resources["Ink"];
        Application.Current.Resources[SystemColors.ControlBrushKey] = Application.Current.Resources["Chrome"];
        Application.Current.Resources[SystemColors.ControlTextBrushKey] = Application.Current.Resources["Ink"];
        Application.Current.Resources[SystemColors.HighlightBrushKey] = Application.Current.Resources["Selection"];
        Application.Current.Resources[SystemColors.HighlightTextBrushKey] = Application.Current.Resources["Ink"];
        Application.Current.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Application.Current.Resources["Selection"];
        Application.Current.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Application.Current.Resources["Ink"];
    }
}

