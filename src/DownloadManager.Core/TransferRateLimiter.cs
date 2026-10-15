using System.Diagnostics;

namespace DownloadManager.Core;

// Shared by every range of one file. Tokens are granted BEFORE reading network payload.
internal sealed class TransferRateLimiter(Func<long> selectedRate) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _rate = -1;
    private double _tokens;
    private long _sampledAt = Stopwatch.GetTimestamp();
    public async Task<int> AcquireAsync(int requested, CancellationToken cancellation)
    {
        while (true)
        {
            TimeSpan wait;
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var rate = selectedRate();
                if (rate <= 0) { _rate = 0; _sampledAt = Stopwatch.GetTimestamp(); return requested; }
                var capacity = Math.Clamp(rate / 10d, 1024, 1048576); var now = Stopwatch.GetTimestamp();
                if (rate != _rate) { _tokens = _rate <= 0 ? capacity : Math.Min(capacity, _tokens); _rate = rate; }
                else _tokens = Math.Min(capacity, _tokens + Stopwatch.GetElapsedTime(_sampledAt, now).TotalSeconds * rate);
                _sampledAt = now;
                if (_tokens >= Math.Min(requested, 1024)) { var count = Math.Min(requested, (int)Math.Floor(_tokens)); _tokens -= count; return count; }
                var target = Math.Min(requested, capacity); wait = TimeSpan.FromSeconds(Math.Max(0.001, (target - _tokens) / rate));
            }
            finally { _gate.Release(); }
            await Task.Delay(wait > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : wait, cancellation).ConfigureAwait(false);
        }
    }
    public async Task RefundAsync(int bytes)
    {
        if (bytes <= 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_rate > 0) _tokens = Math.Min(Math.Clamp(_rate / 10d, 1024, 1048576), _tokens + bytes); }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}
