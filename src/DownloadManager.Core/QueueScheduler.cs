using System.Text.Json;

namespace DownloadManager.Core;

public enum ScheduleKind { Once, Daily, Weekly }
public sealed class QueueSchedule
{
    public bool Enabled { get; set; }
    public ScheduleKind Kind { get; set; }
    public DateOnly FirstDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? LastDate { get; set; }
    public TimeOnly StartTime { get; set; } = new(2, 0);
    public int DurationMinutes { get; set; } = 120;
    public bool StopAtEnd { get; set; } = true;
    public bool CatchUp { get; set; } = true;
    public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || DurationMinutes is < 1 or > 1440 || LastDate < FirstDate || (Kind == ScheduleKind.Weekly && (Days.Count == 0 || Days.Any(day => !Enum.IsDefined(day))))) throw new ArgumentException("scheduler.invalid");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
    public (string Key, DateTimeOffset Start, DateTimeOffset End)? Window(DateTimeOffset now)
    {
        if (!Enabled) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var back = 0; back <= 1; back++)
        {
            var date = today.AddDays(-back);
            if (date < FirstDate || LastDate is { } last && date > last || Kind == ScheduleKind.Once && date != FirstDate || Kind == ScheduleKind.Weekly && !Days.Contains(date.DayOfWeek)) continue;
            var local = date.ToDateTime(StartTime, DateTimeKind.Unspecified);
            // A nonexistent DST wall time is skipped. An ambiguous wall time uses the standard offset exactly once.
            if (zone.IsInvalidTime(local)) continue;
            var start = new DateTimeOffset(local, zone.GetUtcOffset(local)); var end = start.AddMinutes(DurationMinutes);
            if (now >= start && now < end) return (date.ToString("yyyy-MM-dd") + "/" + StartTime.ToString("HH:mm") + "/" + TimeZoneId, start, end);
        }
        return null;
    }
    public DateTimeOffset? Next(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime); if (FirstDate > today) today = FirstDate;
        for (var forward = 0; forward <= 370; forward++)
        {
            var date = today.AddDays(forward);
            if (date < FirstDate || LastDate is { } last && date > last || Kind == ScheduleKind.Once && date != FirstDate || Kind == ScheduleKind.Weekly && !Days.Contains(date.DayOfWeek)) continue;
            var local = date.ToDateTime(StartTime, DateTimeKind.Unspecified); if (zone.IsInvalidTime(local)) continue;
            var start = new DateTimeOffset(local, zone.GetUtcOffset(local)); if (start > now) return start;
        }
        return null;
    }
}
public sealed class DownloadQueue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int Parallel { get; set; } = 1;
    public bool Synchronization { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public DateTimeOffset? NextRun { get; set; }
    public bool CycleSucceeded { get; set; }
    public int RetryLimit { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 30;
    public Dictionary<string,int> Attempts { get; set; } = [];
    public Dictionary<string,DateTimeOffset> RetryAt { get; set; } = [];
    public List<string> JobIds { get; set; } = [];
    public CompletionOptions Completion { get; set; } = new();
    public QueueSchedule Schedule { get; set; } = new();
    public bool IsRunning { get; set; }
    public string? LastOccurrence { get; set; }
    public DateTimeOffset? RunUntil { get; set; }
    public HashSet<string>? RunScope { get; set; }
    public HashSet<string> FailedProbes {get;set;}=[];
    public HashSet<string> Attempted { get; set; } = [];
    public HashSet<string> Owned { get; set; } = [];
    public DownloadQueue Copy() => JsonSerializer.Deserialize<DownloadQueue>(JsonSerializer.Serialize(this))!;
}
public sealed record QueueEvent(DateTimeOffset Time, string QueueId, string Key, string? JobId = null);
public sealed class QueueStore
{
    public List<DownloadQueue> Queues { get; set; } = [new() { Id = "main" }];
    public List<QueueEvent> Events { get; set; } = [];
}

// One serial coordinator owns queue starts; engine workers remain independent of the UI and clock.
public sealed class QueueScheduler : IAsyncDisposable
{
    private readonly DownloadEngine _engine;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private QueueStore _store;
    private int _turn;
    private bool _resumeRecovered = true;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private string? _saved;
    private readonly List<CompletionTicket> _completions = [];
    public async Task<IReadOnlyList<CompletionTicket>> DrainCompletionsAsync() { await _gate.WaitAsync(); try { var result = _completions.ToArray(); _completions.Clear(); return result; } finally { _gate.Release(); } }
    public int GlobalParallel { get; set; } = 1;
    public QueueScheduler(DownloadEngine engine, string dataDirectory)
    {
        _engine = engine; Directory.CreateDirectory(dataDirectory); _path = Path.Combine(dataDirectory, "queues.json");
        _store = File.Exists(_path) ? JsonSerializer.Deserialize<QueueStore>(File.ReadAllText(_path)) ?? throw new InvalidDataException("scheduler.invalid") : new();
        if (_store.Queues.All(queue => queue.Id != "main")) _store.Queues.Insert(0, new() { Id = "main" });
        if (_store.Queues.Select(queue => queue.Id).Distinct().Count() != _store.Queues.Count) throw new InvalidDataException("scheduler.invalid");
        foreach (var queue in _store.Queues) { Validate(queue,false); foreach (var id in queue.Owned) queue.Attempted.Remove(id); }
    }
    private static void Validate(DownloadQueue queue,bool checkFile=true)
    {
        if (string.IsNullOrWhiteSpace(queue.Id) || queue.Name.Length > 80 || queue.Parallel is < 1 or > 10 || queue.RetryLimit is < 0 or > 10 || queue.RetryDelaySeconds is < 1 or > 3600 || queue.IntervalMinutes is < 1 or > 1440) throw new ArgumentException("scheduler.invalid");
        queue.Schedule.Validate(); queue.Completion.Validate(checkFile);
    }
    public async Task SetSequentialDefaultsAsync(){await _gate.WaitAsync().ConfigureAwait(false);try{GlobalParallel=1;foreach(var queue in _store.Queues)queue.Parallel=1;Save();}finally{_gate.Release();}}
    public void Start() { _loop ??= Task.Run(LoopAsync); }
    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { do { try { await TickAsync(DateTimeOffset.UtcNow); } catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException) { await RecordErrorAsync(); } } while (await timer.WaitForNextTickAsync(_stop.Token)); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task RecordErrorAsync() { await _gate.WaitAsync(); try { Event("", "scheduler.error"); Save(); } finally { _gate.Release(); } }
    public async Task<IReadOnlyList<DownloadQueue>> SnapshotAsync() { await _gate.WaitAsync(); try { return _store.Queues.Select(queue => queue.Copy()).ToArray(); } finally { _gate.Release(); } }
    public async Task<IReadOnlyList<QueueEvent>> EventsAsync() { await _gate.WaitAsync(); try { return _store.Events.ToArray(); } finally { _gate.Release(); } }
    public async Task SaveQueueAsync(DownloadQueue edited)
    {
        Validate(edited); await _gate.WaitAsync();
        try
        {
            var old = _store.Queues.FirstOrDefault(queue => queue.Id == edited.Id);
            if (old is null) { var added = edited.Copy(); added.JobIds.Clear(); added.IsRunning = false; added.Owned.Clear(); added.Attempted.Clear(); _store.Queues.Add(added); }
            else { old.Name = edited.Name.Trim(); old.Synchronization=edited.Synchronization; old.IntervalMinutes=edited.IntervalMinutes; old.Parallel = edited.Parallel; old.RetryLimit = edited.RetryLimit; old.RetryDelaySeconds = edited.RetryDelaySeconds; old.Schedule = edited.Copy().Schedule; old.Completion = edited.Copy().Completion; }
            Save();
        }
        finally { _gate.Release(); }
    }
    public async Task DeleteAsync(string queueId)
    {
        if (queueId == "main") throw new ArgumentException("scheduler.main_required");
        await _gate.WaitAsync(); try { var queue = Find(queueId); await StopLocked(queue); var main = Find("main"); main.JobIds.AddRange(queue.JobIds.Where(id => !main.JobIds.Contains(id))); _store.Queues.Remove(queue); Save(); } finally { _gate.Release(); }
    }
    public async Task AssignAsync(IEnumerable<string> ids, string queueId)
    {
        await _gate.WaitAsync(); try { var target = Find(queueId); foreach (var id in ids.Distinct()) { await _engine.PauseAsync(id); await _engine.SetQueueAsync(id, true); foreach (var queue in _store.Queues) { queue.JobIds.Remove(id); queue.Attempted.Remove(id); queue.Owned.Remove(id); } target.JobIds.Add(id); } Save(); } finally { _gate.Release(); }
    }
    public async Task UnassignAsync(IEnumerable<string> ids)
    {
        await _gate.WaitAsync();try{foreach(var id in ids.Distinct()){await _engine.PauseAsync(id);await _engine.SetQueueAsync(id,false);foreach(var queue in _store.Queues){queue.JobIds.Remove(id);queue.Attempted.Remove(id);queue.Attempts.Remove(id);queue.RetryAt.Remove(id);queue.Owned.Remove(id);}}Save();}finally{_gate.Release();}
    }
    public async Task MoveAsync(string queueId, string id, int delta)
    {
        await _gate.WaitAsync(); try { var queue = Find(queueId); var index = queue.JobIds.IndexOf(id); if (index >= 0) { queue.JobIds.RemoveAt(index); queue.JobIds.Insert(Math.Clamp(index + Math.Sign(delta), 0, queue.JobIds.Count), id); Save(); } } finally { _gate.Release(); }
    }
    public async Task MoveManyAsync(string queueId,IEnumerable<string> ids,int delta)
    {
        await _gate.WaitAsync();try{var queue=Find(queueId);var selected=ids.ToHashSet();var order=queue.JobIds.Where(selected.Contains).ToArray();if(delta>0)Array.Reverse(order);foreach(var id in order){var index=queue.JobIds.IndexOf(id);var next=index+Math.Sign(delta);if(next>=0&&next<queue.JobIds.Count&&!selected.Contains(queue.JobIds[next]))(queue.JobIds[index],queue.JobIds[next])=(queue.JobIds[next],queue.JobIds[index]);}Save();}finally{_gate.Release();}
    }
    public async Task StartAsync(string queueId, DateTimeOffset? now = null)
    {
        await _gate.WaitAsync(); try { var queue=Find(queueId);await ReleaseManualPausesLocked(queue,queue.JobIds);if(queue.IsRunning)queue.RunScope=null;else Begin(queue,null,null); Save(); } finally { _gate.Release(); } await TickAsync(now ?? DateTimeOffset.UtcNow);
    }
    public async Task StartSelectedAsync(string queueId,IEnumerable<string> ids)
    {
        await _gate.WaitAsync();try{var queue=Find(queueId);var scope=ids.Where(queue.JobIds.Contains).ToHashSet();await ReleaseManualPausesLocked(queue,scope);if(!queue.IsRunning){Begin(queue,null,null);queue.RunScope=scope;}else queue.RunScope?.UnionWith(scope);Save();}finally{_gate.Release();}await TickAsync(DateTimeOffset.UtcNow);
    }
    private async Task ReleaseManualPausesLocked(DownloadQueue queue,IEnumerable<string> ids)
    {
        var selected=ids.ToHashSet();
        foreach(var job in (await _engine.SnapshotAsync()).Where(job=>selected.Contains(job.Id)&&job.ManuallyPaused&&job.State!=DownloadState.Completed))
        {await _engine.MarkQueuedAsync(job.Id);queue.Attempted.Remove(job.Id);queue.FailedProbes.Remove(job.Id);queue.Attempts.Remove(job.Id);queue.RetryAt.Remove(job.Id);queue.Owned.Remove(job.Id);}
    }
    public async Task PauseSelectedAsync(IEnumerable<string> ids)
    {
        await _gate.WaitAsync();
        try
        {
            var jobs=(await _engine.SnapshotAsync()).ToDictionary(job=>job.Id);
            foreach(var id in ids.Distinct().Where(id=>jobs.TryGetValue(id,out var job)&&job.State!=DownloadState.Completed))
            {
                await _engine.PauseAsync(id,manual:true);
                foreach(var queue in _store.Queues.Where(queue=>queue.JobIds.Contains(id)))
                {queue.Owned.Remove(id);queue.Attempted.Add(id);queue.RetryAt.Remove(id);queue.FailedProbes.Remove(id);}
            }
            Save();
        }
        finally{_gate.Release();}
    }
    public async Task ResumeSelectedAsync(IEnumerable<string> ids)
    {
        await _gate.WaitAsync();
        try{
            var jobs=(await _engine.SnapshotAsync()).ToDictionary(j=>j.Id);
            foreach(var group in ids.Distinct().Where(id=>jobs.TryGetValue(id,out var job)&&job.State is not(DownloadState.Running or DownloadState.Completed)).GroupBy(id=>_store.Queues.FirstOrDefault(q=>q.JobIds.Contains(id))?.Id??"main")){
                var queue=Find(group.Key);var selected=group.ToHashSet();
                if(!queue.IsRunning){Begin(queue,null,null);queue.RunScope=selected;}else queue.RunScope?.UnionWith(selected);
                foreach(var id in selected){if(jobs[id].PendingBrowserCommit)await _engine.ReleaseBrowserHoldAsync(id);await _engine.MarkQueuedAsync(id);if(!queue.JobIds.Contains(id))queue.JobIds.Add(id);queue.Attempted.Remove(id);queue.FailedProbes.Remove(id);queue.Attempts.Remove(id);queue.RetryAt.Remove(id);queue.Owned.Remove(id);}
            }Save();
        }finally{_gate.Release();}
        await TickAsync(DateTimeOffset.UtcNow);
    }
    public async Task StopAsync(string queueId)
    {
        await _gate.WaitAsync(); try { var queue = Find(queueId); queue.LastOccurrence = queue.Schedule.Window(_now)?.Key ?? queue.LastOccurrence; await StopLocked(queue); Save(); } finally { _gate.Release(); }
    }
    public async Task StopAllAsync()
    {
        await _gate.WaitAsync(); try { foreach (var queue in _store.Queues) { queue.LastOccurrence = queue.Schedule.Window(_now)?.Key ?? queue.LastOccurrence; await StopLocked(queue); } Save(); } finally { _gate.Release(); }
    }
    private void Begin(DownloadQueue queue, string? occurrence, DateTimeOffset? end)
    {
        queue.IsRunning = true; queue.RunScope=null; queue.NextRun=null; queue.CycleSucceeded=false; queue.Attempts.Clear(); queue.RetryAt.Clear(); queue.Attempted.Clear(); queue.FailedProbes.Clear(); queue.Owned.Clear(); queue.RunUntil = end; if (occurrence is not null) queue.LastOccurrence = occurrence; Event(queue.Id, "scheduler.started");
    }
    private async Task StopLocked(DownloadQueue queue)
    {
        queue.IsRunning = false; queue.NextRun=null; queue.CycleSucceeded=false;
        foreach (var id in queue.Owned.ToArray()) { if ((await _engine.SnapshotAsync()).Any(job => job.Id == id && job.State == DownloadState.Running)) await _engine.PauseAsync(id); }
        queue.Owned.Clear(); Event(queue.Id, "scheduler.stopped");
    }
    public async Task TickAsync(DateTimeOffset now)
    {
        await _gate.WaitAsync();
        try
        {
            _now = now;
            var jobs = (await _engine.SnapshotAsync()).ToDictionary(job => job.Id);
            foreach (var queue in _store.Queues) foreach (var id in queue.Owned.Where(id => jobs.TryGetValue(id, out var job) && !job.InQueue && job.State == DownloadState.Running).ToArray()) { await _engine.PauseAsync(id); jobs[id].State = DownloadState.Paused; queue.Owned.Remove(id); }
            foreach(var queue in _store.Queues.Where(q=>q.Synchronization)) foreach(var id in queue.JobIds.Where(id=>jobs.TryGetValue(id,out var job)&&job.State==DownloadState.Completed&&!job.InQueue).ToArray()){await _engine.SetQueueAsync(id,true);jobs[id].InQueue=true;}
            foreach (var queue in _store.Queues) queue.JobIds.RemoveAll(id => !jobs.TryGetValue(id, out var job) || !job.InQueue);
            var members = _store.Queues.SelectMany(queue => queue.JobIds).ToHashSet();
            Find("main").JobIds.AddRange(jobs.Values.Where(job => job.InQueue && !members.Contains(job.Id)).OrderBy(job => job.AddedAt).Select(job => job.Id));
            foreach (var queue in _store.Queues)
            {
                foreach(var removed in queue.RetryAt.Keys.Where(id=>!queue.JobIds.Contains(id)).ToArray())queue.RetryAt.Remove(removed);
                if(!queue.IsRunning && queue.Synchronization && queue.NextRun is { } next && now>=next)
                {
                    if(queue.RunUntil is { } until && now>=until){queue.NextRun=null;if(queue.CycleSucceeded && queue.Completion.Action!=CompletionAction.None)_completions.Add(new(Guid.NewGuid().ToString("N"),queue.Id,queue.Completion.Copy()));queue.CycleSucceeded=false;}
                    else Begin(queue,queue.LastOccurrence,queue.RunUntil);
                }
                if (queue.IsRunning && queue.RunUntil is { } end && now >= end) await StopLocked(queue);
                var window = queue.Schedule.Window(now);
                if (!queue.IsRunning && window is { } current && queue.LastOccurrence != current.Key)
                {
                    queue.LastOccurrence = current.Key;
                    if (queue.Schedule.CatchUp || now - current.Start <= TimeSpan.FromSeconds(65)) Begin(queue, current.Key, queue.Schedule.StopAtEnd ? current.End : null);
                    else Event(queue.Id, "scheduler.missed");
                }
                if (queue.IsRunning) foreach (var id in queue.JobIds.Where(id => (queue.RunScope is null || queue.RunScope.Contains(id)) && jobs.TryGetValue(id,out var job) && !job.ManuallyPaused && job.State == DownloadState.Failed && queue.Attempted.Contains(id)).ToArray())
                {
                    if (queue.Attempts.GetValueOrDefault(id) <= queue.RetryLimit && !queue.RetryAt.ContainsKey(id)) { queue.RetryAt[id]=now.AddSeconds(queue.RetryDelaySeconds); Event(queue.Id,"scheduler.retry_wait",id); }
                    if (queue.RetryAt.TryGetValue(id,out var retry) && now>=retry) { queue.RetryAt.Remove(id); queue.Attempted.Remove(id); queue.JobIds.Remove(id); queue.JobIds.Add(id); }
                }
                if (_resumeRecovered && queue.IsRunning) Event(queue.Id, "scheduler.recovered");
            }
            _resumeRecovered = false;
            // Persist occurrence ownership before launching any request, preventing duplicate starts after a crash.
            Save();
            var active = jobs.Values.Count(job => job.State == DownloadState.Running);
            var ordered = _store.Queues.Skip(_turn % _store.Queues.Count).Concat(_store.Queues.Take(_turn % _store.Queues.Count)).ToArray(); _turn++;
            bool launched;
            do
            {
                launched = false;
                foreach (var queue in ordered.Where(queue => queue.IsRunning))
                {
                    if (active >= Math.Clamp(GlobalParallel, 1, 10)) break;
                    if (queue.JobIds.Count(id => jobs.TryGetValue(id, out var item) && item.State == DownloadState.Running) >= queue.Parallel) continue;
                    var id = queue.JobIds.FirstOrDefault(id => (queue.RunScope is null || queue.RunScope.Contains(id)) && jobs.TryGetValue(id, out var item) && !item.ManuallyPaused && !item.PendingBrowserCommit && item.State != DownloadState.Running && (item.State != DownloadState.Completed || queue.Synchronization) && !queue.Attempted.Contains(id));
                    if (id is null) continue;
                    queue.Attempted.Add(id); queue.Attempts[id]=queue.Attempts.GetValueOrDefault(id)+1; queue.Owned.Add(id); Save();
                    try { var started=true; if(queue.Synchronization && jobs[id].State==DownloadState.Completed)started=await _engine.SynchronizeAsync(id,_stop.Token);else await _engine.StartAsync(id);
                        if(!started){launched=true;Event(queue.Id,"scheduler.unchanged",id);continue;} jobs[id].State = DownloadState.Running; active++; launched = true; Event(queue.Id, "scheduler.job_started", id); }
                    catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or HttpRequestException) { queue.FailedProbes.Add(id); Event(queue.Id, "scheduler.job_failed", id); }
                }
            } while (launched && active < Math.Clamp(GlobalParallel, 1, 10));
            foreach (var queue in _store.Queues.Where(queue => queue.IsRunning))
            {
                if (queue.RetryAt.Count == 0 && !queue.JobIds.Any(id => (queue.RunScope is null || queue.RunScope.Contains(id)) && jobs.TryGetValue(id, out var item) && item.State == DownloadState.Running) && !queue.JobIds.Any(id => (queue.RunScope is null || queue.RunScope.Contains(id)) && jobs.TryGetValue(id, out var item) && !item.ManuallyPaused && (item.State != DownloadState.Completed || queue.Synchronization) && !queue.Attempted.Contains(id))) { queue.IsRunning = false;
                    var allSucceeded = queue.FailedProbes.Count == 0 && queue.Attempted.Count > 0 && queue.Attempted.All(id => jobs.TryGetValue(id, out var job) && job.State == DownloadState.Completed);
                    if(queue.Synchronization){queue.CycleSucceeded=allSucceeded;queue.NextRun=now.AddMinutes(queue.IntervalMinutes);if(queue.RunUntil is { } until && queue.NextRun>until)queue.NextRun=until;}
                    if (allSucceeded && !queue.Synchronization && queue.Completion.Action != CompletionAction.None) _completions.Add(new(Guid.NewGuid().ToString("N"), queue.Id, queue.Completion.Copy()));
                    queue.Owned.Clear(); Event(queue.Id, allSucceeded ? "scheduler.finished" : "scheduler.finished_errors"); }
            }
            Save();
        }
        finally { _gate.Release(); }
    }
    private DownloadQueue Find(string id) => _store.Queues.First(queue => queue.Id == id);
    private void Event(string id, string key, string? job = null) { _store.Events.Add(new(_now, id, key, job)); if (_store.Events.Count > 200) _store.Events.RemoveRange(0, _store.Events.Count - 200); }
    private void Save()
    {
        var json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }); if (json == _saved) return;
        using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { using var writer = new StreamWriter(file, leaveOpen: true); writer.Write(json); writer.Flush(); file.Flush(true); }
        File.Move(_path + ".tmp", _path, true); _saved = json;
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); if (_loop is not null) await _loop; _stop.Dispose(); }
}
