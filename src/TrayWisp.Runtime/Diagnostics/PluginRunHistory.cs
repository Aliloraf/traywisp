using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Diagnostics;

/// <summary>Settings for <see cref="PluginRunHistory"/>.</summary>
public sealed record PluginRunHistoryOptions
{
    /// <summary>The default settings.</summary>
    public static PluginRunHistoryOptions Default { get; } = new();

    /// <summary>The longest permitted <see cref="LockTimeout"/>: 1 minute.</summary>
    public static TimeSpan MaxLockTimeout { get; } = TimeSpan.FromMinutes(1);

    /// <summary>How long to wait for the history lock (default 5 s, at most <see cref="MaxLockTimeout"/>).</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>The outcome of <see cref="PluginRunHistory.Record"/>.</summary>
public enum RunHistoryWriteStatus
{
    /// <summary>The record was added; older records may have been removed to stay within the bounds.</summary>
    Recorded,

    /// <summary>The record itself was not acceptable (too large, unsupported identity, root overlapping the history). The history is unchanged.</summary>
    Rejected,

    /// <summary>The history could not be updated (unsafe or inaccessible location, lock timeout, corrupt or unsupported file, write failure). It is unchanged.</summary>
    Unavailable,
}

/// <summary>The result of <see cref="PluginRunHistory.Record"/>. A failure never changes the run it describes.</summary>
public sealed class RunHistoryWriteResult
{
    internal RunHistoryWriteResult(RunHistoryWriteStatus status, int evictedRecords, ImmutableArray<StorageDiagnostic> diagnostics)
    {
        Status = status;
        EvictedRecords = evictedRecords;
        Diagnostics = diagnostics;
    }

    /// <summary>The outcome.</summary>
    public RunHistoryWriteStatus Status { get; }

    /// <summary>Whether the record was stored.</summary>
    public bool Succeeded => Status == RunHistoryWriteStatus.Recorded;

    /// <summary>How many of the oldest records were removed to make room.</summary>
    public int EvictedRecords { get; }

    /// <summary>Diagnostics explaining a failure or an eviction.</summary>
    public ImmutableArray<StorageDiagnostic> Diagnostics { get; }
}

/// <summary>The outcome of <see cref="PluginRunHistory.Read"/>.</summary>
public enum RunHistoryReadStatus
{
    /// <summary>No history exists (no directory or no file). Nothing was created.</summary>
    NoHistory,

    /// <summary>A valid history exists and holds no records.</summary>
    Empty,

    /// <summary>A valid history was read.</summary>
    Loaded,

    /// <summary>The history file is invalid, too large or has too many records. It was not changed.</summary>
    Corrupt,

    /// <summary>The history file has a schema version this TrayWisp does not support. It was not changed.</summary>
    UnsupportedVersion,

    /// <summary>The history could not be read (unsafe or inaccessible location, lock timeout, read failure).</summary>
    Unavailable,
}

/// <summary>The result of <see cref="PluginRunHistory.Read"/>.</summary>
public sealed class RunHistoryReadResult
{
    internal RunHistoryReadResult(RunHistoryReadStatus status, ImmutableArray<PluginRunRecord> records, int totalRecords, StorageDiagnostic? diagnostic)
    {
        Status = status;
        Records = records;
        TotalRecords = totalRecords;
        Diagnostics = diagnostic is null ? [] : [diagnostic];
    }

    /// <summary>The outcome.</summary>
    public RunHistoryReadStatus Status { get; }

    /// <summary>Whether a valid history was read or none exists.</summary>
    public bool Succeeded => Status is RunHistoryReadStatus.NoHistory or RunHistoryReadStatus.Empty or RunHistoryReadStatus.Loaded;

    /// <summary>The most recent records, at most the requested number, oldest first (the order they were recorded in).</summary>
    public ImmutableArray<PluginRunRecord> Records { get; }

    /// <summary>How many records the history holds.</summary>
    public int TotalRecords { get; }

    /// <summary>At most one diagnostic explaining a failure or a missing history.</summary>
    public ImmutableArray<StorageDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Bounded, local history of plugin run summaries for troubleshooting. One versioned JSON file
/// (<c>history.json</c>) in a host-controlled directory, synchronized between threads and processes with a separate lock
/// file (<c>history.json.lock</c>, never the approval store's lock), replaced atomically through a flushed temporary file
/// in the same directory, with at most <see cref="MaxRecords"/> records and <see cref="MaxHistoryBytes"/> bytes. The
/// oldest records are removed first. See docs/diagnostics.md.
/// </summary>
/// <remarks>
/// <para>The history never authorizes or blocks anything: it is written after a run and read only for display.</para>
/// <para>A corrupt or unsupported file is reported and never repaired, replaced or cleared automatically.</para>
/// <para>Nothing is sent anywhere. Records hold no output, labels, actions, standard error, script content,
/// environment variables, interpreter paths, command lines or diagnostic messages (see <see cref="PluginRunRecord"/>).</para>
/// <para>Supported on Windows only. Instances are thread-safe and may share a directory with other instances and processes.</para>
/// </remarks>
public sealed class PluginRunHistory
{
    /// <summary>Most records kept: 200.</summary>
    public const int MaxRecords = 200;

    /// <summary>Largest history file read or written: 1 MiB.</summary>
    public const int MaxHistoryBytes = 1024 * 1024;

    /// <summary>Largest serialized record: 8 KiB. A larger record is not stored.</summary>
    public const int MaxRecordBytes = 8 * 1024;

    /// <summary>Most distinct diagnostic codes kept per record.</summary>
    public const int MaxDiagnosticCodesPerRecord = 32;

    /// <summary>The history file name.</summary>
    public const string FileName = "history.json";

    private readonly PluginRoot _directory;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeProvider _time;
    private readonly RunHistoryTestHooks? _hooks;

    /// <summary>Creates a history kept in <paramref name="directoryPath"/>, which is created when the first record is written.</summary>
    /// <param name="directoryPath">A fully qualified local directory path with normalized segments (the <see cref="PluginRoot"/> syntax rules).</param>
    /// <param name="options">Settings; <see cref="PluginRunHistoryOptions.Default"/> when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> is not acceptable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The lock timeout is out of range.</exception>
    public PluginRunHistory(string directoryPath, PluginRunHistoryOptions? options = null)
        : this(directoryPath, options, TimeProvider.System, hooks: null)
    {
    }

    internal PluginRunHistory(string directoryPath, PluginRunHistoryOptions? options, TimeProvider timeProvider, RunHistoryTestHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= PluginRunHistoryOptions.Default;
        if (options.LockTimeout < TimeSpan.Zero || options.LockTimeout > PluginRunHistoryOptions.MaxLockTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The lock timeout must be between zero and PluginRunHistoryOptions.MaxLockTimeout.");
        }

        if (!PluginRoot.TryCreate(directoryPath, out PluginRoot? directory))
        {
            throw new ArgumentException("The history directory must be a fully qualified path on a local drive, with normalized segments.", nameof(directoryPath));
        }

        _directory = directory;
        _lockTimeout = options.LockTimeout;
        _time = timeProvider;
        _hooks = hooks;
    }

    /// <summary>The normalized history directory.</summary>
    public string DirectoryPath => _directory.Value;

    /// <summary>The history file path.</summary>
    public string FilePath => StorageFiles.Combine(_directory, FileName);

    private string LockPath => FilePath + ".lock";

    /// <summary>
    /// The per-user default location, <c>%LOCALAPPDATA%\TrayWisp\diagnostics</c>, next to the default trust store
    /// directory. There is no fallback when the folder is unavailable.
    /// </summary>
    /// <exception cref="InvalidOperationException">The local application data folder is not available.</exception>
    public static string GetDefaultDirectory()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(local) || !Path.IsPathFullyQualified(local))
        {
            throw new InvalidOperationException("The per-user local application data folder is not available, so there is no default history location.");
        }

        return Path.Combine(local, "TrayWisp", "diagnostics");
    }

    /// <summary>
    /// Adds a summary of <paramref name="result"/>, a run of a plugin under <paramref name="rootPath"/>, to the history.
    /// </summary>
    /// <param name="rootPath">The plugin root the run used. Stored only as a <see cref="PluginRunRecord.RootId"/>.</param>
    /// <param name="result">The run.</param>
    /// <param name="publication">What the host did with the run's output.</param>
    /// <param name="publicationTrustStatus">The status of the host's trust check after the run, if it made one.</param>
    /// <param name="cancellationToken">Cancels the operation until the new history starts being published.</param>
    /// <exception cref="OperationCanceledException">The operation was cancelled before anything was written.</exception>
    public RunHistoryWriteResult Record(string rootPath, PluginExecutionResult result, RunPublication publication, PluginTrustStatus? publicationTrustStatus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return WriteFailed(RunHistoryWriteStatus.Unavailable, StorageFiles.Error(StorageDiagnosticCode.UnsupportedPlatform, "The run history is only supported on Windows."));
        }

        if (!PluginRoot.TryCreate(rootPath, out PluginRoot? root) || (result.Key is not null && result.Key.Root != root) || !PluginTrustKey.IsSupportedPluginId(result.PluginId))
        {
            return WriteFailed(RunHistoryWriteStatus.Rejected, StorageFiles.Error(StorageDiagnosticCode.RecordRejected, "The run was not recorded: its plugin root or identity is not supported, or the root does not match the run."));
        }

        if (root.Overlaps(_directory))
        {
            return WriteFailed(
                RunHistoryWriteStatus.Rejected,
                StorageFiles.Error(StorageDiagnosticCode.RecordRejected, $"The run was not recorded: the plugin root {TrustPaths.Quote(root.Value)} overlaps the history directory {TrustPaths.Quote(_directory.Value)}."));
        }

        if (publication == RunPublication.Published && !result.Succeeded)
        {
            throw new ArgumentException("Only a successful run can have published output.", nameof(publication));
        }

        PluginRunRecord record = CreateRecord(root, result, publication, publicationTrustStatus, _time.GetUtcNow());
        byte[] serialized = RunHistorySerializer.SerializeRecord(record);
        if (serialized.Length > MaxRecordBytes)
        {
            return WriteFailed(
                RunHistoryWriteStatus.Rejected,
                StorageFiles.Error(
                    StorageDiagnosticCode.RecordTooLarge,
                    string.Create(CultureInfo.InvariantCulture, $"The run was not recorded: its record would take {serialized.Length} bytes, more than the limit of {MaxRecordBytes}.")));
        }

        return RecordOnWindows(serialized, cancellationToken);
    }

    /// <summary>Reads the most recent <paramref name="maxRecords"/> records, oldest first. Never creates, repairs or clears anything.</summary>
    /// <remarks>
    /// The read holds the history lock, which it opens but never creates: an existing history whose lock file is missing
    /// is reported as <see cref="RunHistoryReadStatus.Unavailable"/> (<see cref="StorageDiagnosticCode.HistoryLockFailed"/>)
    /// and not read. The next <see cref="Record"/> creates the lock file again.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRecords"/> is not between 0 and <see cref="MaxRecords"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public RunHistoryReadResult Read(int maxRecords, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRecords);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRecords, MaxRecords);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ReadFailed(RunHistoryReadStatus.Unavailable, StorageFiles.Error(StorageDiagnosticCode.UnsupportedPlatform, "The run history is only supported on Windows."));
        }

        StorageDiagnostic? problem = StorageFiles.OpenDirectory(_directory, createMissing: false, "history", out SafeFileHandle? held);
        if (problem is not null)
        {
            return problem.Code == StorageDiagnosticCode.StorageDirectoryMissing ? NoHistory() : ReadFailed(RunHistoryReadStatus.Unavailable, problem);
        }

        using (held)
        {
            // No history file: nothing to lock, and nothing (not even a lock file) is created.
            using (SafeFileHandle? probe = WindowsFileApi.Open(FilePath, WindowsFileApi.OpenMode.Inspect, out int error))
            {
                if (probe is null && error is WindowsFileApi.ErrorFileNotFound or WindowsFileApi.ErrorPathNotFound)
                {
                    return NoHistory();
                }
            }
        }

        problem = AcquireLock(createMissing: false, cancellationToken, out HistoryLock? historyLock);
        if (problem is not null)
        {
            return problem.Code == StorageDiagnosticCode.StorageDirectoryMissing ? NoHistory() : ReadFailed(RunHistoryReadStatus.Unavailable, problem);
        }

        using (historyLock)
        {
            _hooks?.Locked?.Invoke();
            problem = ReadFile(out List<PluginRunRecord>? records, out bool exists);
            if (problem is not null)
            {
                RunHistoryReadStatus status = problem.Code switch
                {
                    StorageDiagnosticCode.HistoryCorrupt or StorageDiagnosticCode.HistoryTooLarge or StorageDiagnosticCode.HistoryTooManyRecords => RunHistoryReadStatus.Corrupt,
                    StorageDiagnosticCode.HistoryUnsupportedVersion => RunHistoryReadStatus.UnsupportedVersion,
                    _ => RunHistoryReadStatus.Unavailable,
                };
                return ReadFailed(status, problem);
            }

            if (!exists)
            {
                return NoHistory();
            }

            ImmutableArray<PluginRunRecord> recent = [.. records!.Skip(Math.Max(0, records!.Count - maxRecords))];
            return new RunHistoryReadResult(records.Count == 0 ? RunHistoryReadStatus.Empty : RunHistoryReadStatus.Loaded, recent, records.Count, diagnostic: null);
        }
    }

    internal static PluginRunRecord CreateRecord(PluginRoot root, PluginExecutionResult result, RunPublication publication, PluginTrustStatus? publicationTrustStatus, DateTimeOffset now)
    {
        // Distinct codes in first-seen order: execution, then trust, then output. Messages are never kept.
        var codes = new List<RecordedDiagnostic>();
        var seen = new HashSet<RecordedDiagnostic>();
        bool truncated = false;
        IEnumerable<RecordedDiagnostic> all = result.Diagnostics.Select(d => new RecordedDiagnostic((int)d.Code, d.Severity))
            .Concat(result.TrustDiagnostics.Select(d => new RecordedDiagnostic((int)d.Code, d.Severity)))
            .Concat(result.OutputDiagnostics.Select(d => new RecordedDiagnostic((int)d.Code, d.Severity)));
        foreach (RecordedDiagnostic diagnostic in all)
        {
            if (!seen.Add(diagnostic))
            {
                continue;
            }

            if (codes.Count == MaxDiagnosticCodesPerRecord)
            {
                truncated = true;
                break;
            }

            codes.Add(diagnostic);
        }

        return new PluginRunRecord(
            now.ToUniversalTime(),
            result.StartedAtUtc?.ToUniversalTime(),
            PluginRunRecord.ComputeRootId(root),
            result.PluginId,
            result.Status,
            result.TrustStatus,
            result.ExitCode,
            (long)result.Elapsed.TotalMilliseconds,
            result.TerminationReason,
            result.Cleanup.Status,
            result.Cleanup.JobObjectAssigned,
            result.Cleanup.RemainingJobProcesses,
            result.StandardOutputBytes,
            result.StandardOutputComplete,
            result.StandardOutputTruncated,
            result.StandardErrorBytes,
            result.StandardErrorTruncated,
            result.Interpreter?.Kind,
            result.Interpreter?.Source,
            publication,
            publicationTrustStatus,
            [.. codes],
            truncated);
    }

    [SupportedOSPlatform("windows")]
    private RunHistoryWriteResult RecordOnWindows(byte[] serialized, CancellationToken cancellationToken)
    {
        StorageDiagnostic? problem = AcquireLock(createMissing: true, cancellationToken, out HistoryLock? historyLock);
        if (problem is not null)
        {
            return WriteFailed(RunHistoryWriteStatus.Unavailable, problem);
        }

        using (historyLock)
        {
            _hooks?.Locked?.Invoke();
            problem = ReadFile(out List<PluginRunRecord>? existing, out bool exists);
            if (problem is not null)
            {
                // A corrupt, unsupported or unreadable history is never replaced: the new record is dropped instead.
                return WriteFailed(RunHistoryWriteStatus.Unavailable, problem);
            }

            var records = new List<byte[]>(existing!.Count + 1);
            records.AddRange(existing.Select(RunHistorySerializer.SerializeRecord));
            records.Add(serialized);
            int evicted = 0;
            while (records.Count > MaxRecords || RunHistorySerializer.TotalSize(records.Select(r => r.Length)) > MaxHistoryBytes)
            {
                records.RemoveAt(0);
                evicted++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            problem = Publish(historyLock!, RunHistorySerializer.Serialize(records), exists);
            if (problem is not null)
            {
                return WriteFailed(RunHistoryWriteStatus.Unavailable, problem);
            }

            ImmutableArray<StorageDiagnostic> diagnostics = evicted == 0
                ? []
                : [new StorageDiagnostic(
                    PluginDiagnosticSeverity.Info,
                    StorageDiagnosticCode.RecordsEvicted,
                    string.Create(CultureInfo.InvariantCulture, $"{evicted} of the oldest run records were removed to keep the history within {MaxRecords} records and {MaxHistoryBytes} bytes."))];
            return new RunHistoryWriteResult(RunHistoryWriteStatus.Recorded, evicted, diagnostics);
        }
    }

    // Validates (and, for writing, creates) the directory chain and holds the directory, then takes the exclusive lock file.
    // Only a writer (createMissing) creates the lock file; a reader opens an existing one or fails.
    [SupportedOSPlatform("windows")]
    private StorageDiagnostic? AcquireLock(bool createMissing, CancellationToken cancellationToken, out HistoryLock? held)
    {
        held = null;
        cancellationToken.ThrowIfCancellationRequested();
        StorageDiagnostic? problem = StorageFiles.OpenDirectory(_directory, createMissing, "history", out SafeFileHandle? directory);
        if (problem is not null)
        {
            return problem;
        }

        SafeFileHandle? lockFile = null;
        try
        {
            problem = WaitForLock(createMissing, cancellationToken, out lockFile) ?? StorageFiles.CheckFile(lockFile!, LockPath, "history lock file");
            if (problem is null)
            {
                held = new HistoryLock(directory!, lockFile!);
                return null;
            }

            return problem;
        }
        finally
        {
            if (held is null)
            {
                lockFile?.Dispose();
                directory!.Dispose();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private StorageDiagnostic? WaitForLock(bool createMissing, CancellationToken cancellationToken, out SafeFileHandle? lockFile)
    {
        long start = Stopwatch.GetTimestamp();
        int delayMilliseconds = 10;
        WindowsFileApi.OpenMode mode = createMissing ? WindowsFileApi.OpenMode.Lock : WindowsFileApi.OpenMode.LockExisting;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lockFile = WindowsFileApi.Open(LockPath, mode, out int error);
            if (lockFile is not null)
            {
                return null;
            }

            if (!createMissing && error is WindowsFileApi.ErrorFileNotFound)
            {
                // Every writer creates the lock file before its first write, so it was removed by something else. A
                // reader never creates it and never reads without it.
                return StorageFiles.Error(
                    StorageDiagnosticCode.HistoryLockFailed,
                    $"The run history exists, but its lock file {TrustPaths.Quote(LockPath)} is missing, so the history was not read. Reading never creates the lock file; the next recorded run creates it again.");
            }

            if (error is not (WindowsFileApi.ErrorSharingViolation or WindowsFileApi.ErrorLockViolation))
            {
                return StorageFiles.Error(
                    StorageDiagnosticCode.HistoryLockFailed,
                    $"The history lock file {TrustPaths.Quote(LockPath)} could not be opened: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed >= _lockTimeout)
            {
                return StorageFiles.Error(
                    StorageDiagnosticCode.HistoryLockTimeout,
                    string.Create(CultureInfo.InvariantCulture, $"The run history is in use by another TrayWisp operation; its lock was not acquired within {_lockTimeout.TotalMilliseconds:0} ms."));
            }

            TimeSpan wait = TimeSpan.FromMilliseconds(Math.Min(delayMilliseconds, (_lockTimeout - elapsed).TotalMilliseconds));
            if (cancellationToken.WaitHandle.WaitOne(wait))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            delayMilliseconds = Math.Min(delayMilliseconds * 2, 100);
        }
    }

    // Reads and strictly parses the history file. Never reads more than MaxHistoryBytes + 1 bytes, whatever the file
    // length says.
    [SupportedOSPlatform("windows")]
    private StorageDiagnostic? ReadFile(out List<PluginRunRecord>? records, out bool exists)
    {
        records = null;
        exists = false;
        SafeFileHandle? handle = WindowsFileApi.Open(FilePath, WindowsFileApi.OpenMode.ReadStore, out int error);
        if (handle is null)
        {
            if (error is WindowsFileApi.ErrorFileNotFound)
            {
                records = [];
                return null;
            }

            return StorageFiles.Error(
                error == WindowsFileApi.ErrorAccessDenied && Directory.Exists(FilePath) ? StorageDiagnosticCode.StorageFileUnsafe : StorageDiagnosticCode.HistoryUnreadable,
                $"The run history {TrustPaths.Quote(FilePath)} could not be opened: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        using (handle)
        {
            exists = true;
            StorageDiagnostic? unsafeFile = StorageFiles.CheckFile(handle, FilePath, "run history");
            if (unsafeFile is not null)
            {
                return unsafeFile;
            }

            byte[] content = new byte[MaxHistoryBytes + 1];
            int total = 0;
            try
            {
                int read;
                while (total < content.Length && (read = RandomAccess.Read(handle, content.AsSpan(total), total)) > 0)
                {
                    total += read;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return StorageFiles.Error(StorageDiagnosticCode.HistoryUnreadable, $"The run history {TrustPaths.Quote(FilePath)} could not be read: {TrustPaths.Quote(exception.Message)}");
            }

            if (total > MaxHistoryBytes)
            {
                return StorageFiles.Error(
                    StorageDiagnosticCode.HistoryTooLarge,
                    $"The run history {TrustPaths.Quote(FilePath)} is larger than {MaxHistoryBytes} bytes. It was not changed; it is never repaired or overwritten automatically.");
            }

            StorageDiagnostic? problem = RunHistorySerializer.Parse(content.AsSpan(0, total), out List<PluginRunRecord> parsed);
            if (problem is not null)
            {
                return problem;
            }

            records = parsed;
            return null;
        }
    }

    // Writes a new file in the same directory (created exclusively, checked through its handle, flushed), re-checks the
    // held directory, and renames it over the history. The live history is never truncated, overwritten in place or
    // deleted. On failure the previous history is unchanged and the temporary file is deleted.
    [SupportedOSPlatform("windows")]
    private StorageDiagnostic? Publish(HistoryLock held, byte[] content, bool replace)
    {
        if (content.Length > MaxHistoryBytes)
        {
            return StorageFiles.Error(StorageDiagnosticCode.HistoryTooLarge, $"The run history would become larger than {MaxHistoryBytes} bytes.");
        }

        string temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        bool created = false;
        bool published = false;
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 0,
            }))
            {
                created = true;
                StorageDiagnostic? unsafeTemporary = StorageFiles.CheckFile(stream.SafeFileHandle, temporary, "temporary history file");
                if (unsafeTemporary is not null)
                {
                    return unsafeTemporary;
                }

                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            _hooks?.BeforePublish?.Invoke(temporary);
            StorageDiagnostic? moved = StorageFiles.CheckHeldDirectory(held.Directory, _directory, "history");
            if (moved is not null)
            {
                return moved;
            }

            if (!WindowsFileApi.TryMove(temporary, FilePath, replace, out int error))
            {
                return WriteError(WindowsFileApi.Describe(error));
            }

            published = true;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WriteError(exception.Message);
        }
        finally
        {
            if (created && !published)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Left behind with a unique name; it is never read.
                }
            }
        }
    }

    private StorageDiagnostic WriteError(string detail) =>
        StorageFiles.Error(StorageDiagnosticCode.HistoryWriteFailed, $"The run history {TrustPaths.Quote(FilePath)} could not be updated: {TrustPaths.Quote(detail)}. The previous history is unchanged.");

    private RunHistoryReadResult NoHistory() =>
        new(RunHistoryReadStatus.NoHistory, [], 0, new StorageDiagnostic(PluginDiagnosticSeverity.Info, StorageDiagnosticCode.HistoryNotFound, $"No run history exists in {TrustPaths.Quote(_directory.Value)} yet."));

    private static RunHistoryReadResult ReadFailed(RunHistoryReadStatus status, StorageDiagnostic diagnostic) => new(status, [], 0, diagnostic);

    private static RunHistoryWriteResult WriteFailed(RunHistoryWriteStatus status, StorageDiagnostic diagnostic) => new(status, 0, [diagnostic]);

    // The history lock of one operation: the held directory and the exclusive lock file, released together.
    [SupportedOSPlatform("windows")]
    private sealed class HistoryLock(SafeFileHandle directory, SafeFileHandle lockFile) : IDisposable
    {
        public SafeFileHandle Directory { get; } = directory;

        public void Dispose()
        {
            lockFile.Dispose();
            Directory.Dispose();
        }
    }
}

// Deterministic seams for tests of races and failures. Never set in production.
internal sealed class RunHistoryTestHooks
{
    // After the history lock has been acquired, before the history file is read (by Read, or by Record for an update).
    public Action? Locked { get; init; }

    // After the new history has been written and flushed to its temporary file, before it is published.
    public Action<string>? BeforePublish { get; init; }
}
