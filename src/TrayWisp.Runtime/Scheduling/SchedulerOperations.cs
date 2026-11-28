using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Scheduling;

// The identity of the plugin root directory (volume serial number and 128-bit file identifier), used to notice that the
// root was replaced by another directory with the same path.
internal readonly record struct RootIdentity(ulong Volume, ulong Low, ulong High);

// The outcome of validating the plugin root before a scan. On success it holds the root directory open (denying its
// deletion and renaming) until disposed, so the scan and the watcher refer to the validated directory.
internal sealed class RootValidation : IDisposable
{
    private readonly IDisposable? _handle;

    public RootValidation(SchedulerDiagnostic problem)
    {
        Problem = problem;
    }

    public RootValidation(IDisposable? handle, RootIdentity? identity)
    {
        _handle = handle;
        Identity = identity;
    }

    // Why the root cannot be scanned, or null when it was validated.
    public SchedulerDiagnostic? Problem { get; }

    // The root's identity, when it could be read.
    public RootIdentity? Identity { get; }

    public void Dispose() => _handle?.Dispose();
}

// Everything the scheduler does to the file system, the trust store and processes. The production implementation calls
// discovery, the long-lived trust service and the execution engine; tests replace it with deterministic fakes. Internal:
// there is no public seam, and the production instance is built only from an execution service and its own trust service.
internal abstract class SchedulerOperations
{
    // Validates the root as the trust checks do: drive type first, then every directory from the drive root down without
    // following links (no reparse points, not remote, no case-sensitive ancestor, final path equal to the root), and only
    // then the short-name expansion check.
    public abstract RootValidation ValidateRoot(PluginRoot root, CancellationToken cancellationToken);

    // PluginDiscovery.Discover with its default limits. Throws OperationCanceledException when cancelled.
    public abstract PluginDiscoveryResult Discover(PluginRoot root, CancellationToken cancellationToken);

    // The authoritative trust check of the shared trust service: PluginTrustService.CheckTrust(root, id).
    public abstract PluginTrustDecision CheckTrust(PluginRoot root, PluginId id, CancellationToken cancellationToken);

    // The trust check for published output: PluginTrustService.CheckTrust(approvalIdentity).
    public abstract PluginTrustDecision CheckApproval(PluginApprovalIdentity approval, CancellationToken cancellationToken);

    // PluginExecutionService.RunAsync with the expected approval: the only way the scheduler launches anything.
    public abstract Task<PluginExecutionResult> RunAsync(PluginRoot root, PluginId id, PluginApprovalIdentity expectedApproval, CancellationToken cancellationToken);

    // Starts watching the root recursively. Returns null (with the reason) when watching is not possible.
    public abstract IDisposable? CreateWatcher(PluginRoot root, Action<WatcherNotification> sink, out string? problem);
}

internal sealed class ProductionSchedulerOperations(PluginExecutionService execution, PluginRunOptions? runOptions) : SchedulerOperations
{
    private readonly PluginTrustService _trust = execution.TrustService;

    public override RootValidation ValidateRoot(PluginRoot root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return new RootValidation(RootProblem(root, "plugin scheduling is only supported on Windows."));
        }

        // Drive type, then the directory chain, held open; never the short-name expansion before both are validated. The
        // same validation as PluginRootValidation.Check, which the CLI uses.
        TrustFailure? failure = PluginRootValidation.Validate(root, hold: true, out SafeFileHandle? handle);
        if (failure is not null)
        {
            TrustDiagnostic diagnostic = failure.Diagnostic;
            return new RootValidation(RootProblem(root, $"TW{(int)diagnostic.Code}: {diagnostic.Message}"));
        }

        RootIdentity? identity = WindowsFileApi.TryGetFileId(handle!, out (ulong Volume, ulong Low, ulong High) id, out _)
            ? new RootIdentity(id.Volume, id.Low, id.High)
            : null;
        return new RootValidation(handle, identity);
    }

    public override PluginDiscoveryResult Discover(PluginRoot root, CancellationToken cancellationToken) =>
        PluginDiscovery.Discover(root.Value, PluginDiscoveryOptions.Default, cancellationToken);

    public override PluginTrustDecision CheckTrust(PluginRoot root, PluginId id, CancellationToken cancellationToken) =>
        _trust.CheckTrust(root.Value, id, cancellationToken);

    public override PluginTrustDecision CheckApproval(PluginApprovalIdentity approval, CancellationToken cancellationToken) =>
        _trust.CheckTrust(approval, cancellationToken);

    public override Task<PluginExecutionResult> RunAsync(PluginRoot root, PluginId id, PluginApprovalIdentity expectedApproval, CancellationToken cancellationToken) =>
        execution.RunAsync(root.Value, id, runOptions, expectedApproval, cancellationToken);

    public override IDisposable? CreateWatcher(PluginRoot root, Action<WatcherNotification> sink, out string? problem) =>
        PluginRootWatcher.TryCreate(root.Value, sink, out problem);

    private static SchedulerDiagnostic RootProblem(PluginRoot root, string reason) =>
        new(
            PluginDiagnosticSeverity.Warning,
            SchedulerDiagnosticCode.RootUnavailable,
            $"The plugin root {TrustPaths.Quote(root.Value)} cannot be scanned: {reason} Known plugins are kept as uncertain and nothing is launched.");
}
