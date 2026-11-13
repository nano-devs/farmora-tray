namespace FarmoraTray.Services;

/// <summary>
/// Tracks print jobs in flight so an update restart never cuts off a print.
/// </summary>
public sealed class PrintActivity
{
    private readonly TimeProvider _time;
    private int _inFlight;
    private long _lastFinishedTicks;

    public PrintActivity()
        : this(TimeProvider.System)
    {
    }

    public PrintActivity(TimeProvider time)
    {
        _time = time;
        _lastFinishedTicks = DateTimeOffset.MinValue.UtcTicks;
    }

    public int InFlight => Volatile.Read(ref _inFlight);

    public DateTimeOffset LastFinished =>
        new(Interlocked.Read(ref _lastFinishedTicks), TimeSpan.Zero);

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _inFlight);
        return new Scope(this);
    }

    /// <summary>
    /// True when no print is running and none finished within <paramref name="quietPeriod"/>.
    /// </summary>
    public bool IsIdle(TimeSpan quietPeriod)
    {
        if (InFlight > 0)
        {
            return false;
        }

        return _time.GetUtcNow() - LastFinished >= quietPeriod;
    }

    private void End()
    {
        Interlocked.Exchange(ref _lastFinishedTicks, _time.GetUtcNow().UtcTicks);
        Interlocked.Decrement(ref _inFlight);
    }

    private sealed class Scope(PrintActivity owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End();
            }
        }
    }
}
