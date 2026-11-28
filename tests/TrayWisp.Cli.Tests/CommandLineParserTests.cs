using System.Globalization;
using TrayWisp.Cli.CommandLine;
using Xunit;

namespace TrayWisp.Cli.Tests;

// The command-line grammar (docs/cli.md, "Command-line syntax"). Pure: no file system access.
public sealed class CommandLineParserTests
{
    private const string Fingerprint = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static CliRequest Request(params string[] args)
    {
        ParseResult result = CommandLineParser.Parse(args);
        Assert.Null(result.Error);
        return Assert.IsType<CliRequest>(result.Request);
    }

    private static string Error(params string[] args)
    {
        ParseResult result = CommandLineParser.Parse(args);
        Assert.Null(result.Request);
        Assert.False(result.ShowHelp);
        Assert.False(result.ShowVersion);
        return Assert.IsType<string>(result.Error);
    }

    [Fact]
    public void NoArgumentsAndHelpShowGlobalHelp()
    {
        Assert.True(CommandLineParser.Parse([]).ShowHelp);
        Assert.True(CommandLineParser.Parse(["--help"]).ShowHelp);
        Assert.True(CommandLineParser.Parse(["-h"]).ShowHelp);
        Assert.True(CommandLineParser.Parse(["--version"]).ShowVersion);
    }

    [Theory]
    [InlineData("--help", "list")]
    [InlineData("--version", "x")]
    [InlineData("-h", "--json")]
    public void GlobalOptionsTakeNoOtherArguments(string first, string second) => Error(first, second);

    [Fact]
    public void EveryCommandHasHelpWithItsOptions()
    {
        foreach (CommandSpec command in CommandLineParser.Commands)
        {
            Assert.Same(command, CommandLineParser.Parse([command.Name, "--help"]).HelpFor);
            Assert.Same(command, CommandLineParser.Parse([command.Name, "-h"]).HelpFor);
            string help = HelpText.ForCommand(command);
            Assert.StartsWith("Usage: traywisp " + command.Name, help, StringComparison.Ordinal);
            foreach (CliOption option in command.Options)
            {
                Assert.Contains(CommandLineParser.Spec(option).Name, help, StringComparison.Ordinal);
            }

            Assert.Contains(command.Name, HelpText.Global(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HelpWinsOverOtherArgumentsButNotAfterTheSeparator()
    {
        Assert.NotNull(CommandLineParser.Parse(["run", "--bogus", "--help"]).HelpFor);
        Assert.Contains("one plugin identity", Error("run", "a.ps1", "--", "--help"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("approve")]
    [InlineData("revoke")]
    [InlineData("run")]
    public void RequiredOperandsAreRequired(string command) => Assert.Contains("needs a plugin identity", Error(command), StringComparison.Ordinal);

    [Theory]
    [InlineData("list", "a.ps1")]
    [InlineData("doctor", "a.ps1")]
    [InlineData("logs", "a.ps1")]
    [InlineData("validate-output", "a.ps1")]
    public void CommandsWithoutOperandsRejectThem(string command, string operand) => Assert.Contains("takes no operands", Error(command, operand), StringComparison.Ordinal);

    [Fact]
    public void ExtraOperandsAreRejected()
    {
        Assert.Contains("one plugin identity", Error("inspect", "a.ps1", "b.ps1"), StringComparison.Ordinal);
        Assert.Contains("one plugin identity", Error("status", "a.ps1", "b.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void StatusTakesAnOptionalOperand()
    {
        Assert.Null(Request("status").PluginId);
        Assert.Equal("a.ps1", Request("status", "a.ps1").PluginId!.Value);
    }

    [Theory]
    [InlineData("List")]
    [InlineData("lst")]
    [InlineData("ls")]
    [InlineData("run-all")]
    public void CommandsMatchExactly(string command) => Assert.Contains("Unknown command", Error(command), StringComparison.Ordinal);

    [Fact]
    public void OptionsBeforeTheCommandAreRejected() => Assert.Contains("must come first", Error("--json", "list"), StringComparison.Ordinal);

    [Theory]
    [InlineData("--jsn")]
    [InlineData("--JSON")]
    [InlineData("-j")]
    [InlineData("--force")]
    [InlineData("--unsafe")]
    [InlineData("--yes")]
    public void UnknownOptionsAreRejected(string option) => Assert.Contains("Unknown option", Error("run", "a.ps1", option), StringComparison.Ordinal);

    [Theory]
    [InlineData("list", "--timeout", "5")]
    [InlineData("list", "--data-dir", @"C:\data")]
    [InlineData("inspect", "--sha256", Fingerprint)]
    [InlineData("validate-output", "--root", @"C:\plugins")]
    [InlineData("doctor", "--limit", "5")]
    [InlineData("status", "--show-stderr", null)]
    [InlineData("run", "--truncated", null)]
    public void OptionsNotAcceptedByACommandAreRejected(string command, string option, string? value)
    {
        string[] args = value is null ? [command, option] : [command, option, value];
        if (command is "inspect" or "run")
        {
            args = [.. args, "a.ps1"];
        }

        Assert.Contains("is not accepted by", Error(args), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateOptionsAreRejected()
    {
        Assert.Contains("more than once", Error("list", "--json", "--json"), StringComparison.Ordinal);
        Assert.Contains("more than once", Error("list", "--root", @"C:\a", "--root=C:\\b"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--root")]
    [InlineData("--root=")]
    public void MissingOptionValuesAreRejected(string option) => Assert.Contains("needs a value", Error("list", option), StringComparison.Ordinal);

    [Fact]
    public void AValueThatLooksLikeAnOptionIsNotTaken()
    {
        Assert.Contains("needs a value", Error("list", "--root", "--json"), StringComparison.Ordinal);
        Assert.Contains("takes no value", Error("list", "--json=true"), StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsMayPrecedeOrFollowTheOperandInBothForms()
    {
        CliRequest before = Request("run", "--timeout", "5", "--root", @"C:\p", "a.ps1");
        CliRequest after = Request("run", "a.ps1", "--timeout=5", @"--root=C:\p");
        Assert.Equal(before.PluginId, after.PluginId);
        Assert.Equal(TimeSpan.FromSeconds(5), before.Timeout);
        Assert.Equal(before.Timeout, after.Timeout);
        Assert.Equal(before.Root, after.Root);
    }

    [Fact]
    public void TheSeparatorEndsOptions()
    {
        Assert.Equal("-dash.ps1", Request("status", "--", "-dash.ps1").PluginId!.Value);
        Assert.Contains("one plugin identity", Error("status", "a.ps1", "--", "--json"), StringComparison.Ordinal);
        Assert.Contains("not valid", Error("status", "--", "--json"), StringComparison.Ordinal);
        Assert.False(Request("status", "--", "x.ps1").Json);
    }

    [Theory]
    [InlineData("work/clock 5s.ps1", "work/clock 5s.ps1")]
    [InlineData(@"work\clock.5s.ps1", "work/clock.5s.ps1")]
    [InlineData("caf\u00E9/\u65E5\u672C.py", "caf\u00E9/\u65E5\u672C.py")]
    [InlineData("rocket \uD83D\uDE80.js", "rocket \uD83D\uDE80.js")]
    [InlineData("a & b ; c $x `y.ps1", "a & b ; c $x `y.ps1")]
    public void IdentitiesWithSpacesAndUnicodeAreAccepted(string operand, string expected) => Assert.Equal(expected, Request("inspect", operand).PluginId!.Value);

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("tool.exe")]
    [InlineData(".hidden/a.ps1")]
    [InlineData("a/../b.ps1")]
    [InlineData("*.ps1")]
    [InlineData("C:\\plugins\\a.ps1")]
    [InlineData("a/b/c/d/e/f/g/h/i/j.ps1")]
    [InlineData("a.ps1 ")]
    [InlineData("\u202Egnp.ps1")]
    public void UnsupportedIdentitiesAreRejected(string operand) => Assert.Contains("not valid", Error("inspect", operand), StringComparison.Ordinal);

    [Fact]
    public void AnIdentityWithAnUnpairedSurrogateIsRejected() => Error("inspect", "a" + (char)0xD800 + ".ps1");

    [Theory]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8555")]
    [InlineData("g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("*")]
    public void FingerprintsAreStrict(string value) => Assert.Contains("64 lowercase", Error("approve", "a.ps1", "--sha256", value), StringComparison.Ordinal);

    [Fact]
    public void ApprovalRequiresAFingerprint()
    {
        Assert.Contains("--sha256 FINGERPRINT", Error("approve", "a.ps1"), StringComparison.Ordinal);
        Assert.Equal(Fingerprint, Request("approve", "a.ps1", "--sha256", Fingerprint).Sha256!.Value);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("300", 300)]
    [InlineData("030", 30)]
    public void TimeoutsInRangeAreAccepted(string value, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), Request("run", "a.ps1", "--timeout", value).Timeout);

    [Theory]
    [InlineData("0")]
    [InlineData("301")]
    [InlineData("1.5")]
    [InlineData("1,5")]
    [InlineData("+5")]
    [InlineData("-5")]
    [InlineData(" 5")]
    [InlineData("5s")]
    [InlineData("1e2")]
    [InlineData("\u0665")]
    [InlineData("\uFF15")]
    [InlineData("99999999999")]
    public void InvalidTimeoutsAreRejected(string value) => Assert.Contains("--timeout", Error("run", "a.ps1", "--timeout", value), StringComparison.Ordinal);

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("ten")]
    public void InvalidLimitsAreRejected(string value) => Error("logs", "--limit", value);

    [Fact]
    public void TheLimitDefaultsToTwenty()
    {
        Assert.Equal(CommandLineParser.DefaultLimit, Request("logs").Limit);
        Assert.Equal(200, Request("logs", "--limit", "200").Limit);
    }

    [Fact]
    public void NumbersAreParsedTheSameInEveryCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "ar-SA", "fa-IR", "tr-TR", "de-DE", "hi-IN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(TimeSpan.FromSeconds(12), Request("run", "a.ps1", "--timeout", "12").Timeout);
                Error("run", "a.ps1", "--timeout", "1.5");
                Assert.Equal(7, Request("logs", "--limit", "7").Limit);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("plugins")]
    [InlineData(@".\plugins")]
    [InlineData("C:plugins")]
    [InlineData(@"\plugins")]
    [InlineData(@"\\server\share\plugins")]
    [InlineData(@"\\?\C:\plugins")]
    [InlineData(@"C:\a\..\b")]
    [InlineData(@"C:\a\\b")]
    [InlineData(@"C:\plugins:stream")]
    [InlineData("%LOCALAPPDATA%\\TrayWisp")]
    [InlineData("~/plugins")]
    public void UnsupportedPathsAreRejectedAndNeverExpanded(string path)
    {
        Assert.Contains("--root", Error("list", "--root", path), StringComparison.Ordinal);
        Assert.Contains("--data-dir", Error("status", "--data-dir", path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("python.exe")]
    [InlineData(@"C:\Python\python")]
    [InlineData(@"C:\Python\python.cmd")]
    [InlineData(@"\\server\python.exe")]
    [InlineData(@"..\python.exe")]
    public void InterpreterPathsMustBeQualifiedExecutables(string path) => Assert.Contains("--python", Error("run", "a.py", "--python", path), StringComparison.Ordinal);

    [Fact]
    public void ArgumentCountAndLengthAreBounded()
    {
        Assert.Contains("Too many arguments", Error([.. Enumerable.Repeat("status", 1), .. Enumerable.Repeat("--json", CommandLineParser.MaxArguments)]), StringComparison.Ordinal);
        Assert.Contains("longer than", Error("inspect", new string('a', CommandLineParser.MaxArgumentLength + 1) + ".ps1"), StringComparison.Ordinal);
        Assert.Contains("NUL", Error("inspect", "a\0.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void UnrecognizedArgumentsAreNotEchoedRaw()
    {
        Assert.Contains("(not shown)", Error("\u001B]8;;http://evil\u0007x"), StringComparison.Ordinal);
        Assert.Contains("(not shown)", Error("list", "--\u001B[31mred"), StringComparison.Ordinal);
        Assert.Contains("(not shown)", Error("list", "--" + new string('x', 40)), StringComparison.Ordinal);
        Assert.DoesNotContain("\u001B", Error("list", "--\u001B[31mred"), StringComparison.Ordinal);
        Assert.Contains("'--jsno'", Error("list", "--jsno"), StringComparison.Ordinal);
    }

    [Fact]
    public void UsageErrorsRememberTheCommandAndJson()
    {
        ParseResult result = CommandLineParser.Parse(["run", "--json"]);
        Assert.NotNull(result.Error);
        Assert.Equal("run", result.ErrorCommand!.Name);
        Assert.True(result.ErrorWantsJson);
        Assert.False(CommandLineParser.Parse(["run", "--", "--json"]).ErrorWantsJson);
        Assert.Null(CommandLineParser.Parse(["nope", "--json"]).ErrorCommand);
    }

    [Fact]
    public void FlagsAreRecorded()
    {
        CliRequest run = Request("run", "a.ps1", "--show-stderr", "--json", "--powershell", @"C:\PS\pwsh.exe", "--python", @"C:\Py\python.exe", "--node", @"C:\N\node.exe");
        Assert.True(run.ShowStandardError);
        Assert.True(run.Json);
        Assert.Equal(@"C:\PS\pwsh.exe", run.PowerShellPath);
        Assert.Equal(@"C:\Py\python.exe", run.PythonPath);
        Assert.Equal(@"C:\N\node.exe", run.NodePath);
        Assert.True(Request("validate-output", "--truncated").Truncated);
        Assert.Equal(@"C:\Data", Request("status", "--data-dir", @"C:\Data\").DataDirectory!.Value);
    }
}
