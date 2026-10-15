using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DownloadManager.Core;

public sealed record GrabberOptions
{
    public string StartUrl{get;init;}="";
    public int Depth{get;init;}=1;
    public int MaximumPages{get;init;}=50;
    public string Extensions{get;init;}="zip pdf mp4 mp3";
    public string ExcludedPatterns{get;init;}="";
    public bool StayInStartFolder{get;init;}=true;
    public void Validate(){if(LinkImport.Normalize(StartUrl) is null||new Uri(StartUrl).Scheme is not("http" or "https")||Depth is <0 or >10||MaximumPages is <1 or >1000||Extensions.Length>4096||ExcludedPatterns.Length>16384)throw new ArgumentException("grabber.invalid");}
}
public sealed record GrabberProgress(int Pages,int Links,string CurrentUrl);
public sealed record GrabberResult(IReadOnlyList<ImportedLink> Links,int Pages,int Skipped);

public static partial class WebsiteGrabber
{
    [GeneratedRegex("(?:href|src)\\s*=\\s*(?:\"(?<url>[^\"]+)\"|'(?<url>[^']+)'|(?<url>[^\\s>]+))",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,300)]
    private static partial Regex References();
    public static async Task<GrabberResult> ScanAsync(GrabberOptions options,NetworkPolicy network,string userAgent,IProgress<GrabberProgress>? progress=null,CancellationToken cancellation=default)
    {
        options.Validate();var start=new Uri(options.StartUrl);var folder=new Uri(start,".").AbsolutePath;
        var extensions=options.Extensions.Split([' ',',',';','\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(s=>s.TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded=options.ExcludedPatterns.Split(['\r','\n',';'],StringSplitOptions.RemoveEmptyEntries);
        var pending=new Queue<(string Url,int Depth)>();pending.Enqueue((start.AbsoluteUri,0));var seen=new HashSet<string>(StringComparer.Ordinal);var found=new Dictionary<string,ImportedLink>(StringComparer.Ordinal);var pages=0;var skipped=0;
        using var client=DownloadHttpClient.Create(network);
        while(pending.Count>0&&pages<options.MaximumPages&&found.Count<LinkImport.MaximumLinks){
            cancellation.ThrowIfCancellationRequested();var item=pending.Dequeue();if(!seen.Add(item.Url))continue;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try{
                using var request=new HttpRequestMessage(HttpMethod.Get,item.Url);request.Headers.UserAgent.ParseAdd(userAgent);
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
                if(!response.IsSuccessStatusCode){skipped++;continue;}
                var effective=response.RequestMessage?.RequestUri??new Uri(item.Url);
                if(!SameOrigin(start,effective)||response.Content.Headers.ContentType?.MediaType is not("text/html" or "application/xhtml+xml")||response.Content.Headers.ContentLength>2*1024*1024){skipped++;continue;}
                await using var body=await response.Content.ReadAsStreamAsync(timeout.Token);using var bytes=new MemoryStream();var buffer=new byte[32768];int count;
                while((count=await body.ReadAsync(buffer,timeout.Token))>0){if(bytes.Length+count>2*1024*1024)throw new InvalidDataException();bytes.Write(buffer,0,count);}
                var encoding=Encoding.UTF8;try{if(response.Content.Headers.ContentType?.CharSet is {} charset)encoding=Encoding.GetEncoding(charset.Trim('"'));}catch(ArgumentException){}
                var html=encoding.GetString(bytes.ToArray());pages++;
                foreach(Match match in References().Matches(html)){
                    var reference=WebUtility.HtmlDecode(match.Groups["url"].Value);if(!Uri.TryCreate(effective,reference,out var uri)||!SameOrigin(start,uri)||uri.UserInfo.Length>0)continue;
                    var normalized=LinkImport.Normalize(uri.AbsoluteUri);if(normalized is null||excluded.Any(pattern=>NetworkPolicy.Wildcard(pattern,normalized)))continue;
                    if(options.StayInStartFolder&&!uri.AbsolutePath.StartsWith(folder,StringComparison.Ordinal))continue;
                    var extension=Path.GetExtension(uri.AbsolutePath).TrimStart('.');
                    if(extensions.Contains(extension)){found.TryAdd(normalized,new(normalized,DownloadEngine.SafeFileName(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last())),uri.IdnHost));if(found.Count==LinkImport.MaximumLinks)break;}
                    else if(item.Depth<options.Depth&&(extension.Length==0||extension.ToLowerInvariant() is "html" or "htm" or "php" or "asp" or "aspx")){if(pending.Count<2000&&!seen.Contains(normalized))pending.Enqueue((normalized,item.Depth+1));}
                }
                progress?.Report(new(pages,found.Count,item.Url));
            }catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){skipped++;}
            catch(Exception error)when(error is HttpRequestException or IOException or RegexMatchTimeoutException){skipped++;}
        }
        return new(found.Values.ToArray(),pages,skipped);
    }
    private static bool SameOrigin(Uri a,Uri b)=>a.Scheme==b.Scheme&&a.IdnHost==b.IdnHost&&a.Port==b.Port;
}
