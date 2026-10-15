using DownloadManager.Core;
using System.ComponentModel;

namespace DownloadManager.Desktop;

public sealed class DownloadRow(DownloadJob job, LocaleCatalog locale) : INotifyPropertyChanged
{
    public DownloadJob Job { get; private set; } = job;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name => Job.FileName;
    public string Size => Job.TotalBytes is { } bytes ? FormatBytes(bytes) : "—";
    public string StateText => Job.State == DownloadState.Failed && Job.ErrorKey == "errors.http" ? locale.S("native.http_failed", ("detail", Job.ErrorDetail)) : locale.S(Job.State switch { DownloadState.Running => "ui.downloading", DownloadState.Paused => "ui.paused", DownloadState.Completed => "ui.complete", DownloadState.Failed => "ui.connection_error", _ => "ui.queued" });
    public string Speed => Job.BytesPerSecond > 0 ? FormatBytes((long)Job.BytesPerSecond) + "/s" : "—";
    public string TimeLeft => Job.SecondsRemaining is { } seconds ? seconds >= 3600 ? locale.S("native.remaining_hours", ("count", Math.Round(seconds/3600,1))) : seconds >= 60 ? locale.S("native.remaining_minutes", ("count", Math.Ceiling(seconds/60))) : locale.S("native.seconds", ("count", Math.Ceiling(seconds))) : "—";
    private string _queueName="";
    public void SetQueueName(string name) { if(name==_queueName)return;_queueName=name;PropertyChanged?.Invoke(this,new(nameof(Queue))); }
    public string Queue => Job.InQueue ? string.IsNullOrEmpty(_queueName) ? "✓" : _queueName : "";
    public double Progress => Job.Progress;
    public string ProgressText => Job.Progress.ToString("F1", locale.Culture) + "%";
    public string Added => Job.AddedAt.ToLocalTime().ToString("g", locale.Culture);
    public string Description => Job.ErrorKey is { } error ? locale.S(error == "errors.http" && Job.ErrorDetail == "403" ? "errors.http_forbidden" : error, ("detail", Job.ErrorDetail)) : Job.Description;
    public string Path => Job.FinalPath;
    public void Apply(DownloadJob next) { Job = next; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public static string FormatBytes(long value) => value >= 1L << 30 ? (value / (double)(1L << 30)).ToString("F2") + " GB" : value >= 1 << 20 ? (value / (double)(1 << 20)).ToString("F2") + " MB" : value >= 1024 ? (value / 1024d).ToString("F1") + " KB" : value + " B";
}
