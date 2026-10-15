namespace DownloadManager.Core;

// Monotonic time and aggregate received bytes, independent of connection bursts.
public sealed class RemainingTimeEstimator
{
    private double? _start, _last, _lastProgress;
    private long _bytes;
    private double _rate;
    public void Reset() { _start = _last = _lastProgress = null; _bytes = 0; _rate = 0; }
    public double? Sample(double now, long received, long? total)
    {
        if (_last is null || received < _bytes) { Reset(); _start = _last = _lastProgress = now; _bytes = received; return null; }
        var elapsed = now - _last.Value;
        if (elapsed >= 1)
        {
            var delta = received - _bytes;
            if (delta > 0) _lastProgress = now;
            var rate = delta / elapsed;
            _rate = _rate == 0 ? rate : _rate + (1 - Math.Exp(-elapsed / 8)) * (rate - _rate);
            _last = now; _bytes = received;
        }
        if (total is null || total <= received || now - _start < 3 || now - _lastProgress >= 5 || _rate <= 0) return null;
        return (total.Value - received) / _rate;
    }
}
