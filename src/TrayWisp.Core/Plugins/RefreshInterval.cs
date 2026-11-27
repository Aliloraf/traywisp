using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TrayWisp.Core.Plugins;

/// <summary>Unit of a <see cref="RefreshInterval"/>, as written in a plugin file name.</summary>
public enum RefreshIntervalUnit
{
    /// <summary><c>ms</c></summary>
    Milliseconds,

    /// <summary><c>s</c></summary>
    Seconds,

    /// <summary><c>m</c></summary>
    Minutes,

    /// <summary><c>h</c></summary>
    Hours,

    /// <summary><c>d</c></summary>
    Days,
}

/// <summary>
/// A validated refresh interval taken from a plugin file name, such as <c>5s</c> in <c>clock.5s.ps1</c>.
/// The value is a positive integer and the duration lies between <see cref="Minimum"/> and
/// <see cref="Maximum"/>, inclusive. Instances can only be created through <see cref="TryCreate"/>.
/// </summary>
public sealed record RefreshInterval
{
    private const long MinimumMilliseconds = 250;
    private const long MaximumMilliseconds = 30L * 24 * 60 * 60 * 1000;

    private RefreshInterval(long value, RefreshIntervalUnit unit, long milliseconds)
    {
        Value = value;
        Unit = unit;
        Duration = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>The shortest allowed interval: 250 milliseconds.</summary>
    public static TimeSpan Minimum { get; } = TimeSpan.FromTicks(MinimumMilliseconds * TimeSpan.TicksPerMillisecond);

    /// <summary>The longest allowed interval: 30 days.</summary>
    public static TimeSpan Maximum { get; } = TimeSpan.FromTicks(MaximumMilliseconds * TimeSpan.TicksPerMillisecond);

    /// <summary>The number as written, for example 5 in <c>5s</c>.</summary>
    public long Value { get; }

    /// <summary>The unit as written, for example seconds in <c>5s</c>.</summary>
    public RefreshIntervalUnit Unit { get; }

    /// <summary>The interval as a duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Creates an interval if <paramref name="value"/> is positive and the duration is within range.</summary>
    public static bool TryCreate(long value, RefreshIntervalUnit unit, [NotNullWhen(true)] out RefreshInterval? interval)
    {
        interval = null;
        long unitMilliseconds = unit switch
        {
            RefreshIntervalUnit.Milliseconds => 1,
            RefreshIntervalUnit.Seconds => 1000,
            RefreshIntervalUnit.Minutes => 60 * 1000,
            RefreshIntervalUnit.Hours => 60 * 60 * 1000,
            RefreshIntervalUnit.Days => 24 * 60 * 60 * 1000,
            _ => 0,
        };

        // Dividing first avoids overflow for any long value.
        if (value <= 0 || unitMilliseconds == 0 || value > MaximumMilliseconds / unitMilliseconds)
        {
            return false;
        }

        long milliseconds = value * unitMilliseconds;
        if (milliseconds < MinimumMilliseconds)
        {
            return false;
        }

        interval = new RefreshInterval(value, unit, milliseconds);
        return true;
    }

    /// <summary>Returns the canonical lower-case form, for example <c>5s</c> or <c>500ms</c>.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + UnitSuffix(Unit);

    internal static string UnitSuffix(RefreshIntervalUnit unit) => unit switch
    {
        RefreshIntervalUnit.Milliseconds => "ms",
        RefreshIntervalUnit.Seconds => "s",
        RefreshIntervalUnit.Minutes => "m",
        RefreshIntervalUnit.Hours => "h",
        RefreshIntervalUnit.Days => "d",
        _ => "?",
    };
}
