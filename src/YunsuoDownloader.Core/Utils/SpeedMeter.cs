namespace YunsuoDownloader.Core.Utils;

/// <summary>滑动窗口速度计：把"已下载字节数"的增量换算成平滑速度（字节/秒）。</summary>
public sealed class SpeedMeter
{
    private readonly Queue<(DateTime At, long Bytes)> _samples = new();
    private readonly TimeSpan _window;
    private long _lastBytes = -1;
    private DateTime _lastAt = DateTime.UtcNow;
    private double _speed;

    public SpeedMeter(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(3);

    /// <summary>当前速度（字节/秒），已做指数平滑，避免数字乱跳。</summary>
    public double Speed => _speed;

    public void Reset()
    {
        _samples.Clear();
        _lastBytes = -1;
        _lastAt = DateTime.UtcNow;
        _speed = 0;
    }

    /// <summary>喂入最新的累计字节数。</summary>
    public double Sample(long totalBytes)
    {
        var now = DateTime.UtcNow;
        if (_lastBytes < 0)
        {
            _lastBytes = totalBytes;
            _lastAt = now;
            return 0;
        }

        var delta = totalBytes - _lastBytes;
        if (delta < 0) { Reset(); _lastBytes = totalBytes; return 0; }  // 文件被截断 / 重新开始
        _lastBytes = totalBytes;

        var elapsedTotal = (now - _lastAt).TotalSeconds;
        if (elapsedTotal < 0.35) return _speed;   // 采样太密，先不更新
        _lastAt = now;
        _samples.Enqueue((now, totalBytes));

        while (_samples.Count > 1 && now - _samples.Peek().At > _window) _samples.Dequeue();

        if (_samples.Count >= 2)
        {
            var first = _samples.Peek();
            var secs = (now - first.At).TotalSeconds;
            if (secs > 0.2)
            {
                var inst = (totalBytes - first.Bytes) / secs;
                _speed = _speed <= 0 ? inst : _speed * 0.55 + inst * 0.45;
            }
        }
        return _speed;
    }
}

/// <summary>全局令牌桶限速器：所有任务共享一个限速额度（设置里的"全局限速"）。</summary>
public sealed class TokenBucket
{
    private readonly object _lock = new();
    private double _tokens;
    private DateTime _last = DateTime.UtcNow;
    private long _bytesPerSecond;

    public TokenBucket(long bytesPerSecond) => _bytesPerSecond = bytesPerSecond;

    public bool Enabled => _bytesPerSecond > 0;

    public void SetRate(long bytesPerSecond)
    {
        lock (_lock)
        {
            _bytesPerSecond = bytesPerSecond;
            _tokens = 0;
        }
    }

    /// <summary>申请 <paramref name="want"/> 字节的额度，返回实际允许的字节数（可能小于 want）。</summary>
    public int Acquire(int want)
    {
        if (_bytesPerSecond <= 0) return want;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _last).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens = Math.Min(_bytesPerSecond, _tokens + elapsed * _bytesPerSecond);
                _last = now;
            }
            if (_tokens < 1) return 0;
            var allowed = (int)Math.Min(want, Math.Floor(_tokens));
            _tokens -= allowed;
            return allowed;
        }
    }

    /// <summary>按需等待直到拿到额度（返回建议休眠毫秒数，0 表示可继续）。</summary>
    public int DelayMsFor(int want)
    {
        if (_bytesPerSecond <= 0) return 0;
        var allowed = Acquire(want);
        if (allowed >= want) return 0;
        var deficit = want - allowed;
        return (int)Math.Clamp(deficit * 1000.0 / _bytesPerSecond, 10, 1000);
    }
}
