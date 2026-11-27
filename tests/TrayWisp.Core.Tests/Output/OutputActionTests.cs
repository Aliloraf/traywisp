using System.Collections.Immutable;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using Xunit;
using static TrayWisp.Core.Tests.Output.OutputTestHelpers;

namespace TrayWisp.Core.Tests.Output;

public sealed class OutputActionTests
{
    [Theory]
    [InlineData("refresh=true")]
    [InlineData("refresh=TRUE")]
    [InlineData("refresh='true'")]
    public void RefreshAloneRefreshesThePlugin(string parameters)
    {
        RefreshAction action = Assert.IsType<RefreshAction>(ParseCleanItem("Refresh | " + parameters).Action);

        Assert.True(action.RefreshesPlugin);
    }

    [Fact]
    public void RefreshFalseAloneIsNoAction() => Assert.Null(ParseCleanItem("Item | refresh=false").Action);

    [Fact]
    public void UrlAction()
    {
        UrlAction action = Assert.IsType<UrlAction>(ParseCleanItem("Documentation | href=https://example.com").Action);

        Assert.Equal("https://example.com", action.Url.OriginalString);
        Assert.Equal("https", action.Url.Scheme);
        Assert.Equal("example.com", action.Url.Host);
        Assert.False(action.RefreshesPlugin);
    }

    [Fact]
    public void UrlWithRefreshRefreshesAfterOpening()
    {
        UrlAction action = Assert.IsType<UrlAction>(ParseCleanItem("Open | href=https://example.com refresh=true").Action);

        Assert.True(action.RefreshesPlugin);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/path/to?q=a=b&c=%20d#fragment")]
    [InlineData("HTTPS://Example.COM/Path")]
    [InlineData("https://example.com:8443/")]
    [InlineData("http://127.0.0.1:8080/status")]
    [InlineData("http://[::1]:8080/x")]
    [InlineData("https://xn--bcher-kva.example/")]
    [InlineData("https://github.com/notifications?query=reason%3Amention")]
    [InlineData("https://example.com/a%7Cb")]
    [InlineData("https://example.com/a%20b")]
    [InlineData("https://example.com/?q=%0A")]
    [InlineData("http://[2001:db8::1]/")]
    public void AcceptsAbsoluteHttpAndHttpsUrls(string value)
    {
        UrlAction action = Assert.IsType<UrlAction>(ParseCleanItem("Open | href=" + value).Action);

        Assert.Equal(value.Trim('"'), action.Url.OriginalString);
        Assert.True(action.Url.Scheme is "http" or "https");
        Assert.NotEmpty(action.Url.Host);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("file://server/share")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("vbscript:msgbox")]
    [InlineData("ftp://example.com/")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ms-settings:display")]
    [InlineData("myapp://open")]
    [InlineData("https:example.com")]
    [InlineData("https:/example.com")]
    [InlineData("https:///example.com")]
    [InlineData("https://")]
    [InlineData("https://?q")]
    [InlineData("//example.com")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com@evil.example/")]
    [InlineData("\"https://exa mple.com\"")]
    [InlineData("\" https://example.com\"")]
    [InlineData("\"https://example.com \"")]
    [InlineData("https://b\u00FCcher.example/")]
    [InlineData("https://example.com/\u00E9")]
    [InlineData("https:\\\\example.com")]
    [InlineData("https://example.com\\@evil.example/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("https://example.com:99999/")]
    [InlineData("https://[::1/")]
    [InlineData("\"https://example.com/\u0007\"")]
    [InlineData("\"https://example.com/\u202Etxt.exe\"")]
    [InlineData("\"\"")]
    [InlineData("\"https://example.com/a|b\"")]
    [InlineData("https://example.com/%zz")]
    [InlineData("https://example.com/%")]
    [InlineData("https://example.com/%2")]
    [InlineData("https://example.com/?q=%G0")]
    [InlineData("https://example.com/a%%41")]
    [InlineData("https://example.com/a{b}")]
    [InlineData("https://example.com/a^b")]
    [InlineData("https://example.com/a<b>")]
    [InlineData("'https://example.com/a\"b'")]
    [InlineData("https://example.com/#a#b")]
    [InlineData("https://exa%6Dple.com/")]
    [InlineData("https://example.com%40evil.example/")]
    [InlineData("http://[fe80::1%25eth0]/")]
    public void RejectsOtherUrls(string value)
    {
        MenuItem item = ParseItem("Open | refresh=true href=" + value, out ImmutableArray<OutputDiagnostic> diagnostics);

        // The refresh request is suppressed together with the invalid URL.
        Assert.Null(item.Action);
        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(OutputDiagnosticCode.InvalidUrl, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void CommandWithTerminalPreference()
    {
        CommandAction action = Assert.IsType<CommandAction>(ParseCleanItem("Task Manager | shell=taskmgr.exe terminal=false").Action);

        Assert.Equal("taskmgr.exe", action.Executable);
        Assert.Empty(action.Arguments);
        Assert.False(action.RunInTerminal);
        Assert.False(action.RefreshesPlugin);
        Assert.Null(Assert.IsType<CommandAction>(ParseCleanItem("X | shell=x.exe").Action).RunInTerminal);
        Assert.True(Assert.IsType<CommandAction>(ParseCleanItem("X | shell=x.exe terminal=TRUE").Action).RunInTerminal);
    }

    [Fact]
    public void WindowsPathsWithSpacesAndBackslashesArePreserved()
    {
        CommandAction action = Assert.IsType<CommandAction>(
            ParseCleanItem("Open editor | shell=\"C:\\Program Files\\Editor\\editor.exe\" param1=\"D:\\My Files\\notes.txt\"").Action);

        Assert.Equal("C:\\Program Files\\Editor\\editor.exe", action.Executable);
        Assert.Equal(["D:\\My Files\\notes.txt"], action.Arguments);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("bash")]
    [InlineData("BASH")]
    public void ShellAndBashAreAliasesForTheExecutable(string name)
    {
        CommandAction action = Assert.IsType<CommandAction>(ParseCleanItem($"Run | {name}=/usr/bin/env param1=python3").Action);

        Assert.Equal("/usr/bin/env", action.Executable);
        Assert.Equal(["python3"], action.Arguments);
    }

    [Fact]
    public void ExecutableIsNeverSplitIntoCommandAndArguments()
    {
        CommandAction action = Assert.IsType<CommandAction>(ParseCleanItem("Run | shell=\"notepad.exe C:\\notes.txt\"").Action);

        Assert.Equal("notepad.exe C:\\notes.txt", action.Executable);
        Assert.Empty(action.Arguments);
    }

    [Fact]
    public void NothingIsExpandedOrRewritten()
    {
        CommandAction action = Assert.IsType<CommandAction>(ParseCleanItem(
            "Run | shell=%COMSPEC% param1=$HOME param2=~/x param3=*.txt param4=$(whoami) param5=`id` param6=\\n param7=%USERPROFILE%\\x param8=/bin/sh").Action);

        Assert.Equal("%COMSPEC%", action.Executable);
        Assert.Equal(["$HOME", "~/x", "*.txt", "$(whoami)", "`id`", "\\n", "%USERPROFILE%\\x", "/bin/sh"], action.Arguments);
    }

    [Theory]
    [InlineData("param0=a param1=b param2=c")]
    [InlineData("param1=a param2=b param3=c")]
    [InlineData("param3=c param1=a param2=b")]
    [InlineData("PARAM1=a Param2=b param3=c")]
    public void ZeroAndOneBasedArgumentsAreOrderedByIndex(string parameters)
    {
        CommandAction action = Assert.IsType<CommandAction>(ParseCleanItem("Run | shell=tool.exe " + parameters).Action);

        Assert.Equal(["a", "b", "c"], action.Arguments);
    }

    [Fact]
    public void ArgumentsAreOrderedNumericallyNotLexically()
    {
        string parameters = string.Join(' ', Enumerable.Range(1, 12).Reverse().Select(i => $"param{i}=v{i}"));

        CommandAction action = Assert.IsType<CommandAction>(ParseCleanItem("Run | shell=tool.exe " + parameters).Action);

        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"v{i}"), action.Arguments);
    }

    [Fact]
    public void EmptyAndSpecialArgumentsArePreserved()
    {
        CommandAction action = Assert.IsType<CommandAction>(ParseCleanItem(
            "Run | shell=tool.exe param1=\"\" param2=x param3='' param4=\"a | b=c\" param5='say \"hi\"' param6=\"C:\\Temp\\\" param7=\"\U0001F680 \u65E5\u672C\"").Action);

        Assert.Equal(["", "x", "", "a | b=c", "say \"hi\"", "C:\\Temp\\", "\U0001F680 \u65E5\u672C"], action.Arguments);
    }

    [Fact]
    public void AcceptsThirtyTwoArgumentsWithEitherNumbering()
    {
        string oneBased = string.Join(' ', Enumerable.Range(1, 32).Select(i => $"param{i}={i}"));
        string zeroBased = string.Join(' ', Enumerable.Range(0, 32).Select(i => $"param{i}={i}"));

        Assert.Equal(32, Assert.IsType<CommandAction>(ParseCleanItem("Run | shell=t.exe " + oneBased).Action).Arguments.Length);
        Assert.Equal(32, Assert.IsType<CommandAction>(ParseCleanItem("Run | shell=t.exe " + zeroBased).Action).Arguments.Length);
    }

    [Theory]
    [InlineData("param1=a param3=c", "without gaps")]
    [InlineData("param0=a param2=c", "without gaps")]
    [InlineData("param2=a param3=b", "start at param0 or param1")]
    [InlineData("param5=a", "start at param0 or param1")]
    public void RejectsArgumentNumberingWithGapsOrAWrongStart(string parameters, string message)
    {
        MenuItem item = ParseItem("Run | shell=tool.exe " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(OutputDiagnosticCode.InvalidCommandArguments, diagnostic.Code);
        Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsThirtyThreeArguments()
    {
        string parameters = string.Join(' ', Enumerable.Range(0, 33).Select(i => $"param{i}={i}"));

        MenuItem item = ParseItem("Run | shell=t.exe " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        Assert.Contains("at most 32 arguments", Assert.Single(diagnostics).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("param1=a PARAM1=b", OutputDiagnosticCode.DuplicateParameter)]
    [InlineData("param1=a param1=a", OutputDiagnosticCode.DuplicateParameter)]
    [InlineData("param01=a", OutputDiagnosticCode.InvalidCommandArguments)]
    [InlineData("param00=a", OutputDiagnosticCode.InvalidCommandArguments)]
    [InlineData("param=a", OutputDiagnosticCode.InvalidCommandArguments)]
    [InlineData("param33=a", OutputDiagnosticCode.InvalidCommandArguments)]
    [InlineData("param1=a param33=b", OutputDiagnosticCode.InvalidCommandArguments)]
    [InlineData("param4294967297=a", OutputDiagnosticCode.InvalidCommandArguments)]
    [InlineData("param99999999999999999999999999999999999999=a", OutputDiagnosticCode.InvalidCommandArguments)]
    public void RejectsDuplicateMalformedAndOverflowingIndices(string parameters, OutputDiagnosticCode code)
    {
        MenuItem item = ParseItem("Run | shell=tool.exe " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        Assert.Equal([code], Codes(diagnostics));
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    [Fact]
    public void ArgumentsAreNeverShiftedToCloseAGap()
    {
        // param1 and param3 must not become a two-argument list.
        MenuItem item = ParseItem("Delete | shell=tool.exe param1=--keep param3=C:\\Important", out _);

        Assert.Null(item.Action);
    }

    [Theory]
    [InlineData("shell=\"\"")]
    [InlineData("shell=\"   \"")]
    [InlineData("shell=\" taskmgr.exe\"")]
    [InlineData("shell=\"taskmgr.exe \"")]
    [InlineData("shell=\"task\tmgr.exe\"")]
    [InlineData("shell=\"a\u0007b.exe\"")]
    [InlineData("shell=\"evil\u202Etxt.exe\"")]
    [InlineData("shell=\"evil\u200Fexe\"")]
    [InlineData("shell=\"a\u2028b\"")]
    public void RejectsEmptyOrUnsafeExecutables(string parameters)
    {
        MenuItem item = ParseItem("Run | " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        Assert.Equal([OutputDiagnosticCode.InvalidCommand], Codes(diagnostics));
    }

    [Theory]
    [InlineData("param1=\"a\u001Bb\"")]
    [InlineData("param1=\"a\tb\"")]
    [InlineData("param1=\"\u202Eexe.txt\"")]
    [InlineData("param1=\"a\u0085b\"")]
    public void RejectsUnsafeArguments(string parameters)
    {
        MenuItem item = ParseItem("Run | shell=tool.exe " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        Assert.Equal([OutputDiagnosticCode.InvalidCommandArguments], Codes(diagnostics));
    }

    [Fact]
    public void ArgumentsWithLoneSurrogatesAreRejected()
    {
        MenuItem item = ParseItem("Run | shell=tool.exe param1=\"a" + (char)0xD800 + "b\"", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        Assert.Equal([OutputDiagnosticCode.InvalidCommandArguments], Codes(diagnostics));
    }

    [Fact]
    public void CommandWithRefreshRefreshesAfterCompletion()
    {
        CommandAction action = Assert.IsType<CommandAction>(ParseCleanItem("Restart | shell=svc.exe param1=restart refresh=true").Action);

        Assert.True(action.RefreshesPlugin);
    }

    [Theory]
    [InlineData("href=https://example.com shell=taskmgr.exe", OutputDiagnosticCode.ConflictingActions)]
    [InlineData("bash=taskmgr.exe href=https://example.com refresh=true", OutputDiagnosticCode.ConflictingActions)]
    [InlineData("shell=a.exe bash=b.exe", OutputDiagnosticCode.DuplicateParameter)]
    [InlineData("shell=a.exe shell=a.exe", OutputDiagnosticCode.DuplicateParameter)]
    [InlineData("href=https://a.example href=https://b.example", OutputDiagnosticCode.DuplicateParameter)]
    [InlineData("refresh=true refresh=true", OutputDiagnosticCode.DuplicateParameter)]
    [InlineData("shell=a.exe terminal=false terminal=true", OutputDiagnosticCode.DuplicateParameter)]
    [InlineData("param1=x", OutputDiagnosticCode.OrphanedActionParameter)]
    [InlineData("refresh=true param1=x", OutputDiagnosticCode.OrphanedActionParameter)]
    [InlineData("href=https://example.com param1=x", OutputDiagnosticCode.OrphanedActionParameter)]
    [InlineData("terminal=true", OutputDiagnosticCode.OrphanedActionParameter)]
    [InlineData("refresh=true terminal=false", OutputDiagnosticCode.OrphanedActionParameter)]
    [InlineData("refresh=yes", OutputDiagnosticCode.InvalidParameterValue)]
    [InlineData("href=https://example.com refresh=1", OutputDiagnosticCode.InvalidParameterValue)]
    [InlineData("shell=a.exe terminal=no", OutputDiagnosticCode.InvalidParameterValue)]
    [InlineData("shell=a.exe refresh=\"\"", OutputDiagnosticCode.InvalidParameterValue)]
    public void ConflictingOrInvalidActionParametersSuppressTheWholeAction(string parameters, OutputDiagnosticCode code)
    {
        MenuItem item = ParseItem("Item | " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        Assert.Contains(code, Codes(diagnostics));
        Assert.All(diagnostics, d => Assert.Equal(PluginDiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void ConflictIsReportedEvenWhenBothActionsAreValid()
    {
        ParseItem("Item | href=https://example.com shell=taskmgr.exe", out ImmutableArray<OutputDiagnostic> diagnostics);

        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(OutputDiagnosticCode.ConflictingActions, diagnostic.Code);
        Assert.Equal(33, diagnostic.Column);
    }

    [Fact]
    public void OverlongActionValuesSuppressTheAction()
    {
        string tooLong = new('x', PluginOutputParser.MaxValueLength + 1);

        Assert.Null(ParseItem($"Run | shell={tooLong}").Action);
        Assert.Null(ParseItem($"Run | shell=t.exe param1={tooLong}").Action);
        Assert.Null(ParseItem($"Run | href=https://example.com/{tooLong}").Action);
        Assert.Null(ParseItem($"Run | refresh=true terminal={tooLong}").Action);
    }

    [Theory]
    [InlineData("disabled=true href=https://example.com", new[] { OutputDiagnosticCode.DisabledItemAction })]
    [InlineData("shell=x.exe disabled=TRUE refresh=true", new[] { OutputDiagnosticCode.DisabledItemAction })]
    [InlineData("refresh=true disabled=maybe", new[] { OutputDiagnosticCode.InvalidParameterValue, OutputDiagnosticCode.DisabledItemAction })]
    [InlineData("refresh=true disabled=false disabled=false", new[] { OutputDiagnosticCode.DuplicateParameter, OutputDiagnosticCode.DisabledItemAction })]
    public void DisabledItemsNeverExposeAnAction(string parameters, OutputDiagnosticCode[] codes)
    {
        MenuItem item = ParseItem("Item | " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.True(item.IsDisabled);
        Assert.Null(item.Action);
        Assert.Equal(codes, Codes(diagnostics));
    }

    [Fact]
    public void DisabledFalseKeepsTheAction() =>
        Assert.IsType<UrlAction>(ParseCleanItem("Item | disabled=false href=https://example.com").Action);

    [Theory]
    [InlineData("alternate=true")]
    [InlineData("key=CmdOrCtrl+o")]
    [InlineData("shortcut=CMD+OPTION+T")]
    [InlineData("webview=true")]
    [InlineData("webvieww=400")]
    [InlineData("webviewh=300")]
    [InlineData("stdin=hello")]
    public void UnsupportedBehaviouralParametersSuppressTheAction(string parameter)
    {
        foreach (string action in new[] { "href=https://example.com", "shell=taskmgr.exe", "refresh=true" })
        {
            MenuItem item = ParseItem($"Item | {action} {parameter}", out ImmutableArray<OutputDiagnostic> diagnostics);

            Assert.Null(item.Action);
            OutputDiagnostic diagnostic = Assert.Single(diagnostics);
            Assert.Equal(OutputDiagnosticCode.UnsupportedBehavior, diagnostic.Code);
            Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
        }
    }

    [Fact]
    public void UnsupportedBehaviouralParameterWithoutActionIsAWarning()
    {
        MenuItem item = ParseItem("Item | alternate=true color=red", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.NotNull(item.Format.Color);
        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(OutputDiagnosticCode.UnsupportedBehavior, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Theory]
    [InlineData("Item | refresh=true colour=red")]
    [InlineData("Item | shell=t.exe runas=admin")]
    [InlineData("Item | href=https://example.com target=_blank")]
    public void UnknownParametersSuppressTheAction(string line)
    {
        MenuItem item = ParseItem(line, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(OutputDiagnosticCode.UnknownParameter, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("Run | shell=taskmgr.exe param1=a param2=\"b")]
    [InlineData("Run | shell=taskmgr.exe | 2=x")]
    [InlineData("Run | shell=taskmgr.exe param1 =a")]
    [InlineData("Run | shell=\"C:\\Program Files\\x.exe")]
    [InlineData("Run | href=https://example.com refresh=true junk")]
    public void MalformedSyntaxNeverYieldsAPartialAction(string line)
    {
        MenuItem item = ParseItem(line, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Action);
        Assert.Equal([OutputDiagnosticCode.MalformedParameters], Codes(diagnostics));
    }

    // Percent-encoded control characters are valid URI syntax. They are neither decoded nor rejected.
    [Theory]
    [InlineData("https://example.com/%00")]
    [InlineData("https://example.com/?q=%0A")]
    [InlineData("https://example.com/#%1F")]
    [InlineData("https://example.com/%7F")]
    public void PercentEncodedControlCharactersStayEscaped(string value)
    {
        UrlAction action = Assert.IsType<UrlAction>(ParseCleanItem("Open | href=" + value).Action);

        Assert.Equal(value, action.Url.OriginalString);
        Assert.Equal(value, action.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Title")]
    [InlineData("---\nRun | shell=editor.exe param1=doc")]
    [InlineData("---\nRun | shell=edit")]
    [InlineData("---\nRun | shell=editor.exe param1=document.txt\n")]
    [InlineData("T | refresh=true\n---\nOpen | href=https://example.com/a\nRun | shell=a.exe\n")]
    public void TruncatedOutputFailsWithoutOutput(string text)
    {
        PluginOutputParseResult result = Parse(text, isTruncated: true);

        AssertFailed(result, OutputDiagnosticCode.OutputTruncated, line: null);
        Assert.Single(result.Diagnostics);
        Assert.Equal(RenderAll(Parse(text)), RenderAll(Parse(text, isTruncated: false)));
    }

    [Fact]
    public void CutUnquotedValuesAreOnlyRejectedWhenTruncationIsReported()
    {
        // Why the caller must report truncation: a cut unquoted value is still a valid, different value.
        CommandAction cut = Assert.IsType<CommandAction>(ParseCleanItem("Run | shell=editor.exe param1=doc").Action);
        Assert.Equal(["doc"], cut.Arguments);

        const string Line = "---\nRun | shell=editor.exe param1=document.txt";
        for (int length = 0; length <= Line.Length; length++)
        {
            AssertFailed(Parse(Line[..length], isTruncated: true), OutputDiagnosticCode.OutputTruncated, line: null);
        }
    }

    // Proves only that prefixes of a line whose values are all quoted never yield a partial quoted value.
    // It says nothing about unquoted values; see CutUnquotedValuesAreOnlyRejectedWhenTruncationIsReported.
    [Fact]
    public void PrefixesOfAFullyQuotedCommandNeverYieldPartialQuotedValues()
    {
        const string Line = "Run | shell=\"C:\\Program Files\\x.exe\" param1=\"a b\" param2='c' refresh=true";
        string[] fullArguments = ["a b", "c"];

        for (int length = 0; length <= Line.Length; length++)
        {
            PluginOutputParseResult result = Parse("---\n" + Line[..length]);
            if (result.Output.Items.IsEmpty || ((MenuItem)result.Output.Items[0]).Action is not CommandAction action)
            {
                continue;
            }

            // Any action that survives a cut is made of complete values only.
            Assert.Equal("C:\\Program Files\\x.exe", action.Executable);
            Assert.Equal(fullArguments.Take(action.Arguments.Length), action.Arguments);
            Assert.True(action.RefreshesPlugin == (length == Line.Length), $"cut at {length}");
        }
    }
}
