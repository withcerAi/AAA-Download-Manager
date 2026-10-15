namespace DownloadManager.Core;
public enum CompletionAction { None, ExitApplication, ShutDown, Sleep, Hibernate, Disconnect, OpenFile }
public sealed class CompletionOptions
{
    public CompletionAction Action { get; set; }
    public int DelaySeconds { get; set; } = 60;
    public string FilePath { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    public bool Force { get; set; }
    public void Validate(bool checkFile = true)
    {
        if (!Enum.IsDefined(Action) || DelaySeconds is < 60 or > 600 || FilePath.Length > 32760 || ConnectionName.Length > 256 || ConnectionName.Any(char.IsControl)) throw new ArgumentException("completion.invalid");
        if (Action == CompletionAction.OpenFile && (!Path.IsPathFullyQualified(FilePath) || (checkFile && !File.Exists(FilePath)))) throw new ArgumentException("completion.invalid");
        if (Action == CompletionAction.Disconnect && string.IsNullOrWhiteSpace(ConnectionName)) throw new ArgumentException("completion.invalid");
    }
    public CompletionOptions Copy() => (CompletionOptions)MemberwiseClone();
}
public sealed record CompletionTicket(string Id, string QueueId, CompletionOptions Options);
public interface ICompletionExecutor { Task ExecuteAsync(CompletionOptions options); }
// Pending destructive actions are intentionally never restored after application restart.
public sealed class CompletionCountdown
{
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly CompletionTicket _ticket;
    private bool _finished;
    public CompletionCountdown(CompletionTicket ticket, TimeProvider? clock = null) { ticket.Options.Validate(); _ticket = ticket; _clock = clock ?? TimeProvider.System; _started = _clock.GetTimestamp(); }
    public int Remaining => Math.Max(0, _ticket.Options.DelaySeconds - (int)_clock.GetElapsedTime(_started).TotalSeconds);
    public bool Finished => _finished;
    public void Cancel() => _finished = true;
    public async Task<bool> TickAsync(bool otherDownloadsRunning, ICompletionExecutor executor)
    {
        if (_finished) return false;
        if (otherDownloadsRunning) { Cancel(); return false; }
        if (Remaining > 0) return false;
        _finished = true; await executor.ExecuteAsync(_ticket.Options.Copy()); return true;
    }
}
