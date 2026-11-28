using System.Collections.Immutable;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;

namespace TrayWisp.Cli;

// Stable identifiers of diagnostics produced by the command-line host itself (TW850-TW899). Documented in docs/cli.md;
// numeric values must not be renumbered.
internal enum CliDiagnosticCode
{
    UsageError = 850,
    UnexpectedFailure = 851,
    Cancelled = 852,
    ConfigurationInvalid = 853,
    StorageOverlapsPluginRoot = 854,
    DiscoveryIncomplete = 855,
    PluginNotInDiscovery = 856,
    OutputWithheld = 857,
    RunNotRecorded = 858,
    InputNotRedirected = 859,
    InputTooLarge = 860,
    InputUnreadable = 861,
    OutputInvalid = 862,
    CleanupIncomplete = 863,
    LookalikeCharacters = 864,
}

// TrayWisp's own exit codes (docs/cli.md, "Exit codes"). A plugin's exit code is never returned as one of these.
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int PluginUnavailable = 3;
    public const int ApprovalDenied = 4;
    public const int Configuration = 5;
    public const int ExecutionFailed = 6;
    public const int TimedOut = 7;
    public const int OutputInvalid = 8;
    public const int CleanupIncomplete = 9;
    public const int StorageFailed = 10;
    public const int DiscoveryIncomplete = 11;
    public const int Cancelled = 130;
}

// One diagnostic as the CLI reports it, whichever component produced it. Source is one of cli, discovery, trust,
// execution, output, storage. Messages come from TrayWisp code, which never repeats plugin output in them; they are still
// rendered through the terminal safety policy and JSON escaping.
internal sealed record CliDiagnostic(string Source, int Code, PluginDiagnosticSeverity Severity, string Message)
{
    public string? Path { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    public static CliDiagnostic Cli(PluginDiagnosticSeverity severity, CliDiagnosticCode code, string message) => new("cli", (int)code, severity, message);

    public static CliDiagnostic Error(CliDiagnosticCode code, string message) => Cli(PluginDiagnosticSeverity.Error, code, message);

    public static CliDiagnostic Warning(CliDiagnosticCode code, string message) => Cli(PluginDiagnosticSeverity.Warning, code, message);

    public static CliDiagnostic From(PluginDiagnostic diagnostic) =>
        new("discovery", (int)diagnostic.Code, diagnostic.Severity, diagnostic.Message) { Path = diagnostic.Path, Line = diagnostic.Line };

    public static CliDiagnostic From(TrustDiagnostic diagnostic) => new("trust", (int)diagnostic.Code, diagnostic.Severity, diagnostic.Message);

    public static CliDiagnostic From(ExecutionDiagnostic diagnostic) => new("execution", (int)diagnostic.Code, diagnostic.Severity, diagnostic.Message);

    public static CliDiagnostic From(OutputDiagnostic diagnostic) =>
        new("output", (int)diagnostic.Code, diagnostic.Severity, diagnostic.Message) { Line = diagnostic.Line, Column = diagnostic.Column };

    public static CliDiagnostic From(StorageDiagnostic diagnostic) => new("storage", (int)diagnostic.Code, diagnostic.Severity, diagnostic.Message);

    public static IEnumerable<CliDiagnostic> From(ImmutableArray<TrustDiagnostic> diagnostics) => diagnostics.Select(From);
}
