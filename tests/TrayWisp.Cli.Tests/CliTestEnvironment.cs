using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TrayWisp.Core.Plugins;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Cli.Tests;

// An isolated temporary base directory with a plugin root (plugins), a data directory (data) and a stand-in
// %LOCALAPPDATA% (local, not created), all with canonical long paths. Commands run in-process through CliApplication
// with in-memory streams, and every execution service they create uses the controlled helper interpreter's hooks to
// count process starts. Tests never touch the real per-user folders.
[SupportedOSPlatform("windows")]
internal sealed class CliTestEnvironment : IDisposable
{
    private readonly List<Action> _cleanup = [];
    private int _starts;

    public CliTestEnvironment()
    {
        string created = Path.Combine(Path.GetTempPath(), "traywisp-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(created);
        Base = Canonicalize(created);
        Root = Directory.CreateDirectory(Path.Combine(Base, "plugins")).FullName;
        Data = Path.Combine(Base, "data");
        Local = Path.Combine(Base, "local");
        Helper = Canonicalize(Path.Combine(AppContext.BaseDirectory, "TrayWisp.TestHelper.exe"));
    }

    public static CliTestEnvironment Create()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "TrayWisp's plugin commands are implemented for Windows only.");
        return new CliTestEnvironment();
    }

    public string Base { get; }

    public string Root { get; }

    public string Data { get; }

    public string Local { get; }

    public string Helper { get; }

    public string StorePath => Path.Combine(Data, "trust", "approvals.json");

    public string HistoryPath => Path.Combine(Data, "diagnostics", "history.json");

    public int ProcessStarts => Volatile.Read(ref _starts);

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    // Extra engine hooks for the next execution services (for example injected failures).
    public ExecutionTestHooks Hooks { get; set; } = new();

    // Replaces the run history the commands create (for example with test hooks).
    public Func<string, Runtime.Diagnostics.PluginRunHistory>? HistoryFactory { get; set; }

    public byte[] Input { get; set; } = [];

    public Func<Stream>? InputStream { get; set; }

    public bool InputRedirected { get; set; } = true;

    // The stand-in %LOCALAPPDATA% provider; by default the (uncreated) Local directory.
    public Func<string?>? LocalApplicationData { get; set; }

    // Replaces the end of Ctrl+C handling (by default there is none in-process).
    public Func<Task<Exception?>>? CloseCancellation { get; set; }

    // --root and --data-dir for this environment.
    public string[] Locations => ["--root", Root, "--data-dir", Data];

    // The helper as the explicitly configured interpreter of every language.
    public string[] Interpreters => ["--powershell", Helper, "--python", Helper, "--node", Helper];

    public CliEnvironment Environment(StringWriter output, StringWriter error) => new()
    {
        Out = output,
        Error = error,
        OpenInput = InputStream ?? (() => new MemoryStream(Input, writable: false)),
        InputRedirected = InputRedirected,
        LocalApplicationData = LocalApplicationData ?? (() => Local),
        CloseCancellation = CloseCancellation,
        CreateExecutionService = (trust, options) =>
        {
            ExecutionTestHooks hooks = Hooks;
            Action<Process>? started = hooks.ProcessStarted;
            return new PluginExecutionService(trust, options, TimeProvider.System, hooks with
            {
                ProcessStarted = process =>
                {
                    Interlocked.Increment(ref _starts);
                    started?.Invoke(process);
                },
            });
        },
        CreateRunHistory = HistoryFactory ?? (directory => new Runtime.Diagnostics.PluginRunHistory(directory)),
    };

    public async Task<CliRun> RunAsync(params string[] args) => await RunAsync(Token, args);

    public async Task<CliRun> RunAsync(CancellationToken token, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = await CliApplication.RunAsync(args, Environment(output, error), token);
        var run = new CliRun(exit, output.ToString(), error.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine($"traywisp {string.Join(' ', args)} -> {exit}\n{run.Output}\n{run.Error}");
        return run;
    }

    // A command with --root, --data-dir and --json added.
    public async Task<JsonElement> JsonAsync(params string[] args)
    {
        CliRun run = await RunAsync([.. args, .. Locations, "--json"]);
        return run.Json;
    }

    public string Write(string relativePath, params string[] lines) => WriteBytes(relativePath, Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    public string WriteBytes(string relativePath, byte[] content)
    {
        string path = PluginPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    // A history file written by hand, with the lock file every writer leaves next to it (reads never create the lock).
    public void WriteHistory(string content) => WriteHistoryBytes(System.Text.Encoding.UTF8.GetBytes(content));

    public void WriteHistoryBytes(byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllBytes(HistoryPath, content);
        File.WriteAllBytes(HistoryPath + ".lock", []);
    }

    public string PluginPath(string relativePath) => Path.Combine(Root, relativePath.Replace('/', '\\'));

    public string MarkerPath(string name) => Path.Combine(Base, name + ".marker");

    // A helper instruction that creates a marker file: proof that the plugin ran.
    public string Marker(string name) => $"write-file {MarkerPath(name)}|ran";

    // Inspects and approves through the CLI, as a user would.
    public async Task<string> ApproveAsync(string id)
    {
        JsonElement inspection = await JsonAsync("inspect", id);
        string fingerprint = inspection.GetProperty("result").GetProperty("sha256").GetString()!;
        CliRun approval = await RunAsync(["approve", id, "--sha256", fingerprint, .. Locations]);
        Assert.Equal(0, approval.ExitCode);
        return fingerprint;
    }

    public PluginTrustService TrustService() => new(StorePath);

    public void OnDispose(Action action) => _cleanup.Add(action);

    public void DenyListing(string directory)
    {
        var info = new DirectoryInfo(directory);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        OnDispose(() =>
        {
            DirectorySecurity restored = info.GetAccessControl();
            restored.RemoveAccessRule(rule);
            info.SetAccessControl(restored);
        });
    }

    // All files and directories under the base directory, with sizes and write times, to detect side effects.
    public string[] Snapshot() =>
        [.. Directory.EnumerateFileSystemEntries(Base, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true })
            .Select(path => File.Exists(path) ? $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}" : path + "|dir")
            .Order(StringComparer.Ordinal)];

    public static string Canonicalize(string path)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = WindowsFileApi.Open(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), WindowsFileApi.OpenMode.Inspect, out int error)
            ?? throw new IOException($"Could not open {path}: error {error}");
        Assert.True(WindowsFileApi.TryGetFinalPath(handle, out string final, out _));
        return final;
    }

    public static bool TryCreateJunction(string link, string target) => RunQuietly("cmd.exe", "/d", "/c", "mklink", "/J", link, target) && Directory.Exists(link);

    public static bool TryCreateHardLink(string link, string existing) => RunQuietly("fsutil.exe", "hardlink", "create", link, existing) && File.Exists(link);

    public void Dispose()
    {
        foreach (Action action in Enumerable.Reverse(_cleanup))
        {
            action();
        }

        // A terminated helper can hold its working directory (the plugin directory) briefly after the job reports it gone.
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                DeleteTree(new DirectoryInfo(Base));
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void DeleteTree(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            return;
        }

        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                entry.Delete();
                continue;
            }

            entry.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            if (entry is DirectoryInfo subdirectory)
            {
                DeleteTree(subdirectory);
            }
            else
            {
                entry.Delete();
            }
        }

        directory.Delete();
    }

    private static bool RunQuietly(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

internal sealed record CliRun(int ExitCode, string Output, string Error)
{
    // The single JSON document on standard output; fails the test if there is anything else.
    public JsonElement Json
    {
        get
        {
            using JsonDocument document = JsonDocument.Parse(Output);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(ExitCode, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(ExitCode == 0, document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(string.Empty, Error);
            return document.RootElement.Clone();
        }
    }

    public string[] Codes => [.. Json.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()!)];
}
