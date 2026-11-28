using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Diagnostics;

// Validation of host storage directories and files, built on the primitives the trust store uses (TrustPaths,
// WindowsFileApi) and following the same rules: the directory and every ancestor are ordinary local directories,
// opened without following links, not case-sensitive, at their configured final path, and only then is the path
// checked for short-name expansion; storage files are regular local files with exactly one name at their configured
// path. Nothing here changes the approval store.
internal static class StorageFiles
{
    public static string Combine(PluginRoot directory, string fileName) => directory.IsDriveRoot ? directory.Value + fileName : directory.Value + "\\" + fileName;

    // Validates (and with createMissing, creates one level at a time) the directory chain, then holds the directory open.
    // Returns null on success, with the held directory in handle. A missing directory without createMissing is reported
    // as StorageDirectoryMissing.
    [SupportedOSPlatform("windows")]
    public static StorageDiagnostic? OpenDirectory(PluginRoot directory, bool createMissing, string what, out SafeFileHandle? handle)
    {
        PathCheck check = TrustPaths.OpenDirectoryChain(directory, rejectCaseSensitiveAncestors: true, createMissing, hold: true, out handle);
        if (check.Problem != PathProblem.None)
        {
            return DirectoryProblem(directory, check, what);
        }

        StorageDiagnostic? problem = null;
        if (!WindowsFileApi.TryIsCaseSensitiveDirectory(handle!, out bool caseSensitive, out int error))
        {
            problem = Error(StorageDiagnosticCode.StorageDirectoryInaccessible, $"The {what} directory {TrustPaths.Quote(directory.Value)} could not be examined: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }
        else if (caseSensitive)
        {
            problem = Error(StorageDiagnosticCode.StorageDirectoryUnsafe, $"The {what} directory {TrustPaths.Quote(directory.Value)} is case-sensitive, so its path does not identify a single file.");
        }
        else if (!TrustPaths.IsUnchangedByFullPath(directory.Value))
        {
            problem = Error(StorageDiagnosticCode.StorageDirectoryUnsafe, $"The {what} directory {TrustPaths.Quote(directory.Value)} is an alias, for example through a short (8.3) name. Use the full path.");
        }

        if (problem is not null)
        {
            handle!.Dispose();
            handle = null;
        }

        return problem;
    }

    // A storage, lock or temporary file must be a regular local file (not a link, directory, offline or remote entry, not
    // being deleted) with exactly one hard link, at its configured final path.
    [SupportedOSPlatform("windows")]
    public static StorageDiagnostic? CheckFile(SafeFileHandle handle, string path, string what)
    {
        if (!WindowsFileApi.TryGetAttributes(handle, out FileAttributes attributes, out int error))
        {
            return Error(StorageDiagnosticCode.StorageFileInaccessible, $"The {what} {TrustPaths.Quote(path)} could not be examined: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | PluginDiscovery.RemoteStorage)) != 0 || !WindowsFileApi.IsDiskFile(handle))
        {
            return Error(StorageDiagnosticCode.StorageFileUnsafe, $"The {what} {TrustPaths.Quote(path)} is not a regular local file (it is a link, directory, or offline or remote entry).");
        }

        if (!WindowsFileApi.TryGetStandardInfo(handle, out bool deletePending, out uint links, out error))
        {
            return Error(StorageDiagnosticCode.StorageFileInaccessible, $"The {what} {TrustPaths.Quote(path)} could not be examined: {TrustPaths.Quote(WindowsFileApi.Describe(error))}");
        }

        if (deletePending)
        {
            return Error(StorageDiagnosticCode.StorageFileInaccessible, $"The {what} {TrustPaths.Quote(path)} is being deleted.");
        }

        if (links != 1)
        {
            return Error(
                StorageDiagnosticCode.StorageFileUnsafe,
                string.Create(CultureInfo.InvariantCulture, $"The {what} {TrustPaths.Quote(path)} has {links} hard links. It must be an ordinary file with a single name."));
        }

        PathCheck check = TrustPaths.CheckFinalPath(handle, path);
        return check.Problem switch
        {
            PathProblem.None => null,
            PathProblem.Inaccessible => Error(StorageDiagnosticCode.StorageFileInaccessible, $"The {what} {TrustPaths.Quote(path)}: {TrustPaths.Quote(check.Detail ?? string.Empty)}"),
            _ => Error(StorageDiagnosticCode.StorageFileUnsafe, $"The {what} {TrustPaths.Quote(path)} resolves to {TrustPaths.Quote(check.Detail ?? string.Empty)} instead of its configured path."),
        };
    }

    // Immediately before an atomic replacement: the held directory is still an ordinary directory at its configured path.
    [SupportedOSPlatform("windows")]
    public static StorageDiagnostic? CheckHeldDirectory(SafeFileHandle handle, PluginRoot directory, string what)
    {
        string? problem = null;
        if (!WindowsFileApi.TryGetAttributes(handle, out FileAttributes attributes, out int error))
        {
            problem = WindowsFileApi.Describe(error);
        }
        else if ((attributes & (FileAttributes.ReparsePoint | PluginDiscovery.RemoteStorage)) != 0)
        {
            problem = "it became a reparse point or remote entry";
        }
        else
        {
            PathCheck check = TrustPaths.CheckFinalPath(handle, directory.Value);
            if (check.Problem != PathProblem.None)
            {
                problem = check.Detail ?? check.Problem.ToString();
            }
        }

        return problem is null
            ? null
            : Error(
                StorageDiagnosticCode.StorageDirectoryUnsafe,
                $"The {what} directory {TrustPaths.Quote(directory.Value)} was moved or changed during the update ({TrustPaths.Quote(problem)}). Nothing was written.");
    }

    public static StorageDiagnostic Error(StorageDiagnosticCode code, string message) => new(PluginDiagnosticSeverity.Error, code, message);

    [SupportedOSPlatform("windows")]
    private static StorageDiagnostic DirectoryProblem(PluginRoot directory, PathCheck check, string what)
    {
        string where = string.Equals(check.Path, directory.Value, StringComparison.OrdinalIgnoreCase)
            ? $"The {what} directory {TrustPaths.Quote(directory.Value)}"
            : $"{TrustPaths.Quote(check.Path)} (an ancestor of the {what} directory {TrustPaths.Quote(directory.Value)})";
        return check.Problem switch
        {
            PathProblem.NotFound => new StorageDiagnostic(PluginDiagnosticSeverity.Info, StorageDiagnosticCode.StorageDirectoryMissing, $"{where} does not exist."),
            PathProblem.Inaccessible => Error(StorageDiagnosticCode.StorageDirectoryInaccessible, $"{where} could not be created or opened: {TrustPaths.Quote(check.Detail ?? "unknown reason")}"),
            PathProblem.Alias => Error(StorageDiagnosticCode.StorageDirectoryUnsafe, $"{where} resolves to {TrustPaths.Quote(check.Detail ?? string.Empty)}; configure that path instead."),
            PathProblem.CaseSensitive => Error(StorageDiagnosticCode.StorageDirectoryUnsafe, $"{where} is a case-sensitive directory, so the path does not identify a single directory."),
            _ => Error(
                StorageDiagnosticCode.StorageDirectoryUnsafe,
                $"{where} is not an ordinary local directory: it is a link or other reparse point, offline or remote, not a directory, or on an unsupported drive."),
        };
    }
}
