using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;

namespace TrayWisp.Runtime.Trust;

// A successful inspection, holding the plugin file and the directories on its path open.
[SupportedOSPlatform("windows")]
internal sealed class InspectedPlugin(PluginFingerprint plugin, string filePath, List<SafeFileHandle> handles) : IDisposable
{
    public PluginFingerprint Plugin { get; } = plugin;

    // The final path of the hashed file, read from its open handle after hashing: an absolute path on a
    // local drive with the on-disk spelling (including case) of every directory and of the file name.
    public string FilePath { get; } = filePath;

    public void Dispose() => Close(handles);

    internal static void Close(List<SafeFileHandle> handles)
    {
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            handles[i].Dispose();
        }

        handles.Clear();
    }
}

// Validates one plugin's root and path and fingerprints its content. See docs/trust-model.md
// ("Filesystem validation" and "Fingerprinting") for the rules this implements.
[SupportedOSPlatform("windows")]
internal sealed class PluginFileInspector(TrustTestHooks? hooks)
{
    // Entries enumerated across all directory listings of one validation pass: the default discovery
    // budget, so a plugin that default discovery can list can also be validated.
    internal static readonly int MaxEntries = PluginDiscoveryOptions.Default.MaxEntries;

    private const int ChunkSize = 64 * 1024;

    private readonly int _maxEntries = hooks?.MaxDirectoryEntries ?? MaxEntries;

    /// <summary>
    /// Validates and fingerprints the plugin. On success, <paramref name="result"/> keeps the plugin file
    /// (denying writes, deletion and renaming) and every directory inside the root (denying deletion and
    /// renaming) open until it is disposed, so callers can make and save their decision while the inspected
    /// state is pinned.
    /// </summary>
    public TrustFailure? Inspect(PluginTrustKey key, CancellationToken cancellationToken, out InspectedPlugin? result)
    {
        result = null;
        var held = new List<SafeFileHandle>();
        bool transferred = false;
        try
        {
            PathCheck rootCheck = TrustPaths.OpenDirectoryChain(key.Root, rejectCaseSensitiveAncestors: true, createMissing: false, hold: true, out SafeFileHandle? rootHandle);
            if (rootCheck.Problem != PathProblem.None)
            {
                return RootFailure(key.Root, rootCheck);
            }

            held.Add(rootHandle!);
            string[] segments = key.Id.Value.Split('/');
            string[] names = new string[segments.Length];
            int budget = _maxEntries;
            string path = key.Root.Value;
            SafeFileHandle file = rootHandle!;

            // Pre-open validation, one level at a time: list the (held) parent, find exactly one ordinary entry
            // of the right kind, open it without following links, check the opened entry, hold it.
            for (int i = 0; i < segments.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool isFile = i == segments.Length - 1;
                string relative = string.Join('/', segments, 0, i + 1);
                string? parentRelative = i == 0 ? null : string.Join('/', segments, 0, i);

                TrustFailure? failure = FindEntry(path, parentRelative, segments[i], relative, isFile, ref budget, cancellationToken, out names[i]);
                if (failure is not null)
                {
                    return failure;
                }

                path = Combine(path, names[i]);
                SafeFileHandle? handle = WindowsFileApi.Open(path, isFile ? WindowsFileApi.OpenMode.ReadFileExclusive : WindowsFileApi.OpenMode.HoldDirectory, out int error);
                if (handle is null)
                {
                    return OpenFailure(error, isFile, relative);
                }

                held.Add(handle);
                failure = CheckOpenedEntry(handle, isFile, relative);
                if (failure is not null)
                {
                    return failure;
                }

                file = handle;
            }

            string onDiskRelative = string.Join('\\', names);
            TrustFailure? problem = CheckFilePath(file, path, onDiskRelative, key.Id.Value, out string openedPath);
            if (problem is not null)
            {
                return problem;
            }

            hooks?.PluginOpened?.Invoke();

            problem = Hash(file, key.Id.Value, cancellationToken, out ContentFingerprint? fingerprint, out long length);
            if (problem is not null)
            {
                return problem;
            }

            // Post-read validation: the file is still the same kind of entry at exactly the same final path, the
            // root still resolves to itself, and a fresh listing of every directory on the path still finds the
            // same, unambiguous names.
            string finalPath = string.Empty;
            problem = CheckOpenedEntry(file, isFile: true, key.Id.Value)
                ?? CheckFilePath(file, path, onDiskRelative, key.Id.Value, out finalPath)
                ?? RootChanged(rootHandle!, key.Root);
            if (problem is not null)
            {
                return problem;
            }

            if (!string.Equals(finalPath, openedPath, StringComparison.Ordinal))
            {
                return Changed(key.Id.Value);
            }

            budget = _maxEntries;
            path = key.Root.Value;
            for (int i = 0; i < segments.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = string.Join('/', segments, 0, i + 1);
                string? parentRelative = i == 0 ? null : string.Join('/', segments, 0, i);
                problem = FindEntry(path, parentRelative, segments[i], relative, isFile: i == segments.Length - 1, ref budget, cancellationToken, out string name);
                if (problem is not null)
                {
                    return problem;
                }

                if (!string.Equals(name, names[i], StringComparison.Ordinal))
                {
                    return Changed(relative);
                }

                path = Combine(path, name);
            }

            // The validated path comes from the file handle, never from the caller's spelling of the identity.
            result = new InspectedPlugin(new PluginFingerprint(key, fingerprint!, length), finalPath, held);
            transferred = true;
            return null;
        }
        finally
        {
            if (!transferred)
            {
                InspectedPlugin.Close(held);
            }
        }
    }

    internal static TrustFailure RootFailure(PluginRoot root, PathCheck check)
    {
        string where = string.Equals(check.Path, root.Value, StringComparison.OrdinalIgnoreCase)
            ? $"The plugin root {TrustPaths.Quote(root.Value)}"
            : $"The ancestor {TrustPaths.Quote(check.Path)} of the plugin root {TrustPaths.Quote(root.Value)}";
        return check.Problem switch
        {
            PathProblem.NotFound => TrustFailure.Unavailable(TrustDiagnosticCode.RootNotFound, $"{where} does not exist."),
            PathProblem.NotDirectory => TrustFailure.Unsafe(TrustDiagnosticCode.RootNotDirectory, $"{where} is not a directory."),
            PathProblem.ReparsePoint => TrustFailure.Unsafe(
                TrustDiagnosticCode.RootUnsafe,
                $"{where} is a symbolic link, junction, mount point or other reparse point."
                + (check.Detail is "cloud" ? " It appears to be synchronized by a cloud storage provider such as OneDrive; use a local folder." : string.Empty)),
            PathProblem.RemoteStorage => TrustFailure.Unsafe(TrustDiagnosticCode.RootUnsafe, $"{where} is offline or stored remotely."),
            PathProblem.CaseSensitive => TrustFailure.Unsafe(
                TrustDiagnosticCode.CaseSensitiveAncestor,
                $"{where} is a case-sensitive directory, so the root path does not identify a single directory."),
            PathProblem.Alias => TrustFailure.Unsafe(
                TrustDiagnosticCode.RootAlias,
                $"{where} resolves to {TrustPaths.Quote(check.Detail ?? string.Empty)}. Use that path instead of a short name, substituted drive or linked path."),
            PathProblem.Network => TrustFailure.Unsafe(TrustDiagnosticCode.RemoteRoot, $"{where} is on a network drive or resolves to a network path, which is not supported."),
            PathProblem.UnsupportedDrive => TrustFailure.Unsafe(TrustDiagnosticCode.RemoteRoot, $"{where} is not on a supported local fixed, removable or RAM drive."),
            _ => TrustFailure.Unavailable(TrustDiagnosticCode.RootInaccessible, $"{where} could not be examined: {TrustPaths.Quote(check.Detail ?? string.Empty)}"),
        };
    }

    // Lists one directory and finds the single ordinary entry of the expected kind whose name equals the
    // identity segment ignoring case, applying discovery's classification (PluginDiscovery.Classify) and its
    // collision rule: two such entries whose names differ only by case are both excluded.
    private TrustFailure? FindEntry(
        string directory,
        string? directoryRelative,
        string segment,
        string relative,
        bool isFile,
        ref int budget,
        CancellationToken cancellationToken,
        out string name)
    {
        name = string.Empty;
        PluginDiagnostic? listingError = PluginDiscovery.ListDirectory(directory, directoryRelative, budget, cancellationToken, out List<FileSystemInfo> entries, out bool truncated);
        budget -= entries.Count;
        string where = directoryRelative is null ? "the plugin root" : $"the directory {TrustPaths.Quote(directoryRelative)}";
        if (listingError is not null)
        {
            return TrustFailure.Unavailable(TrustDiagnosticCode.DirectoryUnreadable, $"While validating {TrustPaths.Quote(relative)}: {listingError.Message}");
        }

        if (truncated)
        {
            return TrustFailure.Unsafe(
                TrustDiagnosticCode.DirectoryLimitReached,
                $"While validating {TrustPaths.Quote(relative)}, listing {where} exceeded the limit of {_maxEntries} entries for all directories on the plugin's path, "
                + "so name collisions could not be ruled out.");
        }

        PluginDiscovery.EntryKind expected = isFile ? PluginDiscovery.EntryKind.Candidate : PluginDiscovery.EntryKind.Directory;
        FileSystemInfo? match = null;
        PluginDiscovery.EntryKind? otherKind = null;
        int participating = 0;
        foreach (FileSystemInfo entry in entries)
        {
            if (!string.Equals(entry.Name, segment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            PluginDiscovery.EntryKind kind = PluginDiscovery.Classify(entry, out _);
            if (kind == expected)
            {
                participating++;
                match = entry;
            }
            else
            {
                otherKind ??= kind;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (participating > 1)
        {
            return TrustFailure.Unsafe(
                TrustDiagnosticCode.IdentityCollision,
                $"{participating} {(isFile ? "files" : "directories")} in {where} have names that differ only by case from {TrustPaths.Quote(segment)}, "
                + "so the identity is ambiguous. Discovery excludes all of them.");
        }

        if (match is not null)
        {
            name = match.Name;
            return null;
        }

        string what = TrustPaths.Quote(relative);
        return otherKind switch
        {
            null => TrustFailure.Unavailable(TrustDiagnosticCode.PluginNotFound, $"{what} does not exist."),
            PluginDiscovery.EntryKind.ReparsePoint => TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is a symbolic link, junction or other reparse point, which is never followed."),
            PluginDiscovery.EntryKind.RemoteStorage => TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is offline or stored remotely; reading it could trigger a download."),
            PluginDiscovery.EntryKind.Hidden => TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is hidden, and discovery skips hidden entries."),
            PluginDiscovery.EntryKind.Directory => TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is a directory, not a plugin file."),
            _ when !isFile => TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is not a directory that discovery scans."),
            _ => TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is not a plugin file that discovery accepts."),
        };
    }

    private static TrustFailure OpenFailure(int error, bool isFile, string relative)
    {
        string what = TrustPaths.Quote(relative);
        return error switch
        {
            WindowsFileApi.ErrorFileNotFound or WindowsFileApi.ErrorPathNotFound =>
                TrustFailure.Unavailable(TrustDiagnosticCode.PluginNotFound, $"{what} was removed before it could be opened."),
            WindowsFileApi.ErrorSharingViolation or WindowsFileApi.ErrorLockViolation => isFile
                ? TrustFailure.Unavailable(TrustDiagnosticCode.FileLocked, $"{what} is in use by another program (for example open for writing), so it was not fingerprinted.")
                : TrustFailure.Unavailable(TrustDiagnosticCode.DirectoryUnreadable, $"{what} is in use by another program that may delete or rename it."),
            WindowsFileApi.ErrorAccessDenied => TrustFailure.Unavailable(
                isFile ? TrustDiagnosticCode.FileUnreadable : TrustDiagnosticCode.DirectoryUnreadable,
                $"Access to {what} was denied."),
            _ => TrustFailure.Unavailable(
                isFile ? TrustDiagnosticCode.FileUnreadable : TrustDiagnosticCode.DirectoryUnreadable,
                $"{what} could not be opened: {TrustPaths.Quote(WindowsFileApi.Describe(error))}"),
        };
    }

    // Checks the entry that was actually opened, through its handle.
    private static TrustFailure? CheckOpenedEntry(SafeFileHandle handle, bool isFile, string relative)
    {
        string what = TrustPaths.Quote(relative);
        if (!WindowsFileApi.TryGetAttributes(handle, out FileAttributes attributes, out int error))
        {
            return TrustFailure.Unavailable(
                isFile ? TrustDiagnosticCode.FileUnreadable : TrustDiagnosticCode.DirectoryUnreadable,
                $"The attributes of {what} could not be read: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is, or changed into, a link or other reparse point.");
        }

        if ((attributes & PluginDiscovery.RemoteStorage) != 0)
        {
            return TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is offline or stored remotely.");
        }

        if ((attributes & FileAttributes.Hidden) != 0)
        {
            return TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is hidden.");
        }

        if (((attributes & FileAttributes.Directory) != 0) == isFile)
        {
            return TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} changed between a file and a directory.");
        }

        if (!isFile)
        {
            return null;
        }

        if (!WindowsFileApi.IsDiskFile(handle))
        {
            return TrustFailure.Unsafe(TrustDiagnosticCode.PluginPathUnsafe, $"{what} is not a regular disk file.");
        }

        if (!WindowsFileApi.TryGetStandardInfo(handle, out bool deletePending, out _, out error))
        {
            return TrustFailure.Unavailable(TrustDiagnosticCode.FileUnreadable, $"{what} could not be examined: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        return deletePending ? TrustFailure.Unavailable(TrustDiagnosticCode.PluginNotFound, $"{what} is being deleted.") : null;
    }

    // The opened file's final path must be the expected path: the root part compared ignoring case (its
    // ancestors are not case-sensitive), the part inside the root exactly as listed.
    private static TrustFailure? CheckFilePath(SafeFileHandle file, string expected, string onDiskRelative, string relative, out string final)
    {
        PathCheck check = TrustPaths.CheckFinalPath(file, expected, out final);
        if (check.Problem == PathProblem.Inaccessible)
        {
            return TrustFailure.Unavailable(TrustDiagnosticCode.FileUnreadable, $"{TrustPaths.Quote(relative)}: {TrustPaths.Quote(check.Detail ?? string.Empty)}");
        }

        if (check.Problem != PathProblem.None)
        {
            return TrustFailure.Unsafe(
                TrustDiagnosticCode.PluginPathAlias,
                $"{TrustPaths.Quote(relative)} resolves to {TrustPaths.Quote(check.Detail ?? string.Empty)} instead of the expected path.");
        }

        return final.EndsWith(onDiskRelative, StringComparison.Ordinal) ? null : Changed(relative);
    }

    private static TrustFailure? RootChanged(SafeFileHandle rootHandle, PluginRoot root)
    {
        PathCheck check = TrustPaths.CheckFinalPath(rootHandle, root.Value);
        return check.Problem == PathProblem.None
            ? null
            : TrustFailure.Unavailable(TrustDiagnosticCode.FileChangedDuringInspection, $"The plugin root {TrustPaths.Quote(root.Value)} was moved or replaced during the inspection.");
    }

    // Hashes the whole file through the open handle, reading at most MaxPluginFileBytes + 1 bytes, so a file
    // that is, or grows, larger than the limit is rejected rather than hashed in part.
    private TrustFailure? Hash(SafeFileHandle file, string relative, CancellationToken cancellationToken, out ContentFingerprint? fingerprint, out long total)
    {
        fingerprint = null;
        total = 0;
        string what = TrustPaths.Quote(relative);
        const long Limit = PluginTrustService.MaxPluginFileBytes;
        try
        {
            long length = RandomAccess.GetLength(file);
            if (length > Limit)
            {
                return TooLarge(what);
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[ChunkSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(buffer.Length, Limit + 1 - total);
                int read = RandomAccess.Read(file, buffer.AsSpan(0, wanted), total);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > Limit)
                {
                    return TooLarge(what);
                }

                hash.AppendData(buffer, 0, read);
                hooks?.ChunkHashed?.Invoke(total);
            }

            if (total != length || RandomAccess.GetLength(file) != total)
            {
                return Changed(relative);
            }

            fingerprint = ContentFingerprint.FromHash(hash.GetHashAndReset());
            return null;
        }
        catch (IOException exception)
        {
            return TrustFailure.Unavailable(TrustDiagnosticCode.FileUnreadable, $"{what} could not be read: {TrustPaths.Quote(exception.Message)}");
        }
        catch (UnauthorizedAccessException)
        {
            return TrustFailure.Unavailable(TrustDiagnosticCode.FileUnreadable, $"Reading {what} was denied.");
        }
    }

    private static TrustFailure TooLarge(string what) =>
        TrustFailure.Unavailable(
            TrustDiagnosticCode.FileTooLarge,
            $"{what} is larger than {PluginTrustService.MaxPluginFileBytes} bytes, the limit for fingerprinting.");

    private static TrustFailure Changed(string relative) =>
        TrustFailure.Unavailable(TrustDiagnosticCode.FileChangedDuringInspection, $"{TrustPaths.Quote(relative)} changed while it was being inspected.");

    private static string Combine(string directory, string name) => directory.EndsWith('\\') ? directory + name : directory + "\\" + name;
}
