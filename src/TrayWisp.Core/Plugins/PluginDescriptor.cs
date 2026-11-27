using System.Collections.Immutable;

namespace TrayWisp.Core.Plugins;

/// <summary>
/// Everything known about a discovered plugin without running it: its identity, the information in
/// its file name and its embedded metadata. A descriptor does not imply that the plugin is trusted
/// or that it can be executed.
/// </summary>
public sealed record PluginDescriptor
{
    /// <summary>Creates a descriptor.</summary>
    /// <exception cref="ArgumentException">The identity's file name differs from <paramref name="fileName"/>.</exception>
    public PluginDescriptor(PluginId id, PluginFileName fileName, PluginMetadata metadata, ImmutableArray<PluginDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!string.Equals(id.FileName, fileName.FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The plugin identity must end with the parsed file name.", nameof(fileName));
        }

        Id = id;
        FileName = fileName;
        Metadata = metadata;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>The root-relative identity.</summary>
    public PluginId Id { get; }

    /// <summary>Name, refresh interval and language parsed from the file name.</summary>
    public PluginFileName FileName { get; }

    /// <summary>Embedded metadata. <see cref="PluginMetadata.Empty"/> when the file has none.</summary>
    public PluginMetadata Metadata { get; }

    /// <summary>Metadata diagnostics for this plugin, with <see cref="PluginDiagnostic.Path"/> set by discovery.</summary>
    public ImmutableArray<PluginDiagnostic> Diagnostics { get; }

    /// <summary>The metadata title if present, otherwise the plugin name from the file name.</summary>
    public string DisplayTitle => Metadata.Title ?? FileName.Name;
}
