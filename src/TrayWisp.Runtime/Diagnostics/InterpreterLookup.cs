using System.Collections.Immutable;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;

namespace TrayWisp.Runtime.Diagnostics;

/// <summary>The outcome of <see cref="InterpreterLookup.Find"/>.</summary>
public enum InterpreterLookupStatus
{
    /// <summary>An interpreter path was found and validated. It was not run, and its version is unknown.</summary>
    Resolved,

    /// <summary>No acceptable interpreter was found by automatic resolution.</summary>
    Unavailable,

    /// <summary>The explicitly configured interpreter cannot be used. There is no fallback.</summary>
    ConfigurationInvalid,

    /// <summary>The language is recognized but its execution is deferred (Bash, batch). Nothing was looked up.</summary>
    UnsupportedLanguage,

    /// <summary>Not Windows. Nothing was looked up.</summary>
    UnsupportedPlatform,
}

/// <summary>The interpreter the execution engine would use for a language at this moment, without running anything.</summary>
public sealed class InterpreterLookupResult
{
    internal InterpreterLookupResult(PluginLanguage language, InterpreterLookupStatus status, bool configured, PluginInterpreter? interpreter, ImmutableArray<ExecutionDiagnostic> diagnostics)
    {
        Language = language;
        Status = status;
        IsConfigured = configured;
        Interpreter = status == InterpreterLookupStatus.Resolved ? interpreter : null;
        Diagnostics = diagnostics;
    }

    /// <summary>The plugin language.</summary>
    public PluginLanguage Language { get; }

    /// <summary>The outcome.</summary>
    public InterpreterLookupStatus Status { get; }

    /// <summary>Whether an interpreter path was explicitly configured for the language.</summary>
    public bool IsConfigured { get; }

    /// <summary>The validated interpreter path, its kind and source, when resolved.</summary>
    public PluginInterpreter? Interpreter { get; }

    /// <summary>The resolution diagnostics (<c>TW610</c>-<c>TW619</c>), exactly as a run would report them.</summary>
    public ImmutableArray<ExecutionDiagnostic> Diagnostics { get; }
}

/// <summary>
/// A read-only view of the execution engine's interpreter resolution, for diagnostics such as <c>traywisp doctor</c>.
/// It uses the engine's own resolver and configuration parsing, so it follows exactly the rules of a run (see
/// docs/execution-engine.md, "Interpreter resolution"): an explicitly configured path is used or rejected without
/// fallback; otherwise PowerShell 7's installation folder, validated PATH directories and the system directory are
/// searched. Nothing is launched, probed for its version, installed or searched through a shell, and environment
/// variable values are not returned.
/// </summary>
/// <remarks>
/// A resolved path names a validated local file with a portable executable header. It does not authenticate the
/// program, prove that it runs the expected language version, or say whether a PowerShell execution policy permits
/// scripts. A later run resolves again and may find something else.
/// </remarks>
public static class InterpreterLookup
{
    /// <summary>
    /// Whether <paramref name="path"/> is acceptable as an explicitly configured interpreter path: a fully qualified local
    /// path to an <c>.exe</c> file with normalized segments. Syntax only; nothing is accessed. The same rule as
    /// <see cref="PluginExecutionService"/>'s constructor.
    /// </summary>
    public static bool IsValidConfiguredPath(string? path) => path is not null && ConfiguredExecutable.TryParse(path, out _);

    /// <summary>Resolves the interpreter for <paramref name="language"/> as a run of a plugin under <paramref name="rootPath"/> would.</summary>
    /// <param name="language">The plugin language.</param>
    /// <param name="options">The host's interpreter configuration; <see cref="PluginExecutionOptions.Default"/> when <see langword="null"/>.</param>
    /// <param name="rootPath">The plugin root, whose directories are never used as interpreter locations, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <exception cref="ArgumentException">A configured interpreter path is not acceptable (see <see cref="IsValidConfiguredPath"/>).</exception>
    /// <exception cref="OperationCanceledException">The lookup was cancelled.</exception>
    public static InterpreterLookupResult Find(PluginLanguage language, PluginExecutionOptions? options = null, string? rootPath = null, CancellationToken cancellationToken = default)
    {
        options ??= PluginExecutionOptions.Default;
        string? configuredPath = language switch
        {
            PluginLanguage.PowerShell => options.PowerShellPath,
            PluginLanguage.Python => options.PythonPath,
            PluginLanguage.JavaScript => options.NodePath,
            _ => null,
        };
        ConfiguredExecutable? configured = null;
        if (configuredPath is not null && !ConfiguredExecutable.TryParse(configuredPath, out configured))
        {
            throw new ArgumentException("A configured interpreter must be a fully qualified path on a local drive to an .exe file.", nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (PluginLanguages.GetExecutionSupport(language) != PluginExecutionSupport.Supported)
        {
            return new InterpreterLookupResult(language, InterpreterLookupStatus.UnsupportedLanguage, configured is not null, interpreter: null, []);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new InterpreterLookupResult(language, InterpreterLookupStatus.UnsupportedPlatform, configured is not null, interpreter: null, []);
        }

        PluginRoot? root = null;
        if (rootPath is not null)
        {
            PluginRoot.TryCreate(rootPath, out root);
        }

        var diagnostics = new ExecutionDiagnosticBag();
        var resolver = new InterpreterResolver(InterpreterSearchLocations.FromCurrentProcess(), root, diagnostics, cancellationToken);
        InterpreterResolution resolution = resolver.Resolve(language, configured, out PluginInterpreter? interpreter);
        InterpreterLookupStatus status = resolution switch
        {
            InterpreterResolution.Resolved => InterpreterLookupStatus.Resolved,
            InterpreterResolution.ConfigurationInvalid => InterpreterLookupStatus.ConfigurationInvalid,
            _ => InterpreterLookupStatus.Unavailable,
        };
        return new InterpreterLookupResult(language, status, configured is not null, interpreter, diagnostics.ToImmutable());
    }
}
