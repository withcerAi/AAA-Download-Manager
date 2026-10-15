using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownloadManager.Core;
public sealed partial class DownloadEngine
{
    private sealed record MediaCache(string? Tag,DateTimeOffset? Modified,long? RemoteSize,long Size,string Hash);
    private static string MediaDirectory(DownloadJob job)=>Path.Combine(Path.GetDirectoryName(job.PartialPath)!,".dm-"+job.Id+".media");
    public static bool IsAdaptiveUrl(string url)=>Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant() is ".m3u8" or ".mpd";
    public static string SuggestedName(string url){var uri=new Uri(url);var name=SafeFileName(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()));return IsAdaptiveUrl(url)?Path.ChangeExtension(name,".mp4"):name;}
    private static void ClearMediaCache(DownloadJob job)
    {
        var directory=MediaDirectory(job);if(!Directory.Exists(directory))return;
        // Only filenames created by this worker are removed; no recursive deletion.
        foreach(var path in Directory.EnumerateFiles(directory))if(Path.GetFileName(path).StartsWith("part-",StringComparison.Ordinal)||Path.GetFileName(path).StartsWith("stream-",StringComparison.Ordinal)||Path.GetFileName(path).StartsWith("key-",StringComparison.Ordinal))File.Delete(path);
        if(!Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);
    }
    private async Task TransferAdaptiveAsync(Entry entry,TransferSettings settings,CancellationToken cancellation)
    {
        var job=entry.Job;var clock=Stopwatch.StartNew();var previousActive=job.ActiveSeconds;var previousTransferred=job.TransferredBytes;long transferred=0,received=0;var sample=Stopwatch.StartNew();long sampleBytes=0;
        var keys=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        var session=SessionRequest.Restore(job.ProtectedSession);using var limiter=new TransferRateLimiter(()=>Volatile.Read(ref entry.SpeedLimit) is var limit&&limit>=0?limit:settings.BytesPerSecond);
        HttpRequestMessage Request(string url)
        {
            var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.UserAgent.ParseAdd(settings.UserAgent);request.Headers.AcceptEncoding.ParseAdd("identity");
            if(session is not null){var a=new Uri(session.Url);var b=new Uri(url);if(a.Scheme==b.Scheme&&a.IdnHost==b.IdnHost&&a.Port==b.Port)(session with{Url=url,Method="GET",BodyBase64=""}).Apply(request);}
            return request;
        }
        async Task<HttpResponseMessage> Open(HttpRequestMessage request)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            try{var response=await ClientFor(job).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);if(!response.IsSuccessStatusCode&&response.StatusCode!=HttpStatusCode.NotModified){var code=((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);response.Dispose();throw new TransferException("errors.http",code);}return response;}
            catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){throw new TransferException("errors.connect_timeout");}
        }
        async Task Pump(Stream input,Stream output,long? expected,long maximum)
        {
            var buffer=new byte[65536];long copied=0;
            while(true){
                var charged=expected is null||copied<expected;var permitted=charged?await limiter.AcquireAsync(buffer.Length,cancellation):1;
                var lease=charged?await _quota.ReserveAsync(permitted,cancellation,()=>UpdateAsync(()=>{job.BytesPerSecond=0;job.PhaseKey="native.waiting_quota";},false)):new QuotaLease(1,0);
                if(charged&&lease.Granted<permitted)await limiter.RefundAsync(permitted-lease.Granted);permitted=lease.Granted;
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));int count;
                try{count=await input.ReadAsync(buffer.AsMemory(0,permitted),timeout.Token);}catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){throw new TransferException("errors.read_timeout");}catch(IOException){throw new TransferException("errors.network");}
                if(charged){_quota.Refund(lease,permitted-count);await limiter.RefundAsync(permitted-count);}if(count==0)break;
                copied+=count;if(copied>maximum||expected is {} exact&&copied>exact)throw new TransferException("errors.length_mismatch");
                await output.WriteAsync(buffer.AsMemory(0,count),cancellation);transferred+=count;received+=count;
                if(sample.ElapsedMilliseconds>=200){var speed=(transferred-sampleBytes)/sample.Elapsed.TotalSeconds;await UpdateAsync(()=>{job.ReceivedBytes=received;job.BytesPerSecond=speed;job.PhaseKey="native.receiving";},false);sample.Restart();sampleBytes=transferred;}
            }
            if(expected is {} length&&copied!=length)throw new TransferException("errors.length_mismatch");
        }
        async Task<MediaDocument> Manifest(Uri uri)
        {
            using var request=Request(uri.AbsoluteUri);using var response=await Open(request);if(response.Content.Headers.ContentLength>2*1024*1024)throw new TransferException("media.manifest_large");
            await using var input=await response.Content.ReadAsStreamAsync(cancellation);using var output=new MemoryStream();await Pump(input,output,response.Content.Headers.ContentLength,2*1024*1024);return new(Encoding.UTF8.GetString(output.ToArray()),response.RequestMessage?.RequestUri??uri);
        }
        try{
            if(session?.Method=="POST")throw new TransferException("media.post_manifest_not_supported");
            if(!Path.IsPathFullyQualified(settings.MediaMuxerPath)||!File.Exists(settings.MediaMuxerPath))throw new TransferException("media.tool_missing");
            var plan=await AdaptiveManifest.ReadAsync(new(job.Url),Manifest,cancellation);var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
            if(job.MediaPlanId!=fingerprint){ClearMediaCache(job);await UpdateAsync(()=>{job.MediaPlanId=fingerprint;job.MediaCompletedParts=0;},true);}
            var directory=MediaDirectory(job);Directory.CreateDirectory(directory);received=0;
            await UpdateAsync(()=>{job.TotalBytes=null;job.MediaTotalParts=plan.Parts.Count;job.MediaCompletedParts=0;job.ResumeSupported=true;job.ConnectionLimit=job.ActiveConnections=1;job.PhaseKey="native.receiving";},true);
            var files=new List<(MediaPart Part,string Path)>();
            for(var index=0;index<plan.Parts.Count;index++){
                cancellation.ThrowIfCancellationRequested();var part=plan.Parts[index];var path=Path.Combine(directory,"part-"+index.ToString("D5",CultureInfo.InvariantCulture)+".bin");var metadata=path+".json";var reusable=false;
                if(File.Exists(path)&&File.Exists(metadata)){
                    try{var cache=JsonSerializer.Deserialize<MediaCache>(File.ReadAllText(metadata));if(cache is not null&&new FileInfo(path).Length==cache.Size&&await HashFileAsync(path,cancellation)==cache.Hash&&(cache.Tag is not null||cache.Modified is not null)){
                        using var check=Request(part.Url);check.Headers.Range=new RangeHeaderValue(0,0);if(cache.Tag is {} tag)check.Headers.IfNoneMatch.ParseAdd(tag);else check.Headers.IfModifiedSince=cache.Modified;
                        using var response=await Open(check);reusable=response.StatusCode==HttpStatusCode.NotModified||response.StatusCode==HttpStatusCode.PartialContent&&(cache.Tag is {} old?response.Headers.ETag?.ToString()==old:response.Content.Headers.LastModified==cache.Modified)&&response.Content.Headers.ContentRange?.Length==cache.RemoteSize;
                    }}catch(Exception error)when(error is IOException or JsonException){reusable=false;}
                }
                if(reusable)received+=new FileInfo(path).Length;
                else{
                    using var request=Request(part.Url);if(part.Offset is {} offset)request.Headers.Range=new RangeHeaderValue(offset,checked(offset+part.Length!.Value-1));
                    using var response=await Open(request);var length=response.Content.Headers.ContentLength;
                    if(part.Offset is {} start&&(response.StatusCode!=HttpStatusCode.PartialContent||response.Content.Headers.ContentRange?.From!=start||response.Content.Headers.ContentRange?.To!=start+part.Length-1))throw new TransferException("errors.invalid_range");
                    if(response.Content.Headers.ContentEncoding.Any(e=>!e.Equals("identity",StringComparison.OrdinalIgnoreCase)))throw new TransferException("errors.unexpected_encoding");
                    var temporary=path+".tmp";try{await using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.Read,65536,FileOptions.Asynchronous)){await using var input=await response.Content.ReadAsStreamAsync(cancellation);await Pump(input,output,part.Length??length,long.MaxValue);await output.FlushAsync(cancellation);output.Flush(true);}File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
                    using var hashInput=File.OpenRead(path);var hash=Convert.ToHexString(await SHA256.HashDataAsync(hashInput,cancellation));var cache=new MediaCache(response.Headers.ETag is {IsWeak:false} etag?etag.ToString():null,response.Content.Headers.LastModified,response.Content.Headers.ContentRange?.Length??length,new FileInfo(path).Length,hash);File.WriteAllText(metadata,JsonSerializer.Serialize(cache));
                }
                if(part.KeyUrl is {} keyUrl&&!keys.ContainsKey(keyUrl)){using var request=Request(keyUrl);using var response=await Open(request);using var bytes=new MemoryStream();await using var input=await response.Content.ReadAsStreamAsync(cancellation);await Pump(input,bytes,response.Content.Headers.ContentLength,16);if(bytes.Length!=16)throw new TransferException("media.invalid_key");keys[keyUrl]=bytes.ToArray();}
                files.Add((part,path));var completed=index+1;await UpdateAsync(()=>{job.MediaCompletedParts=completed;job.ReceivedBytes=received;},true);
            }
            await UpdateAsync(()=>{job.PhaseKey="native.finalizing";job.BytesPerSecond=0;job.ActiveConnections=0;},true);
            var inputs=new List<string>();
            for(var stream=0;stream<plan.Streams;stream++){
                var joined=Path.Combine(directory,"stream-"+stream+".bin");inputs.Add(joined);await using var output=new FileStream(joined,FileMode.Create,FileAccess.Write,FileShare.Read,65536,FileOptions.Asynchronous);
                foreach(var part in files.Where(f=>f.Part.Stream==stream)){
                    await using var input=File.OpenRead(part.Path);
                    if(part.Part.KeyUrl is {} keyUrl){using var aes=Aes.Create();aes.Key=keys[keyUrl];aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;var iv=new byte[16];if(part.Part.Iv is {} hex)iv=Convert.FromHexString(hex[2..]);else System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8),part.Part.Sequence);aes.IV=iv;await using var decrypted=new CryptoStream(input,aes.CreateDecryptor(),CryptoStreamMode.Read,true);await decrypted.CopyToAsync(output,cancellation);}
                    else await input.CopyToAsync(output,cancellation);
                }await output.FlushAsync(cancellation);output.Flush(true);
            }
            foreach(var key in keys.Values)CryptographicOperations.ZeroMemory(key);
            var format=Path.GetExtension(job.FinalPath).ToLowerInvariant() switch{".mkv"=>"matroska",".webm"=>"webm",".m4a"=>"ipod",_=>"mp4"};
            var command=new ProcessStartInfo(settings.MediaMuxerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            foreach(var argument in new[]{"-hide_banner","-loglevel","error","-nostdin","-y"})command.ArgumentList.Add(argument);
            foreach(var input in inputs)foreach(var argument in new[]{"-protocol_whitelist","file,pipe","-fflags","+genpts","-i",input})command.ArgumentList.Add(argument);
            foreach(var argument in plan.Streams==1?new[]{"-map","0:v:0?","-map","0:a:0?"}:new[]{"-map","0:v:0?","-map","1:a:0?"})command.ArgumentList.Add(argument);
            foreach(var argument in new[]{"-c","copy","-f",format,job.PartialPath})command.ArgumentList.Add(argument);
            using(var process=Process.Start(command)??throw new IOException("Media muxer could not start")){
                using var registration=cancellation.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){}});var error=DrainMediaErrorAsync(process.StandardError,cancellation);
                await process.WaitForExitAsync(CancellationToken.None);await error;cancellation.ThrowIfCancellationRequested();if(process.ExitCode!=0||!File.Exists(job.PartialPath)||new FileInfo(job.PartialPath).Length==0)throw new TransferException("media.mux_failed",process.ExitCode.ToString(CultureInfo.InvariantCulture));
            }
            cancellation.ThrowIfCancellationRequested();File.Move(job.PartialPath,job.FinalPath,job.ReplaceExisting);var finalSize=new FileInfo(job.FinalPath).Length;
            await UpdateAsync(()=>{job.State=DownloadState.Completed;job.PhaseKey="ui.complete";job.ReceivedBytes=finalSize;job.TotalBytes=finalSize;job.ErrorKey=null;job.ErrorDetail=null;job.InQueue=false;job.ReplaceExisting=false;},true);try{ClearMediaCache(job);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){await SetStoppedAsync(job,DownloadState.Paused,null,null);}
        catch(TransferException error){await SetStoppedAsync(job,DownloadState.Failed,error.Key,error.Detail);}
        catch(InvalidDataException error){await SetStoppedAsync(job,DownloadState.Failed,error.Message.StartsWith("media.",StringComparison.Ordinal)?error.Message:"media.invalid_manifest",null);}
        catch(Exception error)when(error is FormatException or OverflowException or System.Xml.XmlException or CryptographicException or ArgumentException){await SetStoppedAsync(job,DownloadState.Failed,"media.invalid_manifest",null);}
        finally{foreach(var key in keys.Values)CryptographicOperations.ZeroMemory(key);if(job.State!=DownloadState.Completed){var directory=MediaDirectory(job);var durable=Directory.Exists(directory)?Directory.EnumerateFiles(directory,"part-*.bin").Where(path=>File.Exists(path+".json")).ToArray():[];await UpdateAsync(()=>{job.ReceivedBytes=durable.Sum(path=>new FileInfo(path).Length);job.MediaCompletedParts=durable.Length;},true);}await UpdateAsync(()=>{job.ActiveConnections=0;job.TransferredBytes=previousTransferred+transferred;job.ActiveSeconds=previousActive+clock.Elapsed.TotalSeconds;},true);}
    }
    private static async Task<string> HashFileAsync(string path,CancellationToken cancellation){await using var input=File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(input,cancellation));}
    private static async Task DrainMediaErrorAsync(StreamReader reader,CancellationToken cancellation){var buffer=new char[4096];while(await reader.ReadAsync(buffer,cancellation)>0){} }
}
