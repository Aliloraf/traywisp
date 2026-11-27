using System.Collections.Immutable;
using System.Text;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using Xunit;
using static TrayWisp.Core.Tests.Output.OutputTestHelpers;

namespace TrayWisp.Core.Tests.Output;

// Limits, adversarial text, diagnostics bounds, determinism and randomized malformed input.
public sealed class OutputRobustnessTests
{
    [Fact]
    public void InputAtTheLengthLimitIsAccepted()
    {
        // 16 lines of 16,383 characters plus a line feed each: exactly 262,144 characters.
        string text = string.Concat(Enumerable.Repeat(new string('a', PluginOutputParser.MaxLineLength - 1) + "\n", 16));
        Assert.Equal(PluginOutputParser.MaxInputLength, text.Length);

        PluginOutputParseResult result = Parse(text);

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Equal(16, result.Output.Titles.Length);
    }

    [Fact]
    public void InputAboveTheLengthLimitFailsWithoutOutput()
    {
        string text = "Title\n---\nItem | refresh=true\n" + new string(' ', PluginOutputParser.MaxInputLength);

        PluginOutputParseResult result = Parse(text);

        AssertFailed(result, OutputDiagnosticCode.OutputTooLong, line: null);
    }

    [Fact]
    public void ByteOrderMarkCountsTowardTheLengthLimit()
    {
        string body = string.Concat(Enumerable.Repeat(new string('a', PluginOutputParser.MaxLineLength - 1) + "\n", 16));

        Assert.Equal(OutputParseStatus.Complete, Parse("\uFEFF" + body[..^1]).Status);
        AssertFailed(Parse("\uFEFF" + body), OutputDiagnosticCode.OutputTooLong, line: null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LineCountIncludesBlankLinesAndIsLimited(bool finalLineFeed)
    {
        // "T", "---", then blank lines up to the limit; a final line feed does not start another line.
        string atLimit = "T\n---\n" + new string('\n', PluginOutputParser.MaxLines - 3) + "Item" + (finalLineFeed ? "\n" : string.Empty);
        PluginOutputParseResult accepted = Parse(atLimit);
        Assert.Equal(OutputParseStatus.Complete, accepted.Status);
        Assert.Equal(PluginOutputParser.MaxLines, Assert.Single(accepted.Output.Items).Line);

        AssertFailed(Parse("\n" + atLimit), OutputDiagnosticCode.TooManyLines, line: null);
    }

    [Fact]
    public void LinesAtTheLengthLimitAreAccepted()
    {
        int max = PluginOutputParser.MaxLineLength;
        string longest = new('x', max);
        Assert.Equal(longest, Assert.Single(ParseComplete(longest).Titles).Item.Text);

        // A separator padded with trailing white space and a blank line, both exactly at the limit, and a
        // CRLF terminator, which is not part of the line.
        string separator = "---".PadRight(max);
        string nested = "-----".PadRight(max);
        string blank = new(' ', max);
        PluginOutput output = ParseComplete("T\n" + separator + "\nA\n" + nested + "\r\n" + blank + "\n" + longest + "\r\n");

        Assert.Equal(Lines("title 'T'", "item 'A'", "  separator", "item '" + longest + "'"), Render(output));
    }

    // Each case puts one line, one character over the limit, at line 6 of a menu whose other lines are all
    // valid and interactive. Dropping it would have to guess what it was (separator or item, at which depth),
    // so the whole parse fails and none of the actions before or after it is exposed.
    [Theory]
    [InlineData("---", ' ')]
    [InlineData("-----", ' ')]
    [InlineData("-------", '\t')]
    [InlineData("", 'x')]
    [InlineData("--", 'x')]
    [InlineData("----Child | refresh=true ", 'x')]
    [InlineData("", ' ')]
    [InlineData("", '\t')]
    public void OversizedLineFailsTheWholeParse(string start, char padding)
    {
        string tooLong = start.PadRight(PluginOutputParser.MaxLineLength + 1, padding);
        string text = Lines("Title | refresh=true", "---", "Z | href=https://example.com", "A", "--B | shell=b.exe param1=x", tooLong, "--C | shell=c.exe", "D | refresh=true");

        PluginOutputParseResult result = Parse(text);

        AssertFailed(result, OutputDiagnosticCode.LineTooLong, line: 6);
        Assert.Single(result.Diagnostics);
    }

    [Theory]
    [InlineData("---", ' ')]
    [InlineData("---", '\t')]
    [InlineData("", ' ')]
    [InlineData("Title 2", 'x')]
    public void OversizedLineInTheHeaderFailsTheWholeParse(string start, char padding)
    {
        // An oversized first separator must not be dropped, which would leave the menu items in the header.
        string tooLong = start.PadRight(PluginOutputParser.MaxLineLength + 1, padding);

        PluginOutputParseResult result = Parse(Lines("Title | refresh=true", tooLong, "Item | href=https://example.com", "--Child | shell=c.exe"));

        AssertFailed(result, OutputDiagnosticCode.LineTooLong, line: 2);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void OversizedLineIsCountedWithoutItsTerminatorButWithALoneCarriageReturn()
    {
        string longest = new('x', PluginOutputParser.MaxLineLength);

        Assert.Equal(OutputParseStatus.Complete, Parse("---\n" + longest + "\r\nItem").Status);
        AssertFailed(Parse("---\n" + longest + "\rItem"), OutputDiagnosticCode.LineTooLong, line: 2);
        AssertFailed(Parse("---\n" + longest + "\r"), OutputDiagnosticCode.LineTooLong, line: 2);
    }

    [Fact]
    public void OversizedFirstLineFailsBeforeJsonDetection()
    {
        PluginOutputParseResult result = Parse("{".PadRight(PluginOutputParser.MaxLineLength + 1, ' ') + "\n");

        AssertFailed(result, OutputDiagnosticCode.LineTooLong, line: 1);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void TitleLimit()
    {
        string atLimit = string.Concat(Enumerable.Range(1, PluginOutputParser.MaxTitles).Select(i => $"Title {i}\n"));
        Assert.Equal(PluginOutputParser.MaxTitles, ParseComplete(atLimit).Titles.Length);

        AssertFailed(Parse(atLimit + "One too many | href=https://example.com\n---\nItem"), OutputDiagnosticCode.TooManyTitles, line: PluginOutputParser.MaxTitles + 1);
    }

    [Fact]
    public void MenuNodeLimitCountsItemsAndSeparatorsAtEveryLevelButNotTitles()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < PluginOutputParser.MaxTitles; i++)
        {
            builder.Append("T\n");
        }

        builder.Append("---\nParent\n");
        for (int i = 1; i < PluginOutputParser.MaxMenuNodes; i++)
        {
            builder.Append(i % 2 == 0 ? "-----\n" : "--Child | refresh=true\n");
        }

        PluginOutputParseResult accepted = Parse(builder.ToString());
        Assert.Equal(OutputParseStatus.Complete, accepted.Status);
        Assert.Equal(PluginOutputParser.MaxMenuNodes - 1, ((MenuItem)Assert.Single(accepted.Output.Items)).Children.Length);

        PluginOutputParseResult rejected = Parse(builder + "Extra | href=https://example.com\n");
        AssertFailed(rejected, OutputDiagnosticCode.TooManyMenuNodes, line: PluginOutputParser.MaxTitles + 1 + PluginOutputParser.MaxMenuNodes + 1);
    }

    [Fact]
    public void DroppedNodesDoNotCountTowardTheNodeLimit()
    {
        string text = "---\n" + string.Concat(Enumerable.Repeat("--orphan\n", 1000)) + string.Concat(Enumerable.Repeat("Item\n", PluginOutputParser.MaxMenuNodes));

        PluginOutputParseResult result = Parse(text);

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Equal(PluginOutputParser.MaxMenuNodes, result.Output.Items.Length);
    }

    [Fact]
    public void DepthLimitForItemsAndSeparators()
    {
        var builder = new StringBuilder("---\n");
        for (int depth = 0; depth <= PluginOutputParser.MaxDepth; depth++)
        {
            builder.Append(new string('-', depth * 2)).Append("Level ").Append(depth).Append('\n');
        }

        string deepest = builder.ToString();
        string separatorAtMaxDepth = new string('-', (PluginOutputParser.MaxDepth * 2) + 3) + "\n";
        string tooDeepItem = new string('-', (PluginOutputParser.MaxDepth + 1) * 2) + "Too deep\n";
        string tooDeepSeparator = new string('-', ((PluginOutputParser.MaxDepth + 1) * 2) + 3) + "\n";

        PluginOutputParseResult accepted = Parse(deepest + separatorAtMaxDepth);
        Assert.Empty(accepted.Diagnostics);
        MenuItem node = (MenuItem)accepted.Output.Items[0];
        for (int depth = 1; depth < PluginOutputParser.MaxDepth; depth++)
        {
            node = (MenuItem)Assert.Single(node.Children);
        }

        // The item at depth 7 holds the depth-8 item and the depth-8 separator.
        Assert.Equal(2, node.Children.Length);
        Assert.Equal("Level 8", ((MenuItem)node.Children[0]).Text);
        Assert.IsType<MenuSeparator>(node.Children[1]);

        PluginOutputParseResult rejected = Parse(deepest + tooDeepItem + tooDeepSeparator);
        Assert.Equal([OutputDiagnosticCode.MenuDepthExceeded, OutputDiagnosticCode.MenuDepthExceeded], Codes(rejected));
    }

    [Fact]
    public void ExcessiveNestingIsBoundedAndDoesNotRecurse()
    {
        var builder = new StringBuilder("---\n");
        for (int depth = 0; depth < 300; depth++)
        {
            builder.Append(new string('-', depth * 2)).Append("x\n");
        }

        builder.Append(new string('-', 15000)).Append('\n');
        builder.Append(new string('-', 15001)).Append("y\n");

        PluginOutputParseResult result = Parse(builder.ToString());

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Equal(PluginOutputParser.MaxDepth, MaxDepth(result.Output.Items));
        Assert.True(result.DiagnosticsTruncated);
    }

    [Fact]
    public void ParameterCountLimit()
    {
        string sixtyFour = string.Join(' ', Enumerable.Range(0, PluginOutputParser.MaxParametersPerLine).Select(i => $"k{i}=v"));
        ParseItem("Item | " + sixtyFour, out ImmutableArray<OutputDiagnostic> accepted);
        Assert.Equal(PluginOutputParser.MaxParametersPerLine, accepted.Length);
        Assert.All(accepted, d => Assert.Equal(OutputDiagnosticCode.UnknownParameter, d.Code));

        // A 65th parameter makes every parameter of the line unusable, including the command that came first.
        MenuItem item = ParseItem("Item | shell=t.exe color=red " + sixtyFour[(sixtyFour.IndexOf(' ', StringComparison.Ordinal) + 1)..], out ImmutableArray<OutputDiagnostic> tooMany);
        Assert.Equal([OutputDiagnosticCode.TooManyParameters], Codes(tooMany));
        Assert.Null(item.Action);
        Assert.Same(TextFormat.Default, item.Format);
    }

    [Fact]
    public void ValueLengthLimit()
    {
        string longest = new('v', PluginOutputParser.MaxValueLength);
        Assert.Equal(longest, ParseCleanItem($"Item | tooltip=\"{longest}\"").Format.Tooltip);
        Assert.Equal(longest, Assert.IsType<CommandAction>(ParseCleanItem($"Item | shell={longest}").Action).Executable);

        MenuItem item = ParseItem($"Item | tooltip=\"{longest}v\" color=red", out ImmutableArray<OutputDiagnostic> diagnostics);
        Assert.Null(item.Format.Tooltip);
        Assert.NotNull(item.Format.Color);
        Assert.Equal([OutputDiagnosticCode.InvalidParameterValue], Codes(diagnostics));
    }

    [Fact]
    public void LongMalformedQuotedValuesAreHandledInLinearTime()
    {
        // Thousands of doubled quotes, unterminated, on a line at the length limit.
        string line = "Item | tooltip=\"" + string.Concat(Enumerable.Repeat("\"\"a", (PluginOutputParser.MaxLineLength - 20) / 3));
        Assert.True(line.Length <= PluginOutputParser.MaxLineLength);

        PluginOutputParseResult result = Parse(string.Concat(Enumerable.Repeat("---\n" + line + "\n", 15)));

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.All(result.Diagnostics, d => Assert.Equal(OutputDiagnosticCode.MalformedParameters, d.Code));
    }

    [Theory]
    [InlineData("a\u0000b", "a\uFFFDb")]
    [InlineData("\u001B[31mred\u001B[0m", "\uFFFD[31mred\uFFFD[0m")]
    [InlineData("\u001B]0;title\u0007", "\uFFFD]0;title\uFFFD")]
    [InlineData("a\u007Fb", "a\uFFFDb")]
    [InlineData("a\u0085b\u009Bc", "a\uFFFDb\uFFFDc")]
    [InlineData("line\u2028sep\u2029para", "line\uFFFDsep\uFFFDpara")]
    [InlineData("a\u000Bb\u000Cc", "a\uFFFDb\uFFFDc")]
    [InlineData("evil\u202Etxt.exe", "eviltxt.exe")]
    [InlineData("\u2066isolated\u2069 \u202Aembedded\u202C", "isolated embedded")]
    public void UnsafeCharactersAreReplacedOrRemovedFromDisplayText(string label, string expected)
    {
        MenuItem item = ParseItem(label, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Equal(expected, item.Text);
        Assert.Equal([OutputDiagnosticCode.UnsafeText], Codes(diagnostics));
    }

    [Fact]
    public void InvalidUtf16IsReplaced()
    {
        string label = "a" + (char)0xD800 + "b" + (char)0xDC00 + "c" + (char)0xDC00 + (char)0xD800 + "\U0001F680";

        MenuItem item = ParseItem(label, out ImmutableArray<OutputDiagnostic> diagnostics);

        Assert.Equal("a\uFFFDb\uFFFDc\uFFFD\uFFFD\U0001F680", item.Text);
        OutputDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal((OutputDiagnosticCode.UnsafeText, 2), (diagnostic.Code, diagnostic.Column));
    }

    [Fact]
    public void LoneSurrogateAtTheEndOfTheInputIsReplaced()
    {
        MenuItem item = ParseItem("x" + (char)0xD83D);

        Assert.Equal("x\uFFFD", item.Text);
    }

    [Theory]
    [InlineData("M\u00E9t\u00E9o 21 \u00B0C")]
    [InlineData("\u65E5\u672C\u8A9E \u4E2D\u6587 \uD55C\uAD6D\uC5B4")]
    [InlineData("\u0627\u0644\u0639\u0631\u0628\u064A\u0629 \u05E2\u05D1\u05E8\u05D9\u05EA")]
    [InlineData("\u200F\u05E9\u05DC\u05D5\u05DD\u200E (LRM/RLM kept) \u061C")]
    [InlineData("\U0001F680 \U0001F468\u200D\U0001F469\u200D\U0001F467 \U0001F1EF\U0001F1F5 \U0001F44D\U0001F3FD \u2764\uFE0F")]
    [InlineData("e\u0301 a\u0308 \u0915\u094D\u0937")]
    [InlineData("zero\u200Bwidth\u200Cjoiner\u00ADsoft")]
    public void InternationalTextAndEmojiArePreserved(string label)
    {
        MenuItem item = ParseCleanItem(label + " | color=red");

        Assert.Equal(label + " ", item.Text);
        Assert.Equal(label, item.ToDisplayText());
    }

    [Fact]
    public void TabsBecomeSpacesSilently()
    {
        MenuItem item = ParseCleanItem("CPU\t45%");

        Assert.Equal("CPU 45%", item.Text);
    }

    [Fact]
    public void DisplayTextNeverContainsControlOrDirectionalCharacters()
    {
        string hostile = "\u001B[2J\u202E\u2067a\r\u0000\u0085\u2028" + (char)0xDBFF + "\u2066b\u200F";
        PluginOutputParseResult result = Parse($"{hostile} | tooltip=\"{hostile}\"\n---\n{hostile} | tooltip=\"{hostile}\"");

        foreach (MenuItem item in result.Output.Titles.Select(t => t.Item).Concat(result.Output.Items.Cast<MenuItem>()))
        {
            AssertDisplaySafe(item.Text);
            AssertDisplaySafe(item.ToDisplayText());
            AssertDisplaySafe(item.Format.Tooltip!);
        }
    }

    [Fact]
    public void DiagnosticsNeverRepeatLabelsOrValues()
    {
        const string Secret = "S3cr3t";
        string text = Lines(
            $"{Secret}\u0007 | tooltip=\"{Secret}\u0007\" href=https://{Secret}@example.com",
            "---",
            $"{Secret} | shell=\"{Secret}\u0007\" param1=\"{Secret}\u001B\" param3={Secret}",
            $"{Secret} | href=\"javascript:{Secret}\" refresh={Secret}",
            $"{Secret} | color={Secret} font=\"{Secret}\u0007\" size={Secret} length={Secret} trim={Secret} disabled={Secret}",
            $"{Secret} | tooltip=\"{Secret}",
            $"--{Secret} | refresh=true",
            new string('-', 30) + Secret);

        PluginOutputParseResult result = Parse(text);

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d => Assert.DoesNotContain(Secret, d.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UntrustedParameterNamesInDiagnosticsAreQuotedAndBounded()
    {
        string name = "k" + new string('x', 63);

        ParseItem($"Item | {name}=1", out ImmutableArray<OutputDiagnostic> diagnostics);

        string message = Assert.Single(diagnostics).Message;
        Assert.Contains("'k" + new string('x', 31) + "...'", message, StringComparison.Ordinal);
        Assert.DoesNotContain(name, message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticLimitKeepsASummaryAndDoesNotChangeParsing()
    {
        // 300 lines with an unknown parameter each, then an action line whose own diagnostic is suppressed.
        string text = "---\n" + string.Concat(Enumerable.Repeat("Item | foo=1\n", 300)) + "Run | shell=t.exe foo=1\nOk | refresh=true\n";

        PluginOutputParseResult result = Parse(text);

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.True(result.DiagnosticsTruncated);
        Assert.Equal(PluginOutputParser.MaxDiagnostics - 1, result.Diagnostics.Length);
        OutputDiagnostic summary = result.Diagnostics[^1];
        Assert.Equal(OutputDiagnosticCode.DiagnosticLimitReached, summary.Code);
        Assert.Contains("47 further diagnostics", summary.Message, StringComparison.Ordinal);

        MenuItem run = (MenuItem)result.Output.Items[^2];
        Assert.Equal("Run ", run.Text);
        Assert.Null(run.Action);
        Assert.IsType<RefreshAction>(((MenuItem)result.Output.Items[^1]).Action);
    }

    [Fact]
    public void FatalErrorIsKeptWhenTheDiagnosticLimitIsReached()
    {
        string text = "---\n" + string.Concat(Enumerable.Repeat("Item | foo=1\n", PluginOutputParser.MaxMenuNodes + 1));

        PluginOutputParseResult result = Parse(text);

        Assert.Equal(OutputParseStatus.Failed, result.Status);
        Assert.Same(PluginOutput.Empty, result.Output);
        Assert.Equal(PluginOutputParser.MaxDiagnostics, result.Diagnostics.Length);
        Assert.Equal(OutputDiagnosticCode.TooManyMenuNodes, result.Diagnostics[^2].Code);
        Assert.Equal(OutputDiagnosticCode.DiagnosticLimitReached, result.Diagnostics[^1].Code);
    }

    [Fact]
    public void ResultsAreDeterministic()
    {
        string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Output", "Fixtures", "system-monitor.txt"));

        string first = RenderAll(Parse(text));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(first, RenderAll(Parse(text)));
        }
    }

    [Fact]
    public void SeededRandomMalformedInputUpholdsTheInvariants()
    {
        string[] fragments =
        [
            "Item", " ", "  ", "\t", "|", "||", "\\|", "\\", "\"", "'", "\"\"", "=", "==", "-", "--", "---", "-----", "----",
            "href=", "href=https://example.com", "href=javascript:x", "shell=", "shell=t.exe", "bash=\"C:\\a b\\c.exe\"",
            "param0=", "param1=a", "param2=\"b c\"", "param3=''", "param33=x", "param01=x", "terminal=true", "terminal=x",
            "refresh=true", "refresh=false", "refresh=", "disabled=true", "disabled=x", "checked=true", "dropdown=false",
            "color=red", "color=#f00,#0f0", "color=", "size=12.5", "size=-1", "length=3", "trim=false", "tooltip=\"t\"",
            "font=Menlo", "image=abc", "alternate=true", "key=x", "foo=bar", "~~~", "{", "\r", "\u001B[0m", "\u202E",
            "\u2066", "\u200F", "\U0001F680", "e\u0301", "\u00A0", "\u2028", "\uFEFF", "\uFFFD",
        ];

        var random = new Random(20261126);
        for (int run = 0; run < 300; run++)
        {
            var builder = new StringBuilder();
            int lines = random.Next(0, 60);
            for (int line = 0; line < lines; line++)
            {
                int pieces = random.Next(0, 12);
                for (int piece = 0; piece < pieces; piece++)
                {
                    builder.Append(random.Next(20) == 0 ? (char)random.Next(0xD800, 0xE000) : fragments[random.Next(fragments.Length)]);
                }

                builder.Append(random.Next(4) == 0 ? "\r\n" : "\n");
            }

            string text = builder.ToString();
            PluginOutputParseResult result = Parse(text);
            Assert.Equal(RenderAll(result), RenderAll(Parse(text)));
            AssertInvariants(result);
        }
    }

    [Fact]
    public void RandomMutationsOfAValidMenuUpholdTheInvariants()
    {
        string valid = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Output", "Fixtures", "github-notifications.txt"));
        char[] alphabet = ['|', '"', '\'', '=', '\\', '-', ' ', '\n', '\r', 'x', '\u202E', (char)0xD800];
        var random = new Random(42);
        for (int run = 0; run < 300; run++)
        {
            var text = new StringBuilder(valid);
            int edits = random.Next(1, 8);
            for (int edit = 0; edit < edits && text.Length > 0; edit++)
            {
                int position = random.Next(text.Length);
                switch (random.Next(3))
                {
                    case 0:
                        text.Remove(position, Math.Min(random.Next(1, 20), text.Length - position));
                        break;
                    case 1:
                        text.Insert(position, alphabet[random.Next(alphabet.Length)]);
                        break;
                    default:
                        text[position] = alphabet[random.Next(alphabet.Length)];
                        break;
                }
            }

            AssertInvariants(Parse(text.ToString()));
        }
    }

    private static void AssertDisplaySafe(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            Assert.False(char.IsControl(c), $"control U+{(int)c:X4}");
            Assert.False(c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u2028' or '\u2029', $"U+{(int)c:X4}");
            if (char.IsHighSurrogate(c))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "lone high surrogate");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(c), "lone low surrogate");
            }
        }
    }

    private static void AssertInvariants(PluginOutputParseResult result)
    {
        Assert.InRange(result.Diagnostics.Length, 0, PluginOutputParser.MaxDiagnostics);
        if (result.Status == OutputParseStatus.Failed)
        {
            Assert.Same(PluginOutput.Empty, result.Output);
            return;
        }

        Assert.InRange(result.Output.Titles.Length, 0, PluginOutputParser.MaxTitles);

        // Lines on which a diagnostic says no action was created must not have one.
        HashSet<int> noActionLines =
        [
            .. result.Diagnostics
                .Where(d => d.Line is not null && d.Code is OutputDiagnosticCode.MalformedParameters or OutputDiagnosticCode.TooManyParameters
                    or OutputDiagnosticCode.InvalidUrl or OutputDiagnosticCode.InvalidCommand or OutputDiagnosticCode.InvalidCommandArguments
                    or OutputDiagnosticCode.ConflictingActions or OutputDiagnosticCode.OrphanedActionParameter or OutputDiagnosticCode.DisabledItemAction
                    || (d.Severity == PluginDiagnosticSeverity.Error && d.Code is OutputDiagnosticCode.DuplicateParameter
                        or OutputDiagnosticCode.InvalidParameterValue or OutputDiagnosticCode.UnknownParameter or OutputDiagnosticCode.UnsupportedBehavior))
                .Select(d => d.Line!.Value),
        ];

        int nodes = 0;
        foreach (OutputTitle title in result.Output.Titles)
        {
            CheckItem(title.Item, noActionLines);
            Assert.Empty(title.Item.Children);
        }

        Walk(result.Output.Items, 0);
        Assert.InRange(nodes, 0, PluginOutputParser.MaxMenuNodes);

        void Walk(ImmutableArray<MenuNode> level, int depth)
        {
            Assert.InRange(depth, 0, PluginOutputParser.MaxDepth);
            foreach (MenuNode node in level)
            {
                nodes++;
                if (node is MenuItem item)
                {
                    CheckItem(item, noActionLines);
                    Walk(item.Children, depth + 1);
                }
            }
        }
    }

    private static void CheckItem(MenuItem item, HashSet<int> noActionLines)
    {
        AssertDisplaySafe(item.Text);
        Assert.False(string.IsNullOrWhiteSpace(item.Text));
        if (item.Action is null)
        {
            return;
        }

        Assert.False(item.IsDisabled);
        Assert.Empty(item.Children);
        Assert.DoesNotContain(item.Line, noActionLines);
        switch (item.Action)
        {
            case UrlAction url:
                Assert.True(url.Url.Scheme is "http" or "https");
                Assert.NotEmpty(url.Url.Host);
                break;
            case CommandAction command:
                Assert.False(string.IsNullOrWhiteSpace(command.Executable));
                Assert.InRange(command.Arguments.Length, 0, PluginOutputParser.MaxCommandArguments);
                Assert.All(command.Arguments.Prepend(command.Executable), value => Assert.DoesNotContain(value, char.IsControl));
                break;
            default:
                Assert.IsType<RefreshAction>(item.Action);
                break;
        }
    }

    private static int MaxDepth(ImmutableArray<MenuNode> nodes) =>
        nodes.OfType<MenuItem>().Select(item => item.Children.IsEmpty ? 0 : 1 + MaxDepth(item.Children)).DefaultIfEmpty(0).Max();
}
