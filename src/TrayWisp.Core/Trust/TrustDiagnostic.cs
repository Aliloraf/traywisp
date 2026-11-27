using System.Globalization;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Core.Trust;

/// <summary>
/// Stable identifiers for trust and approval diagnostics, shown as <c>TW500</c>–<c>TW599</c>. Numeric
/// values are part of the documented format (see docs/trust-model.md) and must not be renumbered.
/// </summary>
public enum TrustDiagnosticCode
{
    /// <summary>The plugin root is not a supported fully qualified local path (relative, drive-relative, UNC, device namespace, alternate data stream, invalid or ambiguous segment, too long).</summary>
    InvalidRootPath = 500,

    /// <summary>Trust operations are only supported on Windows.</summary>
    UnsupportedPlatform = 501,

    /// <summary>The plugin root, or one of its ancestors, does not exist.</summary>
    RootNotFound = 502,

    /// <summary>The plugin root, or one of its ancestors, is not a directory.</summary>
    RootNotDirectory = 503,

    /// <summary>The plugin root or an ancestor is a reparse point (link, junction, mount point, cloud placeholder) or is offline or stored remotely.</summary>
    RootUnsafe = 504,

    /// <summary>The plugin root path is an alias: the opened directory's final path differs (short name, substituted drive, link or mount point in an ancestor).</summary>
    RootAlias = 505,

    /// <summary>The plugin root is on a network drive or resolves to a network path.</summary>
    RemoteRoot = 506,

    /// <summary>The plugin root, or an ancestor, could not be examined (access denied, in use, I/O error).</summary>
    RootInaccessible = 507,

    /// <summary>An ancestor of the plugin root is a case-sensitive directory, so the root path would not identify one directory.</summary>
    CaseSensitiveAncestor = 508,

    /// <summary>The plugin identity is not a path discovery can produce (too long, too deep, dot-prefixed segment, not a plugin file name).</summary>
    InvalidPluginPath = 510,

    /// <summary>The plugin file, or a directory on its path, does not exist.</summary>
    PluginNotFound = 511,

    /// <summary>The plugin file or a directory on its path is a reparse point, offline or remote, hidden, of the wrong kind, or not a regular disk file.</summary>
    PluginPathUnsafe = 512,

    /// <summary>Two or more files or directories on the plugin's path have names that differ only by case.</summary>
    IdentityCollision = 513,

    /// <summary>The opened plugin file's final path differs from the expected path.</summary>
    PluginPathAlias = 514,

    /// <summary>A directory on the plugin's path has more entries than the validation limit, so collisions could not be ruled out.</summary>
    DirectoryLimitReached = 515,

    /// <summary>A directory on the plugin's path could not be opened or listed.</summary>
    DirectoryUnreadable = 516,

    /// <summary>The plugin root and the trust store location overlap.</summary>
    RootOverlapsTrustStore = 517,

    /// <summary>The plugin file is locked or in use by another program.</summary>
    FileLocked = 520,

    /// <summary>The plugin file could not be opened or read.</summary>
    FileUnreadable = 521,

    /// <summary>The plugin file is larger than the fingerprinting limit.</summary>
    FileTooLarge = 522,

    /// <summary>The plugin file or its path changed while it was being inspected.</summary>
    FileChangedDuringInspection = 523,

    /// <summary>The plugin has no current approval.</summary>
    ApprovalPending = 530,

    /// <summary>The plugin's content differs from the approved content.</summary>
    ContentChanged = 531,

    /// <summary>The previous approval was invalidated and removed from the trust store.</summary>
    ApprovalInvalidated = 532,

    /// <summary>The invalidation of a changed plugin's approval could not be saved.</summary>
    InvalidationNotPersisted = 533,

    /// <summary>The plugin's current content differs from the content the caller asked to approve.</summary>
    FingerprintMismatch = 534,

    /// <summary>The expected approval identity is no longer the current approval (revoked, reapproved or changed).</summary>
    StaleApproval = 535,

    /// <summary>The trust store could not be opened or read.</summary>
    StoreUnreadable = 540,

    /// <summary>The trust store is not valid: malformed JSON, missing, unknown or duplicate properties, invalid or duplicate records.</summary>
    StoreInvalid = 541,

    /// <summary>The trust store uses a schema version this build does not support.</summary>
    StoreUnsupportedVersion = 542,

    /// <summary>The trust store is larger than the size limit, or would become larger.</summary>
    StoreTooLarge = 543,

    /// <summary>The trust store has, or would have, more approval records than the limit.</summary>
    StoreTooManyRecords = 544,

    /// <summary>The trust store lock could not be acquired in time.</summary>
    StoreLockTimeout = 545,

    /// <summary>The trust store lock file could not be created or opened.</summary>
    StoreLockFailed = 546,

    /// <summary>The updated trust store could not be written or published. The previous store is unchanged.</summary>
    StoreWriteFailed = 547,

    /// <summary>The trust store location is unsafe: a reparse point, offline or remote entry, unexpected directory, alias or unsupported drive.</summary>
    StorePathUnsafe = 548,

    /// <summary>More diagnostics were produced than one result may hold. Later ones were dropped.</summary>
    DiagnosticLimitReached = 599,
}

/// <summary>
/// A structured message produced by a trust operation. Messages never contain plugin content or trust store
/// content. Paths and system messages in them are length-limited and escaped with
/// <see cref="PluginDiagnosticText"/>.
/// </summary>
/// <param name="Severity">How serious the diagnostic is.</param>
/// <param name="Code">Stable diagnostic identifier.</param>
/// <param name="Message">Human-readable English description.</param>
public sealed record TrustDiagnostic(PluginDiagnosticSeverity Severity, TrustDiagnosticCode Code, string Message)
{
    /// <summary>Formats the diagnostic as <c>Severity TWcode: message</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Severity} TW{(int)Code}: {Message}");
}
