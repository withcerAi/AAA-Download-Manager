using System.Text.Json;
namespace DownloadManager.Core;
public sealed class BrowserHandoff
{
    public string Id { get; set; }="";
    public string QueueId { get; set; }="main";
    public List<string> JobIds { get; set; }=[];
    public bool Start { get; set; }
    public string Phase { get; set; }="prepared";
    public DateTimeOffset Created { get; set; }=DateTimeOffset.UtcNow;
}
public sealed class BrowserHandoffs
{
    private readonly string _path;private readonly Dictionary<string,BrowserHandoff> _entries;
    public BrowserHandoffs(string directory){_path=Path.Combine(directory,"browser-handoffs.json");_entries=File.Exists(_path)?JsonSerializer.Deserialize<Dictionary<string,BrowserHandoff>>(File.ReadAllText(_path))??[]:[];}
    public BrowserHandoff? Find(string id)=>_entries.GetValueOrDefault(id);
    public void Save(BrowserHandoff entry)
    {
        if(_entries.Count>=200&&!_entries.ContainsKey(entry.Id)){var oldest=_entries.Values.Where(e=>e.Phase!="prepared").OrderBy(e=>e.Created).FirstOrDefault();if(oldest is null)throw new InvalidOperationException("browser.too_many_pending");_entries.Remove(oldest.Id);}
        _entries[entry.Id]=entry;
        using(var file=new FileStream(_path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,_entries);file.Flush(true);}File.Move(_path+".tmp",_path,true);
    }
}
