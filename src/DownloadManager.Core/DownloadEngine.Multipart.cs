using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Core;

public sealed partial class DownloadEngine
{
    private sealed class SegmentUnsupportedException : Exception;

    private async Task TransferAttemptAsync(Entry entry, TransferSettings settings, CancellationToken cancellation)
    {
        try
        {
            if(BeforeTransfer is {} prerequisite){try{await prerequisite(cancellation).ConfigureAwait(false);}catch(IOException error){throw new TransferException("errors.dial",error.Message);}}
            if(entry.Job.Adaptive){await TransferAdaptiveAsync(entry,settings,cancellation);return;}
            if(new Uri(entry.Job.Url).Scheme is "ftp" or "ftps"){await TransferFtpAsync(entry,settings,cancellation);return;}
            try
            {
                if ((settings.MaxConnections > 1 || entry.Job.Segments.Count > 0) && await TryTransferSegmentsAsync(entry, settings, cancellation).ConfigureAwait(false)) return;
            }
            catch (SegmentUnsupportedException)
            {
                await ResetSegmentFileAsync(entry.Job).ConfigureAwait(false);
            }
            await TransferSingleAttemptAsync(entry, settings, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { await SetStoppedAsync(entry.Job, DownloadState.Paused, null, null).ConfigureAwait(false); }
        catch (TransferException error) { await SetStoppedAsync(entry.Job, DownloadState.Failed, error.Key, error.Detail).ConfigureAwait(false); }
        catch (HttpRequestException error) { await SetStoppedAsync(entry.Job, DownloadState.Failed, "errors.network", error.StatusCode?.ToString()).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await SetStoppedAsync(entry.Job, DownloadState.Failed, "errors.disk", error.GetType().Name).ConfigureAwait(false); }
        catch (Exception error) { await SetStoppedAsync(entry.Job, DownloadState.Failed, "errors.transfer", error.GetType().Name).ConfigureAwait(false); }
    }

    private async Task ResetSegmentFileAsync(DownloadJob job)
    {
        // Called only after every segment worker has stopped and released its file handle.
        if (File.Exists(job.PartialPath)) { using var file = new FileStream(job.PartialPath, FileMode.Truncate, FileAccess.Write, FileShare.Read); file.Flush(true); }
        await UpdateAsync(() => { job.Segments.Clear(); job.ReceivedBytes = 0; job.ResumeSupported = false; job.EntityTag = null; job.LastModified = null; job.ActiveConnections = 0; job.ConnectionLimit = 1; }, true).ConfigureAwait(false);
    }

    private static bool ValidSegmentMap(IReadOnlyList<DownloadSegment> segments, long size)
    {
        if (segments.Count is < 1 or > 32) return false;
        long next = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment.Index != index || segment.Start != next || segment.End < segment.Start || segment.End >= size || segment.DurableBytes < 0 || segment.DurableBytes > segment.Length) return false;
            next = segment.End + 1;
        }
        return next == size;
    }

    private async Task<bool> TryTransferSegmentsAsync(Entry entry, TransferSettings settings, CancellationToken cancellation)
    {
        var job = entry.Job;
        if(SessionRequest.Restore(job.ProtectedSession)?.Method=="POST")return false;
        var hadSegments = job.Segments.Count > 0;
        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, job.Url);
        probeRequest.Headers.UserAgent.ParseAdd(settings.UserAgent); probeRequest.Headers.AcceptEncoding.ParseAdd("identity"); probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
        SessionRequest.Restore(job.ProtectedSession)?.Apply(probeRequest);
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); probeTimeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        HttpResponseMessage probe;
        try { probe = await ClientFor(job).SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, probeTimeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TransferException("errors.connect_timeout"); }
        long size; string? tag; DateTimeOffset? modified; string effective; string? contentType;
        using (probe)
        {
            if (!probe.IsSuccessStatusCode && probe.StatusCode is not (HttpStatusCode.RequestedRangeNotSatisfiable or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)) throw new TransferException("errors.http", ((int)probe.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if(probe.StatusCode==HttpStatusCode.Forbidden)return false;
            var range = probe.Content.Headers.ContentRange;
            tag = probe.Headers.ETag is { IsWeak: false } strong ? strong.ToString() : null; modified = probe.Content.Headers.LastModified;
            var usable = probe.StatusCode == HttpStatusCode.PartialContent && range is { Unit: "bytes", From: 0, To: 0, Length: >= 1048576 } && (probe.Content.Headers.ContentLength is null or 1) && (tag is not null || modified is not null) && !probe.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase));
            if (!usable)
            {
                if (hadSegments) await ResetSegmentFileAsync(job).ConfigureAwait(false);
                return false;
            }
            await ApplyResponseNameAsync(job,probe,settings).ConfigureAwait(false);
            size = range!.Length!.Value; effective = probe.RequestMessage?.RequestUri?.AbsoluteUri ?? job.Url; contentType = probe.Content.Headers.ContentType?.MediaType;
        }
        var identityMatches = job.TotalBytes == size && (job.EntityTag is { } oldTag ? oldTag == tag : job.LastModified is { } oldDate && oldDate == modified);
        var length = File.Exists(job.PartialPath) ? new FileInfo(job.PartialPath).Length : 0;
        var reuse = hadSegments && identityMatches && length == size && ValidSegmentMap(job.Segments, size);
        var prefix = !hadSegments && identityMatches && job.ResumeSupported && length <= size ? length : 0;
        var segments = reuse ? job.Segments.Select(segment => { var copy = segment.Copy(); copy.ReceivedBytes = copy.DurableBytes; copy.BytesPerSecond = 0; return copy; }).ToList() : new List<DownloadSegment>();
        if (!reuse)
        {
            var count = (int)Math.Min(settings.MaxConnections, Math.Max(1, size / 262144));
            for (var index = 0; index < count; index++)
            {
                var from = size / count * index; var to = index == count - 1 ? size - 1 : size / count * (index + 1) - 1;
                var received = Math.Clamp(prefix - from, 0, to - from + 1); segments.Add(new DownloadSegment { Index = index, Start = from, End = to, ReceivedBytes = received, DurableBytes = received });
            }
        }
        using var file = File.OpenHandle(job.PartialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (!reuse && prefix == 0) RandomAccess.SetLength(file, 0);
        RandomAccess.SetLength(file, size); RandomAccess.FlushToDisk(file);
        await UpdateAsync(() =>
        {
            job.Segments = segments; job.TotalBytes = size; job.EntityTag = tag; job.LastModified = modified; job.ResumeSupported = true; job.EffectiveUrl = effective; job.ContentType = contentType; job.HttpStatus = 206; job.ConnectionLimit = Math.Min(settings.MaxConnections, segments.Count); job.ActiveConnections = 0; job.PhaseKey = "native.receiving";
            job.ReceivedBytes = segments.Sum(segment => segment.ReceivedBytes); foreach (var segment in segments) segment.PhaseKey = segment.ReceivedBytes == segment.Length ? "ui.complete" : "ui.queued";
        }, true).ConfigureAwait(false);
        var initialBytes = job.ReceivedBytes; var priorTransferred = job.TransferredBytes; var priorActive = job.ActiveSeconds; var clock = Stopwatch.StartNew(); var sampleClock = Stopwatch.StartNew(); var sampledBytes = initialBytes;
        var lastCheckpoint = Stopwatch.GetTimestamp(); var checkpointRunning = 0;
        using var limiter = new TransferRateLimiter(() => Volatile.Read(ref entry.SpeedLimit) is var rate && rate >= 0 ? rate : settings.BytesPerSecond);
        using var workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation); using var slots = new SemaphoreSlim(settings.MaxConnections);
        Exception? failure = null;
        async Task CheckpointAsync()
        {
            (DownloadSegment Segment, long Received)[] checkpoint;
            await _gate.WaitAsync().ConfigureAwait(false);
            try { checkpoint = segments.Select(segment => (segment, segment.ReceivedBytes)).ToArray(); }
            finally { _gate.Release(); }
            // Capture counters BEFORE flushing. Only these proven-durable prefixes may enter the JSON checkpoint.
            RandomAccess.FlushToDisk(file);
            await UpdateAsync(() => { foreach (var point in checkpoint) point.Segment.DurableBytes = Math.Max(point.Segment.DurableBytes, point.Received); }, true).ConfigureAwait(false);
        }
        async Task FetchAsync(DownloadSegment segment)
        {
            if (segment.ReceivedBytes == segment.Length) return;
            var token = workersCancellation.Token; var entered = false;
            try
            {
                await slots.WaitAsync(token).ConfigureAwait(false); entered = true;
                await UpdateAsync(() => { job.ActiveConnections++; segment.PhaseKey = "native.connecting"; }, false).ConfigureAwait(false);
                var from = segment.Start + segment.ReceivedBytes;
                using var request = new HttpRequestMessage(HttpMethod.Get, job.Url); request.Headers.UserAgent.ParseAdd(settings.UserAgent); request.Headers.AcceptEncoding.ParseAdd("identity"); request.Headers.Range = new RangeHeaderValue(from, segment.End); request.Headers.IfRange = tag is not null ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(tag)) : new RangeConditionHeaderValue(modified!.Value);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                HttpResponseMessage response;
                SessionRequest.Restore(job.ProtectedSession)?.Apply(request);
                try { response = await ClientFor(job).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TransferException("errors.connect_timeout"); }
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.OK) throw new SegmentUnsupportedException();
                    if (!response.IsSuccessStatusCode) throw new TransferException("errors.http", ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != from || range.To != segment.End || range.Length != size || response.Content.Headers.ContentLength is { } count && count != segment.End - from + 1) throw new TransferException("errors.invalid_range");
                    if (tag is not null ? response.Headers.ETag?.ToString() != tag : response.Content.Headers.LastModified != modified) throw new TransferException("errors.remote_changed");
                    if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new TransferException("errors.unexpected_encoding");
                    await UpdateAsync(() => segment.PhaseKey = "native.receiving", false).ConfigureAwait(false);
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); var buffer = new byte[65536]; var partClock = Stopwatch.StartNew(); var partSample = segment.ReceivedBytes;
                    while (true)
                    {
                        var charged = segment.ReceivedBytes != segment.Length; var permitted = charged ? await limiter.AcquireAsync((int)Math.Min(buffer.Length, segment.Length - segment.ReceivedBytes), token).ConfigureAwait(false) : 1;
                        var lease = charged ? await _quota.ReserveAsync(permitted, token, async () => { if(Interlocked.CompareExchange(ref checkpointRunning,1,0)==0){try{await CheckpointAsync().ConfigureAwait(false);}finally{Volatile.Write(ref checkpointRunning,0);}} await UpdateAsync(()=>{job.BytesPerSecond=0;job.PhaseKey="native.waiting_quota";},false).ConfigureAwait(false); }).ConfigureAwait(false) : new QuotaLease(permitted,0);
                        if (charged && lease.Granted < permitted) await limiter.RefundAsync(permitted-lease.Granted).ConfigureAwait(false); permitted=lease.Granted;
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); readTimeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds)); int read;
                        try { read = await input.ReadAsync(buffer.AsMemory(0, permitted), readTimeout.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TransferException("errors.read_timeout"); }
                        catch (IOException) { throw new TransferException("errors.network"); }
                        if (charged) { await limiter.RefundAsync(permitted - read).ConfigureAwait(false); _quota.Refund(lease,permitted-read); }
                        if (read == 0) break;
                        if (segment.ReceivedBytes + read > segment.Length) throw new TransferException("errors.invalid_range");
                        await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), segment.Start + segment.ReceivedBytes, token).ConfigureAwait(false);
                        await UpdateAsync(() =>
                        {
                            segment.ReceivedBytes += read; job.ReceivedBytes = segments.Sum(part => part.ReceivedBytes); job.TransferredBytes = priorTransferred + job.ReceivedBytes - initialBytes; job.ActiveSeconds = priorActive + clock.Elapsed.TotalSeconds;
                            if (sampleClock.ElapsedMilliseconds >= 200) { job.BytesPerSecond = (job.ReceivedBytes - sampledBytes) / sampleClock.Elapsed.TotalSeconds; sampledBytes = job.ReceivedBytes; sampleClock.Restart(); }
                        }, false).ConfigureAwait(false);
                        if (partClock.ElapsedMilliseconds >= 200) { var rate = (segment.ReceivedBytes - partSample) / partClock.Elapsed.TotalSeconds; await UpdateAsync(() => segment.BytesPerSecond = rate, false).ConfigureAwait(false); partSample = segment.ReceivedBytes; partClock.Restart(); }
                        if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastCheckpoint)).TotalMilliseconds >= 750 && Interlocked.CompareExchange(ref checkpointRunning, 1, 0) == 0)
                        {
                            try { await CheckpointAsync().ConfigureAwait(false); Volatile.Write(ref lastCheckpoint, Stopwatch.GetTimestamp()); }
                            finally { Volatile.Write(ref checkpointRunning, 0); }
                        }
                    }
                    if (segment.ReceivedBytes != segment.Length) throw new TransferException("errors.length_mismatch");
                    await UpdateAsync(() => { segment.PhaseKey = "ui.complete"; segment.BytesPerSecond = 0; }, false).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException || !workersCancellation.IsCancellationRequested) Interlocked.CompareExchange(ref failure, error, null);
                await UpdateAsync(() => { segment.PhaseKey = error is OperationCanceledException ? "ui.paused" : "ui.connection_error"; segment.BytesPerSecond = 0; }, false).ConfigureAwait(false); workersCancellation.Cancel(); throw;
            }
            finally
            {
                if (entered) { await UpdateAsync(() => job.ActiveConnections = Math.Max(0, job.ActiveConnections - 1), false).ConfigureAwait(false); slots.Release(); }
            }
        }
        try
        {
            try { await Task.WhenAll(segments.Select(FetchAsync)).ConfigureAwait(false); }
            catch
            {
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
            finally
            {
                await CheckpointAsync().ConfigureAwait(false);
                await UpdateAsync(() => { job.ActiveSeconds = priorActive + clock.Elapsed.TotalSeconds; job.ActiveConnections = 0; job.BytesPerSecond = 0; }, true).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            if (segments.Any(segment => segment.DurableBytes != segment.Length)) throw new TransferException("errors.length_mismatch");
            await UpdateAsync(() => job.PhaseKey = "native.finalizing", false).ConfigureAwait(false);
        }
        catch { throw; }
        // The same preallocated file holds disjoint verified ranges. No concatenation or second full-size file is required.
        file.Dispose(); File.Move(job.PartialPath, job.FinalPath, job.ReplaceExisting);
        await UpdateAsync(() => { job.ReplaceExisting = false; job.State = DownloadState.Completed; job.PhaseKey = "ui.complete"; job.ReceivedBytes = size; job.TotalBytes = size; job.InQueue = false; job.ErrorKey = null; job.ErrorDetail = null; }, true).ConfigureAwait(false);
        return true;
    }
}
