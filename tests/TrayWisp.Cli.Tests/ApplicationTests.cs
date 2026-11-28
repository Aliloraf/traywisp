using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace TrayWisp.Cli.Tests;

// Help, version, usage errors and the JSON contract of the command application, run in-process.
[SupportedOSPlatform("windows")]
public sealed class ApplicationTests : IDisposable
{
    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpSucceedsOnStandardOutput(params string[] args)
    {
        CliRun run = await _env.RunAsync(args);
        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("Usage: traywisp COMMAND", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task VersionIsTheInformationalVersion()
    {
        CliRun run = await _env.RunAsync("--version");
        string expected = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"traywisp {expected}\n", run.Output);
    }

    [Fact]
    public async Task HelpVersionAndInvalidRequestsHaveNoSideEffects()
    {
        string[] before = _env.Snapshot();
        string[][] invocations =
        [
            [], ["--help"], ["--version"], ["list", "--help"], ["run", "a.ps1", "--help"], ["approve", "a.ps1"], ["run", "a.ps1", "--timeout", "0"],
            ["run", "a.ps1", "--bogus"], ["status", "--root", "relative"], ["nope"], ["doctor", "x"], ["approve", "a.ps1", "--sha256", "abc"],
        ];
        foreach (string[] args in invocations)
        {
            await _env.RunAsync(args);
        }

        Assert.Equal(before, _env.Snapshot());
        Assert.False(Directory.Exists(_env.Local));
        Assert.False(Directory.Exists(_env.Data));
        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task UsageErrorsGoToStandardErrorWithExitCode2()
    {
        CliRun run = await _env.RunAsync("list", "--bogus");
        Assert.Equal(2, run.ExitCode);
        Assert.Empty(run.Output);
        Assert.StartsWith("traywisp: error: Unknown option '--bogus'", run.Error, StringComparison.Ordinal);
        Assert.Contains("Usage: traywisp list", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageErrorsAreJsonWhenJsonIsRequested()
    {
        CliRun run = await _env.RunAsync("run", "--json", "--timeout", "999");
        Assert.Equal(2, run.ExitCode);
        JsonElement json = run.Json;
        Assert.Equal("run", json.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("result").ValueKind);
        Assert.Equal(["TW850"], run.Codes);

        CliRun unknown = await _env.RunAsync("frobnicate", "--json");
        Assert.Equal(JsonValueKind.Null, unknown.Json.GetProperty("command").ValueKind);
    }

    [Fact]
    public async Task HelpIsTextEvenWithJson()
    {
        CliRun run = await _env.RunAsync("list", "--json", "--help");
        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("Usage: traywisp list", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultsAreUnderLocalApplicationData()
    {
        // The stand-in %LOCALAPPDATA% does not exist, so the default root is reported missing and nothing is created.
        CliRun run = await _env.RunAsync("list", "--json");
        Assert.Equal(3, run.ExitCode);
        Assert.Equal(Path.Combine(_env.Local, "TrayWisp", "plugins"), run.Json.GetProperty("result").GetProperty("root").GetString());
        Assert.False(Directory.Exists(_env.Local));

        JsonElement doctor = (await _env.RunAsync("doctor", "--json")).Json.GetProperty("result");
        Assert.Equal(Path.Combine(_env.Local, "TrayWisp", "trust"), doctor.GetProperty("trustStore").GetProperty("directory").GetString());
        Assert.Equal(Path.Combine(_env.Local, "TrayWisp", "diagnostics"), doctor.GetProperty("runHistory").GetProperty("directory").GetString());
        Assert.Equal("default", doctor.GetProperty("pluginRoot").GetProperty("source").GetString());
        Assert.False(Directory.Exists(_env.Local));
    }

    [Fact]
    public void TheDefaultTrustStoreIsThePhase3Default()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        Assert.Equal(Path.Combine(local, "TrayWisp", "trust", "approvals.json"), Runtime.Trust.PluginTrustService.GetDefaultStorePath());
        Assert.Equal(Path.Combine(local, "TrayWisp", "diagnostics"), Runtime.Diagnostics.PluginRunHistory.GetDefaultDirectory());
        var paths = new CliPaths(Core.Trust.PluginRoot.Create(Path.Combine(local, "TrayWisp", "plugins")), true, Core.Trust.PluginRoot.Create(Path.Combine(local, "TrayWisp")), true);
        Assert.Equal(Runtime.Trust.PluginTrustService.GetDefaultStorePath(), paths.TrustStorePath);
    }

    [Fact]
    public async Task MissingLocalApplicationDataNeedsExplicitLocations()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        CliEnvironment environment = _env.Environment(output, error);
        environment = new CliEnvironment
        {
            Out = output,
            Error = error,
            OpenInput = environment.OpenInput,
            InputRedirected = true,
            LocalApplicationData = () => null,
        };
        int exit = await CliApplication.RunAsync(["status", "--json"], environment, CliTestEnvironment.Token);
        Assert.Equal(5, exit);
        Assert.Contains("TW853", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeACommandIsReportedAs130()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        CliRun run = await _env.RunAsync(cancelled.Token, ["status", .. _env.Locations, "--json"]);
        Assert.Equal(130, run.ExitCode);
        Assert.Equal(["TW852"], run.Codes);
    }

    [Fact]
    public async Task ABrokenOutputStreamEndsWithExitCode1()
    {
        using var error = new StringWriter();
        CliEnvironment environment = _env.Environment(new StringWriter(), error);
        environment = new CliEnvironment
        {
            Out = new BrokenWriter(),
            Error = error,
            OpenInput = environment.OpenInput,
            InputRedirected = true,
            LocalApplicationData = () => _env.Local,
        };
        Assert.Equal(1, await CliApplication.RunAsync(["list", "--root", _env.Root, "--json"], environment, CliTestEnvironment.Token));
        Assert.Equal(1, await CliApplication.RunAsync(["--help"], environment, CliTestEnvironment.Token));
    }

    [Fact]
    public async Task UnexpectedFailuresAreOneDocumentWithoutMessagesOrStackTraces()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        CliEnvironment environment = _env.Environment(output, error);
        environment = new CliEnvironment
        {
            Out = output,
            Error = error,
            OpenInput = environment.OpenInput,
            InputRedirected = true,
            LocalApplicationData = () => _env.Local,
            CreateTrustService = _ => throw new InvalidOperationException("SECRET-DETAIL at C:\\private"),
        };
        int exit = await CliApplication.RunAsync(["status", .. _env.Locations, "--json"], environment, CliTestEnvironment.Token);
        Assert.Equal(1, exit);
        var run = new CliRun(exit, output.ToString(), error.ToString());
        Assert.Equal(["TW851"], run.Codes);
        Assert.DoesNotContain("SECRET", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", run.Json.GetProperty("diagnostics")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void Write(string? value) => throw new IOException("The pipe has been ended.");
    }
}
