using System.Diagnostics.CodeAnalysis;

namespace TrayWisp.Core.Plugins;

/// <summary>
/// Descriptive metadata embedded in a plugin file. Every field is optional, author-provided and
/// untrusted: values are inert text and are never fetched, evaluated or validated as URLs here.
/// </summary>
public sealed record PluginMetadata
{
    /// <summary>Metadata with no fields set.</summary>
    public static PluginMetadata Empty { get; } = new();

    /// <summary><c>title</c>: the plugin's display title.</summary>
    public string? Title { get; init; }

    /// <summary><c>version</c>: the author's version string, preserved as written (not necessarily semantic versioning).</summary>
    public string? Version { get; init; }

    /// <summary><c>author</c>: by convention a comma-separated list of authors, kept as written.</summary>
    public string? Author { get; init; }

    /// <summary><c>author.github</c>: by convention a comma-separated list of GitHub user names, kept as written.</summary>
    public string? AuthorGitHub { get; init; }

    /// <summary><c>desc</c>: a short description.</summary>
    public string? Description { get; init; }

    /// <summary><c>dependencies</c>: by convention a comma-separated list of dependencies, kept as written.</summary>
    public string? Dependencies { get; init; }

    /// <summary><c>abouturl</c>: text the author intends as a URL. Not validated and never fetched.</summary>
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Untrusted, unvalidated metadata text; parsing it as a Uri is left to consumers that apply a scheme allow-list.")]
    public string? AboutUrl { get; init; }

    /// <summary><c>image</c>: text the author intends as an image URL. Not validated and never fetched.</summary>
    public string? Image { get; init; }
}
