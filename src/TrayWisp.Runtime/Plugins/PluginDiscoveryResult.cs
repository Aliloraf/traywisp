using System.Collections.Immutable;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Runtime.Plugins;

/// <summary>Outcome of <see cref="PluginDiscovery.Discover"/>.</summary>
public sealed record PluginDiscoveryResult
{
    internal PluginDiscoveryResult(
        string rootPath,
        ImmutableArray<PluginDescriptor> plugins,
        ImmutableArray<PluginDiagnostic> diagnostics,
        bool isComplete,
        int entriesInspected)
    {
        RootPath = rootPath;
        Plugins = plugins;
        Diagnostics = diagnostics;
        IsComplete = isComplete;
        EntriesInspected = entriesInspected;
    }

    /// <summary>The fully qualified root directory that was scanned.</summary>
    public string RootPath { get; }

    /// <summary>Discovered plugins, ordered by <see cref="PluginId"/>.</summary>
    public ImmutableArray<PluginDescriptor> Plugins { get; }

    /// <summary>
    /// Discovery-level diagnostics in traversal order: root problems, rejected file names, unreadable
    /// files and directories, skipped reparse points, limits and identity collisions. Metadata
    /// diagnostics of discovered plugins are in <see cref="PluginDescriptor.Diagnostics"/>.
    /// </summary>
    public ImmutableArray<PluginDiagnostic> Diagnostics { get; }

    /// <summary>
    /// <see langword="false"/> if some part of the tree was not examined or its state is unknown: the root
    /// could not be scanned, a directory could not be listed, a candidate file could not be read or
    /// disappeared, an entry changed during the scan, or a traversal limit was reached. Entries skipped on
    /// purpose (hidden, dot-prefixed, reparse points, remote storage, unrelated files, identity collisions)
    /// do not make a result incomplete.
    /// </summary>
    /// <remarks>
    /// A plugin missing from an incomplete result may still exist. Consumers must not treat its absence
    /// as removal (for example to cancel its schedule or forget it); only a complete result shows that a
    /// plugin is gone. A directory whose listing was cut off by the entry limit contributes no plugins and
    /// no subdirectories, because collisions among its entries could not be ruled out.
    /// </remarks>
    public bool IsComplete { get; }

    /// <summary>
    /// Number of directory entries enumerated, counted against <see cref="PluginDiscoveryOptions.MaxEntries"/>
    /// and never larger than it. It includes ignored entries, entries of listings that were later discarded
    /// (truncated, or changed during the scan), and entries not yet processed when a limit stopped
    /// discovery. To detect that the limit is exceeded, discovery enumerates one further entry, at most
    /// once per scan; that look-ahead entry is not counted.
    /// </summary>
    public int EntriesInspected { get; }
}
