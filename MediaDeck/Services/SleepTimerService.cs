namespace MediaDeck.Services;

/// <summary>
/// 分实例定时暂停服务：允许为每个媒体播放实例独立设置倒计时，到期后仅作用于对应实例。
/// </summary>
public sealed class SleepTimerService
{
    private readonly Dictionary<string, DateTimeOffset> _timers = new(StringComparer.OrdinalIgnoreCase);

    public bool HasAnyActive => _timers.Count > 0;

    public bool IsActive(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        return _timers.TryGetValue(instanceId, out var end) && end > DateTimeOffset.UtcNow;
    }

    public TimeSpan GetRemaining(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return TimeSpan.Zero;
        if (!_timers.TryGetValue(instanceId, out var end)) return TimeSpan.Zero;
        var rem = end - DateTimeOffset.UtcNow;
        return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
    }

    public event Action? StateChanged;
    public event Action<string>? Expired;

    public void Start(string instanceId, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return;
        if (duration <= TimeSpan.Zero)
        {
            Cancel(instanceId);
            return;
        }
        _timers[instanceId] = DateTimeOffset.UtcNow + duration;
        StateChanged?.Invoke();
    }

    public void Cancel(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return;
        if (!_timers.Remove(instanceId)) return;
        StateChanged?.Invoke();
    }

    /// <summary>由插件主定时器每秒调用一次。</summary>
    public void Tick()
    {
        if (_timers.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        List<string>? expiredIds = null;

        foreach (var kv in _timers)
        {
            if (now >= kv.Value)
            {
                expiredIds ??= [];
                expiredIds.Add(kv.Key);
            }
        }

        if (expiredIds is not null)
        {
            foreach (var id in expiredIds)
            {
                _timers.Remove(id);
            }
        }

        StateChanged?.Invoke();

        if (expiredIds is not null)
        {
            foreach (var id in expiredIds)
            {
                Expired?.Invoke(id);
            }
        }
    }
}
