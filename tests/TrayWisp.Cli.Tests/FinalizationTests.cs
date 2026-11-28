using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using TrayWisp.Cli.Commands;
using Xunit;

namespace TrayWisp.Cli.Tests;

// Cancellation delivery failures, the process's last steps, and abandoned standard input reads.
public sealed class FinalizationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFailingCancellationCallbackIsObservedAndTheSourceIsStillDisposed()
    {
        var cancellation = new ConsoleCancellation();
        CancellationToken token = cancellation.Token;
        token.Register(() => throw new InvalidOperationException("callback failure"));
        bool otherCallbackRan = false;
        token.Register(() => otherCallbackRan = true);

        Assert.True(cancellation.RequestCancellation());
        Exception? failure = await cancellation.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.NotNull(failure);
        Assert.Contains(failure is AggregateException aggregate ? aggregate.InnerExceptions : [failure], e => e is InvalidOperationException);
        Assert.True(otherCallbackRan);
        Assert.False(cancellation.RequestCancellation());

        // Disposal does not throw, and releases the source.
        await cancellation.DisposeAsync();
        await cancellation.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
    }

    [Fact]
    public async Task FinalizationFlushesBothStreamsAndKeepsTheExitCodeAfterADeliveryFailure()
    {
        var cancellation = new ConsoleCancellation();
        cancellation.Token.Register(() => throw new InvalidOperationException("callback failure"));
        cancellation.RequestCancellation();
        using var output = new RecordingWriter();
        using var error = new RecordingWriter();

        int exitCode = await ProcessFinalization.CompleteAsync(cancellation, output, error, ExitCodes.CleanupIncomplete);
        Assert.Equal(ExitCodes.CleanupIncomplete, exitCode);
        Assert.Equal(1, output.Flushes);
        Assert.Equal(1, error.Flushes);
        Assert.Throws<ObjectDisposedException>(() => cancellation.Token.WaitHandle);
    }

    [Fact]
    public async Task AnUndeliverableReportIsAFailureAndTheOtherStreamIsStillFlushed()
    {
        using var output = new RecordingWriter { FailFlush = true };
        using var error = new RecordingWriter();
        Assert.Equal(ExitCodes.Failure, await ProcessFinalization.CompleteAsync(new ConsoleCancellation(), output, error, ExitCodes.Success));
        Assert.Equal(1, error.Flushes);
        Assert.Equal(ExitCodes.Cancelled, await ProcessFinalization.CompleteAsync(new ConsoleCancellation(), new RecordingWriter(), new RecordingWriter(), ExitCodes.Cancelled));
    }

    [Fact]
    public async Task AnAbandonedReadNeverYieldsPartialInputAndStopsBeforeItsNextRead()
    {
        using var stream = new GatedStream([(byte)'T', (byte)'\n']);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<(byte[], int)> reading = ValidateOutputCommand.ReadBoundedAsync(() => stream, cancellation.Token);
        Assert.True(stream.Blocked.Wait(TimeSpan.FromSeconds(10), Token));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        // The blocked read now returns data; the reader stops instead of reading on, and closes the stream.
        stream.Release();
        Assert.True(stream.Disposed.Wait(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(2, stream.Reads);
    }

    [Fact]
    public async Task ALateFaultOfAnAbandonedReadIsObserved()
    {
        string marker = "late input fault " + Guid.NewGuid().ToString("N");
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.AddRange(e.Exception.Flatten().InnerExceptions.Where(x => x.Message == marker));
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var stream = new GatedStream([]) { FailWith = new IOException(marker) };
            await AbandonAsync(stream);
            stream.Release();
            Assert.True(stream.Disposed.Wait(TimeSpan.FromSeconds(10), Token));
            await Task.Delay(100, Token);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // Starts a read and abandons it by cancellation, without keeping a reference to its task.
    private static async Task AbandonAsync(GatedStream stream)
    {
        using var cancellation = new CancellationTokenSource();
        Task<(byte[], int)> reading = ValidateOutputCommand.ReadBoundedAsync(() => stream, cancellation.Token);
        Assert.True(stream.Blocked.Wait(TimeSpan.FromSeconds(10), Token));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    private sealed class RecordingWriter : StringWriter
    {
        public int Flushes { get; private set; }

        public bool FailFlush { get; init; }

        public override void Flush()
        {
            Flushes++;
            if (FailFlush)
            {
                throw new IOException("closed pipe");
            }
        }
    }
}

// Cancellation through the command application, with a real run and a real ConsoleCancellation.
[SupportedOSPlatform("windows")]
public sealed class FinalizationCommandTests : IDisposable
{
    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task AFailedCancellationCallbackIsReportedWithoutChangingTheRunsResult()
    {
        string started = _env.MarkerPath("started");
        _env.Write("long.ps1", $"write-file {started}|x", "sleep 60000");
        await _env.ApproveAsync("long.ps1");
        await using var cancellation = new ConsoleCancellation();
        cancellation.Token.Register(() => throw new InvalidOperationException("SECRET callback detail"));
        _env.CloseCancellation = cancellation.CloseAsync;

        Task<CliRun> running = _env.RunAsync(cancellation.Token, ["run", "long.ps1", .. _env.Locations, .. _env.Interpreters, "--json"]);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(started))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
            await Task.Delay(20, CliTestEnvironment.Token);
        }

        Assert.True(cancellation.RequestCancellation());
        CliRun run = await running;
        Assert.Equal(130, run.ExitCode);
        JsonElement execution = run.Json.GetProperty("result").GetProperty("execution");
        Assert.Equal("Cancelled", execution.GetProperty("status").GetString());
        Assert.Equal("Terminated", execution.GetProperty("cleanup").GetProperty("status").GetString());
        JsonElement warning = run.Json.GetProperty("diagnostics").EnumerateArray().Single(d => d.GetProperty("code").GetString() == "TW851");
        Assert.Equal("warning", warning.GetProperty("severity").GetString());
        Assert.Contains("InvalidOperationException", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompletedRunKeepsItsExitCodeAndTextReport()
    {
        _env.Write("a.ps1", "out Shown");
        await _env.ApproveAsync("a.ps1");
        _env.CloseCancellation = () => Task.FromResult<Exception?>(new AggregateException(new InvalidOperationException("SECRET")));

        CliRun json = await _env.RunAsync(["run", "a.ps1", .. _env.Locations, .. _env.Interpreters, "--json"]);
        Assert.Equal(0, json.ExitCode);
        Assert.Equal("Shown", json.Json.GetProperty("result").GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
        Assert.Contains("TW851", json.Codes);

        CliRun text = await _env.RunAsync(["run", "a.ps1", .. _env.Locations, .. _env.Interpreters]);
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("[1] Shown", text.Output, StringComparison.Ordinal);
        Assert.Contains("Warning TW851 [cli]", text.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text.Output + text.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, text.Error);
    }

    [Fact]
    public async Task CancelledInputIsNeverParsedOrShown()
    {
        using var stream = new GatedStream([.. "SECRET-TITLE\n"u8]);
        _env.InputStream = () => stream;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CliTestEnvironment.Token);
        Task<CliRun> running = _env.RunAsync(cancellation.Token, ["validate-output", "--json"]);
        Assert.True(stream.Blocked.Wait(TimeSpan.FromSeconds(10), CliTestEnvironment.Token));
        await cancellation.CancelAsync();
        CliRun run = await running;
        stream.Release();

        Assert.Equal(130, run.ExitCode);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("result").ValueKind);
        Assert.DoesNotContain("SECRET", run.Output, StringComparison.Ordinal);
        Assert.True(stream.Disposed.Wait(TimeSpan.FromSeconds(10), CliTestEnvironment.Token));
    }
}

// Returns its data on the first read, then blocks the second read until released; that read then returns data again
// (or throws FailWith), as if the writer had finally written more.
internal sealed class GatedStream(byte[] first) : Stream
{
    private readonly ManualResetEventSlim _gate = new();
    private int _reads;

    public ManualResetEventSlim Blocked { get; } = new();

    public ManualResetEventSlim Disposed { get; } = new();

    public Exception? FailWith { get; init; }

    public int Reads => Volatile.Read(ref _reads);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void Release() => _gate.Set();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = Interlocked.Increment(ref _reads);
        if (read == 1 && first.Length > 0)
        {
            first.CopyTo(buffer, offset);
            return first.Length;
        }

        Blocked.Set();
        _gate.Wait();
        if (FailWith is not null)
        {
            throw FailWith;
        }

        buffer[offset] = (byte)'x';
        return 1;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed.Set();
        base.Dispose(disposing);
    }
}
