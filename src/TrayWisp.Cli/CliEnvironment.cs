using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Cli;

// Everything the command application takes from its host: streams, whether standard input is redirected, the local
// application data folder, and the services it creates. Program supplies the console; tests supply in-memory streams,
// isolated folders and services built with internal test hooks. There is no public seam and no unchecked execution hook:
// the execution service factory can only build a PluginExecutionService, which authorizes every run itself.
internal sealed class CliEnvironment
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public required Func<Stream> OpenInput { get; init; }

    public required bool InputRedirected { get; init; }

    // %LOCALAPPDATA%, or null when unavailable. Only consulted when --root or --data-dir is not given.
    public required Func<string?> LocalApplicationData { get; init; }

    // Ends Ctrl+C handling before the report is written (ConsoleCancellation.CloseAsync): returns a cancellation callback's
    // failure, or null. Not set in tests that do not need it.
    public Func<Task<Exception?>>? CloseCancellation { get; init; }

    public Func<string, PluginTrustService> CreateTrustService { get; init; } = storePath => new PluginTrustService(storePath);

    public Func<PluginTrustService, PluginExecutionOptions, PluginExecutionService> CreateExecutionService { get; init; } =
        (trust, options) => new PluginExecutionService(trust, options);

    public Func<string, PluginRunHistory> CreateRunHistory { get; init; } = directory => new PluginRunHistory(directory);
}

// The locations a command uses, resolved from the request without touching the file system.
internal sealed record CliPaths(PluginRoot Root, bool RootIsDefault, PluginRoot DataDirectory, bool DataDirectoryIsDefault)
{
    public const string TrustDirectoryName = "trust";
    public const string StoreFileName = "approvals.json";
    public const string DiagnosticsDirectoryName = "diagnostics";

    public PluginRoot TrustDirectory => Child(DataDirectory, TrustDirectoryName);

    public string TrustStorePath => TrustDirectory.Value + "\\" + StoreFileName;

    public PluginRoot DiagnosticsDirectory => Child(DataDirectory, DiagnosticsDirectoryName);

    // The protected storage directories that must not overlap the plugin root, in either direction.
    public IEnumerable<(string What, PluginRoot Directory)> StorageDirectories =>
        [("trust store", TrustDirectory), ("run history", DiagnosticsDirectory)];

    public static PluginRoot Child(PluginRoot parent, string name) =>
        PluginRoot.Create(parent.IsDriveRoot ? parent.Value + name : parent.Value + "\\" + name);
}
