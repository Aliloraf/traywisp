using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace TrayWisp.Cli.Tests;

// doctor (read-only host checks) and logs (the bounded run history).
[SupportedOSPlatform("windows")]
public sealed class DoctorAndLogsTests : IDisposable
{
    private static readonly string[] AutomaticSources = ["KnownLocation", "SearchPath", "SystemDirectory"];

    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private async Task<JsonElement> DoctorAsync(params string[] args)
    {
        CliRun run = await _env.RunAsync(["doctor", .. args, "--json"]);
        Assert.Equal(0, run.ExitCode);
        return run.Json.GetProperty("result");
    }

    [Fact]
    public async Task DoctorReportsWithoutStartingOrCreatingAnything()
    {
        _env.Write("a.ps1", _env.Marker("a"));
        string[] before = _env.Snapshot();
        JsonElement result = await DoctorAsync([.. _env.Locations, .. _env.Interpreters]);

        Assert.Equal(before, _env.Snapshot());
        Assert.False(Directory.Exists(_env.Data));
        Assert.Equal(0, _env.ProcessStarts);
        Assert.False(File.Exists(_env.MarkerPath("a")));

        Assert.Equal(CliApplication.Version, result.GetProperty("version").GetString());
        Assert.True(result.GetProperty("platform").GetProperty("supported").GetBoolean());
        Assert.Equal("Valid", result.GetProperty("pluginRoot").GetProperty("status").GetString());
        Assert.Equal("option", result.GetProperty("pluginRoot").GetProperty("source").GetString());
        Assert.Equal("Missing", result.GetProperty("trustStore").GetProperty("directoryStatus").GetString());
        Assert.False(result.GetProperty("trustStore").GetProperty("contentChecked").GetBoolean());
        Assert.Equal(["Bash", "Batch"], result.GetProperty("deferredLanguages").EnumerateArray().Select(l => l.GetString()));
        foreach (JsonElement interpreter in result.GetProperty("interpreters").EnumerateArray())
        {
            Assert.Equal("Resolved", interpreter.GetProperty("status").GetString());
            Assert.True(interpreter.GetProperty("configured").GetBoolean());
            Assert.Equal(_env.Helper, interpreter.GetProperty("path").GetString());
            Assert.False(interpreter.GetProperty("authenticated").GetBoolean());
            Assert.False(interpreter.GetProperty("versionChecked").GetBoolean());
        }

        Assert.False(result.GetProperty("interpreters")[0].GetProperty("executionPolicyChecked").GetBoolean());
        Assert.Equal(1_048_576, result.GetProperty("limits").GetProperty("maxStdoutBytes").GetInt32());
    }

    [Fact]
    public async Task DoctorReportsAnExistingStoreWithoutReadingIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_env.StorePath)!);
        File.WriteAllText(_env.StorePath, "corrupt content that doctor does not read");
        string[] before = _env.Snapshot();
        JsonElement trust = (await DoctorAsync(_env.Locations)).GetProperty("trustStore");
        Assert.Equal("Ready", trust.GetProperty("directoryStatus").GetString());
        Assert.Equal("Present", trust.GetProperty("fileStatus").GetString());
        Assert.False(trust.GetProperty("contentChecked").GetBoolean());
        Assert.Equal(before, _env.Snapshot());
        Assert.False(File.Exists(_env.StorePath + ".lock"));

        CliRun text = await _env.RunAsync(["doctor", .. _env.Locations]);
        Assert.Contains("Content not checked", text.Output, StringComparison.Ordinal);
        Assert.Contains("not authenticated", text.Output, StringComparison.Ordinal);
        Assert.Contains("execution policy allows scripts was not checked", text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorReportsMissingAndUnsafeRoots()
    {
        JsonElement missing = await DoctorAsync("--root", Path.Combine(_env.Base, "missing"), "--data-dir", _env.Data);
        Assert.Equal("NotFound", missing.GetProperty("pluginRoot").GetProperty("status").GetString());
        Assert.Contains(missing.GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("does not exist", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_env.Base, "missing")));

        string file = Path.Combine(_env.Base, "file");
        File.WriteAllText(file, "x");
        Assert.Equal("Unsafe", (await DoctorAsync("--root", file, "--data-dir", _env.Data)).GetProperty("pluginRoot").GetProperty("status").GetString());

        JsonElement overlap = await DoctorAsync("--root", _env.Root, "--data-dir", _env.Root);
        Assert.Equal("trust store", overlap.GetProperty("pluginRoot").GetProperty("overlapsStorage").GetString());
    }

    [Fact]
    public async Task InvalidExplicitInterpretersAreReportedWithoutFallback()
    {
        string notAProgram = Path.Combine(_env.Base, "python.exe");
        File.WriteAllText(notAProgram, "text");
        JsonElement result = await DoctorAsync([.. _env.Locations, "--python", notAProgram, "--node", Path.Combine(_env.Base, "none", "node.exe")]);
        Dictionary<string, JsonElement> interpreters = result.GetProperty("interpreters").EnumerateArray().ToDictionary(i => i.GetProperty("language").GetString()!);
        Assert.Equal("ConfigurationInvalid", interpreters["Python"].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, interpreters["Python"].GetProperty("path").ValueKind);
        Assert.Equal("ConfigurationInvalid", interpreters["JavaScript"].GetProperty("status").GetString());
        Assert.Contains(interpreters["JavaScript"].GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "TW612");
    }

    [Fact]
    public async Task AutomaticResolutionFollowsTheEngine()
    {
        JsonElement powerShell = (await DoctorAsync(_env.Locations)).GetProperty("interpreters")[0];
        Assert.Equal("PowerShell", powerShell.GetProperty("language").GetString());
        Assert.False(powerShell.GetProperty("configured").GetBoolean());

        // Windows PowerShell 5.1 is part of Windows, so automatic resolution finds a PowerShell.
        Assert.Equal("Resolved", powerShell.GetProperty("status").GetString());
        Assert.Contains(powerShell.GetProperty("source").GetString(), AutomaticSources);
    }

    [Fact]
    public async Task DoctorDisclosesNoEnvironmentValues()
    {
        const string Name = "TRAYWISP_DOCTOR_TEST_SECRET";
        string secret = "secret-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(Name, secret);
        try
        {
            CliRun json = await _env.RunAsync(["doctor", .. _env.Locations, "--json"]);
            CliRun text = await _env.RunAsync(["doctor", .. _env.Locations]);
            Assert.DoesNotContain(secret, json.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, text.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(Name, json.Output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    [Fact]
    public async Task LogsDistinguishMissingEmptyAndValidHistory()
    {
        CliRun none = await _env.RunAsync(["logs", .. _env.Locations, "--json"]);
        Assert.Equal(0, none.ExitCode);
        Assert.Equal("NoHistory", none.Json.GetProperty("result").GetProperty("status").GetString());
        Assert.False(Directory.Exists(_env.Data));

        _env.WriteHistory("{\"schemaVersion\":1,\"records\":[]}\n");
        Assert.Equal("Empty", (await _env.JsonAsync("logs")).GetProperty("result").GetProperty("status").GetString());

        _env.Write("a.ps1", "out x");
        await _env.ApproveAsync("a.ps1");
        for (int i = 0; i < 3; i++)
        {
            await _env.RunAsync(["run", "a.ps1", .. _env.Locations, .. _env.Interpreters]);
        }

        JsonElement logs = (await _env.RunAsync(["logs", "--limit", "2", .. _env.Locations, "--json"])).Json.GetProperty("result");
        Assert.Equal("Loaded", logs.GetProperty("status").GetString());
        Assert.Equal(3, logs.GetProperty("totalRecords").GetInt32());
        Assert.Equal("oldestFirst", logs.GetProperty("order").GetString());
        JsonElement[] records = [.. logs.GetProperty("records").EnumerateArray()];
        Assert.Equal(2, records.Length);
        Assert.True(string.CompareOrdinal(records[0].GetProperty("recordedAtUtc").GetString(), records[1].GetProperty("recordedAtUtc").GetString()) <= 0);

        // Another root's records are marked as such.
        string other = Directory.CreateDirectory(Path.Combine(_env.Base, "other")).FullName;
        JsonElement foreign = (await _env.RunAsync(["logs", "--root", other, "--data-dir", _env.Data, "--json"])).Json.GetProperty("result");
        Assert.All(foreign.GetProperty("records").EnumerateArray(), r => Assert.False(r.GetProperty("isConfiguredRoot").GetBoolean()));
    }

    [Theory]
    [InlineData("{ broken", "Corrupt")]
    [InlineData("{\"schemaVersion\":2,\"records\":[]}", "UnsupportedVersion")]
    public async Task UnusableHistoryIsReportedAndLeftUnchanged(string content, string status)
    {
        _env.WriteHistory(content);
        CliRun run = await _env.RunAsync(["logs", .. _env.Locations, "--json"]);
        Assert.Equal(10, run.ExitCode);
        Assert.Equal(status, run.Json.GetProperty("result").GetProperty("status").GetString());

        _env.Write("a.ps1", "out x");
        await _env.ApproveAsync("a.ps1");
        CliRun ran = await _env.RunAsync(["run", "a.ps1", .. _env.Locations, .. _env.Interpreters, "--json"]);
        Assert.Equal(0, ran.ExitCode);
        Assert.False(ran.Json.GetProperty("result").GetProperty("history").GetProperty("recorded").GetBoolean());
        Assert.Equal(content, File.ReadAllText(_env.HistoryPath));
    }
}
