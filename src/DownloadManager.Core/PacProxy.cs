using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
namespace DownloadManager.Core;
internal sealed class PacProxy(NetworkPolicy policy):IWebProxy
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Options{public int Flags;public int AutoDetect;[MarshalAs(UnmanagedType.LPWStr)]public string Url;public IntPtr Reserved;public int ReservedFlags;[MarshalAs(UnmanagedType.Bool)]public bool AutoLogon;}
    [StructLayout(LayoutKind.Sequential)]private struct Info{public int Access;public IntPtr Proxy;public IntPtr Bypass;}
    [DllImport("winhttp.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr WinHttpOpen(string agent,int access,string? proxy,string? bypass,int flags);
    [DllImport("winhttp.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool WinHttpSetTimeouts(IntPtr session,int resolve,int connect,int send,int receive);
    [DllImport("winhttp.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool WinHttpGetProxyForUrl(IntPtr session,string url,ref Options options,out Info info);
    [DllImport("winhttp.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool WinHttpCloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalFree(IntPtr memory);
    private readonly object _gate=new();private readonly Dictionary<string,(DateTimeOffset Time,Uri Result)> _cache=[];
    public ICredentials? Credentials{get;set;}=policy.ProxyCredential;
    public bool IsBypassed(Uri destination)=>GetProxy(destination)==destination;
    public Uri GetProxy(Uri destination)
    {
        if(policy.Bypass.Any(pattern=>NetworkPolicy.Wildcard(pattern,destination.IdnHost)||NetworkPolicy.Wildcard(pattern,destination.AbsoluteUri)))return destination;
        lock(_gate)
        {
            if(_cache.TryGetValue(destination.AbsoluteUri,out var cached)&&DateTimeOffset.UtcNow-cached.Time<TimeSpan.FromSeconds(60))return cached.Result;
            var session=WinHttpOpen("DownloadManager/0.4",1,null,null,0);if(session==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());Info info=default;
            try
            {
                WinHttpSetTimeouts(session,5000,5000,10000,10000);var options=new Options{Flags=2,Url=policy.PacUrl};if(!WinHttpGetProxyForUrl(session,destination.AbsoluteUri,ref options,out info))throw new Win32Exception(Marshal.GetLastWin32Error());
                var text=Marshal.PtrToStringUni(info.Proxy);Uri result=destination;
                if(info.Access!=1&&!string.IsNullOrWhiteSpace(text)){var first=text.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).First();if(first.Contains('=')){var candidates=text.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);first=candidates.FirstOrDefault(p=>p.StartsWith(destination.Scheme+"=",StringComparison.OrdinalIgnoreCase))??first;first=first[(first.IndexOf('=')+1)..];}result=new Uri(first.Contains("://")?first:"http://"+first);}
                if(_cache.Count>200)_cache.Clear();_cache[destination.AbsoluteUri]=(DateTimeOffset.UtcNow,result);return result;
            }
            finally{if(info.Proxy!=IntPtr.Zero)GlobalFree(info.Proxy);if(info.Bypass!=IntPtr.Zero)GlobalFree(info.Bypass);WinHttpCloseHandle(session);}
        }
    }
}
