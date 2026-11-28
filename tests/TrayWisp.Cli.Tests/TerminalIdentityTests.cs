using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using TrayWisp.Cli.Commands;
using TrayWisp.Cli.Output;
using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Cli.Tests;

// Identities and paths in human-readable output: invisible characters made visible (docs/cli.md, "Terminal safety"),
// literal escapes and edge spaces disambiguated, no truncation, exact JSON; and the inert approval suggestion.
public sealed class TerminalIdentityTests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("a\u3164b", "a\\u3164b")]
    [InlineData("\u115Fx", "\\u115Fx")]
    [InlineData("x\u1160", "x\\u1160")]
    [InlineData("x\uFFA0y", "x\\uFFA0y")]
    [InlineData("x\uFE0Fy", "x\\uFE0Fy")]
    [InlineData("x\uFE00y", "x\\uFE00y")]
    [InlineData("x\uDB40\uDD00y", "x\\uDB40\\uDD00y")]
    [InlineData("x\u034Fy", "x\\u034Fy")]
    [InlineData("x\u180By", "x\\u180By")]
    [InlineData("x\u2800y", "x\\u2800y")]
    [InlineData("x\u00A0y", "x\\u00A0y")]
    [InlineData("x\u3000y", "x\\u3000y")]
    [InlineData("x\u2007y", "x\\u2007y")]
    [InlineData("x\u00ADy", "x\\u00ADy")]
    [InlineData("x\uD834\uDD73y", "x\\uD834\\uDD73y")]
    [InlineData("x\uD82F\uDCA0y", "x\\uD82F\\uDCA0y")]
    [InlineData("x\uDB40\uDC20y", "x\\uDB40\\uDC20y")]
    public void InvisibleCharactersInIdentitiesAndTextAreEscaped(string value, string expected)
    {
        Assert.Equal(expected, TerminalText.Inline(value, kind: TerminalText.Kind.Identity));
        Assert.Equal(expected, TerminalText.Inline(value, kind: TerminalText.Kind.Text));
    }

    [Theory]
    [InlineData("caf\u00E9 \u65E5\u672C \u0627\u0644\u0639\u0631\u0628\u064A\u0629")]
    [InlineData("\u2764\uFE0F \uD83D\uDC4D\uD83C\uDFFD")]
    [InlineData("\uD83D\uDC69\u200D\uD83D\uDC67 family")]
    [InlineData("e\u0301 combining acute")]
    public void LabelsKeepMeaningfulUnicodeAndEmoji(string label) =>
        Assert.Equal(label, TerminalText.Inline(label, kind: TerminalText.Kind.Label));

    [Fact]
    public void VisibleUnicodeInIdentitiesIsKept()
    {
        Assert.Equal("caf\u00E9/\u65E5\u672C\uD83D\uDE80.ps1", TerminalText.Inline("caf\u00E9/\u65E5\u672C\uD83D\uDE80.ps1", kind: TerminalText.Kind.Identity));
        Assert.Equal("e\u0301.ps1", TerminalText.Inline("e\u0301.ps1", kind: TerminalText.Kind.Identity));
    }

    [Fact]
    public void LiteralEscapeTextInAnIdentityCannotImitateAnEscape()
    {
        string invisible = TerminalText.Inline("C:\\x\u200Bdir", kind: TerminalText.Kind.Identity);
        string literal = TerminalText.Inline("C:\\x\\u200Bdir", kind: TerminalText.Kind.Identity);
        Assert.Equal("C:\\x\\u200Bdir", invisible);
        Assert.Equal("C:\\x\\u005Cu200Bdir", literal);
        Assert.NotEqual(invisible, literal);

        Assert.Equal("C:\\Users\\me", TerminalText.Inline("C:\\Users\\me", kind: TerminalText.Kind.Identity));
        Assert.Equal("D:\\u005Cusers\\x", TerminalText.Inline("D:\\users\\x", kind: TerminalText.Kind.Identity));
        Assert.Equal("end\\", TerminalText.Inline("end\\", kind: TerminalText.Kind.Identity));
        Assert.Equal("a\\\\u200B", TerminalText.Inline("a\\\u200B", kind: TerminalText.Kind.Identity));
    }

    [Theory]
    [InlineData(" lead.ps1", "\\u0020lead.ps1")]
    [InlineData("trail ", "trail\\u0020")]
    [InlineData(" ", "\\u0020")]
    [InlineData("  two", "\\u0020 two")]
    [InlineData("in side/my clock.ps1", "in side/my clock.ps1")]
    public void SpacesAtTheEdgesOfAnIdentityAreVisible(string value, string expected) =>
        Assert.Equal(expected, TerminalText.Inline(value, kind: TerminalText.Kind.Identity));

    [Fact]
    public void IdentityFieldsAreNotShortened()
    {
        var writer = new HumanWriter();
        string id = new string('a', 4000) + "\u3164" + new string('b', 90) + ".ps1";
        writer.Field("Plugin", id);
        string line = writer.ToString();
        Assert.Equal("Plugin: " + new string('a', 4000) + "\\u3164" + new string('b', 90) + ".ps1\n", line);
        Assert.DoesNotContain(TerminalText.TruncationMarker, line, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticPathsGetTheSameGuarantees()
    {
        string formatted = HumanWriter.Format(CliDiagnostic.Warning(CliDiagnosticCode.RunNotRecorded, "m") with { Path = "a\u3164b/\uDB40\uDC41c\u2800\u00A0.ps1" });
        Assert.Contains("'a\\u3164b/\\uDB40\\uDC41c\\u2800\\u00A0.ps1'", formatted, StringComparison.Ordinal);

        // Quote doubles literal backslashes, so an escape-looking path stays distinguishable inside the quotes.
        string literal = HumanWriter.Format(CliDiagnostic.Warning(CliDiagnosticCode.RunNotRecorded, "m") with { Path = "C:\\u200B" });
        string invisible = HumanWriter.Format(CliDiagnostic.Warning(CliDiagnosticCode.RunNotRecorded, "m") with { Path = "C:\u200B" });
        Assert.Contains("'C:\\\\u200B'", literal, StringComparison.Ordinal);
        Assert.Contains("'C:\\u200B'", invisible, StringComparison.Ordinal);
        Assert.EndsWith(": m\\u3164", HumanWriter.Format(CliDiagnostic.Warning(CliDiagnosticCode.RunNotRecorded, "m\u3164")), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Suggestions => new()
    {
        { "-clock.ps1", "'-clock.ps1'" },
        { "--example.ps1", "'--example.ps1'" },
        { "it's.ps1", "'it''s.ps1'" },
        { "my clock.ps1", "'my clock.ps1'" },
        { "a;b&c|d$(x)`y{z}@w#v%.ps1", "'a;b&c|d$(x)`y{z}@w#v%.ps1'" },
        { "sub/-x.1m.ps1", "'sub/-x.1m.ps1'" },
        { "u200B.ps1", "'u200B.ps1'" },
    };

    [Theory]
    [MemberData(nameof(Suggestions))]
    public void SuggestionsPutEveryOptionBeforeTheQuotedOperand(string id, string quoted)
    {
        Assert.Equal(
            $"traywisp approve --sha256 {Fingerprint} --root 'C:\\it''s here' --data-dir 'D:\\data' -- {quoted}",
            CommandSupport.PowerShellCommand("approve", id, "--sha256", Fingerprint, "--root", "C:\\it's here", "--data-dir", "D:\\data"));
        Assert.Equal($"traywisp approve --sha256 {Fingerprint} -- {quoted}", CommandSupport.PowerShellCommand("approve", id, "--sha256", Fingerprint));
    }

    [Theory]
    [InlineData("caf\u00E9.ps1")]
    [InlineData("a\u3164.ps1")]
    [InlineData("a\u200B.ps1")]
    [InlineData("a\u2019s.ps1")]
    [InlineData("a\tb.ps1")]
    public void NoPasteableSuggestionForIdentitiesThatAreNotPrintableAscii(string id) =>
        Assert.Null(CommandSupport.PowerShellCommand("approve", id, "--sha256", Fingerprint));

    [Fact]
    public void NoPasteableSuggestionForUnusualOptions()
    {
        Assert.Null(CommandSupport.PowerShellCommand("approve", "a.ps1", "--root", "-x"));
        Assert.Null(CommandSupport.PowerShellCommand("approve", "a.ps1", "--root", "C:\\caf\u00E9"));
        Assert.Null(CommandSupport.PowerShellCommand("approve", "a.ps1", "--root"));
        Assert.Null(CommandSupport.PowerShellCommand("approve", "a.ps1", "root", "C:\\x"));
    }
}

// The same guarantees through the commands, with real plugin files.
[SupportedOSPlatform("windows")]
public sealed class TerminalIdentityCommandTests : IDisposable
{
    private readonly CliTestEnvironment _env = CliTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task InvisibleCharactersInAFileNameAreShownButTheIdentityIsUnchanged()
    {
        string id = "clo\u3164ck\u2800.ps1";
        _env.Write(id, "out x");

        CliRun text = await _env.RunAsync(["inspect", id, .. _env.Locations]);
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("Plugin: clo\\u3164ck\\u2800.ps1\n", text.Output, StringComparison.Ordinal);
        Assert.Contains("clo\\u3164ck\\u2800.ps1", text.Output.Split('\n').Single(l => l.StartsWith("Validated file: ", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain('\u3164', text.Output);
        Assert.DoesNotContain('\u2800', text.Output);
        Assert.Contains("TW864", text.Output, StringComparison.Ordinal);
        Assert.Contains("-- 'PLUGIN_ID'   (a template", text.Output, StringComparison.Ordinal);

        // JSON keeps the exact identity, and approving it needs the reviewed fingerprint as before.
        JsonElement inspection = (await _env.JsonAsync("inspect", id)).GetProperty("result");
        Assert.Equal(id, inspection.GetProperty("pluginId").GetString());
        Assert.Equal(_env.PluginPath(id), inspection.GetProperty("validatedFilePath").GetString());
        Assert.Equal(4, (await _env.RunAsync(["approve", id, "--sha256", new string('0', 64), .. _env.Locations])).ExitCode);
        Assert.Equal(0, (await _env.RunAsync(["approve", id, "--sha256", inspection.GetProperty("sha256").GetString()!, .. _env.Locations])).ExitCode);
        JsonElement status = (await _env.JsonAsync("status", id)).GetProperty("result");
        Assert.Equal("Approved", status.GetProperty("plugins")[0].GetProperty("trustStatus").GetString());
        Assert.Equal(id, status.GetProperty("plugins")[0].GetProperty("pluginId").GetString());
    }

    [Fact]
    public async Task ARootWithEscapeLookingTextIsShownUnambiguously()
    {
        string root = Directory.CreateDirectory(Path.Combine(_env.Base, "u200Bplugins")).FullName;
        File.WriteAllText(Path.Combine(root, "a.ps1"), "out x\n");
        CliRun text = await _env.RunAsync(["list", "--root", root]);
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("Plugin root: " + _env.Base + "\\u005Cu200Bplugins\n", text.Output, StringComparison.Ordinal);
        CliRun json = await _env.RunAsync(["list", "--root", root, "--json"]);
        Assert.Equal(root, json.Json.GetProperty("result").GetProperty("root").GetString());
    }

    public static TheoryData<string> AwkwardIdentities => ["-clock.ps1", "--example.ps1", "it's $(Get-Date) & y;.ps1", "my clock.ps1"];

    // The suggestion is pasted into Windows PowerShell 5.1 as printed (with the command name replaced by the built
    // executable's path), and must approve exactly the inspected plugin with the reviewed fingerprint.
    [Theory]
    [MemberData(nameof(AwkwardIdentities))]
    public async Task TheSuggestedCommandApprovesExactlyThisPluginInWindowsPowerShell(string id)
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.SkipUnless(File.Exists(powershell), "Windows PowerShell is not available.");
        string executable = Path.Combine(AppContext.BaseDirectory, "traywisp.exe");
        Assert.True(File.Exists(executable));
        _env.Write(id, "out x");
        _env.Write("other.ps1", "out y");

        CliRun inspect = await _env.RunAsync(["inspect", .. _env.Locations, "--", id]);
        Assert.Equal(0, inspect.ExitCode);
        string[] lines = inspect.Output.Split('\n');
        string suggestion = lines[Array.FindIndex(lines, l => l.Contains("approve this fingerprint:", StringComparison.Ordinal)) + 1].Trim();
        Assert.StartsWith("traywisp approve --sha256 ", suggestion, StringComparison.Ordinal);
        Assert.EndsWith(" -- '" + id.Replace("'", "''", StringComparison.Ordinal) + "'", suggestion, StringComparison.Ordinal);

        string command = "& '" + executable.Replace("'", "''", StringComparison.Ordinal) + "'" + suggestion["traywisp".Length..] + "; exit $LASTEXITCODE";
        var startInfo = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(CliTestEnvironment.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(CliTestEnvironment.Token);
        await process.WaitForExitAsync(CliTestEnvironment.Token);
        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}\n{await output}\n{await error}");

        JsonElement plugins = (await _env.JsonAsync("status")).GetProperty("result").GetProperty("plugins");
        Assert.Equal("Approved", plugins.EnumerateArray().Single(p => p.GetProperty("pluginId").GetString() == id).GetProperty("trustStatus").GetString());
        Assert.Equal("PendingApproval", plugins.EnumerateArray().Single(p => p.GetProperty("pluginId").GetString() == "other.ps1").GetProperty("trustStatus").GetString());
    }
}
