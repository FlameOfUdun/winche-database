namespace Winche.Database.AspNetCore.WebSockets.Connections;

/// <summary>
/// A token bucket per connection: <see cref="WaitAsync"/> returns at once while tokens remain and otherwise waits
/// until the next one refills — so a connection over its rate is slowed, not refused.
/// </summary>
internal sealed class RequestThrottle(int perSecond, int burst, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly double _capacity = Math.Max(1, burst);
    private double _tokens = Math.Max(1, burst);
    private long _last = (clock ?? TimeProvider.System).GetTimestamp();

    /// <summary>Waits until a request may proceed (immediately when unlimited).</summary>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        if (perSecond <= 0) return;
        Refill();
        if (_tokens < 1)
        {
            var wait = TimeSpan.FromSeconds((1 - _tokens) / perSecond);
            await Task.Delay(wait, _clock, ct);
            Refill();
        }
        _tokens = Math.Max(0, _tokens - 1);
    }

    private void Refill()
    {
        var now = _clock.GetTimestamp();
        _tokens = Math.Min(_capacity, _tokens + _clock.GetElapsedTime(_last, now).TotalSeconds * perSecond);
        _last = now;
    }
}
