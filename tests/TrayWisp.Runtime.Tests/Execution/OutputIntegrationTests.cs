using System.Runtime.Versioning;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Runtime.Execution;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// Decoding of captured bytes and integration with the Phase 2 parser, over real redirected pipes.
[SupportedOSPlatform("windows")]
public sealed class OutputIntegrationTests : IDisposable
{
    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private async Task<PluginExecutionResult> RunAsync(params string[] instructions)
    {
        _env.Approved("a.ps1", instructions);
        return await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task EmptyStandardOutputIsASuccessfulEmptyResult()
    {
        PluginExecutionResult result = await RunAsync("exit 0");

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Output!.Titles);
        Assert.Empty(result.Output.Items);
        Assert.Equal(0, result.StandardOutputBytes);
    }

    [Fact]
    public async Task OutputWithoutAFinalLineFeedIsComplete()
    {
        PluginExecutionResult result = await RunAsync("out-nonl Title");
        Assert.Equal("Title", Assert.Single(result.Output!.Titles).Item.Text);
    }

    [Fact]
    public async Task UnicodeIncludingSupplementaryCharactersRoundTrips()
    {
        const string Title = "Caf\u00E9 \u65E5\u672C \uD83D\uDE80";
        PluginExecutionResult result = await RunAsync("out " + Title);
        Assert.Equal(Title, Assert.Single(result.Output!.Titles).Item.Text);
    }

    [Fact]
    public async Task MultibyteSequencesSplitAcrossPipeWritesAreDecoded()
    {
        // The rocket's four bytes arrive in separate writes, and so in separate reads.
        PluginExecutionResult result = await RunAsync("out-hex 41", "out-hex F0", "out-hex 9F 9A", "out-hex 80", "out-hex 0A");
        Assert.Equal("A\uD83D\uDE80", Assert.Single(result.Output!.Titles).Item.Text);
    }

    [Theory]
    [InlineData("EF BB BF 54 69 74 6C 65", StandardOutputEncoding.Utf8WithBom)]
    [InlineData("FF FE 54 00 69 00 74 00 6C 00 65 00", StandardOutputEncoding.Utf16LittleEndian)]
    [InlineData("FE FF 00 54 00 69 00 74 00 6C 00 65", StandardOutputEncoding.Utf16BigEndian)]
    public async Task ByteOrderMarksSelectTheEncoding(string hex, StandardOutputEncoding encoding)
    {
        PluginExecutionResult result = await RunAsync("out-hex " + hex);
        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal(encoding, result.StandardOutputEncoding);
        Assert.Equal("Title", Assert.Single(result.Output!.Titles).Item.Text);
    }

    [Theory]
    [InlineData("52 75 6E 20 7C 20 73 68 65 6C 6C 3D 61 2E 65 78 65 20 70 61 72 61 6D 31 3D 64 6F C3")] // "Run | shell=a.exe param1=do" + a truncated sequence
    [InlineData("54 69 74 6C 65 82 0A")] // a legacy OEM code page byte
    [InlineData("FF FE 41")] // odd UTF-16
    public async Task InvalidEncodingNeverBecomesOutput(string hex)
    {
        PluginExecutionResult result = await RunAsync("out-hex " + hex);

        Assert.Equal(PluginExecutionStatus.OutputDecodingFailed, result.Status);
        Assert.True(result.StandardOutputComplete);
        Assert.Null(result.Output);
        Assert.Empty(result.OutputDiagnostics);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.OutputDecodingFailed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("shell", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverableParserDiagnosticsKeepTheOutput()
    {
        PluginExecutionResult result = await RunAsync("out Title", "out ---", "out Item | color=notacolor", "out Other | bogus=1");

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Output!.Items.Length);
        Assert.Contains(result.OutputDiagnostics, d => d.Code == OutputDiagnosticCode.InvalidParameterValue);
        Assert.Contains(result.OutputDiagnostics, d => d.Code == OutputDiagnosticCode.UnknownParameter);
    }

    [Fact]
    public async Task AFatalParserResultSuppressesTheOutput()
    {
        PluginExecutionResult result = await RunAsync("out Title", "out ~~~", "out Second");

        Assert.Equal(PluginExecutionStatus.OutputParsingFailed, result.Status);
        Assert.Null(result.Output);
        Assert.Contains(result.OutputDiagnostics, d => d.Code == OutputDiagnosticCode.StreamingNotSupported);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.OutputParsingFailed);
    }

    [Fact]
    public async Task ParserLimitsStillApplyToCompleteOutput()
    {
        PluginExecutionResult result = await RunAsync($"out-fill {PluginOutputParser.MaxLineLength + 1}");

        Assert.Equal(PluginExecutionStatus.OutputParsingFailed, result.Status);
        Assert.Contains(result.OutputDiagnostics, d => d.Code == OutputDiagnosticCode.LineTooLong);
    }

    [Fact]
    public async Task ParsedActionsStayInert()
    {
        string marker = _env.MarkerPath("action");
        PluginExecutionResult result = await RunAsync(
            "out Title | refresh=true",
            "out ---",
            $"out Run | shell={_env.Helper} param1=--hold param2=1 param3={marker}",
            "out Open | href=https://example.com/");

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        MenuItem run = Assert.IsType<MenuItem>(result.Output!.Items[0]);
        CommandAction command = Assert.IsType<CommandAction>(run.Action);
        Assert.Equal(_env.Helper, command.Executable);
        Assert.IsType<UrlAction>(Assert.IsType<MenuItem>(result.Output.Items[1]).Action);

        // Only the plugin itself was started: the command was not run, nothing was opened or refreshed.
        Assert.Equal(1, _env.ProcessStarts);
        Assert.False(File.Exists(marker));
    }
}
