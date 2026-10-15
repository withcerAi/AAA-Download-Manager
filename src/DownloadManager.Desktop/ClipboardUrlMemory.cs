using DownloadManager.Core;
using System.Runtime.InteropServices;
namespace DownloadManager.Desktop;
// Session-only cache. Never reads OS clipboard history or stores signed URLs on disk.
public sealed class ClipboardUrlMemory
{
    public string? LastUrl{get;private set;}
    public bool UsedCached{get;private set;}
    public string? Read(Func<string> reader)
    {
        UsedCached=false;
        try{var text=reader();var found=LinkImport.Extract(text).Links.FirstOrDefault()?.Url;if(found is not null){LastUrl=found;return found;}}
        catch(Exception error)when(error is ExternalException or System.Security.SecurityException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException){}
        UsedCached=LastUrl is not null;return LastUrl;
    }
}
