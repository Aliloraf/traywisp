using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Tests.Trust;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// An isolated plugin root and trust store (TrustTestEnvironment) plus the controlled helper program
// (tests/TrayWisp.TestHelper), configured explicitly as the interpreter of every language. Plugin files hold helper
// instructions, not real scripts. Every process start is counted through the execution test hooks, and every helper
// process that records its PID is checked, and if necessary killed, on dispose.
[SupportedOSPlatform("windows")]
internal sealed class ExecutionTestEnvironment : IDisposable
{
    private readonly List<string> _pidFiles = [];
    private int _starts;

    private ExecutionTestEnvironment(TrustTestEnvironment trust)
    {
        Trust = trust;
        TrustService = trust.Service();
        Helper = HelperPath();
    }

    public TrustTestEnvironment Trust { get; }

    // The long-lived trust service of this environment, shared by every execution service it creates.
    public PluginTrustService TrustService { get; }

    public string Helper { get; }

    public string Root => Trust.Root;

    public int ProcessStarts => Volatile.Read(ref _starts);

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    // The helper as the interpreter of every supported language.
    public PluginExecutionOptions HelperOptions => new() { PowerShellPath = Helper, PythonPath = Helper, NodePath = Helper };

    public static ExecutionTestEnvironment Create()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Plugin execution is implemented for Windows only.");
        return new ExecutionTestEnvironment(new TrustTestEnvironment());
    }

    public static string HelperPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TrayWisp.TestHelper.exe");
        Assert.True(File.Exists(path), "The test helper was not built next to the tests.");
        return TrustTestEnvironment.Canonicalize(path);
    }

    // An execution service using this environment's trust service. Process starts are counted; extra hooks are kept.
    public PluginExecutionService Service(PluginExecutionOptions? options = null, ExecutionTestHooks? hooks = null, PluginTrustService? trust = null)
    {
        hooks ??= new ExecutionTestHooks();
        Action<Process>? started = hooks.ProcessStarted;
        ExecutionTestHooks counting = hooks with
        {
            ProcessStarted = process =>
            {
                Interlocked.Increment(ref _starts);
                started?.Invoke(process);
            },
        };
        return new PluginExecutionService(trust ?? TrustService, options ?? HelperOptions, TimeProvider.System, counting);
    }

    // Writes a plugin made of helper instructions, one per line.
    public string Write(string relativePath, params string[] instructions) => Trust.Write(relativePath, string.Join("\n", instructions) + "\n");

    // Writes and approves a plugin.
    public PluginApproval Approved(string relativePath, params string[] instructions)
    {
        Write(relativePath, instructions);
        return Trust.Approve(TrustService, relativePath);
    }

    public async Task<PluginExecutionResult> RunAsync(PluginExecutionService service, string relativePath, TimeSpan? timeout = null, CancellationToken? token = null)
    {
        PluginExecutionResult result = await service.RunAsync(Root, PluginId.Create(relativePath), new PluginRunOptions { Timeout = timeout }, token ?? Token);

        // Shown for failing tests.
        TestContext.Current.TestOutputHelper?.WriteLine($"{relativePath}: {result.Status}, exit {result.ExitCode}, {result.Elapsed.TotalMilliseconds:0} ms, cleanup {result.Cleanup.Status}");
        foreach (ExecutionDiagnostic diagnostic in result.Diagnostics)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("  " + diagnostic);
        }

        if (result.StandardError.Length > 0)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("  stderr: " + result.StandardError);
        }

        return result;
    }

    // A path for a helper PID file outside the plugin root. The process it names is checked on dispose.
    public string PidFile(string name)
    {
        string path = Path.Combine(Trust.Base, name + ".pid");
        _pidFiles.Add(path);
        return path;
    }

    public string MarkerPath(string name) => Path.Combine(Trust.Base, name + ".marker");

    // Waits (bounded) for a helper PID file, then returns the process it names, or null if it already exited.
    public static Process? ProcessFromPidFile(string pidFile)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(pidFile))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "The helper did not record its process ID.");
            Thread.Sleep(10);
        }

        string[] parts = File.ReadAllText(pidFile).Split(' ');
        int pid = int.Parse(parts[0], CultureInfo.InvariantCulture);
        long startTicks = long.Parse(parts[1], CultureInfo.InvariantCulture);
        try
        {
            Process process = Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Asserts that the process recorded in the PID file has exited, waiting a bounded time for it to disappear.
    public static void AssertExited(string pidFile, TimeSpan? within = null)
    {
        using Process? process = ProcessFromPidFile(pidFile);
        if (process is not null)
        {
            Assert.True(process.WaitForExit(within ?? TimeSpan.FromSeconds(10)), $"Helper process {process.Id} is still running.");
        }
    }

    public static JsonElement Report(PluginExecutionResult result)
    {
        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.NotNull(result.Output);
        Assert.Single(result.Output.Titles);
        return JsonDocument.Parse(result.Output.Titles[0].Item.Text).RootElement;
    }

    // A process terminated through its job leaves the job (so the job reports it gone) slightly before Windows has
    // closed its last handles, such as its current directory, which is the plugin directory. Deleting the test
    // directories is therefore retried for a short, bounded time; a process that is really still running is caught by
    // the PID checks instead.
    private void DeleteDirectories()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Trust.Dispose();
                return;
            }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        // Safety net: no controlled helper process outlives its test. A test that expects one to remain kills it itself.
        var leftovers = new List<int>();
        foreach (string pidFile in _pidFiles.Where(File.Exists))
        {
            using Process? process = ProcessFromPidFile(pidFile);
            if (process is not null && !process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                leftovers.Add(process.Id);
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }

        DeleteDirectories();
        Assert.Empty(leftovers);
    }
}
