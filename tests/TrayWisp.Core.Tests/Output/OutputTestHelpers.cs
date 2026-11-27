using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Output;

// Shared helpers: parsing shortcuts and a canonical text rendering of a parse result, so that structure
// tests can compare whole trees and determinism tests can compare complete results.
internal static class OutputTestHelpers
{
    public static PluginOutputParseResult Parse(string text) => PluginOutputParser.Parse(text);

    public static PluginOutputParseResult Parse(string text, bool isTruncated) => PluginOutputParser.Parse(text, isTruncated);

    // Asserts a failed parse: nothing exposed, and the last diagnostic is the fatal error.
    public static void AssertFailed(PluginOutputParseResult result, OutputDiagnosticCode code, int? line)
    {
        Assert.Equal(OutputParseStatus.Failed, result.Status);
        Assert.Same(PluginOutput.Empty, result.Output);
        Assert.Empty(result.Output.Titles);
        Assert.Empty(result.Output.Items);
        OutputDiagnostic fatal = result.Diagnostics[^1];
        Assert.Equal(code, fatal.Code);
        Assert.Equal(PluginDiagnosticSeverity.Error, fatal.Severity);
        Assert.Equal(line, fatal.Line);
    }

    // Joins lines with LF and ends with LF. Used instead of raw string literals, whose line endings depend on
    // how the source file was checked out.
    public static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    // Parses text that must succeed, and returns its output.
    public static PluginOutput ParseComplete(string text)
    {
        PluginOutputParseResult result = Parse(text);
        Assert.Equal(OutputParseStatus.Complete, result.Status);
        return result.Output;
    }

    // Parses a single body line ("---\n" + line) and returns its menu item.
    public static MenuItem ParseItem(string line, out ImmutableArray<OutputDiagnostic> diagnostics)
    {
        PluginOutputParseResult result = Parse("---\n" + line);
        Assert.Equal(OutputParseStatus.Complete, result.Status);
        diagnostics = result.Diagnostics;
        return Assert.IsType<MenuItem>(Assert.Single(result.Output.Items));
    }

    public static MenuItem ParseItem(string line) => ParseItem(line, out _);

    // Parses a single body line that must produce no diagnostics.
    public static MenuItem ParseCleanItem(string line)
    {
        MenuItem item = ParseItem(line, out ImmutableArray<OutputDiagnostic> diagnostics);
        Assert.Empty(diagnostics);
        return item;
    }

    public static OutputDiagnosticCode[] Codes(PluginOutputParseResult result) => [.. result.Diagnostics.Select(d => d.Code)];

    public static OutputDiagnosticCode[] Codes(ImmutableArray<OutputDiagnostic> diagnostics) => [.. diagnostics.Select(d => d.Code)];

    // Renders titles and menu as indented lines, for example:
    //   title 'Clock' [hidden]
    //   item 'Open' url=https://example.com/
    //     separator
    public static string Render(PluginOutput output)
    {
        var builder = new StringBuilder();
        foreach (OutputTitle title in output.Titles)
        {
            builder.Append("title ").Append(Describe(title.Item));
            if (!title.ShowInDropdown)
            {
                builder.Append(" [hidden]");
            }

            builder.Append('\n');
        }

        foreach (MenuNode node in output.Items)
        {
            Render(node, 0, builder);
        }

        return builder.ToString();
    }

    // Renders everything, including status and diagnostics, for determinism checks.
    public static string RenderAll(PluginOutputParseResult result) =>
        $"{result.Status} {result.DiagnosticsTruncated}\n{Render(result.Output)}{string.Join('\n', result.Diagnostics)}";

    private static void Render(MenuNode node, int depth, StringBuilder builder)
    {
        builder.Append(' ', depth * 2);
        if (node is MenuItem item)
        {
            builder.Append("item ").Append(Describe(item)).Append('\n');
            foreach (MenuNode child in item.Children)
            {
                Render(child, depth + 1, builder);
            }
        }
        else
        {
            builder.Append("separator\n");
        }
    }

    private static string Describe(MenuItem item)
    {
        var builder = new StringBuilder();
        builder.Append('\'').Append(item.Text).Append('\'');
        if (item.IsDisabled)
        {
            builder.Append(" disabled");
        }

        if (item.IsChecked)
        {
            builder.Append(" checked");
        }

        TextFormat f = item.Format;
        if (f.Color is not null)
        {
            builder.Append(" color=").Append(f.Color.Light).Append(',').Append(f.Color.Dark);
        }

        if (f.Font is not null)
        {
            builder.Append(" font=").Append(f.Font);
        }

        if (f.Size is double size)
        {
            builder.Append(" size=").Append(size.ToString(CultureInfo.InvariantCulture));
        }

        if (f.Length is int length)
        {
            builder.Append(" length=").Append(length.ToString(CultureInfo.InvariantCulture));
        }

        if (!f.Trim)
        {
            builder.Append(" trim=false");
        }

        if (f.Tooltip is not null)
        {
            builder.Append(" tooltip='").Append(f.Tooltip).Append('\'');
        }

        builder.Append(item.Action switch
        {
            null => string.Empty,
            RefreshAction => " refresh",
            UrlAction url => " url=" + url.Url.OriginalString + (url.RefreshesPlugin ? " +refresh" : string.Empty),
            CommandAction command => " run=<" + command.Executable + ">" + string.Concat(command.Arguments.Select(a => " <" + a + ">"))
                + (command.RunInTerminal is bool terminal ? " terminal=" + (terminal ? "true" : "false") : string.Empty)
                + (command.RefreshesPlugin ? " +refresh" : string.Empty),
            _ => throw new InvalidOperationException("Unknown action type."),
        });
        return builder.ToString();
    }
}
