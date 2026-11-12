namespace SpeechToText.Tests;

public class TriggerTests
{
    [Theory]
    [InlineData(true, TriggerSource.OnlyMe, true)]     // my own write, strict policy: counts
    [InlineData(true, TriggerSource.Anyone, true)]
    [InlineData(false, TriggerSource.OnlyMe, false)]   // someone else's write, strict policy: rejected
    [InlineData(false, TriggerSource.Anyone, true)]    // someone else's write, open policy: counts
    public void PolicyTable(bool modifiedByLocal, TriggerSource source, bool expected) =>
        Assert.Equal(expected, TriggerPolicy.Accept(modifiedByLocal, source));

    [Fact]
    public void DefaultSettingIsTheStrictOne() => Assert.Equal(TriggerSource.OnlyMe, default(TriggerSource));

    private sealed class Clock { public long Now; }

    [Fact]
    public void AutoOffNeverFiresWhenDisabled()
    {
        var c = new Clock();
        var a = new TriggerAutoOff(() => c.Now);
        Assert.False(a.ShouldSwitchOff(true, 0));
        c.Now += 3_600_000;
        Assert.False(a.ShouldSwitchOff(true, 0));
        Assert.False(a.ShouldSwitchOff(true, -5));
    }

    [Fact]
    public void AutoOffFiresOnlyOnceTheLimitHasPassedAndKeepsFiringUntilClosed()
    {
        var c = new Clock();
        var a = new TriggerAutoOff(() => c.Now);
        Assert.False(a.ShouldSwitchOff(true, 10));       // just opened
        c.Now += 9_999;
        Assert.False(a.ShouldSwitchOff(true, 10));       // 1 ms short
        c.Now += 1;
        Assert.True(a.ShouldSwitchOff(true, 10));        // at the limit
        c.Now += 250;
        Assert.True(a.ShouldSwitchOff(true, 10));        // the write didn't take: ask again
        Assert.False(a.ShouldSwitchOff(false, 10));      // it closed
    }

    [Fact]
    public void ClosingRestartsTheClock()
    {
        var c = new Clock();
        var a = new TriggerAutoOff(() => c.Now);
        a.ShouldSwitchOff(true, 10);
        c.Now += 8_000;
        a.ShouldSwitchOff(false, 10);                    // released after 8 s
        c.Now += 5_000;
        Assert.False(a.ShouldSwitchOff(true, 10));       // reopened: starts from zero, not from the earlier 8 s
        c.Now += 9_000;
        Assert.False(a.ShouldSwitchOff(true, 10));
        c.Now += 1_000;
        Assert.True(a.ShouldSwitchOff(true, 10));
    }

    [Fact]
    public void LoweringTheLimitWhileOpenAppliesImmediately()
    {
        var c = new Clock();
        var a = new TriggerAutoOff(() => c.Now);
        a.ShouldSwitchOff(true, 60);
        c.Now += 20_000;
        Assert.False(a.ShouldSwitchOff(true, 60));
        Assert.True(a.ShouldSwitchOff(true, 15));        // already open 20 s, new limit 15 s
    }

    [Fact]
    public void OpenForReportsTheRunningTime()
    {
        var c = new Clock();
        var a = new TriggerAutoOff(() => c.Now);
        Assert.Equal(TimeSpan.Zero, a.OpenFor);
        a.ShouldSwitchOff(true, 0);
        c.Now += 2_500;
        Assert.Equal(TimeSpan.FromMilliseconds(2500), a.OpenFor);
        a.ShouldSwitchOff(false, 0);
        Assert.Equal(TimeSpan.Zero, a.OpenFor);
    }
}
