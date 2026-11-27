using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Execution;

// Where interpreters are looked for: this process's PATH, Program Files folder and Windows system directory.
internal sealed record InterpreterSearchLocations(string? PathVariable, string? ProgramFiles, string? SystemDirectory)
{
    public static InterpreterSearchLocations FromCurrentProcess() => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify),
        Environment.SystemDirectory);
}

internal enum InterpreterResolution
{
    Resolved,
    Unavailable,
    ConfigurationInvalid,
}

// A fully qualified executable path from trusted host configuration, split into its directory and file name.
internal sealed record ConfiguredExecutable(PluginRoot Directory, string FileName)
{
    // Syntax only, no file system access: a fully qualified local path (PluginRoot rules for the directory) whose
    // file name is a valid segment ending in .exe.
    public static bool TryParse(string path, [NotNullWhen(true)] out ConfiguredExecutable? executable)
    {
        executable = null;
        int separator = path.LastIndexOfAny(['\\', '/']);
        if (separator < 2)
        {
            return false;
        }

        string fileName = path[(separator + 1)..];
        string directory = separator == 2 ? path[..3] : path[..separator];
        bool doubledSeparator = separator > 2 && path[separator - 1] is '\\' or '/';
        if (doubledSeparator
            || !PluginRoot.TryCreate(directory, out PluginRoot? root)
            || !PluginId.IsValidSegment(fileName)
            || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        executable = new ConfiguredExecutable(root, fileName);
        return true;
    }
}

// Test seam for interpreter resolution: observes the order of file system access and can replace drive type
// detection, so unsupported drive types are testable without remote infrastructure. Never set in production.
internal sealed class InterpreterResolverProbe
{
    // Replaces GetDriveType for a drive root such as C:\.
    public Func<string, DriveType>? DriveType { get; init; }

    // Immediately before the directory chain of a candidate directory is opened, with the directory path.
    public Action<string>? ValidatingDirectory { get; init; }

    // Immediately before a candidate executable is opened, with its path.
    public Action<string>? OpeningCandidate { get; init; }
}

// Deterministic interpreter resolution, see docs/execution-engine.md ("Interpreter resolution"). Nothing is ever
// launched, probed for its version or searched through a shell, and the current directory is never searched.
// A resolved path names a validated local file; it does not authenticate the program.
//
// Access order for every candidate: path syntax (no file system access), then the drive type, then the directory and
// each of its ancestors without following links, then .NET normalization of the validated directory, and only then
// the candidate file itself, while the validated directory stays open.
[SupportedOSPlatform("windows")]
internal sealed class InterpreterResolver(
    InterpreterSearchLocations locations,
    PluginRoot? pluginRoot,
    ExecutionDiagnosticBag diagnostics,
    CancellationToken cancellationToken,
    InterpreterResolverProbe? probe = null)
{
    // The longest environment variable Windows supports.
    internal const int MaxPathVariableLength = 32_767;

    // PATH entries examined; later entries are ignored.
    internal const int MaxPathEntries = 256;

    private readonly PathEntryCounts _ignored = new();
    private List<PluginRoot>? _searchPath;

    public InterpreterResolution Resolve(PluginLanguage language, ConfiguredExecutable? configured, out PluginInterpreter? interpreter)
    {
        interpreter = null;
        if (configured is not null)
        {
            return ResolveConfigured(language, configured, out interpreter);
        }

        InterpreterResolution resolution = language switch
        {
            PluginLanguage.PowerShell => ResolvePowerShell(out interpreter),
            PluginLanguage.Python => SearchPath(["python.exe", "python3.exe"], InterpreterKind.Python, out interpreter),
            PluginLanguage.JavaScript => SearchPath(["node.exe"], InterpreterKind.Node, out interpreter),
            _ => InterpreterResolution.Unavailable,
        };

        ReportIgnoredEntries(resolution == InterpreterResolution.Resolved);
        if (resolution != InterpreterResolution.Resolved)
        {
            diagnostics.Add(PluginDiagnosticSeverity.Error, ExecutionDiagnosticCode.InterpreterNotFound, NotFoundMessage(language));
        }

        return resolution;
    }

    private InterpreterResolution ResolveConfigured(PluginLanguage language, ConfiguredExecutable configured, out PluginInterpreter? interpreter)
    {
        interpreter = null;
        string path = Combine(configured.Directory, configured.FileName);
        string? problem;
        bool notFound;
        string? finalPath = null;
        using (SafeFileHandle? directory = TryOpenDirectory(configured.Directory, out problem, out notFound))
        {
            if (directory is not null)
            {
                TryValidateCandidate(configured.Directory, configured.FileName, out finalPath, out problem, out notFound);
            }
        }

        if (finalPath is not null)
        {
            InterpreterKind kind = language switch
            {
                PluginLanguage.PowerShell when configured.FileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) => InterpreterKind.WindowsPowerShell,
                PluginLanguage.PowerShell => InterpreterKind.PowerShell,
                PluginLanguage.Python => InterpreterKind.Python,
                _ => InterpreterKind.Node,
            };
            interpreter = new PluginInterpreter(finalPath, kind, InterpreterSource.Configured);
            return InterpreterResolution.Resolved;
        }

        diagnostics.Add(
            PluginDiagnosticSeverity.Error,
            ExecutionDiagnosticCode.ConfiguredInterpreterInvalid,
            $"The configured {Describe(language)} interpreter {TrustPaths.Quote(path)} cannot be used: {(notFound ? "it does not exist" : problem)}. "
            + "TrayWisp does not fall back to another interpreter; fix or remove the configured path.");
        return InterpreterResolution.ConfigurationInvalid;
    }

    // PowerShell 7 in its default installation folder, then pwsh.exe in PATH, then Windows PowerShell 5.1 in the
    // system directory.
    private InterpreterResolution ResolvePowerShell(out PluginInterpreter? interpreter)
    {
        if (TryFixedLocation(locations.ProgramFiles, @"PowerShell\7", "pwsh.exe", InterpreterKind.PowerShell, InterpreterSource.KnownLocation, out interpreter)
            || SearchPath(["pwsh.exe"], InterpreterKind.PowerShell, out interpreter) == InterpreterResolution.Resolved
            || TryFixedLocation(locations.SystemDirectory, @"WindowsPowerShell\v1.0", "powershell.exe", InterpreterKind.WindowsPowerShell, InterpreterSource.SystemDirectory, out interpreter))
        {
            return InterpreterResolution.Resolved;
        }

        return InterpreterResolution.Unavailable;
    }

    private bool TryFixedLocation(string? baseDirectory, string relativeDirectory, string fileName, InterpreterKind kind, InterpreterSource source, out PluginInterpreter? interpreter)
    {
        interpreter = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(baseDirectory) || !PluginRoot.TryCreate(Path.Join(baseDirectory, relativeDirectory), out PluginRoot? directory))
        {
            return false;
        }

        string? finalPath = null;
        string? problem;
        bool notFound;
        using (SafeFileHandle? held = TryOpenDirectory(directory, out problem, out notFound))
        {
            if (held is not null)
            {
                TryValidateCandidate(directory, fileName, out finalPath, out problem, out notFound);
            }
        }

        if (finalPath is null)
        {
            if (!notFound)
            {
                Rejected(Combine(directory, fileName), problem!);
            }

            return false;
        }

        interpreter = new PluginInterpreter(finalPath, kind, source);
        return true;
    }

    // Each validated PATH directory in order; within a directory, the file names in order. A directory that fails
    // validation is skipped as a whole, before any file in it is opened, and counted in the TW613 summary.
    private InterpreterResolution SearchPath(string[] fileNames, InterpreterKind kind, out PluginInterpreter? interpreter)
    {
        interpreter = null;
        foreach (PluginRoot directory in GetSearchPath())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SafeFileHandle? held = TryOpenDirectory(directory, out _, out bool missing);
            if (held is null)
            {
                if (!missing)
                {
                    _ignored.Unsafe++;
                }

                continue;
            }

            foreach (string fileName in fileNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryValidateCandidate(directory, fileName, out string? finalPath, out string? problem, out bool notFound))
                {
                    interpreter = new PluginInterpreter(finalPath, kind, InterpreterSource.SearchPath);
                    return InterpreterResolution.Resolved;
                }

                if (!notFound)
                {
                    Rejected(Combine(directory, fileName), problem!);
                }
            }
        }

        return InterpreterResolution.Unavailable;
    }

    // The PATH entries that may be searched, in order, without duplicates. Parsed once per resolution, with syntax
    // checks only: nothing here accesses the file system.
    private List<PluginRoot> GetSearchPath()
    {
        if (_searchPath is not null)
        {
            return _searchPath;
        }

        _searchPath = [];
        string value = locations.PathVariable ?? string.Empty;
        if (value.Length > MaxPathVariableLength)
        {
            // Only entries that end within the limit are complete. The separator that ends the last complete entry may
            // be the first character beyond the limit. An entry the limit cuts is never shortened into another path.
            int end = value.LastIndexOf(';', MaxPathVariableLength);
            string beyond = end < 0 ? value : value[(end + 1)..];
            _ignored.BeyondLimits += beyond.Split(';').Count(entry => entry.Length > 0);
            value = end < 0 ? string.Empty : value[..end];
        }

        var seen = new HashSet<PluginRoot>();
        int examined = 0;
        foreach (string entry in value.Split(';'))
        {
            if (entry.Length == 0)
            {
                // An empty entry would mean the current directory to some programs. It is never searched.
                _ignored.Empty++;
                continue;
            }

            if (++examined > MaxPathEntries)
            {
                _ignored.BeyondLimits++;
                continue;
            }

            if (!PluginRoot.TryCreate(entry, out PluginRoot? directory))
            {
                // Relative, drive-relative, UNC, device, quoted or otherwise malformed: never resolved against anything.
                _ignored.Unqualified++;
            }
            else if (pluginRoot is not null && pluginRoot.Contains(directory))
            {
                _ignored.InsidePluginRoot++;
            }
            else if (!seen.Add(directory))
            {
                _ignored.Duplicate++;
            }
            else
            {
                _searchPath.Add(directory);
            }
        }

        return _searchPath;
    }

    // Validates a directory that candidates are looked for in, before any file in it is opened: not inside the plugin
    // root; a local fixed, removable or RAM drive (asked before anything on the drive is opened); the directory and
    // every ancestor ordinary local directories, opened one at a time from the drive root without following links
    // (no junction, symbolic link or mount point, not offline or remote, final path equal to the path, which also
    // rejects 8.3 short-name aliases); and finally unchanged by .NET path normalization. Path.GetFullPath can expand
    // short names by querying the file system, so it runs only after the chain has been validated.
    //
    // On success, returns the directory held open (it cannot be renamed or deleted while held); the caller inspects
    // its candidates and then disposes it. Ancestors are not held: a concurrent rename of one is detected by the
    // candidate's final path check, not prevented.
    private SafeFileHandle? TryOpenDirectory(PluginRoot directory, out string? problem, out bool notFound)
    {
        problem = null;
        notFound = false;
        if (pluginRoot is not null && pluginRoot.Contains(directory))
        {
            problem = "it is inside the plugin root, and plugins must not provide their own interpreter";
            return null;
        }

        string driveRoot = directory.Value[..3];
        PathCheck drive = probe?.DriveType is { } driveType ? TrustPaths.ClassifyDrive(driveRoot, driveType(driveRoot)) : TrustPaths.CheckDrive(driveRoot);
        if (drive.Problem != PathProblem.None)
        {
            notFound = drive.Problem == PathProblem.NotFound;
            problem = DescribeDirectoryProblem(drive);
            return null;
        }

        probe?.ValidatingDirectory?.Invoke(directory.Value);
        PathCheck chain = TrustPaths.OpenDirectoryChain(directory, rejectCaseSensitiveAncestors: false, createMissing: false, hold: true, out SafeFileHandle? held);
        if (chain.Problem != PathProblem.None)
        {
            notFound = chain.Problem == PathProblem.NotFound;
            problem = DescribeDirectoryProblem(chain);
            return null;
        }

        if (!TrustPaths.IsUnchangedByFullPath(directory.Value))
        {
            held!.Dispose();
            problem = "its directory is an alias (for example a short 8.3 name); use the full path";
            return null;
        }

        return held;
    }

    private static string DescribeDirectoryProblem(PathCheck check) => check.Problem switch
    {
        PathProblem.ReparsePoint => $"the directory {TrustPaths.Quote(check.Path)} is a link, junction or other reparse point",
        PathProblem.Network or PathProblem.UnsupportedDrive => "it is not on a local fixed, removable or RAM drive",
        PathProblem.RemoteStorage => $"the directory {TrustPaths.Quote(check.Path)} is offline or stored remotely",
        PathProblem.Alias => $"its directory resolves to {TrustPaths.Quote(check.Detail ?? string.Empty)}",
        PathProblem.NotFound => "it does not exist",
        PathProblem.NotDirectory => $"{TrustPaths.Quote(check.Path)} is not a directory",
        _ => $"its directory {TrustPaths.Quote(check.Path)} could not be validated",
    };

    // Validates one candidate in a directory that TryOpenDirectory validated and still holds, without running it: a
    // regular local file that is not a reparse point (so not an app execution alias or a link), not offline, whose
    // final path is the expected path, with a portable executable header.
    private bool TryValidateCandidate(PluginRoot directory, string fileName, [NotNullWhen(true)] out string? finalPath, out string? problem, out bool notFound)
    {
        finalPath = null;
        problem = null;
        notFound = false;
        string path = Combine(directory, fileName);
        probe?.OpeningCandidate?.Invoke(path);
        using SafeFileHandle? handle = WindowsFileApi.Open(path, WindowsFileApi.OpenMode.ReadShared, out int error);
        if (handle is null)
        {
            notFound = error is WindowsFileApi.ErrorFileNotFound or WindowsFileApi.ErrorPathNotFound;
            problem = $"it could not be opened: {TrustPaths.Quote(WindowsFileApi.Describe(error))}";
            return false;
        }

        if (!WindowsFileApi.TryGetAttributes(handle, out FileAttributes attributes, out uint reparseTag, out error))
        {
            problem = $"its attributes could not be read: {TrustPaths.Quote(WindowsFileApi.Describe(error))}";
            return false;
        }

        problem = attributes switch
        {
            _ when reparseTag == WindowsFileApi.ReparseTagAppExecutionLink =>
                "it is an app execution alias (such as a Microsoft Store shortcut in WindowsApps), which is never followed; configure the real executable's full path",
            _ when (attributes & FileAttributes.ReparsePoint) != 0 => "it is a symbolic link or other reparse point, which is never followed",
            _ when (attributes & FileAttributes.Directory) != 0 => "it is a directory",
            _ when (attributes & PluginDiscovery.RemoteStorage) != 0 => "it is offline or stored remotely",
            _ when !WindowsFileApi.IsDiskFile(handle) => "it is not a regular disk file",
            _ => null,
        };
        if (problem is not null)
        {
            return false;
        }

        PathCheck check = TrustPaths.CheckFinalPath(handle, path, out string final);
        if (check.Problem != PathProblem.None)
        {
            problem = check.Problem == PathProblem.Network ? "it resolves to a network path" : $"it resolves to {TrustPaths.Quote(check.Detail ?? string.Empty)}";
            return false;
        }

        problem = CheckPortableExecutable(handle);
        if (problem is not null)
        {
            return false;
        }

        finalPath = final;
        return true;
    }

    // A Windows program starts with "MZ" and points, at offset 0x3C, to a "PE\0\0" signature. Anything else (a script,
    // a text file, a DOS program) is rejected before Windows is asked to start it: for such files process creation can
    // fall back to other loaders, or block. This checks the format only; it does not authenticate the program.
    internal static string? CheckPortableExecutable(SafeFileHandle handle)
    {
        const string NotAProgram = "it is not a Windows program (no portable executable header); scripts and other files cannot be interpreters";
        try
        {
            long length = RandomAccess.GetLength(handle);
            Span<byte> header = stackalloc byte[0x40];
            if (length < header.Length || RandomAccess.Read(handle, header, 0) != header.Length || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                return NotAProgram;
            }

            uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]);
            Span<byte> signature = stackalloc byte[4];
            if (offset < header.Length || offset > length - signature.Length || RandomAccess.Read(handle, signature, offset) != signature.Length
                || !signature.SequenceEqual("PE\0\0"u8))
            {
                return NotAProgram;
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "it could not be read";
        }
    }

    private void Rejected(string path, string problem) =>
        diagnostics.Add(PluginDiagnosticSeverity.Warning, ExecutionDiagnosticCode.InterpreterCandidateRejected, $"Ignored the interpreter candidate {TrustPaths.Quote(path)}: {problem}.");

    // Empty and duplicate entries are common and harmless, so they are only reported when resolution failed.
    private void ReportIgnoredEntries(bool resolved)
    {
        int significant = _ignored.Unqualified + _ignored.Unsafe + _ignored.InsidePluginRoot + _ignored.BeyondLimits;
        if (significant > 0 || (!resolved && _ignored.Total > 0))
        {
            diagnostics.Add(
                PluginDiagnosticSeverity.Info,
                ExecutionDiagnosticCode.PathEntriesIgnored,
                $"{_ignored.Total} PATH entries were not searched: {_ignored.Empty} empty, {_ignored.Unqualified} not fully qualified local directory paths, "
                + $"{_ignored.Unsafe} unsafe directories (a link, junction, remote, offline, aliased or inaccessible directory on the path), "
                + $"{_ignored.InsidePluginRoot} inside the plugin root, {_ignored.Duplicate} duplicates, {_ignored.BeyondLimits} beyond the limit of {MaxPathEntries} entries "
                + $"or {MaxPathVariableLength} characters.");
        }
    }

    private static string NotFoundMessage(PluginLanguage language) => language switch
    {
        PluginLanguage.PowerShell =>
            @"No PowerShell interpreter was found: neither PowerShell 7 (pwsh.exe in Program Files\PowerShell\7 or in a PATH directory) nor Windows PowerShell "
            + "in the system directory. Configure PowerShellPath explicitly to use another installation.",
        PluginLanguage.Python => "No Python interpreter was found: no acceptable python.exe or python3.exe in the PATH directories. Configure PythonPath explicitly to use another installation.",
        _ => "No Node.js interpreter was found: no acceptable node.exe in the PATH directories. Configure NodePath explicitly to use another installation.",
    };

    private static string Describe(PluginLanguage language) => language switch
    {
        PluginLanguage.PowerShell => "PowerShell",
        PluginLanguage.Python => "Python",
        _ => "Node.js",
    };

    private static string Combine(PluginRoot directory, string fileName) => directory.IsDriveRoot ? directory.Value + fileName : directory.Value + "\\" + fileName;

    private sealed class PathEntryCounts
    {
        public int Empty { get; set; }

        public int Unqualified { get; set; }

        public int Unsafe { get; set; }

        public int InsidePluginRoot { get; set; }

        public int Duplicate { get; set; }

        public int BeyondLimits { get; set; }

        public int Total => Empty + Unqualified + Unsafe + InsidePluginRoot + Duplicate + BeyondLimits;
    }
}
