using System.Net;
using System.Net.Http.Headers;
namespace DownloadManager.Core;
public sealed partial class DownloadEngine
{
    public async Task<bool> SynchronizeAsync(string id,CancellationToken cancellation=default)
    {
        var snapshot=(await SnapshotAsync()).Single(job=>job.Id==id);
        if(snapshot.State!=DownloadState.Completed){await StartAsync(id);return true;}
        var session=SessionRequest.Restore(snapshot.ProtectedSession);if(session?.Method=="POST")throw new InvalidOperationException("POST downloads cannot be polled automatically");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(CurrentSettings.TimeoutSeconds));
        using var request=new HttpRequestMessage(HttpMethod.Get,snapshot.Url);request.Headers.Range=new RangeHeaderValue(0,0);request.Headers.AcceptEncoding.ParseAdd("identity");request.Headers.UserAgent.ParseAdd(CurrentSettings.UserAgent);
        if(snapshot.EntityTag is { } tag)request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(tag));else if(snapshot.LastModified is { } modified)request.Headers.IfModifiedSince=modified;
        session?.Apply(request);
        using var syncClient=DownloadHttpClient.Create(CurrentSettings.Network,session is not null);
        using var response=await syncClient.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
        if(response.StatusCode==HttpStatusCode.NotModified && File.Exists(snapshot.FinalPath))return false;
        response.EnsureSuccessStatusCode();
        if(response.StatusCode==HttpStatusCode.PartialContent && (response.Content.Headers.ContentRange?.From!=0 || response.Content.Headers.ContentRange?.To!=0 || response.Content.Headers.ContentLength!=1))throw new InvalidDataException("errors.invalid_range");
        var same=snapshot.EntityTag is { } oldTag && response.Headers.ETag is { IsWeak:false } newTag && newTag.ToString()==oldTag || snapshot.EntityTag is null && snapshot.LastModified is { } oldModified && response.Content.Headers.LastModified==oldModified;
        var size=response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        if(same && size==snapshot.TotalBytes && File.Exists(snapshot.FinalPath) && new FileInfo(snapshot.FinalPath).Length==snapshot.TotalBytes)return false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry=_entries[id];if(entry.Job.State!=DownloadState.Completed)throw new InvalidOperationException("scheduler.sync_conflict");
            var job=entry.Job;if(File.Exists(job.PartialPath))File.Delete(job.PartialPath);
            job.ReplaceExisting=true;job.State=DownloadState.Queued;job.InQueue=true;job.ReceivedBytes=0;job.TotalBytes=null;job.EntityTag=null;job.LastModified=null;job.Segments.Clear();job.ErrorKey=null;SaveLocked();
        }
        finally{_gate.Release();}
        await StartAsync(id).ConfigureAwait(false);return true;
    }
}
