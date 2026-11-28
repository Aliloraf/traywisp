using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Unicode;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Diagnostics;

// Strict reader and deterministic writer of the versioned run history (docs/diagnostics.md, "Format"). The reader
// accepts a history only if every byte of it is valid, and never returns part of one. The writer serializes each record
// separately, so record and total sizes are measured exactly before anything is written.
//
// Record size (docs/diagnostics.md, "Bounds"): a record is measured in UTF-8 bytes, both as stored (its JSON object from
// '{' to '}', including any whitespace inside it, excluding the separators between records) and in its canonical form
// (compact, exactly as SerializeRecord writes it). The writer only writes canonical records, so for its records the two
// are equal; the reader requires both to be at most PluginRunHistory.MaxRecordBytes, so a record read from a file can
// always be written back within the limit.
//
// Malformed Unicode fails closed: invalid UTF-8 anywhere in the file is rejected before parsing, and strings or property
// names holding escaped unpaired surrogates (which System.Text.Json parses, but cannot decode: GetString, Name,
// NameEquals and ValueEquals all throw InvalidOperationException for them) are rejected as invalid values. Names and
// strings are only ever examined through TryGetName and TryGetString, which guard exactly the decoding call.
internal static class RunHistorySerializer
{
    public const int SchemaVersion = 1;
    public const int MaxDepth = 8;

    private const string TimestampFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

    private static readonly byte[] Header = "{\"schemaVersion\":1,\"records\":["u8.ToArray();
    private static readonly byte[] Footer = "]}\n"u8.ToArray();

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = MaxDepth,
    };

    private static readonly string[] RecordProperties =
    [
        "recordedAtUtc", "startedAtUtc", "rootId", "pluginId", "status", "trustStatus", "exitCode", "elapsedMilliseconds",
        "terminationReason", "cleanupStatus", "jobObjectAssigned", "remainingJobProcesses", "stdoutBytes", "stdoutComplete",
        "stdoutTruncated", "stderrBytes", "stderrTruncated", "interpreterKind", "interpreterSource", "publication",
        "publicationTrustStatus", "diagnostics", "diagnosticsTruncated",
    ];

    // The size of a history holding records of the given serialized sizes.
    public static long TotalSize(IEnumerable<int> recordSizes)
    {
        // Separators: "\n" after the header, ",\n" between records and "\n" before the footer, so two bytes per record.
        long total = Header.Length + Footer.Length;
        foreach (int size in recordSizes)
        {
            total += size + 2;
        }

        return total;
    }

    public static byte[] Serialize(IReadOnlyList<byte[]> records)
    {
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write(Header);
        for (int i = 0; i < records.Count; i++)
        {
            buffer.Write(i == 0 ? "\n"u8 : ",\n"u8);
            buffer.Write(records[i]);
        }

        if (records.Count > 0)
        {
            buffer.Write("\n"u8);
        }

        buffer.Write(Footer);
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] SerializeRecord(PluginRunRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, MaxDepth = MaxDepth }))
        {
            writer.WriteStartObject();
            writer.WriteString("recordedAtUtc", FormatTimestamp(record.RecordedAtUtc));
            WriteNullableString(writer, "startedAtUtc", record.StartedAtUtc is { } started ? FormatTimestamp(started) : null);
            writer.WriteString("rootId", record.RootId);
            writer.WriteString("pluginId", record.PluginId.Value);
            writer.WriteString("status", record.Status.ToString());
            WriteNullableString(writer, "trustStatus", record.TrustStatus?.ToString());
            WriteNullableNumber(writer, "exitCode", record.ExitCode);
            writer.WriteNumber("elapsedMilliseconds", record.ElapsedMilliseconds);
            writer.WriteString("terminationReason", record.TerminationReason.ToString());
            writer.WriteString("cleanupStatus", record.CleanupStatus.ToString());
            writer.WriteBoolean("jobObjectAssigned", record.JobObjectAssigned);
            WriteNullableNumber(writer, "remainingJobProcesses", record.RemainingJobProcesses);
            writer.WriteNumber("stdoutBytes", record.StandardOutputBytes);
            writer.WriteBoolean("stdoutComplete", record.StandardOutputComplete);
            writer.WriteBoolean("stdoutTruncated", record.StandardOutputTruncated);
            writer.WriteNumber("stderrBytes", record.StandardErrorBytes);
            writer.WriteBoolean("stderrTruncated", record.StandardErrorTruncated);
            WriteNullableString(writer, "interpreterKind", record.InterpreterKind?.ToString());
            WriteNullableString(writer, "interpreterSource", record.InterpreterSource?.ToString());
            writer.WriteString("publication", record.Publication.ToString());
            WriteNullableString(writer, "publicationTrustStatus", record.PublicationTrustStatus?.ToString());
            writer.WriteStartArray("diagnostics");
            foreach (RecordedDiagnostic diagnostic in record.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteNumber("code", diagnostic.Code);
                writer.WriteString("severity", diagnostic.Severity.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("diagnosticsTruncated", record.DiagnosticsTruncated);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    // All or nothing: on success every record, in file order; on any problem a diagnostic and no records.
    public static StorageDiagnostic? Parse(ReadOnlySpan<byte> utf8, out List<PluginRunRecord> records)
    {
        records = [];

        // System.Text.Json validates UTF-8 only when a string is decoded, so the whole file is checked first.
        if (!Utf8.IsValid(utf8))
        {
            return Corrupt("The run history is not valid UTF-8.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray(), DocumentOptions);
        }
        catch (JsonException exception)
        {
            // The exception message can quote file content, so only the position is reported.
            string position = exception.LineNumber is long line && exception.BytePositionInLine is long column
                ? string.Create(CultureInfo.InvariantCulture, $" at line {line + 1}, byte {column + 1}")
                : string.Empty;
            return Corrupt($"The run history is not valid JSON{position}, or is nested more than {MaxDepth} levels deep.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Corrupt("The run history is not a JSON object.");
            }

            // The version is examined first, so that a history written by a newer TrayWisp is reported as unsupported.
            // Every top-level name is decoded here, before anything else is examined.
            JsonElement? version = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!TryGetName(property, out string? name))
                {
                    return Corrupt("The run history has a top-level property name that is not valid Unicode.");
                }

                if (name == "schemaVersion")
                {
                    if (version is not null)
                    {
                        return Corrupt("The run history has more than one 'schemaVersion' property.");
                    }

                    version = property.Value;
                }
            }

            if (version is not { } versionElement)
            {
                return Corrupt("The run history has no 'schemaVersion' property.");
            }

            if (versionElement.ValueKind != JsonValueKind.Number || !TryCanonicalInt(versionElement, out long versionNumber) || versionNumber < 1)
            {
                return Corrupt("The run history's 'schemaVersion' is not a positive integer.");
            }

            if (versionNumber != SchemaVersion)
            {
                return new StorageDiagnostic(
                    PluginDiagnosticSeverity.Error,
                    StorageDiagnosticCode.HistoryUnsupportedVersion,
                    string.Create(CultureInfo.InvariantCulture, $"The run history has schema version {versionNumber}, but this version of TrayWisp only supports version {SchemaVersion}. It was not changed."));
            }

            var parsed = new List<PluginRunRecord>();
            bool seenRecords = false;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                string name = TryGetName(property, out string? decoded) ? decoded : string.Empty;
                if (name == "schemaVersion")
                {
                    continue;
                }

                if (name != "records")
                {
                    return Corrupt("The run history has an unknown top-level property. Version 1 allows only 'schemaVersion' and 'records'.");
                }

                if (seenRecords)
                {
                    return Corrupt("The run history has more than one 'records' property.");
                }

                seenRecords = true;
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    return Corrupt("The run history's 'records' is not an array.");
                }

                int index = 0;
                foreach (JsonElement element in property.Value.EnumerateArray())
                {
                    index++;
                    if (index > PluginRunHistory.MaxRecords)
                    {
                        return new StorageDiagnostic(
                            PluginDiagnosticSeverity.Error,
                            StorageDiagnosticCode.HistoryTooManyRecords,
                            $"The run history has more than {PluginRunHistory.MaxRecords} records. It was not changed.");
                    }

                    if (JsonMarshal.GetRawUtf8Value(element).Length > PluginRunHistory.MaxRecordBytes)
                    {
                        return Corrupt(string.Create(CultureInfo.InvariantCulture, $"Record {index} of the run history is larger than {PluginRunHistory.MaxRecordBytes} bytes."));
                    }

                    string? problem = ReadRecord(element, out PluginRunRecord? record);
                    if (problem is not null)
                    {
                        return Corrupt(string.Create(CultureInfo.InvariantCulture, $"Record {index} of the run history is invalid: {problem}."));
                    }

                    if (SerializeRecord(record!).Length > PluginRunHistory.MaxRecordBytes)
                    {
                        return Corrupt(string.Create(CultureInfo.InvariantCulture, $"Record {index} of the run history would be larger than {PluginRunHistory.MaxRecordBytes} bytes in its canonical form."));
                    }

                    parsed.Add(record!);
                }
            }

            if (!seenRecords)
            {
                return Corrupt("The run history has no 'records' property.");
            }

            records = parsed;
            return null;
        }
    }

    private static string? ReadRecord(JsonElement element, out PluginRunRecord? record)
    {
        record = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "it is not an object";
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!TryGetName(property, out string? name))
            {
                return "a property name is not valid Unicode";
            }

            if (Array.IndexOf(RecordProperties, name) < 0)
            {
                return "it has an unknown property";
            }

            if (!values.TryAdd(name, property.Value))
            {
                return "it has a duplicate property";
            }
        }

        if (values.Count != RecordProperties.Length)
        {
            return "it is missing a property";
        }

        if (!TryTimestamp(values["recordedAtUtc"], nullable: false, out DateTimeOffset? recordedAt)
            || !TryTimestamp(values["startedAtUtc"], nullable: true, out DateTimeOffset? startedAt))
        {
            return "a timestamp is invalid";
        }

        if (!TryGetString(values["rootId"], out string? rootId)
            || rootId.Length != 32
            || !rootId.All(char.IsAsciiHexDigitLower))
        {
            return "'rootId' is invalid";
        }

        if (!TryGetString(values["pluginId"], out string? idText)
            || !PluginId.TryCreate(idText, out PluginId? pluginId)
            || !string.Equals(pluginId.Value, idText, StringComparison.Ordinal)
            || !PluginTrustKey.IsSupportedPluginId(pluginId))
        {
            return "'pluginId' is invalid";
        }

        if (!TryEnum(values["status"], nullable: false, out PluginExecutionStatus? status)
            || !TryEnum(values["trustStatus"], nullable: true, out PluginTrustStatus? trustStatus)
            || !TryEnum(values["terminationReason"], nullable: false, out ExecutionTerminationReason? termination)
            || !TryEnum(values["cleanupStatus"], nullable: false, out ProcessCleanupStatus? cleanup)
            || !TryEnum(values["interpreterKind"], nullable: true, out InterpreterKind? kind)
            || !TryEnum(values["interpreterSource"], nullable: true, out InterpreterSource? source)
            || !TryEnum(values["publication"], nullable: false, out RunPublication? publication)
            || !TryEnum(values["publicationTrustStatus"], nullable: true, out PluginTrustStatus? publicationTrust))
        {
            return "a status value is invalid";
        }

        if (!TryNumber(values["exitCode"], nullable: true, int.MinValue, int.MaxValue, out long? exitCode)
            || !TryNumber(values["elapsedMilliseconds"], nullable: false, 0, long.MaxValue, out long? elapsed)
            || !TryNumber(values["remainingJobProcesses"], nullable: true, 0, int.MaxValue, out long? remaining)
            || !TryNumber(values["stdoutBytes"], nullable: false, 0, long.MaxValue, out long? stdoutBytes)
            || !TryNumber(values["stderrBytes"], nullable: false, 0, long.MaxValue, out long? stderrBytes))
        {
            return "a number is invalid";
        }

        if (!TryBoolean(values["jobObjectAssigned"], out bool jobAssigned)
            || !TryBoolean(values["stdoutComplete"], out bool stdoutComplete)
            || !TryBoolean(values["stdoutTruncated"], out bool stdoutTruncated)
            || !TryBoolean(values["stderrTruncated"], out bool stderrTruncated)
            || !TryBoolean(values["diagnosticsTruncated"], out bool diagnosticsTruncated))
        {
            return "a Boolean value is invalid";
        }

        string? diagnosticsProblem = ReadDiagnostics(values["diagnostics"], out ImmutableArray<RecordedDiagnostic> diagnostics);
        if (diagnosticsProblem is not null)
        {
            return diagnosticsProblem;
        }

        record = new PluginRunRecord(
            recordedAt!.Value,
            startedAt,
            rootId,
            pluginId,
            status!.Value,
            trustStatus,
            (int?)exitCode,
            elapsed!.Value,
            termination!.Value,
            cleanup!.Value,
            jobAssigned,
            (int?)remaining,
            stdoutBytes!.Value,
            stdoutComplete,
            stdoutTruncated,
            stderrBytes!.Value,
            stderrTruncated,
            kind,
            source,
            publication!.Value,
            publicationTrust,
            diagnostics,
            diagnosticsTruncated);
        return null;
    }

    private static string? ReadDiagnostics(JsonElement element, out ImmutableArray<RecordedDiagnostic> diagnostics)
    {
        diagnostics = [];
        if (element.ValueKind != JsonValueKind.Array)
        {
            return "'diagnostics' is not an array";
        }

        var builder = ImmutableArray.CreateBuilder<RecordedDiagnostic>();
        var seen = new HashSet<RecordedDiagnostic>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (builder.Count >= PluginRunHistory.MaxDiagnosticCodesPerRecord)
            {
                return "it has too many diagnostic codes";
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                return "a diagnostic is not an object";
            }

            JsonElement? code = null;
            JsonElement? severity = null;
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (!TryGetName(property, out string? name))
                {
                    return "a diagnostic property name is not valid Unicode";
                }

                if (name == "code" && code is null)
                {
                    code = property.Value;
                }
                else if (name == "severity" && severity is null)
                {
                    severity = property.Value;
                }
                else
                {
                    return "a diagnostic has an unknown or duplicate property";
                }
            }

            if (code is not { } codeElement || severity is not { } severityElement
                || !TryNumber(codeElement, nullable: false, 0, 9999, out long? number)
                || !TryEnum(severityElement, nullable: false, out PluginDiagnosticSeverity? level))
            {
                return "a diagnostic is invalid";
            }

            var recorded = new RecordedDiagnostic((int)number!.Value, level!.Value);
            if (!seen.Add(recorded))
            {
                return "a diagnostic code is repeated";
            }

            builder.Add(recorded);
        }

        diagnostics = builder.ToImmutable();
        return null;
    }

    private static bool TryTimestamp(JsonElement element, bool nullable, out DateTimeOffset? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return nullable;
        }

        if (!TryGetString(element, out string? text))
        {
            return false;
        }

        if (!DateTime.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            return false;
        }

        var result = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        value = result;
        return string.Equals(FormatTimestamp(result), text, StringComparison.Ordinal);
    }

    // Only the exact member names are accepted: no numbers, no other case, no combined flags.
    private static bool TryEnum<T>(JsonElement element, bool nullable, out T? value)
        where T : struct, Enum
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return nullable;
        }

        if (!TryGetString(element, out string? text))
        {
            return false;
        }

        foreach (T candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        return false;
    }

    // A JSON string, decoded. False for any other kind, and for a string holding an escaped unpaired surrogate, which has no
    // UTF-16 representation (System.Text.Json throws InvalidOperationException for it; invalid UTF-8 was rejected before
    // parsing, so nothing else can make the decoding fail).
    private static bool TryGetString(JsonElement element, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            value = element.GetString();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return value is not null;
    }

    // A property name, decoded, with the same rule as TryGetString.
    private static bool TryGetName(JsonProperty property, [NotNullWhen(true)] out string? name)
    {
        name = null;
        try
        {
            name = property.Name;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return true;
    }

    private static bool TryNumber(JsonElement element, bool nullable, long min, long max, out long? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return nullable;
        }

        if (element.ValueKind != JsonValueKind.Number || !TryCanonicalInt(element, out long number) || number < min || number > max)
        {
            return false;
        }

        value = number;
        return true;
    }

    // An integer written as the writer writes it: no fraction, exponent, sign on zero or leading zeros.
    private static bool TryCanonicalInt(JsonElement element, out long value) =>
        element.TryGetInt64(out value) && string.Equals(element.GetRawText(), value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool TryBoolean(JsonElement element, out bool value)
    {
        value = element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static StorageDiagnostic Corrupt(string message) =>
        new(PluginDiagnosticSeverity.Error, StorageDiagnosticCode.HistoryCorrupt, message + " The history is never repaired or overwritten automatically.");
}
