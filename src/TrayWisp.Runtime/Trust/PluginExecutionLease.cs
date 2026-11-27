using System.Runtime.Versioning;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Trust;

// The authorization of exactly one plugin launch. Created only by PluginTrustService.CheckTrustForExecution for an
// Approved decision, from the inspection that decision was made on: the plugin file stays open with the sharing
// the inspection used (read only: other programs cannot write, delete, rename or replace it), and the root and
// every directory inside it on the file's path stay open (they cannot be deleted or renamed). The trust store
// lock is not part of it.
//
// It is not a permission token. It is internal, single-use, holds no reference to the trust service, and the
// execution service creates exactly one per run and disposes it when that run ends, on every path. See
// docs/execution-engine.md ("Authorization lease").
[SupportedOSPlatform("windows")]
internal sealed class PluginExecutionLease : IDisposable
{
    private readonly InspectedPlugin _inspection;
    private int _disposed;

    public PluginExecutionLease(PluginTrustDecision decision, InspectedPlugin inspection)
    {
        if (!decision.IsApproved || !string.Equals(decision.ValidatedFilePath, inspection.FilePath, StringComparison.Ordinal))
        {
            inspection.Dispose();
            throw new ArgumentException("A lease can only be created for an approved decision and its own inspection.", nameof(decision));
        }

        Decision = decision;
        _inspection = inspection;
    }

    // The approved decision the lease was created with.
    public PluginTrustDecision Decision { get; }

    // The approval identity that authorized this launch. Output of the run is associated with it.
    public PluginApprovalIdentity Approval => Decision.Approval!;

    // The exact file that was validated and hashed, with its on-disk spelling. The only path that may be launched.
    public string ValidatedFilePath => Decision.ValidatedFilePath!;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // Whether the inspection's handles are still held (for tests).
    internal bool HoldsHandles => !IsDisposed && _inspection.HoldsHandles;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inspection.Dispose();
        }
    }
}
