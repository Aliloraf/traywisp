using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Tests.Plugins;
using TrayWisp.Runtime.Tests.Trust;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// Every launch is authorized by a fresh authoritative trust check, and nothing that is denied starts a process.
// Denials are verified through the process-start hook and a marker file the plugin would create, not only through
// the returned status.
[SupportedOSPlatform("windows")]
public sealed class ExecutionAuthorizationTests : IDisposable
{
    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private string[] MarkerScript(string name) => [$"write-file {_env.MarkerPath(name)}|ran", "out ok"];

    private void AssertNotStarted(PluginExecutionResult result, string marker, PluginExecutionStatus status = PluginExecutionStatus.TrustDenied, bool authorized = false)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(0, _env.ProcessStarts);
        Assert.False(File.Exists(_env.MarkerPath(marker)));
        Assert.Null(result.Output);
        Assert.Equal(authorized, result.Approval is not null);
        Assert.Equal(authorized, result.ValidatedFilePath is not null);
        Assert.Null(result.StartedAtUtc);
        Assert.Null(result.ExitCode);
        Assert.Equal(ProcessCleanupStatus.NotStarted, result.Cleanup.Status);
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
    }

    [Fact]
    public async Task APendingPluginDoesNotRun()
    {
        _env.Write("a.ps1", MarkerScript("a"));

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        AssertNotStarted(result, "a");
        Assert.Equal(PluginTrustStatus.PendingApproval, result.TrustStatus);
        Assert.Contains(result.TrustDiagnostics, d => d.Code == TrustDiagnosticCode.ApprovalPending);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.TrustDenied);
    }

    [Fact]
    public async Task AnApprovedPluginRunsUnderItsApprovalIdentity()
    {
        PluginApproval approval = _env.Approved("a.ps1", MarkerScript("a"));

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, _env.ProcessStarts);
        Assert.True(File.Exists(_env.MarkerPath("a")));
        Assert.Equal(approval.Identity, result.Approval);
        Assert.Equal(PluginTrustStatus.Approved, result.TrustStatus);
        Assert.Empty(result.TrustDiagnostics);
        Assert.Equal("ok", Assert.Single(result.Output!.Titles).Item.Text);
        Assert.Equal(_env.Trust.PluginPath("a.ps1"), result.ValidatedFilePath);
    }

    [Fact]
    public async Task AModifiedPluginDoesNotRunAndLosesItsApproval()
    {
        _env.Approved("a.ps1", MarkerScript("a"));
        _env.Write("a.ps1", [.. MarkerScript("a"), "out changed"]);

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        AssertNotStarted(result, "a");
        Assert.Equal(PluginTrustStatus.ContentChanged, result.TrustStatus);
        Assert.Contains(result.TrustDiagnostics, d => d.Code == TrustDiagnosticCode.ContentChanged);
        Assert.Equal(PluginTrustStatus.PendingApproval, _env.Trust.Check(_env.TrustService, "a.ps1").Status);
    }

    [Fact]
    public async Task ARevokedPluginDoesNotRun()
    {
        _env.Approved("a.ps1", MarkerScript("a"));
        Assert.Equal(PluginRevocationStatus.Revoked, _env.TrustService.Revoke(_env.Root, PluginId.Create("a.ps1"), ExecutionTestEnvironment.Token).Status);

        AssertNotStarted(await _env.RunAsync(_env.Service(), "a.ps1"), "a");
    }

    [Fact]
    public async Task ACorruptTrustStoreDeniesEveryPlugin()
    {
        _env.Approved("a.ps1", MarkerScript("a"));
        File.WriteAllText(_env.Trust.StorePath, "{ not json");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        AssertNotStarted(result, "a");
        Assert.Equal(PluginTrustStatus.StoreUnavailable, result.TrustStatus);
        Assert.Contains(result.TrustDiagnostics, d => d.Code == TrustDiagnosticCode.StoreInvalid);
    }

    [Fact]
    public async Task ALockedTrustStoreDeniesThePlugin()
    {
        _env.Approved("a.ps1", MarkerScript("a"));
        PluginTrustService impatient = _env.Trust.Service(new PluginTrustOptions { LockTimeout = TimeSpan.Zero });
        using var holder = new FileStream(_env.Trust.StorePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        PluginExecutionResult result = await _env.RunAsync(_env.Service(trust: impatient), "a.ps1");

        AssertNotStarted(result, "a");
        Assert.Contains(result.TrustDiagnostics, d => d.Code == TrustDiagnosticCode.StoreLockTimeout);
    }

    [Fact]
    public async Task AnUnsavedInvalidationKeepsDenyingEvenAfterTheApprovedBytesReturn()
    {
        string[] original = MarkerScript("a");
        _env.Approved("a.ps1", original);
        _env.Write("a.ps1", [.. original, "out changed"]);
        File.SetAttributes(_env.Trust.StorePath, FileAttributes.ReadOnly);
        _env.Trust.OnDispose(() => File.SetAttributes(_env.Trust.StorePath, FileAttributes.Normal));
        PluginExecutionService service = _env.Service();

        PluginExecutionResult changed = await _env.RunAsync(service, "a.ps1");
        _env.Write("a.ps1", original);
        PluginExecutionResult restored = await _env.RunAsync(service, "a.ps1");

        AssertNotStarted(changed, "a");
        AssertNotStarted(restored, "a");
        Assert.Contains(changed.TrustDiagnostics, d => d.Code == TrustDiagnosticCode.InvalidationNotPersisted);
        Assert.Equal(PluginTrustStatus.ContentChanged, restored.TrustStatus);
    }

    [Fact]
    public async Task OutputIsTiedToTheProducingApprovalAndBecomesStaleAfterReapproval()
    {
        PluginApproval first = _env.Approved("a.ps1", "out menu");
        PluginExecutionService service = _env.Service();
        PluginExecutionResult before = await _env.RunAsync(service, "a.ps1");

        _env.TrustService.Revoke(_env.Root, PluginId.Create("a.ps1"), ExecutionTestEnvironment.Token);
        PluginApproval second = _env.Trust.Approve(_env.TrustService, "a.ps1");
        PluginExecutionResult after = await _env.RunAsync(service, "a.ps1");

        Assert.Equal(first.Identity, before.Approval);
        Assert.Equal(second.Identity, after.Approval);
        Assert.NotEqual(before.Approval, after.Approval);
        Assert.Equal(PluginTrustStatus.StaleApproval, _env.TrustService.CheckTrust(before.Approval!, ExecutionTestEnvironment.Token).Status);
        Assert.Equal(PluginTrustStatus.Approved, _env.TrustService.CheckTrust(after.Approval!, ExecutionTestEnvironment.Token).Status);
    }

    [Theory]
    [InlineData("tool.sh")]
    [InlineData("tool.bat")]
    [InlineData("tool.cmd")]
    public async Task UnsupportedLanguagesNeverStartAnything(string plugin)
    {
        _env.Approved(plugin, MarkerScript("a"));

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), plugin);

        AssertNotStarted(result, "a", PluginExecutionStatus.UnsupportedLanguage);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.UnsupportedLanguage);
        Assert.Null(result.Interpreter);
    }

    [Fact]
    public async Task AnInvalidConfiguredInterpreterStartsNothing()
    {
        _env.Approved("a.py", MarkerScript("a"));
        var options = new PluginExecutionOptions { PythonPath = Path.Combine(_env.Trust.Base, "missing", "python.exe") };

        PluginExecutionResult result = await _env.RunAsync(_env.Service(options), "a.py");

        AssertNotStarted(result, "a", PluginExecutionStatus.InterpreterConfigurationInvalid);
        Assert.Null(result.TrustStatus);
    }

    [Fact]
    public async Task AnUnavailableInterpreterStartsNothing()
    {
        _env.Approved("a.js", MarkerScript("a"));
        var hooks = new ExecutionTestHooks { SearchLocations = new InterpreterSearchLocations(PathVariable: string.Empty, ProgramFiles: null, SystemDirectory: null) };

        PluginExecutionResult result = await _env.RunAsync(_env.Service(PluginExecutionOptions.Default, hooks), "a.js");

        AssertNotStarted(result, "a", PluginExecutionStatus.InterpreterUnavailable);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.InterpreterNotFound);
    }

    [Fact]
    public async Task AFakeInterpreterInsideThePluginRootIsNeverUsed()
    {
        _env.Approved("a.py", MarkerScript("a"));
        string local = Path.Combine(_env.Root, "python.exe");
        File.Copy(_env.Helper, local);
        var hooks = new ExecutionTestHooks { SearchLocations = new InterpreterSearchLocations(_env.Root, null, null) };

        PluginExecutionResult result = await _env.RunAsync(_env.Service(PluginExecutionOptions.Default, hooks), "a.py");

        AssertNotStarted(result, "a", PluginExecutionStatus.InterpreterUnavailable);
    }

    [Fact]
    public async Task CancellationBeforeTheRunStartsNothing()
    {
        _env.Approved("a.ps1", MarkerScript("a"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", token: cancelled.Token);

        AssertNotStarted(result, "a", PluginExecutionStatus.Cancelled);
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task CancellationDuringTheTrustCheckStartsNothingAndReleasesTheStore()
    {
        _env.Approved("a.ps1", MarkerScript("a"));
        using var cancellation = new CancellationTokenSource();
        PluginTrustService trust = _env.Trust.Service(hooks: new TrustTestHooks { StoreLocked = cancellation.Cancel });

        PluginExecutionResult result = await _env.RunAsync(_env.Service(trust: trust), "a.ps1", token: cancellation.Token);

        AssertNotStarted(result, "a", PluginExecutionStatus.Cancelled);
        _env.Trust.AssertStoreDirectoryReleased();
        AssertFileReleased(_env.Trust.PluginPath("a.ps1"));
    }

    [Fact]
    public async Task CancellationImmediatelyBeforeTheLaunchStartsNothingAndReleasesTheLease()
    {
        _env.Approved("a.ps1", MarkerScript("a"));
        using var cancellation = new CancellationTokenSource();
        var hooks = new ExecutionTestHooks { BeforeProcessStart = _ => cancellation.Cancel() };

        PluginExecutionResult result = await _env.RunAsync(_env.Service(hooks: hooks), "a.ps1", token: cancellation.Token);

        // The run had been authorized, and is reported with that approval, but nothing was started.
        AssertNotStarted(result, "a", PluginExecutionStatus.Cancelled, authorized: true);
        AssertFileReleased(_env.Trust.PluginPath("a.ps1"));
    }

    [Fact]
    public async Task TheLaunchedPathIsTheValidatedOnDiskSpelling()
    {
        _env.Trust.Write(@"Work Dir\Clock.PS1", "report\n");
        _env.Trust.Approve(_env.TrustService, "work dir/clock.ps1");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "WORK DIR/CLOCK.ps1");
        JsonElement report = ExecutionTestEnvironment.Report(result);

        string expected = _env.Trust.PluginPath(@"Work Dir\Clock.PS1");
        Assert.Equal(expected, result.ValidatedFilePath);
        Assert.Equal(expected, report.GetProperty("args").EnumerateArray().Last().GetString());
        Assert.Equal(Path.GetDirectoryName(expected), report.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task AHiddenCaseVariantIsNeverLaunched()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_env.Root, "cs")).FullName;
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(directory), "Case-sensitive directories need the Windows Subsystem for Linux optional component.");
        _env.Write("cs/clock.ps1", "report");
        string hidden = _env.Write("cs/CLOCK.ps1", $"write-file {_env.MarkerPath("hidden")}|ran", "report");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        _env.Trust.Approve(_env.TrustService, "cs/clock.ps1");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "cs/CLOCK.ps1");
        JsonElement report = ExecutionTestEnvironment.Report(result);

        Assert.Equal(_env.Trust.PluginPath("cs/clock.ps1"), report.GetProperty("args").EnumerateArray().Last().GetString());
        Assert.False(File.Exists(_env.MarkerPath("hidden")));
    }

    [Fact]
    public async Task ThePluginFileAndItsDirectoriesAreProtectedForTheWholeRunAndReleasedAfterwards()
    {
        string release = _env.MarkerPath("release");
        string started = _env.MarkerPath("started");
        string script = _env.Write("dir/a.ps1", $"write-file {started}|", $"wait-file {release}|20000", "out done");
        _env.Trust.Approve(_env.TrustService, "dir/a.ps1");
        string directory = Path.GetDirectoryName(script)!;

        Task<PluginExecutionResult> run = _env.RunAsync(_env.Service(), "dir/a.ps1", TimeSpan.FromSeconds(30));
        PluginExecutionResult result;
        try
        {
            WaitForFile(started);

            // While the plugin runs: no ordinary write, replacement, deletion or rename of the file or its directory.
            Assert.Throws<IOException>(() => File.WriteAllText(script, "out replaced\n"));
            Assert.Throws<IOException>(() => File.AppendAllText(script, "out appended\n"));
            string replacement = Path.Combine(_env.Trust.Base, "replacement.ps1");
            File.WriteAllText(replacement, "out replaced\n");
            AssertRefused(() => File.Move(replacement, script, overwrite: true));
            AssertRefused(() => File.Delete(script));
            AssertRefused(() => File.Move(script, script + ".moved"));
            AssertRefused(() => Directory.Move(directory, directory + "-moved"));

            // Reading stays possible, for editors and for the interpreter itself.
            Assert.Contains("wait-file", File.ReadAllText(script), StringComparison.Ordinal);
        }
        finally
        {
            File.WriteAllText(release, string.Empty);
            result = await run;
        }

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        AssertFileReleased(script);
        Directory.Move(directory, directory + "-moved");
        Directory.Move(directory + "-moved", directory);
    }

    [Fact]
    public async Task TheLeaseIsReleasedAfterAProcessStartFailure()
    {
        _env.Approved("a.py", "out x");
        string copy = Path.Combine(Directory.CreateDirectory(Path.Combine(_env.Trust.Base, "denied")).FullName, "python.exe");
        File.Copy(_env.Helper, copy);
        _env.Trust.DenyExecuting(copy);

        PluginExecutionResult result = await _env.RunAsync(_env.Service(new PluginExecutionOptions { PythonPath = copy }), "a.py");

        Assert.Equal(PluginExecutionStatus.ProcessStartFailed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.ProcessStartFailed && d.Message.Contains("error 5", StringComparison.Ordinal));
        Assert.Equal(0, _env.ProcessStarts);
        Assert.NotNull(result.Approval);
        Assert.Null(result.Output);
        Assert.Null(result.StartedAtUtc);
        Assert.Equal(ProcessCleanupStatus.NotStarted, result.Cleanup.Status);
        AssertFileReleased(_env.Trust.PluginPath("a.py"));
    }

    [Fact]
    public async Task ANonProgramConfiguredAsInterpreterIsRejectedBeforeAnyLaunch()
    {
        _env.Approved("a.py", MarkerScript("a"));
        string bogus = Path.Combine(Directory.CreateDirectory(Path.Combine(_env.Trust.Base, "bogus")).FullName, "python.exe");
        File.WriteAllText(bogus, "this is not a program");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(new PluginExecutionOptions { PythonPath = bogus }), "a.py");

        AssertNotStarted(result, "a", PluginExecutionStatus.InterpreterConfigurationInvalid);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.ConfiguredInterpreterInvalid && d.Message.Contains("not a Windows program", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLeaseIsReleasedAfterANonZeroExitAndATimeout()
    {
        _env.Approved("fail.ps1", "out x", "exit 3");
        _env.Approved("slow.ps1", "sleep 60000");
        PluginExecutionService service = _env.Service(hooks: new ExecutionTestHooks { MinTimeout = TimeSpan.FromMilliseconds(200) });

        PluginExecutionResult failed = await _env.RunAsync(service, "fail.ps1");
        PluginExecutionResult slow = await _env.RunAsync(service, "slow.ps1", TimeSpan.FromMilliseconds(300));

        Assert.Equal(PluginExecutionStatus.NonZeroExitCode, failed.Status);
        Assert.Equal(PluginExecutionStatus.TimedOut, slow.Status);
        AssertFileReleased(_env.Trust.PluginPath("fail.ps1"));
        AssertFileReleased(_env.Trust.PluginPath("slow.ps1"));
    }

    [Fact]
    public void ALeaseIsOnlyIssuedForAnApprovedCheck()
    {
        _env.Write("a.ps1", "out x");
        PluginTrustDecision pending = _env.TrustService.CheckTrustForExecution(_env.Root, PluginId.Create("a.ps1"), ExecutionTestEnvironment.Token, out PluginExecutionLease? none);
        Assert.False(pending.IsApproved);
        Assert.Null(none);

        _env.Trust.Approve(_env.TrustService, "a.ps1");
        PluginTrustDecision approved = _env.TrustService.CheckTrustForExecution(_env.Root, PluginId.Create("a.ps1"), ExecutionTestEnvironment.Token, out PluginExecutionLease? lease);
        Assert.True(approved.IsApproved);
        Assert.NotNull(lease);
        Assert.True(lease.HoldsHandles);
        Assert.Equal(approved.ValidatedFilePath, lease.ValidatedFilePath);
        Assert.Equal(approved.Approval, lease.Approval);

        // The store lock is not part of the lease: other trust operations proceed while it is held.
        Assert.Equal(PluginTrustStatus.Approved, _env.Trust.Check(_env.TrustService, "a.ps1").Status);
        _env.Trust.AssertStoreDirectoryReleased();

        lease.Dispose();
        Assert.False(lease.HoldsHandles);
        lease.Dispose();
        AssertFileReleased(_env.Trust.PluginPath("a.ps1"));
    }

    // Windows reports a refused replacement or deletion as a sharing violation or as access denied.
    private static void AssertRefused(Action action)
    {
        Exception exception = Assert.ThrowsAny<Exception>(action);
        Assert.True(exception is IOException or UnauthorizedAccessException, exception.ToString());
    }

    // Opening for writing and deleting (by renaming away and back) succeed only when nothing holds the file.
    private static void AssertFileReleased(string path)
    {
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        File.Move(path, path + ".released");
        File.Move(path + ".released", path);
    }

    private static void WaitForFile(string path)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "The plugin did not start.");
            Thread.Sleep(10);
        }
    }
}
