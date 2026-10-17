using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DownloadManager.Core;

public sealed partial class DownloadEngine : IAsyncDisposable
{
    private sealed class Entry(DownloadJob job)
    {
        public DownloadJob Job { get; } = job;
        public RemainingTimeEstimator Estimate { get; } = new();
        public CancellationTokenSource? Cancellation { get; set; }
        public Task? Worker { get; set; }
        public long SpeedLimit = -1;
        public bool Removing;
        public TransferSettings Policy = new();
    }
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,HttpClient> _jobClients = [];
    private HttpClient ClientFor(DownloadJob job)=>_jobClients.GetValueOrDefault(job.Id)??_client;
    private readonly TransferQuota _quota;
    private readonly string _storePath;
    private bool _closing;
    private TransferSettings _settings = new();
    public Func<CancellationToken,Task>? BeforeTransfer {get;set;}
    public TransferSettings CurrentSettings => Volatile.Read(ref _settings);
    public void Configure(TransferSettings settings) { var valid=settings.Validated(); _quota.Configure(valid.Quota); Volatile.Write(ref _settings,valid); }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DownloadEngine(string dataDirectory)
    {
        System.IO.Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "downloads.json"); _quota = new TransferQuota(dataDirectory);
        _client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseDrainSize = 0,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            UseProxy = true
        }) { Timeout = Timeout.InfiniteTimeSpan };
        if (File.Exists(_storePath))
        {
            using var stored=JsonDocument.Parse(File.ReadAllText(_storePath));
            var jobs = stored.RootElement.Deserialize<List<DownloadJob>>(JsonOptions) ?? [];
            // Older versions did not store pause intent. Preserve their paused entries on upgrade.
            var legacyPaused=stored.RootElement.EnumerateArray().Where(item=>!item.TryGetProperty(nameof(DownloadJob.ManuallyPaused),out _)&&item.TryGetProperty(nameof(DownloadJob.State),out var state)&&state.GetInt32()==(int)DownloadState.Paused).Select(item=>item.GetProperty(nameof(DownloadJob.Id)).GetString()).ToHashSet();
            foreach (var job in jobs)
            {
                if(legacyPaused.Contains(job.Id))job.ManuallyPaused=true;
                if (job.State == DownloadState.Running) job.State = DownloadState.Paused;
                job.BytesPerSecond = 0;
                job.ActiveConnections = 0;
                if (job.Segments.Count > 0)
                {
                    foreach (var segment in job.Segments) { segment.ReceivedBytes = segment.DurableBytes; segment.BytesPerSecond = 0; segment.PhaseKey = segment.ReceivedBytes == segment.Length ? "ui.complete" : "ui.paused"; }
                    job.ReceivedBytes = job.Segments.Sum(segment => segment.ReceivedBytes);
                }
                else if (job.MediaTotalParts==0 && job.State != DownloadState.Completed && File.Exists(job.PartialPath)) job.ReceivedBytes = new FileInfo(job.PartialPath).Length;
                if (!job.RememberSpeedLimit) job.SpeedLimitBytesPerSecond = null;
                MigrateLegacyTemporaryFiles(job);
                _entries.Add(job.Id, new Entry(job) { SpeedLimit = job.SpeedLimitBytesPerSecond ?? -1 });
            }
            SaveLocked();
        }
    }

    public async Task<DownloadJob> AddAsync(string url, string directory, string? filename = null, bool start = true, string description = "", bool holdForBrowser = false, SessionRequest? session = null, bool? adaptive = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("errors.invalid_url");
        if (url.Any(char.IsControl) || uri.Scheme is "ftp" or "ftps" && Uri.UnescapeDataString(uri.AbsolutePath).Any(char.IsControl)) throw new ArgumentException("errors.invalid_url");
        if(session is not null && session.Validated().Url!=uri.AbsoluteUri)throw new ArgumentException("browser.context_scope");
        var protectedSession=session?.Protect();
        var folder = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(folder);
        var isAdaptive=adaptive??IsAdaptiveUrl(uri.AbsoluteUri);
        var name = SafeFileName(filename ?? SuggestedName(uri.AbsoluteUri));
        if(isAdaptive&&Path.GetExtension(name).ToLowerInvariant() is not(".mp4" or ".mkv" or ".webm" or ".m4a"))name=Path.ChangeExtension(name,".mp4");
        folder=DownloadDestination.Resolve(folder,name,CurrentSettings);System.IO.Directory.CreateDirectory(folder);
        await _gate.WaitAsync().ConfigureAwait(false);
        DownloadJob job;
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var final = UniquePathLocked(folder, name);
            job = new DownloadJob
            {
                AutomaticFileName = filename is null || SafeFileName(filename).Equals(SafeFileName(SuggestedName(uri.AbsoluteUri)),StringComparison.OrdinalIgnoreCase), RequestedDirectory = Path.GetFullPath(directory),
                Url = uri.AbsoluteUri, ProtectedSession=protectedSession, Adaptive=isAdaptive,
                Directory = folder,
                FileName = Path.GetFileName(final),
                FinalPath = final,
                Description = description,
                InQueue = !start && !holdForBrowser, PendingBrowserCommit=holdForBrowser, PhaseKey=holdForBrowser?"browser.waiting_commit":"ui.queued"
            };
            var temporary=TemporaryFolder(CurrentSettings);
            System.IO.Directory.CreateDirectory(temporary);
            job.PartialPath = Path.Combine(temporary, ".dm-" + job.Id + ".part");
            _entries.Add(job.Id, new Entry(job));
            SaveLocked();
        }
        finally { _gate.Release(); }
        if (start) await StartAsync(job.Id).ConfigureAwait(false);
        return job.Copy();
    }

    private string TemporaryFolder(TransferSettings settings)=>string.IsNullOrWhiteSpace(settings.TemporaryDirectory)?Path.Combine(Path.GetDirectoryName(_storePath)!,"Temporary Downloads"):Path.GetFullPath(settings.TemporaryDirectory);
    private void MigrateLegacyTemporaryFiles(DownloadJob job)
    {
        if(job.State==DownloadState.Completed)return;
        var source=Path.GetDirectoryName(job.PartialPath)!;
        var legacy=source.Equals(job.Directory,StringComparison.OrdinalIgnoreCase)||source.Equals(Path.Combine(job.Directory,"Temporary Downloads"),StringComparison.OrdinalIgnoreCase);
        if(!legacy||!Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(TemporaryFolder(CurrentSettings)),StringComparison.OrdinalIgnoreCase))return;
        try{RelocateTemporaryFiles(job,TemporaryFolder(CurrentSettings));}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
    }
    private static void RelocateTemporaryFiles(DownloadJob job,string folder)
    {
        var sourceFolder=Path.GetDirectoryName(job.PartialPath)!;
        if(sourceFolder.Equals(folder,StringComparison.OrdinalIgnoreCase))return;
        if(Path.GetFileName(job.PartialPath)!=".dm-"+job.Id+".part")throw new IOException("Unexpected temporary file name");
        System.IO.Directory.CreateDirectory(folder);
        var target=Path.Combine(folder,Path.GetFileName(job.PartialPath));var oldMedia=Path.Combine(sourceFolder,".dm-"+job.Id+".media");var newMedia=Path.Combine(folder,Path.GetFileName(oldMedia));
        if(File.Exists(target)||System.IO.Directory.Exists(newMedia)){if(!File.Exists(job.PartialPath)&&!System.IO.Directory.Exists(oldMedia)){job.PartialPath=target;return;}throw new IOException("Temporary destination already exists");}
        var copies=new List<(string Source,string Target)>();if(File.Exists(job.PartialPath))copies.Add((job.PartialPath,target));
        if(System.IO.Directory.Exists(oldMedia)){
            if(System.IO.Directory.EnumerateDirectories(oldMedia).Any())throw new IOException("Unexpected cache directory");
            foreach(var source in System.IO.Directory.EnumerateFiles(oldMedia)){var name=Path.GetFileName(source);if(!name.StartsWith("part-",StringComparison.Ordinal)&&!name.StartsWith("stream-",StringComparison.Ordinal)&&!name.StartsWith("key-",StringComparison.Ordinal))throw new IOException("Unexpected cache file");copies.Add((source,Path.Combine(newMedia,name)));}
            System.IO.Directory.CreateDirectory(newMedia);
        }
        var copied=new List<string>();
        try{foreach(var copy in copies){using var source=File.OpenRead(copy.Source);using var destination=new FileStream(copy.Target,FileMode.CreateNew,FileAccess.Write,FileShare.None);copied.Add(copy.Target);source.CopyTo(destination);destination.Flush(true);}}
        catch{foreach(var path in copied)try{File.Delete(path);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}try{if(System.IO.Directory.Exists(newMedia)&&!System.IO.Directory.EnumerateFileSystemEntries(newMedia).Any())System.IO.Directory.Delete(newMedia);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}throw;}
        job.PartialPath=target;
        foreach(var copy in copies)try{File.Delete(copy.Source);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        try{if(System.IO.Directory.Exists(oldMedia)&&!System.IO.Directory.EnumerateFileSystemEntries(oldMedia).Any())System.IO.Directory.Delete(oldMedia);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
    }

    public async Task SetSpeedLimitAsync(string id, long? bytesPerSecond, bool remember)
    {
        if (bytesPerSecond is < 0 or > 1073741824 || bytesPerSecond is > 0 and < 1024) throw new ArgumentException("errors.invalid_speed_limit");
        await _gate.WaitAsync().ConfigureAwait(false);
        try { var entry = _entries[id]; entry.Job.SpeedLimitBytesPerSecond = bytesPerSecond; entry.Job.RememberSpeedLimit = remember; Volatile.Write(ref entry.SpeedLimit, bytesPerSecond ?? -1); SaveLocked(); }
        finally { _gate.Release(); }
    }
    public async Task SetCompletionOptionsAsync(string id, bool showSummary, bool closeWindow)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { var job = _entries[id].Job; job.ShowCompletionSummary = showSummary; job.CloseProgressOnCompletion = closeWindow; SaveLocked(); }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var entry = _entries[id];
            if(entry.Removing)return;
            if(entry.Job.PendingBrowserCommit)throw new InvalidOperationException("browser.waiting_commit");
            if (entry.Job.State == DownloadState.Completed || entry.Worker is { IsCompleted: false }) return;
            await Task.Run(()=>RelocateTemporaryFiles(entry.Job,TemporaryFolder(CurrentSettings))).ConfigureAwait(false);SaveLocked();
            entry.Policy=CurrentSettings with{MaxConnections=CurrentSettings.Network.ConnectionsFor(entry.Job.Url,CurrentSettings.MaxConnections)};
            if(_jobClients.TryRemove(id,out var previous))previous.Dispose();_jobClients[id]=DownloadHttpClient.Create(entry.Policy.Network,entry.Job.ProtectedSession is not null);
            entry.Cancellation?.Dispose();
            entry.Cancellation = new CancellationTokenSource();
            if (!entry.Job.RememberSpeedLimit) { entry.Job.SpeedLimitBytesPerSecond = null; Volatile.Write(ref entry.SpeedLimit, -1); }
            entry.Job.PhaseKey = "native.connecting"; entry.Job.RetryNumber = 0;
            entry.Estimate.Reset(); entry.Job.EstimatedSecondsRemaining=null;
            entry.Job.State = DownloadState.Running;
            entry.Job.ManuallyPaused = false;
            entry.Job.ErrorKey = null;
            entry.Job.ErrorDetail = null;
            SaveLocked();
            entry.Worker = Task.Run(() => TransferAsync(entry, entry.Cancellation.Token));
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync(string id, bool manual = false)
    {
        Task? worker;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = _entries[id];
            worker = entry.Worker;
            if(manual && entry.Job.State!=DownloadState.Completed){entry.Job.ManuallyPaused=true;SaveLocked();}
            entry.Cancellation?.Cancel();
            if (worker is null || worker.IsCompleted)
            {
                if (entry.Job.State != DownloadState.Completed) entry.Job.State = DownloadState.Paused;
                SaveLocked();
            }
        }
        finally { _gate.Release(); }
        if (worker is not null) await worker.ConfigureAwait(false);
    }

    // Existing internal callers discard partial data but preserve completed files.
    public Task RemoveAsync(string id) => RemoveCoreAsync(id, true, false);
    public Task RemoveAsync(string id, bool deleteFiles) => RemoveCoreAsync(id, deleteFiles, deleteFiles);
    private async Task RemoveCoreAsync(string id, bool deletePartial, bool deleteFinal)
    {
        Entry entry;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (!_entries.TryGetValue(id, out entry!)) return; entry.Removing = true; }
        finally { _gate.Release(); }
        try
        {
            await PauseAsync(id).ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Do file operations first: on access failure retain the row for retry.
                if (deleteFinal && File.Exists(entry.Job.FinalPath)) File.Delete(entry.Job.FinalPath);
                if (deletePartial) { if (File.Exists(entry.Job.PartialPath)) File.Delete(entry.Job.PartialPath); ClearMediaCache(entry.Job); }
                entry.Cancellation?.Dispose();
                if (_jobClients.TryRemove(id, out var client)) client.Dispose();
                _entries.Remove(id);
                SaveLocked();
            }
            finally { _gate.Release(); }
        }
        finally { entry.Removing = false; }
    }

    public async Task ReleaseBrowserHoldAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);try{var job=_entries[id].Job;job.PendingBrowserCommit=false;job.PhaseKey="ui.queued";SaveLocked();}finally{_gate.Release();}
    }
    public async Task SetQueueAsync(string id, bool queued)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _entries[id].Job.InQueue = queued; SaveLocked(); }
        finally { _gate.Release(); }
    }
    public async Task MarkQueuedAsync(string id)
    {
        await _gate.WaitAsync();try{var job=_entries[id].Job;if(job.State is not(DownloadState.Running or DownloadState.Completed)){job.ManuallyPaused=false;job.InQueue=true;job.State=DownloadState.Queued;job.PhaseKey="ui.queued";job.ErrorKey=null;job.ErrorDetail=null;SaveLocked();}}finally{_gate.Release();}
    }

    public async Task<IReadOnlyList<DownloadJob>> SnapshotAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { RefreshEstimatesLocked(); return _entries.Values.Select(entry => entry.Job.Copy()).OrderByDescending(job => job.AddedAt).ToArray(); }
        finally { _gate.Release(); }
    }

    private async Task TransferAsync(Entry entry, CancellationToken cancellation)
    {
        SessionRequest? session;try{session=SessionRequest.Restore(entry.Job.ProtectedSession);}catch(Exception error)when(error is System.ComponentModel.Win32Exception or FormatException or ArgumentException or JsonException){await SetStoppedAsync(entry.Job,DownloadState.Failed,"errors.session_unavailable",null);return;}var settings = session?.Method=="POST"?entry.Policy with {MaxConnections=1,RetryCount=0}:entry.Policy;
        for (var attempt = 0; ; attempt++)
        {
            await TransferAttemptAsync(entry, settings, cancellation).ConfigureAwait(false);
            var job = entry.Job;
            var retryable = job.ErrorKey is "errors.network" or "errors.read_timeout" or "errors.connect_timeout" or "errors.remote_changed" || job.ErrorKey == "errors.http" && int.TryParse(job.ErrorDetail, out var status) && (status is 408 or 429 || status >= 500);
            if (job.State != DownloadState.Failed || !retryable || attempt >= settings.RetryCount || cancellation.IsCancellationRequested) return;
            await UpdateAsync(() => { job.State = DownloadState.Running; job.PhaseKey = "native.waiting_retry"; job.RetryNumber = attempt + 1; job.ErrorKey = "errors.retry_pending"; job.ErrorDetail = (attempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture); }, true).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromSeconds(settings.RetryDelaySeconds), cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { await SetStoppedAsync(job, DownloadState.Paused, null, null).ConfigureAwait(false); return; }
        }
    }

    private async Task TransferSingleAttemptAsync(Entry entry, TransferSettings settings, CancellationToken cancellation)
    {
        var job = entry.Job; var attemptClock = Stopwatch.StartNew(); var priorActive = job.ActiveSeconds; var priorTransferred = job.TransferredBytes; long transferred = 0;
        try
        {
            await UpdateAsync(() => job.PhaseKey = "native.connecting", false).ConfigureAwait(false);
            var offset = File.Exists(job.PartialPath) ? new FileInfo(job.PartialPath).Length : 0;
            if (SessionRequest.Restore(job.ProtectedSession)?.Method=="POST" || !HasValidator(job) || !job.ResumeSupported || job.TotalBytes is { } knownSize && offset > knownSize) offset = 0;
            using var response = await OpenResponseAsync(job, offset, settings, cancellation).ConfigureAwait(false);
            var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) offset = 0;
            var total = response.StatusCode == HttpStatusCode.PartialContent ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.Unit != "bytes" || range.From != offset || range.To is null || range.To < offset)
                    throw new TransferException("errors.invalid_range");
                if (response.Content.Headers.ContentLength is { } length && range.To - range.From + 1 != length)
                    throw new TransferException("errors.invalid_range");
            }
            if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new TransferException("errors.unexpected_encoding");
            await UpdateAsync(() =>
            {
                job.PhaseKey = "native.receiving"; job.ConnectionOffset = offset; job.ActiveConnections = 1; job.ConnectionLimit = 1;
                job.EffectiveUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? job.Url; job.ContentType = response.Content.Headers.ContentType?.MediaType; job.HttpStatus = (int)response.StatusCode;
                job.ReceivedBytes = offset;
                job.TotalBytes = total;
                job.EntityTag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                job.LastModified = response.Content.Headers.LastModified;
                job.ResumeSupported = SessionRequest.Restore(job.ProtectedSession)?.Method!="POST" && (response.StatusCode == HttpStatusCode.PartialContent || response.Headers.AcceptRanges.Contains("bytes")) && HasValidator(job);
            }, true).ConfigureAwait(false);
            await ApplyResponseNameAsync(job,response,settings).ConfigureAwait(false);
            await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using (var output = new FileStream(job.PartialPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                var received = offset;
                var checkpoint = Stopwatch.StartNew();
                var sampledAt = Stopwatch.StartNew();
                var sampleBytes = received;
                using var limiter = new TransferRateLimiter(() => Volatile.Read(ref entry.SpeedLimit) is var rate && rate >= 0 ? rate : settings.BytesPerSecond);
                try
                {
                    while (true)
                    {
                        var charged = !(total is { } full && received >= full); var permitted = charged ? await limiter.AcquireAsync(buffer.Length, cancellation).ConfigureAwait(false) : 1;
                        var lease = charged ? await _quota.ReserveAsync(permitted, cancellation, async () => { await output.FlushAsync(cancellation).ConfigureAwait(false); output.Flush(true); await UpdateAsync(() => { job.ReceivedBytes=received; job.BytesPerSecond=0; job.PhaseKey="native.waiting_quota"; },true).ConfigureAwait(false); }).ConfigureAwait(false) : new QuotaLease(permitted,0);
                        if (charged && lease.Granted < permitted) await limiter.RefundAsync(permitted - lease.Granted).ConfigureAwait(false); permitted = lease.Granted;
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        readTimeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                        int read;
                        try { read = await input.ReadAsync(buffer.AsMemory(0, permitted), readTimeout.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TransferException("errors.read_timeout"); }
                        catch (IOException) { throw new TransferException("errors.network"); }
                        if (charged) { await limiter.RefundAsync(permitted - read).ConfigureAwait(false); _quota.Refund(lease,permitted-read); }
                        if (read == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                        received += read;
                        transferred += read;
                        if (total is { } maximum && received > maximum) throw new TransferException("errors.length_mismatch");
                        if (sampledAt.ElapsedMilliseconds >= 200)
                        {
                            var rate = (received - sampleBytes) / sampledAt.Elapsed.TotalSeconds;
                            await UpdateAsync(() => { job.PhaseKey = "native.receiving"; job.ReceivedBytes = received; job.BytesPerSecond = rate; job.TransferredBytes = priorTransferred + transferred; job.ActiveSeconds = priorActive + attemptClock.Elapsed.TotalSeconds; }, false).ConfigureAwait(false);
                            sampleBytes = received;
                            sampledAt.Restart();
                        }
                        if (checkpoint.ElapsedMilliseconds >= 750)
                        {
                            await output.FlushAsync(cancellation).ConfigureAwait(false);
                            output.Flush(true);
                            await UpdateAsync(() => job.ReceivedBytes = received, true).ConfigureAwait(false);
                            checkpoint.Restart();
                        }
                    }
                    if (total is { } expected && received != expected) throw new TransferException("errors.length_mismatch");
                    await output.FlushAsync(cancellation).ConfigureAwait(false);
                    output.Flush(true);
                    await UpdateAsync(() => job.ReceivedBytes = received, true).ConfigureAwait(false);
                }
                finally { output.Flush(true); }
            }
            cancellation.ThrowIfCancellationRequested();
            await UpdateAsync(() => job.PhaseKey = "native.finalizing", false).ConfigureAwait(false);
            File.Move(job.PartialPath, job.FinalPath, job.ReplaceExisting);
            await UpdateAsync(() =>
            {
                job.ErrorKey = null; job.ErrorDetail = null;
                job.ActiveConnections = 0; job.PhaseKey = "ui.complete"; job.ReplaceExisting = false; job.State = DownloadState.Completed;
                job.TotalBytes = job.ReceivedBytes;
                job.BytesPerSecond = 0;
                job.InQueue = false;
            }, true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await SetStoppedAsync(job, DownloadState.Paused, null, null).ConfigureAwait(false);
        }
        catch (TransferException exception)
        {
            await SetStoppedAsync(job, DownloadState.Failed, exception.Key, exception.Detail).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            await SetStoppedAsync(job, DownloadState.Failed, "errors.network", exception.StatusCode?.ToString()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await SetStoppedAsync(job, DownloadState.Failed, "errors.disk", exception.GetType().Name).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SetStoppedAsync(job, DownloadState.Failed, "errors.transfer", exception.GetType().Name).ConfigureAwait(false);
        }
        finally { await UpdateAsync(() => { job.TransferredBytes = priorTransferred + transferred; job.ActiveSeconds = priorActive + attemptClock.Elapsed.TotalSeconds; }, true).ConfigureAwait(false); }
    }

    private async Task<HttpResponseMessage> OpenResponseAsync(DownloadJob job, long offset, TransferSettings settings, CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
            request.Headers.UserAgent.ParseAdd(settings.UserAgent);
            request.Headers.AcceptEncoding.ParseAdd("identity"); SessionRequest.Restore(job.ProtectedSession)?.Apply(request);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = job.EntityTag is { } tag ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(tag)) : new RangeConditionHeaderValue(job.LastModified!.Value);
            }
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            HttpResponseMessage response;
            try { response = await ClientFor(job).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TransferException("errors.connect_timeout"); }
            var changed = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent &&
                (job.TotalBytes is { } oldSize && response.Content.Headers.ContentRange?.Length != oldSize ||
                 job.EntityTag is { } oldTag && response.Headers.ETag is { } newTag && oldTag != newTag.ToString());
            if (offset > 0 && (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable || changed))
            {
                response.Dispose();
                offset = 0;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
                response.Dispose();
                throw new TransferException("errors.http", status);
            }
            return response;
        }
        throw new TransferException("errors.invalid_range");
    }

    private async Task SetStoppedAsync(DownloadJob job, DownloadState state, string? error, string? detail)
    {
        await UpdateAsync(() =>
        {
            job.ActiveConnections = 0; foreach (var segment in job.Segments) segment.BytesPerSecond = 0;
            job.PhaseKey = state == DownloadState.Paused ? "ui.paused" : "ui.connection_error";
            job.State = state;
            job.BytesPerSecond = 0;
            job.ErrorKey = error;
            job.ErrorDetail = detail; if(error == "errors.http" && int.TryParse(detail, out var status)) job.HttpStatus = status;
            if (job.Segments.Count > 0) job.ReceivedBytes = job.Segments.Sum(segment => segment.ReceivedBytes);
            else if (job.MediaTotalParts==0 && File.Exists(job.PartialPath)) job.ReceivedBytes = new FileInfo(job.PartialPath).Length;
        }, true).ConfigureAwait(false);
    }

    private async Task UpdateAsync(Action update, bool save)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { update(); RefreshEstimatesLocked(); if (save) SaveLocked(); }
        finally { _gate.Release(); }
    }

    private void RefreshEstimatesLocked()
    {
        var now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        foreach(var entry in _entries.Values){var job=entry.Job;
            if(job.State!=DownloadState.Running||job.PhaseKey!="native.receiving"||job.Adaptive){entry.Estimate.Reset();job.EstimatedSecondsRemaining=null;}
            else job.EstimatedSecondsRemaining=entry.Estimate.Sample(now,job.ReceivedBytes,job.TotalBytes);
        }
    }

    private void SaveLocked()
    {
        var staging = _storePath + ".tmp";
        using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, _entries.Values.Select(entry =>
            {
                var job = entry.Job.Copy();
                if (job.Segments.Count > 0) { foreach (var segment in job.Segments) segment.ReceivedBytes = segment.DurableBytes; job.ReceivedBytes = job.Segments.Sum(segment => segment.DurableBytes); }
                return job;
            }).ToArray(), JsonOptions);
            output.Flush(true);
        }
        File.Move(staging, _storePath, true);
    }

    private string UniquePathLocked(string directory, string filename, string? excludeId = null)
    {
        var candidate = Path.Combine(directory, filename);
        var stem = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        for (var number = 1; File.Exists(candidate) || _entries.Values.Any(entry => entry.Job.Id != excludeId && entry.Job.FinalPath.Equals(candidate, StringComparison.OrdinalIgnoreCase)); number++)
            candidate = Path.Combine(directory, stem + " (" + number + ")" + extension);
        return candidate;
    }

    private static bool HasValidator(DownloadJob job) => job.EntityTag is not null || job.LastModified is not null;

    public static string SafeFileName(string input)
    {
        var last = input.Split(['/', '\\']).Last().Trim();
        var chars = last.Select(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray();
        var value = new string(chars).Trim(' ', '.');
        if (value.Length == 0) value = "download.bin";
        var device = value.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(device)) value = "_" + value;
        if (value.Length > 180) value = value[..150] + Path.GetExtension(value)[..Math.Min(20, Path.GetExtension(value).Length)];
        return value;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] workers;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _closing = true;
            foreach (var entry in _entries.Values) entry.Cancellation?.Cancel();
            workers = _entries.Values.Where(entry => entry.Worker is not null).Select(entry => entry.Worker!).ToArray();
        }
        finally { _gate.Release(); }
        await Task.WhenAll(workers).ConfigureAwait(false);
        foreach (var entry in _entries.Values) entry.Cancellation?.Dispose();
        foreach(var client in _jobClients.Values)client.Dispose(); _client.Dispose(); _quota.Dispose();
        _gate.Dispose();
    }

    private sealed class TransferException(string key, string? detail = null) : Exception(key)
    {
        public string Key { get; } = key;
        public string? Detail { get; } = detail;
    }
}
