using DownloadManager.Core;
namespace DownloadManager.Desktop;
public interface IDownloadTraySink
{
    bool Add(uint id,DownloadJob job);
    void Update(uint id,DownloadJob job);
    void Remove(uint id);
}
public sealed record DownloadTrayEntry(uint IconId,string JobId);
public sealed class DownloadTrayRegistry(IDownloadTraySink sink,Action<string> restore)
{
    private readonly Dictionary<uint,DownloadTrayEntry> _entries=[];
    private uint _next=2;
    public IReadOnlyList<DownloadTrayEntry> Entries=>_entries.Values.ToArray();
    public bool Add(DownloadJob job){if(_entries.Values.Any(e=>e.JobId==job.Id))return true;var id=_next++;if(!sink.Add(id,job))return false;_entries.Add(id,new(id,job.Id));return true;}
    public bool Restore(uint id){if(!_entries.TryGetValue(id,out var entry))return false;Remove(entry.JobId);restore(entry.JobId);return true;}
    public void Remove(string jobId){foreach(var entry in _entries.Values.Where(e=>e.JobId==jobId).ToArray()){sink.Remove(entry.IconId);_entries.Remove(entry.IconId);}}
    public void Update(IEnumerable<DownloadJob> jobs){var snapshot=jobs.ToDictionary(j=>j.Id);foreach(var entry in Entries){if(snapshot.TryGetValue(entry.JobId,out var job))sink.Update(entry.IconId,job);else Remove(entry.JobId);}}
    public void Clear(){foreach(var entry in Entries)Remove(entry.JobId);}
}
public static class DownloadTrayText
{
    private static string Trim(string text,int limit){if(text.Length<=limit)return text;var count=Math.Max(0,limit-1);if(count>0&&char.IsHighSurrogate(text[count-1]))count--;return text[..count]+"…";}
    public static string Format(DownloadJob job,LocaleCatalog locale){var row=new DownloadRow(job,locale);var suffix=locale.S("tray.download_tip",("name",""),("state",row.StateText),("progress",row.ProgressText),("speed",row.Speed));var name=Trim(job.FileName,Math.Max(1,127-suffix.Length));var text=locale.S("tray.download_tip",("name",name),("state",row.StateText),("progress",row.ProgressText),("speed",row.Speed));return Trim(text,127);}
}
