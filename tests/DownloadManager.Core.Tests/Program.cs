using DownloadManager.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "test-artifacts"));
Directory.CreateDirectory(root);
await using var server = new FixtureServer();
var links = LinkImport.Extract("https://EXAMPLE.test/A.zip#one https://example.test/A.zip#two https://example.test/a.zip https://name:secret@example.test/b.zip https://example.test/B.zip?a=1&amp;b=2");
Check(links.Links.Count == 3 && links.Duplicates == 1 && links.Rejected == 1, "import_dedup_or_validation");
Check(links.Links.Any(link => link.Url.Contains("?a=1&amp;b=2")), "plain_signed_query_preserved");
Check(LinkImport.FromClipboard("", "<a href=\"https://example.test/B.zip?a=1&amp;b=2\">Download</a>").Links.Single().Url.EndsWith("?a=1&b=2"), "html_query_decoding");
Check(LinkImport.Extract("متن اضافی https://example.test/a.zip,https://example.test/b.zip;https://example.test/c.zip پایان").Links.Count == 3, "adjacent_links_separated");
Check(LinkImport.Extract("https://example.test/a.ziphttps://example.test/b.zip").Links.Count == 0, "ambiguous_links_rejected");
Check(LinkImport.Extract("https://example.test/a?url=https://example.test/b&sig=abc!").Links.Single().Url.EndsWith("&sig=abc!"), "nested_signed_url_preserved");
Check(LinkImport.Extract("(https://example.test/a?sig=abc)").Links.Single().Url.EndsWith("?sig=abc"), "prose_wrapper_removed");
Check(LinkImport.Extract("https://example.test/a?sig=abc)").Links.Single().Url.EndsWith("?sig=abc)"), "query_parenthesis_preserved");
var rich = LinkImport.FromClipboard("https://example.test/a.zip", "SourceURL:https://page.test/\n<!--StartFragment--><a data-href='https://fake.test/' title=\"href='https://fake2.test/'\" href='https://example.test/a.zip'>file</a><img src='https://image.test/a.png'><script>https://script.test/</script><p>https://example.test/b.zip</p><!--EndFragment-->");
Check(rich.Links.Select(x => x.Url).SequenceEqual(new[] { "https://example.test/a.zip", "https://example.test/b.zip" }), "rich_clipboard_targets_only");
Check(LinkImport.Extract("ftp://example.test/a.zip javascript:alert(1) file:///c:/a.txt").Links.Count == 1, "unsupported_import_scheme");
Check(LinkImport.ExpandRange("https://example.test/file[001-003].zip").SequenceEqual(new[] { "https://example.test/file001.zip", "https://example.test/file002.zip", "https://example.test/file003.zip" }), "batch_padding");
foreach (var invalid in new[] { "https://example.test/[3-1].zip", "https://example.test/[1-1001].zip", "file:///a[1-2]", "https://example.test/no-range" }) { try { LinkImport.ExpandRange(invalid); throw new InvalidOperationException("invalid_pattern_accepted"); } catch (ArgumentException) { } }
try { LinkImport.Extract(new string('x', LinkImport.MaximumTextLength + 1)); throw new InvalidOperationException("oversized_import_accepted"); } catch (ArgumentException) { }
var bounded = LinkImport.Extract(string.Join("\n", Enumerable.Range(0, 1001).Select(index => $"https://example.test/{index}.zip"))); Check(bounded.Limited && bounded.Links.Count == 1000, "import_link_limit");
var fragment = LinkImport.ClipboardFragment("SourceURL:https://not-a-download.test/\n<!--StartFragment--><a href=\"https://example.test/a.zip\">file</a><!--EndFragment-->"); Check(LinkImport.Extract(fragment).Links.Single().Url == "https://example.test/a.zip", "clipboard_metadata_imported");
Console.WriteLine("PASS: clipboard/text parser, duplicate handling, scheme/credentials validation, bounded inputs and numbered batches (fixture strings only; no system clipboard read).");
await server.StartAsync();
var passed = 0;
var testPort = server.Port;
Console.WriteLine($"Fixture bound only to 127.0.0.1:{testPort}");
async Task AssertFileAsync(DownloadJob job)
{
    var actual = await File.ReadAllBytesAsync(job.FinalPath);
    Check(SHA256.HashData(actual).SequenceEqual(SHA256.HashData(server.CurrentData)), "hash_mismatch");
}

var state = Path.Combine(root, "resume-state");
var folder = Path.Combine(root, "files");
string resumeId;
long pausedBytes;
await using (var engine = new DownloadEngine(state))
{
    var job = await engine.AddAsync(server.Url("range"), folder, "resume.bin");
    await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144);
    await engine.PauseAsync(job.Id);
    var paused = (await engine.SnapshotAsync()).Single();
    Check(paused.State == DownloadState.Paused && paused.ReceivedBytes < server.CurrentData.Length, "pause_failed");
    Check(paused.ResumeSupported, "resume_not_advertised");
    resumeId = job.Id; pausedBytes = paused.ReceivedBytes;
}
await using (var engine = new DownloadEngine(state))
{
    Check((await engine.SnapshotAsync()).Single().ReceivedBytes == pausedBytes, "restart_offset_lost");
    await engine.StartAsync(resumeId);
    await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed);
    var job = (await engine.SnapshotAsync()).Single();
    Check(job.State == DownloadState.Completed, "resume_failed:" + job.ErrorKey);
    Check(server.Requests.Any(request => request.Path == "range" && request.Offset == pausedBytes), "range_request_missing");
    await AssertFileAsync(job); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "no-range-state")))
{
    var job = await engine.AddAsync(server.Url("no-range"), folder, "restart.bin");
    await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144);
    await engine.PauseAsync(job.Id);
    Check(!(await engine.SnapshotAsync()).Single().ResumeSupported, "unsafe_resume_advertised");
    await engine.StartAsync(job.Id);
    await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed);
    var completed = (await engine.SnapshotAsync()).Single();
    Check(completed.State == DownloadState.Completed, "no_range_restart_failed");
    await AssertFileAsync(completed); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "changed-state")))
{
    var job = await engine.AddAsync(server.Url("changed"), folder, "changed.bin");
    await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144);
    await engine.PauseAsync(job.Id);
    server.ChangeVersion();
    await engine.StartAsync(job.Id);
    await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed);
    var completed = (await engine.SnapshotAsync()).Single();
    Check(completed.State == DownloadState.Completed, "changed_resource_failed");
    await AssertFileAsync(completed); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "invalid-range-state")))
{
    var job = await engine.AddAsync(server.Url("bad-range"), folder, "invalid.bin");
    await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144);
    await engine.PauseAsync(job.Id);
    await engine.StartAsync(job.Id);
    await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed);
    var failed = (await engine.SnapshotAsync()).Single();
    Check(failed.State == DownloadState.Failed && failed.ErrorKey == "errors.invalid_range", "bad_range_accepted");
    Check(!File.Exists(job.FinalPath), "bad_range_finalized"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "short-state")))
{
    var job = await engine.AddAsync(server.Url("short"), folder, "short.bin");
    await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed);
    Check((await engine.SnapshotAsync()).Single().State == DownloadState.Failed, "truncated_transfer_completed");
    Check(!File.Exists(job.FinalPath), "truncated_transfer_finalized"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "filename-state")))
{
    var one = await engine.AddAsync(server.Url("range"), folder, "../CON.bin", false);
    var two = await engine.AddAsync(server.Url("range"), folder, "../CON.bin", false);
    Check(Path.GetDirectoryName(one.FinalPath) == Path.GetFullPath(folder), "filename_escaped_folder");
    Check(one.FileName.StartsWith('_') && one.FinalPath != two.FinalPath, "filename_collision_or_device");
    await engine.RemoveAsync(one.Id);
    Check((await engine.SnapshotAsync()).Count == 1, "remove_failed"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "retry-state")))
{
    engine.Configure(new TransferSettings { RetryCount = 1, RetryDelaySeconds = 0, UserAgent = "DownloadManagerTest/1.0" });
    var job = await engine.AddAsync(server.Url("flaky"), folder, "retry.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed);
    await AssertFileAsync((await engine.SnapshotAsync()).Single()); Check(server.UserAgents.Contains("DownloadManagerTest/1.0"), "custom_agent_missing"); Check(server.Requests.Count(request => request.Path == "flaky") == 2, "retry_count_incorrect"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "cancel-retry-state")))
{
    engine.Configure(new TransferSettings { RetryCount = 2, RetryDelaySeconds = 10 }); var job = await engine.AddAsync(server.Url("unavailable"), folder, "cancel-retry.bin");
    await Until(async () => (await engine.SnapshotAsync()).Single().ErrorKey == "errors.retry_pending"); var clock = System.Diagnostics.Stopwatch.StartNew(); await engine.PauseAsync(job.Id);
    Check(clock.Elapsed < TimeSpan.FromSeconds(2) && (await engine.SnapshotAsync()).Single().State == DownloadState.Paused, "retry_pause_not_responsive"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "throttle-state")))
{
    engine.Configure(new TransferSettings { BytesPerSecond = 2 * 1024 * 1024 }); var clock = System.Diagnostics.Stopwatch.StartNew(); var job = await engine.AddAsync(server.Url("range"), folder, "throttled.bin");
    await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed); Check(clock.Elapsed >= TimeSpan.FromSeconds(1.8), "speed_limit_not_applied"); await AssertFileAsync((await engine.SnapshotAsync()).Single()); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "timeout-state")))
{
    engine.Configure(new TransferSettings { TimeoutSeconds = 1 }); var job = await engine.AddAsync(server.Url("stall"), folder, "timeout.bin");
    await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Failed); Check((await engine.SnapshotAsync()).Single().ErrorKey == "errors.read_timeout", "configured_timeout_missing"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "live-limit-state")))
{
    var job = await engine.AddAsync(server.Url("range"), folder, "live-limit.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144);
    await engine.SetSpeedLimitAsync(job.Id, 128 * 1024, true); Check((await engine.SnapshotAsync()).Single().SpeedLimitBytesPerSecond == 128 * 1024, "live_limit_not_set"); await Task.Delay(150);
    var clock = System.Diagnostics.Stopwatch.StartNew(); await engine.SetSpeedLimitAsync(job.Id, 0, true);
    await Until(async () => (await engine.SnapshotAsync()).Single() is { State: DownloadState.Completed, TransferredBytes: > 0, ActiveSeconds: > 0 } item && item.TransferredBytes == server.CurrentData.Length);
    Check(clock.Elapsed < TimeSpan.FromSeconds(4), "live_limit_disable_not_responsive"); var done = (await engine.SnapshotAsync()).Single(); Check(done.HttpStatus == 200 && done.EffectiveUrl is not null && done.PhaseKey == "ui.complete", "telemetry_incorrect"); await AssertFileAsync(done); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "throttle-cancel-state")))
{
    var job = await engine.AddAsync(server.Url("range"), folder, "throttle-cancel.bin"); await engine.SetSpeedLimitAsync(job.Id, 1024, true);
    await Until(async () => (await engine.SnapshotAsync()).Single().PhaseKey == "native.receiving"); var clock = System.Diagnostics.Stopwatch.StartNew(); await engine.PauseAsync(job.Id);
    Check(clock.Elapsed < TimeSpan.FromSeconds(2) && (await engine.SnapshotAsync()).Single().State == DownloadState.Paused, "throttle_cancellation_unresponsive"); passed++;
}
var preferenceState = Path.Combine(root, "job-preferences-state"); string preferenceId;
await using (var engine = new DownloadEngine(preferenceState))
{
    var job = await engine.AddAsync(server.Url("range"), folder, "job-preferences.bin", false); preferenceId = job.Id; await engine.SetSpeedLimitAsync(job.Id, 65536, true); await engine.SetCompletionOptionsAsync(job.Id, false, true);
}
await using (var engine = new DownloadEngine(preferenceState))
{
    var restored = (await engine.SnapshotAsync()).Single(); Check(restored.SpeedLimitBytesPerSecond == 65536 && restored.RememberSpeedLimit && !restored.ShowCompletionSummary && restored.CloseProgressOnCompletion, "job_preferences_not_restored");
    await engine.SetSpeedLimitAsync(preferenceId, 32768, false); await engine.StartAsync(preferenceId); Check((await engine.SnapshotAsync()).Single().SpeedLimitBytesPerSecond is null, "transient_limit_survived_restart"); await engine.PauseAsync(preferenceId); passed++;
}
var multipartState = Path.Combine(root, "multipart-resume-state"); string multipartId; DownloadJob multipartPaused;
await using (var engine = new DownloadEngine(multipartState))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); var job = await engine.AddAsync(server.Url("multi-8"), folder, "multipart-resume.bin"); multipartId = job.Id;
    await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144); await engine.PauseAsync(job.Id); multipartPaused = (await engine.SnapshotAsync()).Single();
    Check(multipartPaused.Segments.Count == 8 && multipartPaused.ActiveConnections == 0 && multipartPaused.ReceivedBytes < server.CurrentData.Length, "multipart_pause_failed");
    Check(new FileInfo(multipartPaused.PartialPath).Length == server.CurrentData.Length && multipartPaused.ReceivedBytes < new FileInfo(multipartPaused.PartialPath).Length, "preallocated_file_treated_as_received");
    Check(multipartPaused.Segments.All(segment => segment.DurableBytes == segment.ReceivedBytes), "pause_checkpoint_not_durable");
}
await using (var engine = new DownloadEngine(multipartState))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); var restored = (await engine.SnapshotAsync()).Single(); Check(restored.ReceivedBytes == multipartPaused.ReceivedBytes, "multipart_restart_counter_lost");
    await engine.StartAsync(multipartId); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Completed, "multipart_resume_failed:" + done.ErrorKey); await AssertFileAsync(done);
    Check(server.MaximumByPath.GetValueOrDefault("multi-8") >= 8, "no_real_parallel_requests");
    foreach (var segment in multipartPaused.Segments.Where(segment => segment.DurableBytes > 0 && segment.DurableBytes < segment.Length)) Check(server.Ranges.Any(range => range.Path == "multi-8" && range.From == segment.Start + segment.DurableBytes && range.To == segment.End), "segment_resume_range_missing"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-no-range-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); var job = await engine.AddAsync(server.Url("no-range"), folder, "multi-fallback.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Completed && done.Segments.Count == 0 && done.ConnectionLimit == 1, "unsupported_server_did_not_fallback"); await AssertFileAsync(done); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-ignore-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 4 }); var job = await engine.AddAsync(server.Url("multi-ignore"), folder, "multi-ignore.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Completed && done.Segments.Count == 0, "lying_range_server_not_restarted"); await AssertFileAsync(done); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-invalid-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 4 }); var job = await engine.AddAsync(server.Url("multi-bad"), folder, "multi-invalid.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Failed && done.ErrorKey == "errors.invalid_range" && !File.Exists(done.FinalPath), "bad_segment_range_accepted"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-version-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 4, RetryCount = 1, RetryDelaySeconds = 0 }); var job = await engine.AddAsync(server.Url("multi-change"), folder, "multi-version.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed); var done = (await engine.SnapshotAsync()).Single(); await AssertFileAsync(done); Check(done.Segments.All(segment => segment.DurableBytes == segment.Length), "version_retry_not_durable"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-migration-state")))
{
    var job = await engine.AddAsync(server.Url("multi-migrate"), folder, "multi-migrate.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144); await engine.PauseAsync(job.Id); var prefix = (await engine.SnapshotAsync()).Single().ReceivedBytes;
    engine.Configure(new TransferSettings { MaxConnections = 4 }); await engine.StartAsync(job.Id); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Completed && done.Segments.Count == 4, "prefix_migration_failed"); Check(server.Ranges.Any(range => range.Path == "multi-migrate" && range.From == prefix), "prefix_migration_redownloaded_valid_prefix"); await AssertFileAsync(done); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-throttle-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 4, BytesPerSecond = 2 * 1024 * 1024 }); var clock = System.Diagnostics.Stopwatch.StartNew(); var job = await engine.AddAsync(server.Url("multi-throttle"), folder, "multi-throttle.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed); Check(clock.Elapsed >= TimeSpan.FromSeconds(1.8), "multipart_speed_limit_applied_per_segment_instead_of_file"); await AssertFileAsync((await engine.SnapshotAsync()).Single()); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-throttle-pause-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 8, BytesPerSecond = 1024 }); var job = await engine.AddAsync(server.Url("multi-pause"), folder, "multi-pause.bin"); await Until(async () => (await engine.SnapshotAsync()).Single() is { Segments.Count: 8, ReceivedBytes: > 0 }); var clock = System.Diagnostics.Stopwatch.StartNew(); await engine.PauseAsync(job.Id); var paused = (await engine.SnapshotAsync()).Single(); Check(clock.Elapsed < TimeSpan.FromSeconds(2) && paused.ActiveConnections == 0 && paused.State == DownloadState.Paused && paused.ReceivedBytes <= 2048, "multipart_pause_or_shared_initial_rate_not_responsive"); passed++;
}
var missingState = Path.Combine(root, "multipart-missing-state"); string missingId;
await using (var engine = new DownloadEngine(missingState))
{
    engine.Configure(new TransferSettings { MaxConnections = 4 }); var job = await engine.AddAsync(server.Url("multi-missing"), folder, "multi-missing.bin"); missingId = job.Id; await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144); await engine.PauseAsync(job.Id); File.Delete(job.PartialPath);
}
await using (var engine = new DownloadEngine(missingState))
{
    engine.Configure(new TransferSettings { MaxConnections = 4 }); await engine.StartAsync(missingId); await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed); await AssertFileAsync((await engine.SnapshotAsync()).Single()); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-two-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 2 }); var job = await engine.AddAsync(server.Url("multi-two"), folder, "multi-two.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed); var done = (await engine.SnapshotAsync()).Single(); Check(done.Segments.Count == 2 && server.MaximumByPath.GetValueOrDefault("multi-two") == 2, "two_connection_setting_ignored"); await AssertFileAsync(done); passed++;
}
var rollbackState = Path.Combine(root, "multipart-rollback-state"); string rollbackId; string staleCheckpoint; long durableBefore;
await using (var engine = new DownloadEngine(rollbackState))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); var job = await engine.AddAsync(server.Url("multi-rollback"), folder, "multi-rollback.bin"); rollbackId = job.Id; await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144); staleCheckpoint = await File.ReadAllTextAsync(Path.Combine(rollbackState, "downloads.json")); var checkpoint = JsonNode.Parse(staleCheckpoint)![0]!; durableBefore = checkpoint["Segments"]!.AsArray().Sum(segment => (long)segment!["DurableBytes"]!); await engine.PauseAsync(job.Id);
}
await File.WriteAllTextAsync(Path.Combine(rollbackState, "downloads.json"), staleCheckpoint);
await using (var engine = new DownloadEngine(rollbackState))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); Check((await engine.SnapshotAsync()).Single().ReceivedBytes == durableBefore, "uncommitted_bytes_claimed_after_restart"); await engine.StartAsync(rollbackId); await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed); await AssertFileAsync((await engine.SnapshotAsync()).Single()); passed++;
}
var invalidMapState = Path.Combine(root, "multipart-map-state"); string invalidMapId;
await using (var engine = new DownloadEngine(invalidMapState))
{
    engine.Configure(new TransferSettings { MaxConnections = 4 }); var job = await engine.AddAsync(server.Url("multi-map"), folder, "multi-map.bin"); invalidMapId = job.Id; await Until(async () => (await engine.SnapshotAsync()).Single().ReceivedBytes >= 262144); await engine.PauseAsync(job.Id);
}
var mapPath = Path.Combine(invalidMapState, "downloads.json"); var invalidMap = JsonNode.Parse(await File.ReadAllTextAsync(mapPath))!; invalidMap[0]!["Segments"]![0]!["DurableBytes"] = server.CurrentData.Length + 1; await File.WriteAllTextAsync(mapPath, invalidMap.ToJsonString());
await using (var engine = new DownloadEngine(invalidMapState))
{
    engine.Configure(new TransferSettings { MaxConnections = 4 }); await engine.StartAsync(invalidMapId); await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed); await AssertFileAsync((await engine.SnapshotAsync()).Single()); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-weak-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); var job = await engine.AddAsync(server.Url("multi-weak"), folder, "multi-weak.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State == DownloadState.Completed); var done = (await engine.SnapshotAsync()).Single(); Check(done.Segments.Count == 0 && !done.ResumeSupported, "weak_validator_used_for_parts"); await AssertFileAsync(done); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "empty-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); var job = await engine.AddAsync(server.Url("empty"), folder, "empty.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Completed && done.Progress == 100 && new FileInfo(done.FinalPath).Length == 0, "empty_file_failed"); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "unknown-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); var job = await engine.AddAsync(server.Url("unknown"), folder, "unknown.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Completed && done.Segments.Count == 0, "unknown_length_failed"); await AssertFileAsync(done); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-short-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 4 }); var job = await engine.AddAsync(server.Url("multi-short"), folder, "multi-short.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Failed && !File.Exists(done.FinalPath) && done.ReceivedBytes < done.TotalBytes, "truncated_segment_finalized"); passed++;
}
server.ResizeData(8 * 1024 * 1024);
await using (var engine = new DownloadEngine(Path.Combine(root, "multipart-32-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 32 }); var job = await engine.AddAsync(server.Url("multi-32"), folder, "multi-32.bin"); await Until(async () => (await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed); var done = (await engine.SnapshotAsync()).Single(); Check(done.State == DownloadState.Completed && done.Segments.Count == 32 && done.ConnectionLimit == 32, "32_connection_plan_failed"); await AssertFileAsync(done); passed++;
}
await using (var engine = new DownloadEngine(Path.Combine(root, "preview-state")))
{
    engine.Configure(new TransferSettings { MaxConnections = 8 }); var job = await engine.AddAsync(server.Url("multi-preview"), folder, "parallel-validation.bin"); var captured = false; var samples = new List<double>(); var started = System.Diagnostics.Stopwatch.StartNew();
    while (true)
    {
        var snapshot = (await engine.SnapshotAsync()).Single();
        if (snapshot.State == DownloadState.Failed) throw new InvalidOperationException("preview_transfer_failed:" + snapshot.ErrorKey);
        if (snapshot.State == DownloadState.Completed) { await AssertFileAsync(snapshot); break; }
        samples.Add(snapshot.BytesPerSecond);
        if (!captured && started.Elapsed >= TimeSpan.FromSeconds(1) && snapshot.ActiveConnections == 8 && snapshot.BytesPerSecond > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "captured-progress.json"), System.Text.Json.JsonSerializer.Serialize(snapshot));
            await File.WriteAllTextAsync(Path.Combine(root, "captured-speed-samples.json"), System.Text.Json.JsonSerializer.Serialize(samples)); captured = true;
        }
        if (started.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("preview_capture_timeout"); await Task.Delay(100);
    }
    Check(captured, "eight_live_connections_not_captured"); passed++;
}
if (args.Length > 1)
{
    var executable = Path.GetFullPath(args[1]); var appData = Path.Combine(root, "packaged-app-state"); var appFiles = Path.Combine(root, "packaged-app-files"); var report = Path.Combine(root, "packaged-app-result.json");
    var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "--headless", "--url", server.Url("multi-packaged"), "--directory", appFiles, "--filename", "packaged.bin", "--data-dir", appData, "--report", report }) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("packaged_app_did_not_start");
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token); Check(process.ExitCode == 0, "packaged_app_failed");
        var result = System.Text.Json.JsonSerializer.Deserialize<DownloadJob>(await File.ReadAllTextAsync(report))!; Check(result.State == DownloadState.Completed && result.Segments.Count == 8, "packaged_app_not_using_default_parallelism"); await AssertFileAsync(result); passed++;
    }
    finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
}
await server.DisposeAsync();
Check(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == testPort && endpoint.Address.Equals(IPAddress.Loopback)), "test_listener_left_open");
Console.WriteLine($"PASS: no TCP listener remains on 127.0.0.1:{testPort} after fixture disposal (OS endpoint audit).");
Console.WriteLine($"PASS: {passed} transfer integration scenarios; SHA-256 verified; pause, restart and range semantics verified.");

static void Check(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); }
static async Task Until(Func<Task<bool>> condition)
{
    var end = DateTimeOffset.UtcNow.AddSeconds(20);
    while (!await condition())
    {
        if (DateTimeOffset.UtcNow > end) throw new TimeoutException("test_timeout");
        await Task.Delay(25);
    }
}

sealed class FixtureServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private Task? _accept;
    private bool _disposed;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    private byte[] _data = Enumerable.Range(0, 4 * 1024 * 1024).Select(number => (byte)(number % 251)).ToArray();
    private int _flakyRequests;
    private int _partChanged;
    private int _version = 1;
    private readonly object _versionGate = new();
    private readonly ConcurrentDictionary<string, int> _activeByPath = [];
    public ConcurrentDictionary<string, int> MaximumByPath { get; } = [];
    public ConcurrentQueue<(string Path, long From, long To)> Ranges { get; } = [];
    public ConcurrentQueue<string> UserAgents { get; } = [];
    private string _tag = "\"fixture-v1\"";
    public ConcurrentQueue<(string Path, long Offset)> Requests { get; } = [];
    public byte[] CurrentData => _data;
    public Task StartAsync()
    {
        _listener.Start();
        _accept = AcceptAsync();
        return Task.CompletedTask;
    }
    public string Url(string path) => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/" + path;
    public void ResizeData(int size) { lock (_versionGate) { _data = Enumerable.Range(0, size).Select(value => (byte)(value % 251)).ToArray(); _tag = "\"fixture-v" + (++_version) + "\""; } }
    public void ChangeVersion() { lock (_versionGate) { _data = _data.Select(value => (byte)(value ^ 0x55)).ToArray(); _tag = "\"fixture-v" + (++_version) + "\""; } }
    private async Task AcceptAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try { var socket = await _listener.AcceptTcpClientAsync(_cancellation.Token); _clients.Add(HandleAsync(socket)); }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task HandleAsync(TcpClient socket)
    {
        using (socket)
        {
            string? countedPath = null;
            try
            {
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var first = await reader.ReadLineAsync(_cancellation.Token) ?? "";
                var path = first.Split(' ').ElementAtOrDefault(1)?.TrimStart('/') ?? "";
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(_cancellation.Token) is { Length: > 0 } line)
                {
                    var split = line.IndexOf(':');
                    if (split > 0) headers[line[..split]] = line[(split + 1)..].Trim();
                }
                var offset = headers.TryGetValue("Range", out var range) ? long.Parse(range[6..].Split('-')[0]) : 0;
                Requests.Enqueue((path, offset)); UserAgents.Enqueue(headers.GetValueOrDefault("User-Agent") ?? "");
                if (path == "unavailable" || path == "flaky" && Interlocked.Increment(ref _flakyRequests) == 1)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _cancellation.Token); return;
                }
                if (path == "multi-change" && headers.ContainsKey("If-Range") && Interlocked.CompareExchange(ref _partChanged, 1, 0) == 0) ChangeVersion();
                byte[] data; string tag; lock (_versionGate) { data = _data; tag = _tag; }
                if (path == "empty") data = [];
                var supported = path is not ("no-range" or "unknown");
                var hasRange = headers.ContainsKey("Range");
                var useRange = supported && hasRange;
                if (path == "multi-ignore" && headers.ContainsKey("If-Range")) useRange = false;
                if (useRange && path is not ("changed" or "multi-change") && headers.TryGetValue("If-Range", out var validator) && validator != tag) useRange = false;
                if (!useRange) offset = 0;
                var rangeEnd = data.Length - 1L;
                if (useRange && long.TryParse(headers["Range"][6..].Split('-').ElementAtOrDefault(1), out var requestedEnd)) rangeEnd = Math.Min(rangeEnd, requestedEnd);
                if (useRange) Ranges.Enqueue((path, offset, rangeEnd));
                if (headers.ContainsKey("If-Range")) { countedPath = path; var active = _activeByPath.AddOrUpdate(path, 1, (_, value) => value + 1); MaximumByPath.AddOrUpdate(path, active, (_, value) => Math.Max(active, value)); }
                if (useRange && offset >= data.Length)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{data.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    return;
                }
                var from = useRange && (path == "bad-range" || path == "multi-bad" && headers.ContainsKey("If-Range")) ? offset + 1 : offset;
                var response = $"HTTP/1.1 {(useRange ? "206 Partial Content" : "200 OK")}\r\nContent-Length: {rangeEnd - offset + 1}\r\nETag: {(path == "multi-weak" ? "W/" + tag : tag)}\r\nConnection: close\r\n";
                if (path == "unknown") response = response.Replace($"Content-Length: {rangeEnd - offset + 1}\r\n", "");
                if (supported) response += "Accept-Ranges: bytes\r\n";
                if (useRange) response += $"Content-Range: bytes {from}-{rangeEnd}/{data.Length}\r\n";
                response += "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cancellation.Token);
                if (path == "stall") await Task.Delay(3000, _cancellation.Token);
                var last = path == "short" ? data.Length / 2 : path == "multi-short" && headers.ContainsKey("If-Range") ? (int)(offset + (rangeEnd - offset + 1) / 2) : (int)rangeEnd + 1;
                for (var position = (int)offset; position < last; position += 32768)
                {
                    await stream.WriteAsync(data.AsMemory(position, Math.Min(32768, last - position)), _cancellation.Token);
                    await Task.Delay(path == "multi-preview" ? 60 : 8, _cancellation.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
            finally { if (countedPath is { } path) _activeByPath.AddOrUpdate(path, 0, (_, value) => Math.Max(0, value - 1)); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _cancellation.Cancel();
        _listener.Stop();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_clients);
        _cancellation.Dispose();
    }
}
