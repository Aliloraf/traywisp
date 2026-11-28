using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Execution;
using Xunit;

namespace TrayWisp.Cli.Tests;

// traywisp run, in-process, with the controlled helper program as the explicitly configured interpreter. Process
// creation is observed through the engine's start hook and through marker files the plugins would create.
[SupportedOSPlatform("windows")]
public sealed class RunCommandTests : IDisposable
{
    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private Task<CliRun> RunAsync(string id, params string[] extra) => _env.RunAsync(["run", id, .. _env.Locations, .. _env.Interpreters, .. extra]);

    private async Task<JsonElement> RunJsonAsync(string id, params string[] extra) => (await RunAsync(id, [.. extra, "--json"])).Json.GetProperty("result");

    [Fact]
    public async Task AnApprovedPluginRunsOnceAndItsOutputIsShown()
    {
        _env.Write("clock.5s.ps1", _env.Marker("clock"), "out Clock 12:00", "out ---", "out Item | href=https://example.com/");
        await _env.ApproveAsync("clock.5s.ps1");

        CliRun run = await RunAsync("clock.5s.ps1", "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(1, _env.ProcessStarts);
        Assert.True(File.Exists(_env.MarkerPath("clock")));
        JsonElement result = run.Json.GetProperty("result");
        JsonElement execution = result.GetProperty("execution");
        Assert.Equal("Succeeded", execution.GetProperty("status").GetString());
        Assert.Equal(0, execution.GetProperty("pluginExitCode").GetInt32());
        Assert.True(execution.GetProperty("elapsedMilliseconds").GetInt64() >= 0);
        Assert.Equal("Exited", execution.GetProperty("cleanup").GetProperty("status").GetString());
        Assert.True(execution.GetProperty("stdout").GetProperty("complete").GetBoolean());
        Assert.Equal(_env.Helper, execution.GetProperty("interpreter").GetProperty("path").GetString());
        Assert.Equal("Configured", execution.GetProperty("interpreter").GetProperty("source").GetString());
        Assert.Equal("Published", result.GetProperty("publication").GetProperty("status").GetString());
        Assert.Equal("Approved", result.GetProperty("publication").GetProperty("trustStatus").GetString());
        Assert.Equal("Clock 12:00", result.GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
        Assert.Equal("https://example.com/", result.GetProperty("output").GetProperty("items")[0].GetProperty("action").GetProperty("url").GetString());
        Assert.True(result.GetProperty("history").GetProperty("recorded").GetBoolean());

        CliRun text = await RunAsync("clock.5s.ps1");
        Assert.Contains("Output (confirmed by a trust check after the run; actions are inert):", text.Output, StringComparison.Ordinal);
        Assert.Contains("[1] Clock 12:00", text.Output, StringComparison.Ordinal);
        Assert.Contains("Run history: recorded", text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnapprovedAndModifiedPluginsDoNotStart()
    {
        _env.Write("pending.ps1", _env.Marker("pending"));
        CliRun pending = await RunAsync("pending.ps1", "--json");
        Assert.Equal(4, pending.ExitCode);
        Assert.Equal("TrustDenied", pending.Json.GetProperty("result").GetProperty("execution").GetProperty("status").GetString());
        Assert.Equal("PendingApproval", pending.Json.GetProperty("result").GetProperty("execution").GetProperty("trustStatus").GetString());

        _env.Write("changed.ps1", _env.Marker("changed"));
        await _env.ApproveAsync("changed.ps1");
        _env.Write("changed.ps1", _env.Marker("changed"), "out modified");
        CliRun changed = await RunAsync("changed.ps1", "--json");
        Assert.Equal(4, changed.ExitCode);
        Assert.Equal("ContentChanged", changed.Json.GetProperty("result").GetProperty("execution").GetProperty("trustStatus").GetString());

        Assert.Equal(0, _env.ProcessStarts);
        Assert.False(File.Exists(_env.MarkerPath("pending")));
        Assert.False(File.Exists(_env.MarkerPath("changed")));
        CliRun text = await RunAsync("pending.ps1");
        Assert.Contains("No process was started.", text.Output, StringComparison.Ordinal);
        Assert.Contains("Inspect and approve it first", text.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheSelectedPluginRuns()
    {
        _env.Write("a.1s.ps1", _env.Marker("a"), "out A");
        _env.Write("b.1s.ps1", _env.Marker("b"), "out B");
        _env.Write("sub/c.250ms.py", _env.Marker("c"), "out C");
        await _env.ApproveAsync("a.1s.ps1");
        await _env.ApproveAsync("b.1s.ps1");
        await _env.ApproveAsync("sub/c.250ms.py");

        Assert.Equal(0, (await RunAsync("b.1s.ps1")).ExitCode);
        await Task.Delay(1500, CliTestEnvironment.Token);

        Assert.Equal(1, _env.ProcessStarts);
        Assert.True(File.Exists(_env.MarkerPath("b")));
        Assert.False(File.Exists(_env.MarkerPath("a")));
        Assert.False(File.Exists(_env.MarkerPath("c")));
    }

    [Fact]
    public async Task EmptyOutputSucceeds()
    {
        _env.Write("empty.ps1", "# nothing");
        await _env.ApproveAsync("empty.ps1");
        JsonElement result = await RunJsonAsync("empty.ps1");
        Assert.Equal("Published", result.GetProperty("publication").GetProperty("status").GetString());
        Assert.Empty(result.GetProperty("output").GetProperty("titles").EnumerateArray());
        Assert.Empty(result.GetProperty("output").GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ANonZeroExitIsAnExecutionFailureWithThePluginsCode()
    {
        _env.Write("fail.ps1", "out Title", "exit 42");
        await _env.ApproveAsync("fail.ps1");
        CliRun run = await RunAsync("fail.ps1", "--json");
        Assert.Equal(6, run.ExitCode);
        JsonElement result = run.Json.GetProperty("result");
        Assert.Equal(42, result.GetProperty("execution").GetProperty("pluginExitCode").GetInt32());
        Assert.Equal("NonZeroExitCode", result.GetProperty("execution").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("output").ValueKind);
        Assert.Equal("NotApplicable", result.GetProperty("publication").GetProperty("status").GetString());
        Assert.DoesNotContain("Title", run.Output.Replace("\"titles\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutIsExitCode7()
    {
        _env.Write("slow.ps1", "sleep 60000");
        await _env.ApproveAsync("slow.ps1");
        var watch = Stopwatch.StartNew();
        CliRun run = await RunAsync("slow.ps1", "--timeout", "1", "--json");
        Assert.Equal(7, run.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        JsonElement execution = run.Json.GetProperty("result").GetProperty("execution");
        Assert.True(execution.GetProperty("timedOut").GetBoolean());
        Assert.Equal("Terminated", execution.GetProperty("cleanup").GetProperty("status").GetString());
    }

    [Fact]
    public async Task CancellationWaitsForCleanupAndIsExitCode130()
    {
        string started = _env.MarkerPath("started");
        _env.Write("long.ps1", $"write-file {started}|x", "sleep 60000");
        await _env.ApproveAsync("long.ps1");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CliTestEnvironment.Token);
        Task<CliRun> running = _env.RunAsync(cancellation.Token, ["run", "long.ps1", .. _env.Locations, .. _env.Interpreters, "--json"]);
        await WaitForFileAsync(started);
        await cancellation.CancelAsync();

        CliRun run = await running;
        Assert.Equal(130, run.ExitCode);
        JsonElement execution = run.Json.GetProperty("result").GetProperty("execution");
        Assert.Equal("Cancelled", execution.GetProperty("status").GetString());
        Assert.Equal("Terminated", execution.GetProperty("cleanup").GetProperty("status").GetString());
        Assert.True(execution.GetProperty("cleanup").GetProperty("rootExitConfirmed").GetBoolean());
    }

    [Fact]
    public async Task IncompleteCleanupWinsOverCancellationAndIsReportedProminently()
    {
        string started = _env.MarkerPath("started");
        _env.Write("long.ps1", $"write-file {started}|x", "sleep 60000");
        await _env.ApproveAsync("long.ps1");
        _env.Hooks = new ExecutionTestHooks { FailJobQuery = true, CleanupGracePeriod = TimeSpan.FromMilliseconds(300) };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CliTestEnvironment.Token);
        Task<CliRun> running = _env.RunAsync(cancellation.Token, ["run", "long.ps1", .. _env.Locations, .. _env.Interpreters]);
        await WaitForFileAsync(started);
        await cancellation.CancelAsync();

        CliRun run = await running;
        Assert.Equal(9, run.ExitCode);
        Assert.Contains("WARNING: PROCESS CLEANUP INCOMPLETE", run.Output, StringComparison.Ordinal);
        Assert.Contains("TW863", run.Output, StringComparison.Ordinal);
        Assert.Contains("may still be running", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncompleteCleanupAfterANormalExitWithholdsOutput()
    {
        _env.Write("a.ps1", "out Secret title");
        await _env.ApproveAsync("a.ps1");
        _env.Hooks = new ExecutionTestHooks { FailJobQuery = true, CleanupGracePeriod = TimeSpan.FromMilliseconds(300) };
        CliRun run = await RunAsync("a.ps1", "--json");
        Assert.Equal(9, run.ExitCode);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("result").GetProperty("output").ValueKind);
        Assert.DoesNotContain("Secret", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputOverflowIsAnExecutionFailureAndCaptureFailuresAreReported()
    {
        _env.Write("big.ps1", $"out-fill {PluginExecutionService.MaxStandardOutputBytes + 10}");
        await _env.ApproveAsync("big.ps1");
        CliRun big = await RunAsync("big.ps1", "--json");
        Assert.Equal(6, big.ExitCode);
        Assert.Equal("OutputLimitExceeded", big.Json.GetProperty("result").GetProperty("execution").GetProperty("status").GetString());
        Assert.True(big.Json.GetProperty("result").GetProperty("execution").GetProperty("stdout").GetProperty("truncated").GetBoolean());

        _env.Write("capture.ps1", "out x");
        await _env.ApproveAsync("capture.ps1");
        // An injected read failure stops reading before the stream is closed, so the engine cannot confirm that every
        // process released the output pipe: incomplete cleanup takes precedence over the capture failure.
        _env.Hooks = new ExecutionTestHooks { StandardOutputRead = _ => throw new IOException("injected"), CleanupGracePeriod = TimeSpan.FromMilliseconds(300) };
        CliRun capture = await RunAsync("capture.ps1", "--json");
        Assert.Equal(9, capture.ExitCode);
        Assert.Contains("TW642", capture.Codes);
        Assert.Contains("TW863", capture.Codes);
        Assert.Equal(JsonValueKind.Null, capture.Json.GetProperty("result").GetProperty("output").ValueKind);
    }

    [Fact]
    public async Task InvalidOutputIsExitCode8()
    {
        _env.Write("bad.ps1", "out-hex ff fe 00 00 41 00 00 00");
        await _env.ApproveAsync("bad.ps1");
        CliRun run = await RunAsync("bad.ps1", "--json");
        Assert.Equal(8, run.ExitCode);
        Assert.Equal("OutputDecodingFailed", run.Json.GetProperty("result").GetProperty("execution").GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnsupportedLanguagesAreNotRun()
    {
        _env.Write("tool.sh", _env.Marker("sh"));
        CliRun run = await RunAsync("tool.sh", "--json");
        Assert.Equal(3, run.ExitCode);
        Assert.Equal("UnsupportedLanguage", run.Json.GetProperty("result").GetProperty("execution").GetProperty("status").GetString());
        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task AnInvalidExplicitInterpreterNeverFallsBack()
    {
        _env.Write("a.py", _env.Marker("a"));
        await _env.ApproveAsync("a.py");
        string notAProgram = Path.Combine(_env.Base, "python.exe");
        File.WriteAllText(notAProgram, "not a program");
        foreach (string interpreter in new[] { notAProgram, Path.Combine(_env.Base, "missing", "python.exe") })
        {
            CliRun run = await _env.RunAsync(["run", "a.py", .. _env.Locations, "--python", interpreter, "--json"]);
            Assert.Equal(5, run.ExitCode);
            Assert.Equal("InterpreterConfigurationInvalid", run.Json.GetProperty("result").GetProperty("execution").GetProperty("status").GetString());
            Assert.Contains("TW612", run.Codes);
        }

        Assert.Equal(0, _env.ProcessStarts);
        Assert.False(File.Exists(_env.MarkerPath("a")));
    }

    [Fact]
    public async Task AMissingInterpreterIsExitCode5()
    {
        _env.Write("a.js", _env.Marker("a"));
        await _env.ApproveAsync("a.js");
        _env.Hooks = new ExecutionTestHooks { SearchLocations = new InterpreterSearchLocations(PathVariable: string.Empty, ProgramFiles: null, SystemDirectory: null) };
        CliRun run = await _env.RunAsync(["run", "a.js", .. _env.Locations, "--json"]);
        Assert.Equal(5, run.ExitCode);
        Assert.Equal("InterpreterUnavailable", run.Json.GetProperty("result").GetProperty("execution").GetProperty("status").GetString());
        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task ATrustDenialAfterTheRunWithholdsTheOutput()
    {
        string started = _env.MarkerPath("started");
        string gate = _env.MarkerPath("gate");
        _env.Write("a.ps1", $"write-file {started}|x", $"wait-file {gate}|30000", "out SECRET-LABEL", "out ---", "out Click | href=https://example.com/secret");
        await _env.ApproveAsync("a.ps1");

        Task<CliRun> running = RunAsync("a.ps1", "--json");
        await WaitForFileAsync(started);

        // Revoked by another invocation while the plugin runs: the run continues, but its output is never shown.
        Assert.Equal(0, (await _env.RunAsync(["revoke", "a.ps1", .. _env.Locations])).ExitCode);
        File.WriteAllText(gate, "go");
        CliRun run = await running;

        Assert.Equal(4, run.ExitCode);
        JsonElement result = run.Json.GetProperty("result");
        Assert.Equal("Succeeded", result.GetProperty("execution").GetProperty("status").GetString());
        Assert.Equal("Withheld", result.GetProperty("publication").GetProperty("status").GetString());
        Assert.Equal("StaleApproval", result.GetProperty("publication").GetProperty("trustStatus").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("output").ValueKind);
        Assert.Contains("TW857", run.Codes);
        Assert.DoesNotContain("SECRET", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", run.Output, StringComparison.Ordinal);

        RunHistoryReadResult history = new PluginRunHistory(Path.GetDirectoryName(_env.HistoryPath)!).Read(10, CliTestEnvironment.Token);
        PluginRunRecord record = history.Records[^1];
        Assert.Equal(RunPublication.Withheld, record.Publication);
        Assert.Equal(Core.Trust.PluginTrustStatus.StaleApproval, record.PublicationTrustStatus);
    }

    [Fact]
    public async Task OutputActionsAreNeverExecuted()
    {
        string actionMarker = _env.MarkerPath("action");
        _env.Write("a.ps1", "out Menu", "out ---", $"out Run | shell=\"{_env.Helper}\" param1=--hold param2=1 param3={actionMarker} refresh=true", "out Again | refresh=true");
        await _env.ApproveAsync("a.ps1");
        JsonElement result = await RunJsonAsync("a.ps1");
        Assert.Equal("command", result.GetProperty("output").GetProperty("items")[0].GetProperty("action").GetProperty("type").GetString());
        await Task.Delay(500, CliTestEnvironment.Token);
        Assert.False(File.Exists(actionMarker));
        Assert.Equal(1, _env.ProcessStarts);
    }

    [Fact]
    public async Task StandardErrorIsShownOnlyOnRequestAndSanitized()
    {
        _env.Write("a.ps1", "err secret \u001B]8;;http://x\u0007tail", "out T");
        await _env.ApproveAsync("a.ps1");
        CliRun hidden = await RunAsync("a.ps1");
        Assert.DoesNotContain("secret", hidden.Output, StringComparison.Ordinal);
        Assert.Contains("use --show-stderr", hidden.Output, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, (await RunJsonAsync("a.ps1")).GetProperty("execution").GetProperty("stderr").GetProperty("text").ValueKind);

        CliRun shown = await RunAsync("a.ps1", "--show-stderr");
        Assert.Contains("| secret ", shown.Output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001B', shown.Output);
        Assert.DoesNotContain('\u0007', shown.Output);
        Assert.Contains("secret", (await RunJsonAsync("a.ps1", "--show-stderr")).GetProperty("execution").GetProperty("stderr").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", File.ReadAllText(_env.HistoryPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHistoryFailureDoesNotChangeTheRunsResult()
    {
        _env.Write("a.ps1", "out Shown");
        await _env.ApproveAsync("a.ps1");
        File.WriteAllText(Path.Combine(_env.Data, "diagnostics"), "a file where the history directory belongs");

        CliRun run = await RunAsync("a.ps1", "--json");
        Assert.Equal(0, run.ExitCode);
        JsonElement result = run.Json.GetProperty("result");
        Assert.Equal("Published", result.GetProperty("publication").GetProperty("status").GetString());
        Assert.Equal("Shown", result.GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
        Assert.False(result.GetProperty("history").GetProperty("recorded").GetBoolean());
        Assert.Contains("TW858", run.Codes);
        Assert.Contains(run.Json.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("source").GetString() == "storage");

        CliRun text = await RunAsync("a.ps1");
        Assert.Contains("NOT recorded", text.Output, StringComparison.Ordinal);
        Assert.Contains("[1] Shown", text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedHistoryFailureDoesNotConcealTheRun()
    {
        _env.Write("a.ps1", "out Shown");
        await _env.ApproveAsync("a.ps1");
        _env.HistoryFactory = _ => throw new InvalidOperationException("SECRET-DETAIL");
        CliRun run = await RunAsync("a.ps1", "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Shown", run.Json.GetProperty("result").GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
        Assert.Equal(["TW858"], run.Codes);
        Assert.DoesNotContain("SECRET", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRunIsRecordedWithoutItsOutput()
    {
        _env.Write("a.ps1", "out PRIVATE-TITLE | tooltip=PRIVATE-TIP", "out ---", "out x | shell=C:\\x.exe param1=PRIVATE-ARG", "err PRIVATE-ERR");
        _env.Write("pending.ps1", "out y");
        await _env.ApproveAsync("a.ps1");
        Assert.Equal(0, (await RunAsync("a.ps1")).ExitCode);
        Assert.Equal(4, (await RunAsync("pending.ps1")).ExitCode);

        string history = File.ReadAllText(_env.HistoryPath);
        foreach (string secret in new[] { "PRIVATE", _env.Root, _env.Helper, "x.exe" })
        {
            Assert.DoesNotContain(secret, history, StringComparison.OrdinalIgnoreCase);
        }

        JsonElement logs = (await _env.JsonAsync("logs")).GetProperty("result");
        JsonElement[] records = [.. logs.GetProperty("records").EnumerateArray()];
        Assert.Equal(2, records.Length);
        Assert.Equal("Succeeded", records[0].GetProperty("status").GetString());
        Assert.Equal("TrustDenied", records[1].GetProperty("status").GetString());
        Assert.True(records[0].GetProperty("isConfiguredRoot").GetBoolean());
        Assert.Equal("a.ps1", records[0].GetProperty("pluginId").GetString());
    }

    [Fact]
    public async Task ExitCodesDistinguishThePluginFromTrayWisp()
    {
        _env.Write("a.ps1", "exit 4");
        await _env.ApproveAsync("a.ps1");
        CliRun run = await RunAsync("a.ps1", "--json");
        Assert.Equal(6, run.ExitCode);
        Assert.Equal(6, run.Json.GetProperty("exitCode").GetInt32());
        Assert.Equal(4, run.Json.GetProperty("result").GetProperty("execution").GetProperty("pluginExitCode").GetInt32());
    }

    private static async Task WaitForFileAsync(string path)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "The plugin did not start.");
            await Task.Delay(20, CliTestEnvironment.Token);
        }
    }
}
