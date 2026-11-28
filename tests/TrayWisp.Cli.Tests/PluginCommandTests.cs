using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace TrayWisp.Cli.Tests;

// list, inspect, approve, revoke and status against isolated temporary roots and trust stores.
[SupportedOSPlatform("windows")]
public sealed class PluginCommandTests : IDisposable
{
    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private static JsonElement Result(JsonElement json) => json.GetProperty("result");

    private async Task<string> TrustStatusAsync(string id) =>
        Result(await _env.JsonAsync("status", id)).GetProperty("plugins")[0].GetProperty("trustStatus").GetString()!;

    [Fact]
    public async Task ListAcceptsOnlyItsOwnOptions() => Assert.Equal(2, (await _env.RunAsync(["list", .. _env.Locations])).ExitCode);

    [Fact]
    public async Task ListShowsDiscoveredPluginsInStableOrderWithoutRunningOrHashing()
    {
        _env.Write("b/zeta.5m.py", "# <traywisp.title>Zeta</traywisp.title>", _env.Marker("zeta"));
        _env.Write("Alpha.js", _env.Marker("alpha"));
        _env.Write("tool.sh", "echo");
        _env.Write("notes.txt", "x");

        CliRun run = await _env.RunAsync(["list", "--root", _env.Root, "--json"]);
        Assert.Equal(0, run.ExitCode);
        JsonElement result = Result(run.Json);
        Assert.True(result.GetProperty("complete").GetBoolean());
        JsonElement[] plugins = [.. result.GetProperty("plugins").EnumerateArray()];
        Assert.Equal(["Alpha.js", "b/zeta.5m.py", "tool.sh"], plugins.Select(p => p.GetProperty("pluginId").GetString()));
        Assert.Equal("Zeta", plugins[1].GetProperty("displayTitle").GetString());
        Assert.Equal("Python", plugins[1].GetProperty("language").GetString());
        Assert.Equal("5m", plugins[1].GetProperty("refreshInterval").GetProperty("text").GetString());
        Assert.Equal(300_000, plugins[1].GetProperty("refreshInterval").GetProperty("milliseconds").GetInt64());
        Assert.Equal(JsonValueKind.Null, plugins[0].GetProperty("refreshInterval").ValueKind);
        Assert.Equal("Deferred", plugins[2].GetProperty("executionSupport").GetString());
        Assert.DoesNotContain("sha256", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trustStatus", run.Output, StringComparison.Ordinal);

        CliRun text = await _env.RunAsync("list", "--root", _env.Root);
        Assert.Contains("Plugin: b/zeta.5m.py", text.Output, StringComparison.Ordinal);
        Assert.Contains("Refresh: every 5m", text.Output, StringComparison.Ordinal);
        Assert.Contains("Refresh: manual only", text.Output, StringComparison.Ordinal);
        Assert.Contains("nothing was run, hashed or approved", text.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(_env.MarkerPath("zeta")));
        Assert.False(Directory.Exists(_env.Data));
    }

    [Fact]
    public async Task AnIncompleteListIsReportedWithExitCode11()
    {
        _env.Write("a.ps1", "out x");
        string locked = Directory.CreateDirectory(Path.Combine(_env.Root, "locked")).FullName;
        _env.DenyListing(locked);

        CliRun run = await _env.RunAsync("list", "--root", _env.Root, "--json");
        Assert.Equal(11, run.ExitCode);
        Assert.False(Result(run.Json).GetProperty("complete").GetBoolean());
        Assert.Contains("TW855", run.Codes);
        Assert.Single(Result(run.Json).GetProperty("plugins").EnumerateArray());

        CliRun text = await _env.RunAsync("list", "--root", _env.Root);
        Assert.Contains("INCOMPLETE", text.Output, StringComparison.Ordinal);
        Assert.Contains("may still exist", text.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingRootIsNeverCreated()
    {
        string missing = Path.Combine(_env.Base, "missing");
        foreach (string command in new[] { "list", "status", "doctor" })
        {
            CliRun run = command == "list"
                ? await _env.RunAsync(command, "--root", missing)
                : await _env.RunAsync(command, "--root", missing, "--data-dir", _env.Data);
            Assert.False(Directory.Exists(missing));
            if (command != "doctor")
            {
                Assert.Equal(3, run.ExitCode);
                Assert.Contains("create it explicitly", run.Error, StringComparison.Ordinal);
            }
        }

        Assert.False(Directory.Exists(_env.Data));
    }

    [Fact]
    public async Task InspectReportsTheFullContentFingerprintAndApprovesNothing()
    {
        byte[] content = System.Text.Encoding.UTF8.GetBytes("# <traywisp.title>Clock</traywisp.title>\n" + _env.Marker("clock") + "\n" + new string('x', 200_000));
        _env.WriteBytes("clock.5s.ps1", content);

        CliRun run = await _env.RunAsync(["inspect", "clock.5s.ps1", .. _env.Locations, "--json"]);
        Assert.Equal(0, run.ExitCode);
        JsonElement result = Result(run.Json);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), result.GetProperty("sha256").GetString());
        Assert.Equal(content.Length, result.GetProperty("contentBytes").GetInt64());
        Assert.Equal(_env.PluginPath("clock.5s.ps1"), result.GetProperty("validatedFilePath").GetString());
        Assert.True(result.GetProperty("approvalRequired").GetBoolean());
        Assert.False(result.GetProperty("approvalChecked").GetBoolean());
        Assert.Equal("Clock", result.GetProperty("metadata").GetProperty("title").GetString());
        Assert.DoesNotContain("xxxxxxxx", run.Output, StringComparison.Ordinal);

        Assert.Equal("PendingApproval", await TrustStatusAsync("clock.5s.ps1"));
        Assert.False(File.Exists(_env.MarkerPath("clock")));
        Assert.Equal(0, _env.ProcessStarts);

        CliRun text = await _env.RunAsync(["inspect", "clock.5s.ps1", .. _env.Locations]);
        Assert.Contains("Nothing was run or approved", text.Output, StringComparison.Ordinal);
        Assert.Contains("does not mean the script was reviewed", text.Output, StringComparison.Ordinal);
        Assert.Contains($"traywisp approve --sha256 {result.GetProperty("sha256").GetString()} --root '{_env.Root}' --data-dir '{_env.Data}' -- 'clock.5s.ps1'", text.Output, StringComparison.Ordinal);
        Assert.Contains("not a sandbox", text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectDoesNotSuggestAPasteableCommandForUnusualIdentities()
    {
        _env.Write("caf\u00E9.ps1", "out x");
        CliRun text = await _env.RunAsync(["inspect", "caf\u00E9.ps1", .. _env.Locations]);
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("traywisp approve --sha256 ", text.Output, StringComparison.Ordinal);
        Assert.Contains(" -- 'PLUGIN_ID'   (a template", text.Output, StringComparison.Ordinal);
        Assert.Contains("TW864", text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectOfAMissingOrUnsafePluginFails()
    {
        Assert.Equal(3, (await _env.RunAsync(["inspect", "missing.ps1", .. _env.Locations])).ExitCode);
        string hidden = _env.Write("hidden.ps1", "out x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        CliRun run = await _env.RunAsync(["inspect", "hidden.ps1", .. _env.Locations, "--json"]);
        Assert.Equal(3, run.ExitCode);
        Assert.Equal("UnsafePath", Result(run.Json).GetProperty("inspectionStatus").GetString());
        Assert.Contains("TW512", run.Codes);
    }

    [Fact]
    public async Task ApprovalNeedsTheExactCurrentFingerprint()
    {
        _env.Write("a.ps1", "out one");
        string fingerprint = Result(await _env.JsonAsync("inspect", "a.ps1")).GetProperty("sha256").GetString()!;
        _env.Write("a.ps1", "out two");

        CliRun stale = await _env.RunAsync(["approve", "a.ps1", "--sha256", fingerprint, .. _env.Locations, "--json"]);
        Assert.Equal(4, stale.ExitCode);
        JsonElement result = Result(stale.Json);
        Assert.Equal("FingerprintMismatch", result.GetProperty("approvalStatus").GetString());
        Assert.False(result.GetProperty("approved").GetBoolean());
        Assert.NotEqual(fingerprint, result.GetProperty("currentSha256").GetString());
        Assert.False(result.GetProperty("pluginRun").GetBoolean());
        Assert.Equal("PendingApproval", await TrustStatusAsync("a.ps1"));

        string current = await _env.ApproveAsync("a.ps1");
        Assert.Equal("Approved", await TrustStatusAsync("a.ps1"));
        Assert.Equal(0, _env.ProcessStarts);

        // Idempotent: the same generation is kept.
        JsonElement first = Result((await _env.RunAsync(["approve", "a.ps1", "--sha256", current, .. _env.Locations, "--json"])).Json);
        Assert.Equal("AlreadyApproved", first.GetProperty("approvalStatus").GetString());
        JsonElement status = Result(await _env.JsonAsync("status", "a.ps1")).GetProperty("plugins")[0];
        Assert.Equal(first.GetProperty("approval").GetProperty("generation").GetString(), status.GetProperty("approval").GetProperty("generation").GetString());
    }

    [Fact]
    public async Task ApprovalTextExplainsConsentAndDoesNotRun()
    {
        _env.Write("a.ps1", _env.Marker("a"));
        string fingerprint = Result(await _env.JsonAsync("inspect", "a.ps1")).GetProperty("sha256").GetString()!;
        CliRun run = await _env.RunAsync(["approve", "a.ps1", "--sha256", fingerprint, .. _env.Locations]);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Result: approved", run.Output, StringComparison.Ordinal);
        Assert.Contains("Approval generation: ", run.Output, StringComparison.Ordinal);
        Assert.Contains("not a sandbox", run.Output, StringComparison.Ordinal);
        Assert.Contains("The plugin was not run", run.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(_env.MarkerPath("a")));
    }

    [Fact]
    public async Task IdentitiesAreCaseInsensitive()
    {
        _env.Write("Clock.ps1", "out x");
        await _env.ApproveAsync("CLOCK.PS1");
        Assert.Equal("Approved", await TrustStatusAsync("clock.ps1"));
        JsonElement revoked = Result((await _env.RunAsync(["revoke", "cLoCk.Ps1", .. _env.Locations, "--json"])).Json);
        Assert.True(revoked.GetProperty("revoked").GetBoolean());
    }

    [Fact]
    public async Task RevocationWorksForMissingPluginsAndIsIdempotent()
    {
        _env.Write("a.ps1", "out x");
        await _env.ApproveAsync("a.ps1");
        File.Delete(_env.PluginPath("a.ps1"));

        CliRun revoked = await _env.RunAsync(["revoke", "a.ps1", .. _env.Locations, "--json"]);
        Assert.Equal(0, revoked.ExitCode);
        Assert.Equal("Revoked", Result(revoked.Json).GetProperty("revocationStatus").GetString());
        Assert.Equal(64, Result(revoked.Json).GetProperty("revokedApproval").GetProperty("sha256").GetString()!.Length);

        CliRun again = await _env.RunAsync(["revoke", "a.ps1", .. _env.Locations, "--json"]);
        Assert.Equal(0, again.ExitCode);
        Assert.Equal("NotApproved", Result(again.Json).GetProperty("revocationStatus").GetString());

        // Even when the whole root is gone.
        Directory.Delete(_env.Root, recursive: true);
        CliRun gone = await _env.RunAsync(["revoke", "a.ps1", .. _env.Locations]);
        Assert.Equal(0, gone.ExitCode);
        Assert.Contains("no matching approval existed", gone.Output, StringComparison.Ordinal);
        Assert.Contains("was not changed or deleted", gone.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsEachTrustStateAndPersistsInvalidation()
    {
        _env.Write("approved.ps1", "out a");
        _env.Write("changed.ps1", "out b");
        _env.Write("pending.ps1", "out c");
        await _env.ApproveAsync("approved.ps1");
        await _env.ApproveAsync("changed.ps1");
        _env.Write("changed.ps1", "out B");

        CliRun run = await _env.RunAsync(["status", .. _env.Locations, "--json"]);
        Assert.Equal(0, run.ExitCode);
        Dictionary<string, JsonElement> plugins = Result(run.Json).GetProperty("plugins").EnumerateArray().ToDictionary(p => p.GetProperty("pluginId").GetString()!);
        Assert.Equal("Approved", plugins["approved.ps1"].GetProperty("trustStatus").GetString());
        Assert.Equal("ContentChanged", plugins["changed.ps1"].GetProperty("trustStatus").GetString());
        Assert.True(plugins["changed.ps1"].GetProperty("approvalInvalidated").GetBoolean());
        Assert.Equal("PendingApproval", plugins["pending.ps1"].GetProperty("trustStatus").GetString());
        Assert.True(Result(run.Json).GetProperty("discoveryComplete").GetBoolean());

        // The invalidation was saved: restoring the bytes does not restore the approval.
        _env.Write("changed.ps1", "out b");
        Assert.Equal("PendingApproval", await TrustStatusAsync("changed.ps1"));

        CliRun text = await _env.RunAsync(["status", .. _env.Locations]);
        Assert.Contains("not a lasting authorization", text.Output, StringComparison.Ordinal);
        Assert.Contains("removes the obsolete approval", text.Output, StringComparison.Ordinal);
        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task TargetedStatusNeedsNoDiscoveryAndReportsUnavailableAndUnsafePlugins()
    {
        Assert.Equal("PluginUnavailable", await TrustStatusAsync("missing.ps1"));
        string hidden = _env.Write("h.ps1", "out x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        CliRun run = await _env.RunAsync(["status", "h.ps1", .. _env.Locations, "--json"]);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("UnsafePath", Result(run.Json).GetProperty("plugins")[0].GetProperty("trustStatus").GetString());
        Assert.Equal(JsonValueKind.Null, Result(run.Json).GetProperty("discoveryComplete").ValueKind);
    }

    [Fact]
    public async Task StatusOfAnIncompleteScanIsExitCode11()
    {
        _env.Write("a.ps1", "out x");
        string locked = Directory.CreateDirectory(Path.Combine(_env.Root, "locked")).FullName;
        _env.DenyListing(locked);
        CliRun run = await _env.RunAsync(["status", .. _env.Locations, "--json"]);
        Assert.Equal(11, run.ExitCode);
        Assert.False(Result(run.Json).GetProperty("discoveryComplete").GetBoolean());
    }

    [Fact]
    public async Task ACorruptTrustStoreIsReportedAndNeverReplaced()
    {
        _env.Write("a.ps1", "out x");
        Directory.CreateDirectory(Path.GetDirectoryName(_env.StorePath)!);
        File.WriteAllText(_env.StorePath, "{ not json");
        byte[] before = File.ReadAllBytes(_env.StorePath);
        string fingerprint = Result(await _env.JsonAsync("inspect", "a.ps1")).GetProperty("sha256").GetString()!;

        CliRun status = await _env.RunAsync(["status", "a.ps1", .. _env.Locations, "--json"]);
        Assert.Equal(10, status.ExitCode);
        Assert.Equal("StoreUnavailable", Result(status.Json).GetProperty("plugins")[0].GetProperty("trustStatus").GetString());
        Assert.Contains("TW541", status.Codes);
        Assert.Equal(10, (await _env.RunAsync(["status", .. _env.Locations])).ExitCode);
        Assert.Equal(10, (await _env.RunAsync(["approve", "a.ps1", "--sha256", fingerprint, .. _env.Locations])).ExitCode);
        Assert.Equal(10, (await _env.RunAsync(["revoke", "a.ps1", .. _env.Locations])).ExitCode);
        Assert.Equal(before, File.ReadAllBytes(_env.StorePath));
    }

    [Fact]
    public async Task AnUnsafeTrustDirectoryIsRejectedBeforeTheStoreIsUsed()
    {
        _env.Write("a.ps1", "out x");
        string elsewhere = Directory.CreateDirectory(Path.Combine(_env.Base, "elsewhere")).FullName;
        Directory.CreateDirectory(_env.Data);
        Assert.SkipUnless(CliTestEnvironment.TryCreateJunction(Path.Combine(_env.Data, "trust"), elsewhere), "Junctions are not available.");

        CliRun run = await _env.RunAsync(["status", "a.ps1", .. _env.Locations, "--json"]);
        Assert.Equal(10, run.ExitCode);
        Assert.Contains("TW803", run.Codes);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Theory]
    [InlineData("root-is-data")]
    [InlineData("data-in-root")]
    [InlineData("root-in-trust")]
    [InlineData("root-in-diagnostics")]
    public async Task StorageMustNotOverlapThePluginRoot(string layout)
    {
        (string root, string data) = layout switch
        {
            "root-is-data" => (_env.Root, _env.Root),
            "data-in-root" => (_env.Root, Path.Combine(_env.Root, "data")),
            "root-in-trust" => (Path.Combine(_env.Data, "trust", "plugins"), _env.Data),
            _ => (Path.Combine(_env.Data, "diagnostics", "plugins"), _env.Data),
        };
        Directory.CreateDirectory(root);
        string[][] invocations = [["status"], ["inspect", "a.ps1"], ["approve", "a.ps1", "--sha256", new string('0', 64)], ["run", "a.ps1"]];
        foreach (string[] args in invocations)
        {
            CliRun run = await _env.RunAsync([.. args, "--root", root, "--data-dir", data, "--json"]);
            Assert.Equal(5, run.ExitCode);
            Assert.Contains("TW854", run.Codes);
        }

        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task UnsafeRootsAreRejected()
    {
        string target = Directory.CreateDirectory(Path.Combine(_env.Base, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "a.ps1"), "out x");
        string link = Path.Combine(_env.Base, "link");
        Assert.SkipUnless(CliTestEnvironment.TryCreateJunction(link, target), "Junctions are not available.");

        CliRun run = await _env.RunAsync("list", "--root", link, "--json");
        Assert.Equal(3, run.ExitCode);
        Assert.Equal("Unsafe", Result(run.Json).GetProperty("rootStatus").GetString());
        Assert.Contains("TW504", run.Codes);
    }

    [Fact]
    public async Task AFileIsNotARoot()
    {
        string file = Path.Combine(_env.Base, "file");
        File.WriteAllText(file, "x");
        CliRun run = await _env.RunAsync("list", "--root", file, "--json");
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("TW503", run.Codes);
    }

    [Fact]
    public async Task ListWithAnExplicitRootNeverConsultsTheDefaultDataFolder()
    {
        _env.Write("a.ps1", "out x");
        int consulted = 0;
        _env.LocalApplicationData = () =>
        {
            Interlocked.Increment(ref consulted);
            return null;
        };
        string[] before = _env.Snapshot();

        CliRun json = await _env.RunAsync("list", "--root", _env.Root, "--json");
        Assert.Equal(0, json.ExitCode);
        Assert.Equal("a.ps1", Assert.Single(Result(json.Json).GetProperty("plugins").EnumerateArray()).GetProperty("pluginId").GetString());
        CliRun text = await _env.RunAsync("list", "--root", _env.Root);
        Assert.Equal(0, text.ExitCode);
        Assert.Equal(0, consulted);
        Assert.Equal(before, _env.Snapshot());
    }

    [Fact]
    public async Task DefaultsAreStillResolvedWhenTheyAreNeeded()
    {
        _env.Write("a.ps1", "out x");
        _env.LocalApplicationData = () => null;

        // list without --root needs the default root.
        CliRun list = await _env.RunAsync("list", "--json");
        Assert.Equal(5, list.ExitCode);
        Assert.Contains("TW853", list.Codes);
        Assert.Contains("Use --root.", list.Json.GetProperty("diagnostics")[0].GetProperty("message").GetString(), StringComparison.Ordinal);

        // Commands that use storage still need a data directory: explicit, or the default.
        CliRun status = await _env.RunAsync("status", "--root", _env.Root, "--json");
        Assert.Equal(5, status.ExitCode);
        Assert.Contains("TW853", status.Codes);
        Assert.Equal(0, (await _env.RunAsync(["status", .. _env.Locations])).ExitCode);

        // With an available default, list without --root uses %LOCALAPPDATA%\TrayWisp\plugins and creates nothing.
        string local = Directory.CreateDirectory(Path.Combine(_env.Base, "appdata")).FullName;
        Directory.CreateDirectory(Path.Combine(local, "TrayWisp", "plugins"));
        _env.LocalApplicationData = () => local;
        string[] before = _env.Snapshot();
        CliRun defaults = await _env.RunAsync("list", "--json");
        Assert.Equal(0, defaults.ExitCode);
        Assert.Equal(Path.Combine(local, "TrayWisp", "plugins"), Result(defaults.Json).GetProperty("root").GetString());
        Assert.Equal(before, _env.Snapshot());
    }
}
