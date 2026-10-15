namespace DownloadManager.Core;

public sealed record TransferSettings
{
    public string CategoryRoot {get;init;}="";
    public IReadOnlyList<CategoryFolderRule> CategoryFolders {get;init;}=[];
    public string TemporaryDirectory {get;init;}="";
    public string MediaMuxerPath {get;init;}="";
    public bool FtpPassive {get;init;}=true;
    public string FtpUser {get;init;}="";
    public string FtpPassword {get;init;}="";
    public NetworkPolicy Network { get; init; } = new();
    public QuotaPolicy Quota { get; init; } = new();
    public int MaxConnections { get; init; } = 1;
    public int TimeoutSeconds { get; init; } = 30;
    public int RetryCount { get; init; }
    public int RetryDelaySeconds { get; init; } = 10;
    public long BytesPerSecond { get; init; }
    public string UserAgent { get; init; } = "DownloadManager/0.1";
    public TransferSettings Validated()
    {
        if (MaxConnections is < 1 or > 32 || TimeoutSeconds is < 1 or > 3600 || RetryCount is < 0 or > 10 || RetryDelaySeconds is < 0 or > 3600 || BytesPerSecond < 0 || UserAgent.Length > 1024 || UserAgent.Any(char.IsControl)) throw new ArgumentException("errors.invalid_transfer_settings");
        if(FtpUser.Length>256||FtpPassword.Length>256||FtpUser.Any(char.IsControl)||FtpPassword.Any(char.IsControl))throw new ArgumentException("errors.invalid_transfer_settings");
        if(!string.IsNullOrEmpty(TemporaryDirectory)&&!Path.IsPathFullyQualified(TemporaryDirectory))throw new ArgumentException("errors.invalid_folder");
        foreach(var rule in CategoryFolders) _ = DownloadDestination.CategoryFolder(string.IsNullOrWhiteSpace(CategoryRoot)?Environment.CurrentDirectory:CategoryRoot,rule.Folder);
        Quota.Validate(); Network.Validate();
        using var request = new HttpRequestMessage();
        try { request.Headers.UserAgent.ParseAdd(UserAgent); } catch (FormatException) { throw new ArgumentException("errors.invalid_transfer_settings"); }
        return this;
    }
}
