using System.Diagnostics;
using System.Runtime.Versioning;
using TrayWisp.Core.Execution;
using TrayWisp.Runtime.Execution;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// The post-launch lifecycle: failures while supervision is set up, exit monitoring faults, and the arbitration of
// completion, the deadline and cancellation when the supervising continuation resumes late. Faults and delays are
// injected deterministically through the execution test hooks.
[SupportedOSPlatform("windows")]
public sealed class SupervisionTests : IDisposable
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(20);

    private static readonly string[] TitleOnly = ["out Title"];

    private static readonly string[] LongRunningTitle = ["out Title", "sleep 60000"];

    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private PluginExecutionService Service(ExecutionTestHooks hooks) => _env.Service(hooks: hooks with { MinTimeout = TimeSpan.FromMilliseconds(100) });

    [Theory]
    [InlineData("JobAssignment")]
    [InlineData("StandardInput")]
    [InlineData("OutputStream")]
    [InlineData("ErrorStream")]
    [InlineData("OutputCapture")]
    [InlineData("ErrorCapture")]
    [InlineData("ExitMonitoring")]
    public async Task ASetupFailureAfterTheStartTerminatesTheProcessAndReleasesEverything(string failingStep)
    {
        SupervisionStep step = Enum.Parse<SupervisionStep>(failingStep);
        _env.Approved("a.ps1", "out started", "sleep 60000");
        int processId = 0;
        Task? captureLoops = null;
        var hooks = new ExecutionTestHooks
        {
            ProcessStarted = process => processId = process.Id,
            BeforeSupervisionStep = current =>
            {
                if (current == step)
                {
                    throw new IOException("Injected setup failure.");
                }
            },
            CaptureLoopsEnded = loops => captureLoops = loops,
        };
        var watch = Stopwatch.StartNew();

        PluginExecutionResult result = await _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromSeconds(60));

        Assert.True(watch.Elapsed < Bounded, $"Elapsed {watch.Elapsed}.");
        Assert.Equal(ExecutionTerminationReason.SupervisionFailure, result.TerminationReason);
        Assert.Null(result.Output);
        Assert.False(result.StandardOutputComplete);
        ExecutionDiagnostic failure = Assert.Single(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.SupervisionFailed);
        Assert.Contains("IOException", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Injected", failure.Message, StringComparison.Ordinal);
        Assert.True(result.Cleanup.RootExitConfirmed);

        // Without a read of both streams, their closing cannot be confirmed, and that is reported.
        bool bothCaptured = step == SupervisionStep.ExitMonitoring;
        Assert.Equal(bothCaptured ? PluginExecutionStatus.SupervisionFailed : PluginExecutionStatus.CleanupIncomplete, result.Status);
        Assert.Equal(bothCaptured ? ProcessCleanupStatus.Terminated : ProcessCleanupStatus.Incomplete, result.Cleanup.Status);
        Assert.Equal(step != SupervisionStep.JobAssignment, result.Cleanup.JobObjectAssigned);

        Assert.True(ProcessIsGone(processId));
        Assert.NotNull(captureLoops);
        await captureLoops.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        AssertReleased(_env.Trust.PluginPath("a.ps1"));
    }

    [Fact]
    public async Task AnExceptionThatIsNotAnOperationalFailurePropagatesAfterCleanup()
    {
        _env.Approved("a.ps1", "sleep 60000");
        int processId = 0;
        var hooks = new ExecutionTestHooks
        {
            ProcessStarted = process => processId = process.Id,
            BeforeSupervisionStep = step => throw new InjectedFatalException(),
        };

        await Assert.ThrowsAsync<InjectedFatalException>(() => _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromSeconds(60)));

        Assert.True(ProcessIsGone(processId));
        AssertReleased(_env.Trust.PluginPath("a.ps1"));
    }

    [Fact]
    public async Task CancellationDuringSetupTerminatesTheProcess()
    {
        _env.Approved("a.ps1", "sleep 60000");
        using var cancellation = new CancellationTokenSource();
        int processId = 0;
        var hooks = new ExecutionTestHooks
        {
            ProcessStarted = process => processId = process.Id,
            BeforeSupervisionStep = step =>
            {
                if (step == SupervisionStep.OutputCapture)
                {
                    cancellation.Cancel();
                }
            },
        };

        PluginExecutionResult result = await _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromSeconds(60), cancellation.Token);

        Assert.Equal(PluginExecutionStatus.Cancelled, result.Status);
        Assert.Equal(ExecutionTerminationReason.Cancellation, result.TerminationReason);
        Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
        Assert.True(ProcessIsGone(processId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFaultedExitMonitorIsNeverTakenAsAnExit(bool longRunning)
    {
        _env.Approved("a.ps1", longRunning ? LongRunningTitle : TitleOnly);
        int processId = 0;
        var hooks = new ExecutionTestHooks
        {
            ProcessStarted = process => processId = process.Id,
            WaitForExit = static (_, _) => Task.FromException(new InvalidOperationException("Injected monitor failure.")),
        };
        var watch = Stopwatch.StartNew();

        PluginExecutionResult result = await _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromSeconds(60));

        Assert.True(watch.Elapsed < Bounded, $"Elapsed {watch.Elapsed}.");
        Assert.Equal(PluginExecutionStatus.SupervisionFailed, result.Status);
        Assert.Equal(ExecutionTerminationReason.SupervisionFailure, result.TerminationReason);
        Assert.Null(result.Output);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.SupervisionFailed && d.Message.Contains("not taken as proof", StringComparison.Ordinal));

        // The exit was then confirmed from the process itself.
        Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
        Assert.True(result.Cleanup.RootExitConfirmed);
        Assert.NotNull(result.ExitCode);
        Assert.True(ProcessIsGone(processId));
    }

    [Fact]
    public async Task CompletionBeforeTheDeadlineSucceedsWhenSupervisionResumesAfterIt()
    {
        _env.Approved("a.ps1", "out Title");
        var hooks = new ExecutionTestHooks
        {
            BeforeDecision = async arbiter =>
            {
                await WaitUntilAsync(() => arbiter.CompletionRecorded);
                await WaitUntilAsync(() => arbiter.TimeUntilDeadline() <= TimeSpan.Zero);
            },
        };

        // Long enough for the helper to start and complete well before the deadline.
        PluginExecutionResult result = await _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromSeconds(3));

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal("Title", Assert.Single(result.Output!.Titles).Item.Text);
    }

    [Fact]
    public async Task CompletionAfterTheDeadlineIsATimeoutEvenWhenSupervisionResumesLate()
    {
        string marker = _env.MarkerPath("go");
        _env.Approved("a.ps1", $"wait-file {marker}|30000", "out Title");
        var hooks = new ExecutionTestHooks
        {
            BeforeDecision = async arbiter =>
            {
                // Resumed by the deadline. Before the supervisor decides, the plugin completes.
                Assert.True(arbiter.TimeUntilDeadline() <= TimeSpan.Zero);
                await File.WriteAllTextAsync(marker, string.Empty);
                await WaitUntilAsync(() => arbiter.CompletionRecorded);
            },
        };

        PluginExecutionResult result = await _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromMilliseconds(500));

        Assert.Equal(PluginExecutionStatus.TimedOut, result.Status);
        Assert.Equal(ExecutionTerminationReason.Timeout, result.TerminationReason);
        Assert.Null(result.Output);
        Assert.False(result.StandardOutputComplete);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task CancellationBeforeCompletionIsKeptWhenCompletionFollowsBeforeSupervisionResumes()
    {
        string marker = _env.MarkerPath("go");
        _env.Approved("a.ps1", "out started", $"wait-file {marker}|30000", "out Title");
        using var cancellation = new CancellationTokenSource();
        var hooks = new ExecutionTestHooks
        {
            StandardOutputRead = _ => cancellation.Cancel(),
            BeforeDecision = async arbiter =>
            {
                Assert.True(arbiter.IsRecorded(RunEvent.Cancellation));
                await File.WriteAllTextAsync(marker, string.Empty);
                await WaitUntilAsync(() => arbiter.CompletionRecorded);
            },
        };

        PluginExecutionResult result = await _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromSeconds(60), cancellation.Token);

        Assert.Equal(PluginExecutionStatus.Cancelled, result.Status);
        Assert.Equal(ExecutionTerminationReason.Cancellation, result.TerminationReason);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task CompletionBeforeCancellationStaysASuccess()
    {
        _env.Approved("a.ps1", "out Title");
        using var cancellation = new CancellationTokenSource();
        var hooks = new ExecutionTestHooks
        {
            BeforeDecision = async arbiter =>
            {
                await WaitUntilAsync(() => arbiter.CompletionRecorded);
                await cancellation.CancelAsync();
            },
        };

        PluginExecutionResult result = await _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromSeconds(60), cancellation.Token);

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.Output);
    }

    [Fact]
    public async Task AnOverflowObservedWithACancellationIsDecidedDeterministically()
    {
        _env.Approved("a.ps1", $"out-fill {PluginExecutionService.MaxStandardOutputBytes + 1}", "sleep 60000");
        for (int i = 0; i < 3; i++)
        {
            using var cancellation = new CancellationTokenSource();

            // Cancels while handling the very chunk that overflows; the overflow was recorded first.
            var hooks = new ExecutionTestHooks
            {
                StandardOutputRead = total =>
                {
                    if (total > PluginExecutionService.MaxStandardOutputBytes)
                    {
                        cancellation.Cancel();
                    }
                },
            };

            PluginExecutionResult result = await _env.RunAsync(Service(hooks), "a.ps1", TimeSpan.FromSeconds(60), cancellation.Token);

            Assert.Equal(PluginExecutionStatus.OutputLimitExceeded, result.Status);
            Assert.Equal(ExecutionTerminationReason.OutputLimit, result.TerminationReason);
            Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
            Assert.Null(result.Output);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "A condition the test waits for did not become true.");
            await Task.Delay(10);
        }
    }

    private static void AssertReleased(string path)
    {
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
    }

    private static bool ProcessIsGone(int processId)
    {
        Assert.NotEqual(0, processId);
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

    // Stands for a failure that is not operational (like running out of memory), which must not become a result.
    private sealed class InjectedFatalException : Exception
    {
    }
}
