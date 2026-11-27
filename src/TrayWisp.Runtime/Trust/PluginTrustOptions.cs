namespace TrayWisp.Runtime.Trust;

/// <summary>Settings for <see cref="PluginTrustService"/>.</summary>
public sealed record PluginTrustOptions
{
    /// <summary>The default settings.</summary>
    public static PluginTrustOptions Default { get; } = new();

    /// <summary>Largest accepted <see cref="LockTimeout"/>: 5 minutes.</summary>
    public static TimeSpan MaxLockTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an operation waits for another TrayWisp operation (in this or another process) to release the
    /// trust store lock before failing with <c>TW545</c>. Default 10 seconds; zero tries once. At most
    /// <see cref="MaxLockTimeout"/>.
    /// </summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
