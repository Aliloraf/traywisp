using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;

namespace TrayWisp.Runtime.Trust;

// The approvals read from the store under the lock, and changed in memory before being written back.
internal sealed class ApprovalSnapshot
{
    private readonly Dictionary<PluginTrustKey, PluginApproval> _approvals;

    public ApprovalSnapshot(IEnumerable<PluginApproval> approvals, bool exists)
    {
        _approvals = approvals.ToDictionary(approval => approval.Key);
        Exists = exists;
    }

    // Whether the store file existed when it was read: decides between replacing it and creating it.
    public bool Exists { get; }

    public int Count => _approvals.Count;

    public IEnumerable<PluginApproval> Approvals => _approvals.Values;

    public PluginApproval? Find(PluginTrustKey key) => _approvals.GetValueOrDefault(key);

    public bool ContainsGeneration(ApprovalGeneration generation) => _approvals.Values.Any(approval => approval.Generation == generation);

    public void Set(PluginApproval approval)
    {
        _approvals.Remove(approval.Key);
        _approvals.Add(approval.Key, approval);
    }

    public bool Remove(PluginTrustKey key) => _approvals.Remove(key);
}

// The trust store lock held by one operation: the exclusive lock file, and the validated store directory,
// kept open (denying its deletion and renaming) until the operation ends. Disposing releases both.
[SupportedOSPlatform("windows")]
internal sealed class StoreLock(SafeFileHandle directory, SafeFileHandle lockFile) : IDisposable
{
    public SafeFileHandle Directory { get; } = directory;

    public SafeFileHandle LockFile { get; } = lockFile;

    public void Dispose()
    {
        LockFile.Dispose();
        Directory.Dispose();
    }
}

// The versioned JSON approval store: a lock file for read-modify-write synchronization between threads
// and processes, strict reads, and atomic replacement through a same-directory temporary file.
[SupportedOSPlatform("windows")]
internal sealed class ApprovalStore(string filePath, PluginRoot directory, TrustTestHooks? hooks)
{
    public string FilePath { get; } = filePath;

    public PluginRoot Directory { get; } = directory;

    public string LockPath { get; } = filePath + ".lock";

    /// <summary>
    /// Validates the store directory (creating missing levels) and keeps it open, then acquires the lock file
    /// exclusively, retrying while another holder has it, until <paramref name="timeout"/> elapses. On success
    /// <paramref name="held"/> owns both handles; on failure or cancellation both are already released.
    /// </summary>
    public TrustFailure? AcquireLock(TimeSpan timeout, CancellationToken cancellationToken, out StoreLock? held)
    {
        held = null;
        cancellationToken.ThrowIfCancellationRequested();

        // Case-sensitive ancestors are rejected: there, differently cased spellings of the store path could
        // name different directories, and so different stores and lock files.
        PathCheck check = TrustPaths.OpenDirectoryChain(Directory, rejectCaseSensitiveAncestors: true, createMissing: true, hold: true, out SafeFileHandle? directoryHandle);
        if (check.Problem != PathProblem.None)
        {
            return DirectoryFailure(check);
        }

        SafeFileHandle? lockHandle = null;
        StoreLock? acquired = null;
        try
        {
            TrustFailure? failure = CheckCaseInsensitive(directoryHandle!)
                ?? WaitForLock(timeout, cancellationToken, out lockHandle)
                ?? CheckStoreFile(lockHandle!, LockPath, "lock file");
            if (failure is not null)
            {
                return failure;
            }

            hooks?.StoreLocked?.Invoke();
            acquired = new StoreLock(directoryHandle!, lockHandle!);
            held = acquired;
            return null;
        }
        finally
        {
            if (acquired is null)
            {
                lockHandle?.Dispose();
                directoryHandle!.Dispose();
            }
        }
    }

    private TrustFailure? WaitForLock(TimeSpan timeout, CancellationToken cancellationToken, out SafeFileHandle? lockHandle)
    {
        lockHandle = null;
        long start = Stopwatch.GetTimestamp();
        int delayMilliseconds = 10;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lockHandle = WindowsFileApi.Open(LockPath, WindowsFileApi.OpenMode.Lock, out int error);
            if (lockHandle is not null)
            {
                return null;
            }

            if (error is not (WindowsFileApi.ErrorSharingViolation or WindowsFileApi.ErrorLockViolation))
            {
                return TrustFailure.Store(
                    TrustDiagnosticCode.StoreLockFailed,
                    $"The trust store lock file {TrustPaths.Quote(LockPath)} could not be opened: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed >= timeout)
            {
                return TrustFailure.Store(
                    TrustDiagnosticCode.StoreLockTimeout,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The trust store is in use by another TrayWisp operation; its lock was not acquired within {timeout.TotalMilliseconds:0} ms."));
            }

            TimeSpan wait = TimeSpan.FromMilliseconds(Math.Min(delayMilliseconds, (timeout - elapsed).TotalMilliseconds));
            if (cancellationToken.WaitHandle.WaitOne(wait))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            delayMilliseconds = Math.Min(delayMilliseconds * 2, 100);
        }
    }

    /// <summary>Reads the whole store. Must be called with the lock held. A missing store is an empty one.</summary>
    public TrustFailure? Read(out ApprovalSnapshot? snapshot)
    {
        snapshot = null;
        SafeFileHandle? handle = WindowsFileApi.Open(FilePath, WindowsFileApi.OpenMode.ReadStore, out int error);
        if (handle is null)
        {
            if (error == WindowsFileApi.ErrorFileNotFound)
            {
                snapshot = new ApprovalSnapshot([], exists: false);
                return null;
            }

            if (error == WindowsFileApi.ErrorAccessDenied && System.IO.Directory.Exists(FilePath))
            {
                return TrustFailure.Store(TrustDiagnosticCode.StorePathUnsafe, $"The trust store path {TrustPaths.Quote(FilePath)} is a directory.");
            }

            return TrustFailure.Store(
                TrustDiagnosticCode.StoreUnreadable,
                $"The trust store {TrustPaths.Quote(FilePath)} could not be opened: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        using (handle)
        {
            TrustFailure? unsafeStore = CheckStoreFile(handle, FilePath, "trust store");
            if (unsafeStore is not null)
            {
                return unsafeStore;
            }

            byte[] content;
            try
            {
                long length = RandomAccess.GetLength(handle);
                if (length > PluginTrustService.MaxStoreBytes)
                {
                    return TooLarge();
                }

                // One spare byte detects a file that grew after its length was read.
                content = new byte[length + 1];
                int total = 0;
                int read;
                while (total < content.Length && (read = RandomAccess.Read(handle, content.AsSpan(total), total)) > 0)
                {
                    total += read;
                }

                if (total != length)
                {
                    return TrustFailure.Store(TrustDiagnosticCode.StoreUnreadable, $"The trust store {TrustPaths.Quote(FilePath)} changed while it was being read.");
                }

                content = content[..total];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return TrustFailure.Store(
                    TrustDiagnosticCode.StoreUnreadable,
                    $"The trust store {TrustPaths.Quote(FilePath)} could not be read: {TrustPaths.Quote(exception.Message)}");
            }

            TrustDiagnostic? problem = ApprovalStoreSerializer.Parse(content, out List<PluginApproval> approvals);
            if (problem is not null)
            {
                return new TrustFailure(TrustFailureKind.StoreUnavailable, problem);
            }

            snapshot = new ApprovalSnapshot(approvals, exists: true);
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/> to a new temporary file in the store directory, flushes it, checks
    /// that the held store directory is still at its configured path, and renames the temporary file over the
    /// store (or creates the store, when it did not exist). On failure the previous store is unchanged and the
    /// temporary file is removed.
    /// </summary>
    public TrustFailure? Write(StoreLock held, ApprovalSnapshot snapshot)
    {
        if (snapshot.Count > PluginTrustService.MaxApprovals)
        {
            return TrustFailure.Store(
                TrustDiagnosticCode.StoreTooManyRecords,
                $"The trust store would have more than {PluginTrustService.MaxApprovals} approval records.");
        }

        byte[] content = ApprovalStoreSerializer.Serialize(snapshot.Approvals);
        if (content.Length > PluginTrustService.MaxStoreBytes)
        {
            return TooLarge();
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
                TrustFailure? unsafeTemporary = CheckStoreFile(stream.SafeFileHandle, temporary, "temporary trust store file");
                if (unsafeTemporary is not null)
                {
                    return unsafeTemporary;
                }

                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            hooks?.BeforeStorePublish?.Invoke(temporary);
            TrustFailure? moved = CheckHeldDirectory(held);
            if (moved is not null)
            {
                return moved;
            }

            if (!WindowsFileApi.TryMove(temporary, FilePath, replaceExisting: snapshot.Exists, out int error))
            {
                return WriteFailed(WindowsFileApi.Describe(error));
            }

            published = true;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WriteFailed(exception.Message);
        }
        finally
        {
            if (created && !published)
            {
                TryDelete(temporary);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind; it has a unique name and is never read.
        }
    }

    // A store, lock or temporary file must be a regular local file with exactly one name, at its configured
    // path. A hard link would give the file a second name outside TrayWisp's control (and could make the lock
    // file and the store one file).
    private static TrustFailure? CheckStoreFile(SafeFileHandle handle, string path, string what)
    {
        TrustFailure? failure = CheckRegularFile(handle, path, what);
        if (failure is not null)
        {
            return failure;
        }

        if (!WindowsFileApi.TryGetStandardInfo(handle, out bool deletePending, out uint links, out int error))
        {
            return TrustFailure.Store(TrustDiagnosticCode.StoreUnreadable, $"The {what} {TrustPaths.Quote(path)} could not be examined: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        if (deletePending)
        {
            return TrustFailure.Store(TrustDiagnosticCode.StoreUnreadable, $"The {what} {TrustPaths.Quote(path)} is being deleted.");
        }

        if (links != 1)
        {
            return TrustFailure.Store(
                TrustDiagnosticCode.StorePathUnsafe,
                string.Create(CultureInfo.InvariantCulture, $"The {what} {TrustPaths.Quote(path)} has {links} hard links. It must be an ordinary file with a single name."));
        }

        PathCheck check = TrustPaths.CheckFinalPath(handle, path);
        return check.Problem switch
        {
            PathProblem.None => null,
            PathProblem.Inaccessible => TrustFailure.Store(TrustDiagnosticCode.StoreUnreadable, $"The {what} {TrustPaths.Quote(path)}: {TrustPaths.Quote(check.Detail ?? string.Empty)}"),
            _ => TrustFailure.Store(
                TrustDiagnosticCode.StorePathUnsafe,
                $"The {what} {TrustPaths.Quote(path)} resolves to {TrustPaths.Quote(check.Detail ?? string.Empty)} instead of its configured path."),
        };
    }

    // In a case-sensitive store directory, differently cased store paths would name different stores with
    // different lock files, so operations using them would not be synchronized.
    private TrustFailure? CheckCaseInsensitive(SafeFileHandle directoryHandle)
    {
        if (!WindowsFileApi.TryIsCaseSensitiveDirectory(directoryHandle, out bool caseSensitive, out int error))
        {
            return TrustFailure.Store(
                TrustDiagnosticCode.StoreUnreadable,
                $"The trust store directory {TrustPaths.Quote(Directory.Value)} could not be examined: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        return caseSensitive
            ? TrustFailure.Store(
                TrustDiagnosticCode.StorePathUnsafe,
                $"The trust store directory {TrustPaths.Quote(Directory.Value)} is case-sensitive, so the store path does not identify a single store and lock file.")
            : null;
    }

    // Immediately before publication: the held store directory is still an ordinary directory at its
    // configured path (an ancestor was not renamed or replaced), so the rename targets the validated directory.
    private TrustFailure? CheckHeldDirectory(StoreLock held)
    {
        string? problem = null;
        if (!WindowsFileApi.TryGetAttributes(held.Directory, out FileAttributes attributes, out int error))
        {
            problem = WindowsFileApi.Describe(error);
        }
        else if ((attributes & (FileAttributes.ReparsePoint | PluginDiscovery.RemoteStorage)) != 0)
        {
            problem = "it became a reparse point or remote entry";
        }
        else
        {
            PathCheck check = TrustPaths.CheckFinalPath(held.Directory, Directory.Value);
            if (check.Problem != PathProblem.None)
            {
                problem = check.Detail ?? check.Problem.ToString();
            }
        }

        return problem is null
            ? null
            : TrustFailure.Store(
                TrustDiagnosticCode.StorePathUnsafe,
                $"The trust store directory {TrustPaths.Quote(Directory.Value)} was moved or changed during the update ({TrustPaths.Quote(problem)}). The store was not updated.");
    }

    private static TrustFailure? CheckRegularFile(SafeFileHandle handle, string path, string what)
    {
        if (!WindowsFileApi.TryGetAttributes(handle, out FileAttributes attributes, out int error))
        {
            return TrustFailure.Store(TrustDiagnosticCode.StoreUnreadable, $"The {what} {TrustPaths.Quote(path)} could not be examined: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | PluginDiscovery.RemoteStorage)) != 0 || !WindowsFileApi.IsDiskFile(handle))
        {
            return TrustFailure.Store(
                TrustDiagnosticCode.StorePathUnsafe,
                $"The {what} {TrustPaths.Quote(path)} is not a regular local file (it is a link, directory, or offline or remote entry).");
        }

        return null;
    }

    private TrustFailure DirectoryFailure(PathCheck check)
    {
        string where = $"{TrustPaths.Quote(check.Path)} (trust store directory {TrustPaths.Quote(Directory.Value)})";
        return check.Problem switch
        {
            PathProblem.Inaccessible or PathProblem.NotFound => TrustFailure.Store(
                TrustDiagnosticCode.StoreUnreadable,
                $"{where} could not be created or opened: {TrustPaths.Quote(check.Detail ?? "it does not exist")}"),
            PathProblem.Alias => TrustFailure.Store(
                TrustDiagnosticCode.StorePathUnsafe,
                $"{where} resolves to {TrustPaths.Quote(check.Detail ?? string.Empty)}; configure that path instead."),
            PathProblem.CaseSensitive => TrustFailure.Store(
                TrustDiagnosticCode.StorePathUnsafe,
                $"{where} is a case-sensitive directory, so the store path does not identify a single directory."),
            _ => TrustFailure.Store(
                TrustDiagnosticCode.StorePathUnsafe,
                $"{where} is not an ordinary local directory: it is a link or other reparse point, offline or remote, not a directory, or on an unsupported drive."),
        };
    }

    private static TrustFailure TooLarge() =>
        TrustFailure.Store(TrustDiagnosticCode.StoreTooLarge, $"The trust store is, or would become, larger than {PluginTrustService.MaxStoreBytes} bytes.");

    private TrustFailure WriteFailed(string detail) =>
        TrustFailure.Store(
            TrustDiagnosticCode.StoreWriteFailed,
            $"The trust store {TrustPaths.Quote(FilePath)} could not be updated: {TrustPaths.Quote(detail)}. The previous store is unchanged.");
}
