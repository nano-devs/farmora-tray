using FarmoraTray.Services;

namespace FarmoraTray.Tests;

public class PrintActivityTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Idle_WhenNothingHasPrinted()
    {
        var activity = new PrintActivity(new ManualTime());

        Assert.True(activity.IsIdle(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void NotIdle_WhilePrintIsRunning()
    {
        var activity = new PrintActivity(new ManualTime());

        using (activity.Begin())
        {
            Assert.Equal(1, activity.InFlight);
            Assert.False(activity.IsIdle(TimeSpan.Zero));
        }

        Assert.Equal(0, activity.InFlight);
        Assert.True(activity.IsIdle(TimeSpan.Zero));
    }

    [Fact]
    public void NotIdle_UntilQuietPeriodPassesAfterLastPrint()
    {
        var time = new ManualTime();
        var activity = new PrintActivity(time);

        activity.Begin().Dispose();
        time.Now += TimeSpan.FromMinutes(4);
        Assert.False(activity.IsIdle(TimeSpan.FromMinutes(5)));

        time.Now += TimeSpan.FromMinutes(1);
        Assert.True(activity.IsIdle(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void DisposingScopeTwice_CountsOnce()
    {
        var activity = new PrintActivity(new ManualTime());
        using var first = activity.Begin();
        var second = activity.Begin();

        second.Dispose();
        second.Dispose();

        Assert.Equal(1, activity.InFlight);
    }
}
