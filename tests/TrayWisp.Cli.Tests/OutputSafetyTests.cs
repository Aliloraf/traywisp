using System.Text.Json;
using TrayWisp.Cli.Output;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Cli.Tests;

// The display policy for untrusted text and the JSON document format (docs/cli.md, "Terminal safety", "JSON output").
public sealed class OutputSafetyTests
{
    [Theory]
    [InlineData("\u001B[31mred\u001B[0m", "\\u001B[31mred\\u001B[0m")]
    [InlineData("\u001B]8;;https://evil.example\u0007click\u001B]8;;\u0007", "\\u001B]8;;https://evil.example\\u0007click\\u001B]8;;\\u0007")]
    [InlineData("safe\rEVIL", "safe\\u000DEVIL")]
    [InlineData("ab\b\bXY", "ab\\u0008\\u0008XY")]
    [InlineData("one\ntwo", "one\\u000Atwo")]
    [InlineData("tab\tbed", "tab\\u0009bed")]
    [InlineData("\u009B31m", "\\u009B31m")]
    [InlineData("del\u007F", "del\\u007F")]
    [InlineData("abc\u202Egnp.exe", "abc\\u202Egnp.exe")]
    [InlineData("x\u2066y\u2069", "x\\u2066y\\u2069")]
    [InlineData("zero\u200Bwidth", "zero\\u200Bwidth")]
    [InlineData("line\u2028sep", "line\\u2028sep")]
    [InlineData("C:\\Users\\me", "C:\\Users\\me")]
    [InlineData("caf\u00E9 \u65E5\u672C \uD83D\uDE80", "caf\u00E9 \u65E5\u672C \uD83D\uDE80")]
    [InlineData("  spaced  out  ", "  spaced  out  ")]
    [InlineData("Error TW500: fake", "Error TW500: fake")]
    public void InlineTextIsSingleLineAndFreeOfControls(string value, string expected)
    {
        string safe = TerminalText.Inline(value);
        Assert.Equal(expected, safe);
        Assert.False(HasUnsafe(safe));
    }

    [Fact]
    public void LoneSurrogatesAreEscaped()
    {
        Assert.Equal("a\\uD800b", TerminalText.Inline("a" + (char)0xD800 + "b"));
        Assert.Equal("a\\uDC00", TerminalText.Inline("a" + (char)0xDC00));
    }

    [Fact]
    public void TagCharactersAreEscapedAsPairs() => Assert.Equal("a\\uDB40\\uDC41", TerminalText.Inline("a\uDB40\uDC41"));

    [Fact]
    public void LabelsKeepZeroWidthJoinersButIdentitiesDoNot()
    {
        string family = "\uD83D\uDC69\u200D\uD83D\uDC67";
        Assert.Equal(family, TerminalText.Inline(family, kind: TerminalText.Kind.Label));
        Assert.Contains("\\u200D", TerminalText.Inline(family), StringComparison.Ordinal);
    }

    [Fact]
    public void LongValuesAreCutWithAMarker()
    {
        string safe = TerminalText.Inline(new string('a', 5000), 100);
        Assert.Equal(100 + TerminalText.TruncationMarker.Length, safe.Length);
        Assert.EndsWith(TerminalText.TruncationMarker, safe, StringComparison.Ordinal);
        Assert.Equal("\uD83D\uDE80" + TerminalText.TruncationMarker, TerminalText.Inline("\uD83D\uDE80\uD83D\uDE80", 1));
    }

    [Fact]
    public void LinesAreMadeSafeOneByOneAndBounded()
    {
        string[] lines = [.. TerminalText.Lines("a\r\nb\u001B\nc", 2)];
        Assert.Equal(["b\\u001B", "c"], lines);
    }

    [Fact]
    public void QuotedValuesShowArgumentBoundaries()
    {
        Assert.Equal("\"a b\"", OutputRenderer.Quoted("a b"));
        Assert.Equal("\"say \\\"hi\\\"\"", OutputRenderer.Quoted("say \"hi\""));
        Assert.Equal("\"C:\\\\x\\\\\"", OutputRenderer.Quoted("C:\\x\\"));
        Assert.Equal("\"\\u001B[2J\"", OutputRenderer.Quoted("\u001B[2J"));
    }

    [Fact]
    public void HumanFieldsCannotStartNewLines()
    {
        var writer = new HumanWriter();
        writer.Field("Title", "x\nError TW999: injected\rmore");
        writer.Diagnostics([CliDiagnostic.Error(CliDiagnosticCode.UsageError, "a\nb") with { Path = "p\u202E.ps1" }]);
        string text = writer.ToString();
        Assert.Equal(3, text.Count(c => c == '\n'));
        Assert.StartsWith("Title: x\\u000AError TW999", text, StringComparison.Ordinal);
        Assert.False(HasUnsafe(text.Replace("\n", string.Empty, StringComparison.Ordinal)));
    }

    [Fact]
    public void JsonIsOneAsciiDocumentWithTheSchemaFields()
    {
        string json = JsonReport.Write("run", 4, writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("label", "\u001B[31m\u202E\u65E5 \"q\" \\ " + (char)0xD800);
            writer.WriteEndObject();
        }, [CliDiagnostic.Warning(CliDiagnosticCode.RunNotRecorded, "m") with { Path = "a/b.ps1", Line = 3 }]);

        Assert.All(json, c => Assert.True(c < 0x80 && (c >= 0x20 || c == '\n'), $"U+{(int)c:X4}"));
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(["schemaVersion", "command", "success", "exitCode", "result", "diagnostics"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("run", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(4, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("\u001B[31m\u202E\u65E5 \"q\" \\ \uFFFD", root.GetProperty("result").GetProperty("label").GetString());
        JsonElement diagnostic = Assert.Single(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(["source", "code", "severity", "message", "path", "line", "column"], diagnostic.EnumerateObject().Select(p => p.Name));
        Assert.Equal("TW858", diagnostic.GetProperty("code").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(3, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("column").ValueKind);
    }

    [Fact]
    public void JsonOutputIsDeterministic()
    {
        static string Make() => JsonReport.Write("list", 0, writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("a", "b\u00E9");
            writer.WriteEndObject();
        }, [CliDiagnostic.Warning(CliDiagnosticCode.DiscoveryIncomplete, "x")]);
        Assert.Equal(Make(), Make());
    }

    [Fact]
    public void ActionsAreRenderedAsInertDataWithExactValues()
    {
        PluginOutputParseResult parsed = PluginOutputParser.Parse(
            "T\n---\nA | shell=C:\\Tools\\run.exe param1=\"a b\" param2=\"x;y&z\" terminal=false\nU | href=https://example.com/p%0Aq\nR | refresh=true\n");
        Assert.Equal(OutputParseStatus.Complete, parsed.Status);

        var writer = new HumanWriter();
        OutputRenderer.WriteHuman(writer, parsed.Output);
        string text = writer.ToString();
        Assert.Contains("executable: \"C:\\\\Tools\\\\run.exe\"", text, StringComparison.Ordinal);
        Assert.Contains("argument[0]: \"a b\"", text, StringComparison.Ordinal);
        Assert.Contains("argument[1]: \"x;y&z\"", text, StringComparison.Ordinal);
        Assert.Contains("\"https://example.com/p%0Aq\"", text, StringComparison.Ordinal);
        Assert.Contains("not run", text, StringComparison.Ordinal);
        Assert.DoesNotContain("run.exe a b", text, StringComparison.Ordinal);

        string json = JsonReport.Write("validate-output", 0, w => OutputRenderer.WriteJson(w, parsed.Output), []);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement[] items = [.. document.RootElement.GetProperty("result").GetProperty("items").EnumerateArray()];
        JsonElement command = items[0].GetProperty("action");
        Assert.Equal("command", command.GetProperty("type").GetString());
        Assert.Equal(@"C:\Tools\run.exe", command.GetProperty("executable").GetString());
        Assert.Equal(["a b", "x;y&z"], command.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()));
        Assert.False(command.GetProperty("terminal").GetBoolean());
        Assert.Equal("https://example.com/p%0Aq", items[1].GetProperty("action").GetProperty("url").GetString());
        Assert.Equal("refresh", items[2].GetProperty("action").GetProperty("type").GetString());
        Assert.DoesNotContain("commandLine", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NestedMenusKeepTheirStructure()
    {
        PluginOutputParseResult parsed = PluginOutputParser.Parse("T\n---\nTop\n--Child\n----Grandchild\n-----\nAfter\n");
        string json = JsonReport.Write("validate-output", 0, w => OutputRenderer.WriteJson(w, parsed.Output), []);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement top = document.RootElement.GetProperty("result").GetProperty("items")[0];
        Assert.Equal("Top", top.GetProperty("text").GetString());
        JsonElement child = top.GetProperty("children")[0];
        Assert.Equal("Child", child.GetProperty("text").GetString());
        Assert.Equal("Grandchild", child.GetProperty("children")[0].GetProperty("text").GetString());

        var writer = new HumanWriter();
        OutputRenderer.WriteHuman(writer, parsed.Output);
        Assert.Contains("\n      - Grandchild\n", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsAreFormattedWithTheirSource() =>
        Assert.Equal(
            "Warning TW214 [discovery] 'a/b.ps1' line 2: m\\u001B",
            HumanWriter.Format(CliDiagnostic.From(new PluginDiagnostic(PluginDiagnosticSeverity.Warning, PluginDiagnosticCode.MetadataInvalidValue, "m\u001B") { Path = "a/b.ps1", Line = 2 })));

    // Whether the text holds a control, format or separator character, or an unpaired surrogate.
    private static bool HasUnsafe(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c) || char.IsControl(c)
                || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }
}
