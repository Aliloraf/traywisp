using System.Reflection;
using System.Runtime.Versioning;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Tests.Execution;
using TrayWisp.Runtime.Tests.Plugins;
using TrayWisp.Runtime.Tests.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Diagnostics;

// The read-only host checks added for the CLI: plugin root validation, storage locations and interpreter lookup.
[SupportedOSPlatform("windows")]
public sealed class HostDiagnosticsTests : IDisposable
{
    private readonly TrustTestEnvironment _env = TrustTestEnvironment.Create();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _env.Dispose();

    [Fact]
    public void AValidRootIsAcceptedAndNotHeld()
    {
        PluginRootCheckResult result = PluginRootValidation.Check(_env.Root + "\\", Token);
        Assert.True(result.IsValid);
        Assert.Equal(_env.Root, result.Root!.Value);
        Assert.Empty(result.Diagnostics);

        // Nothing stays open: the root can be renamed right away.
        Directory.Move(_env.Root, _env.Root + "-moved");
        Directory.Move(_env.Root + "-moved", _env.Root);
    }

    [Fact]
    public void RootProblemsAreClassifiedWithoutCreatingAnything()
    {
        string missing = Path.Combine(_env.Base, "missing", "plugins");
        PluginRootCheckResult notFound = PluginRootValidation.Check(missing, Token);
        Assert.Equal(PluginRootStatus.NotFound, notFound.Status);
        Assert.Equal(TrustDiagnosticCode.RootNotFound, Assert.Single(notFound.Diagnostics).Code);
        Assert.False(Directory.Exists(Path.Combine(_env.Base, "missing")));

        string file = Path.Combine(_env.Base, "file");
        File.WriteAllText(file, "x");
        Assert.Equal(PluginRootStatus.Unsafe, PluginRootValidation.Check(file, Token).Status);

        Assert.Equal(PluginRootStatus.InvalidPath, PluginRootValidation.Check("relative", Token).Status);
        Assert.Equal(PluginRootStatus.InvalidPath, PluginRootValidation.Check(@"\\server\share", Token).Status);
        Assert.Equal(TrustDiagnosticCode.InvalidRootPath, Assert.Single(PluginRootValidation.Check(@"C:\a\..\b", Token).Diagnostics).Code);
    }

    [Fact]
    public void LinkedRootsAreUnsafe()
    {
        string link = Path.Combine(_env.Base, "link");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(link, _env.Root), "Junctions are not available.");
        PluginRootCheckResult result = PluginRootValidation.Check(link, Token);
        Assert.Equal(PluginRootStatus.Unsafe, result.Status);
        Assert.Equal(TrustDiagnosticCode.RootUnsafe, Assert.Single(result.Diagnostics).Code);

        PluginRootCheckResult below = PluginRootValidation.Check(Path.Combine(link, "sub"), Token);
        Assert.Equal(PluginRootStatus.Unsafe, below.Status);
    }

    [Fact]
    public void AShortNameRootIsAnAliasCheckedOnlyAfterTheChain()
    {
        string longName = Directory.CreateDirectory(Path.Combine(_env.Base, "a long directory name")).FullName;
        string? shortName = ShortPath(longName);
        Assert.SkipWhen(shortName is null || shortName == longName, "8.3 short names are not available on this volume.");
        PluginRootCheckResult result = PluginRootValidation.Check(shortName!, Token);
        Assert.Equal(PluginRootStatus.Unsafe, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code is TrustDiagnosticCode.RootAlias);
    }

    [Fact]
    public void StorageLocationsAreCheckedReadOnly()
    {
        string directory = Path.Combine(_env.Base, "store");
        StorageLocationReport missing = StorageLocation.Check(directory, "approvals.json", Token);
        Assert.Equal(StorageLocationStatus.Missing, missing.Status);
        Assert.Equal(StorageFileStatus.NotChecked, missing.FileStatus);
        Assert.False(Directory.Exists(directory));

        Directory.CreateDirectory(directory);
        Assert.Equal(StorageFileStatus.Missing, StorageLocation.Check(directory, "approvals.json", Token).FileStatus);
        File.WriteAllText(Path.Combine(directory, "approvals.json"), "never read");
        StorageLocationReport present = StorageLocation.Check(directory, "approvals.json", Token);
        Assert.Equal(StorageLocationStatus.Ready, present.Status);
        Assert.Equal(StorageFileStatus.Present, present.FileStatus);
        Assert.Equal(["approvals.json"], Directory.GetFiles(directory).Select(Path.GetFileName));

        Assert.Equal(StorageLocationStatus.InvalidPath, StorageLocation.Check("relative", cancellationToken: Token).Status);
        Assert.Throws<ArgumentException>(() => StorageLocation.Check(directory, "a/b", Token));
    }

    [Fact]
    public void UnsafeStorageLocationsAreReported()
    {
        string target = Directory.CreateDirectory(Path.Combine(_env.Base, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "a.json"), "x");
        string link = Path.Combine(_env.Base, "link");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(link, target), "Junctions are not available.");
        Assert.Equal(StorageLocationStatus.Unsafe, StorageLocation.Check(link, "a.json", Token).Status);

        string store = Directory.CreateDirectory(Path.Combine(_env.Base, "store")).FullName;
        Assert.SkipUnless(TrustTestEnvironment.TryCreateHardLink(Path.Combine(store, "a.json"), Path.Combine(target, "a.json")), "Hard links are not available.");
        Assert.Equal(StorageFileStatus.Unsafe, StorageLocation.Check(store, "a.json", Token).FileStatus);
        Directory.CreateDirectory(Path.Combine(store, "dir.json"));
        Assert.Equal(StorageFileStatus.Unsafe, StorageLocation.Check(store, "dir.json", Token).FileStatus);
    }

    [Fact]
    public void InterpreterLookupUsesTheEnginesRulesWithoutRunningAnything()
    {
        string helper = ExecutionTestEnvironment.HelperPath();
        var options = new PluginExecutionOptions { PowerShellPath = helper, PythonPath = helper, NodePath = helper };
        foreach (PluginLanguage language in new[] { PluginLanguage.PowerShell, PluginLanguage.Python, PluginLanguage.JavaScript })
        {
            InterpreterLookupResult result = InterpreterLookup.Find(language, options, _env.Root, Token);
            Assert.Equal(InterpreterLookupStatus.Resolved, result.Status);
            Assert.True(result.IsConfigured);
            Assert.Equal(helper, result.Interpreter!.Path);
            Assert.Equal(InterpreterSource.Configured, result.Interpreter.Source);
        }

        Assert.Equal(InterpreterLookupStatus.UnsupportedLanguage, InterpreterLookup.Find(PluginLanguage.Bash, options, cancellationToken: Token).Status);
        Assert.Equal(InterpreterLookupStatus.UnsupportedLanguage, InterpreterLookup.Find(PluginLanguage.Batch, cancellationToken: Token).Status);
    }

    [Fact]
    public void AnInvalidConfiguredInterpreterHasNoFallback()
    {
        string notAProgram = Path.Combine(_env.Base, "python.exe");
        File.WriteAllText(notAProgram, "print('x')");
        InterpreterLookupResult result = InterpreterLookup.Find(PluginLanguage.Python, new PluginExecutionOptions { PythonPath = notAProgram }, _env.Root, Token);
        Assert.Equal(InterpreterLookupStatus.ConfigurationInvalid, result.Status);
        Assert.Null(result.Interpreter);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.ConfiguredInterpreterInvalid);

        string inRoot = Path.Combine(_env.Root, "python.exe");
        File.Copy(ExecutionTestEnvironment.HelperPath(), inRoot);
        Assert.Equal(InterpreterLookupStatus.ConfigurationInvalid, InterpreterLookup.Find(PluginLanguage.Python, new PluginExecutionOptions { PythonPath = inRoot }, _env.Root, Token).Status);

        Assert.Throws<ArgumentException>(() => InterpreterLookup.Find(PluginLanguage.Python, new PluginExecutionOptions { PythonPath = "python.exe" }, cancellationToken: Token));
        Assert.False(InterpreterLookup.IsValidConfiguredPath("python.exe"));
        Assert.False(InterpreterLookup.IsValidConfiguredPath(@"C:\Python\python.bat"));
        Assert.False(InterpreterLookup.IsValidConfiguredPath(null));
        Assert.True(InterpreterLookup.IsValidConfiguredPath(@"C:\Python\python.exe"));
    }

    [Fact]
    public void TheDiagnosticsNamespaceExposesOnlyTheDocumentedTypes()
    {
        string[] expected =
        [
            nameof(InterpreterLookup), nameof(InterpreterLookupResult), nameof(InterpreterLookupStatus), nameof(PluginRunHistory), nameof(PluginRunHistoryOptions),
            nameof(PluginRunRecord), nameof(RecordedDiagnostic), nameof(RunHistoryReadResult), nameof(RunHistoryReadStatus), nameof(RunHistoryWriteResult),
            nameof(RunHistoryWriteStatus), nameof(RunPublication), nameof(StorageDiagnostic), nameof(StorageDiagnosticCode), nameof(StorageFileStatus),
            nameof(StorageLocation), nameof(StorageLocationReport), nameof(StorageLocationStatus),
        ];
        Assembly runtime = typeof(PluginRunHistory).Assembly;
        string[] actual = [.. runtime.GetExportedTypes().Where(type => type.Namespace == "TrayWisp.Runtime.Diagnostics").Select(type => type.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);

        foreach (Type type in new[] { typeof(PluginRunRecord), typeof(RunHistoryReadResult), typeof(RunHistoryWriteResult), typeof(StorageLocationReport), typeof(InterpreterLookupResult), typeof(PluginRootCheckResult) })
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod is { IsPublic: true } ? property.SetMethod : null));
        }

        // Nothing in the namespace can start a process: no member takes or returns process types or delegates.
        foreach (Type type in runtime.GetExportedTypes().Where(type => type.Namespace == "TrayWisp.Runtime.Diagnostics"))
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain(method.GetParameters(), parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
            }
        }
    }

    private static string? ShortPath(string path)
    {
        char[] buffer = new char[1024];
        uint length = GetShortPathName(path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string longPath, char[] shortPath, uint bufferLength);
}
