using System.Collections.Immutable;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Trust;

// The category of a failed step, which decides the status an operation reports.
internal enum TrustFailureKind
{
    UnsafePath,
    PluginUnavailable,
    StoreUnavailable,
}

internal sealed record TrustFailure(TrustFailureKind Kind, TrustDiagnostic Diagnostic)
{
    public static TrustFailure Unsafe(TrustDiagnosticCode code, string message) =>
        new(TrustFailureKind.UnsafePath, new TrustDiagnostic(PluginDiagnosticSeverity.Error, code, message));

    public static TrustFailure Unavailable(TrustDiagnosticCode code, string message) =>
        new(TrustFailureKind.PluginUnavailable, new TrustDiagnostic(PluginDiagnosticSeverity.Error, code, message));

    public static TrustFailure Store(TrustDiagnosticCode code, string message) =>
        new(TrustFailureKind.StoreUnavailable, new TrustDiagnostic(PluginDiagnosticSeverity.Error, code, message));
}

// Collects the diagnostics of one operation, keeping at most PluginTrustService.MaxDiagnostics. When the
// limit is reached, the last slot reports that diagnostics were dropped.
internal sealed class TrustDiagnosticBag
{
    private readonly List<TrustDiagnostic> _items = [];
    private bool _overflowed;

    public void Add(TrustDiagnostic diagnostic)
    {
        if (_overflowed)
        {
            return;
        }

        if (_items.Count < PluginTrustService.MaxDiagnostics - 1)
        {
            _items.Add(diagnostic);
            return;
        }

        _overflowed = true;
        _items.Add(new TrustDiagnostic(
            PluginDiagnosticSeverity.Warning,
            TrustDiagnosticCode.DiagnosticLimitReached,
            $"More than {PluginTrustService.MaxDiagnostics - 1} diagnostics were produced; the rest were dropped."));
    }

    public void Add(PluginDiagnosticSeverity severity, TrustDiagnosticCode code, string message) => Add(new TrustDiagnostic(severity, code, message));

    public ImmutableArray<TrustDiagnostic> ToImmutable() => [.. _items];
}

// Deterministic seams for tests of races and failures. Never set in production.
internal sealed class TrustTestHooks
{
    // After the plugin file has been opened and validated, before it is hashed.
    public Action? PluginOpened { get; init; }

    // After each chunk of the plugin file has been hashed, with the number of bytes hashed so far.
    public Action<long>? ChunkHashed { get; init; }

    // After the replacement store has been written and flushed to its temporary file, before it is published.
    public Action<string>? BeforeStorePublish { get; init; }

    // Replaces the directory entry budget of one validation pass, so the limit can be tested with few files.
    public int? MaxDirectoryEntries { get; init; }

    // Replaces the capacity for unsaved invalidations of one service, so the bound can be tested.
    public int? MaxPendingInvalidations { get; init; }

    // After the trust store lock has been acquired and the store directory is held open.
    public Action? StoreLocked { get; init; }
}
