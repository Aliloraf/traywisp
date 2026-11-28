using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using TrayWisp.Cli.Commands;
using Xunit;

namespace TrayWisp.Cli.Tests;

// traywisp validate-output: bounded standard input, the engine's strict decoder and the Phase 2 parser. Nothing runs.
[SupportedOSPlatform("windows")]
public sealed class ValidateOutputTests : IDisposable
{
    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private async Task<CliRun> ValidateAsync(byte[] input, params string[] extra)
    {
        _env.Input = input;
        return await _env.RunAsync(["validate-output", .. extra]);
    }

    private async Task<JsonElement> ValidJsonAsync(byte[] input)
    {
        CliRun run = await ValidateAsync(input, "--json");
        Assert.Equal(0, run.ExitCode);
        return run.Json.GetProperty("result");
    }

    [Fact]
    public async Task EmptyInputIsValidEmptyOutput()
    {
        JsonElement result = await ValidJsonAsync([]);
        Assert.Equal("Complete", result.GetProperty("parseStatus").GetString());
        Assert.Equal(0, result.GetProperty("input").GetProperty("bytes").GetInt32());
        Assert.Empty(result.GetProperty("output").GetProperty("titles").EnumerateArray());
    }

    [Theory]
    [InlineData("Utf8", "")]
    [InlineData("Utf8WithBom", "EFBBBF")]
    public async Task Utf8IsAccepted(string encoding, string bom)
    {
        byte[] input = [.. Convert.FromHexString(bom), .. Encoding.UTF8.GetBytes("Caf\u00E9 \uD83D\uDE80\n---\nItem\n")];
        JsonElement result = await ValidJsonAsync(input);
        Assert.Equal(encoding, result.GetProperty("input").GetProperty("encoding").GetString());
        Assert.Equal("Caf\u00E9 \uD83D\uDE80", result.GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Utf16WithAByteOrderMarkIsAccepted(bool littleEndian)
    {
        Encoding encoding = new UnicodeEncoding(bigEndian: !littleEndian, byteOrderMark: true);
        byte[] input = [.. encoding.GetPreamble(), .. encoding.GetBytes("\u65E5\u672C\n")];
        JsonElement result = await ValidJsonAsync(input);
        Assert.Equal(littleEndian ? "Utf16LittleEndian" : "Utf16BigEndian", result.GetProperty("input").GetProperty("encoding").GetString());
        Assert.Equal("\u65E5\u672C", result.GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("41 C3 28 0A")]
    [InlineData("FF FE 00 00 41 00 00 00")]
    [InlineData("FF FE 41")]
    [InlineData("ED A0 80")]
    public async Task InvalidAndUnsupportedEncodingsAreRejected(string hex)
    {
        CliRun run = await ValidateAsync(Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal)), "--json");
        Assert.Equal(8, run.ExitCode);
        Assert.Equal(["TW862"], run.Codes);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("result").GetProperty("output").ValueKind);
    }

    [Fact]
    public async Task InputAtTheLimitIsReadAndInputBeyondItIsRejectedUnparsed()
    {
        byte[] exact = new byte[ValidateOutputCommand.MaxInputBytes];
        Array.Fill(exact, (byte)'a');
        CliRun atLimit = await ValidateAsync(exact, "--json");
        JsonElement input = atLimit.Json.GetProperty("result").GetProperty("input");
        Assert.Equal(ValidateOutputCommand.MaxInputBytes, input.GetProperty("bytes").GetInt32());
        Assert.False(input.GetProperty("limitExceeded").GetBoolean());
        Assert.DoesNotContain("TW860", atLimit.Codes);

        CliRun beyond = await ValidateAsync([.. exact, (byte)'a'], "--json");
        Assert.Equal(8, beyond.ExitCode);
        Assert.Equal(["TW860"], beyond.Codes);
        Assert.True(beyond.Json.GetProperty("result").GetProperty("input").GetProperty("limitExceeded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, beyond.Json.GetProperty("result").GetProperty("parseStatus").ValueKind);
    }

    [Fact]
    public async Task InputIsNeverReadBeyondTheLimit()
    {
        var endless = new EndlessStream();
        _env.InputStream = () => endless;
        CliRun run = await _env.RunAsync("validate-output", "--json");
        Assert.Equal(8, run.ExitCode);
        Assert.Equal(ValidateOutputCommand.MaxInputBytes + 1, endless.BytesRead);
    }

    [Fact]
    public async Task ShortReadsAreAssembled()
    {
        byte[] content = Encoding.UTF8.GetBytes("Title\n---\nOne\n--Child\nTwo\n");
        _env.InputStream = () => new TrickleStream(content);
        CliRun run = await _env.RunAsync("validate-output", "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Child", run.Json.GetProperty("result").GetProperty("output").GetProperty("items")[0].GetProperty("children")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ExplicitTruncationRejectsTheWholeOutput()
    {
        CliRun run = await ValidateAsync(Encoding.UTF8.GetBytes("Title\n---\nItem | href=https://example.com/\n"), "--truncated", "--json");
        Assert.Equal(8, run.ExitCode);
        Assert.Contains("TW405", run.Codes);
        Assert.Equal("Failed", run.Json.GetProperty("result").GetProperty("parseStatus").GetString());
        Assert.True(run.Json.GetProperty("result").GetProperty("input").GetProperty("markedTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("result").GetProperty("output").ValueKind);
    }

    [Fact]
    public async Task FatalParserErrorsRejectEverything()
    {
        CliRun longLine = await ValidateAsync(Encoding.UTF8.GetBytes("T\n" + new string('x', 20_000) + "\n"), "--json");
        Assert.Equal(8, longLine.ExitCode);
        Assert.Contains("TW410", longLine.Codes);

        CliRun streaming = await ValidateAsync(Encoding.UTF8.GetBytes("A\n~~~\nB\n"));
        Assert.Equal(8, streaming.ExitCode);
        Assert.Contains("INVALID", streaming.Output, StringComparison.Ordinal);
        Assert.Contains("rejected as a whole", streaming.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverableDiagnosticsAreShownWithSuccess()
    {
        CliRun run = await ValidateAsync(Encoding.UTF8.GetBytes("T\n---\nA | shell=x.exe bogus=1\nB\n"), "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.NotEmpty(run.Codes);
        JsonElement items = run.Json.GetProperty("result").GetProperty("output").GetProperty("items");
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("action").ValueKind);
        Assert.Equal("B", items[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ActionsStayInertAndNothingRunsOrIsStored()
    {
        string marker = _env.MarkerPath("action");
        string[] before = _env.Snapshot();
        CliRun run = await ValidateAsync(Encoding.UTF8.GetBytes($"T\n---\nGo | shell=\"{_env.Helper}\" param1=--hold param2=1 param3={marker}\n"));
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("action: command (inert, not run)", run.Output, StringComparison.Ordinal);
        Assert.Contains("proves neither that a script is safe nor that it is authorized", run.Output, StringComparison.Ordinal);
        await Task.Delay(300, CliTestEnvironment.Token);
        Assert.False(File.Exists(marker));
        Assert.Equal(before, _env.Snapshot());
        Assert.False(Directory.Exists(_env.Local));
        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task InteractiveInputIsRefusedWithoutReading()
    {
        _env.InputRedirected = false;
        _env.InputStream = () => throw new InvalidOperationException("must not be opened");
        CliRun run = await _env.RunAsync("validate-output");
        Assert.Equal(2, run.ExitCode);
        Assert.Contains("TW859", run.Output, StringComparison.Ordinal);
        Assert.Contains("not redirected", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationWhileWaitingForInputIs130()
    {
        using var blocking = new BlockingStream();
        _env.InputStream = () => blocking;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CliTestEnvironment.Token);
        Task<CliRun> running = _env.RunAsync(cancellation.Token, "validate-output", "--json");
        Assert.True(blocking.ReadStarted.Wait(TimeSpan.FromSeconds(10), CliTestEnvironment.Token));
        await cancellation.CancelAsync();
        CliRun run = await running.WaitAsync(TimeSpan.FromSeconds(10), CliTestEnvironment.Token);
        Assert.Equal(130, run.ExitCode);
        Assert.Equal(["TW852"], run.Codes);
    }

    [Fact]
    public async Task AnUnreadableInputIsAFailure()
    {
        _env.InputStream = () => new FailingStream();
        CliRun run = await _env.RunAsync("validate-output", "--json");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["TW861"], run.Codes);
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrickleStream(byte[] content) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 3));
    }

    private sealed class FailingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("injected");
    }

    // A read that never returns until the stream is disposed, like a pipe whose writer never closes it.
    private sealed class BlockingStream : MemoryStream
    {
        private readonly ManualResetEventSlim _released = new();

        public ManualResetEventSlim ReadStarted { get; } = new();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadStarted.Set();
            _released.Wait();
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _released.Set();
            }

            base.Dispose(disposing);
        }
    }
}
