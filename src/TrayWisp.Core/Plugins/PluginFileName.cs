namespace TrayWisp.Core.Plugins;

/// <summary>
/// A plugin file name that follows the <c>name[.interval].extension</c> convention.
/// Created only by <see cref="PluginFileNameParser"/>.
/// </summary>
public sealed record PluginFileName
{
    internal PluginFileName(string fileName, string name, RefreshInterval? refreshInterval, string extension, PluginLanguage language)
    {
        FileName = fileName;
        Name = name;
        RefreshInterval = refreshInterval;
        Extension = extension;
        Language = language;
    }

    /// <summary>The complete file name as given, for example <c>CLOCK.5S.PS1</c>.</summary>
    public string FileName { get; }

    /// <summary>The plugin name: everything before the interval and extension, for example <c>github.notifications</c>.</summary>
    public string Name { get; }

    /// <summary>The refresh interval, or <see langword="null"/> when the plugin is refreshed manually.</summary>
    public RefreshInterval? RefreshInterval { get; }

    /// <summary>The extension without the leading dot, with its original casing, for example <c>PS1</c>.</summary>
    public string Extension { get; }

    /// <summary>The language implied by the extension.</summary>
    public PluginLanguage Language { get; }

    /// <summary>Whether execution of this language is planned or deferred.</summary>
    public PluginExecutionSupport ExecutionSupport => PluginLanguages.GetExecutionSupport(Language);

    /// <summary><see langword="true"/> when the file name has no interval.</summary>
    public bool IsManualRefresh => RefreshInterval is null;
}
