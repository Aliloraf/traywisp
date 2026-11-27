using System.Collections.Immutable;
using System.Globalization;

namespace TrayWisp.Core.Output;

/// <summary>Whether <see cref="PluginOutputParser.Parse"/> produced a usable result.</summary>
public enum OutputParseStatus
{
    /// <summary>
    /// Every line was processed. Malformed lines may have been dropped or had their parameters ignored;
    /// <see cref="PluginOutputParseResult.Diagnostics"/> says which.
    /// </summary>
    Complete,

    /// <summary>
    /// The output is unusable as a whole: it exceeded a global limit (including the line-length limit), was
    /// reported as truncated by the caller, or uses unsupported streaming. <see cref="PluginOutputParseResult.Output"/>
    /// is <see cref="PluginOutput.Empty"/>, so no partial menu or action is exposed.
    /// </summary>
    Failed,
}

/// <summary>Result of <see cref="PluginOutputParser.Parse"/>.</summary>
public sealed class PluginOutputParseResult
{
    internal PluginOutputParseResult(OutputParseStatus status, PluginOutput output, ImmutableArray<OutputDiagnostic> diagnostics, bool diagnosticsTruncated)
    {
        if (status == OutputParseStatus.Failed && !ReferenceEquals(output, PluginOutput.Empty))
        {
            throw new ArgumentException("A failed parse must not expose output.", nameof(output));
        }

        Status = status;
        Output = output;
        Diagnostics = diagnostics;
        DiagnosticsTruncated = diagnosticsTruncated;
    }

    /// <summary>Whether the output was usable.</summary>
    public OutputParseStatus Status { get; }

    /// <summary>The parsed titles and menu. <see cref="PluginOutput.Empty"/> when <see cref="Status"/> is <see cref="OutputParseStatus.Failed"/>.</summary>
    public PluginOutput Output { get; }

    /// <summary>
    /// Problems found while parsing, in the order they were detected, followed by the fatal
    /// error, if any, and a <see cref="OutputDiagnosticCode.DiagnosticLimitReached"/> summary when
    /// <see cref="DiagnosticsTruncated"/> is <see langword="true"/>. At most <see cref="PluginOutputParser.MaxDiagnostics"/> entries.
    /// Detection order is mostly, but not strictly, ascending line order: a
    /// <see cref="OutputDiagnosticCode.ParentActionRemoved"/> diagnostic names the earlier parent line but is
    /// reported when its first child is processed.
    /// </summary>
    public ImmutableArray<OutputDiagnostic> Diagnostics { get; }

    /// <summary><see langword="true"/> if some diagnostics were not recorded because of the diagnostic limit.</summary>
    public bool DiagnosticsTruncated { get; }
}

/// <summary>
/// Parsed SwiftBar/xbar-style text output: the header titles and the dropdown menu. The model is immutable
/// and inert. Actions are untrusted requests, not permission to run anything.
/// </summary>
public sealed class PluginOutput
{
    internal PluginOutput(ImmutableArray<OutputTitle> titles, ImmutableArray<MenuNode> items)
    {
        Titles = titles;
        Items = items;
    }

    /// <summary>Output with no titles and no menu items. Also the output of a failed parse.</summary>
    public static PluginOutput Empty { get; } = new([], []);

    /// <summary>
    /// Header titles in output order, for display (and future rotation) in the status area. May be empty:
    /// the parser never invents a default title.
    /// </summary>
    public ImmutableArray<OutputTitle> Titles { get; }

    /// <summary>Top-level dropdown menu items and separators, in output order.</summary>
    public ImmutableArray<MenuNode> Items { get; }
}

/// <summary>A header title line.</summary>
public sealed class OutputTitle
{
    internal OutputTitle(MenuItem item, bool showInDropdown)
    {
        if (!item.Children.IsEmpty)
        {
            throw new ArgumentException("A title cannot have children.", nameof(item));
        }

        Item = item;
        ShowInDropdown = showInDropdown;
    }

    /// <summary>The title's text, formatting and action. <see cref="MenuItem.Children"/> is always empty.</summary>
    public MenuItem Item { get; }

    /// <summary>
    /// <see langword="false"/> when the title declared <c>dropdown=false</c>: it is shown only in the status
    /// area, not repeated at the top of the dropdown menu.
    /// </summary>
    public bool ShowInDropdown { get; }
}

/// <summary>A dropdown menu entry: either a <see cref="MenuItem"/> or a <see cref="MenuSeparator"/>.</summary>
public abstract class MenuNode
{
    private protected MenuNode(int line)
    {
        Line = line;
    }

    /// <summary>One-based physical line number of the output line that produced this node.</summary>
    public int Line { get; }
}

/// <summary>A menu separator (<c>---</c>, or <c>-----</c>, <c>-------</c>, ... inside submenus). Separators have no children.</summary>
public sealed class MenuSeparator : MenuNode
{
    internal MenuSeparator(int line)
        : base(line)
    {
    }
}

/// <summary>A menu item or header title with its text, formatting, optional action and optional submenu.</summary>
public sealed class MenuItem : MenuNode
{
    internal MenuItem(int line, string text, TextFormat format, OutputAction? action, bool isDisabled, bool isChecked, ImmutableArray<MenuNode> children)
        : base(line)
    {
        if (action is not null && (isDisabled || !children.IsEmpty))
        {
            throw new ArgumentException("Disabled items and items with a submenu cannot have an action.", nameof(action));
        }

        Text = text;
        Format = format;
        Action = action;
        IsDisabled = isDisabled;
        IsChecked = isChecked;
        Children = children;
    }

    /// <summary>
    /// The full label, after <c>\|</c> unescaping and replacement of unsafe characters (see docs/output-protocol.md).
    /// It is not trimmed or truncated; <see cref="ToDisplayText"/> applies those display preferences.
    /// </summary>
    public string Text { get; }

    /// <summary>Formatting preferences. Never <see langword="null"/>.</summary>
    public TextFormat Format { get; }

    /// <summary>
    /// The action requested on activation, or <see langword="null"/>. Always <see langword="null"/> for
    /// disabled items and items with children. An action is an untrusted request: carrying it out requires the
    /// trust checks of Phase 3 and the execution safeguards of Phase 4.
    /// </summary>
    public OutputAction? Action { get; }

    /// <summary><c>disabled=true</c>: shown greyed out and not activatable.</summary>
    public bool IsDisabled { get; }

    /// <summary><c>checked=true</c>: shown with a check mark.</summary>
    public bool IsChecked { get; }

    /// <summary>Submenu entries in output order. Empty when the item has no submenu.</summary>
    public ImmutableArray<MenuNode> Children { get; }

    /// <summary>
    /// Returns <see cref="Text"/> with the display preferences applied: surrounding white space removed when
    /// <see cref="TextFormat.Trim"/> is set, then, if <see cref="TextFormat.Length"/> is set and the text is
    /// longer, the first <c>Length</c> user-perceived characters (extended grapheme clusters) followed by
    /// an ellipsis (U+2026). Surrogate pairs, emoji sequences and combining marks are never split.
    /// </summary>
    public string ToDisplayText()
    {
        string text = Format.Trim ? Text.Trim() : Text;
        if (Format.Length is not int maxElements)
        {
            return text;
        }

        int index = 0;
        int elements = 0;
        while (index < text.Length)
        {
            if (elements == maxElements)
            {
                return string.Concat(text.AsSpan(0, index), "\u2026");
            }

            index += StringInfo.GetNextTextElementLength(text.AsSpan(index));
            elements++;
        }

        return text;
    }
}
