using System.Runtime.Versioning;
using System.Text.Json;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Cli.Tests;

// The publication decision of traywisp run is made after the run history has been written: the history operation is
// held (its lock-acquired hook) after the first, approved check, while another operation changes the trust state or the
// command is cancelled. The output must then be withheld, the run's own result kept, and the record must show only the
// decision observed when it was written.
[SupportedOSPlatform("windows")]
public sealed class RunPublicationTests : IDisposable
{
    private const string Plugin = "a.ps1";

    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    public static TheoryData<string> Interferences => ["revoke", "reapprove", "modify", "storeUnavailable", "cancel"];

    [Theory]
    [MemberData(nameof(Interferences))]
    public async Task ATrustChangeDuringTheHistoryWriteWithholdsTheOutput(string interference)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CliTestEnvironment.Token);
        await SetUpAsync(interference, cancellation);

        CliRun json = await RunAsync(cancellation.Token, "--json");
        (int exitCode, string trustStatus) = Expected(interference);
        Assert.Equal(exitCode, json.ExitCode);
        Assert.Equal(1, _env.ProcessStarts);
        JsonElement result = json.Json.GetProperty("result");
        Assert.Equal("Succeeded", result.GetProperty("execution").GetProperty("status").GetString());
        Assert.Equal(0, result.GetProperty("execution").GetProperty("pluginExitCode").GetInt32());
        JsonElement publication = result.GetProperty("publication");
        Assert.Equal("Withheld", publication.GetProperty("status").GetString());
        Assert.True(publication.GetProperty("withheldByFinalCheck").GetBoolean());
        Assert.Equal(interference == "cancel", publication.GetProperty("trustCheckCancelled").GetBoolean());
        if (interference == "cancel")
        {
            Assert.Equal(JsonValueKind.Null, publication.GetProperty("trustStatus").ValueKind);
        }
        else
        {
            Assert.Equal(trustStatus, publication.GetProperty("trustStatus").GetString());
        }

        Assert.Equal(JsonValueKind.Null, result.GetProperty("output").ValueKind);
        Assert.DoesNotContain("SECRET", json.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", json.Output, StringComparison.Ordinal);
        Assert.Contains("TW857", json.Codes);

        // The record was written while the approval was still confirmed, and says so; it does not claim the output was shown.
        JsonElement history = result.GetProperty("history");
        Assert.True(history.GetProperty("recorded").GetBoolean());
        Assert.Equal("Published", history.GetProperty("recordedPublication").GetString());
        Assert.Equal("Approved", history.GetProperty("recordedPublicationTrustStatus").GetString());
        PluginRunRecord record = ReadHistory().Records[^1];
        Assert.Equal(RunPublication.Published, record.Publication);
        Assert.Equal(PluginTrustStatus.Approved, record.PublicationTrustStatus);
        Assert.Equal(Core.Execution.PluginExecutionStatus.Succeeded, record.Status);
    }

    [Theory]
    [MemberData(nameof(Interferences))]
    public async Task TheTextReportWithholdsTheOutputToo(string interference)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CliTestEnvironment.Token);
        await SetUpAsync(interference, cancellation);

        CliRun text = await RunAsync(cancellation.Token);
        Assert.Equal(Expected(interference).ExitCode, text.ExitCode);
        Assert.DoesNotContain("SECRET", text.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", text.Output, StringComparison.Ordinal);
        Assert.Contains("Execution: Succeeded", text.Output, StringComparison.Ordinal);
        Assert.Contains(
            interference == "cancel"
                ? "Output: WITHHELD. The final trust check, after the run history was written, was cancelled."
                : $"Output: WITHHELD. The final trust check, after the run history was written, returned {Expected(interference).TrustStatus}.",
            text.Output,
            StringComparison.Ordinal);
        Assert.Contains("Run history: recorded (a summary without output; output decision when recorded: Published, which does not show whether output was displayed)", text.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Output (confirmed", text.Output, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, text.Error);
    }

    [Fact]
    public async Task WithoutInterferenceTheFinalCheckPublishesTheOutput()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CliTestEnvironment.Token);
        await SetUpAsync("none", cancellation);

        CliRun json = await RunAsync(cancellation.Token, "--json");
        Assert.Equal(0, json.ExitCode);
        JsonElement result = json.Json.GetProperty("result");
        Assert.Equal("Published", result.GetProperty("publication").GetProperty("status").GetString());
        Assert.False(result.GetProperty("publication").GetProperty("withheldByFinalCheck").GetBoolean());
        Assert.Equal("SECRET-LABEL", result.GetProperty("output").GetProperty("titles")[0].GetProperty("text").GetString());
        Assert.Equal("Published", result.GetProperty("history").GetProperty("recordedPublication").GetString());

        CliRun text = await RunAsync(cancellation.Token);
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("[1] SECRET-LABEL", text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHistoryFailureStillGetsTheFinalCheck()
    {
        _env.Write(Plugin, "out SECRET-LABEL");
        await _env.ApproveAsync(Plugin);

        // The history fails before anything is written; the final check still decides, and here it denies.
        _env.HistoryFactory = directory => new PluginRunHistory(directory, null, TimeProvider.System, new RunHistoryTestHooks
        {
            Locked = () =>
            {
                Revoke();
                throw new IOException("injected history failure");
            },
        });
        CliRun run = await _env.RunAsync(["run", Plugin, .. _env.Locations, .. _env.Interpreters, "--json"]);
        Assert.Equal(4, run.ExitCode);
        JsonElement result = run.Json.GetProperty("result");
        Assert.Equal("Withheld", result.GetProperty("publication").GetProperty("status").GetString());
        Assert.False(result.GetProperty("history").GetProperty("recorded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("history").GetProperty("recordedPublication").ValueKind);
        Assert.Contains("TW858", run.Codes);
        Assert.DoesNotContain("SECRET", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADenialRightAfterTheRunIsFinalAndRecordedAsWithheld()
    {
        _env.Write(Plugin, "out SECRET-LABEL");
        await _env.ApproveAsync(Plugin);
        int locked = 0;

        // Reapproved during the history write after a first check that already denied: the output is still withheld.
        _env.Hooks = new Runtime.Execution.ExecutionTestHooks { ProcessStarted = _ => Revoke() };
        _env.HistoryFactory = directory => new PluginRunHistory(directory, null, TimeProvider.System, new RunHistoryTestHooks
        {
            Locked = () =>
            {
                Interlocked.Increment(ref locked);
                Approve();
            },
        });
        CliRun run = await _env.RunAsync(["run", Plugin, .. _env.Locations, .. _env.Interpreters, "--json"]);
        Assert.Equal(1, locked);
        Assert.Equal(4, run.ExitCode);
        JsonElement result = run.Json.GetProperty("result");
        Assert.Equal("Withheld", result.GetProperty("publication").GetProperty("status").GetString());
        Assert.False(result.GetProperty("publication").GetProperty("withheldByFinalCheck").GetBoolean());
        Assert.Equal("StaleApproval", result.GetProperty("publication").GetProperty("trustStatus").GetString());
        Assert.Equal("Withheld", result.GetProperty("history").GetProperty("recordedPublication").GetString());
        Assert.Equal(RunPublication.Withheld, ReadHistory().Records[^1].Publication);
        Assert.DoesNotContain("SECRET", run.Output, StringComparison.Ordinal);
    }

    private async Task SetUpAsync(string interference, CancellationTokenSource cancellation)
    {
        _env.Write(Plugin, "out SECRET-LABEL", "out ---", "out Click | href=https://example.com/secret");
        await _env.ApproveAsync(Plugin);
        int runs = 0;
        _env.HistoryFactory = directory => new PluginRunHistory(directory, null, TimeProvider.System, new RunHistoryTestHooks
        {
            // Runs while the history lock is held, after the first post-run check approved the output. Only the first
            // command of a test interferes, so the trust state is restored for the second.
            Locked = () =>
            {
                if (Interlocked.Increment(ref runs) > 1)
                {
                    Restore(interference);
                }

                Interfere(interference, cancellation);
            },
        });
    }

    // Changes the trust state the way another TrayWisp invocation or the user would. A trust operation succeeding here
    // also shows that the command holds no approval-store lock while it writes the history.
    private void Interfere(string interference, CancellationTokenSource cancellation)
    {
        switch (interference)
        {
            case "revoke":
                Revoke();
                break;
            case "reapprove":
                Revoke();
                Approve();
                break;
            case "modify":
                _env.Write(Plugin, "out SECRET-LABEL", "out changed");
                break;
            case "storeUnavailable":
                File.WriteAllText(_env.StorePath, "{ corrupt");
                break;
            case "cancel":
                cancellation.Cancel();
                break;
        }
    }

    // Undoes the previous command's interference, so that the next command starts from an approved plugin.
    private void Restore(string interference)
    {
        switch (interference)
        {
            case "revoke" or "reapprove":
                Approve();
                break;
            case "modify":
                _env.Write(Plugin, "out SECRET-LABEL", "out ---", "out Click | href=https://example.com/secret");
                Approve();
                break;
            case "storeUnavailable":
                File.Delete(_env.StorePath);
                Approve();
                break;
        }
    }

    private static (int ExitCode, string TrustStatus) Expected(string interference) => interference switch
    {
        "revoke" or "reapprove" => (4, "StaleApproval"),
        "modify" => (4, "ContentChanged"),
        "storeUnavailable" => (10, "StoreUnavailable"),
        "cancel" => (130, string.Empty),
        _ => (0, "Approved"),
    };

    private void Revoke() => Assert.True(_env.TrustService().Revoke(_env.Root, PluginId.Create(Plugin), CliTestEnvironment.Token).Succeeded);

    private void Approve()
    {
        PluginTrustService trust = _env.TrustService();
        PluginInspectionResult inspection = trust.Inspect(_env.Root, PluginId.Create(Plugin), CliTestEnvironment.Token);
        Assert.True(trust.Approve(_env.Root, PluginId.Create(Plugin), inspection.Plugin!.Fingerprint, CliTestEnvironment.Token).Succeeded);
    }

    private Task<CliRun> RunAsync(CancellationToken token, params string[] extra) =>
        _env.RunAsync(token, ["run", Plugin, .. _env.Locations, .. _env.Interpreters, .. extra]);

    private RunHistoryReadResult ReadHistory() => new PluginRunHistory(Path.GetDirectoryName(_env.HistoryPath)!).Read(10, CliTestEnvironment.Token);
}
