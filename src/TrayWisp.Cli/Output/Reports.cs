using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Cli.Output;

// What a command produced: its exit code, diagnostics, and how to render its result as JSON and as text.
internal sealed class CommandResult
{
    public int ExitCode { get; set; }

    public List<CliDiagnostic> Diagnostics { get; } = [];

    // Writes the value of the JSON "result" property (an object), or nothing for null.
    public Action<Utf8JsonWriter>? Json { get; set; }

    public Action<HumanWriter>? Human { get; set; }

    // One line written to standard error in text mode when the exit code is not 0.
    public string? Summary { get; set; }

    public void Add(CliDiagnostic diagnostic) => Diagnostics.Add(diagnostic);

    public void AddRange(IEnumerable<CliDiagnostic> diagnostics) => Diagnostics.AddRange(diagnostics);
}

// Plain, line-oriented text. Every untrusted value goes through TerminalText; labels are TrayWisp's own fixed text, so a
// value can never start a line or imitate a label, a heading or a diagnostic.
internal sealed class HumanWriter
{
    private readonly StringBuilder _text = new();
    private int _indent;

    public void Line(string trustedText = "")
    {
        if (trustedText.Length > 0)
        {
            _text.Append(' ', _indent * 2);
        }

        _text.Append(trustedText).Append('\n');
    }

    // An untrusted value, by default an identity or path, which is shown in full (see TerminalText.DefaultLimit).
    public void Field(string label, string? untrustedValue, TerminalText.Kind kind = TerminalText.Kind.Identity, int? maxLength = null) =>
        Line(untrustedValue is null ? $"{label}: (none)" : $"{label}: {TerminalText.Inline(untrustedValue, maxLength ?? TerminalText.DefaultLimit(kind), kind)}");

    // A list entry: a fixed prefix (such as "-" or "[1]") followed by an untrusted value.
    public void Entry(string prefix, string? untrustedValue, TerminalText.Kind kind = TerminalText.Kind.Identity, int? maxLength = null) =>
        Line($"{prefix} {TerminalText.Inline(untrustedValue, maxLength ?? TerminalText.DefaultLimit(kind), kind)}");

    // A value TrayWisp formatted itself from trusted data (numbers, enum names, fixed words).
    public void Value(string label, string trustedValue) => Line($"{label}: {trustedValue}");

    public IDisposable Indent()
    {
        _indent++;
        return new Unindent(this);
    }

    public void Diagnostics(IReadOnlyCollection<CliDiagnostic> diagnostics, string heading = "Diagnostics")
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        Line($"{heading}:");
        using (Indent())
        {
            foreach (CliDiagnostic diagnostic in diagnostics)
            {
                Line(Format(diagnostic));
            }
        }
    }

    public static string Format(CliDiagnostic diagnostic)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{diagnostic.Severity} TW{diagnostic.Code} [{diagnostic.Source}]");
        if (diagnostic.Path is not null)
        {
            // Quote doubles backslashes and escapes control and format characters; Inline then escapes the remaining
            // invisible characters (default-ignorable, Braille blank, non-ASCII spaces, supplementary format characters).
            builder.Append(' ').Append(TerminalText.Inline(PluginDiagnosticText.Quote(diagnostic.Path, PluginDiagnostic.MaxPathLengthInText), int.MaxValue, TerminalText.Kind.Text));
        }

        if (diagnostic.Line is int line)
        {
            builder.Append(CultureInfo.InvariantCulture, $" line {line}");
            if (diagnostic.Column is int column)
            {
                builder.Append(CultureInfo.InvariantCulture, $":{column}");
            }
        }

        builder.Append(": ").Append(TerminalText.Inline(diagnostic.Message, 2048, TerminalText.Kind.Text));
        return builder.ToString();
    }

    public override string ToString() => _text.ToString();

    private sealed class Unindent(HumanWriter writer) : IDisposable
    {
        public void Dispose() => writer._indent--;
    }
}

// The versioned JSON document (docs/cli.md, "JSON output"). Written to memory first and emitted in one piece, so standard
// output never holds a partial or second document. JavaScriptEncoder.Default escapes every non-ASCII and HTML-sensitive
// character, so the document is pure ASCII and contains no control characters.
internal static class JsonReport
{
    public const int SchemaVersion = 1;

    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.Default,
        MaxDepth = 64,
    };

    public static string Write(string? command, int exitCode, Action<Utf8JsonWriter>? result, IReadOnlyCollection<CliDiagnostic> diagnostics)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.SafeString("command", command);
            writer.WriteBoolean("success", exitCode == ExitCodes.Success);
            writer.WriteNumber("exitCode", exitCode);
            writer.WritePropertyName("result");
            if (result is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                result(writer);
            }

            writer.WritePropertyName("diagnostics");
            WriteDiagnostics(writer, diagnostics);
            writer.WriteEndObject();
        }

        return Encoding.ASCII.GetString(memory.ToArray()) + "\n";
    }

    public static void WriteDiagnostics(Utf8JsonWriter writer, IEnumerable<CliDiagnostic> diagnostics)
    {
        writer.WriteStartArray();
        foreach (CliDiagnostic diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("source", diagnostic.Source);
            writer.WriteString("code", string.Create(CultureInfo.InvariantCulture, $"TW{diagnostic.Code}"));
            writer.WriteString("severity", Severity(diagnostic.Severity));
            writer.SafeString("message", diagnostic.Message);
            writer.SafeString("path", diagnostic.Path);
            writer.NullableNumber("line", diagnostic.Line);
            writer.NullableNumber("column", diagnostic.Column);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static string Severity(PluginDiagnosticSeverity severity) => severity switch
    {
        PluginDiagnosticSeverity.Info => "info",
        PluginDiagnosticSeverity.Warning => "warning",
        _ => "error",
    };

    // Writes a string property, or null. JSON cannot carry unpaired UTF-16 surrogates (System.Text.Json refuses them), so
    // each one is replaced with U+FFFD; such identities cannot be approved anyway (TW510).
    public static void SafeString(this Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, WellFormed(value));
        }
    }

    public static void SafeStringValue(this Utf8JsonWriter writer, string value) => writer.WriteStringValue(WellFormed(value));

    public static void NullableNumber(this Utf8JsonWriter writer, string name, long? value)
    {
        if (value is long number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    public static void NullableEnum<T>(this Utf8JsonWriter writer, string name, T? value)
        where T : struct, Enum
    {
        if (value is T member)
        {
            writer.WriteString(name, member.ToString());
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    public static void Timestamp(this Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is DateTimeOffset time)
        {
            writer.WriteString(name, time.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string WellFormed(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return string.Create(value.Length, value, static (span, source) =>
                {
                    for (int j = 0; j < source.Length; j++)
                    {
                        char c = source[j];
                        if (char.IsHighSurrogate(c) && j + 1 < source.Length && char.IsLowSurrogate(source[j + 1]))
                        {
                            span[j] = c;
                            span[j + 1] = source[j + 1];
                            j++;
                        }
                        else
                        {
                            span[j] = char.IsSurrogate(c) ? '\uFFFD' : c;
                        }
                    }
                });
            }
        }

        return value;
    }
}
