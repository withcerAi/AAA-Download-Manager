namespace DownloadManager.Core;

public sealed record DialPolicy(bool Enabled=false,string Name="",string User="",string Password="",int Attempts=3,int DelaySeconds=30)
{
    public void Validate(){if(Name.Length>256||Name.Any(char.IsControl)||User.Length>256||Password.Length>256||User.Any(char.IsControl)||Password.Any(char.IsControl)||Attempts is <0 or >100||DelaySeconds is <1 or >3600||Enabled&&string.IsNullOrWhiteSpace(Name))throw new ArgumentException("dial.invalid");}
}
public interface IDialTransport { Task ConnectAsync(DialPolicy policy,CancellationToken cancellation); }
public sealed class ConnectionPrerequisite(IDialTransport transport)
{
    private readonly SemaphoreSlim _gate=new(1,1);
    public async Task EnsureAsync(DialPolicy policy,CancellationToken cancellation)
    {
        policy.Validate();if(!policy.Enabled)return;await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try{for(var attempt=0;;attempt++){cancellation.ThrowIfCancellationRequested();try{await transport.ConnectAsync(policy,cancellation).ConfigureAwait(false);return;}catch(IOException)when(policy.Attempts==0||attempt+1<policy.Attempts){await Task.Delay(TimeSpan.FromSeconds(policy.DelaySeconds),cancellation).ConfigureAwait(false);}}}
        finally{_gate.Release();}
    }
}
