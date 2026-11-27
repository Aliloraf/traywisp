using System.Collections.Immutable;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Core.Output;

/// <summary>
/// Parses SwiftBar/xbar-style text output (decoded stdout) into a <see cref="PluginOutput"/>. The parser is
/// a pure function: it never runs, opens, resolves or fetches anything, never throws for any input, and
/// always produces the same result for the same text. The grammar, limits and recovery rules are specified
/// in docs/output-protocol.md.
/// </summary>
public static class PluginOutputParser
{
    /// <summary>Longest accepted output, in UTF-16 code units (including a leading BOM). Longer output fails.</summary>
    public const int MaxInputLength = 262_144;

    /// <summary>Most physical lines accepted, blank lines included. More lines fail.</summary>
    public const int MaxLines = 4_096;

    /// <summary>
    /// Longest accepted physical line, in UTF-16 code units without its line terminator. A longer line, even a
    /// blank one, fails the parse.
    /// </summary>
    public const int MaxLineLength = 16_384;

    /// <summary>Most header titles accepted. More titles fail.</summary>
    public const int MaxTitles = 64;

    /// <summary>Most menu items and separators accepted, at all levels together. More nodes fail.</summary>
    public const int MaxMenuNodes = 2_048;

    /// <summary>Deepest submenu level. Top-level menu items have depth 0; deeper nodes are dropped.</summary>
    public const int MaxDepth = 8;

    /// <summary>Most parameters on one line. More make the line's parameters malformed.</summary>
    public const int MaxParametersPerLine = 64;

    /// <summary>Longest parameter value, in UTF-16 code units after unquoting. Longer values are invalid.</summary>
    public const int MaxValueLength = 4_096;

    /// <summary>Most command arguments (<c>paramN</c>).</summary>
    public const int MaxCommandArguments = 32;

    /// <summary>Most diagnostics returned, including a fatal error and the suppression summary.</summary>
    public const int MaxDiagnostics = 256;

    /// <summary>Smallest accepted <c>size</c>, in points.</summary>
    public const int MinFontSize = 1;

    /// <summary>Largest accepted <c>size</c>, in points.</summary>
    public const int MaxFontSize = 256;

    /// <summary>Largest accepted <c>length</c>.</summary>
    public const int MaxDisplayLength = 1_024;

    /// <summary>Parses decoded plugin output.</summary>
    /// <param name="text">The plugin's decoded standard output. Standard error must not be included.</param>
    /// <param name="isTruncated">
    /// <see langword="true"/> if <paramref name="text"/> is only part of what the plugin wrote (for example
    /// because capture stopped at a size limit). Truncated output always fails with
    /// <see cref="OutputDiagnosticCode.OutputTruncated"/> and exposes nothing, because a cut can leave a
    /// syntactically valid but different value (<c>param1=document.txt</c> cut to <c>param1=doc</c>). The
    /// parser never infers truncation, for example from a missing final line feed; callers must pass
    /// <see langword="true"/> whenever capture was incomplete.
    /// </param>
    public static PluginOutputParseResult Parse(ReadOnlySpan<char> text, bool isTruncated = false)
    {
        var diagnostics = new OutputDiagnosticBag();
        if (isTruncated)
        {
            return Fail(diagnostics, OutputDiagnosticCode.OutputTruncated, "The output was truncated before it ended; it was rejected and nothing is shown.", line: null);
        }

        if (text.Length > MaxInputLength)
        {
            return Fail(diagnostics, OutputDiagnosticCode.OutputTooLong, $"The output is longer than {MaxInputLength} characters; it was rejected and nothing is shown.", line: null);
        }

        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        // A final line feed ends the last line; it does not start another one.
        int lineCount = text.Count('\n') + (text.IsEmpty || text[^1] == '\n' ? 0 : 1);
        if (lineCount > MaxLines)
        {
            return Fail(diagnostics, OutputDiagnosticCode.TooManyLines, $"The output has more than {MaxLines} lines; it was rejected and nothing is shown.", line: null);
        }

        var builder = new Builder(diagnostics);
        int start = 0;
        int lineNumber = 0;
        while (start < text.Length)
        {
            int offset = text[start..].IndexOf('\n');
            int end = offset < 0 ? text.Length : start + offset;
            ReadOnlySpan<char> line = text[start..end];

            // Only a CR immediately before LF is part of the line terminator. A lone CR is content.
            if (offset >= 0 && line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            start = end + 1;
            lineNumber++;
            if (builder.AddLine(line, lineNumber) is { } fatal)
            {
                return Fail(diagnostics, fatal.Code, fatal.Message, lineNumber);
            }
        }

        return builder.Complete();
    }

    private static PluginOutputParseResult Fail(OutputDiagnosticBag diagnostics, OutputDiagnosticCode code, string message, int? line)
    {
        var fatal = new OutputDiagnostic(PluginDiagnosticSeverity.Error, code, message) { Line = line };
        ImmutableArray<OutputDiagnostic> all = diagnostics.ToImmutable(fatal, out bool truncated);
        return new PluginOutputParseResult(OutputParseStatus.Failed, PluginOutput.Empty, all, truncated);
    }

    // True if the line, ignoring trailing white space, consists only of hyphens. Leading white space is not allowed.
    private static bool IsHyphenRun(ReadOnlySpan<char> line, out int hyphens)
    {
        ReadOnlySpan<char> trimmed = line.TrimEnd();
        hyphens = trimmed.Length;
        return !trimmed.IsEmpty && trimmed.IndexOfAnyExcept('-') < 0;
    }

    private static bool IsStreamingDelimiter(ReadOnlySpan<char> line) => line.TrimEnd().SequenceEqual("~~~");

    private readonly record struct Fatal(OutputDiagnosticCode Code, string Message);

    private sealed class Builder(OutputDiagnosticBag diagnostics)
    {
        private readonly List<OutputTitle> _titles = [];
        private readonly List<Node> _roots = [];

        // _open[d] is the most recent item at depth d that can still receive children at depth d + 1.
        private readonly List<Node> _open = [];
        private bool _inBody;
        private bool _seenContent;
        private int _nodeCount;

        public Fatal? AddLine(ReadOnlySpan<char> line, int number)
        {
            // Checked before anything else, blank lines included: a dropped long line could not be classified
            // reliably (separator or item, and at which depth), so the whole output is rejected instead.
            if (line.Length > MaxLineLength)
            {
                return new Fatal(OutputDiagnosticCode.LineTooLong, $"The line is longer than {MaxLineLength} characters; the output was rejected and nothing is shown.");
            }

            if (OutputText.IsBlank(line))
            {
                return null;
            }

            if (!_seenContent)
            {
                _seenContent = true;
                if (line.TrimStart().StartsWith('{'))
                {
                    Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.JsonNotSupported, "The output looks like JSON. Only the SwiftBar/xbar text format is supported; it was parsed as text.", number);
                }
            }

            // Frames before and after a delimiter would combine into a menu the plugin never printed, with
            // stale actions, so streaming output is rejected as a whole.
            if (IsStreamingDelimiter(line))
            {
                return new Fatal(OutputDiagnosticCode.StreamingNotSupported, "'~~~' is a SwiftBar streaming delimiter; streaming output is not supported, so the output was rejected and nothing is shown.");
            }

            return _inBody ? AddBodyLine(line, number) : AddHeaderLine(line, number);
        }

        public PluginOutputParseResult Complete()
        {
            var items = ImmutableArray.CreateBuilder<MenuNode>(_roots.Count);
            foreach (Node root in _roots)
            {
                items.Add(root.Freeze());
            }

            ImmutableArray<OutputDiagnostic> all = diagnostics.ToImmutable(fatal: null, out bool truncated);
            return new PluginOutputParseResult(OutputParseStatus.Complete, new PluginOutput([.. _titles], items.MoveToImmutable()), all, truncated);
        }

        // Header lines are never nested: a leading "--" is part of the title text.
        private Fatal? AddHeaderLine(ReadOnlySpan<char> line, int number)
        {
            if (IsHyphenRun(line, out int hyphens) && hyphens == 3)
            {
                _inBody = true;
                return null;
            }

            ParsedLine? parsed = OutputLineParser.Parse(line, 1, number, isTitle: true, diagnostics);
            if (parsed is null)
            {
                ReportEmptyLabel(number);
                return null;
            }

            if (_titles.Count == MaxTitles)
            {
                return new Fatal(OutputDiagnosticCode.TooManyTitles, $"The output has more than {MaxTitles} header titles; it was rejected and nothing is shown.");
            }

            var item = new MenuItem(number, parsed.Text, parsed.Format, parsed.Action, parsed.IsDisabled, parsed.IsChecked, []);
            _titles.Add(new OutputTitle(item, parsed.ShowInDropdown));
            return null;
        }

        private Fatal? AddBodyLine(ReadOnlySpan<char> line, int number)
        {
            // Separators are recognized before nesting prefixes: 2d + 3 hyphens is a separator at depth d.
            if (IsHyphenRun(line, out int hyphens) && hyphens >= 2)
            {
                int separatorDepth = hyphens / 2;
                if (hyphens % 2 == 0)
                {
                    Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.MalformedSeparator, "A line of an even number of hyphens is not a separator (separators are ---, -----, -------, ...); it was dropped.", number);
                    CloseLevels(separatorDepth);
                    return null;
                }

                separatorDepth = (hyphens - 3) / 2;
                return CheckDepth(separatorDepth, number) ? Attach(new Node(number, content: null), separatorDepth) : null;
            }

            int depth = LeadingHyphens(line) / 2;
            if (!CheckDepth(depth, number) || !CheckParent(depth, number))
            {
                return null;
            }

            ParsedLine? parsed = OutputLineParser.Parse(line[(2 * depth)..], (2 * depth) + 1, number, isTitle: false, diagnostics);
            if (parsed is null)
            {
                ReportEmptyLabel(number);
                CloseLevels(depth);
                return null;
            }

            return Attach(new Node(number, parsed), depth);
        }

        private bool CheckDepth(int depth, int number)
        {
            if (depth <= MaxDepth)
            {
                return true;
            }

            Add(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.MenuDepthExceeded, $"The line is nested deeper than {MaxDepth} submenu levels; it was dropped with its children.", number);
            CloseLevels(depth);
            return false;
        }

        // A node at depth d needs an open item at depth d - 1. Missing parents are never invented and the node
        // is never attached to an unrelated item; it is dropped, and so are its children, which then have no
        // open parent either.
        private bool CheckParent(int depth, int number)
        {
            if (depth <= _open.Count)
            {
                return true;
            }

            Add(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.OrphanedMenuNode, $"The line is nested at level {depth}, but there is no menu item at level {depth - 1} above it; it was dropped with its children.", number);
            return false;
        }

        private Fatal? Attach(Node node, int depth)
        {
            if (!CheckParent(depth, node.Line))
            {
                return null;
            }

            if (++_nodeCount > MaxMenuNodes)
            {
                return new Fatal(OutputDiagnosticCode.TooManyMenuNodes, $"The menu has more than {MaxMenuNodes} items and separators; it was rejected and nothing is shown.");
            }

            if (depth == 0)
            {
                _roots.Add(node);
            }
            else
            {
                Node parent = _open[depth - 1];
                if (parent.RemoveAction())
                {
                    Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.ParentActionRemoved, "The item has a submenu, so its action was removed; the submenu was kept.", parent.Line);
                }

                parent.Children.Add(node);
            }

            CloseLevels(depth);
            if (!node.IsSeparator)
            {
                _open.Add(node);
            }

            return null;
        }

        private void CloseLevels(int depth)
        {
            if (depth < _open.Count)
            {
                _open.RemoveRange(depth, _open.Count - depth);
            }
        }

        private void ReportEmptyLabel(int number) =>
            Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.EmptyLabel, "The line has an empty or invisible label; it was dropped with its parameters and children.", number);

        private void Add(PluginDiagnosticSeverity severity, OutputDiagnosticCode code, string message, int number) =>
            diagnostics.Add(severity, code, message, number);

        private static int LeadingHyphens(ReadOnlySpan<char> line)
        {
            int count = line.IndexOfAnyExcept('-');
            return count < 0 ? line.Length : count;
        }
    }

    // Mutable while lines are added; frozen into immutable MenuNodes at the end. Depth is bounded by
    // MaxDepth, so Freeze recurses at most MaxDepth + 1 levels.
    private sealed class Node(int line, ParsedLine? content)
    {
        private OutputAction? _action = content?.Action;

        public int Line { get; } = line;

        public bool IsSeparator => content is null;

        public List<Node> Children { get; } = [];

        // Returns true the first time an action is actually removed.
        public bool RemoveAction()
        {
            bool removed = _action is not null;
            _action = null;
            return removed;
        }

        public MenuNode Freeze()
        {
            if (content is null)
            {
                return new MenuSeparator(Line);
            }

            var children = ImmutableArray.CreateBuilder<MenuNode>(Children.Count);
            foreach (Node child in Children)
            {
                children.Add(child.Freeze());
            }

            return new MenuItem(Line, content.Text, content.Format, _action, content.IsDisabled, content.IsChecked, children.MoveToImmutable());
        }
    }
}
