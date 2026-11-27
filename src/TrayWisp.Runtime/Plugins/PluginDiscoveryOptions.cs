namespace TrayWisp.Runtime.Plugins;

/// <summary>Traversal limits for <see cref="PluginDiscovery"/>. All limits are inclusive.</summary>
public sealed record PluginDiscoveryOptions
{
    /// <summary>The default limits.</summary>
    public static PluginDiscoveryOptions Default { get; } = new();

    /// <summary>Largest accepted <see cref="MaxDepth"/>. It bounds recursion, so deep trees cannot exhaust the stack.</summary>
    public const int MaxDepthLimit = 64;

    /// <summary>
    /// Deepest directory level that is scanned. The root is level 0, so 0 scans only files directly in
    /// the root. Default 8, at most <see cref="MaxDepthLimit"/>.
    /// </summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>
    /// Maximum number of directory entries (files and directories, including ignored ones) enumerated
    /// across the whole scan, counted as each directory is listed. A directory whose listing does not
    /// fit in the remaining budget is discarded as a whole, and no further directories are listed; see
    /// <see cref="PluginDiscoveryResult.EntriesInspected"/>. Default 10,000.
    /// </summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>
    /// Maximum number of candidate plugin files (recognized extension and valid file name) read before
    /// discovery stops. Default 500.
    /// </summary>
    public int MaxCandidates { get; init; } = 500;
}
