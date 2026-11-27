namespace TrayWisp.Runtime.Execution;

/// <summary>
/// Trusted host configuration for <see cref="PluginExecutionService"/>. Nothing here comes from plugins, their
/// metadata or their output.
/// </summary>
/// <remarks>
/// An explicitly configured interpreter path replaces automatic resolution for its language. If it turns out to be
/// missing or unsafe when a plugin runs, that run fails with
/// <see cref="Core.Execution.PluginExecutionStatus.InterpreterConfigurationInvalid"/>; TrayWisp never falls back to
/// another interpreter. A configured path is validated, not authenticated: pointing it at a program makes that
/// program the interpreter (see docs/execution-engine.md, "Interpreter resolution").
/// </remarks>
public sealed record PluginExecutionOptions
{
    /// <summary>The default settings: automatic interpreter resolution and a 30 second timeout.</summary>
    public static PluginExecutionOptions Default { get; } = new();

    /// <summary>Shortest accepted run timeout: 1 second.</summary>
    public static TimeSpan MinTimeout { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest accepted run timeout: 5 minutes.</summary>
    public static TimeSpan MaxTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The timeout of runs that do not set <see cref="PluginRunOptions.Timeout"/>: 30 seconds by default, between
    /// <see cref="MinTimeout"/> and <see cref="MaxTimeout"/>.
    /// </summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The full path of the PowerShell executable used for <c>.ps1</c> plugins (for example
    /// <c>C:\Program Files\PowerShell\7\pwsh.exe</c>), or <see langword="null"/> to resolve it automatically.
    /// </summary>
    public string? PowerShellPath { get; init; }

    /// <summary>The full path of the Python 3 executable used for <c>.py</c> plugins, or <see langword="null"/> to resolve it automatically.</summary>
    public string? PythonPath { get; init; }

    /// <summary>The full path of the Node.js executable used for <c>.js</c> plugins, or <see langword="null"/> to resolve it automatically.</summary>
    public string? NodePath { get; init; }
}

/// <summary>Settings for one call of <see cref="PluginExecutionService.RunAsync"/>.</summary>
public sealed record PluginRunOptions
{
    /// <summary>The default settings.</summary>
    public static PluginRunOptions Default { get; } = new();

    /// <summary>
    /// How long the run may take, from the moment the process starts until it has exited and both output streams have
    /// ended. <see langword="null"/> uses <see cref="PluginExecutionOptions.DefaultTimeout"/>. Between
    /// <see cref="PluginExecutionOptions.MinTimeout"/> and <see cref="PluginExecutionOptions.MaxTimeout"/>.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
