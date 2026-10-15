using System.Net;
using System.Text.Json;
namespace DownloadManager.Core;

public sealed record SessionRequest
{
    public string Url {get;init;}="";
    public string Method {get;init;}="GET";
    public Dictionary<string,string> Headers {get;init;}=[];
    public string BodyBase64 {get;init;}="";
    public SessionRequest Validated()
    {
        if(!Uri.TryCreate(Url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")||uri.UserInfo.Length>0||Url.Any(char.IsControl)||Method is not("GET" or "POST")||Headers is null||Headers.Count>10||BodyBase64 is null||BodyBase64.Length>350000)throw new ArgumentException("browser.invalid_context");
        foreach(var header in Headers)if(header.Key.ToLowerInvariant() is not("cookie" or "authorization" or "referer" or "user-agent" or "accept" or "accept-language" or "content-type")||header.Value is null||header.Value.Length>65536||header.Value.Any(char.IsControl))throw new ArgumentException("browser.invalid_context");
        try{if(Convert.FromBase64String(BodyBase64).Length>262144||Method=="GET"&&BodyBase64.Length>0)throw new ArgumentException("browser.invalid_context");}catch(FormatException){throw new ArgumentException("browser.invalid_context");}
        return this;
    }
    public string Protect()=>UserSecret.Seal(JsonSerializer.Serialize(Validated()));
    public static SessionRequest? Restore(string? text)=>string.IsNullOrEmpty(text)?null:JsonSerializer.Deserialize<SessionRequest>(UserSecret.Open(text))?.Validated();
    public void Apply(HttpRequestMessage request)
    {
        Validated();if(request.RequestUri?.AbsoluteUri!=new Uri(Url).AbsoluteUri)throw new ArgumentException("browser.context_scope");
        request.Method=new HttpMethod(Method);request.Version=HttpVersion.Version20;request.VersionPolicy=HttpVersionPolicy.RequestVersionOrLower;
        if(Method=="POST")request.Content=new ByteArrayContent(Convert.FromBase64String(BodyBase64));
        foreach(var header in Headers){if(header.Key.Equals("content-type",StringComparison.OrdinalIgnoreCase)){if(request.Content is not null){request.Content.Headers.Remove(header.Key);request.Content.Headers.TryAddWithoutValidation(header.Key,header.Value);}}else{request.Headers.Remove(header.Key);request.Headers.TryAddWithoutValidation(header.Key,header.Value);}}
    }
}

// Raw Cookie/Authorization headers must never follow cross-origin redirects.
internal sealed class SessionRedirectHandler(HttpMessageHandler inner):DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
    {
        var current=request;var owned=false;
        try{
            for(var redirects=0;;redirects++){
                var response=await base.SendAsync(current,cancellation).ConfigureAwait(false);
                if((int)response.StatusCode is not(301 or 302 or 303 or 307 or 308)||response.Headers.Location is not {} location)return response;
                if(redirects>=10){response.Dispose();throw new HttpRequestException("Redirect limit exceeded");}
                var nextUri=new Uri(current.RequestUri!,location);var sameOrigin=nextUri.Scheme==current.RequestUri!.Scheme&&nextUri.IdnHost==current.RequestUri.IdnHost&&nextUri.Port==current.RequestUri.Port;
                if(nextUri.Scheme is not("http" or "https")||nextUri.UserInfo.Length>0||current.RequestUri.Scheme=="https"&&nextUri.Scheme!="https"||!sameOrigin&&current.Method==HttpMethod.Post&&(int)response.StatusCode is 307 or 308){response.Dispose();throw new HttpRequestException("Unsafe session redirect");}
                var method=(int)response.StatusCode==303&&current.Method!=HttpMethod.Head||(int)response.StatusCode is 301 or 302&&current.Method==HttpMethod.Post?HttpMethod.Get:current.Method;
                var next=new HttpRequestMessage(method,nextUri){Version=current.Version,VersionPolicy=current.VersionPolicy};
                foreach(var header in current.Headers){if(!sameOrigin&&header.Key.ToLowerInvariant() is "cookie" or "authorization" or "referer")continue;next.Headers.TryAddWithoutValidation(header.Key,header.Value);}
                if(method==current.Method&&current.Content is not null){next.Content=new ByteArrayContent(await current.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false));foreach(var header in current.Content.Headers)next.Content.Headers.TryAddWithoutValidation(header.Key,header.Value);}
                response.Dispose();if(owned)current.Dispose();current=next;owned=true;
            }
        }finally{if(owned)current.Dispose();}
    }
}
