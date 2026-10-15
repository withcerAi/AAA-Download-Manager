using System.Net;
namespace DownloadManager.Core;
public enum ProxySelection{Direct,System,Manual,Socks,Pac}
public sealed record SiteLoginRule(string Prefix,string Authentication,NetworkCredential? Credential,bool UseWindowsIdentity=false);
public sealed record ServerConnectionRule(string Pattern,int Connections);
public sealed record NetworkPolicy
{
    public ProxySelection Mode{get;init;}=ProxySelection.System;
    public string Host{get;init;}="";public int Port{get;init;}=8080;
    public string HttpHost{get;init;}="";public int HttpPort{get;init;}=8080;
    public string HttpsHost{get;init;}="";public int HttpsPort{get;init;}=8080;
    public string SocksVersion{get;init;}="5";public string PacUrl{get;init;}="";
    public NetworkCredential? ProxyCredential{get;init;}
    public IReadOnlyList<string> Bypass{get;init;}=[];
    public IReadOnlyList<SiteLoginRule> Logins{get;init;}=[];
    public IReadOnlyList<ServerConnectionRule> ServerRules{get;init;}=[];
    public void Validate()
    {
        if(!Enum.IsDefined(Mode)||Port is <1 or >65535||HttpPort is <1 or >65535||HttpsPort is <1 or >65535||Bypass.Count>200||Logins.Count>200||ServerRules.Count>200)throw new ArgumentException("network.invalid");
        if(Mode is ProxySelection.Manual or ProxySelection.Socks && (string.IsNullOrWhiteSpace(Host)||Uri.CheckHostName(Host)==UriHostNameType.Unknown))throw new ArgumentException("network.invalid");
        if(Mode==ProxySelection.Socks&&SocksVersion is not("4" or "4a" or "5"))throw new ArgumentException("network.invalid");
        if(Mode==ProxySelection.Pac&&(!Uri.TryCreate(PacUrl,UriKind.Absolute,out var pac)||pac.Scheme is not("http" or "https")||pac.UserInfo.Length>0))throw new ArgumentException("network.invalid");
        foreach(var login in Logins)if(!Uri.TryCreate(login.Prefix,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0||login.Authentication is not("Basic" or "Digest" or "NTLM" or "Negotiate" or "Kerberos" or "Auto"))throw new ArgumentException("network.invalid");
        if(Bypass.Any(p=>p.Length>1024)||ServerRules.Any(r=>r.Pattern.Length is <1 or >1024||r.Connections is <1 or >32))throw new ArgumentException("network.invalid");
    }
    public int ConnectionsFor(string url,int fallback){var uri=new Uri(url);return ServerRules.FirstOrDefault(r=>Wildcard(r.Pattern,uri.IdnHost)||Wildcard(r.Pattern,uri.AbsoluteUri))?.Connections??fallback;}
    public static bool Wildcard(string pattern,string text)
    {
        var p=0;var t=0;var star=-1;var saved=0;
        while(t<text.Length){if(p<pattern.Length&&(pattern[p]=='?'||char.ToUpperInvariant(pattern[p])==char.ToUpperInvariant(text[t]))){p++;t++;}else if(p<pattern.Length&&pattern[p]=='*'){star=p++;saved=t;}else if(star>=0){p=star+1;t=++saved;}else return false;}
        while(p<pattern.Length&&pattern[p]=='*')p++;return p==pattern.Length;
    }
}
public static class DownloadHttpClient
{
    public static HttpClient Create(NetworkPolicy policy,bool session=false)
    {
        policy.Validate();var credentials=new CredentialCache();
        foreach(var login in policy.Logins)foreach(var kind in login.Authentication=="Auto"?new[]{"Negotiate","NTLM","Digest","Basic"}:new[]{login.Authentication})credentials.Add(new Uri(login.Prefix),kind,login.UseWindowsIdentity?CredentialCache.DefaultNetworkCredentials:login.Credential??new());
        var handler=new SocketsHttpHandler{UseCookies=!session,AutomaticDecompression=DecompressionMethods.None,MaxResponseDrainSize=0,AllowAutoRedirect=!session,MaxAutomaticRedirections=10,ConnectTimeout=TimeSpan.FromSeconds(30),Credentials=credentials,UseProxy=policy.Mode!=ProxySelection.Direct,Proxy=policy.Mode switch{ProxySelection.Manual or ProxySelection.Socks=>new RoutingProxy(policy),ProxySelection.Pac=>new PacProxy(policy),_=>null}};
        return new HttpClient(session?new SessionRedirectHandler(handler):handler){Timeout=Timeout.InfiniteTimeSpan,DefaultRequestVersion=HttpVersion.Version20,DefaultVersionPolicy=HttpVersionPolicy.RequestVersionOrLower};
    }
    private sealed class RoutingProxy(NetworkPolicy policy):IWebProxy
    {
        public ICredentials? Credentials{get;set;}=policy.ProxyCredential;
        public bool IsBypassed(Uri host)=>policy.Bypass.Any(pattern=>NetworkPolicy.Wildcard(pattern,host.IdnHost)||NetworkPolicy.Wildcard(pattern,host.AbsoluteUri));
        public Uri GetProxy(Uri destination){if(IsBypassed(destination))return destination;var host=destination.Scheme=="https"&&policy.HttpsHost.Length>0?policy.HttpsHost:destination.Scheme=="http"&&policy.HttpHost.Length>0?policy.HttpHost:policy.Host;var port=host==policy.HttpsHost&&policy.HttpsHost.Length>0?policy.HttpsPort:host==policy.HttpHost&&policy.HttpHost.Length>0?policy.HttpPort:policy.Port;return new UriBuilder(policy.Mode==ProxySelection.Socks?"socks"+policy.SocksVersion:"http",host,port).Uri;}
    }
}
