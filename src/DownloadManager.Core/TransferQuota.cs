using System.Text.Json;
namespace DownloadManager.Core;
public sealed record QuotaPolicy(bool Enabled = false, long MaximumBytes = 200L << 20, int Hours = 5)
{
    public void Validate() { if (MaximumBytes < 1024 || MaximumBytes > 1L << 50 || Hours is < 1 or > 168) throw new ArgumentException("errors.invalid_transfer_settings"); }
}
public readonly record struct QuotaLease(int Granted, long Window);
public sealed class TransferQuota : IDisposable
{
    private sealed class Ledger { public DateTimeOffset Start { get; set; } = DateTimeOffset.UtcNow; public long Reserved { get; set; } }
    private readonly object _gate = new(); private readonly string _path; private readonly TimeProvider _clock; private Ledger _ledger; private long _credit; private QuotaPolicy _policy = new();
    public TransferQuota(string directory, TimeProvider? clock = null) { _path = Path.Combine(directory,"quota.json"); _clock = clock ?? TimeProvider.System; _ledger = File.Exists(_path) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllText(_path)) ?? new() : new(){Start=_clock.GetUtcNow()}; }
    public void Configure(QuotaPolicy policy) { policy.Validate(); lock(_gate) _policy = policy; }
    public async Task<QuotaLease> ReserveAsync(int maximum, CancellationToken token, Func<Task>? onWait = null)
    {
        bool notified=false;
        while(true)
        {
            token.ThrowIfCancellationRequested();
            lock(_gate)
            {
                if(!_policy.Enabled) return new(maximum,0);
                if(_clock.GetUtcNow() >= _ledger.Start.AddHours(_policy.Hours)) { _ledger = new(){Start=_clock.GetUtcNow()}; _credit=0; Save(); }
                var available = Math.Max(0,_policy.MaximumBytes-_ledger.Reserved);
                if(_credit==0 && available>0) { var block=Math.Min(available,1L<<20); _ledger.Reserved+=block; _credit+=block; Save(); }
                // Lowering a limit takes effect immediately, including already reserved credit.
                var usable=Math.Min(_credit,Math.Max(0,_policy.MaximumBytes-(_ledger.Reserved-_credit)));
                if(usable>0) { var count=(int)Math.Min(maximum,usable);_credit-=count;return new(count,_ledger.Start.UtcTicks); }
            }
            if(!notified && onWait is not null){notified=true;await onWait();}
            await Task.Delay(100,token);
        }
    }
    public void Refund(QuotaLease lease,int unused) { if(lease.Window==0 || unused<=0)return;lock(_gate){if(_ledger.Start.UtcTicks==lease.Window)_credit+=Math.Min(unused,lease.Granted);} }
    public long Consumed { get { lock(_gate)return Math.Max(0,_ledger.Reserved-_credit); } }
    private void Save() { Directory.CreateDirectory(Path.GetDirectoryName(_path)!);using(var file=new FileStream(_path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,_ledger);file.Flush(true);}File.Move(_path+".tmp",_path,true); }
    public void Dispose() { lock(_gate){_ledger.Reserved=Math.Max(0,_ledger.Reserved-_credit);_credit=0;if(File.Exists(_path)||_policy.Enabled)Save();} }
}
