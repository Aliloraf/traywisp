using System.Collections.Immutable;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using Xunit;
using static TrayWisp.Core.Tests.Output.OutputTestHelpers;

namespace TrayWisp.Core.Tests.Output;

public sealed class OutputFormattingTests
{
    [Fact]
    public void DefaultsWhenNothingIsSpecified()
    {
        MenuItem item = ParseCleanItem("Item");

        Assert.Same(TextFormat.Default, item.Format);
        Assert.True(item.Format.Trim);
        Assert.Null(item.Format.Color);
        Assert.Null(item.Format.Font);
        Assert.Null(item.Format.Size);
        Assert.Null(item.Format.Length);
        Assert.Null(item.Format.Tooltip);
        Assert.False(item.IsDisabled);
        Assert.False(item.IsChecked);
        Assert.Null(item.Action);
        Assert.Empty(item.Children);
    }

    [Theory]
    [InlineData("red", 0xFF0000)]
    [InlineData("RED", 0xFF0000)]
    [InlineData("Green", 0x008000)]
    [InlineData("lime", 0x00FF00)]
    [InlineData("rebeccapurple", 0x663399)]
    [InlineData("lightgoldenrodyellow", 0xFAFAD2)]
    [InlineData("grey", 0x808080)]
    [InlineData("gray", 0x808080)]
    [InlineData("darkslategrey", 0x2F4F4F)]
    [InlineData("#f00", 0xFF0000)]
    [InlineData("#F80", 0xFF8800)]
    [InlineData("#ff8800", 0xFF8800)]
    [InlineData("#A1b2C3", 0xA1B2C3)]
    [InlineData("#000000", 0x000000)]
    public void AcceptsCssNamesAndHexColors(string value, int rgb)
    {
        MenuItem item = ParseCleanItem($"Item | color={value}");

        var expected = new OutputColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        Assert.Equal(new TextColor(expected, expected), item.Format.Color);
    }

    [Fact]
    public void AcceptsLightAndDarkColorPairs()
    {
        MenuItem item = ParseCleanItem("Item | color=black,#FFFFFF");

        Assert.Equal(new OutputColor(0, 0, 0), item.Format.Color!.Light);
        Assert.Equal(new OutputColor(255, 255, 255), item.Format.Color.Dark);
        Assert.Equal("#000000", item.Format.Color.Light.ToString());
    }

    [Theory]
    [InlineData("#ff00")]
    [InlineData("#ff000000")]
    [InlineData("#f00f")]
    [InlineData("#gg0000")]
    [InlineData("#")]
    [InlineData("ff0000")]
    [InlineData("transparent")]
    [InlineData("currentColor")]
    [InlineData("ButtonText")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("red,blue,green")]
    [InlineData("red,")]
    [InlineData(",red")]
    [InlineData("\"red, blue\"")]
    [InlineData("\" red\"")]
    [InlineData("\"\"")]
    [InlineData("ｒｅｄ")]
    [InlineData("#０００")]
    [InlineData("lightgoldenrodyellowx")]
    public void RejectsOtherColorSyntax(string value)
    {
        MenuItem item = ParseItem($"Item | color={value}", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Format.Color);
        Assert.Equal([OutputDiagnosticCode.InvalidParameterValue], Codes(diagnostics));
    }

    [Theory]
    [InlineData("Menlo", "Menlo")]
    [InlineData("\"Segoe UI\"", "Segoe UI")]
    [InlineData("'Cascadia Mono'", "Cascadia Mono")]
    [InlineData("微软雅黑", "微软雅黑")]
    public void FontNamesAreKeptAsWritten(string value, string expected) =>
        Assert.Equal(expected, ParseCleanItem($"Item | font={value}").Format.Font);

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"Segoe\tUI\"")]
    [InlineData("\"Segoe\u202EUI\"")]
    public void RejectsEmptyOrUnsafeFontNames(string value)
    {
        MenuItem item = ParseItem($"Item | font={value}", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Format.Font);
        Assert.Equal([OutputDiagnosticCode.InvalidParameterValue], Codes(diagnostics));
    }

    [Fact]
    public void FontNameLengthIsLimited()
    {
        Assert.Equal(128, ParseCleanItem("Item | font=" + new string('F', 128)).Format.Font!.Length);
        Assert.Null(ParseItem("Item | font=" + new string('F', 129)).Format.Font);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("12", 12)]
    [InlineData("012", 12)]
    [InlineData("12.5", 12.5)]
    [InlineData("9.75", 9.75)]
    [InlineData("256", 256)]
    [InlineData("255.99", 255.99)]
    public void AcceptsSizesInRange(string value, double expected) =>
        Assert.Equal(expected, ParseCleanItem($"Item | size={value}").Format.Size);

    [Theory]
    [InlineData("0")]
    [InlineData("0.99")]
    [InlineData("256.01")]
    [InlineData("257")]
    [InlineData("99999999999999999999999")]
    [InlineData("-1")]
    [InlineData("+12")]
    [InlineData("12,5")]
    [InlineData("1e2")]
    [InlineData(".5")]
    [InlineData("12.")]
    [InlineData("12.555")]
    [InlineData("1.2.3")]
    [InlineData("\" 12\"")]
    [InlineData("12pt")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("١٢")]
    public void RejectsInvalidSizes(string value)
    {
        MenuItem item = ParseItem($"Item | size={value}", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Format.Size);
        Assert.Equal([OutputDiagnosticCode.InvalidParameterValue], Codes(diagnostics));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("1024", 1024)]
    public void AcceptsLengthsInRange(string value, int expected) =>
        Assert.Equal(expected, ParseCleanItem($"Item | length={value}").Format.Length);

    [Theory]
    [InlineData("0")]
    [InlineData("1025")]
    [InlineData("99999999999999999999999999999999")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("ten")]
    public void RejectsInvalidLengths(string value)
    {
        MenuItem item = ParseItem($"Item | length={value}", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Format.Length);
        Assert.Equal([OutputDiagnosticCode.InvalidParameterValue], Codes(diagnostics));
    }

    [Fact]
    public void LengthTruncatesForDisplayButKeepsTheFullLabel()
    {
        MenuItem item = ParseCleanItem("Repository notifications | length=10");

        Assert.Equal("Repository notifications ", item.Text);
        Assert.Equal("Repository\u2026", item.ToDisplayText());
    }

    [Theory]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcd", 3, "abc\u2026")]
    [InlineData("  abc  ", 3, "abc")]
    [InlineData("\U0001F680\U0001F680\U0001F680", 2, "\U0001F680\U0001F680\u2026")]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466 family", 1, "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466\u2026")]
    [InlineData("\U0001F1EB\U0001F1F7\U0001F1E9\U0001F1EA", 1, "\U0001F1EB\U0001F1F7\u2026")]
    [InlineData("e\u0301e\u0301e\u0301", 2, "e\u0301e\u0301\u2026")]
    [InlineData("\U0001F44D\U0001F3FD ok", 1, "\U0001F44D\U0001F3FD\u2026")]
    [InlineData("한국어", 2, "한국\u2026")]
    public void TruncationNeverSplitsUserPerceivedCharacters(string label, int length, string expected)
    {
        MenuItem item = ParseCleanItem($"{label} | length={length}");

        Assert.Equal(expected, item.ToDisplayText());
    }

    [Fact]
    public void TrimFalseKeepsSurroundingWhiteSpaceForDisplay()
    {
        MenuItem item = ParseCleanItem("   indented | trim=false length=5");

        Assert.False(item.Format.Trim);
        Assert.Equal("   in\u2026", item.ToDisplayText());
    }

    [Theory]
    [InlineData("trim=yes")]
    [InlineData("trim=1")]
    [InlineData("trim=\"\"")]
    [InlineData("trim=\" true\"")]
    public void InvalidTrimDefaultsToTrue(string parameters)
    {
        MenuItem item = ParseItem("  Item  | " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.True(item.Format.Trim);
        Assert.Equal("Item", item.ToDisplayText());
        Assert.Equal([OutputDiagnosticCode.InvalidParameterValue], Codes(diagnostics));
    }

    [Fact]
    public void TooltipIsSanitizedLikeLabels()
    {
        MenuItem item = ParseItem("Item | tooltip=\"a\u001B[31mb\u202Ec\tdone\"", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Equal("a\uFFFD[31mbc done", item.Format.Tooltip);
        Assert.Equal([OutputDiagnosticCode.UnsafeText], Codes(diagnostics));
    }

    [Fact]
    public void EmptyTooltipIsAbsent() => Assert.Null(ParseCleanItem("Item | tooltip=\"\"").Format.Tooltip);

    [Theory]
    [InlineData("disabled=true", true)]
    [InlineData("disabled=TRUE", true)]
    [InlineData("disabled=false", false)]
    [InlineData("checked=true", false)]
    public void ParsesDisabled(string parameters, bool expected) =>
        Assert.Equal(expected, ParseCleanItem("Item | " + parameters).IsDisabled);

    [Theory]
    [InlineData("disabled=yes", OutputDiagnosticCode.InvalidParameterValue)]
    [InlineData("disabled=1", OutputDiagnosticCode.InvalidParameterValue)]
    [InlineData("disabled=\"\"", OutputDiagnosticCode.InvalidParameterValue)]
    [InlineData("disabled=false disabled=false", OutputDiagnosticCode.DuplicateParameter)]
    public void InvalidOrDuplicatedDisabledCountsAsDisabled(string parameters, OutputDiagnosticCode code)
    {
        MenuItem item = ParseItem("Item | " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.True(item.IsDisabled);
        Assert.Equal([code], Codes(diagnostics));
        Assert.Contains("treated as disabled", diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledValueThatIsTooLongCountsAsDisabled()
    {
        MenuItem item = ParseItem("Item | disabled=" + new string('x', PluginOutputParser.MaxValueLength + 1));

        Assert.True(item.IsDisabled);
    }

    [Theory]
    [InlineData("checked=true", true, 0)]
    [InlineData("checked=False", false, 0)]
    [InlineData("checked=on", false, 1)]
    public void ParsesChecked(string parameters, bool expected, int diagnosticCount)
    {
        MenuItem item = ParseItem("Item | " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Equal(expected, item.IsChecked);
        Assert.Equal(diagnosticCount, diagnostics.Length);
    }

    [Theory]
    [InlineData("image=iVBORw0KGgo=")]
    [InlineData("templateImage=iVBORw0KGgo=")]
    [InlineData("sfimage=sun.max")]
    [InlineData("sfcolor=red")]
    [InlineData("sfcolor2=red")]
    [InlineData("sfsize=12")]
    [InlineData("sfconfig=e30=")]
    [InlineData("ansi=true")]
    [InlineData("md=true")]
    [InlineData("emojize=false")]
    [InlineData("symbolize=false")]
    [InlineData("badge=3")]
    public void UnsupportedAppearanceParametersAreIgnoredAndKeepTheAction(string parameter)
    {
        MenuItem item = ParseItem($"Open | href=https://example.com {parameter}", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.IsType<UrlAction>(item.Action);
        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(OutputDiagnosticCode.UnsupportedParameter, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void AnsiSequencesAreNotRendered()
    {
        MenuItem item = ParseItem("\u001B[32mOK\u001B[0m | ansi=true", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Equal("\uFFFD[32mOK\uFFFD[0m ", item.Text);
        Assert.Equal([OutputDiagnosticCode.UnsafeText, OutputDiagnosticCode.UnsupportedParameter], Codes(diagnostics));
    }

    [Fact]
    public void FormattingIsSeparateFromTheAction()
    {
        MenuItem item = ParseCleanItem("Open | color=red,blue font=Menlo size=14 length=20 trim=false tooltip=Docs checked=true href=https://example.com");

        Assert.Equal(new TextColor(new OutputColor(255, 0, 0), new OutputColor(0, 0, 255)), item.Format.Color);
        Assert.Equal("Menlo", item.Format.Font);
        Assert.Equal(14, item.Format.Size);
        Assert.Equal(20, item.Format.Length);
        Assert.False(item.Format.Trim);
        Assert.Equal("Docs", item.Format.Tooltip);
        Assert.True(item.IsChecked);
        Assert.Equal("https://example.com", Assert.IsType<UrlAction>(item.Action).Url.OriginalString);
    }
}
