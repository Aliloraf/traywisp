using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Runtime.Execution;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// Process creation, argument transport, environment, streams, timeouts, cancellation and cleanup, with the helper
// program as the interpreter of approved plugins.
[SupportedOSPlatform("windows")]
public sealed class ProcessExecutionTests : IDisposable
{
    // Characters a shell would interpret, plus spaces and non-ASCII text, in a directory name.
    private const string AwkwardDirectory = "dir with space & ; ' $ ^ %PATH% ( ) ! [x] {y} ~ = + , \u00FC\u00F1 \uD83D\uDE80";

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private PluginExecutionService QuickService(ExecutionTestHooks? hooks = null) =>
        _env.Service(hooks: (hooks ?? new ExecutionTestHooks()) with { MinTimeout = TimeSpan.FromMilliseconds(100) });

    [Theory]
    [InlineData("plugin.ps1", new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File" })]
    [InlineData("plugin.py", new[] { "-E", "-u", "-X", "utf8" })]
    [InlineData("plugin.js", new[] { "--" })]
    public async Task FixedArgumentsAndTheScriptPathAreTransportedExactly(string fileName, string[] fixedArguments)
    {
        string relative = $"{AwkwardDirectory}/{fileName}";
        _env.Approved(relative, "report");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), relative);
        JsonElement report = ExecutionTestEnvironment.Report(result);

        string script = _env.Trust.PluginPath(relative);
        string[] arguments = [.. report.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()!)];
        Assert.Equal([.. fixedArguments, script], arguments);
        Assert.Equal(Path.GetDirectoryName(script), report.GetProperty("cwd").GetString());
        Assert.Equal(_env.Helper, result.Interpreter!.Path);
        Assert.Equal(InterpreterSource.Configured, result.Interpreter.Source);
    }

    [Fact]
    public async Task StandardInputIsClosedAtOnce()
    {
        _env.Approved("a.ps1", "report");
        var watch = Stopwatch.StartNew();

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(20));

        Assert.Equal(0, ExecutionTestEnvironment.Report(result).GetProperty("stdin").GetInt64());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task TheEnvironmentIsFilteredByTheDocumentedPolicy()
    {
        string[] removed =
        [
            "NODE_OPTIONS", "node_path", "NODE_REPL_EXTERNAL_MODULE", "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PythonInspect",
            "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER",
            "COR_ENABLE_PROFILING", "COR_PROFILER_PATH_64", "APPDOMAIN_MANAGER_ASM",
        ];
        string[] kept = ["TRAYWISP_TEST_KEEP", "NODEJS_HOME", "PSModulePath", "Path", "PSExecutionPolicyPreference"];
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string)entry.Value!;
        }

        foreach (string name in removed)
        {
            environment[name] = "secret-" + name;
        }

        environment["TRAYWISP_TEST_KEEP"] = "kept value";
        environment["PSExecutionPolicyPreference"] = "Restricted";
        environment["NODEJS_HOME"] = "kept too";
        environment.TryAdd("PSModulePath", "kept modules");
        _env.Approved("a.ps1", "report " + string.Join('|', [.. removed, .. kept]));

        PluginExecutionResult result = await _env.RunAsync(QuickService(new ExecutionTestHooks { BaseEnvironment = environment }), "a.ps1");
        JsonElement reported = ExecutionTestEnvironment.Report(result).GetProperty("env");

        Assert.All(removed, name => Assert.Equal(JsonValueKind.Null, reported.GetProperty(name).ValueKind));
        Assert.Equal("kept value", reported.GetProperty("TRAYWISP_TEST_KEEP").GetString());
        Assert.Equal("kept too", reported.GetProperty("NODEJS_HOME").GetString());
        Assert.Equal(environment["Path"], reported.GetProperty("Path").GetString());
        Assert.Equal(environment["PSModulePath"], reported.GetProperty("PSModulePath").GetString());
        Assert.Equal("Restricted", reported.GetProperty("PSExecutionPolicyPreference").GetString());
        string everything = string.Join("\n", result.Diagnostics.Select(d => d.ToString()));
        Assert.DoesNotContain("secret-", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("kept value", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulRunPublishesItsParsedMenu()
    {
        _env.Approved("a.ps1", "out Title", "out ---", "out Item one", "out --Nested");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);
        Assert.Equal(ProcessCleanupStatus.Exited, result.Cleanup.Status);
        Assert.True(result.Cleanup.RootExitConfirmed);
        Assert.True(result.Cleanup.JobObjectAssigned);
        Assert.Equal(0, result.Cleanup.RemainingJobProcesses);
        Assert.True(result.StandardOutputComplete);
        Assert.False(result.StandardOutputTruncated);
        Assert.Equal(StandardOutputEncoding.Utf8, result.StandardOutputEncoding);
        Assert.NotNull(result.StartedAtUtc);
        Assert.True(result.Elapsed > TimeSpan.Zero);
        Assert.Equal("Title", Assert.Single(result.Output!.Titles).Item.Text);
        MenuItem item = Assert.IsType<MenuItem>(Assert.Single(result.Output.Items));
        Assert.Equal("Item one", item.Text);
        Assert.Single(item.Children);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ANonZeroExitSuppressesEvenAValidMenu()
    {
        _env.Approved("a.ps1", "out Title", "out ---", "out Run | shell=editor.exe param1=document.txt", "exit 3");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        Assert.Equal(PluginExecutionStatus.NonZeroExitCode, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Null(result.Output);
        Assert.True(result.StandardOutputComplete);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.NonZeroExitCode);
    }

    [Fact]
    public async Task StandardErrorAloneDoesNotFailARun()
    {
        _env.Approved("a.ps1", "err warning: something odd", "out Title");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal("warning: something odd", result.StandardError);
        Assert.False(result.StandardErrorTruncated);
        Assert.Equal(23, result.StandardErrorBytes);
    }

    [Fact]
    public async Task LargeStandardErrorKeepsOnlyABoundedTail()
    {
        _env.Approved("a.ps1", "err-fill 200000", "err ", "err last line", "out Title");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.True(result.StandardErrorTruncated);
        Assert.Equal(200000 + 1 + 10, result.StandardErrorBytes);
        Assert.Equal("last line", result.StandardError);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.StandardErrorTruncated);
    }

    [Fact]
    public async Task BothStreamsAreCapturedConcurrentlyWithoutDeadlock()
    {
        _env.Approved("a.ps1", "both-fill 500000");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(60));

        Assert.True(result.StandardOutputComplete);
        Assert.Equal(500000, result.StandardOutputBytes);
        Assert.Equal(500000, result.StandardErrorBytes);
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);

        // 500,000 characters on one line exceed the parser's own limits, which still apply.
        Assert.Equal(PluginExecutionStatus.OutputParsingFailed, result.Status);
        Assert.Contains(result.OutputDiagnostics, d => d.Code == OutputDiagnosticCode.OutputTooLong);
    }

    [Theory]
    [InlineData(PluginExecutionService.MaxStandardOutputBytes - 1)]
    [InlineData(PluginExecutionService.MaxStandardOutputBytes)]
    public async Task StandardOutputUpToTheLimitIsComplete(int bytes)
    {
        _env.Approved("a.ps1", $"out-fill {bytes}", "err-fill 1048576");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(60));

        Assert.True(result.StandardOutputComplete);
        Assert.False(result.StandardOutputTruncated);
        Assert.Equal(bytes, result.StandardOutputBytes);
        Assert.True(result.StandardErrorTruncated);
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);
        Assert.Equal(PluginExecutionStatus.OutputParsingFailed, result.Status);
    }

    [Fact]
    public async Task OneByteAboveTheLimitTerminatesTheRunAndRejectsTheOutput()
    {
        string pid = _env.PidFile("holder");
        _env.Approved("a.ps1", $"spawn-holder {pid}|60000", "out Title", "out ---", "out Run | shell=editor.exe param1=doc", $"out-fill {PluginExecutionService.MaxStandardOutputBytes}", "sleep 60000");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(60));

        Assert.Equal(PluginExecutionStatus.OutputLimitExceeded, result.Status);
        Assert.Equal(ExecutionTerminationReason.OutputLimit, result.TerminationReason);
        Assert.True(result.StandardOutputTruncated);
        Assert.False(result.StandardOutputComplete);
        Assert.Null(result.Output);
        Assert.Contains(result.OutputDiagnostics, d => d.Code == OutputDiagnosticCode.OutputTruncated);
        Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
        ExecutionTestEnvironment.AssertExited(pid);
    }

    [Fact]
    public async Task ATimeoutTerminatesTheProcessTree()
    {
        string pid = _env.PidFile("holder");
        _env.Approved("a.ps1", $"spawn-holder {pid}|60000", "out spawned", "sleep 60000");
        var watch = Stopwatch.StartNew();

        PluginExecutionResult result = await _env.RunAsync(QuickService(), "a.ps1", TimeSpan.FromSeconds(3));

        Assert.Equal(PluginExecutionStatus.TimedOut, result.Status);
        Assert.True(result.TimedOut);
        Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
        Assert.True(result.Cleanup.RootExitConfirmed);
        Assert.Equal(0, result.Cleanup.RemainingJobProcesses);
        Assert.NotNull(result.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.TimedOut);
        ExecutionTestEnvironment.AssertExited(pid);
    }

    [Fact]
    public async Task ATimeoutAfterACompleteLookingMenuStillRejectsIt()
    {
        _env.Approved("a.ps1", "out Title", "out ---", "out Run | shell=editor.exe param1=doc", "sleep 60000");

        PluginExecutionResult result = await _env.RunAsync(QuickService(), "a.ps1", TimeSpan.FromSeconds(2));

        Assert.Equal(PluginExecutionStatus.TimedOut, result.Status);
        Assert.False(result.StandardOutputComplete);
        Assert.Null(result.Output);
        Assert.Null(result.StandardOutputEncoding);
        OutputDiagnostic truncated = Assert.Single(result.OutputDiagnostics);
        Assert.Equal(OutputDiagnosticCode.OutputTruncated, truncated.Code);
    }

    [Fact]
    public async Task ADescendantHoldingThePipesAfterTheRootExitsCannotKeepTheRunWaiting()
    {
        string pid = _env.PidFile("holder");
        _env.Approved("a.ps1", $"spawn-holder {pid}|60000", "out Title", "exit 0");
        var watch = Stopwatch.StartNew();

        PluginExecutionResult result = await _env.RunAsync(QuickService(), "a.ps1", TimeSpan.FromSeconds(3));

        Assert.Equal(PluginExecutionStatus.TimedOut, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Output);
        Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        ExecutionTestEnvironment.AssertExited(pid);
    }

    [Fact]
    public async Task WithoutAJobObjectTheProcessTreeIsStillTerminatedBestEffort()
    {
        string pid = _env.PidFile("holder");
        _env.Approved("a.ps1", $"spawn-holder {pid}|60000", "sleep 60000");

        PluginExecutionResult result = await _env.RunAsync(QuickService(new ExecutionTestHooks { DisableJobObject = true }), "a.ps1", TimeSpan.FromSeconds(3));

        Assert.Equal(PluginExecutionStatus.TimedOut, result.Status);
        Assert.False(result.Cleanup.JobObjectAssigned);
        Assert.Null(result.Cleanup.RemainingJobProcesses);
        Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
        ExecutionTestEnvironment.AssertExited(pid);
    }

    [Fact]
    public async Task CancellationDuringTheRunTerminatesIt()
    {
        string pid = _env.PidFile("holder");
        _env.Approved("a.ps1", $"spawn-holder {pid}|60000", "out started", "sleep 60000");
        using var cancellation = new CancellationTokenSource();
        var hooks = new ExecutionTestHooks { StandardOutputRead = _ => cancellation.Cancel() };

        PluginExecutionResult result = await _env.RunAsync(QuickService(hooks), "a.ps1", TimeSpan.FromSeconds(60), cancellation.Token);

        Assert.Equal(PluginExecutionStatus.Cancelled, result.Status);
        Assert.True(result.Cancelled);
        Assert.Equal(ExecutionTerminationReason.Cancellation, result.TerminationReason);
        Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
        Assert.Null(result.Output);
        Assert.Equal(1, _env.ProcessStarts);
        ExecutionTestEnvironment.AssertExited(pid);
    }

    [Fact]
    public async Task RepeatedCancellationLeavesNothingBehind()
    {
        _env.Approved("a.ps1", "out started", "sleep 60000");
        for (int i = 0; i < 5; i++)
        {
            using var cancellation = new CancellationTokenSource();
            int processId = 0;
            var hooks = new ExecutionTestHooks { StandardOutputRead = _ => cancellation.Cancel(), ProcessStarted = process => processId = process.Id };

            PluginExecutionResult result = await _env.RunAsync(_env.Service(hooks: hooks), "a.ps1", TimeSpan.FromSeconds(60), cancellation.Token);

            Assert.Equal(PluginExecutionStatus.Cancelled, result.Status);
            Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
            Assert.True(ProcessIsGone(processId));
            AssertReleased(_env.Trust.PluginPath("a.ps1"));
        }
    }

    [Fact]
    public async Task IncompleteCleanupIsReported()
    {
        _env.Approved("a.ps1", "sleep 60000");
        Process? observed = null;
        var hooks = new ExecutionTestHooks
        {
            SuppressTermination = true,
            CleanupGracePeriod = TimeSpan.FromMilliseconds(300),
            ProcessStarted = process => observed = Process.GetProcessById(process.Id),
        };

        PluginExecutionResult result = await _env.RunAsync(QuickService(hooks), "a.ps1", Short);

        using (observed)
        {
            Assert.Equal(PluginExecutionStatus.CleanupIncomplete, result.Status);
            Assert.Equal(ExecutionTerminationReason.Timeout, result.TerminationReason);
            Assert.Equal(ProcessCleanupStatus.Incomplete, result.Cleanup.Status);
            Assert.False(result.Cleanup.RootExitConfirmed);
            Assert.Null(result.ExitCode);
            Assert.Null(result.Output);
            Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.CleanupIncomplete);

            // Closing the job object when the run ended still terminated the process.
            Assert.True(observed!.WaitForExit(TimeSpan.FromSeconds(10)));
        }
    }

    // Replaces ProcessesThatReleasedThePipesAreLeftRunningAfterANormalExit: a run no longer releases its tracked
    // processes. See DescendantCleanupTests for the full policy.
    [Fact]
    public async Task ProcessesThatReleasedThePipesAreTerminatedAfterANormalExit()
    {
        string pid = _env.PidFile("detached");
        _env.Approved("a.ps1", $"spawn-detached {pid}|60000", "out Title");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(30));

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ProcessCleanupStatus.DescendantsTerminated, result.Cleanup.Status);
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);
        Assert.Equal(0, result.Cleanup.RemainingJobProcesses);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.DescendantsTerminated);
        ExecutionTestEnvironment.AssertExited(pid, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ImmediateExitsRaceTheTimeoutDeterministically()
    {
        _env.Approved("a.ps1", "out Title");
        PluginExecutionService service = _env.Service();
        for (int i = 0; i < 15; i++)
        {
            PluginExecutionResult result = await _env.RunAsync(service, "a.ps1", TimeSpan.FromSeconds(1));
            Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        }
    }

    [Fact]
    public async Task ConcurrentIndependentRunsDoNotInterfere()
    {
        const int Runs = 8;
        for (int i = 0; i < Runs; i++)
        {
            _env.Approved($"p{i}.ps1", $"out title {i}", "both-fill 100000");
        }

        PluginExecutionService service = _env.Service();
        PluginExecutionResult[] results = await Task.WhenAll(Enumerable.Range(0, Runs).Select(i => _env.RunAsync(service, $"p{i}.ps1", TimeSpan.FromSeconds(60))));

        for (int i = 0; i < Runs; i++)
        {
            Assert.True(results[i].StandardOutputComplete);
            Assert.Equal(100000 + $"title {i}\n".Length, results[i].StandardOutputBytes);
            Assert.Equal(100000, results[i].StandardErrorBytes);
            Assert.Equal(_env.Trust.PluginPath($"p{i}.ps1"), results[i].ValidatedFilePath);
        }

        Assert.Equal(Runs, _env.ProcessStarts);
    }

    private static void AssertReleased(string path)
    {
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
    }

    private static bool ProcessIsGone(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
