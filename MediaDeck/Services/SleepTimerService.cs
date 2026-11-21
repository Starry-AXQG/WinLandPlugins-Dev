namespace MediaDeck.Services;

/// <summary>
/// 定时暂停服务：倒计时结束后自动暂停媒体播放。
/// </summary>
public sealed class SleepTimerService
{
    private DateTimeOffset? _endTime;

    public bool IsActive => _endTime.HasValue && Remaining > TimeSpan.Zero;

    public TimeSpan Remaining => _endTime.HasValue
        ? (_endTime.Value > DateTimeOffset.UtcNow ? _endTime.Value - DateTimeOffset.UtcNow : TimeSpan.Zero)
        : TimeSpan.Zero;

    public event Action? StateChanged;
    public event Action? Expired;

    public void Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            Cancel();
            return;
        }
        _endTime = DateTimeOffset.UtcNow + duration;
        StateChanged?.Invoke();
    }

    public void Cancel()
    {
        if (!_endTime.HasValue) return;
        _endTime = null;
        StateChanged?.Invoke();
    }

    /// <summary>由插件主定时器每秒调用一次。</summary>
    public void Tick()
    {
        if (!_endTime.HasValue) return;

        if (DateTimeOffset.UtcNow >= _endTime.Value)
        {
            _endTime = null;
            StateChanged?.Invoke();
            Expired?.Invoke();
        }
        else
        {
            StateChanged?.Invoke();
        }
    }
}
