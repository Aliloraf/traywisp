namespace TrayWisp.Core.Plugins;

/// <summary>Script language of a plugin, determined only from its file extension.</summary>
public enum PluginLanguage
{
    /// <summary>PowerShell, <c>.ps1</c>.</summary>
    PowerShell,

    /// <summary>Python, <c>.py</c>.</summary>
    Python,

    /// <summary>JavaScript, <c>.js</c>.</summary>
    JavaScript,

    /// <summary>Bash or another POSIX shell, <c>.sh</c>.</summary>
    Bash,

    /// <summary>Windows batch, <c>.bat</c> or <c>.cmd</c>.</summary>
    Batch,
}

/// <summary>
/// Whether TrayWisp can run plugins of a language. Support for a language does not mean that its interpreter is
/// installed: interpreter availability is determined separately, for each run (see docs/execution-engine.md).
/// </summary>
public enum PluginExecutionSupport
{
    /// <summary>
    /// Execution is implemented (PowerShell, Python, JavaScript). Discovery is unaffected, and a plugin still runs
    /// only after explicit approval and when an interpreter is available.
    /// </summary>
    Supported,

    /// <summary>Recognized during discovery, but execution support is deferred with no committed design (Bash, batch).</summary>
    Deferred,
}

/// <summary>Maps file extensions to <see cref="PluginLanguage"/> values.</summary>
public static class PluginLanguages
{
    /// <summary>
    /// Classifies an extension, with or without a leading dot, using ordinal case-insensitive comparison.
    /// Any extension other than <c>ps1</c>, <c>py</c>, <c>js</c>, <c>sh</c>, <c>bat</c> and <c>cmd</c>,
    /// including executables such as <c>exe</c>, is not a plugin.
    /// </summary>
    public static bool TryClassifyExtension(string? extension, out PluginLanguage language)
    {
        ReadOnlySpan<char> value = extension;
        if (value.StartsWith('.'))
        {
            value = value[1..];
        }

        (bool known, language) = value switch
        {
            _ when value.Equals("ps1", StringComparison.OrdinalIgnoreCase) => (true, PluginLanguage.PowerShell),
            _ when value.Equals("py", StringComparison.OrdinalIgnoreCase) => (true, PluginLanguage.Python),
            _ when value.Equals("js", StringComparison.OrdinalIgnoreCase) => (true, PluginLanguage.JavaScript),
            _ when value.Equals("sh", StringComparison.OrdinalIgnoreCase) => (true, PluginLanguage.Bash),
            _ when value.Equals("bat", StringComparison.OrdinalIgnoreCase) => (true, PluginLanguage.Batch),
            _ when value.Equals("cmd", StringComparison.OrdinalIgnoreCase) => (true, PluginLanguage.Batch),
            _ => (false, default(PluginLanguage)),
        };
        return known;
    }

    /// <summary>Returns whether execution of <paramref name="language"/> is supported or deferred.</summary>
    public static PluginExecutionSupport GetExecutionSupport(PluginLanguage language) => language switch
    {
        PluginLanguage.PowerShell or PluginLanguage.Python or PluginLanguage.JavaScript => PluginExecutionSupport.Supported,
        PluginLanguage.Bash or PluginLanguage.Batch => PluginExecutionSupport.Deferred,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown plugin language."),
    };
}
