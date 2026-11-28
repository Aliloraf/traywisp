using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TrayWisp.Cli.Tests;

// The built traywisp.exe, started as a separate process with redirected streams, as scripts and users run it. Every
// invocation passes explicit --root and --data-dir, so the real per-user folders are never used.
[SupportedOSPlatform("windows")]
public sealed class ProcessTests : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private static string Executable()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "traywisp.exe");
        Assert.True(File.Exists(path), "traywisp.exe was not built next to the tests.");
        return path;
    }

    private static ProcessRun Run(byte[]? input, params string[] args)
    {
        var startInfo = new ProcessStartInfo(Executable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            if (input is not null)
            {
                process.StandardInput.BaseStream.Write(input);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited without reading all of its input.
        }

        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("traywisp did not exit in time.");
        }

        var run = new ProcessRun(process.ExitCode, output.Result, error.Result);
        TestContext.Current.TestOutputHelper?.WriteLine($"traywisp {string.Join(' ', args)} -> {run.ExitCode}\n{run.Output}\n{run.Error}");
        return run;
    }

    private ProcessRun Cli(params string[] args) => Run(null, [.. args, .. _env.Locations]);

    [Fact]
    public void HelpAndVersionUseStandardOutput()
    {
        ProcessRun help = Run(null);
        Assert.Equal(0, help.ExitCode);
        Assert.StartsWith("Usage: traywisp COMMAND", help.Output, StringComparison.Ordinal);
        Assert.Empty(help.Error);

        ProcessRun version = Run(null, "--version");
        Assert.Equal(0, version.ExitCode);
        Assert.Equal($"traywisp {CliApplication.Version}\n", version.Output);
    }

    [Fact]
    public void UsageErrorsUseStandardErrorAndExitCode2()
    {
        ProcessRun run = Run(null, "list", "--no-such-option");
        Assert.Equal(2, run.ExitCode);
        Assert.Empty(run.Output);
        Assert.Contains("Unknown option", run.Error, StringComparison.Ordinal);

        ProcessRun json = Run(null, "approve", "a.ps1", "--json");
        Assert.Equal(2, json.ExitCode);
        Assert.Empty(json.Error);
        Assert.Equal(2, JsonDocument.Parse(json.Output).RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void ApprovalPersistsBetweenInvocationsAndARealRunWorks()
    {
        string marker = _env.MarkerPath("ran");
        _env.Write("clock.5s.ps1", $"write-file {marker}|x", "out Caf\u00E9 \uD83D\uDE80", "out ---", "out Open | href=https://example.com/");
        _env.Write("other.5s.ps1", _env.Marker("other"), "out Other");

        ProcessRun denied = Cli("run", "clock.5s.ps1", "--powershell", _env.Helper);
        Assert.Equal(4, denied.ExitCode);
        Assert.False(File.Exists(marker));

        using JsonDocument inspection = JsonDocument.Parse(Cli("inspect", "clock.5s.ps1", "--json").Output);
        string fingerprint = inspection.RootElement.GetProperty("result").GetProperty("sha256").GetString()!;
        Assert.Equal(0, Cli("approve", "clock.5s.ps1", "--sha256", fingerprint).ExitCode);
        Assert.Equal(0, Cli("approve", "other.5s.ps1", "--sha256", JsonDocument.Parse(Cli("inspect", "other.5s.ps1", "--json").Output).RootElement.GetProperty("result").GetProperty("sha256").GetString()!).ExitCode);

        using JsonDocument status = JsonDocument.Parse(Cli("status", "clock.5s.ps1", "--json").Output);
        Assert.Equal("Approved", status.RootElement.GetProperty("result").GetProperty("plugins")[0].GetProperty("trustStatus").GetString());

        ProcessRun run = Cli("run", "clock.5s.ps1", "--powershell", _env.Helper, "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Error);
        Assert.True(run.Output.All(c => c < 0x80), "JSON output is ASCII.");
        using JsonDocument result = JsonDocument.Parse(run.Output);
        Assert.Equal("Caf\u00E9 \uD83D\uDE80", result.RootElement.GetProperty("result").GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(_env.MarkerPath("other")));

        ProcessRun text = Cli("run", "clock.5s.ps1", "--powershell", _env.Helper);
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("[1] Caf\u00E9 \uD83D\uDE80", text.Output, StringComparison.Ordinal);

        Assert.Equal(0, Cli("revoke", "clock.5s.ps1").ExitCode);
        Assert.Equal(4, Cli("run", "clock.5s.ps1", "--powershell", _env.Helper).ExitCode);

        using JsonDocument logs = JsonDocument.Parse(Cli("logs", "--json").Output);
        Assert.Equal(4, logs.RootElement.GetProperty("result").GetProperty("totalRecords").GetInt32());
        Assert.False(File.Exists(_env.MarkerPath("other")));
    }

    [Fact]
    public void ValidateOutputReadsRedirectedInput()
    {
        ProcessRun valid = Run(Encoding.UTF8.GetBytes("Title\n---\nItem | href=https://example.com/\n"), "validate-output", "--json");
        Assert.Equal(0, valid.ExitCode);
        Assert.Equal("Complete", JsonDocument.Parse(valid.Output).RootElement.GetProperty("result").GetProperty("parseStatus").GetString());

        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("\u65E5\u672C\n")];
        ProcessRun wide = Run(utf16, "validate-output");
        Assert.Equal(0, wide.ExitCode);
        Assert.Contains("[1] \u65E5\u672C", wide.Output, StringComparison.Ordinal);

        ProcessRun invalid = Run([0x41, 0xC3, 0x28], "validate-output");
        Assert.Equal(8, invalid.ExitCode);
        Assert.Contains("not valid UTF-8", invalid.Output, StringComparison.Ordinal);
        Assert.NotEmpty(invalid.Error);

        ProcessRun tooLarge = Run(new byte[(1024 * 1024) + 1], "validate-output", "--json");
        Assert.Equal(8, tooLarge.ExitCode);
    }

    [Fact]
    public void DoctorRunsNoInterpreter()
    {
        string[] before = _env.Snapshot();
        ProcessRun run = Cli("doctor", "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(before, _env.Snapshot());
        Assert.Equal("doctor", JsonDocument.Parse(run.Output).RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void ARootThatDoesNotExistIsExitCode3()
    {
        ProcessRun run = Run(null, "list", "--root", Path.Combine(_env.Base, "missing"));
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("create it explicitly", run.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_env.Base, "missing")));
    }

    // A real interpreter, resolved by the CLI exactly as in production: Python through automatic resolution. Optional by
    // default; required (fails instead of skipping) when TRAYWISP_REQUIRED_INTERPRETERS names Python or "all", as in CI.
    [Fact]
    public void ARealPythonPluginRunsThroughTheCli()
    {
        _env.Write("real.py", "import sys", "sys.stdout.write('Python \\u00e9 ok\\n---\\nItem | href=https://example.com/\\n')");
        Assert.Equal(0, Cli("approve", "real.py", "--sha256", Fingerprint("real.py")).ExitCode);

        ProcessRun run = Cli("run", "real.py", "--json");
        using JsonDocument json = JsonDocument.Parse(run.Output);
        JsonElement execution = json.RootElement.GetProperty("result").GetProperty("execution");
        if (execution.GetProperty("status").GetString() == "InterpreterUnavailable")
        {
            NotRunnable("Python", "No Python interpreter was found.");
        }

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Python", execution.GetProperty("interpreter").GetProperty("kind").GetString());
        Assert.Equal("Python \u00E9 ok", json.RootElement.GetProperty("result").GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
    }

    // Windows PowerShell 5.1, configured explicitly. The effective execution policy applies unchanged: TrayWisp neither
    // sets nor bypasses it, so on a machine whose policy forbids scripts this test skips (or fails when required).
    [Fact]
    public void ARealWindowsPowerShellPluginRunsThroughTheCli()
    {
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell))
        {
            NotRunnable("WindowsPowerShell", "Windows PowerShell is not installed.");
        }

        _env.Write("real.ps1", "Write-Output 'PowerShell ok'", "Write-Output '---'", "Write-Output 'Item'");
        Assert.Equal(0, Cli("approve", "real.ps1", "--sha256", Fingerprint("real.ps1")).ExitCode);
        ProcessRun run = Cli("run", "real.ps1", "--powershell", powershell, "--show-stderr", "--json");
        using JsonDocument json = JsonDocument.Parse(run.Output);
        JsonElement execution = json.RootElement.GetProperty("result").GetProperty("execution");
        if (execution.GetProperty("status").GetString() == "NonZeroExitCode"
            && execution.GetProperty("stderr").GetProperty("text").GetString()!.Contains("about_Execution_Policies", StringComparison.Ordinal))
        {
            NotRunnable("WindowsPowerShell", "The effective PowerShell execution policy does not allow scripts, and TrayWisp deliberately does not bypass it.");
        }

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("WindowsPowerShell", execution.GetProperty("interpreter").GetProperty("kind").GetString());
        Assert.Equal("PowerShell ok", json.RootElement.GetProperty("result").GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
    }

    private string Fingerprint(string id)
    {
        using JsonDocument inspection = JsonDocument.Parse(Cli("inspect", id, "--json").Output);
        return inspection.RootElement.GetProperty("result").GetProperty("sha256").GetString()!;
    }

    private static void NotRunnable(string interpreter, string reason)
    {
        string[] required = (Environment.GetEnvironmentVariable("TRAYWISP_REQUIRED_INTERPRETERS") ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (required.Any(name => name.Equals("all", StringComparison.OrdinalIgnoreCase) || name.Equals(interpreter, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Fail($"{reason} {interpreter} is required by TRAYWISP_REQUIRED_INTERPRETERS, so this test must run.");
        }

        Assert.Skip(reason);
    }

    private sealed record ProcessRun(int ExitCode, string Output, string Error);
}
