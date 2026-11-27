using System.Collections.Immutable;
using System.Globalization;
using TrayWisp.Core.Output;
using Xunit;
using static TrayWisp.Core.Tests.Output.OutputTestHelpers;

namespace TrayWisp.Core.Tests.Output;

// Line grammar: label, parameter separators, quoting, escaping and name rules.
public sealed class OutputParameterTests
{
    [Theory]
    [InlineData("Item | color=green size=12 refresh=true")]
    [InlineData("Item | color=green | size=12 | refresh=true")]
    [InlineData("Item |color=green|size=12|refresh=true")]
    [InlineData("Item | color=green\tsize=12 || refresh=true |  ")]
    [InlineData("Item|color=green size=12|refresh=true")]
    public void SpaceAndPipeSeparatedParametersAreEquivalent(string line)
    {
        MenuItem item = ParseCleanItem(line);

        Assert.Equal("Item", item.ToDisplayText());
        Assert.Equal(new OutputColor(0, 128, 0), item.Format.Color!.Light);
        Assert.Equal(12, item.Format.Size);
        Assert.IsType<RefreshAction>(item.Action);
    }

    [Theory]
    [InlineData("Item |")]
    [InlineData("Item | ")]
    [InlineData("Item ||| \t |")]
    public void EmptyParameterSectionIsAllowed(string line)
    {
        MenuItem item = ParseCleanItem(line);

        Assert.Same(TextFormat.Default, item.Format);
        Assert.Null(item.Action);
        Assert.Equal("Item", item.ToDisplayText());
    }

    [Fact]
    public void LabelKeepsItsWhiteSpaceUntilDisplay()
    {
        MenuItem item = ParseCleanItem("  Item  | trim=false");

        Assert.Equal("  Item  ", item.Text);
        Assert.Equal("  Item  ", item.ToDisplayText());
        Assert.Equal("Item", ParseCleanItem("  Item  | color=red").ToDisplayText());
    }

    [Fact]
    public void LabelsMayContainEqualsSignsAndQuotes()
    {
        MenuItem item = ParseCleanItem("a=b \"quoted\" 'single' it's");

        Assert.Equal("a=b \"quoted\" 'single' it's", item.Text);
        Assert.Same(TextFormat.Default, item.Format);
    }

    [Theory]
    [InlineData("A \\| B | color=red", "A | B ", true)]
    [InlineData("A \\| B", "A | B", false)]
    [InlineData("\\|\\|", "||", false)]
    [InlineData("C:\\dir\\file | color=red", "C:\\dir\\file ", true)]
    [InlineData("\\\\server\\share", "\\\\server\\share", false)]
    [InlineData("Path C:\\ | color=red", "Path C:\\ ", true)]
    [InlineData("x\\\\| y", "x\\| y", false)]
    [InlineData("50\\% \\n done", "50\\% \\n done", false)]
    public void BackslashPipeIsTheOnlyLabelEscape(string line, string expectedText, bool hasColor)
    {
        MenuItem item = ParseCleanItem(line);

        Assert.Equal(expectedText, item.Text);
        Assert.Equal(hasColor, item.Format.Color is not null);
    }

    [Theory]
    [InlineData("tooltip=\"a b | c=d\"", "a b | c=d")]
    [InlineData("tooltip='a b | c=d'", "a b | c=d")]
    [InlineData("tooltip=\"say \"\"hi\"\"\"", "say \"hi\"")]
    [InlineData("tooltip='it''s'", "it's")]
    [InlineData("tooltip=\"it's\"", "it's")]
    [InlineData("tooltip='say \"hi\"'", "say \"hi\"")]
    [InlineData("tooltip=\"\"\"\"", "\"")]
    [InlineData("tooltip=\"ends with \\\"", "ends with \\")]
    [InlineData("tooltip='C:\\Program Files\\'", "C:\\Program Files\\")]
    [InlineData("tooltip=\"\\n\\t\\x41 $HOME %PATH% ~ *\"", "\\n\\t\\x41 $HOME %PATH% ~ *")]
    [InlineData("tooltip=a=b=c", "a=b=c")]
    [InlineData("tooltip=https://example.com/?q=1&r=2", "https://example.com/?q=1&r=2")]
    [InlineData("tooltip=\\\\server\\share\\", "\\\\server\\share\\")]
    public void QuotedAndBareValuesArePreservedLiterally(string parameters, string expected)
    {
        MenuItem item = ParseCleanItem("Item | " + parameters);

        Assert.Equal(expected, item.Format.Tooltip);
    }

    [Fact]
    public void QuotedValueMayBeFollowedByPipeOrWhiteSpace()
    {
        MenuItem item = ParseCleanItem("Item | tooltip=\"x\"|color=red\ttrim='false'");

        Assert.Equal("x", item.Format.Tooltip);
        Assert.NotNull(item.Format.Color);
        Assert.False(item.Format.Trim);
    }

    [Theory]
    [InlineData("Item | tooltip=\"abc refresh=true", 16)]
    [InlineData("Item | tooltip='abc\" refresh=true", 16)]
    [InlineData("Item | tooltip=\"a\"\"", 16)]
    [InlineData("Item | color= size=12", 14)]
    [InlineData("Item | color=", 14)]
    [InlineData("Item | color = red", 8)]
    [InlineData("Item | color =red", 8)]
    [InlineData("Item | refresh", 8)]
    [InlineData("Item | refresh=true extra", 21)]
    [InlineData("Item | tooltip=\"a\"b", 19)]
    [InlineData("Item | tooltip=\"a\"'b'", 19)]
    [InlineData("Item | font=O'Brien", 14)]
    [InlineData("Item | font=Brien'", 18)]
    [InlineData("Item | 1x=2", 8)]
    [InlineData("Item | =x", 8)]
    [InlineData("Item | -k=v", 8)]
    [InlineData("Item | \"color\"=red", 8)]
    [InlineData("Item | col\u00F6r=red", 8)]
    [InlineData("Item | color:red", 8)]
    public void MalformedParameterSyntaxIgnoresEveryParameterOnTheLine(string line, int column)
    {
        MenuItem item = ParseItem(line + " href=https://example.com", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Equal("Item ", item.Text);
        Assert.Same(TextFormat.Default, item.Format);
        Assert.Null(item.Action);
        Assert.False(item.IsDisabled);
        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(OutputDiagnosticCode.MalformedParameters, diagnostic.Code);
        Assert.Equal((2, column), (diagnostic.Line, diagnostic.Column));
    }

    [Fact]
    public void WhiteSpaceAroundEqualsHasItsOwnMessage()
    {
        ParseItem("Item | color = red", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Contains("white space is not allowed around '='", Assert.Single(diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmatchedQuoteCannotSwallowALaterParameter()
    {
        // Without the rule, the rest of the line would become the tooltip and refresh would be lost silently,
        // or (worse) "refresh=true" would be read from inside a value.
        MenuItem item = ParseItem("Item | tooltip=\"abc refresh=true", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Null(item.Format.Tooltip);
        Assert.Null(item.Action);
        Assert.Equal([OutputDiagnosticCode.MalformedParameters], Codes(diagnostics));
    }

    [Fact]
    public void ParameterNamesAreCaseInsensitive()
    {
        MenuItem item = ParseCleanItem("Item | COLOR=Red SiZe=12 Refresh=TRUE TRIM=False ToolTip=x");

        Assert.Equal(new OutputColor(255, 0, 0), item.Format.Color!.Light);
        Assert.Equal(12, item.Format.Size);
        Assert.False(item.Format.Trim);
        Assert.Equal("x", item.Format.Tooltip);
        Assert.IsType<RefreshAction>(item.Action);
    }

    [Theory]
    [InlineData("color=red color=blue", "color")]
    [InlineData("color=red COLOR=red", "color")]
    [InlineData("size=12 Size=12", "size")]
    [InlineData("tooltip=a tooltip=b", "tooltip")]
    [InlineData("trim=false trim=false", "trim")]
    public void DuplicateFormattingParametersAreIgnoredEvenWithEqualValues(string parameters, string name)
    {
        MenuItem item = ParseItem("Item | " + parameters, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Same(TextFormat.Default, item.Format);
        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(OutputDiagnosticCode.DuplicateParameter, diagnostic.Code);
        Assert.Contains($"'{name}'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1 + "Item | ".Length + parameters.IndexOf(' ', StringComparison.Ordinal) + 1, diagnostic.Column);
    }

    [Fact]
    public void UnknownParametersWithoutAnActionAreIgnored()
    {
        MenuItem item = ParseItem("Item | colour=red color=blue x.y_z-1=2", out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Equal(new OutputColor(0, 0, 255), item.Format.Color!.Light);
        Assert.Equal([OutputDiagnosticCode.UnknownParameter, OutputDiagnosticCode.UnknownParameter], Codes(diagnostics));
        Assert.Equal([8, 30], diagnostics.Select(d => d.Column));
        Assert.Contains("'colour'", diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnsAccountForTheNestingPrefix()
    {
        PluginOutputParseResult result = Parse("---\nParent\n--Item | foo=1");

        OutputDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal((3, 10), (diagnostic.Line, diagnostic.Column));
    }

    [Fact]
    public void ParameterNameLengthIsLimited()
    {
        string longest = "k" + new string('x', 63);
        ParseItem($"Item | {longest}=1", out ImmutableArray<OutputDiagnostic> accepted);
        Assert.Equal([OutputDiagnosticCode.UnknownParameter], Codes(accepted));

        ParseItem($"Item | {longest}x=1", out ImmutableArray<OutputDiagnostic> rejected);
        Assert.Equal([OutputDiagnosticCode.MalformedParameters], Codes(rejected));
    }

    [Fact]
    public void ParsingDoesNotDependOnTheCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo originalUi = CultureInfo.CurrentUICulture;
        string text = Lines("T", "---", "Item | SIZE=12.5 COLOR=White DISABLED=TRUE LENGTH=10 TITLE=x", "Link | HREF=https://example.com REFRESH=TRUE");
        string expected = RenderAll(Parse(text));
        try
        {
            foreach (string name in new[] { "tr-TR", "de-DE", "fr-FR", "ar-SA", "fa-IR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(expected, RenderAll(Parse(text)));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }

        MenuItem item = (MenuItem)Parse(text).Output.Items[0];
        Assert.Equal(12.5, item.Format.Size);
        Assert.True(item.IsDisabled);
        Assert.Equal(new OutputColor(255, 255, 255), item.Format.Color!.Light);
    }
}
