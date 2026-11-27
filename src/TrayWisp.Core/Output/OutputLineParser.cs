using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Core.Output;

/// <summary>The content of one title or menu-item line: label, formatting and action.</summary>
internal sealed record ParsedLine(string Text, TextFormat Format, OutputAction? Action, bool IsDisabled, bool IsChecked, bool ShowInDropdown);

/// <summary>
/// Parses the content of one line (after any nesting prefix): the label up to the first unescaped <c>|</c>,
/// then <c>name=value</c> parameters. The grammar is specified in docs/output-protocol.md. This is neither a
/// Unix shell nor a Windows command-line tokenizer: nothing is expanded, and backslashes in values are literal.
/// </summary>
internal static class OutputLineParser
{
    /// <summary>Longest accepted parameter name.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>Longest accepted <c>font</c> value.</summary>
    public const int MaxFontNameLength = 128;

    private const int MaxNameInMessage = 32;
    private const string Ignored = "it was ignored.";
    private const string NoAction = "no action was created.";

    private static readonly string[] KeyNames =
        ["color", "font", "size", "length", "trim", "tooltip", "dropdown", "disabled", "checked", "href", "shell/bash", "terminal", "refresh"];

    private static readonly FrozenDictionary<string, Key> Keys = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
    {
        ["color"] = Key.Color,
        ["font"] = Key.Font,
        ["size"] = Key.Size,
        ["length"] = Key.Length,
        ["trim"] = Key.Trim,
        ["tooltip"] = Key.Tooltip,
        ["dropdown"] = Key.Dropdown,
        ["disabled"] = Key.Disabled,
        ["checked"] = Key.Checked,
        ["href"] = Key.Href,
        ["shell"] = Key.Shell,
        ["bash"] = Key.Shell,
        ["terminal"] = Key.Terminal,
        ["refresh"] = Key.Refresh,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Known SwiftBar/xbar parameters that only affect appearance. Ignored with a diagnostic.
    private static readonly FrozenSet<string> UnsupportedAppearance = new[]
    {
        "image", "templateImage", "sfimage", "sfcolor", "sfsize", "sfconfig", "ansi", "md", "emojize", "symbolize", "badge",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Known SwiftBar/xbar parameters that change how or when an action is invoked. They suppress the line's action.
    private static readonly FrozenSet<string> UnsupportedBehavior = new[]
    {
        "alternate", "key", "shortcut", "webview", "webvieww", "webviewh", "stdin",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private enum Key
    {
        Color,
        Font,
        Size,
        Length,
        Trim,
        Tooltip,
        Dropdown,
        Disabled,
        Checked,
        Href,
        Shell,
        Terminal,
        Refresh,
    }

    private enum OtherKind
    {
        Appearance,
        Behavior,
        Unknown,
    }

    private enum ValueState
    {
        Absent,
        Invalid,
        Present,
    }

    /// <summary>
    /// Parses <paramref name="content"/>. Returns <see langword="null"/> if the label shows nothing (empty,
    /// white space or format characters only; the caller reports it); parameters are then not examined.
    /// </summary>
    /// <param name="content">The line without its nesting prefix and line terminator.</param>
    /// <param name="firstColumn">One-based column of <paramref name="content"/>[0] in the physical line.</param>
    /// <param name="line">One-based line number.</param>
    /// <param name="isTitle">Whether the line is a header title.</param>
    /// <param name="diagnostics">Receives diagnostics.</param>
    public static ParsedLine? Parse(ReadOnlySpan<char> content, int firstColumn, int line, bool isTitle, OutputDiagnosticBag diagnostics)
    {
        int separator = FindParameterSeparator(content);
        ReadOnlySpan<char> label = separator < 0 ? content : content[..separator];
        string text = OutputText.ToDisplayText(label, unescapePipes: true, out int firstUnsafe);
        if (OutputText.IsInvisible(text))
        {
            return null;
        }

        if (firstUnsafe >= 0)
        {
            diagnostics.Add(
                PluginDiagnosticSeverity.Warning,
                OutputDiagnosticCode.UnsafeText,
                "The label contains control characters, line separators, invalid UTF-16 or directional formatting characters; they were replaced with U+FFFD or removed.",
                line,
                firstColumn + firstUnsafe);
        }

        var tokens = new List<Token>();
        if (separator >= 0 && !Tokenize(content[(separator + 1)..], firstColumn + separator + 1, line, tokens, diagnostics))
        {
            return new ParsedLine(text, TextFormat.Default, null, IsDisabled: false, IsChecked: false, ShowInDropdown: true);
        }

        return new Interpreter(line, diagnostics).Interpret(text, tokens, isTitle);
    }

    // The first '|' not preceded by a backslash. "\|" is the only escape in labels.
    private static int FindParameterSeparator(ReadOnlySpan<char> content)
    {
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\\' && i + 1 < content.Length && content[i + 1] == '|')
            {
                i++;
            }
            else if (content[i] == '|')
            {
                return i;
            }
        }

        return -1;
    }

    // Splits the parameter section into name=value tokens. Returns false (after one diagnostic) on any
    // syntax error, in which case no parameter of the line is used.
    private static bool Tokenize(ReadOnlySpan<char> text, int firstColumn, int line, List<Token> tokens, OutputDiagnosticBag diagnostics)
    {
        int i = 0;
        while (true)
        {
            while (i < text.Length && IsDelimiter(text[i]))
            {
                i++;
            }

            if (i == text.Length)
            {
                return true;
            }

            int column = firstColumn + i;
            if (tokens.Count == PluginOutputParser.MaxParametersPerLine)
            {
                diagnostics.Add(
                    PluginDiagnosticSeverity.Error,
                    OutputDiagnosticCode.TooManyParameters,
                    $"The line has more than {PluginOutputParser.MaxParametersPerLine} parameters; all of its parameters were ignored and no action was created.",
                    line,
                    column);
                return false;
            }

            int keyStart = i;
            while (i < text.Length && IsKeyCharacter(text[i]))
            {
                i++;
            }

            ReadOnlySpan<char> key = text[keyStart..i];
            if (key.IsEmpty || !char.IsAsciiLetter(key[0]) || key.Length > MaxKeyLength)
            {
                return Malformed(diagnostics, line, column, $"a parameter name must start with an ASCII letter, contain only ASCII letters, digits, '_', '-' and '.', and be at most {MaxKeyLength} characters long");
            }

            if (i == text.Length || text[i] != '=')
            {
                bool spaceBeforeEquals = i < text.Length && IsSpace(text[i]) && text[i..].TrimStart(" \t").StartsWith('=');
                return Malformed(
                    diagnostics,
                    line,
                    column,
                    spaceBeforeEquals ? "white space is not allowed around '='" : "a parameter must have the form name=value");
            }

            i++;
            int valueColumn = firstColumn + i;
            if (i == text.Length || IsDelimiter(text[i]))
            {
                return Malformed(diagnostics, line, valueColumn, "the parameter has no value (white space is not allowed after '='); write name=\"\" for an empty value");
            }

            string value;
            char quote = text[i];
            if (quote is '"' or '\'')
            {
                var builder = new StringBuilder();
                i++;
                while (true)
                {
                    int offset = text[i..].IndexOf(quote);
                    if (offset < 0)
                    {
                        return Malformed(diagnostics, line, valueColumn, "the quoted value has no closing quote");
                    }

                    builder.Append(text.Slice(i, offset));
                    i += offset + 1;
                    if (i < text.Length && text[i] == quote)
                    {
                        // A doubled quote character stands for one literal quote.
                        builder.Append(quote);
                        i++;
                        continue;
                    }

                    break;
                }

                if (i < text.Length && !IsDelimiter(text[i]))
                {
                    return Malformed(diagnostics, line, firstColumn + i, "a closing quote must be followed by white space, '|' or the end of the line");
                }

                value = builder.ToString();
            }
            else
            {
                int valueStart = i;
                while (i < text.Length && !IsDelimiter(text[i]))
                {
                    if (text[i] is '"' or '\'')
                    {
                        return Malformed(diagnostics, line, firstColumn + i, "a quote may only appear at the start of a value; quote the whole value and double any quote inside it");
                    }

                    i++;
                }

                value = text[valueStart..i].ToString();
            }

            tokens.Add(new Token(key.ToString(), value, column));
        }
    }

    private static bool Malformed(OutputDiagnosticBag diagnostics, int line, int column, string problem)
    {
        diagnostics.Add(
            PluginDiagnosticSeverity.Error,
            OutputDiagnosticCode.MalformedParameters,
            $"Malformed parameters: {problem}. All parameters on the line were ignored and no action was created.",
            line,
            column);
        return false;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t';

    private static bool IsDelimiter(char c) => c is ' ' or '\t' or '|';

    private static bool IsKeyCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';

    private static string QuoteName(string name) => PluginDiagnosticText.Quote(name, MaxNameInMessage);

    // paramN, case-insensitive. Returns false if the name is not of that shape at all (for example "params").
    private static bool IsArgumentName(string name, out int index, out string? problem)
    {
        index = -1;
        problem = null;
        const string Prefix = "param";
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> digits = name.AsSpan(Prefix.Length);
        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        if (digits.IsEmpty)
        {
            problem = "the argument index is missing";
        }
        else if (digits.Length > 1 && digits[0] == '0')
        {
            problem = "leading zeros are not allowed in argument indices";
        }
        else if (!TryParseInteger(digits, 0, PluginOutputParser.MaxCommandArguments, out index))
        {
            index = -1;
            problem = $"the highest argument index is {PluginOutputParser.MaxCommandArguments}";
        }

        return true;
    }

    private static OtherKind Classify(string name)
    {
        if (UnsupportedAppearance.Contains(name))
        {
            return OtherKind.Appearance;
        }

        // sfcolor1, sfcolor2, ... colour individual SF Symbols in SwiftBar.
        if (name.StartsWith("sfcolor", StringComparison.OrdinalIgnoreCase) && name.AsSpan("sfcolor".Length).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return OtherKind.Appearance;
        }

        return UnsupportedBehavior.Contains(name) ? OtherKind.Behavior : OtherKind.Unknown;
    }

    /// <summary>
    /// Parses ASCII digits into a value between <paramref name="min"/> and <paramref name="max"/>. Leading zeros
    /// are allowed. Arbitrarily long digit strings cannot overflow.
    /// </summary>
    internal static bool TryParseInteger(ReadOnlySpan<char> text, int min, int max, out int value)
    {
        value = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        bool overflow = false;
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            // Stop accumulating once above max; max * 10 + 9 still fits in an int for the limits used here.
            if (!overflow)
            {
                value = (value * 10) + (c - '0');
                overflow = value > max;
            }
        }

        return !overflow && value >= min;
    }

    // ASCII digits with an optional fractional part of one or two digits: 12, 12.5, 9.75. No sign, exponent,
    // group separator or white space, so the result never depends on culture.
    private static bool TryParseSize(ReadOnlySpan<char> text, out double size)
    {
        size = 0;
        int dot = text.IndexOf('.');
        ReadOnlySpan<char> whole = dot < 0 ? text : text[..dot];
        ReadOnlySpan<char> fraction = dot < 0 ? [] : text[(dot + 1)..];
        if (!TryParseInteger(whole, 0, PluginOutputParser.MaxFontSize, out int wholeValue)
            || (dot >= 0 && (fraction.IsEmpty || fraction.Length > 2 || fraction.IndexOfAnyExceptInRange('0', '9') >= 0)))
        {
            return false;
        }

        size = dot < 0 ? wholeValue : double.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return size is >= PluginOutputParser.MinFontSize and <= PluginOutputParser.MaxFontSize;
    }

    private static bool TryParseBoolean(string text, out bool value)
    {
        value = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        return value || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidExecutable(string value) =>
        !OutputText.IsBlank(value)
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1])
        && OutputText.IsSafeValue(value);

    /// <summary>
    /// Accepts only an absolute <c>http://</c> or <c>https://</c> URL with a host, written using only the
    /// characters RFC 3986 allows (unreserved, reserved and <c>%</c>; so no spaces, controls, backslashes,
    /// <c>|</c>, <c>{</c>, <c>}</c>, <c>^</c>, <c>`</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c> or non-ASCII), with every
    /// <c>%</c> starting a two-hex-digit escape, well formed as a whole
    /// (<see cref="Uri.IsWellFormedOriginalString"/>), without user information, and whose host .NET does
    /// not rewrite (such as the numeric IPv4 form <c>http://2130706433/</c>). Nothing is decoded or normalized:
    /// the stored <see cref="Uri.OriginalString"/> is <paramref name="value"/>.
    /// </summary>
    internal static bool TryCreateUrl(string value, out Uri? url)
    {
        url = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length || !char.IsAsciiHexDigit(value[i + 1]) || !char.IsAsciiHexDigit(value[i + 2]))
                {
                    return false;
                }

                i += 2;
            }
            else if (!char.IsAsciiLetterOrDigit(c) && !"-._~:/?#[]@!$&'()*+,;=".Contains(c))
            {
                return false;
            }
        }

        int authorityStart = value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https://".Length
            : value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http://".Length
            : -1;
        if (authorityStart < 0)
        {
            return false;
        }

        ReadOnlySpan<char> authority = value.AsSpan(authorityStart);
        int authorityEnd = authority.IndexOfAny('/', '?', '#');
        authority = authorityEnd < 0 ? authority : authority[..authorityEnd];
        if (authority.IsEmpty || authority.Contains('@'))
        {
            return false;
        }

        // The host as written: a bracketed IPv6 literal, or everything before the port.
        ReadOnlySpan<char> host;
        if (authority[0] == '[')
        {
            int close = authority.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = authority[..(close + 1)];
        }
        else
        {
            int colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || !parsed.IsWellFormedOriginalString()
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.Host.Length == 0
            || parsed.UserInfo.Length != 0
            || !host.Equals(parsed.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    private sealed record Token(string Key, string Value, int Column);

    private sealed class Slot
    {
        public Token? First { get; private set; }

        public Token? Second { get; private set; }

        public int Count { get; private set; }

        public void Add(Token token)
        {
            Count++;
            if (Count == 1)
            {
                First = token;
            }
            else if (Count == 2)
            {
                Second = token;
            }
        }
    }

    private sealed class Interpreter(int line, OutputDiagnosticBag diagnostics)
    {
        private readonly Slot[] _slots = CreateSlots(KeyNames.Length);
        private readonly Slot[] _arguments = CreateSlots(PluginOutputParser.MaxCommandArguments + 1);
        private bool _argumentNameInvalid;
        private int _firstArgumentColumn;

        public ParsedLine Interpret(string text, List<Token> tokens, bool isTitle)
        {
            var others = new List<(Token Token, OtherKind Kind)>();
            foreach (Token token in tokens)
            {
                if (Keys.TryGetValue(token.Key, out Key key))
                {
                    Slot(key).Add(token);
                }
                else if (IsArgumentName(token.Key, out int index, out string? problem))
                {
                    _firstArgumentColumn = _firstArgumentColumn == 0 ? token.Column : _firstArgumentColumn;
                    if (problem is null)
                    {
                        _arguments[index].Add(token);
                    }
                    else
                    {
                        _argumentNameInvalid = true;
                        Add(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.InvalidCommandArguments, $"{QuoteName(token.Key)} is not a valid argument name: {problem}; {NoAction}", token.Column);
                    }
                }
                else
                {
                    others.Add((token, Classify(token.Key)));
                }
            }

            bool declaresAction = Slot(Key.Href).Count > 0 || Slot(Key.Shell).Count > 0 || Slot(Key.Terminal).Count > 0
                || Slot(Key.Refresh).Count > 0 || _argumentNameInvalid || _firstArgumentColumn > 0;

            bool actionValid = ReportOthers(others, declaresAction);
            bool isDisabled = ReadDisabled();
            TextFormat format = ReadFormat();
            bool isChecked = ReadBoolean(Key.Checked, fallback: false);
            bool showInDropdown = ReadDropdown(isTitle);

            OutputAction? action = declaresAction ? ReadAction(actionValid) : null;
            if (action is not null && isDisabled)
            {
                Add(PluginDiagnosticSeverity.Info, OutputDiagnosticCode.DisabledItemAction, "The item is disabled, so its action is not exposed.", column: null);
                action = null;
            }

            return new ParsedLine(text, format, action, isDisabled, isChecked, showInDropdown);
        }

        private static Slot[] CreateSlots(int count)
        {
            var slots = new Slot[count];
            for (int i = 0; i < count; i++)
            {
                slots[i] = new Slot();
            }

            return slots;
        }

        private Slot Slot(Key key) => _slots[(int)key];

        // Reports unsupported and unknown parameters. Returns false if one of them must suppress the action.
        private bool ReportOthers(List<(Token Token, OtherKind Kind)> others, bool declaresAction)
        {
            bool actionValid = true;
            foreach ((Token token, OtherKind kind) in others)
            {
                string name = QuoteName(token.Key);
                switch (kind)
                {
                    case OtherKind.Appearance:
                        Add(PluginDiagnosticSeverity.Info, OutputDiagnosticCode.UnsupportedParameter, $"{name} is not supported (images, ANSI, Markdown, emoji and symbol rendering are not implemented); {Ignored}", token.Column);
                        break;
                    case OtherKind.Behavior when declaresAction:
                        actionValid = false;
                        Add(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.UnsupportedBehavior, $"{name} is not supported and would change how or when the action is invoked; {NoAction}", token.Column);
                        break;
                    case OtherKind.Behavior:
                        Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.UnsupportedBehavior, $"{name} is not supported; {Ignored}", token.Column);
                        break;
                    case OtherKind.Unknown when declaresAction:
                        actionValid = false;
                        Add(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.UnknownParameter, $"{name} is not a recognized parameter, and its effect on the action is unknown; {NoAction}", token.Column);
                        break;
                    default:
                        Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.UnknownParameter, $"{name} is not a recognized parameter; {Ignored}", token.Column);
                        break;
                }
            }

            return actionValid;
        }

        private TextFormat ReadFormat()
        {
            TextColor? color = null;
            if (Read(Key.Color, PluginDiagnosticSeverity.Warning, Ignored, out string colorText, out int colorColumn) == ValueState.Present
                && !TextColor.TryParse(colorText, out color))
            {
                Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.InvalidParameterValue, $"'color' must be a CSS colour name, #RGB or #RRGGBB, or two of them separated by a comma (light,dark); {Ignored}", colorColumn);
            }

            string? font = null;
            if (Read(Key.Font, PluginDiagnosticSeverity.Warning, Ignored, out string fontText, out int fontColumn) == ValueState.Present)
            {
                if (fontText.Length <= MaxFontNameLength && !OutputText.IsBlank(fontText) && OutputText.IsSafeValue(fontText))
                {
                    font = fontText;
                }
                else
                {
                    Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.InvalidParameterValue, $"'font' must be a non-empty font name of at most {MaxFontNameLength} characters without control or bidirectional characters; {Ignored}", fontColumn);
                }
            }

            double? size = null;
            if (Read(Key.Size, PluginDiagnosticSeverity.Warning, Ignored, out string sizeText, out int sizeColumn) == ValueState.Present)
            {
                if (TryParseSize(sizeText, out double parsedSize))
                {
                    size = parsedSize;
                }
                else
                {
                    Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.InvalidParameterValue, $"'size' must be a number from {PluginOutputParser.MinFontSize} to {PluginOutputParser.MaxFontSize} with at most two decimals, written with '.'; {Ignored}", sizeColumn);
                }
            }

            int? length = null;
            if (Read(Key.Length, PluginDiagnosticSeverity.Warning, Ignored, out string lengthText, out int lengthColumn) == ValueState.Present)
            {
                if (TryParseInteger(lengthText, 1, PluginOutputParser.MaxDisplayLength, out int parsedLength))
                {
                    length = parsedLength;
                }
                else
                {
                    Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.InvalidParameterValue, $"'length' must be a whole number from 1 to {PluginOutputParser.MaxDisplayLength}; {Ignored}", lengthColumn);
                }
            }

            string? tooltip = null;
            if (Read(Key.Tooltip, PluginDiagnosticSeverity.Warning, Ignored, out string tooltipText, out int tooltipColumn) == ValueState.Present)
            {
                tooltip = OutputText.ToDisplayText(tooltipText, unescapePipes: false, out int firstUnsafe);
                if (firstUnsafe >= 0)
                {
                    Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.UnsafeText, "The tooltip contains control characters, line separators, invalid UTF-16 or directional formatting characters; they were replaced with U+FFFD or removed.", tooltipColumn);
                }

                tooltip = OutputText.IsInvisible(tooltip) ? null : tooltip;
            }

            bool trim = ReadBoolean(Key.Trim, fallback: true);
            return color is null && font is null && size is null && length is null && tooltip is null && trim
                ? TextFormat.Default
                : new TextFormat { Color = color, Font = font, Size = size, Length = length, Trim = trim, Tooltip = tooltip };
        }

        // An invalid or duplicated disabled parameter counts as disabled, so a line meant to be disabled
        // never becomes clickable because of a typo.
        private bool ReadDisabled()
        {
            const string TreatedAsDisabled = "the item was treated as disabled.";
            ValueState state = Read(Key.Disabled, PluginDiagnosticSeverity.Warning, TreatedAsDisabled, out string text, out int column);
            if (state != ValueState.Present)
            {
                return state == ValueState.Invalid;
            }

            if (TryParseBoolean(text, out bool value))
            {
                return value;
            }

            Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.InvalidParameterValue, $"'disabled' must be true or false; {TreatedAsDisabled}", column);
            return true;
        }

        private bool ReadDropdown(bool isTitle)
        {
            if (!isTitle)
            {
                if (Slot(Key.Dropdown).Count > 0)
                {
                    Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.ParameterNotApplicable, $"'dropdown' applies only to header titles; {Ignored}", Slot(Key.Dropdown).First!.Column);
                }

                return true;
            }

            return ReadBoolean(Key.Dropdown, fallback: true);
        }

        private bool ReadBoolean(Key key, bool fallback)
        {
            if (Read(key, PluginDiagnosticSeverity.Warning, Ignored, out string text, out int column) != ValueState.Present)
            {
                return fallback;
            }

            if (TryParseBoolean(text, out bool value))
            {
                return value;
            }

            Add(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.InvalidParameterValue, $"'{KeyNames[(int)key]}' must be true or false; {Ignored}", column);
            return fallback;
        }

        // Every problem is reported; any problem means no action at all (no partial action, no refresh).
        private OutputAction? ReadAction(bool valid)
        {
            valid &= !_argumentNameInvalid;
            const PluginDiagnosticSeverity Error = PluginDiagnosticSeverity.Error;

            Uri? url = null;
            ValueState hrefState = Read(Key.Href, Error, NoAction, out string href, out int hrefColumn);
            if (hrefState == ValueState.Invalid)
            {
                valid = false;
            }
            else if (hrefState == ValueState.Present && !TryCreateUrl(href, out url))
            {
                valid = false;
                Add(Error, OutputDiagnosticCode.InvalidUrl, $"'href' must be a well-formed absolute http:// or https:// URL with a host and without user information, using only characters allowed in URLs (others must be percent-encoded); {NoAction}", hrefColumn);
            }

            ValueState shellState = Read(Key.Shell, Error, NoAction, out string executable, out int shellColumn);
            if (shellState == ValueState.Invalid)
            {
                valid = false;
            }
            else if (shellState == ValueState.Present && !IsValidExecutable(executable))
            {
                valid = false;
                Add(Error, OutputDiagnosticCode.InvalidCommand, $"'shell'/'bash' must name an executable: not empty, without surrounding white space, control characters or bidirectional controls; {NoAction}", shellColumn);
            }

            bool hasCommand = Slot(Key.Shell).Count > 0;
            if (Slot(Key.Href).Count > 0 && hasCommand)
            {
                valid = false;
                Add(Error, OutputDiagnosticCode.ConflictingActions, $"The line declares both 'href' and 'shell'/'bash'; {NoAction}", Slot(Key.Shell).First!.Column);
            }

            bool? terminal = ReadActionBoolean(Key.Terminal, ref valid);
            bool refresh = ReadActionBoolean(Key.Refresh, ref valid) ?? false;

            if (!hasCommand && (_firstArgumentColumn > 0 || _argumentNameInvalid))
            {
                valid = false;
                Add(Error, OutputDiagnosticCode.OrphanedActionParameter, $"Command arguments (paramN) require 'shell' or 'bash'; {NoAction}", _firstArgumentColumn);
            }

            if (!hasCommand && Slot(Key.Terminal).Count > 0)
            {
                valid = false;
                Add(Error, OutputDiagnosticCode.OrphanedActionParameter, $"'terminal' requires 'shell' or 'bash'; {NoAction}", Slot(Key.Terminal).First!.Column);
            }

            ImmutableArray<string> arguments = [];
            if (hasCommand && !_argumentNameInvalid && !TryReadArguments(out arguments))
            {
                valid = false;
            }

            if (!valid)
            {
                return null;
            }

            if (shellState == ValueState.Present)
            {
                return new CommandAction(executable, arguments, terminal, refresh);
            }

            if (url is not null)
            {
                return new UrlAction(url, refresh);
            }

            return refresh ? RefreshAction.Instance : null;
        }

        private bool? ReadActionBoolean(Key key, ref bool valid)
        {
            ValueState state = Read(key, PluginDiagnosticSeverity.Error, NoAction, out string text, out int column);
            if (state == ValueState.Invalid)
            {
                valid = false;
                return null;
            }

            if (state == ValueState.Absent)
            {
                return null;
            }

            if (TryParseBoolean(text, out bool value))
            {
                return value;
            }

            valid = false;
            Add(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.InvalidParameterValue, $"'{KeyNames[(int)key]}' must be true or false; {NoAction}", column);
            return null;
        }

        // Arguments must be numbered contiguously from param0 or from param1. Gaps, duplicates and unsafe
        // values are rejected rather than shifted or dropped.
        private bool TryReadArguments(out ImmutableArray<string> arguments)
        {
            arguments = [];
            bool valid = true;
            int count = 0;
            int min = -1;
            int max = -1;
            for (int index = 0; index < _arguments.Length; index++)
            {
                Slot slot = _arguments[index];
                if (slot.Count == 0)
                {
                    continue;
                }

                count++;
                min = min < 0 ? index : min;
                max = index;
                string name = "param" + index.ToString(CultureInfo.InvariantCulture);
                if (Read(slot, name, PluginDiagnosticSeverity.Error, NoAction, out string value, out int column) != ValueState.Present)
                {
                    valid = false;
                }
                else if (!OutputText.IsSafeValue(value))
                {
                    valid = false;
                    Add(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.InvalidCommandArguments, $"'{name}' contains control characters, line separators, invalid UTF-16 or bidirectional controls; {NoAction}", column);
                }
            }

            if (count == 0)
            {
                return valid;
            }

            string? problem = min > 1 ? "argument numbering must start at param0 or param1"
                : max - min + 1 != count ? $"arguments must be numbered without gaps from param{min} to param{max}"
                : count > PluginOutputParser.MaxCommandArguments ? $"at most {PluginOutputParser.MaxCommandArguments} arguments are allowed"
                : null;
            if (problem is not null)
            {
                Add(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.InvalidCommandArguments, $"Invalid command arguments: {problem}; {NoAction}", _firstArgumentColumn);
                return false;
            }

            if (!valid)
            {
                return false;
            }

            var builder = ImmutableArray.CreateBuilder<string>(count);
            for (int index = min; index <= max; index++)
            {
                builder.Add(_arguments[index].First!.Value);
            }

            arguments = builder.MoveToImmutable();
            return true;
        }

        private ValueState Read(Key key, PluginDiagnosticSeverity severity, string consequence, out string value, out int column) =>
            Read(Slot(key), KeyNames[(int)key], severity, consequence, out value, out column);

        // A value is usable if its parameter occurs exactly once and the value is within the length limit.
        private ValueState Read(Slot slot, string name, PluginDiagnosticSeverity severity, string consequence, out string value, out int column)
        {
            value = string.Empty;
            column = slot.First?.Column ?? 0;
            if (slot.Count == 0)
            {
                return ValueState.Absent;
            }

            if (slot.Count > 1)
            {
                Add(severity, OutputDiagnosticCode.DuplicateParameter, $"'{name}' appears more than once; {consequence}", slot.Second!.Column);
                return ValueState.Invalid;
            }

            if (slot.First!.Value.Length > PluginOutputParser.MaxValueLength)
            {
                Add(severity, OutputDiagnosticCode.InvalidParameterValue, $"The value of '{name}' is longer than {PluginOutputParser.MaxValueLength} characters; {consequence}", column);
                return ValueState.Invalid;
            }

            value = slot.First.Value;
            return ValueState.Present;
        }

        private void Add(PluginDiagnosticSeverity severity, OutputDiagnosticCode code, string message, int? column) =>
            diagnostics.Add(severity, code, message, line, column);
    }
}
