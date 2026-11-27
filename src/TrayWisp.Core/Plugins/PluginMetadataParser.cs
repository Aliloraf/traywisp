using System.Collections.Immutable;
using System.Text;

namespace TrayWisp.Core.Plugins;

/// <summary>Result of <see cref="PluginMetadataParser.Parse"/>.</summary>
/// <param name="Metadata">The resolved metadata. <see cref="PluginMetadata.Empty"/> when nothing usable was found.</param>
/// <param name="Diagnostics">Problems found while parsing, in a deterministic order.</param>
public sealed record PluginMetadataParseResult(PluginMetadata Metadata, ImmutableArray<PluginDiagnostic> Diagnostics);

/// <summary>
/// Extracts SwiftBar/xbar-style metadata tags such as <c>&lt;xbar.title&gt;Clock&lt;/xbar.title&gt;</c>
/// from comments at the beginning of a plugin file. Comment-shaped lines are recognized with the comment
/// syntax of the plugin's language. This is not a tokenizer: a line inside a multi-line string literal
/// (a here-string, triple-quoted string or here-document) that starts with a comment marker is treated
/// as a comment, so tags can be extracted from it. The content is treated as inert, untrusted descriptive
/// text: nothing is evaluated, no XML parser is used, entities are not decoded and URLs are not fetched.
/// Metadata never establishes identity or approval. See docs/plugin-format.md.
/// </summary>
public static class PluginMetadataParser
{
    /// <summary>Only this many bytes from the start of a file are scanned.</summary>
    public const int MaxScanBytes = 64 * 1024;

    /// <summary>Tags in recognized namespaces beyond this count are ignored.</summary>
    public const int MaxTags = 128;

    /// <summary>Values longer than this, in UTF-16 code units after trimming, are ignored.</summary>
    public const int MaxValueLength = 1024;

    /// <summary>Tag names longer than this are not recognized.</summary>
    public const int MaxTagNameLength = 64;

    // Namespaces in precedence order: a lower index wins.
    private static readonly string[] Namespaces = ["traywisp", "swiftbar", "xbar", "bitbar"];

    private static readonly string[] FieldNames = ["title", "version", "author", "author.github", "desc", "dependencies", "abouturl", "image"];

    // Documented SwiftBar/xbar directives that change behaviour or UI. TrayWisp recognizes them
    // only to report that they have no effect.
    private static readonly string[] SwiftBarDirectives =
    [
        "hideAbout", "hideRunInTerminal", "hideLastUpdated", "hideDisablePlugin", "hideSwiftBar",
        "schedule", "refreshOnOpen", "collapseOnClose", "runInBash", "type", "environment", "persistentWebView",
    ];

    private static readonly string[] XbarDirectives = ["var", "droptypes"];

    private static readonly CommentSyntax HashComments = new(["#"], [], null, null);
    private static readonly CommentSyntax PowerShellComments = new(["#"], [], "<#", "#>");
    private static readonly CommentSyntax JavaScriptComments = new(["//"], [], "/*", "*/");
    private static readonly CommentSyntax BatchComments = new(["::"], ["REM", "@REM"], null, null);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Parses metadata from the first bytes of a plugin file.
    /// </summary>
    /// <param name="content">The beginning of the file. Only the first <see cref="MaxScanBytes"/> bytes are used.</param>
    /// <param name="language">The plugin's language, which determines the comment syntax.</param>
    /// <param name="isTruncated"><see langword="true"/> if the file continues beyond <paramref name="content"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="language"/> is not a defined value.</exception>
    public static PluginMetadataParseResult Parse(ReadOnlySpan<byte> content, PluginLanguage language, bool isTruncated = false)
    {
        CommentSyntax syntax = language switch
        {
            PluginLanguage.PowerShell => PowerShellComments,
            PluginLanguage.Python or PluginLanguage.Bash => HashComments,
            PluginLanguage.JavaScript => JavaScriptComments,
            PluginLanguage.Batch => BatchComments,
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown plugin language."),
        };

        var diagnostics = new List<PluginDiagnostic>();
        if (content.Length > MaxScanBytes)
        {
            content = content[..MaxScanBytes];
            isTruncated = true;
        }

        if (isTruncated)
        {
            diagnostics.Add(new PluginDiagnostic(
                PluginDiagnosticSeverity.Info,
                PluginDiagnosticCode.MetadataScanTruncated,
                $"Only the first {MaxScanBytes} bytes were scanned for metadata; tags after that are ignored."));
        }

        if (!TryDecode(content, isTruncated, diagnostics, out string? text))
        {
            return new PluginMetadataParseResult(PluginMetadata.Empty, [.. diagnostics]);
        }

        if (isTruncated)
        {
            // The last line may be cut off mid-tag, so it is not scanned.
            int lastNewLine = text.LastIndexOf('\n');
            text = lastNewLine >= 0 ? text[..(lastNewLine + 1)] : string.Empty;
        }

        var occurrences = new List<Occurrence>();
        Scan(text, syntax, occurrences, diagnostics);
        PluginMetadata metadata = Resolve(occurrences, diagnostics);
        return new PluginMetadataParseResult(metadata, [.. diagnostics]);
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, bool isTruncated, List<PluginDiagnostic> diagnostics, out string text)
    {
        text = string.Empty;
        Encoding encoding;
        int bomLength;
        string encodingName;

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]) || bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            diagnostics.Add(new PluginDiagnostic(
                PluginDiagnosticSeverity.Warning,
                PluginDiagnosticCode.MetadataUnsupportedEncoding,
                "The file starts with a UTF-32 byte order mark. Only UTF-8 and UTF-16 are supported; metadata was ignored."));
            return false;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            (encoding, bomLength, encodingName) = (StrictUtf8, 3, "UTF-8");
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            (encoding, bomLength, encodingName) = (StrictUtf16LittleEndian, 2, "UTF-16 LE");
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            (encoding, bomLength, encodingName) = (StrictUtf16BigEndian, 2, "UTF-16 BE");
        }
        else
        {
            (encoding, bomLength, encodingName) = (StrictUtf8, 0, "UTF-8");
        }

        ReadOnlySpan<byte> body = bytes[bomLength..];
        char[] chars = new char[encoding.GetMaxCharCount(body.Length)];
        int charCount;
        try
        {
            // When the content is truncated, an incomplete trailing sequence is expected and is
            // not flushed (so it is not reported as invalid).
            charCount = encoding.GetDecoder().GetChars(body, chars, flush: !isTruncated);
        }
        catch (DecoderFallbackException)
        {
            diagnostics.Add(new PluginDiagnostic(
                PluginDiagnosticSeverity.Warning,
                PluginDiagnosticCode.MetadataInvalidEncoding,
                $"The file is not valid {encodingName}; metadata was ignored. Save the file as UTF-8 (with or without BOM) or as UTF-16 with a BOM."));
            return false;
        }

        ReadOnlySpan<char> decoded = chars.AsSpan(0, charCount);
        if (decoded.Contains('\0'))
        {
            diagnostics.Add(new PluginDiagnostic(
                PluginDiagnosticSeverity.Warning,
                PluginDiagnosticCode.MetadataUnsupportedEncoding,
                "The file contains NUL characters (it may be binary, or UTF-16 without a byte order mark); metadata was ignored."));
            return false;
        }

        text = new string(decoded);
        return true;
    }

    private static void Scan(string text, CommentSyntax syntax, List<Occurrence> occurrences, List<PluginDiagnostic> diagnostics)
    {
        int tagCount = 0;
        int lineNumber = 0;
        int start = 0;
        bool inBlockComment = false;
        bool reportedCodeTag = false;
        while (start < text.Length)
        {
            int newLine = text.IndexOf('\n', start);
            int end = newLine < 0 ? text.Length : newLine;
            lineNumber++;

            ReadOnlySpan<char> line = text.AsSpan(start, end - start);
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            start = end + 1;
            if (!TryGetComment(line, syntax, ref inBlockComment, out ReadOnlySpan<char> comment))
            {
                if (!reportedCodeTag && ContainsRecognizedOpeningTag(line))
                {
                    reportedCodeTag = true;
                    diagnostics.Add(Diagnostic(
                        PluginDiagnosticSeverity.Info,
                        PluginDiagnosticCode.MetadataTagOutsideComment,
                        "A metadata tag outside a comment (in code or a string literal) was ignored. Metadata is only read from comments; later occurrences are not reported.",
                        lineNumber));
                }

                continue;
            }

            if (!ScanLine(comment, lineNumber, ref tagCount, occurrences, diagnostics))
            {
                return;
            }
        }
    }

    // Returns the comment text of a line, or false for a code line. A line is a comment if, after
    // leading whitespace, it starts with a line-comment marker, or it is inside a block comment, or it
    // starts a block comment. Comment markers later in a line are not recognized, because they could
    // be inside string literals; this keeps the rule simple and conservative.
    private static bool TryGetComment(ReadOnlySpan<char> line, CommentSyntax syntax, ref bool inBlockComment, out ReadOnlySpan<char> comment)
    {
        if (inBlockComment)
        {
            int close = line.IndexOf(syntax.BlockClose, StringComparison.Ordinal);
            inBlockComment = close < 0;
            comment = close < 0 ? line : line[..close];
            return true;
        }

        ReadOnlySpan<char> trimmed = line.TrimStart();
        foreach (string prefix in syntax.LinePrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                comment = trimmed[prefix.Length..];
                return true;
            }
        }

        // Keywords such as REM must be followed by whitespace or the end of the line.
        foreach (string keyword in syntax.KeywordPrefixes)
        {
            if (trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == keyword.Length || char.IsWhiteSpace(trimmed[keyword.Length])))
            {
                comment = trimmed[keyword.Length..];
                return true;
            }
        }

        if (syntax.BlockOpen is not null && trimmed.StartsWith(syntax.BlockOpen, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> rest = trimmed[syntax.BlockOpen.Length..];
            int close = rest.IndexOf(syntax.BlockClose, StringComparison.Ordinal);
            inBlockComment = close < 0;
            comment = close < 0 ? rest : rest[..close];
            return true;
        }

        comment = default;
        return false;
    }

    // Returns false when the tag limit has been reached and scanning must stop.
    private static bool ScanLine(ReadOnlySpan<char> line, int lineNumber, ref int tagCount, List<Occurrence> occurrences, List<PluginDiagnostic> diagnostics)
    {
        int position = 0;
        while (position < line.Length)
        {
            int offset = line[position..].IndexOf('<');
            if (offset < 0)
            {
                return true;
            }

            int open = position + offset;
            int nameStart = open + 1;
            int nameEnd = ReadTagName(line, nameStart);
            ReadOnlySpan<char> tagName = line[nameStart..nameEnd];
            int namespaceRank = MatchNamespace(tagName, out int fieldStart);
            if (namespaceRank < 0)
            {
                // Not one of our tags (HTML, generics, comparisons...). Ignore silently.
                position = open + 1;
                continue;
            }

            tagCount++;
            if (tagCount > MaxTags)
            {
                diagnostics.Add(Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.MetadataTagLimitReached,
                    $"More than {MaxTags} metadata tags were found; the rest of the file was not scanned.",
                    lineNumber));
                return false;
            }

            if (tagName.Length > MaxTagNameLength || nameEnd >= line.Length || line[nameEnd] != '>')
            {
                diagnostics.Add(Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.MetadataMalformedTag,
                    $"A metadata tag starting with '<{Truncate(tagName)}' is malformed. Tags take the form <namespace.field>value</namespace.field> on one line, without attributes.",
                    lineNumber));
                position = nameEnd;
                continue;
            }

            string tag = tagName.ToString();
            int valueStart = nameEnd + 1;
            string closingTag = "</" + tag + ">";
            int closeOffset = line[valueStart..].IndexOf(closingTag, StringComparison.OrdinalIgnoreCase);
            if (closeOffset < 0)
            {
                diagnostics.Add(Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.MetadataMalformedTag,
                    $"The tag <{tag}> has no matching {closingTag} on the same line.",
                    lineNumber));
                position = valueStart;
                continue;
            }

            ReadOnlySpan<char> rawValue = line.Slice(valueStart, closeOffset);
            if (ContainsTagMarkup(rawValue))
            {
                diagnostics.Add(Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.MetadataMalformedTag,
                    $"The value of <{tag}> contains another tag; nested tags are not supported.",
                    lineNumber));
                position = valueStart;
                continue;
            }

            HandleTag(tag, namespaceRank, tag[fieldStart..], rawValue.Trim(), lineNumber, occurrences, diagnostics);
            position = valueStart + closeOffset + closingTag.Length;
        }

        return true;
    }

    private static void HandleTag(string tag, int namespaceRank, string field, ReadOnlySpan<char> value, int lineNumber, List<Occurrence> occurrences, List<PluginDiagnostic> diagnostics)
    {
        int fieldIndex = IndexOf(FieldNames, field);
        if (fieldIndex < 0)
        {
            bool isDirective = Namespaces[namespaceRank] switch
            {
                "swiftbar" => IndexOf(SwiftBarDirectives, field) >= 0,
                "xbar" or "bitbar" => IndexOf(XbarDirectives, field) >= 0,
                _ => false,
            };

            diagnostics.Add(isDirective
                ? Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.MetadataUnsupportedDirective,
                    $"<{tag}> is a known SwiftBar/xbar directive that TrayWisp does not support; it has no effect.",
                    lineNumber)
                : Diagnostic(
                    PluginDiagnosticSeverity.Info,
                    PluginDiagnosticCode.MetadataUnknownField,
                    $"<{tag}> is not a recognized metadata field and was ignored.",
                    lineNumber));
            return;
        }

        if (value.IsEmpty)
        {
            diagnostics.Add(Diagnostic(
                PluginDiagnosticSeverity.Info,
                PluginDiagnosticCode.MetadataEmptyValue,
                $"<{tag}> is empty and was treated as absent.",
                lineNumber));
            return;
        }

        if (value.Length > MaxValueLength)
        {
            diagnostics.Add(Diagnostic(
                PluginDiagnosticSeverity.Warning,
                PluginDiagnosticCode.MetadataValueTooLong,
                $"The value of <{tag}> is longer than {MaxValueLength} characters and was ignored.",
                lineNumber));
            return;
        }

        foreach (char c in value)
        {
            if (char.IsControl(c) && c != '\t')
            {
                diagnostics.Add(Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.MetadataInvalidValue,
                    $"The value of <{tag}> contains control characters and was ignored.",
                    lineNumber));
                return;
            }
        }

        occurrences.Add(new Occurrence(fieldIndex, namespaceRank, tag, value.ToString(), lineNumber));
    }

    // For each field: the highest-precedence namespace wins, and within a namespace the first
    // occurrence wins. Ignored occurrences with a different value are reported.
    private static PluginMetadata Resolve(List<Occurrence> occurrences, List<PluginDiagnostic> diagnostics)
    {
        string?[] values = new string?[FieldNames.Length];
        for (int field = 0; field < FieldNames.Length; field++)
        {
            Occurrence? chosen = null;
            foreach (Occurrence occurrence in occurrences)
            {
                if (occurrence.Field == field && (chosen is null || occurrence.NamespaceRank < chosen.NamespaceRank))
                {
                    chosen = occurrence;
                }
            }

            if (chosen is null)
            {
                continue;
            }

            values[field] = chosen.Value;
            foreach (Occurrence occurrence in occurrences)
            {
                if (occurrence.Field != field || ReferenceEquals(occurrence, chosen) || string.Equals(occurrence.Value, chosen.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                diagnostics.Add(occurrence.NamespaceRank == chosen.NamespaceRank
                    ? Diagnostic(
                        PluginDiagnosticSeverity.Warning,
                        PluginDiagnosticCode.MetadataFieldConflict,
                        $"<{occurrence.Tag}> conflicts with an earlier <{chosen.Tag}> on line {chosen.Line}; the first value is used.",
                        occurrence.Line)
                    : Diagnostic(
                        PluginDiagnosticSeverity.Info,
                        PluginDiagnosticCode.MetadataFieldOverridden,
                        $"<{occurrence.Tag}> is overridden by <{chosen.Tag}> on line {chosen.Line} (namespace precedence: traywisp, swiftbar, xbar, bitbar).",
                        occurrence.Line));
            }
        }

        return new PluginMetadata
        {
            Title = values[0],
            Version = values[1],
            Author = values[2],
            AuthorGitHub = values[3],
            Description = values[4],
            Dependencies = values[5],
            AboutUrl = values[6],
            Image = values[7],
        };
    }

    // Returns the namespace precedence rank, or -1 if the name is not "<namespace>.<field>".
    private static int MatchNamespace(ReadOnlySpan<char> tagName, out int fieldStart)
    {
        for (int rank = 0; rank < Namespaces.Length; rank++)
        {
            string ns = Namespaces[rank];
            if (tagName.Length > ns.Length + 1
                && tagName[ns.Length] == '.'
                && tagName.StartsWith(ns, StringComparison.OrdinalIgnoreCase))
            {
                fieldStart = ns.Length + 1;
                return rank;
            }
        }

        fieldStart = 0;
        return -1;
    }

    // True if the value contains a closing tag or an opening tag in a recognized namespace.
    private static bool ContainsTagMarkup(ReadOnlySpan<char> value) =>
        value.Contains("</", StringComparison.Ordinal) || ContainsRecognizedOpeningTag(value);

    private static bool ContainsRecognizedOpeningTag(ReadOnlySpan<char> text)
    {
        int position = 0;
        while (position < text.Length)
        {
            int offset = text[position..].IndexOf('<');
            if (offset < 0)
            {
                return false;
            }

            int nameStart = position + offset + 1;
            if (MatchNamespace(text[nameStart..ReadTagName(text, nameStart)], out _) >= 0)
            {
                return true;
            }

            position = nameStart;
        }

        return false;
    }

    // Returns the end of the tag name starting at nameStart, reading at most MaxTagNameLength + 1
    // characters so that overlong names can be detected without scanning further.
    private static int ReadTagName(ReadOnlySpan<char> text, int nameStart)
    {
        int nameEnd = nameStart;
        while (nameEnd < text.Length && nameEnd - nameStart <= MaxTagNameLength && IsTagNameCharacter(text[nameEnd]))
        {
            nameEnd++;
        }

        return nameEnd;
    }

    private static bool IsTagNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    private static int IndexOf(string[] names, string name)
    {
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Truncate(ReadOnlySpan<char> tagName) =>
        tagName.Length > MaxTagNameLength ? string.Concat(tagName[..MaxTagNameLength], "...") : tagName.ToString();

    private static PluginDiagnostic Diagnostic(PluginDiagnosticSeverity severity, PluginDiagnosticCode code, string message, int line) =>
        new(severity, code, message) { Line = line };

    private sealed record Occurrence(int Field, int NamespaceRank, string Tag, string Value, int Line);

    private sealed record CommentSyntax(string[] LinePrefixes, string[] KeywordPrefixes, string? BlockOpen, string? BlockClose);
}
