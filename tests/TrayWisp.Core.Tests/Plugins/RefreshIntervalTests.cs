using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Plugins;

public sealed class RefreshIntervalTests
{
    [Fact]
    public void BoundsAreQuarterSecondAndThirtyDays()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), RefreshInterval.Minimum);
        Assert.Equal(TimeSpan.FromDays(30), RefreshInterval.Maximum);
    }

    [Theory]
    [InlineData(250L, RefreshIntervalUnit.Milliseconds, true)]
    [InlineData(249L, RefreshIntervalUnit.Milliseconds, false)]
    [InlineData(1L, RefreshIntervalUnit.Seconds, true)]
    [InlineData(30L, RefreshIntervalUnit.Days, true)]
    [InlineData(31L, RefreshIntervalUnit.Days, false)]
    [InlineData(720L, RefreshIntervalUnit.Hours, true)]
    [InlineData(721L, RefreshIntervalUnit.Hours, false)]
    [InlineData(0L, RefreshIntervalUnit.Seconds, false)]
    [InlineData(-1L, RefreshIntervalUnit.Seconds, false)]
    [InlineData(long.MaxValue, RefreshIntervalUnit.Days, false)]
    [InlineData(long.MinValue, RefreshIntervalUnit.Milliseconds, false)]
    [InlineData(5L, (RefreshIntervalUnit)42, false)]
    public void TryCreateEnforcesRange(long value, RefreshIntervalUnit unit, bool expected)
    {
        bool created = RefreshInterval.TryCreate(value, unit, out RefreshInterval? interval);

        Assert.Equal(expected, created);
        Assert.Equal(expected, interval is not null);
        if (interval is not null)
        {
            Assert.InRange(interval.Duration, RefreshInterval.Minimum, RefreshInterval.Maximum);
        }
    }

    [Theory]
    [InlineData(500L, RefreshIntervalUnit.Milliseconds, "500ms")]
    [InlineData(5L, RefreshIntervalUnit.Seconds, "5s")]
    [InlineData(10L, RefreshIntervalUnit.Minutes, "10m")]
    [InlineData(2L, RefreshIntervalUnit.Hours, "2h")]
    [InlineData(1L, RefreshIntervalUnit.Days, "1d")]
    public void ToStringIsCanonical(long value, RefreshIntervalUnit unit, string expected)
    {
        Assert.True(RefreshInterval.TryCreate(value, unit, out RefreshInterval? interval));
        Assert.Equal(expected, interval.ToString());
    }

    [Fact]
    public void EqualityUsesValueAndUnitAsWritten()
    {
        Assert.True(RefreshInterval.TryCreate(5, RefreshIntervalUnit.Minutes, out RefreshInterval? minutes));
        Assert.True(RefreshInterval.TryCreate(5, RefreshIntervalUnit.Minutes, out RefreshInterval? sameMinutes));
        Assert.True(RefreshInterval.TryCreate(300, RefreshIntervalUnit.Seconds, out RefreshInterval? seconds));

        Assert.Equal(minutes, sameMinutes);
        Assert.NotEqual(minutes, seconds);
        Assert.Equal(minutes.Duration, seconds.Duration);
    }
}
