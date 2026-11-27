using System.Buffers;
using System.Globalization;
using System.Text.Json;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Trust;

// Strict reader and deterministic writer for the versioned approval store (docs/trust-model.md, "Storage").
// The reader accepts a store only if every byte of it is valid; it never returns part of a store.
internal static class ApprovalStoreSerializer
{
    public const int SchemaVersion = 1;
    public const int MaxDepth = 16;

    private const string TimestampFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

    // A JSON string escape can take up to 6 bytes per UTF-16 code unit (\uXXXX).
    private const int MaxEncodedStringBytes = 6 * PluginRoot.MaxLength;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        AllowMultipleValues = false,
        MaxDepth = MaxDepth,
    };

    // All or nothing: on success, every approval of the store; on any problem, a diagnostic and an empty
    // collection, however much of the store was read before the problem was found.
    public static TrustDiagnostic? Parse(ReadOnlySpan<byte> utf8, out List<PluginApproval> approvals)
    {
        var parsed = new List<PluginApproval>();
        TrustDiagnostic? problem;
        try
        {
            problem = ReadSchemaVersion(utf8) ?? ReadVersion1(utf8, parsed);
        }
        catch (JsonException exception)
        {
            // The exception message can quote store content, so only its position is reported.
            string position = exception.LineNumber is long line && exception.BytePositionInLine is long column
                ? string.Create(CultureInfo.InvariantCulture, $" at line {line + 1}, byte {column + 1}")
                : string.Empty;
            problem = Invalid($"The trust store is not valid JSON{position}, or is nested more than {MaxDepth} levels deep.");
        }

        approvals = problem is null ? parsed : [];
        return problem;
    }

    public static byte[] Serialize(IEnumerable<PluginApproval> approvals)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n", MaxDepth = MaxDepth }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("approvals");
            foreach (PluginApproval approval in approvals.OrderBy(approval => approval.Key))
            {
                writer.WriteStartObject();
                writer.WriteString("root", approval.Key.Root.Value);
                writer.WriteString("pluginId", approval.Key.Id.Value);
                writer.WriteString("sha256", approval.Fingerprint.Value);
                writer.WriteString("generation", approval.Generation.Value);
                writer.WriteString("approvedAtUtc", FormatTimestamp(approval.ApprovedAtUtc));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    public static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    // First pass: find schemaVersion without interpreting anything else, so that a store written by a newer
    // version is reported as unsupported rather than as invalid. The whole document is still syntax-checked.
    private static TrustDiagnostic? ReadSchemaVersion(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return Invalid("The trust store is not a JSON object.");
        }

        int? version = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isVersion = reader.ValueTextEquals("schemaVersion"u8);
            reader.Read();
            if (!isVersion)
            {
                reader.Skip();
                continue;
            }

            if (version is not null)
            {
                return Invalid("The trust store has more than one 'schemaVersion' property.");
            }

            if (!TryReadVersion(ref reader, out int value))
            {
                return Invalid("The trust store's 'schemaVersion' is not a positive integer.");
            }

            version = value;
        }

        if (reader.Read())
        {
            return Invalid("The trust store has content after its top-level object.");
        }

        return version switch
        {
            null => Invalid("The trust store has no 'schemaVersion' property."),
            SchemaVersion => null,
            _ => new TrustDiagnostic(
                PluginDiagnosticSeverity.Error,
                TrustDiagnosticCode.StoreUnsupportedVersion,
                string.Create(CultureInfo.InvariantCulture, $"The trust store has schema version {version}, but this version of TrayWisp only supports version {SchemaVersion}.")),
        };
    }

    private static TrustDiagnostic? ReadVersion1(ReadOnlySpan<byte> utf8, List<PluginApproval> approvals)
    {
        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        reader.Read();
        bool seenVersion = false;
        bool seenApprovals = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("schemaVersion"u8))
            {
                // Validated and checked for duplicates by the first pass.
                seenVersion = true;
                reader.Read();
                continue;
            }

            if (!reader.ValueTextEquals("approvals"u8))
            {
                return Invalid("The trust store has an unknown top-level property. Version 1 allows only 'schemaVersion' and 'approvals'.");
            }

            if (seenApprovals)
            {
                return Invalid("The trust store has more than one 'approvals' property.");
            }

            seenApprovals = true;
            reader.Read();
            TrustDiagnostic? problem = ReadApprovals(ref reader, approvals);
            if (problem is not null)
            {
                return problem;
            }
        }

        if (!seenVersion || !seenApprovals)
        {
            return Invalid("The trust store has no 'approvals' property.");
        }

        return null;
    }

    private static TrustDiagnostic? ReadApprovals(ref Utf8JsonReader reader, List<PluginApproval> approvals)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return Invalid("The trust store's 'approvals' is not an array.");
        }

        var keys = new HashSet<PluginTrustKey>();
        var generations = new HashSet<ApprovalGeneration>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            int index = approvals.Count + 1;
            if (approvals.Count >= PluginTrustService.MaxApprovals)
            {
                return new TrustDiagnostic(
                    PluginDiagnosticSeverity.Error,
                    TrustDiagnosticCode.StoreTooManyRecords,
                    $"The trust store has more than {PluginTrustService.MaxApprovals} approval records.");
            }

            TrustDiagnostic? problem = ReadRecord(ref reader, index, out PluginApproval? approval);
            if (problem is not null)
            {
                return problem;
            }

            if (!keys.Add(approval!.Key))
            {
                return Invalid(Record(index, "approves the same root and plugin identity (ignoring case) as an earlier record."));
            }

            if (!generations.Add(approval.Generation))
            {
                return Invalid(Record(index, "has the same generation as an earlier record."));
            }

            approvals.Add(approval);
        }

        return null;
    }

    private static TrustDiagnostic? ReadRecord(ref Utf8JsonReader reader, int index, out PluginApproval? approval)
    {
        approval = null;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return Invalid(Record(index, "is not a JSON object."));
        }

        string? root = null, pluginId = null, sha256 = null, generation = null, approvedAt = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            ref string? field = ref root;
            string fieldName;
            if (reader.ValueTextEquals("root"u8))
            {
                fieldName = "root";
            }
            else if (reader.ValueTextEquals("pluginId"u8))
            {
                field = ref pluginId;
                fieldName = "pluginId";
            }
            else if (reader.ValueTextEquals("sha256"u8))
            {
                field = ref sha256;
                fieldName = "sha256";
            }
            else if (reader.ValueTextEquals("generation"u8))
            {
                field = ref generation;
                fieldName = "generation";
            }
            else if (reader.ValueTextEquals("approvedAtUtc"u8))
            {
                field = ref approvedAt;
                fieldName = "approvedAtUtc";
            }
            else
            {
                return Invalid(Record(index, "has an unknown property."));
            }

            if (field is not null)
            {
                return Invalid(Record(index, $"has more than one '{fieldName}' property."));
            }

            reader.Read();
            if (!TryReadString(ref reader, out field))
            {
                return Invalid(Record(index, $"has a '{fieldName}' that is not a string of at most {PluginRoot.MaxLength} characters."));
            }
        }

        if (root is null || pluginId is null || sha256 is null || generation is null || approvedAt is null)
        {
            return Invalid(Record(index, "is missing one of 'root', 'pluginId', 'sha256', 'generation' and 'approvedAtUtc'."));
        }

        if (!PluginRoot.TryCreate(root, out PluginRoot? pluginRoot) || !string.Equals(pluginRoot.Value, root, StringComparison.Ordinal))
        {
            return Invalid(Record(index, "has a 'root' that is not a normalized, fully qualified local path."));
        }

        if (!PluginId.TryCreate(pluginId, out PluginId? id) || !string.Equals(id.Value, pluginId, StringComparison.Ordinal) || !PluginTrustKey.IsSupportedPluginId(id))
        {
            return Invalid(Record(index, "has a 'pluginId' that is not a normalized plugin identity."));
        }

        if (!ContentFingerprint.TryParse(sha256, out ContentFingerprint? fingerprint))
        {
            return Invalid(Record(index, "has a 'sha256' that is not 64 lowercase hexadecimal characters."));
        }

        if (!ApprovalGeneration.TryParse(generation, out ApprovalGeneration? approvalGeneration))
        {
            return Invalid(Record(index, "has a 'generation' that is not 32 lowercase hexadecimal characters."));
        }

        if (!TryParseTimestamp(approvedAt, out DateTimeOffset approvedAtUtc))
        {
            return Invalid(Record(index, "has an 'approvedAtUtc' that is not a UTC time in the form 2024-01-31T12:00:00.0000000Z."));
        }

        approval = new PluginApproval(new PluginTrustKey(pluginRoot, id), fingerprint, approvalGeneration, approvedAtUtc);
        return null;
    }

    private static bool TryReadVersion(ref Utf8JsonReader reader, out int version)
    {
        version = 0;
        if (reader.TokenType != JsonTokenType.Number)
        {
            return false;
        }

        // Only plain positive integers without sign, fraction, exponent or leading zeros.
        ReadOnlySpan<byte> raw = reader.ValueSpan;
        if (raw.Length is 0 or > 9 || raw[0] == (byte)'0')
        {
            return false;
        }

        foreach (byte b in raw)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return reader.TryGetInt32(out version);
    }

    private static bool TryReadString(ref Utf8JsonReader reader, out string? value)
    {
        value = null;
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > MaxEncodedStringBytes)
        {
            return false;
        }

        try
        {
            value = reader.GetString();
        }
        catch (InvalidOperationException)
        {
            // Escaped lone surrogates cannot be represented.
            return false;
        }

        return value is not null && value.Length <= PluginRoot.MaxLength;
    }

    private static bool TryParseTimestamp(string text, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            return false;
        }

        value = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        return string.Equals(FormatTimestamp(value), text, StringComparison.Ordinal);
    }

    private static string Record(int index, string problem) => string.Create(CultureInfo.InvariantCulture, $"Approval record {index} {problem}");

    private static TrustDiagnostic Invalid(string message) => new(PluginDiagnosticSeverity.Error, TrustDiagnosticCode.StoreInvalid, message);
}
