namespace DownloadManager.Core;

public enum DownloadState { Queued, Running, Paused, Completed, Failed }

public sealed class DownloadSegment
{
    public int Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long ReceivedBytes { get; set; }
    public long DurableBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public string PhaseKey { get; set; } = "ui.queued";
    public long Length => End - Start + 1;
    public DownloadSegment Copy() => (DownloadSegment)MemberwiseClone();
}

public sealed class DownloadJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Adaptive {get;set;}
    public string? MediaPlanId {get;set;}
    public int MediaCompletedParts {get;set;}
    public int MediaTotalParts {get;set;}
    public string? ProtectedSession { get; set; }
    public string Url { get; set; } = "";
    public bool? AutomaticFileName { get; set; }
    public string? RequestedDirectory { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public double? EstimatedSecondsRemaining { get; set; }
    public string FileName { get; set; } = "";
    public string Directory { get; set; } = "";
    public string FinalPath { get; set; } = "";
    public string PartialPath { get; set; } = "";
    public DownloadState State { get; set; } = DownloadState.Queued;
    public long ReceivedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public string? EntityTag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public bool ResumeSupported { get; set; }
    public double BytesPerSecond { get; set; }
    public string? ErrorKey { get; set; }
    public string? ErrorDetail { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Description { get; set; } = "";
    public bool PendingBrowserCommit { get; set; }
    public bool ReplaceExisting { get; set; }
    public bool InQueue { get; set; }
    public bool ManuallyPaused { get; set; }
    public string PhaseKey { get; set; } = "ui.queued";
    public long TransferredBytes { get; set; }
    public double ActiveSeconds { get; set; }
    public long ConnectionOffset { get; set; }
    public int ActiveConnections { get; set; }
    public int ConnectionLimit { get; set; } = 1;
    public List<DownloadSegment> Segments { get; set; } = [];
    public string? EffectiveUrl { get; set; }
    public string? ContentType { get; set; }
    public int? HttpStatus { get; set; }
    public int RetryNumber { get; set; }
    public long? SpeedLimitBytesPerSecond { get; set; }
    public bool RememberSpeedLimit { get; set; }
    public bool ShowCompletionSummary { get; set; } = true;
    public bool CloseProgressOnCompletion { get; set; }
    public double Progress => State==DownloadState.Completed?100:MediaTotalParts>0?Math.Clamp(MediaCompletedParts*98d/MediaTotalParts,0,98): TotalBytes is > 0 ? Math.Clamp(ReceivedBytes * 100d / TotalBytes.Value, 0, 100) : State == DownloadState.Completed ? 100 : 0;
    public double? SecondsRemaining => State == DownloadState.Running && PhaseKey == "native.receiving" ? EstimatedSecondsRemaining : null;
    public DownloadJob Copy() { var copy = (DownloadJob)MemberwiseClone(); copy.Segments = Segments.Select(segment => segment.Copy()).ToList(); return copy; }
}
