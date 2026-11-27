using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;

namespace TrayWisp.Runtime.Trust;

// What is wrong with a directory path, found by TrustPaths.OpenDirectoryChain.
internal enum PathProblem
{
    None,
    NotFound,
    NotDirectory,
    ReparsePoint,
    RemoteStorage,
    CaseSensitive,
    Alias,
    Network,
    UnsupportedDrive,
    Inaccessible,
}

internal readonly record struct PathCheck(PathProblem Problem, string Path, string? Detail)
{
    public static PathCheck Ok { get; } = new(PathProblem.None, string.Empty, null);
}

// Path validation shared by plugin inspection and the trust store.
internal static class TrustPaths
{
    public const int MaxQuotedPathLength = 260;

    public static string Quote(string text) => PluginDiagnosticText.Quote(text, MaxQuotedPathLength);

    /// <summary>
    /// Parses a caller-supplied plugin root. The syntax rules are those of <see cref="PluginRoot"/>; in
    /// addition, .NET's own normalization must leave the path unchanged, so the path cannot mean anything
    /// other than what it says. The only file system access is the short-name expansion that
    /// <see cref="Path.GetFullPath(string)"/> performs on Windows for segments containing <c>~</c>.
    /// </summary>
    public static TrustFailure? TryParseRoot(string rootPath, out PluginRoot? root)
    {
        if (PluginRoot.TryCreate(rootPath, out root) && !IsUnchangedByFullPath(root.Value))
        {
            // On Windows, Path.GetFullPath expands 8.3 short names (C:\PROGRA~1), which are aliases.
            string expanded = FullPathOrEmpty(root.Value);
            root = null;
            return TrustFailure.Unsafe(
                TrustDiagnosticCode.RootAlias,
                $"The plugin root {Quote(rootPath)} is an alias of {Quote(expanded)}, for example through a short (8.3) name. Use the full path instead.");
        }

        if (root is null)
        {
            return TrustFailure.Unsafe(
                TrustDiagnosticCode.InvalidRootPath,
                $"The plugin root {Quote(rootPath)} is not a supported path. Use a fully qualified path on a local drive, such as C:\\plugins, "
                + "without '.' or '..' segments, doubled separators, alternate data streams, device or UNC prefixes, or names ending in a space or dot.");
        }

        return null;
    }

    public static bool IsUnchangedByFullPath(string normalizedPath) =>
        string.Equals(FullPathOrEmpty(normalizedPath), normalizedPath, StringComparison.Ordinal);

    private static string FullPathOrEmpty(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Validates a directory and every ancestor up to its drive root: the drive must be a local drive,
    /// every ancestor an ordinary directory (no reparse point, not offline or remote, and optionally not
    /// case-sensitive), and the directory itself an ordinary directory whose final path, read from its open
    /// handle, equals its normalized path. Hidden ancestors are allowed (AppData is hidden).
    /// </summary>
    /// <param name="hold">On success, keep the directory open (denying deletion and renaming) and return it.</param>
    /// <param name="createMissing">Create missing directories one level at a time, after validating their parent.</param>
    [SupportedOSPlatform("windows")]
    public static PathCheck OpenDirectoryChain(PluginRoot directory, bool rejectCaseSensitiveAncestors, bool createMissing, bool hold, out SafeFileHandle? handle)
    {
        handle = null;
        PathCheck drive = CheckDrive(directory.Value[..3]);
        if (drive.Problem != PathProblem.None)
        {
            return drive;
        }

        List<string> prefixes = Prefixes(directory);
        for (int i = 0; i < prefixes.Count; i++)
        {
            bool last = i == prefixes.Count - 1;
            string path = prefixes[i];
            WindowsFileApi.OpenMode mode = last && hold ? WindowsFileApi.OpenMode.HoldDirectory : WindowsFileApi.OpenMode.Inspect;
            SafeFileHandle? current = WindowsFileApi.Open(path, mode, out int error);
            if (current is null && createMissing && error is WindowsFileApi.ErrorFileNotFound or WindowsFileApi.ErrorPathNotFound)
            {
                try
                {
                    // The parent was validated in the previous iteration, so this creates exactly one level.
                    Directory.CreateDirectory(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return new PathCheck(PathProblem.Inaccessible, path, exception.Message);
                }

                current = WindowsFileApi.Open(path, mode, out error);
            }

            if (current is null)
            {
                return error switch
                {
                    WindowsFileApi.ErrorFileNotFound or WindowsFileApi.ErrorPathNotFound => new PathCheck(PathProblem.NotFound, path, null),
                    _ => new PathCheck(PathProblem.Inaccessible, path, WindowsFileApi.Describe(error)),
                };
            }

            bool keep = false;
            try
            {
                PathCheck check = CheckDirectoryHandle(current, path, caseSensitivity: rejectCaseSensitiveAncestors && !last);
                if (check.Problem == PathProblem.None && last)
                {
                    check = CheckFinalPath(current, directory.Value);
                }

                if (check.Problem != PathProblem.None)
                {
                    return check;
                }

                if (last && hold)
                {
                    handle = current;
                    keep = true;
                }
            }
            finally
            {
                if (!keep)
                {
                    current.Dispose();
                }
            }
        }

        return PathCheck.Ok;
    }

    /// <summary>The final path of an open directory or file must equal <paramref name="expected"/>, ignoring case.</summary>
    [SupportedOSPlatform("windows")]
    public static PathCheck CheckFinalPath(SafeFileHandle handle, string expected) => CheckFinalPath(handle, expected, out _);

    /// <inheritdoc cref="CheckFinalPath(SafeFileHandle, string)"/>
    [SupportedOSPlatform("windows")]
    public static PathCheck CheckFinalPath(SafeFileHandle handle, string expected, out string final)
    {
        if (!WindowsFileApi.TryGetFinalPath(handle, out final, out int error))
        {
            return new PathCheck(PathProblem.Inaccessible, expected, "its final path could not be determined: " + WindowsFileApi.Describe(error));
        }

        if (final.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new PathCheck(PathProblem.Network, expected, final);
        }

        return string.Equals(final, expected, StringComparison.OrdinalIgnoreCase)
            ? PathCheck.Ok
            : new PathCheck(PathProblem.Alias, expected, final);
    }

    // Every prefix of the path from the drive root to the directory itself, for example C:\, C:\a, C:\a\b.
    public static List<string> Prefixes(PluginRoot directory)
    {
        string value = directory.Value;
        var prefixes = new List<string> { value[..3] };
        for (int i = 3; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                prefixes.Add(value[..i]);
            }
        }

        if (!directory.IsDriveRoot)
        {
            prefixes.Add(value);
        }

        return prefixes;
    }

    [SupportedOSPlatform("windows")]
    private static PathCheck CheckDirectoryHandle(SafeFileHandle handle, string path, bool caseSensitivity)
    {
        if (!WindowsFileApi.TryGetAttributes(handle, out FileAttributes attributes, out int error))
        {
            return new PathCheck(PathProblem.Inaccessible, path, WindowsFileApi.Describe(error));
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new PathCheck(PathProblem.ReparsePoint, path, (attributes & PluginDiscovery.CloudSync) != 0 ? "cloud" : null);
        }

        if ((attributes & PluginDiscovery.RemoteStorage) != 0)
        {
            return new PathCheck(PathProblem.RemoteStorage, path, null);
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            return new PathCheck(PathProblem.NotDirectory, path, null);
        }

        if (caseSensitivity)
        {
            if (!WindowsFileApi.TryIsCaseSensitiveDirectory(handle, out bool caseSensitive, out error))
            {
                return new PathCheck(PathProblem.Inaccessible, path, WindowsFileApi.Describe(error));
            }

            if (caseSensitive)
            {
                return new PathCheck(PathProblem.CaseSensitive, path, null);
            }
        }

        return PathCheck.Ok;
    }

    private static PathCheck CheckDrive(string driveRoot)
    {
        DriveType type;
        try
        {
            type = new DriveInfo(driveRoot).DriveType;
        }
        catch (ArgumentException)
        {
            return new PathCheck(PathProblem.UnsupportedDrive, driveRoot, null);
        }

        return type switch
        {
            DriveType.Fixed or DriveType.Removable or DriveType.Ram => PathCheck.Ok,
            DriveType.Network => new PathCheck(PathProblem.Network, driveRoot, null),
            DriveType.NoRootDirectory => new PathCheck(PathProblem.NotFound, driveRoot, null),
            _ => new PathCheck(PathProblem.UnsupportedDrive, driveRoot, type.ToString()),
        };
    }
}
