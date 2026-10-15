namespace DownloadManager.Core;

public sealed partial class DownloadEngine
{
    private async Task ApplyResponseNameAsync(DownloadJob job, HttpResponseMessage response, TransferSettings settings)
    {
        if (job.Adaptive || job.AutomaticFileName == false || job.ReplaceExisting) return;
        if (job.AutomaticFileName is null && job.FileName != SafeFileName(SuggestedName(job.Url))) return;
        var name = LinkInspector.ResponseName(job.Url, response);
        var directory = DownloadDestination.Resolve(job.RequestedDirectory ?? job.Directory, name, settings);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (job.FileName.Equals(name, StringComparison.OrdinalIgnoreCase) && job.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)) return;
            System.IO.Directory.CreateDirectory(directory);
            var final = UniquePathLocked(directory, name, job.Id);
            job.FileName = Path.GetFileName(final); job.Directory = directory; job.FinalPath = final;
            job.AutomaticFileName = true;
            SaveLocked();
        }
        finally { _gate.Release(); }
    }
}
