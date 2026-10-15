using System.Diagnostics;
using System.Net;

namespace DownloadManager.Core;

public sealed partial class DownloadEngine
{
    // The framework FTP implementation supplies active/passive data channels and
    // certificate-validated explicit TLS. Never install a certificate override.
#pragma warning disable SYSLIB0014
    private async Task TransferFtpAsync(Entry entry,TransferSettings settings,CancellationToken cancellation)
    {
        var job=entry.Job;var clock=Stopwatch.StartNew();var priorActive=job.ActiveSeconds;var priorTransferred=job.TransferredBytes;long transferred=0;
        FtpWebRequest Request(string method)
        {
            var uri=new Uri(job.Url);var address=new UriBuilder(uri){Scheme="ftp",Port=uri.Port<0?21:uri.Port}.Uri;
            var request=(FtpWebRequest)WebRequest.Create(address);request.Method=method;request.EnableSsl=uri.Scheme=="ftps";request.UsePassive=settings.FtpPassive;request.UseBinary=true;request.KeepAlive=false;request.Proxy=null;request.Timeout=settings.TimeoutSeconds*1000;request.ReadWriteTimeout=settings.TimeoutSeconds*1000;
            request.Credentials=new NetworkCredential(settings.FtpUser.Length==0?"anonymous":settings.FtpUser,settings.FtpUser.Length==0?"download-manager@":settings.FtpPassword);return request;
        }
        async Task<FtpWebResponse> Open(FtpWebRequest request)
        {
            using var registration=cancellation.Register(request.Abort);
            try{return (FtpWebResponse)await request.GetResponseAsync().WaitAsync(TimeSpan.FromSeconds(settings.TimeoutSeconds),cancellation).ConfigureAwait(false);}
            catch(TimeoutException){request.Abort();throw new TransferException("errors.connect_timeout");}
        }
        try
        {
            await UpdateAsync(()=>job.PhaseKey="native.connecting",false).ConfigureAwait(false);
            long? size=null;DateTimeOffset? modified=null;
            try{using var metadata=await Open(Request(WebRequestMethods.Ftp.GetFileSize));if(metadata.ContentLength>=0)size=metadata.ContentLength;}
            catch(WebException error)when(error.Response is FtpWebResponse response&&(int)response.StatusCode is 500 or 502 or 504){response.Dispose();}
            try{using var metadata=await Open(Request(WebRequestMethods.Ftp.GetDateTimestamp));if(metadata.LastModified.Year>1900)modified=new DateTimeOffset(metadata.LastModified.ToUniversalTime());}
            catch(WebException error)when(error.Response is FtpWebResponse response&&(int)response.StatusCode is 500 or 502 or 504 or 550){response.Dispose();}
            var offset=File.Exists(job.PartialPath)?new FileInfo(job.PartialPath).Length:0;
            if(modified is null||job.LastModified!=modified||size is null||job.TotalBytes!=size||offset>size)offset=0;
            var request=Request(WebRequestMethods.Ftp.DownloadFile);request.ContentOffset=offset;
            using var registration=cancellation.Register(request.Abort);
            FtpWebResponse download;
            try{download=await Open(request);}
            catch(WebException error)when(offset>0&&error.Response is FtpWebResponse rejected&&(int)rejected.StatusCode is 500 or 502 or 504){rejected.Dispose();offset=0;request=Request(WebRequestMethods.Ftp.DownloadFile);download=await Open(request);}
            using(download)
            {
                using var transferCancellation=cancellation.Register(request.Abort);
                await UpdateAsync(()=>{job.TotalBytes=size;job.LastModified=modified;job.EntityTag=null;job.ResumeSupported=size is not null&&modified is not null;job.ReceivedBytes=offset;job.ConnectionOffset=offset;job.ConnectionLimit=job.ActiveConnections=1;job.PhaseKey="native.receiving";},true).ConfigureAwait(false);
                await using var input=download.GetResponseStream();
                await using(var output=new FileStream(job.PartialPath,offset>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.Read,65536,FileOptions.Asynchronous))
                {
                    using var limiter=new TransferRateLimiter(()=>Volatile.Read(ref entry.SpeedLimit) is var rate&&rate>=0?rate:settings.BytesPerSecond);
                    var received=offset;var buffer=new byte[65536];var sample=Stopwatch.StartNew();var checkpoint=Stopwatch.StartNew();var sampleBytes=received;
                    try{
                        while(true){
                            var charged=size is null||received<size;var permitted=charged?await limiter.AcquireAsync(buffer.Length,cancellation):1;
                            var lease=charged?await _quota.ReserveAsync(permitted,cancellation,async()=>{await output.FlushAsync(cancellation);output.Flush(true);await UpdateAsync(()=>{job.ReceivedBytes=received;job.BytesPerSecond=0;job.PhaseKey="native.waiting_quota";},true);}):new QuotaLease(1,0);
                            if(charged&&lease.Granted<permitted)await limiter.RefundAsync(permitted-lease.Granted);permitted=lease.Granted;
                            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                            int count;try{count=await input.ReadAsync(buffer.AsMemory(0,permitted),timeout.Token);}catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){throw new TransferException("errors.read_timeout");}catch(IOException){throw new TransferException("errors.network");}
                            if(charged){_quota.Refund(lease,permitted-count);await limiter.RefundAsync(permitted-count);}if(count==0)break;
                            await output.WriteAsync(buffer.AsMemory(0,count),cancellation);received+=count;transferred+=count;if(size is {} maximum&&received>maximum)throw new TransferException("errors.length_mismatch");
                            if(sample.ElapsedMilliseconds>=200){var rate=(received-sampleBytes)/sample.Elapsed.TotalSeconds;await UpdateAsync(()=>{job.ReceivedBytes=received;job.BytesPerSecond=rate;job.PhaseKey="native.receiving";},false);sampleBytes=received;sample.Restart();}
                            if(checkpoint.ElapsedMilliseconds>=750){await output.FlushAsync(cancellation);output.Flush(true);await UpdateAsync(()=>job.ReceivedBytes=received,true);checkpoint.Restart();}
                        }
                        if(size is {} expected&&received!=expected)throw new TransferException("errors.length_mismatch");await output.FlushAsync(cancellation);output.Flush(true);await UpdateAsync(()=>job.ReceivedBytes=received,true);
                    }finally{output.Flush(true);}
                }
            }
            cancellation.ThrowIfCancellationRequested();File.Move(job.PartialPath,job.FinalPath,job.ReplaceExisting);
            await UpdateAsync(()=>{job.State=DownloadState.Completed;job.PhaseKey="ui.complete";job.ErrorKey=null;job.ErrorDetail=null;job.InQueue=false;job.ReplaceExisting=false;job.ActiveConnections=0;job.BytesPerSecond=0;job.TotalBytes=job.ReceivedBytes;},true);
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){await SetStoppedAsync(job,DownloadState.Paused,null,null);}
        catch(WebException error){if(cancellation.IsCancellationRequested)await SetStoppedAsync(job,DownloadState.Paused,null,null);else{var code=error.Response is FtpWebResponse response?((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture):error.Status.ToString();error.Response?.Dispose();await SetStoppedAsync(job,DownloadState.Failed,"errors.ftp",code);}}
        catch(TransferException error){await SetStoppedAsync(job,DownloadState.Failed,error.Key,error.Detail);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){await SetStoppedAsync(job,DownloadState.Failed,"errors.disk",error.GetType().Name);}
        finally{await UpdateAsync(()=>{job.TransferredBytes=priorTransferred+transferred;job.ActiveSeconds=priorActive+clock.Elapsed.TotalSeconds;},true);}
    }
#pragma warning restore SYSLIB0014
}
