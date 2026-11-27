using System.Globalization;
using System.Runtime.Versioning;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Tests.Plugins;
using TrayWisp.Runtime.Tests.Trust;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// Interpreter resolution never launches a candidate, so fake interpreters are empty files with the right names.
[SupportedOSPlatform("windows")]
public sealed class InterpreterResolutionTests : IDisposable
{
    private static readonly string[] PythonNames = ["python.exe", "python3.exe"];

    private readonly TrustTestEnvironment _env = TrustTestEnvironment.Create();

    private static CancellationToken Token => TrustTestEnvironment.Token;

    public void Dispose() => _env.Dispose();

    private string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(_env.Base, name)).FullName;

    private string Fake(string directory, string fileName)
    {
        string path = Path.Combine(Directory(directory), fileName);
        File.WriteAllBytes(path, MinimalPortableExecutableHeader());
        return path;
    }

    // "MZ", the offset of the PE signature at 0x3C, and the signature: enough for the format check, never launched.
    internal static byte[] MinimalPortableExecutableHeader()
    {
        byte[] header = new byte[0x44];
        header[0] = (byte)'M';
        header[1] = (byte)'Z';
        header[0x3C] = 0x40;
        "PE\0\0"u8.CopyTo(header.AsSpan(0x40));
        return header;
    }

    private (InterpreterResolution Resolution, PluginInterpreter? Interpreter, ExecutionDiagnosticBag Diagnostics) Resolve(
        PluginLanguage language,
        string? path,
        string? configured = null,
        string? programFiles = null,
        string? systemDirectory = null,
        string? root = null,
        InterpreterResolverProbe? probe = null)
    {
        var diagnostics = new ExecutionDiagnosticBag();
        ConfiguredExecutable? executable = null;
        if (configured is not null)
        {
            Assert.True(ConfiguredExecutable.TryParse(configured, out executable));
        }

        var resolver = new InterpreterResolver(
            new InterpreterSearchLocations(path, programFiles ?? Path.Combine(_env.Base, "no-program-files"), systemDirectory ?? Path.Combine(_env.Base, "no-system")),
            PluginRoot.Create(root ?? _env.Root),
            diagnostics,
            Token,
            probe);
        InterpreterResolution resolution = resolver.Resolve(language, executable, out PluginInterpreter? interpreter);
        return (resolution, interpreter, diagnostics);
    }

    private static string Codes(ExecutionDiagnosticBag diagnostics) => string.Join(",", diagnostics.ToImmutable().Select(d => (int)d.Code));

    [Fact]
    public void AConfiguredInterpreterIsUsedInsteadOfSearching()
    {
        string configured = Fake("configured", "python.exe");
        string onPath = Fake("onpath", "python.exe");

        var (resolution, interpreter, _) = Resolve(PluginLanguage.Python, Path.GetDirectoryName(onPath), configured);

        Assert.Equal(InterpreterResolution.Resolved, resolution);
        Assert.Equal(configured, interpreter!.Path);
        Assert.Equal(InterpreterSource.Configured, interpreter.Source);
        Assert.Equal(InterpreterKind.Python, interpreter.Kind);
    }

    [Fact]
    public void AMissingConfiguredInterpreterFailsWithoutFallingBack()
    {
        string onPath = Fake("onpath", "python.exe");
        string missing = Path.Combine(_env.Base, "missing", "python.exe");

        var (resolution, interpreter, diagnostics) = Resolve(PluginLanguage.Python, Path.GetDirectoryName(onPath), missing);

        Assert.Equal(InterpreterResolution.ConfigurationInvalid, resolution);
        Assert.Null(interpreter);
        ExecutionDiagnostic diagnostic = Assert.Single(diagnostics.ToImmutable());
        Assert.Equal(ExecutionDiagnosticCode.ConfiguredInterpreterInvalid, diagnostic.Code);
        Assert.Contains("does not exist", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("does not fall back", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredDirectoryOrPluginLocalInterpreterIsRejected()
    {
        string directory = Path.Combine(Directory("configured"), "node.exe");
        System.IO.Directory.CreateDirectory(directory);
        string local = Path.Combine(_env.Root, "tools", "node.exe");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllBytes(local, []);

        var (directoryResolution, _, directoryDiagnostics) = Resolve(PluginLanguage.JavaScript, path: null, directory);
        var (localResolution, _, localDiagnostics) = Resolve(PluginLanguage.JavaScript, path: null, local);

        Assert.Equal(InterpreterResolution.ConfigurationInvalid, directoryResolution);
        Assert.Contains("directory", directoryDiagnostics.ToImmutable()[0].Message, StringComparison.Ordinal);
        Assert.Equal(InterpreterResolution.ConfigurationInvalid, localResolution);
        Assert.Contains("inside the plugin root", localDiagnostics.ToImmutable()[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("python.exe")]
    [InlineData(@"tools\python.exe")]
    [InlineData(@"C:python.exe")]
    [InlineData(@"\python.exe")]
    [InlineData(@"\\server\share\python.exe")]
    [InlineData(@"\\?\C:\Python\python.exe")]
    [InlineData(@"\\.\C:\Python\python.exe")]
    [InlineData(@"C:\Python\python.exe:stream")]
    [InlineData(@"C:\Python\..\python.exe")]
    [InlineData(@"C:\Python\\python.exe")]
    [InlineData(@"C:\Python\python.cmd")]
    [InlineData(@"C:\Python\python.ps1")]
    [InlineData(@"C:\Python\python")]
    [InlineData(@"C:\Python\")]
    [InlineData("")]
    public void ASyntacticallyInvalidConfiguredInterpreterIsRejectedAtConstruction(string path)
    {
        PluginTrustService trust = _env.Service();
        Assert.Throws<ArgumentException>(() => new PluginExecutionService(trust, new PluginExecutionOptions { PythonPath = path }));
        Assert.Throws<ArgumentException>(() => new PluginExecutionService(trust, new PluginExecutionOptions { PowerShellPath = path }));
        Assert.Throws<ArgumentException>(() => new PluginExecutionService(trust, new PluginExecutionOptions { NodePath = path }));
    }

    [Fact]
    public void AConfiguredWindowsPowerShellIsRecognizedByName()
    {
        string windows = Fake("winps", "powershell.exe");
        string seven = Fake("ps7", "pwsh.exe");

        Assert.Equal(InterpreterKind.WindowsPowerShell, Resolve(PluginLanguage.PowerShell, null, windows).Interpreter!.Kind);
        Assert.Equal(InterpreterKind.PowerShell, Resolve(PluginLanguage.PowerShell, null, seven).Interpreter!.Kind);
    }

    [Fact]
    public void PathDirectoriesAreSearchedInOrderAndFileNamesWithinEachDirectory()
    {
        string first = Path.GetDirectoryName(Fake("first", "python3.exe"))!;
        string second = Path.GetDirectoryName(Fake("second", "python.exe"))!;
        Fake("both", "python.exe");
        string both = Path.GetDirectoryName(Fake("both", "python3.exe"))!;

        Assert.Equal(Path.Combine(first, "python3.exe"), Resolve(PluginLanguage.Python, $"{first};{second}").Interpreter!.Path);
        Assert.Equal(Path.Combine(second, "python.exe"), Resolve(PluginLanguage.Python, $"{second};{first}").Interpreter!.Path);
        Assert.Equal(Path.Combine(both, "python.exe"), Resolve(PluginLanguage.Python, $"{both};{first}").Interpreter!.Path);
        Assert.Equal(InterpreterSource.SearchPath, Resolve(PluginLanguage.Python, first).Interpreter!.Source);
    }

    [Fact]
    public void ResolutionIsDeterministic()
    {
        string a = Path.GetDirectoryName(Fake("a", "node.exe"))!;
        string b = Path.GetDirectoryName(Fake("b", "node.exe"))!;
        string[] paths = [.. Enumerable.Range(0, 20).Select(_ => Resolve(PluginLanguage.JavaScript, $"{a};{b}").Interpreter!.Path)];
        Assert.All(paths, path => Assert.Equal(Path.Combine(a, "node.exe"), path));
    }

    [Fact]
    public void NoInterpreterIsUnavailable()
    {
        var (resolution, interpreter, diagnostics) = Resolve(PluginLanguage.JavaScript, Directory("empty"));
        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Null(interpreter);
        Assert.Equal("610", Codes(diagnostics));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(@".\")]
    [InlineData("bin")]
    [InlineData(@"..\bin")]
    [InlineData("C:")]
    [InlineData("C:bin")]
    [InlineData(@"\bin")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData("\"C:\\Program Files\\Python\"")]
    [InlineData(@"%SystemRoot%\system32")]
    [InlineData(@"C:\bin:stream")]
    [InlineData(@"C:\bin\..\bin")]
    [InlineData(@" C:\bin")]
    [InlineData(@"C:\bin ")]
    public void UnqualifiedAndUnsafePathEntriesAreNeverSearched(string entry)
    {
        string valid = Path.GetDirectoryName(Fake("valid", "python.exe"))!;

        var (resolution, interpreter, diagnostics) = Resolve(PluginLanguage.Python, $"{entry};{valid}");

        Assert.Equal(InterpreterResolution.Resolved, resolution);
        Assert.Equal(Path.Combine(valid, "python.exe"), interpreter!.Path);
        ExecutionDiagnostic ignored = Assert.Single(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored);
        Assert.Contains("1 not fully qualified", ignored.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPathEntriesNeverMeanTheCurrentDirectory()
    {
        var (resolution, _, diagnostics) = Resolve(PluginLanguage.Python, ";;;");
        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("4 empty", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyOrMissingPathFindsNothing()
    {
        Assert.Equal(InterpreterResolution.Unavailable, Resolve(PluginLanguage.Python, path: null).Resolution);
        Assert.Equal(InterpreterResolution.Unavailable, Resolve(PluginLanguage.Python, string.Empty).Resolution);
    }

    [Fact]
    public void DuplicatePathEntriesAreSearchedOnce()
    {
        string directory = Directory("dup");
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "python.exe"));

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.Python, $"{directory};{directory.ToUpperInvariant()};{directory}\\;{directory.Replace('\\', '/')}");

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Single(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.InterpreterCandidateRejected);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("3 duplicates", StringComparison.Ordinal));
    }

    [Fact]
    public void PathEntriesInsideThePluginRootAreIgnored()
    {
        Fake(@"plugins\bin", "python.exe");
        Fake("plugins", "python.exe");
        string root = _env.Root;

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.Python, $"{root}\\bin;{root}", root: root);

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("2 inside the plugin root", StringComparison.Ordinal));
    }

    [Fact]
    public void ADirectoryJunctionInPathIsRejected()
    {
        string target = Path.GetDirectoryName(Fake("target", "node.exe"))!;
        string junction = Path.Combine(_env.Base, "junction");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(junction, target), "Directory junctions are not available.");

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.JavaScript, junction);

        // The directory is rejected as a whole, before node.exe in it is opened, and counted as unsafe.
        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.DoesNotContain(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.InterpreterCandidateRejected);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("1 unsafe directories", StringComparison.Ordinal));
    }

    [Fact]
    public void ASymbolicLinkInterpreterIsRejected()
    {
        string target = Fake("real", "node.exe");
        string link = Path.Combine(Directory("links"), "node.exe");
        Assert.SkipUnless(TemporaryDirectory.TryCreateFileSymbolicLink(link, target), "Symbolic links need Developer Mode or elevation.");

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.JavaScript, Path.GetDirectoryName(link));
        var (configured, _, _) = Resolve(PluginLanguage.JavaScript, null, link);

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Contains("symbolic link", Assert.Single(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.InterpreterCandidateRejected).Message, StringComparison.Ordinal);
        Assert.Equal(InterpreterResolution.ConfigurationInvalid, configured);
    }

    [Fact]
    public void AppExecutionAliasesAreRejected()
    {
        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        string? alias = PythonNames.Select(name => Path.Combine(windowsApps, name))
            .FirstOrDefault(path => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
        Assert.SkipWhen(alias is null, "No app execution alias for Python (Microsoft Store shortcut) exists on this machine.");

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.Python, windowsApps);

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.InterpreterCandidateRejected && d.Message.Contains("app execution alias", StringComparison.Ordinal));
    }

    [Fact]
    public void PowerShell7InProgramFilesComesFirst()
    {
        string programFiles = Directory("pf");
        string known = Fake(@"pf\PowerShell\7", "pwsh.exe");
        string onPath = Path.GetDirectoryName(Fake("pathps", "pwsh.exe"))!;
        string system = Directory("sys");
        Fake(@"sys\WindowsPowerShell\v1.0", "powershell.exe");

        PluginInterpreter interpreter = Resolve(PluginLanguage.PowerShell, onPath, programFiles: programFiles, systemDirectory: system).Interpreter!;

        Assert.Equal(known, interpreter.Path);
        Assert.Equal(InterpreterKind.PowerShell, interpreter.Kind);
        Assert.Equal(InterpreterSource.KnownLocation, interpreter.Source);
    }

    [Fact]
    public void PwshInPathComesBeforeWindowsPowerShell()
    {
        string onPath = Path.GetDirectoryName(Fake("pathps", "pwsh.exe"))!;
        string system = Directory("sys");
        Fake(@"sys\WindowsPowerShell\v1.0", "powershell.exe");

        PluginInterpreter interpreter = Resolve(PluginLanguage.PowerShell, onPath, systemDirectory: system).Interpreter!;

        Assert.Equal(Path.Combine(onPath, "pwsh.exe"), interpreter.Path);
        Assert.Equal(InterpreterSource.SearchPath, interpreter.Source);
    }

    [Fact]
    public void WindowsPowerShellIsTheFallback()
    {
        string system = Directory("sys");
        string windows = Fake(@"sys\WindowsPowerShell\v1.0", "powershell.exe");
        string otherPath = Path.GetDirectoryName(Fake("other", "powershell.exe"))!;

        PluginInterpreter interpreter = Resolve(PluginLanguage.PowerShell, otherPath, systemDirectory: system).Interpreter!;

        Assert.Equal(windows, interpreter.Path);
        Assert.Equal(InterpreterKind.WindowsPowerShell, interpreter.Kind);
        Assert.Equal(InterpreterSource.SystemDirectory, interpreter.Source);
    }

    [Fact]
    public void TheRealSystemWindowsPowerShellResolves()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")), "Windows PowerShell is not installed.");
        var (resolution, interpreter, _) = Resolve(PluginLanguage.PowerShell, path: null, systemDirectory: Environment.SystemDirectory);
        Assert.Equal(InterpreterResolution.Resolved, resolution);
        Assert.Equal(InterpreterKind.WindowsPowerShell, interpreter!.Kind);
        Assert.True(Path.IsPathFullyQualified(interpreter.Path));
    }

    [Fact]
    public void SpacesAndUnicodeInPathsAreSupported()
    {
        string interpreter = Fake("Py thon \u00FC\u00F1\u00EF \uD83D\uDE80 & (x86); 'q'", "python.exe");
        var (resolution, resolved, _) = Resolve(PluginLanguage.Python, $"{Path.GetDirectoryName(interpreter)}");

        // A ';' inside a directory name cannot be expressed in PATH, so that entry splits; the configured path can hold it.
        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Null(resolved);
        Assert.Equal(interpreter, Resolve(PluginLanguage.Python, null, interpreter).Interpreter!.Path);

        string plain = Fake("Py thon \u00FC\u00F1\u00EF \uD83D\uDE80 & (x86) 'q'", "python.exe");
        Assert.Equal(plain, Resolve(PluginLanguage.Python, Path.GetDirectoryName(plain)).Interpreter!.Path);
    }

    [Fact]
    public void ResolutionIsCultureIndependentAndKeepsTheOnDiskSpelling()
    {
        string onDisk = Fake("Interp I i", "PYTHON.EXE");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            PluginInterpreter interpreter = Resolve(PluginLanguage.Python, Path.GetDirectoryName(onDisk)!.ToLowerInvariant()).Interpreter!;
            Assert.Equal(onDisk, interpreter.Path);
            Assert.True(InterpreterCommand.IsRemovedVariable("pythoninspect"));
            Assert.True(InterpreterCommand.IsRemovedVariable("node_options"));
            Assert.False(InterpreterCommand.IsRemovedVariable("psexecutionpolicypreference"));
            Assert.False(InterpreterCommand.IsRemovedVariable("PSEXECUTIONPOLICYPREFERENCE"));
            Assert.False(InterpreterCommand.IsRemovedVariable("PATH"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ThePathEntryCountIsBounded()
    {
        string late = Path.GetDirectoryName(Fake("late", "node.exe"))!;
        string missing = Path.Combine(_env.Base, "missing");
        string path = string.Join(';', Enumerable.Repeat(missing, InterpreterResolver.MaxPathEntries).Select((entry, i) => $"{entry}{i}")) + ";" + late;

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.JavaScript, path);

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("1 beyond the limit", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePathLengthIsBounded()
    {
        string late = Path.GetDirectoryName(Fake("late", "node.exe"))!;
        string path = new string(';', InterpreterResolver.MaxPathVariableLength) + late;

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.JavaScript, path);

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("@echo off\r\ncalc.exe\r\n")]
    [InlineData("#!/usr/bin/env python3\nprint('hi')\n")]
    [InlineData("MZ")]
    [InlineData("MZ this is a DOS stub without a portable executable header, long enough to have an offset field.")]
    public void ScriptsAndNonProgramsMasqueradingAsInterpretersAreRejected(string content)
    {
        string directory = Directory("masquerade");
        string fake = Path.Combine(directory, "python.exe");
        File.WriteAllText(fake, content);

        var (searched, _, diagnostics) = Resolve(PluginLanguage.Python, directory);
        var (configured, _, configuredDiagnostics) = Resolve(PluginLanguage.Python, null, fake);

        Assert.Equal(InterpreterResolution.Unavailable, searched);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.InterpreterCandidateRejected && d.Message.Contains("not a Windows program", StringComparison.Ordinal));
        Assert.Equal(InterpreterResolution.ConfigurationInvalid, configured);
        Assert.Contains("not a Windows program", configuredDiagnostics.ToImmutable()[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealProgramPassesTheFormatCheck()
    {
        string helper = ExecutionTestEnvironment.HelperPath();
        Assert.Equal(helper, Resolve(PluginLanguage.Python, null, helper).Interpreter!.Path);
    }

    // Records the order of drive checks, directory validation and candidate access.
    private static InterpreterResolverProbe Recording(List<string> accesses, Func<string, DriveType>? driveType = null) => new()
    {
        DriveType = driveType is null ? null : root =>
        {
            accesses.Add("drive " + root);
            return driveType(root);
        },
        ValidatingDirectory = directory => accesses.Add("directory " + directory),
        OpeningCandidate = candidate => accesses.Add("candidate " + candidate),
    };

    [Fact]
    public void EachDirectoryIsValidatedBeforeAnyCandidateInItIsOpened()
    {
        string first = Directory("first");
        string second = Path.GetDirectoryName(Fake("second", "python3.exe"))!;
        var accesses = new List<string>();

        PluginInterpreter interpreter = Resolve(PluginLanguage.Python, $"{first};{second}", probe: Recording(accesses, static _ => DriveType.Fixed)).Interpreter!;

        Assert.Equal(Path.Combine(second, "python3.exe"), interpreter.Path);
        string drive = first[..3];
        Assert.Equal(
            [
                "drive " + drive, "directory " + first, "candidate " + Path.Combine(first, "python.exe"), "candidate " + Path.Combine(first, "python3.exe"),
                "drive " + drive, "directory " + second, "candidate " + Path.Combine(second, "python.exe"), "candidate " + Path.Combine(second, "python3.exe"),
            ],
            accesses);
    }

    [Fact]
    public void ConfiguredAndFixedLocationsAreValidatedInTheSameOrder()
    {
        string configured = Fake("configured", "node.exe");
        string programFiles = Directory("pf");
        string known = Fake(@"pf\PowerShell\7", "pwsh.exe");
        var configuredAccesses = new List<string>();
        var knownAccesses = new List<string>();

        Assert.Equal(configured, Resolve(PluginLanguage.JavaScript, null, configured, probe: Recording(configuredAccesses)).Interpreter!.Path);
        Assert.Equal(known, Resolve(PluginLanguage.PowerShell, null, programFiles: programFiles, probe: Recording(knownAccesses)).Interpreter!.Path);

        Assert.Equal(["directory " + Path.GetDirectoryName(configured), "candidate " + configured], configuredAccesses);
        Assert.Equal(["directory " + Path.GetDirectoryName(known), "candidate " + known], knownAccesses);
    }

    [Fact]
    public void AJunctionAsAnIntermediateDirectoryIsRejectedBeforeTheCandidateIsOpened()
    {
        string real = Directory("real");
        string interpreter = Fake(@"real\inner", "python.exe");
        string junction = Path.Combine(_env.Base, "junction");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(junction, real), "Directory junctions are not available.");
        string throughJunction = Path.Combine(junction, "inner");
        Assert.True(File.Exists(Path.Combine(throughJunction, "python.exe")), "The junction does not lead to the interpreter.");
        var accesses = new List<string>();

        var (searched, _, diagnostics) = Resolve(PluginLanguage.Python, throughJunction, probe: Recording(accesses));
        var (configured, _, configuredDiagnostics) = Resolve(PluginLanguage.Python, null, Path.Combine(throughJunction, "python.exe"), probe: Recording(accesses));

        Assert.Equal(InterpreterResolution.Unavailable, searched);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("1 unsafe directories", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.InterpreterCandidateRejected);
        Assert.Equal(InterpreterResolution.ConfigurationInvalid, configured);
        Assert.Contains("reparse point", Assert.Single(configuredDiagnostics.ToImmutable()).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(accesses, access => access.StartsWith("candidate ", StringComparison.Ordinal));

        // The same interpreter through its real path is accepted.
        Assert.Equal(interpreter, Resolve(PluginLanguage.Python, Path.GetDirectoryName(interpreter)).Interpreter!.Path);
    }

    [Fact]
    public void AnOfflineIntermediateDirectoryIsRejectedBeforeTheCandidateIsOpened()
    {
        string offline = Directory("offline");
        Fake(@"offline\inner", "node.exe");
        try
        {
            File.SetAttributes(offline, FileAttributes.Directory | FileAttributes.Offline);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Assert.Skip("The offline attribute cannot be set here: " + exception.GetType().Name);
        }

        try
        {
            Assert.SkipUnless((File.GetAttributes(offline) & FileAttributes.Offline) != 0, "The file system does not keep the offline attribute on directories.");
            var accesses = new List<string>();

            var (resolution, _, diagnostics) = Resolve(PluginLanguage.JavaScript, Path.Combine(offline, "inner"), probe: Recording(accesses));

            Assert.Equal(InterpreterResolution.Unavailable, resolution);
            Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("1 unsafe directories", StringComparison.Ordinal));
            Assert.DoesNotContain(accesses, access => access.StartsWith("candidate ", StringComparison.Ordinal));
        }
        finally
        {
            File.SetAttributes(offline, FileAttributes.Directory);
        }
    }

    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Unknown)]
    public void AnUnsupportedDriveIsRejectedBeforeAnythingOnItIsOpened(DriveType type)
    {
        string valid = Path.GetDirectoryName(Fake("valid", "node.exe"))!;
        var accesses = new List<string>();

        var (searched, _, diagnostics) = Resolve(PluginLanguage.JavaScript, valid, probe: Recording(accesses, _ => type));
        var (configured, _, configuredDiagnostics) = Resolve(PluginLanguage.JavaScript, null, Path.Combine(valid, "node.exe"), probe: Recording(accesses, _ => type));

        Assert.Equal(InterpreterResolution.Unavailable, searched);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("1 unsafe directories", StringComparison.Ordinal));
        Assert.Equal(InterpreterResolution.ConfigurationInvalid, configured);
        Assert.Contains("not on a local fixed, removable or RAM drive", Assert.Single(configuredDiagnostics.ToImmutable()).Message, StringComparison.Ordinal);
        Assert.All(accesses, access => Assert.StartsWith("drive ", access, StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingDriveIsSilentlyAbsent()
    {
        string valid = Path.GetDirectoryName(Fake("valid", "node.exe"))!;
        var accesses = new List<string>();

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.JavaScript, valid, probe: Recording(accesses, static _ => DriveType.NoRootDirectory));

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Equal("610", Codes(diagnostics));
        Assert.All(accesses, access => Assert.StartsWith("drive ", access, StringComparison.Ordinal));
    }

    [Fact]
    public void AShortNameAliasDirectoryIsRejectedBeforeTheCandidateIsOpened()
    {
        string longName = Path.GetDirectoryName(Fake("a directory with a long name", "node.exe"))!;
        string shortName = ShortPath(longName);
        Assert.SkipWhen(string.Equals(shortName, longName, StringComparison.OrdinalIgnoreCase), "8.3 short names are disabled on this volume.");
        var accesses = new List<string>();

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.JavaScript, shortName, probe: Recording(accesses));

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("1 unsafe directories", StringComparison.Ordinal));
        Assert.DoesNotContain(accesses, access => access.StartsWith("candidate ", StringComparison.Ordinal));
    }

    [Fact]
    public void APathEntryCutByTheLengthLimitIsNeverShortenedIntoAnotherDirectory()
    {
        // "...\lat" exists and has an interpreter; the PATH entry is "...\late", and the limit falls on its last letter.
        string prefix = Path.GetDirectoryName(Fake("lat", "node.exe"))!;
        string entry = prefix + "e";
        string path = new string(';', InterpreterResolver.MaxPathVariableLength - prefix.Length) + entry;
        Assert.Equal(InterpreterResolver.MaxPathVariableLength + 1, path.Length);

        var (resolution, _, diagnostics) = Resolve(PluginLanguage.JavaScript, path);

        Assert.Equal(InterpreterResolution.Unavailable, resolution);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("1 beyond the limit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(";")]
    [InlineData(";C:\\after\\the\\limit")]
    public void AnEntryEndingExactlyAtTheLengthLimitIsSearched(string after)
    {
        string directory = Path.GetDirectoryName(Fake("exact", "node.exe"))!;
        string path = new string(';', InterpreterResolver.MaxPathVariableLength - directory.Length) + directory + after;

        var (resolution, interpreter, _) = Resolve(PluginLanguage.JavaScript, path);

        Assert.Equal(InterpreterResolution.Resolved, resolution);
        Assert.Equal(Path.Combine(directory, "node.exe"), interpreter!.Path);
    }

    [Fact]
    public void CompleteEntriesKeepTheirOrderWhenThePathIsCut()
    {
        string empty = Directory("none");
        string first = Path.GetDirectoryName(Fake("first", "node.exe"))!;
        string second = Path.GetDirectoryName(Fake("second", "node.exe"))!;
        string cut = Path.GetDirectoryName(Fake("cut", "node.exe"))!;
        string head = $"{empty};{second};{first};";
        string path = head + new string(';', InterpreterResolver.MaxPathVariableLength - head.Length - 2) + cut;

        var (resolution, interpreter, diagnostics) = Resolve(PluginLanguage.JavaScript, path);

        Assert.Equal(InterpreterResolution.Resolved, resolution);
        Assert.Equal(Path.Combine(second, "node.exe"), interpreter!.Path);
        Assert.Contains(diagnostics.ToImmutable(), d => d.Code == ExecutionDiagnosticCode.PathEntriesIgnored && d.Message.Contains("1 beyond the limit", StringComparison.Ordinal));
    }

    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        uint length = GetShortPathName(path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? path : new string(buffer, 0, (int)length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string longPath, char[] shortPath, uint length);

    [Fact]
    public void ResolutionObservesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var resolver = new InterpreterResolver(new InterpreterSearchLocations(Directory("x"), null, null), null, new ExecutionDiagnosticBag(), cancelled.Token);
        Assert.Throws<OperationCanceledException>(() => resolver.Resolve(PluginLanguage.Python, null, out _));
    }
}
