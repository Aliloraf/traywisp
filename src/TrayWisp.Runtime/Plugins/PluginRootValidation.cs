using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Plugins;

/// <summary>The outcome of <see cref="PluginRootValidation.Check"/>.</summary>
public enum PluginRootStatus
{
    /// <summary>The root is an ordinary local directory that the trust checks accept.</summary>
    Valid,

    /// <summary>The root, or one of its ancestors, does not exist. Nothing was created.</summary>
    NotFound,

    /// <summary>The root or an ancestor is a link, junction, alias, remote, offline, case-sensitive or not a directory.</summary>
    Unsafe,

    /// <summary>The root or an ancestor could not be examined (for example access was denied).</summary>
    Inaccessible,

    /// <summary>The path is not a fully qualified local path with normalized segments. Nothing was accessed.</summary>
    InvalidPath,

    /// <summary>Not Windows: plugin roots are only supported on Windows. Nothing was accessed.</summary>
    UnsupportedPlatform,
}

/// <summary>A point-in-time observation of a plugin root. It authorizes nothing and holds nothing open.</summary>
public sealed class PluginRootCheckResult
{
    internal PluginRootCheckResult(PluginRootStatus status, PluginRoot? root, TrustDiagnostic? diagnostic)
    {
        Status = status;
        Root = root;
        Diagnostics = diagnostic is null ? [] : [diagnostic];
    }

    /// <summary>The outcome.</summary>
    public PluginRootStatus Status { get; }

    /// <summary>Whether the root was accepted.</summary>
    public bool IsValid => Status == PluginRootStatus.Valid;

    /// <summary>The normalized root, when its syntax was accepted.</summary>
    public PluginRoot? Root { get; }

    /// <summary>At most one diagnostic (<c>TW500</c>-<c>TW508</c>) explaining a rejection.</summary>
    public ImmutableArray<TrustDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Read-only validation of a plugin root, in the same order and with the same rules as the scheduler's root validation
/// and the trust checks: path syntax (no file system access), then the drive type, then every directory from the drive
/// root down, opened without following links (no reparse points, not offline or remote, not case-sensitive, final path
/// equal to the root), and only then the 8.3 short-name expansion check. Nothing is created, written or locked, and
/// nothing stays open after the method returns. See docs/cli.md ("Paths").
/// </summary>
public static class PluginRootValidation
{
    /// <summary>Validates <paramref name="rootPath"/> as a plugin root.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="rootPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The check was cancelled before it started.</exception>
    public static PluginRootCheckResult Check(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return new PluginRootCheckResult(
                PluginRootStatus.UnsupportedPlatform,
                root: null,
                new TrustDiagnostic(PluginDiagnosticSeverity.Error, TrustDiagnosticCode.UnsupportedPlatform, "Plugin roots are only supported on Windows."));
        }

        if (!PluginRoot.TryCreate(rootPath, out PluginRoot? root))
        {
            return new PluginRootCheckResult(
                PluginRootStatus.InvalidPath,
                root: null,
                new TrustDiagnostic(
                    PluginDiagnosticSeverity.Error,
                    TrustDiagnosticCode.InvalidRootPath,
                    $"The plugin root {TrustPaths.Quote(rootPath)} is not a supported path. Use a fully qualified path on a local drive, such as C:\\plugins, "
                    + "without '.' or '..' segments, doubled separators, alternate data streams, device or UNC prefixes, or names ending in a space or dot."));
        }

        TrustFailure? failure = Validate(root, hold: false, out _);
        if (failure is null)
        {
            return new PluginRootCheckResult(PluginRootStatus.Valid, root, diagnostic: null);
        }

        PluginRootStatus status = failure switch
        {
            { Kind: TrustFailureKind.PluginUnavailable, Diagnostic.Code: TrustDiagnosticCode.RootNotFound } => PluginRootStatus.NotFound,
            { Kind: TrustFailureKind.PluginUnavailable } => PluginRootStatus.Inaccessible,
            _ => PluginRootStatus.Unsafe,
        };
        return new PluginRootCheckResult(status, root, failure.Diagnostic);
    }

    // The shared validation of the scheduler and of Check: drive type, then the directory chain (held open while it is
    // checked), and only then Path.GetFullPath, which may expand 8.3 short names by querying the file system. With hold,
    // a validated root stays open (denying its deletion and renaming) in handle, which the caller disposes.
    [SupportedOSPlatform("windows")]
    internal static TrustFailure? Validate(PluginRoot root, bool hold, out SafeFileHandle? handle)
    {
        PathCheck check = TrustPaths.OpenDirectoryChain(root, rejectCaseSensitiveAncestors: true, createMissing: false, hold: true, out handle);
        if (check.Problem != PathProblem.None)
        {
            return PluginFileInspector.RootFailure(root, check);
        }

        if (!TrustPaths.IsUnchangedByFullPath(root.Value))
        {
            handle!.Dispose();
            handle = null;
            return TrustFailure.Unsafe(
                TrustDiagnosticCode.RootAlias,
                $"The plugin root {TrustPaths.Quote(root.Value)} is an alias, for example through a short (8.3) name. Use the full path instead.");
        }

        if (!hold)
        {
            handle!.Dispose();
            handle = null;
        }

        return null;
    }
}
