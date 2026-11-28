using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Diagnostics;

/// <summary>The state of a storage directory observed by <see cref="StorageLocation.Check"/>.</summary>
public enum StorageLocationStatus
{
    /// <summary>The directory exists and is an ordinary local directory at its configured path.</summary>
    Ready,

    /// <summary>The directory, or one of its ancestors, does not exist yet. The ancestors that exist were validated.</summary>
    Missing,

    /// <summary>The directory or an ancestor is a link, alias, remote, offline, case-sensitive or not a directory.</summary>
    Unsafe,

    /// <summary>The directory or an ancestor could not be examined.</summary>
    Inaccessible,

    /// <summary>The path is not a fully qualified local path with normalized segments. Nothing was accessed.</summary>
    InvalidPath,

    /// <summary>Not Windows. Nothing was accessed.</summary>
    UnsupportedPlatform,
}

/// <summary>The state of one file in a storage directory.</summary>
public enum StorageFileStatus
{
    /// <summary>No file was named, or the directory was not <see cref="StorageLocationStatus.Ready"/>.</summary>
    NotChecked,

    /// <summary>The file does not exist.</summary>
    Missing,

    /// <summary>The file is a regular local file with a single name at its configured path. Its content was not read.</summary>
    Present,

    /// <summary>The file is a link, directory, hard-linked, offline or remote entry, or resolves elsewhere.</summary>
    Unsafe,

    /// <summary>The file could not be examined.</summary>
    Inaccessible,
}

/// <summary>A point-in-time, read-only observation of a storage location. It never describes the file's content.</summary>
public sealed class StorageLocationReport
{
    internal StorageLocationReport(string directoryPath, StorageLocationStatus status, string? fileName, StorageFileStatus fileStatus, StorageDiagnostic? diagnostic)
    {
        DirectoryPath = directoryPath;
        Status = status;
        FileName = fileName;
        FileStatus = fileStatus;
        Diagnostics = diagnostic is null ? [] : [diagnostic];
    }

    /// <summary>The directory, normalized when its syntax was accepted.</summary>
    public string DirectoryPath { get; }

    /// <summary>The state of the directory.</summary>
    public StorageLocationStatus Status { get; }

    /// <summary>The file name that was checked, if any.</summary>
    public string? FileName { get; }

    /// <summary>The state of that file.</summary>
    public StorageFileStatus FileStatus { get; }

    /// <summary>At most one diagnostic explaining a problem or a missing directory.</summary>
    public ImmutableArray<StorageDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Read-only checks of host storage locations (the trust store directory, the run history directory) for diagnostics
/// such as <c>traywisp doctor</c>. Nothing is created, written, locked or read: only the directory chain and, optionally,
/// one file's attributes, link count and final path are examined, in the same order and with the same rules the
/// stores use when they write (see docs/diagnostics.md).
/// </summary>
public static class StorageLocation
{
    /// <summary>Checks <paramref name="directoryPath"/> and, when given, the file <paramref name="fileName"/> in it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="directoryPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not a single valid file name.</exception>
    /// <exception cref="OperationCanceledException">The check was cancelled before it started.</exception>
    public static StorageLocationReport Check(string directoryPath, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        if (fileName is not null && !PluginId.IsValidSegment(fileName))
        {
            throw new ArgumentException("The file name must be a single valid file name.", nameof(fileName));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return new StorageLocationReport(directoryPath, StorageLocationStatus.UnsupportedPlatform, fileName, StorageFileStatus.NotChecked,
                StorageFiles.Error(StorageDiagnosticCode.UnsupportedPlatform, "Storage locations are only supported on Windows."));
        }

        if (!PluginRoot.TryCreate(directoryPath, out PluginRoot? directory))
        {
            return new StorageLocationReport(directoryPath, StorageLocationStatus.InvalidPath, fileName, StorageFileStatus.NotChecked,
                StorageFiles.Error(StorageDiagnosticCode.InvalidStoragePath, $"The storage directory {TrustPaths.Quote(directoryPath)} is not a fully qualified local path with normalized segments."));
        }

        StorageDiagnostic? problem = StorageFiles.OpenDirectory(directory, createMissing: false, "storage", out SafeFileHandle? held);
        if (problem is not null)
        {
            StorageLocationStatus status = problem.Code switch
            {
                StorageDiagnosticCode.StorageDirectoryMissing => StorageLocationStatus.Missing,
                StorageDiagnosticCode.StorageDirectoryInaccessible => StorageLocationStatus.Inaccessible,
                _ => StorageLocationStatus.Unsafe,
            };
            return new StorageLocationReport(directory.Value, status, fileName, StorageFileStatus.NotChecked, problem);
        }

        using (held)
        {
            if (fileName is null)
            {
                return new StorageLocationReport(directory.Value, StorageLocationStatus.Ready, fileName, StorageFileStatus.NotChecked, diagnostic: null);
            }

            string path = StorageFiles.Combine(directory, fileName);
            using SafeFileHandle? file = WindowsFileApi.Open(path, WindowsFileApi.OpenMode.Inspect, out int error);
            if (file is null)
            {
                return error is WindowsFileApi.ErrorFileNotFound or WindowsFileApi.ErrorPathNotFound
                    ? new StorageLocationReport(directory.Value, StorageLocationStatus.Ready, fileName, StorageFileStatus.Missing, diagnostic: null)
                    : new StorageLocationReport(directory.Value, StorageLocationStatus.Ready, fileName, StorageFileStatus.Inaccessible,
                        StorageFiles.Error(StorageDiagnosticCode.StorageFileInaccessible, $"The file {TrustPaths.Quote(path)} could not be opened: {TrustPaths.Quote(WindowsFileApi.Describe(error))}"));
            }

            StorageDiagnostic? fileProblem = StorageFiles.CheckFile(file, path, "file");
            StorageFileStatus fileStatus = fileProblem switch
            {
                null => StorageFileStatus.Present,
                { Code: StorageDiagnosticCode.StorageFileUnsafe } => StorageFileStatus.Unsafe,
                _ => StorageFileStatus.Inaccessible,
            };
            return new StorageLocationReport(directory.Value, StorageLocationStatus.Ready, fileName, fileStatus, fileProblem);
        }
    }
}
