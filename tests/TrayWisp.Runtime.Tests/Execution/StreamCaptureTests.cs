using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Runtime.Execution;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// The bounded capture of one output stream, against in-memory streams that deliver data in controlled pieces.
public sealed class StreamCaptureTests
{
    private const int Limit = 100;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(StreamCapture Capture, int Overflows, int Failures)> CaptureAsync(Stream stream, bool keepTail, int limit = Limit)
    {
        var capture = new StreamCapture(limit, keepTail);
        int overflows = 0;
        int failures = 0;
        capture.Start(stream, onRead: null, () => overflows++, () => failures++);
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(30), Token);
        return (capture, overflows, failures);
    }

    [Fact]
    public async Task EmptyStreamIsCompleteAndEmpty()
    {
        (StreamCapture capture, int overflows, int failures) = await CaptureAsync(new MemoryStream(), keepTail: false);
        Assert.True(capture.ReachedEnd);
        Assert.False(capture.Overflowed);
        Assert.Equal(0, capture.TotalBytes);
        Assert.Empty(capture.Freeze());
        Assert.Equal(0, overflows + failures);
    }

    [Theory]
    [InlineData(Limit - 1)]
    [InlineData(Limit)]
    public async Task OutputUpToTheLimitIsComplete(int length)
    {
        byte[] data = Bytes(length);
        (StreamCapture capture, int overflows, _) = await CaptureAsync(new ChunkedStream(data, 7), keepTail: false);
        Assert.True(capture.ReachedEnd);
        Assert.False(capture.Overflowed);
        Assert.Equal(0, overflows);
        Assert.Equal(length, capture.TotalBytes);
        Assert.Equal(data, capture.Freeze());
    }

    [Fact]
    public async Task OneByteAboveTheLimitOverflowsAndRetainsOnlyTheLimit()
    {
        byte[] data = Bytes(Limit + 1);
        var stream = new ChunkedStream(data, 1000);
        (StreamCapture capture, int overflows, _) = await CaptureAsync(stream, keepTail: false);

        Assert.True(capture.Overflowed);
        Assert.Equal(1, overflows);
        Assert.Equal(data[..Limit], capture.Freeze());

        // The look-ahead never asked for more than one byte past the limit while the output could still be complete.
        Assert.Equal(Limit + 1, stream.LargestRequestBeforeOverflow(Limit + 1));
    }

    [Fact]
    public async Task AfterAnOverflowTheStreamIsDrainedToItsEndWithoutRetainingMore()
    {
        byte[] data = Bytes(Limit * 50);
        (StreamCapture capture, int overflows, _) = await CaptureAsync(new ChunkedStream(data, 33), keepTail: false);

        Assert.True(capture.Overflowed);
        Assert.True(capture.ReachedEnd);
        Assert.Equal(1, overflows);
        Assert.Equal(data.Length, capture.TotalBytes);
        Assert.Equal(Limit, capture.Freeze().Length);
    }

    [Fact]
    public async Task TailModeKeepsTheLastBytesAcrossWrapAround()
    {
        byte[] data = Bytes(Limit * 3 + 17);
        (StreamCapture capture, int overflows, _) = await CaptureAsync(new ChunkedStream(data, 13), keepTail: true);

        Assert.True(capture.ReachedEnd);
        Assert.True(capture.Overflowed);
        Assert.Equal(0, overflows);
        Assert.Equal(data[^Limit..], capture.Freeze());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(Limit)]
    [InlineData(Limit + 5)]
    public async Task TailModeHandlesChunksOfAnySize(int chunk)
    {
        byte[] data = Bytes(Limit * 2 + 3);
        (StreamCapture capture, _, _) = await CaptureAsync(new ChunkedStream(data, chunk), keepTail: true);
        Assert.Equal(data[^Limit..], capture.Freeze());
    }

    [Fact]
    public async Task TailModeBelowTheLimitKeepsEverything()
    {
        byte[] data = Bytes(Limit - 3);
        (StreamCapture capture, _, _) = await CaptureAsync(new ChunkedStream(data, 9), keepTail: true);
        Assert.False(capture.Overflowed);
        Assert.Equal(data, capture.Freeze());
    }

    [Fact]
    public async Task MultibyteCharactersSplitAcrossReadsAreKeptByteForByte()
    {
        byte[] data = Encoding.UTF8.GetBytes("\u00E9\uD83D\uDE80\u65E5 menu\n---\nitem");
        (StreamCapture capture, _, _) = await CaptureAsync(new ChunkedStream(data, 1), keepTail: false);
        Assert.Equal(data, capture.Freeze());
    }

    [Fact]
    public async Task AReadFailureIsRecordedWithoutItsMessage()
    {
        var stream = new ChunkedStream(Bytes(10), 4) { FailAfter = 8, FailureMessage = "secret value" };
        (StreamCapture capture, _, int failures) = await CaptureAsync(stream, keepTail: false);

        Assert.Equal(1, failures);
        Assert.False(capture.ReachedEnd);
        Assert.NotNull(capture.Failure);
        Assert.DoesNotContain("secret", capture.Failure, StringComparison.Ordinal);
        Assert.Contains("IOException", capture.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreezingStopsRetainingWhateverIsStillRead()
    {
        using var gate = new SemaphoreSlim(0);
        var stream = new ChunkedStream(Bytes(50), 10) { Gate = gate };
        var capture = new StreamCapture(Limit, keepTail: false);
        capture.Start(stream, onRead: null, static () => { }, static () => { });

        gate.Release();
        while (capture.TotalBytes < 10)
        {
            await Task.Delay(1, Token);
        }

        byte[] frozen = capture.Freeze();
        gate.Release(10);
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(10, frozen.Length);
        Assert.Equal(10, capture.Freeze().Length);
        Assert.Equal(10, capture.TotalBytes);
        Assert.False(capture.ReachedEnd);
    }

    [Fact]
    public async Task AStopRequestedBeforeTheFirstReadReadsNothing()
    {
        var capture = new StreamCapture(Limit, keepTail: false);
        capture.RequestStop();
        capture.Start(new ChunkedStream(Bytes(10), 10), onRead: null, static () => { }, static () => { });
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(0, capture.TotalBytes);
        Assert.False(capture.ReachedEnd);
        Assert.Null(capture.Failure);
    }

    // The investigation of interrupting reads of a Windows anonymous pipe, read through FileStream exactly as Process
    // exposes redirected output, while a writer keeps the pipe open: RequestStop cancels the blocked synchronous read.
    [Fact]
    public async Task ABlockedAnonymousPipeReadIsInterruptedByAStopRequest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows anonymous pipes are what Process uses on Windows.");
        using var server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var writer = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
        using var reader = new FileStream(new SafeFileHandle(server.SafePipeHandle.DangerousGetHandle(), ownsHandle: false), FileAccess.Read, 4096, isAsync: false);
        var capture = new StreamCapture(Limit, keepTail: true);
        int failures = 0;
        capture.Start(reader, onRead: null, static () => { }, () => failures++);
        writer.Write(Bytes(5));
        while (capture.TotalBytes < 5)
        {
            await Task.Delay(1, Token);
        }

        // The loop is now blocked in its next read. Requests are repeated, as the execution engine does.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!capture.Completion.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            capture.RequestStop();
            await Task.WhenAny(capture.Completion, Task.Delay(20, Token));
        }

        Assert.True(capture.Completion.IsCompleted, "The blocked pipe read was not interrupted.");
        Assert.False(capture.ReachedEnd);
        Assert.Equal(0, failures);
        Assert.Null(capture.Failure);
        Assert.Equal(Bytes(5), capture.Freeze());
    }

    [Fact]
    public async Task APipeReachesItsEndWhenTheWriterCloses()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows anonymous pipes are what Process uses on Windows.");
        using var server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var writer = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
        using var reader = new FileStream(new SafeFileHandle(server.SafePipeHandle.DangerousGetHandle(), ownsHandle: false), FileAccess.Read, 4096, isAsync: false);
        var capture = new StreamCapture(Limit, keepTail: false);
        capture.Start(reader, onRead: null, static () => { }, static () => { });

        writer.Write(Bytes(Limit));
        writer.Dispose();
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.True(capture.ReachedEnd);
        Assert.False(capture.Overflowed);
        Assert.Equal(Bytes(Limit), capture.Freeze());
    }

    private static byte[] Bytes(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)('a' + (i % 26));
        }

        return data;
    }

    // Returns at most `chunk` bytes per read, optionally waiting on a gate before each read or failing at an offset.
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private readonly List<(long Position, int Requested)> _requests = [];
        private int _position;

        public SemaphoreSlim? Gate { get; init; }

        public int FailAfter { get; init; } = -1;

        public string FailureMessage { get; init; } = "failed";

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        // The largest number of bytes requested by any read that started before `position` bytes had been delivered.
        public int LargestRequestBeforeOverflow(long position) =>
            _requests.Where(request => request.Position < position).Select(request => (int)request.Position + request.Requested).Max();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Gate?.Wait();
            _requests.Add((_position, count));
            if (FailAfter >= 0 && _position >= FailAfter)
            {
                throw new IOException(FailureMessage);
            }

            int length = Math.Min(Math.Min(chunk, count), data.Length - _position);
            data.AsSpan(_position, length).CopyTo(buffer.AsSpan(offset));
            _position += length;
            return length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
