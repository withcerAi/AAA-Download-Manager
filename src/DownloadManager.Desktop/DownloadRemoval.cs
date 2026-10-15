using System.IO;
using DownloadManager.Core;
namespace DownloadManager.Desktop;
public static class DownloadRemoval
{
    public static bool NeedsDiskChoice(IEnumerable<DownloadJob> jobs)=>jobs.Any(job=>job.State==DownloadState.Completed&&File.Exists(job.FinalPath));
    public static async Task ExecuteAsync(DownloadEngine engine,IEnumerable<DownloadJob> jobs,bool deleteCompletedFiles)
    {
        foreach(var job in jobs)
        {
            // Unfinished selections only discard their own temporary data. If completion
            // races with the click, retain the finished file rather than delete it unasked.
            if(job.State==DownloadState.Completed)await engine.RemoveAsync(job.Id,deleteCompletedFiles).ConfigureAwait(false);
            else await engine.RemoveAsync(job.Id).ConfigureAwait(false);
        }
    }
}
