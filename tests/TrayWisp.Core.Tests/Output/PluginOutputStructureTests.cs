using TrayWisp.Core.Output;
using Xunit;
using static TrayWisp.Core.Tests.Output.OutputTestHelpers;

namespace TrayWisp.Core.Tests.Output;

public sealed class PluginOutputStructureTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n\r\n")]
    [InlineData("  \t \n\n   ")]
    public void EmptyOrBlankOutputIsAValidEmptyResult(string text)
    {
        PluginOutputParseResult result = Parse(text);

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Empty(result.Output.Titles);
        Assert.Empty(result.Output.Items);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SingleLineIsOneTitle()
    {
        PluginOutput output = ParseComplete("12:45");

        OutputTitle title = Assert.Single(output.Titles);
        Assert.Equal("12:45", title.Item.Text);
        Assert.True(title.ShowInDropdown);
        Assert.Null(title.Item.Action);
        Assert.Empty(output.Items);
    }

    [Fact]
    public void WithoutSeparatorEveryNonBlankLineIsATitleInOrder()
    {
        PluginOutput output = ParseComplete(Lines("CPU 12%", "", "RAM 40%", "   ", "Disk 70%"));

        Assert.Equal(Lines("title 'CPU 12%'", "title 'RAM 40%'", "title 'Disk 70%'"), Render(output));
        Assert.Equal([1, 3, 5], output.Titles.Select(t => t.Item.Line));
    }

    [Fact]
    public void FirstSeparatorEndsTheHeader()
    {
        PluginOutput output = ParseComplete(Lines("Title", "Second title", "---", "Item", "Other item"));

        Assert.Equal(Lines("title 'Title'", "title 'Second title'", "item 'Item'", "item 'Other item'"), Render(output));
    }

    [Fact]
    public void OutputMayStartWithTheSeparatorAndHaveNoTitles()
    {
        PluginOutput output = ParseComplete(Lines("---", "Only a menu"));

        Assert.Empty(output.Titles);
        Assert.Equal(Lines("item 'Only a menu'"), Render(output));
    }

    [Fact]
    public void LaterSeparatorsAreKeptAsWrittenIncludingLeadingConsecutiveAndTrailingOnes()
    {
        PluginOutput output = ParseComplete(Lines("T", "---", "---", "A", "---", "---", "B", "---"));

        Assert.Equal(Lines("title 'T'", "separator", "item 'A'", "separator", "separator", "item 'B'", "separator"), Render(output));
    }

    [Fact]
    public void HeaderLinesAreNeverNestedOrTreatedAsSubmenuSeparators()
    {
        PluginOutput output = ParseComplete(Lines("--:--", "----", "-----", "-", "---", "x"));

        Assert.Equal(Lines("title '--:--'", "title '----'", "title '-----'", "title '-'", "item 'x'"), Render(output));
    }

    [Fact]
    public void LfAndCrLfProduceTheSameResult()
    {
        string lf = "Title\n---\nA | color=red\n--B\n-----\n--C\n";
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        string mixed = "Title\r\n---\nA | color=red\r\n--B\n-----\r\n--C";

        string expected = RenderAll(Parse(lf));
        Assert.Equal(expected, RenderAll(Parse(crlf)));
        Assert.Equal(expected, RenderAll(Parse(mixed)));
        Assert.Empty(Parse(crlf).Diagnostics);
    }

    [Fact]
    public void LoneCarriageReturnIsContentNotALineBreak()
    {
        PluginOutputParseResult result = Parse("A\rB\n---\nC");

        OutputTitle title = Assert.Single(result.Output.Titles);
        Assert.Equal("A\uFFFDB", title.Item.Text);
        OutputDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(OutputDiagnosticCode.UnsafeText, diagnostic.Code);
        Assert.Equal((1, 2), (diagnostic.Line, diagnostic.Column));
        Assert.Single(result.Output.Items);
    }

    [Fact]
    public void CarriageReturnBeforeCrLfIsALoneCarriageReturn()
    {
        PluginOutputParseResult result = Parse("Title\r\r\n---\r\nItem\r\n");

        Assert.Equal("Title\uFFFD", Assert.Single(result.Output.Titles).Item.Text);
        Assert.Equal([OutputDiagnosticCode.UnsafeText], Codes(result));
    }

    [Fact]
    public void TrailingLoneCarriageReturnsAreWhiteSpaceForBlankLinesAndSeparators()
    {
        // "\r" alone is a blank line, and trailing white space (including CR) does not stop a separator.
        PluginOutputParseResult result = Parse("Title\n\r\r\n---\r\r\nItem\n---\r");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(Lines("title 'Title'", "item 'Item'", "separator"), Render(result.Output));
    }

    [Fact]
    public void LeadingByteOrderMarkIsSkipped()
    {
        Assert.Equal(Lines("title 'Clock'", "item 'A'"), Render(ParseComplete("\uFEFFClock\n---\nA")));

        // The separator is still recognized right after the BOM.
        PluginOutput output = ParseComplete("\uFEFF---\nA");
        Assert.Empty(output.Titles);
        Assert.Equal(Lines("item 'A'"), Render(output));
    }

    [Fact]
    public void OnlyTheLeadingByteOrderMarkIsSkipped()
    {
        PluginOutput output = ParseComplete("A\n\uFEFF---");

        Assert.Equal(["A", "\uFEFF---"], output.Titles.Select(t => t.Item.Text));
    }

    [Fact]
    public void LineNumbersCountBlankLines()
    {
        PluginOutput output = ParseComplete("\n\nTitle\n\n---\n\nItem\n--Child");

        Assert.Equal(3, output.Titles[0].Item.Line);
        MenuItem item = Assert.IsType<MenuItem>(Assert.Single(output.Items));
        Assert.Equal(7, item.Line);
        Assert.Equal(8, Assert.Single(item.Children).Line);
    }

    [Fact]
    public void NestedSubmenusUseTwoHyphensPerLevelAndKeepOrder()
    {
        PluginOutput output = ParseComplete(Lines(
            "---",
            "Fruits",
            "--Apples",
            "----Gala",
            "----Fuji",
            "--Pears",
            "Vegetables",
            "--Carrots"));

        Assert.Equal(
            Lines("item 'Fruits'", "  item 'Apples'", "    item 'Gala'", "    item 'Fuji'", "  item 'Pears'", "item 'Vegetables'", "  item 'Carrots'"),
            Render(output));
    }

    [Fact]
    public void BlankLinesDoNotInterruptNesting()
    {
        PluginOutput output = ParseComplete(Lines("---", "A", "", "   ", "--B"));

        Assert.Equal(Lines("item 'A'", "  item 'B'"), Render(output));
    }

    [Fact]
    public void NestedSeparatorsUseTwoMoreHyphensPerLevel()
    {
        PluginOutput output = ParseComplete(Lines(
            "---",
            "A",
            "--B",
            "-----",
            "--C",
            "----D",
            "-------",
            "----E",
            "---"));

        Assert.Equal(
            Lines("item 'A'", "  item 'B'", "  separator", "  item 'C'", "    item 'D'", "    separator", "    item 'E'", "separator"),
            Render(output));
    }

    [Fact]
    public void SeparatorsMayHaveTrailingButNotLeadingWhiteSpace()
    {
        PluginOutput output = ParseComplete("---\nA\n--- \t\n ---");

        Assert.Equal(Lines("item 'A'", "separator", "item ' ---'"), Render(output));
    }

    [Fact]
    public void LeadingWhiteSpacePreventsNesting()
    {
        PluginOutput output = ParseComplete(Lines("---", "A", "  --B"));

        Assert.Equal(Lines("item 'A'", "item '  --B'"), Render(output));
        Assert.Equal("--B", ((MenuItem)output.Items[1]).ToDisplayText());
    }

    [Fact]
    public void OddHyphenPrefixKeepsTheExtraHyphenInTheLabel()
    {
        PluginOutput output = ParseComplete(Lines("---", "A", "---5 °C", "-5 °C", "-"));

        Assert.Equal(Lines("item 'A'", "  item '-5 °C'", "item '-5 °C'", "item '-'"), Render(output));
    }

    [Theory]
    [InlineData("--")]
    [InlineData("----")]
    [InlineData("------")]
    public void EvenHyphenRunsAreMalformedSeparatorsAndDropped(string line)
    {
        PluginOutputParseResult result = Parse(Lines("---", "A", line, "--B"));

        Assert.Equal([OutputDiagnosticCode.MalformedSeparator], Codes(result));
        Assert.Equal(3, result.Diagnostics[0].Line);
        Assert.Equal(Lines("item 'A'", "  item 'B'"), Render(result.Output));
    }

    [Fact]
    public void ChildWithoutParentIsDropped()
    {
        PluginOutputParseResult result = Parse(Lines("---", "--Orphan", "Top"));

        Assert.Equal(Lines("item 'Top'"), Render(result.Output));
        OutputDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(OutputDiagnosticCode.OrphanedMenuNode, diagnostic.Code);
        Assert.Equal(2, diagnostic.Line);
    }

    [Fact]
    public void DepthJumpDropsTheNodeAndItsChildrenWithoutInventingParents()
    {
        PluginOutputParseResult result = Parse(Lines("---", "A", "----Grandchild", "------Great-grandchild", "--B"));

        Assert.Equal(Lines("item 'A'", "  item 'B'"), Render(result.Output));
        Assert.Equal([OutputDiagnosticCode.OrphanedMenuNode, OutputDiagnosticCode.OrphanedMenuNode], Codes(result));
        Assert.Equal([3, 4], result.Diagnostics.Select(d => d.Line));
    }

    [Fact]
    public void ChildrenOfADroppedItemDoNotAttachToItsPreviousSibling()
    {
        // B is open at depth 1 when the empty-label item at depth 1 is dropped. Its child must not move to B.
        PluginOutputParseResult result = Parse(Lines("---", "A", "--B", "-- | href=https://example.com", "----Child", "--C"));

        Assert.Equal(Lines("item 'A'", "  item 'B'", "  item 'C'"), Render(result.Output));
        Assert.Equal([OutputDiagnosticCode.EmptyLabel, OutputDiagnosticCode.OrphanedMenuNode], Codes(result));
    }

    [Fact]
    public void ChildrenOfAnOrphanAreDroppedToo()
    {
        PluginOutputParseResult result = Parse(Lines("A", "---", "--X", "----Y", "-------", "Z"));

        Assert.Equal(Lines("title 'A'", "item 'Z'"), Render(result.Output));
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == OutputDiagnosticCode.OrphanedMenuNode));
    }

    [Fact]
    public void SeparatorsCannotBeParents()
    {
        PluginOutputParseResult top = Parse(Lines("---", "---", "--Child"));
        Assert.Equal(Lines("separator"), Render(top.Output));
        Assert.Equal([OutputDiagnosticCode.OrphanedMenuNode], Codes(top));

        PluginOutputParseResult nested = Parse(Lines("---", "A", "-----", "----Child", "--B"));
        Assert.Equal(Lines("item 'A'", "  separator", "  item 'B'"), Render(nested.Output));
        Assert.Equal([OutputDiagnosticCode.OrphanedMenuNode], Codes(nested));
    }

    [Fact]
    public void NestedSeparatorWithoutParentIsDropped()
    {
        PluginOutputParseResult result = Parse(Lines("---", "-----", "A"));

        Assert.Equal(Lines("item 'A'"), Render(result.Output));
        Assert.Equal([OutputDiagnosticCode.OrphanedMenuNode], Codes(result));
    }

    [Fact]
    public void ParentWithChildrenLosesItsActionButKeepsChildren()
    {
        PluginOutputParseResult result = Parse(Lines("---", "Open | href=https://example.com refresh=true", "--One", "--Two", "Plain | refresh=true"));

        Assert.Equal(Lines("item 'Open '", "  item 'One'", "  item 'Two'", "item 'Plain ' refresh"), Render(result.Output));
        OutputDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(OutputDiagnosticCode.ParentActionRemoved, diagnostic.Code);
        Assert.Equal(2, diagnostic.Line);
    }

    [Fact]
    public void NestedSeparatorAloneAlsoMakesASubmenu()
    {
        PluginOutputParseResult result = Parse(Lines("---", "Run | shell=taskmgr.exe", "-----"));

        MenuItem item = Assert.IsType<MenuItem>(Assert.Single(result.Output.Items));
        Assert.Null(item.Action);
        Assert.IsType<MenuSeparator>(Assert.Single(item.Children));
        Assert.Equal([OutputDiagnosticCode.ParentActionRemoved], Codes(result));
    }

    [Fact]
    public void EmptyLabelsAreDroppedWithTheirParameters()
    {
        PluginOutputParseResult result = Parse(Lines(" | refresh=true", "Title", "---", "| href=https://example.com", "\t", "  | color=red", "Item"));

        Assert.Equal(Lines("title 'Title'", "item 'Item'"), Render(result.Output));
        Assert.Equal([OutputDiagnosticCode.EmptyLabel, OutputDiagnosticCode.EmptyLabel, OutputDiagnosticCode.EmptyLabel], Codes(result));
        Assert.Equal([1, 4, 6], result.Diagnostics.Select(d => d.Line));
    }

    [Fact]
    public void LabelThatBecomesEmptyAfterRemovingDirectionalControlsIsDropped()
    {
        PluginOutputParseResult result = Parse("---\n\u202E\u2066 | refresh=true");

        Assert.Empty(result.Output.Items);
        Assert.Equal([OutputDiagnosticCode.EmptyLabel], Codes(result));
    }

    [Theory]
    [InlineData("\u200B")]
    [InlineData("\u200B\u200C\u200D \u2060")]
    [InlineData("\u200E\u200F\u061C")]
    [InlineData("\u00AD")]
    [InlineData("\uFEFF")]
    [InlineData("\U000E0041\U000E007F")]
    [InlineData("\u2800")]
    [InlineData("\u2800\u2800 \u2800")]
    [InlineData("\u115F")]
    [InlineData("\u1160")]
    [InlineData("\u3164")]
    [InlineData("\uFFA0")]
    [InlineData("\uFE0F")]
    [InlineData("\uFE00\uFE0E")]
    [InlineData("\U000E0100")]
    [InlineData("\U000E01EF")]
    [InlineData("\u180B\u180D")]
    [InlineData("\u034F")]
    [InlineData("\u3164\u200B \u2800\uFE0F\u00A0")]
    public void InvisibleLabelsAreDroppedLikeEmptyOnes(string label)
    {
        // Regression: a label of format characters only used to produce an invisible clickable item.
        PluginOutputParseResult result = Parse($"Title\n{label} | refresh=true\n---\n{label} | href=https://example.com\nVisible");

        Assert.Equal(Lines("title 'Title'", "item 'Visible'"), Render(result.Output));
        Assert.Equal([OutputDiagnosticCode.EmptyLabel, OutputDiagnosticCode.EmptyLabel], Codes(result));
    }

    [Fact]
    public void FormatCharactersInsideVisibleTextAreKept() =>
        Assert.Equal("a\u200Bb ", ParseCleanItem("a\u200Bb | refresh=true").Text);

    // Characters that only count as invisible when a whole label consists of them are kept in visible text,
    // and visible text in other scripts, with combining marks, emoji sequences or surrogate pairs is accepted.
    [Theory]
    [InlineData("a\u2800b")]
    [InlineData("\u2801")]
    [InlineData("\u3164x")]
    [InlineData("\u115F\u1161")]
    [InlineData("\uD55C\uAD6D\uC5B4")]
    [InlineData("\u2764\uFE0F")]
    [InlineData("\u845B\U000E0100")]
    [InlineData("e\u0301")]
    [InlineData("\u0915\u094D\u0937")]
    [InlineData("\u0627\u0644\u0639\u0631\u0628\u064A\u0629")]
    [InlineData("\U0001F680")]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467")]
    public void MeaningfulUnicodeLabelsAreKeptUnchanged(string label)
    {
        MenuItem item = ParseCleanItem(label + " | refresh=true");

        Assert.Equal(label + " ", item.Text);
        Assert.IsType<RefreshAction>(item.Action);
    }

    [Theory]
    [InlineData("\u2800")]
    [InlineData("\u3164\uFE0F")]
    public void InvisibleTooltipIsAbsent(string tooltip) =>
        Assert.Null(ParseCleanItem("Item | tooltip=\"" + tooltip + "\"").Format.Tooltip);

    [Fact]
    public void StreamingDelimiterInsideASubmenuFailsTheWholeParse()
    {
        // Previously "~~~" only closed open submenus, and the frames on both sides were combined.
        PluginOutputParseResult result = Parse(Lines("---", "A", "--B", "~~~", "--Child", "C", "--D"));

        AssertFailed(result, OutputDiagnosticCode.StreamingNotSupported, line: 4);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void DropdownFalseHidesATitleFromTheDropdown()
    {
        PluginOutputParseResult result = Parse(Lines("Visible", "Hidden | dropdown=false", "Explicit | dropdown=TRUE", "---", "Item | dropdown=false"));

        Assert.Equal(Lines("title 'Visible'", "title 'Hidden ' [hidden]", "title 'Explicit '", "item 'Item '"), Render(result.Output));
        OutputDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(OutputDiagnosticCode.ParameterNotApplicable, diagnostic.Code);
        Assert.Equal(5, diagnostic.Line);
    }

    [Fact]
    public void InvalidDropdownValueKeepsTheTitleInTheDropdown()
    {
        PluginOutputParseResult result = Parse("T | dropdown=no");

        Assert.True(Assert.Single(result.Output.Titles).ShowInDropdown);
        Assert.Equal([OutputDiagnosticCode.InvalidParameterValue], Codes(result));
    }

    [Fact]
    public void TitlesCanCarryFormattingAndActions()
    {
        OutputTitle title = Assert.Single(ParseComplete("12:45 | color=#FF0000 href=https://example.com").Titles);

        Assert.Equal(new OutputColor(255, 0, 0), title.Item.Format.Color!.Light);
        Assert.IsType<UrlAction>(title.Item.Action);
        Assert.Empty(title.Item.Children);
    }

    [Theory]
    [InlineData("Frame 1\n~~~\nFrame 2\n---\nA\n", 2)]
    [InlineData("Frame 1\n---\nA\n~~~ \n", 4)]
    [InlineData("Frame 1\n---\nA\n~~~\t\r\nB", 4)]
    [InlineData("\n\n~~~", 3)]
    [InlineData("Frame 1\n---\nA\n~~~", 4)]
    public void StreamingDelimiterFailsTheWholeParse(string text, int line)
    {
        PluginOutputParseResult result = Parse(text);

        AssertFailed(result, OutputDiagnosticCode.StreamingNotSupported, line);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void StreamingFramesNeverExposeActionsFromAnyFrame()
    {
        // Regression: stale actions of an earlier frame used to be combined with those of later frames.
        string text = Lines(
            "Frame 1 | refresh=true",
            "---",
            "Run A | shell=a.exe param1=old",
            "Open A | href=https://example.com/old",
            "~~~",
            "Frame 2",
            "---",
            "Run B | shell=b.exe param1=new refresh=true",
            "Open B | href=https://example.com/new");

        PluginOutputParseResult result = Parse(text);

        AssertFailed(result, OutputDiagnosticCode.StreamingNotSupported, line: 5);
        Assert.Single(result.Diagnostics);
    }

    [Theory]
    [InlineData("~~~~")]
    [InlineData("~~")]
    [InlineData(" ~~~")]
    [InlineData("~~~ x")]
    [InlineData("a ~~~")]
    [InlineData("~~~ | refresh=true")]
    [InlineData("~~~\\|")]
    public void LabelsContainingTildesAreNotStreamingDelimiters(string line)
    {
        PluginOutputParseResult result = Parse("---\n" + line);

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Single(result.Output.Items);
        Assert.DoesNotContain(OutputDiagnosticCode.StreamingNotSupported, Codes(result));
    }

    [Fact]
    public void OversizedStreamingDelimiterIsReportedAsTooLong() =>
        AssertFailed(Parse("---\n" + "~~~".PadRight(PluginOutputParser.MaxLineLength + 1)), OutputDiagnosticCode.LineTooLong, line: 2);

    [Fact]
    public void JsonLookingOutputIsReportedAndParsedAsText()
    {
        PluginOutputParseResult result = Parse("\n  {\"text\": \"CPU\"}\n");

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Equal([OutputDiagnosticCode.JsonNotSupported], Codes(result));
        Assert.Equal("  {\"text\": \"CPU\"}", Assert.Single(result.Output.Titles).Item.Text);
    }

    [Fact]
    public void BraceOnALaterLineIsNotReportedAsJson()
    {
        PluginOutputParseResult result = Parse("Title\n{not json}");

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ModelCollectionsAreImmutable()
    {
        PluginOutput output = ParseComplete(Lines("T", "---", "A", "--B"));

        // ImmutableArray exposes no mutators; the interface views reject changes.
        Assert.Throws<NotSupportedException>(() => ((IList<MenuNode>)output.Items).Add(null!));
        Assert.Throws<NotSupportedException>(() => ((IList<OutputTitle>)output.Titles).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<MenuNode>)((MenuItem)output.Items[0]).Children).RemoveAt(0));
        Assert.All(
            typeof(MenuItem).GetProperties().Concat(typeof(PluginOutput).GetProperties()).Concat(typeof(CommandAction).GetProperties()),
            property => Assert.False(property.SetMethod?.IsPublic ?? false, property.Name));
    }
}
