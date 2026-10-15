namespace DownloadManager.Core;
public sealed class BrowserHandoffService(DownloadEngine engine, QueueScheduler scheduler, BrowserHandoffs store)
{
    private readonly SemaphoreSlim _gate=new(1,1);
    public async Task<BrowserReply?> ExistingAsync(BrowserRequest request)
    {
        request.Validated();await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing=store.Find(request.Id);if(existing is null)return request.Action is "commit" or "abort"?new(request.Id,false,"missing"):null;
            if(request.Action=="prepare")return new(request.Id,existing.Phase!="aborted",existing.Phase,existing.JobIds.Count);
            if(request.Action=="abort"&&existing.Phase=="prepared")
            {
                foreach(var job in (await engine.SnapshotAsync().ConfigureAwait(false)).Where(j=>existing.JobIds.Contains(j.Id)&&j.PendingBrowserCommit))await engine.RemoveAsync(job.Id).ConfigureAwait(false);
                existing.Phase="aborted";store.Save(existing);return new(request.Id,true,"aborted");
            }
            if(request.Action=="commit"&&existing.Phase=="prepared")
            {
                var jobs=(await engine.SnapshotAsync().ConfigureAwait(false)).Where(j=>existing.JobIds.Contains(j.Id)).ToArray();if(jobs.Length!=existing.JobIds.Count)return new(request.Id,false,"missing");
                foreach(var job in jobs)await engine.ReleaseBrowserHoldAsync(job.Id).ConfigureAwait(false);
                var queue=(await scheduler.SnapshotAsync().ConfigureAwait(false)).Any(q=>q.Id==existing.QueueId)?existing.QueueId:"main";
                await scheduler.AssignAsync(existing.JobIds,queue).ConfigureAwait(false);if(existing.Start)await scheduler.StartSelectedAsync(queue,existing.JobIds).ConfigureAwait(false);
                existing.Phase="committed";store.Save(existing);return new(request.Id,true,"committed",existing.JobIds.Count);
            }
            return new(request.Id,existing.Phase=="committed",existing.Phase,existing.JobIds.Count);
        }
        finally{_gate.Release();}
    }
    public async Task<int> PrepareAsync(BrowserRequest request,IEnumerable<string> urls,string folder,string queue,bool start,bool summary,bool close,CancellationToken token,string? filename=null,string description="")
    {
        request.Validated();await _gate.WaitAsync(token).ConfigureAwait(false);var added=new List<string>();
        try
        {
            if(store.Find(request.Id) is { } existing)return existing.JobIds.Count;
            foreach(var url in urls){token.ThrowIfCancellationRequested();var job=await engine.AddAsync(url,folder,filename,false,description,holdForBrowser:true,session:request.Context,adaptive:request.Media).ConfigureAwait(false);added.Add(job.Id);await engine.SetCompletionOptionsAsync(job.Id,summary,close).ConfigureAwait(false);}
            token.ThrowIfCancellationRequested();store.Save(new(){Id=request.Id,JobIds=added,QueueId=queue,Start=start});return added.Count;
        }
        catch{foreach(var id in added)await engine.RemoveAsync(id).ConfigureAwait(false);throw;}
        finally{_gate.Release();}
    }
}
