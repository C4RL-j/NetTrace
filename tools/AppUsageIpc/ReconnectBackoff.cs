namespace AppUsageIpc;

// Only failed reconnect attempts increase the delay. Successful Hello resets it.
public sealed class ReconnectBackoff
{
    private readonly TimeSpan initial, maximum;
    private TimeSpan next;
    public ReconnectBackoff(TimeSpan? initial = null, TimeSpan? maximum = null)
    {
        this.initial = initial ?? TimeSpan.FromSeconds(2);
        this.maximum = maximum ?? TimeSpan.FromSeconds(8);
        if (this.initial <= TimeSpan.Zero || this.maximum < this.initial)
            throw new ArgumentOutOfRangeException(nameof(initial));
        next = this.initial;
    }
    public TimeSpan NextDelay()
    {
        var result = next;
        next = TimeSpan.FromTicks(Math.Min(maximum.Ticks,
            next.Ticks > maximum.Ticks / 2 ? maximum.Ticks : next.Ticks * 2));
        return result;
    }
    public void Reset() => next = initial;
}
