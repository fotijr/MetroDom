using MetroDom.Core.Policy;

namespace MetroDom.Core.Tests;

public class CurfewTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, Offset);

    private static TimeSpan Minutes(double m) => TimeSpan.FromMinutes(m);

    private static readonly Curfew Nightly = new(new TimeOnly(23, 0), new TimeOnly(10, 0), null, Minutes(5));

    [Fact]
    public void Nightly_IsInForceAcrossMidnight_AndLiftsInTheMorning()
    {
        Assert.False(Nightly.InForce(At(13, 22, 59)));
        Assert.True(Nightly.InForce(At(13, 23, 0)));
        Assert.True(Nightly.InForce(At(14, 9, 59)));
        Assert.False(Nightly.InForce(At(14, 10, 0)));
        Assert.Equal(At(13, 23, 0), Nightly.NextStop(At(13, 20, 0)));
        Assert.Equal(At(14, 23, 0), Nightly.NextStop(At(14, 10, 0)));
        Assert.Equal(At(14, 10, 0), Nightly.LiftsAt(At(13, 23, 30)));
        Assert.Null(Nightly.LiftsAt(At(13, 20, 0)));
        Assert.Equal("Curfew until 10:00.", Nightly.Reason(At(14, 1, 0)));
        Assert.Null(Nightly.Reason(At(13, 20, 0)));
    }

    [Fact]
    public void ASong_StartsOnlyIfItCanFinish_WithinTheGrace()
    {
        var fourMinutes = Minutes(4);
        Assert.True(Nightly.Fits(At(13, 22, 50), fourMinutes));
        Assert.True(Nightly.Fits(At(13, 22, 59), fourMinutes)); // ends 23:03, inside 5 min grace
        Assert.False(Nightly.Fits(At(13, 22, 59), Minutes(7)));  // ends 23:06
        Assert.False(Nightly.Fits(At(13, 23, 1), Minutes(1)));   // in force
        Assert.Equal(At(13, 23, 5), Nightly.Deadline(At(13, 22, 58)));
    }

    [Fact]
    public void EndsAt_IsForever_AndWinsWhenItComesFirst()
    {
        var rental = Nightly with { EndsAt = At(13, 22, 0) };
        Assert.Equal(At(13, 22, 0), rental.NextStop(At(13, 21, 0)));
        Assert.True(rental.InForce(At(13, 22, 0)));
        Assert.True(rental.InForce(At(14, 12, 0))); // the next morning, still over
        Assert.Null(rental.LiftsAt(At(13, 23, 30)));
        Assert.Null(rental.NextStop(At(14, 12, 0)));
        Assert.Equal("The booking ended at 22:00.", rental.Reason(At(13, 22, 30)));

        var endOnly = Curfew.None with { EndsAt = At(14, 1, 0), Grace = TimeSpan.Zero };
        Assert.True(endOnly.Fits(At(14, 0, 50), Minutes(10)));
        Assert.False(endOnly.Fits(At(14, 0, 51), Minutes(10)));
        Assert.Equal(At(14, 1, 0), endOnly.Deadline(At(14, 0, 0)));
        Assert.Equal("ends 2026-11-14 01:00 · 0 min grace", endOnly.Describe());
    }

    [Fact]
    public void ACurfewLiftingAfterTheBookingEnds_NeverLifts()
    {
        var rental = Nightly with { EndsAt = At(14, 2, 0) };
        Assert.Null(rental.LiftsAt(At(13, 23, 30)));
        Assert.Equal("Curfew.", rental.Reason(At(13, 23, 30)));
    }

    [Fact]
    public void None_NeverStopsAnything()
    {
        Assert.False(Curfew.None.IsSet);
        Assert.True(Curfew.None.Fits(At(13, 23, 59), TimeSpan.FromHours(3)));
        Assert.Null(Curfew.None.Deadline(At(13, 23, 59)));
        Assert.Null(Curfew.None.Describe());
        Assert.Equal("curfew 23:00-10:00 · 5 min grace", Nightly.Describe());
    }

    [Fact]
    public void Tonight_AddsAStop_UntilLifted_AndNeverExtendsThePolicy()
    {
        var tonight = Nightly.Tonight(At(13, 21, 0), new TimeOnly(22, 30));
        Assert.Equal(At(13, 22, 30), tonight.StopAt);
        Assert.Equal(At(13, 22, 30), tonight.NextStop(At(13, 21, 0)));
        Assert.True(tonight.InForce(At(13, 22, 30)));
        Assert.True(tonight.InForce(At(14, 12, 0))); // past the morning: still, until the owner lifts it
        Assert.Null(tonight.LiftsAt(At(13, 23, 30)));
        Assert.Equal("Curfew since 22:30, until the owner lifts it.", tonight.Reason(At(13, 22, 45)));
        Assert.Equal("curfew 23:00-10:00 · tonight's curfew 22:30 · 5 min grace", tonight.Describe());
        Assert.Equal(Nightly, tonight with { StopAt = null });

        // Later than the policy's curfew: the policy's still comes first.
        var late = Nightly.Tonight(At(13, 21, 0), new TimeOnly(23, 30));
        Assert.Equal(At(13, 23, 0), late.NextStop(At(13, 21, 0)));

        // Set after midnight's time has passed: the next time the clock reads it.
        Assert.Equal(At(14, 1, 0), Curfew.None.Tonight(At(13, 21, 0), new TimeOnly(1, 0)).StopAt);
    }
}
