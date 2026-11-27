using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using TrayWisp.Core.Execution;
using TrayWisp.Runtime.Execution;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// Integration tests with the interpreters installed on this machine, resolved exactly as in production. Nothing is
// installed and nothing uses the network. The scripts are original and harmless.
//
// Optional by default: a test skips, saying why, when its interpreter is unavailable or the PowerShell execution
// policy forbids scripts, and a skipped test verifies nothing. A dedicated integration environment makes them
// required with TRAYWISP_REQUIRED_INTERPRETERS (a list of WindowsPowerShell, PowerShell, Python and Node, separated
// by ',' or ';', or "all"): a required interpreter that is missing or blocked fails its tests instead.
//
// PowerShell scripts run under the effective execution policy, which TrayWisp never changes. A controlled test
// invocation may allow locally created scripts for its own process only, by setting PSExecutionPolicyPreference to
// RemoteSigned in the environment of the test process (see .github/workflows/ci.yml); TrayWisp passes it on like every
// other variable. No test changes a persistent (user or machine) policy.
[SupportedOSPlatform("windows")]
public sealed class RealInterpreterTests : IDisposable
{
    internal const string RequiredVariable = "TRAYWISP_REQUIRED_INTERPRETERS";

    // Shell metacharacters, spaces and non-ASCII text in the plugin's directory.
    private const string Directory = "real dir & ; 'q' $x %PATH% (1) \u00FC";

    private const string WindowsPowerShell = "WindowsPowerShell";
    private const string PowerShell7 = "PowerShell";
    private const string Python = "Python";
    private const string Node = "Node";

    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    internal static bool IsRequired(string interpreter)
    {
        string[] required = (Environment.GetEnvironmentVariable(RequiredVariable) ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return required.Any(name => name.Equals("all", StringComparison.OrdinalIgnoreCase) || name.Equals(interpreter, StringComparison.OrdinalIgnoreCase));
    }

    // Skips an optional test; fails a required one.
    private static void NotRunnable(string[] interpreters, string reason)
    {
        string? required = interpreters.FirstOrDefault(IsRequired);
        if (required is not null)
        {
            Assert.Fail($"{reason} {required} is required by {RequiredVariable}, so this test must run.");
        }

        Assert.Skip(reason);
    }

    private static bool IsPolicyDenial(PluginExecutionResult result) =>
        result.Status == PluginExecutionStatus.NonZeroExitCode && result.StandardError.Contains("about_Execution_Policies", StringComparison.Ordinal);

    private static string WindowsPowerShellPath() => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    // PowerShell 7 as production resolution finds it (Program Files, then PATH), or null.
    private static string? PowerShell7Path()
    {
        var resolver = new InterpreterResolver(InterpreterSearchLocations.FromCurrentProcess(), pluginRoot: null, new ExecutionDiagnosticBag(), TestContext.Current.CancellationToken);
        return resolver.Resolve(Core.Plugins.PluginLanguage.PowerShell, configured: null, out PluginInterpreter? interpreter) == InterpreterResolution.Resolved
            && interpreter!.Kind == InterpreterKind.PowerShell
            ? interpreter.Path
            : null;
    }

    private static string RequireWindowsPowerShell()
    {
        string path = WindowsPowerShellPath();
        if (!File.Exists(path))
        {
            NotRunnable([WindowsPowerShell], "Windows PowerShell is not installed.");
        }

        return path;
    }

    private static string RequirePowerShell7()
    {
        string? path = PowerShell7Path();
        if (path is null)
        {
            NotRunnable([PowerShell7], "PowerShell 7 (pwsh.exe) was not found in Program Files or PATH.");
        }

        return path!;
    }

    private async Task<PluginExecutionResult> RunAsync(string relativePath, string script, string[] interpreters, PluginExecutionOptions? options = null, bool utf8Bom = false)
    {
        byte[] content = Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n", StringComparison.Ordinal));
        _env.Trust.WriteBytes(relativePath, utf8Bom ? [0xEF, 0xBB, 0xBF, .. content] : content);
        _env.Trust.Approve(_env.TrustService, relativePath);
        PluginExecutionResult result = await _env.RunAsync(_env.Service(options ?? PluginExecutionOptions.Default), relativePath, TimeSpan.FromSeconds(60));
        if (result.Status == PluginExecutionStatus.InterpreterUnavailable)
        {
            NotRunnable(interpreters, $"No interpreter is available for {relativePath}.");
        }

        if (IsPolicyDenial(result))
        {
            NotRunnable(
                interpreters,
                "The effective PowerShell execution policy does not allow scripts, and TrayWisp deliberately does not bypass it. "
                + "A controlled test invocation can set PSExecutionPolicyPreference=RemoteSigned for its own process: " + result.StandardError);
        }

        return result;
    }

    private static string[] Titles(PluginExecutionResult result)
    {
        Assert.True(result.Status == PluginExecutionStatus.Succeeded, $"{result.Status}: {string.Join(" | ", result.Diagnostics)} | stderr: {result.StandardError}");
        return [.. result.Output!.Titles.Select(title => title.Item.Text)];
    }

    // PowerShell output is the console code page unless the script selects UTF-8 itself (docs/execution-engine.md).
    // The script is ASCII and builds non-ASCII text from code points, because Windows PowerShell reads a script
    // without a byte order mark in the ANSI code page.
    private const string PowerShellScript = """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $title = 'Caf' + [char]0xE9 + ' ' + [char]::ConvertFromUtf32(0x1F680)
        Write-Output $title
        Write-Output $PSCommandPath
        Write-Output (Get-Location).ProviderPath
        Write-Output ('stdin:' + [Console]::In.ReadToEnd().Length)
        Write-Output '---'
        Write-Output 'Item'
        """;

    [Fact]
    public async Task PowerShellRunsWithTheDefaultResolution()
    {
        string relative = $"{Directory}/clock.ps1";
        PluginExecutionResult result = await RunAsync(relative, PowerShellScript, [PowerShell7, WindowsPowerShell]);
        string[] titles = Titles(result);

        string script = _env.Trust.PluginPath(relative);
        Assert.Equal(["Caf\u00E9 \uD83D\uDE80", script, Path.GetDirectoryName(script)!, "stdin:0"], titles);
        Assert.Contains(result.Interpreter!.Kind, new[] { InterpreterKind.PowerShell, InterpreterKind.WindowsPowerShell });
        Assert.NotEqual(InterpreterSource.Configured, result.Interpreter.Source);
        Assert.Equal(ProcessCleanupStatus.Exited, result.Cleanup.Status);
        Assert.True(!IsRequired(PowerShell7) || result.Interpreter.Kind == InterpreterKind.PowerShell, "PowerShell 7 is required but Windows PowerShell was resolved.");
    }

    [Fact]
    public async Task WindowsPowerShellRunsAndCanEmitUtf8()
    {
        string windowsPowerShell = RequireWindowsPowerShell();

        PluginExecutionResult result = await RunAsync($"{Directory}/win.ps1", PowerShellScript, [WindowsPowerShell], new PluginExecutionOptions { PowerShellPath = windowsPowerShell });

        Assert.Equal(InterpreterKind.WindowsPowerShell, result.Interpreter!.Kind);
        string script = _env.Trust.PluginPath($"{Directory}/win.ps1");
        Assert.Equal(["Caf\u00E9 \uD83D\uDE80", script, Path.GetDirectoryName(script)!, "stdin:0"], Titles(result));
    }

    [Fact]
    public async Task PowerShell7RunsAndCanEmitUtf8()
    {
        string pwsh = RequirePowerShell7();

        PluginExecutionResult result = await RunAsync($"{Directory}/seven.ps1", PowerShellScript, [PowerShell7], new PluginExecutionOptions { PowerShellPath = pwsh });

        Assert.Equal(InterpreterKind.PowerShell, result.Interpreter!.Kind);
        string script = _env.Trust.PluginPath($"{Directory}/seven.ps1");
        Assert.Equal(["Caf\u00E9 \uD83D\uDE80", script, Path.GetDirectoryName(script)!, "stdin:0"], Titles(result));
    }

    [Fact]
    public async Task APowerShellScriptErrorIsANonZeroExitWithItsStandardError()
    {
        PluginExecutionResult result = await RunAsync("fail.ps1", "Write-Output 'Title'\nWrite-Error 'plugin failure marker'\nexit 4\n", [PowerShell7, WindowsPowerShell]);

        Assert.Equal(PluginExecutionStatus.NonZeroExitCode, result.Status);
        Assert.Equal(4, result.ExitCode);

        // Windows PowerShell wraps error records at the console width, so compare with whitespace collapsed.
        Assert.Contains("plugin failure marker", System.Text.RegularExpressions.Regex.Replace(result.StandardError, @"\s+", " "), StringComparison.Ordinal);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task PowerShellCannotPromptForInput()
    {
        PluginExecutionResult result = await RunAsync("prompt.ps1", "$answer = Read-Host 'Name'\nWrite-Output ('got:' + $answer)\n", [PowerShell7, WindowsPowerShell]);

        // -NonInteractive makes Read-Host fail, or it reads the closed standard input; it never waits.
        Assert.NotEqual(PluginExecutionStatus.TimedOut, result.Status);
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);
    }

    [Fact]
    public async Task TheExecutionPolicyIsNeverBypassed()
    {
        string windowsPowerShell = RequireWindowsPowerShell();
        await AssertRestrictiveProcessPolicyIsPreservedAsync(windowsPowerShell, WindowsPowerShell);
    }

    [Fact]
    public async Task TheExecutionPolicyIsNeverBypassedByPowerShell7()
    {
        string pwsh = RequirePowerShell7();
        await AssertRestrictiveProcessPolicyIsPreservedAsync(pwsh, PowerShell7);
    }

    // A restrictive process-scope policy inherited by TrayWisp (PSExecutionPolicyPreference, here in unusual casing)
    // reaches the interpreter and stops an approved script, whatever the other scopes allow. Deterministic: the
    // restrictive policy is set in the child's base environment, not taken from the machine, and nothing persistent
    // is changed. The control run, with the unchanged environment, shows what the other scopes allow; when it
    // succeeds, it also shows that TrayWisp added no policy override of its own.
    private async Task AssertRestrictiveProcessPolicyIsPreservedAsync(string interpreter, string name)
    {
        string marker = _env.MarkerPath("policy-" + name);
        string script = $"New-Item -ItemType File -Path '{marker.Replace("'", "''", StringComparison.Ordinal)}' | Out-Null\n"
            + "Write-Output ('policy:' + (Get-ExecutionPolicy))\n";
        _env.Trust.Write("policy.ps1", script);
        _env.Trust.Approve(_env.TrustService, "policy.ps1");
        var options = new PluginExecutionOptions { PowerShellPath = interpreter };

        PluginExecutionResult control = await _env.RunAsync(_env.Service(options), "policy.ps1", TimeSpan.FromSeconds(60));
        if (control.Status == PluginExecutionStatus.Succeeded)
        {
            string effective = Assert.Single(control.Output!.Titles).Item.Text;
            Assert.StartsWith("policy:", effective, StringComparison.Ordinal);
            Assert.Equal(PolicyOf(Environment.GetEnvironmentVariable("PSExecutionPolicyPreference")) ?? effective, effective, ignoreCase: true);
            Assert.True(File.Exists(marker));
            File.Delete(marker);
        }
        else
        {
            Assert.True(IsPolicyDenial(control), $"{control.Status}: {control.StandardError}");
            Assert.False(IsRequired(name), $"{name} is required by {RequiredVariable}, but the effective policy of the test invocation does not allow scripts: {control.StandardError}");
            TestContext.Current.TestOutputHelper?.WriteLine("The other scopes do not allow scripts here; only the restrictive run is meaningful.");
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string)entry.Value!;
        }

        environment.Remove("PSExecutionPolicyPreference");
        environment.Add("psExecutionPolicyPREFERENCE", "Restricted");
        PluginExecutionResult restricted = await _env.RunAsync(_env.Service(options, new ExecutionTestHooks { BaseEnvironment = environment }), "policy.ps1", TimeSpan.FromSeconds(60));

        Assert.True(IsPolicyDenial(restricted), $"{restricted.Status}: {restricted.StandardError}");
        Assert.Equal(PluginExecutionStatus.NonZeroExitCode, restricted.Status);
        Assert.Null(restricted.Output);
        Assert.False(File.Exists(marker), "The script ran although the inherited process policy is Restricted.");

        static string? PolicyOf(string? value) => string.IsNullOrEmpty(value) ? null : "policy:" + value;
    }

    private const string PythonScript = """
        import os, sys
        import localmod
        print("Caf\u00E9 \U0001F680")
        print(sys.argv[0])
        print(os.getcwd())
        print("stdin:%d" % len(sys.stdin.read()))
        print("flags:%d%d" % (sys.flags.ignore_environment, sys.flags.utf8_mode))
        print(localmod.VALUE)
        print("---")
        print("Item")
        """;

    [Fact]
    public async Task PythonRunsWithUtf8OutputAndPluginLocalImports()
    {
        string relative = $"{Directory}/day.py";
        _env.Trust.Write($"{Directory}/localmod.py", "VALUE = 'local import'\n");

        PluginExecutionResult result = await RunAsync(relative, PythonScript, [Python]);
        string[] titles = Titles(result);

        string script = _env.Trust.PluginPath(relative);
        Assert.Equal(["Caf\u00E9 \uD83D\uDE80", script, Path.GetDirectoryName(script)!, "stdin:0", "flags:11", "local import"], titles);
        Assert.Equal(InterpreterKind.Python, result.Interpreter!.Kind);
        Assert.Equal(StandardOutputEncoding.Utf8, result.StandardOutputEncoding);
    }

    [Fact]
    public async Task APythonExceptionIsANonZeroExit()
    {
        PluginExecutionResult result = await RunAsync("fail.py", "print('Title')\nraise RuntimeError('python failure marker')\n", [Python]);

        Assert.Equal(PluginExecutionStatus.NonZeroExitCode, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("python failure marker", result.StandardError, StringComparison.Ordinal);
    }

    private const string NodeScript = """
        const fs = require("fs");
        console.log("Caf\u00E9 \u{1F680}");
        console.log(process.argv[1]);
        console.log(process.cwd());
        console.log("stdin:" + fs.readFileSync(0).length);
        console.log("---");
        console.log("Item");
        """;

    [Fact]
    public async Task NodeRunsWithUtf8Output()
    {
        string relative = $"{Directory}/hello.js";

        PluginExecutionResult result = await RunAsync(relative, NodeScript, [Node]);
        string[] titles = Titles(result);

        string script = _env.Trust.PluginPath(relative);
        Assert.Equal(["Caf\u00E9 \uD83D\uDE80", script, Path.GetDirectoryName(script)!, "stdin:0"], titles);
        Assert.Equal(InterpreterKind.Node, result.Interpreter!.Kind);
    }

    [Fact]
    public async Task TheExamplePluginsRunWhenTheirInterpretersAreAvailable()
    {
        string? examples = FindExamples();
        Assert.SkipWhen(examples is null, "The repository's plugins/examples directory was not found.");
        var skipped = new List<string>();
        foreach (string file in System.IO.Directory.GetFiles(examples).Where(path => !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            _env.Trust.WriteBytes(name, File.ReadAllBytes(file));
            _env.Trust.Approve(_env.TrustService, name);
            DateTime before = DateTime.Now;
            PluginExecutionResult result = await _env.RunAsync(_env.Service(PluginExecutionOptions.Default), name, TimeSpan.FromSeconds(60));
            if (result.Status == PluginExecutionStatus.InterpreterUnavailable || IsPolicyDenial(result))
            {
                string[] interpreters = Path.GetExtension(name).ToUpperInvariant() switch
                {
                    ".PS1" => [PowerShell7, WindowsPowerShell],
                    ".PY" => [Python],
                    _ => [Node],
                };
                Assert.False(interpreters.Any(IsRequired), $"{name} could not run ({result.Status}) although its interpreter is required by {RequiredVariable}: {result.StandardError}");
                skipped.Add($"{name} ({result.Status})");
                continue;
            }

            Assert.True(result.Succeeded, $"{name}: {result.Status} {result.StandardError}");

            // A non-terminating PowerShell error still exits with code 0, so an example must also be silent on stderr.
            Assert.True(result.StandardError.Length == 0, $"{name} wrote to standard error: {result.StandardError}");
            Assert.NotEmpty(result.Output!.Titles);
            Assert.NotEmpty(result.Output.Items);
            Assert.DoesNotContain(result.OutputDiagnostics, d => d.Severity == Core.Plugins.PluginDiagnosticSeverity.Error);
            if (name == ClockExample)
            {
                AssertClockWeekItem(result, before, DateTime.Now);
            }
        }

        Assert.SkipWhen(skipped.Count > 0, "Not run: " + string.Join(", ", skipped));
    }

    private const string ClockExample = "clock.1m.ps1";

    // The actual clock example with Windows PowerShell 5.1, even where PowerShell 7 would be resolved by default.
    [Fact]
    public async Task TheClockExampleRunsWithWindowsPowerShell()
    {
        string windowsPowerShell = RequireWindowsPowerShell();
        string? examples = FindExamples();
        Assert.SkipWhen(examples is null, "The repository's plugins/examples directory was not found.");

        DateTime before = DateTime.Now;
        PluginExecutionResult result = await RunAsync(
            ClockExample,
            File.ReadAllText(Path.Combine(examples, ClockExample)),
            [WindowsPowerShell],
            new PluginExecutionOptions { PowerShellPath = windowsPowerShell });

        Assert.Equal(InterpreterKind.WindowsPowerShell, result.Interpreter!.Kind);
        Assert.True(result.Succeeded, $"{result.Status}: {result.StandardError}");
        Assert.True(result.StandardError.Length == 0, $"The clock example wrote to standard error: {result.StandardError}");
        AssertClockWeekItem(result, before, DateTime.Now);
    }

    [Fact]
    public async Task TheClockExampleComputesIsoWeeksAcrossYearBoundariesWithWindowsPowerShell()
    {
        string windowsPowerShell = RequireWindowsPowerShell();
        await AssertClockIsoWeeksAsync(windowsPowerShell, WindowsPowerShell, InterpreterKind.WindowsPowerShell);
    }

    [Fact]
    public async Task TheClockExampleComputesIsoWeeksAcrossYearBoundariesWithPowerShell7()
    {
        string pwsh = RequirePowerShell7();
        await AssertClockIsoWeeksAsync(pwsh, PowerShell7, InterpreterKind.PowerShell);
    }

    // Runs the unchanged clock example once per date, with Get-Date replaced by a function (functions take precedence
    // over cmdlets) and a French culture, whose day and month names are not all ASCII. The expected week numbers come
    // from .NET's ISOWeek, which Windows PowerShell 5.1 does not have. The wrapper selects no encoding itself and writes
    // only after the clock has run, so the UTF-8 output shows that the example selects UTF-8 before its first write.
    private async Task AssertClockIsoWeeksAsync(string interpreter, string name, InterpreterKind kind)
    {
        string? examples = FindExamples();
        Assert.SkipWhen(examples is null, "The repository's plugins/examples directory was not found.");
        _env.Trust.WriteBytes($"weeks/{ClockExample}", File.ReadAllBytes(Path.Combine(examples, ClockExample)));

        var days = new List<DateTime>();
        for (int year = 2015; year <= 2032; year++)
        {
            for (DateTime day = new(year - 1, 12, 25); day <= new DateTime(year, 1, 8); day = day.AddDays(1))
            {
                days.Add(day);
            }
        }

        string wrapper = "[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::new('fr-FR')\n"
            + "function Get-Date { $testNow }\n"
            + $"$clock = Join-Path $PSScriptRoot '{ClockExample}'\n"
            + "$results = foreach ($day in @(" + string.Join(",", days.Select(day => $"'{Iso(day)}'")) + ")) {\n"
            + "    $testNow = [datetime]::ParseExact($day, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture).AddHours(23).AddMinutes(59)\n"
            + "    $lines = @(& $clock)\n"
            + "    $day + ' / ' + ($lines -join ' / ')\n"
            + "}\n"
            + "Write-Output 'Weeks'\n"
            + "Write-Output '---'\n"
            + "$results\n";

        PluginExecutionResult result = await RunAsync("weeks/weeks.ps1", wrapper, [name], new PluginExecutionOptions { PowerShellPath = interpreter });

        Assert.Equal(kind, result.Interpreter!.Kind);
        Assert.True(result.Succeeded, $"{result.Status}: {string.Join(" | ", result.Diagnostics)} | stderr: {result.StandardError}");
        Assert.True(result.StandardError.Length == 0, $"The clock example wrote to standard error: {result.StandardError}");
        Assert.Equal(StandardOutputEncoding.Utf8, result.StandardOutputEncoding);

        var french = new CultureInfo("fr-FR");
        string[] expected = [.. days.Select(day => $"{Iso(day)} / 23:59 / --- / {day.ToString("dddd d MMMM yyyy", french)} / Week {ISOWeek.GetWeekOfYear(day)}")];
        string[] actual = [.. result.Output!.Items.OfType<Core.Output.MenuItem>().Select(item => item.Text)];
        Assert.Equal(expected, actual);

        // The year boundaries that ISO 8601 assigns to the neighbouring year, independently of ISOWeek.
        Assert.Contains("2021-01-01 / 23:59 / --- / vendredi 1 janvier 2021 / Week 53", actual);
        Assert.Contains("2021-01-04 / 23:59 / --- / lundi 4 janvier 2021 / Week 1", actual);
        Assert.Contains("2018-12-31 / 23:59 / --- / lundi 31 d\u00E9cembre 2018 / Week 1", actual);

        static string Iso(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // The clock's last menu item is the ISO week of the moment it ran, which lies between before and after.
    private static void AssertClockWeekItem(PluginExecutionResult result, DateTime before, DateTime after)
    {
        string[] items = [.. result.Output!.Items.OfType<Core.Output.MenuItem>().Select(item => item.Text)];
        Assert.Equal(2, items.Length);
        string[] weeks = [$"Week {ISOWeek.GetWeekOfYear(before)}", $"Week {ISOWeek.GetWeekOfYear(after)}"];
        Assert.Contains(items[1], weeks);
    }

    private static string? FindExamples()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TrayWisp.slnx")))
            {
                string examples = Path.Combine(directory.FullName, "plugins", "examples");
                return System.IO.Directory.Exists(examples) ? examples : null;
            }
        }

        return null;
    }
}
