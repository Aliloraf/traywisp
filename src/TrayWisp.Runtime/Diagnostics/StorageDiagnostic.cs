using System.Globalization;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Runtime.Diagnostics;

/// <summary>
/// Stable identifiers for diagnostics of host storage locations and the run history (<c>TW800</c>-<c>TW849</c>). Numeric
/// values are part of the documented format (see docs/diagnostics.md) and must not be renumbered.
/// </summary>
public enum StorageDiagnosticCode
{
    /// <summary>The storage directory path is not a fully qualified local path with normalized segments.</summary>
    InvalidStoragePath = 800,

    /// <summary>Storage operations are only supported on Windows.</summary>
    UnsupportedPlatform = 801,

    /// <summary>The storage directory, or one of its ancestors, does not exist yet (informational).</summary>
    StorageDirectoryMissing = 802,

    /// <summary>The storage directory or an ancestor is a link, alias, remote, offline, case-sensitive or not a directory.</summary>
    StorageDirectoryUnsafe = 803,

    /// <summary>The storage directory or an ancestor could not be examined or created.</summary>
    StorageDirectoryInaccessible = 804,

    /// <summary>A storage, lock or temporary file is not a regular local file with a single name at its configured path.</summary>
    StorageFileUnsafe = 805,

    /// <summary>A storage file could not be examined.</summary>
    StorageFileInaccessible = 806,

    /// <summary>The history file could not be opened or read.</summary>
    HistoryUnreadable = 810,

    /// <summary>The history file is not a valid version 1 history. It is never overwritten automatically.</summary>
    HistoryCorrupt = 811,

    /// <summary>The history file has a schema version this TrayWisp does not support. It is never overwritten.</summary>
    HistoryUnsupportedVersion = 812,

    /// <summary>The history file is larger than <see cref="PluginRunHistory.MaxHistoryBytes"/>.</summary>
    HistoryTooLarge = 813,

    /// <summary>The history file holds more than <see cref="PluginRunHistory.MaxRecords"/> records.</summary>
    HistoryTooManyRecords = 814,

    /// <summary>The history lock was not acquired within the lock timeout.</summary>
    HistoryLockTimeout = 815,

    /// <summary>The history lock file could not be opened.</summary>
    HistoryLockFailed = 816,

    /// <summary>The updated history could not be written; the previous history is unchanged.</summary>
    HistoryWriteFailed = 817,

    /// <summary>One record would be larger than <see cref="PluginRunHistory.MaxRecordBytes"/>; it was not stored.</summary>
    RecordTooLarge = 818,

    /// <summary>The record was not stored: its root or identity is not supported, or the root overlaps the history.</summary>
    RecordRejected = 819,

    /// <summary>Older records were removed to keep the history within its bounds (informational).</summary>
    RecordsEvicted = 820,

    /// <summary>No history exists yet (informational).</summary>
    HistoryNotFound = 821,
}

/// <summary>
/// A diagnostic of a storage location or of the run history. Messages are English, never contain plugin output, and
/// quote paths with <see cref="PluginDiagnosticText.Quote"/>.
/// </summary>
/// <param name="Severity">How serious the diagnostic is.</param>
/// <param name="Code">Stable identifier.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record StorageDiagnostic(PluginDiagnosticSeverity Severity, StorageDiagnosticCode Code, string Message)
{
    /// <summary>Formats the diagnostic as <c>Severity TWcode: message</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Severity} TW{(int)Code}: {Message}");
}
