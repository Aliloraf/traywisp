using System.Globalization;
using System.Text;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Trust;

// The store format itself, independent of the file system.
public sealed class ApprovalStoreSerializerTests
{
    private const string Hash1 = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Gen1 = "0123456789abcdef0123456789abcdef";
    private const string Gen2 = "fedcba9876543210fedcba9876543210";
    private const string Time = "2026-11-26T10:20:30.1234567Z";

    public static List<PluginApproval> ReadStore(string path)
    {
        TrustDiagnostic? problem = ApprovalStoreSerializer.Parse(File.ReadAllBytes(path), out List<PluginApproval> approvals);
        Assert.Null(problem);
        return approvals;
    }

    public static string EscapeJson(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal);

    public static string Record(
        string root = @"C:\\plugins",
        string pluginId = "clock.ps1",
        string sha256 = Hash1,
        string generation = Gen1,
        string approvedAtUtc = Time,
        string extra = "") =>
        $$"""{"root":"{{root}}","pluginId":"{{pluginId}}","sha256":"{{sha256}}","generation":"{{generation}}","approvedAtUtc":"{{approvedAtUtc}}"{{extra}}}""";

    public static string Store(params string[] records) => $$"""{"schemaVersion":1,"approvals":[{{string.Join(',', records)}}]}""";

    private static PluginApproval Approval(string root, string id, char hash, Guid generation) =>
        new(
            new PluginTrustKey(PluginRoot.Create(root), PluginId.Create(id)),
            ContentFingerprint.Parse(new string(hash, 64)),
            ApprovalGeneration.FromGuid(generation),
            new DateTimeOffset(2026, 11, 26, 10, 20, 30, TimeSpan.Zero).AddTicks(1234567));

    private static TrustDiagnostic? Parse(string json, out List<PluginApproval> approvals) =>
        ApprovalStoreSerializer.Parse(Encoding.UTF8.GetBytes(json), out approvals);

    [Fact]
    public void SerializesDeterministicallyInKeyOrder()
    {
        PluginApproval[] approvals =
        [
            Approval(@"D:\b", "z.ps1", 'c', Guid.Parse("00000000-0000-0000-0000-000000000003")),
            Approval(@"C:\Plugins", "work/b.py", 'b', Guid.Parse("00000000-0000-0000-0000-000000000002")),
            Approval(@"c:\plugins", "A.ps1", 'a', Guid.Parse("00000000-0000-0000-0000-000000000001")),
        ];

        string expected = string.Join('\n',
            "{",
            "  \"schemaVersion\": 1,",
            "  \"approvals\": [",
            "    {",
            "      \"root\": \"c:\\\\plugins\",",
            "      \"pluginId\": \"A.ps1\",",
            $"      \"sha256\": \"{new string('a', 64)}\",",
            "      \"generation\": \"00000000000000000000000000000001\",",
            "      \"approvedAtUtc\": \"2026-11-26T10:20:30.1234567Z\"",
            "    },",
            "    {",
            "      \"root\": \"C:\\\\Plugins\",",
            "      \"pluginId\": \"work/b.py\",",
            $"      \"sha256\": \"{new string('b', 64)}\",",
            "      \"generation\": \"00000000000000000000000000000002\",",
            "      \"approvedAtUtc\": \"2026-11-26T10:20:30.1234567Z\"",
            "    },",
            "    {",
            "      \"root\": \"D:\\\\b\",",
            "      \"pluginId\": \"z.ps1\",",
            $"      \"sha256\": \"{new string('c', 64)}\",",
            "      \"generation\": \"00000000000000000000000000000003\",",
            "      \"approvedAtUtc\": \"2026-11-26T10:20:30.1234567Z\"",
            "    }",
            "  ]",
            "}",
            string.Empty);

        string forward = Encoding.UTF8.GetString(ApprovalStoreSerializer.Serialize(approvals));
        string reversed = Encoding.UTF8.GetString(ApprovalStoreSerializer.Serialize(approvals.Reverse()));

        Assert.Equal(expected, forward);
        Assert.Equal(expected, reversed);
    }

    [Fact]
    public void RoundTripsEveryField()
    {
        PluginApproval[] approvals =
        [
            Approval(@"C:\", "x.ps1", 'd', Guid.NewGuid()),
            Approval(@"C:\Users\Ünïcode dir\plugins", "a/b/c/d/e/f/g/h/deep.js", 'e', Guid.NewGuid()),
        ];

        Assert.Null(ApprovalStoreSerializer.Parse(ApprovalStoreSerializer.Serialize(approvals), out List<PluginApproval> read));

        Assert.Equal(approvals.OrderBy(a => a.Key), read);
    }

    [Fact]
    public void EmptyStoreRoundTrips()
    {
        byte[] content = ApprovalStoreSerializer.Serialize([]);

        Assert.Equal("{\n  \"schemaVersion\": 1,\n  \"approvals\": []\n}\n", Encoding.UTF8.GetString(content));
        Assert.Null(ApprovalStoreSerializer.Parse(content, out List<PluginApproval> read));
        Assert.Empty(read);
    }

    [Fact]
    public void AcceptsRecordsInAnyOrderAndPropertiesInAnyOrder()
    {
        string record = $$"""{"approvedAtUtc":"{{Time}}","generation":"{{Gen2}}","sha256":"{{Hash1}}","pluginId":"b.ps1","root":"C:\\plugins"}""";
        string json = $$"""{"approvals":[{{record}},{{Record()}}],"schemaVersion":1}""";

        Assert.Null(Parse(json, out List<PluginApproval> approvals));
        Assert.Equal(2, approvals.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":null}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[1]}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[[]]}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[],}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[]} {}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[]} x")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[]}//")]
    [InlineData("{/* c */\"schemaVersion\":1,\"approvals\":[]}")]
    [InlineData("{'schemaVersion':1,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[],\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"schem\\u0061Version\":1,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[],\"extra\":true}")]
    [InlineData("{\"schemaVersion\":1,\"approvals\":[],\"SchemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":0,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":-1,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":1.0,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":1e0,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":01,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":\"1\",\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":null,\"approvals\":[]}")]
    [InlineData("{\"schemaVersion\":true,\"approvals\":[]}")]
    public void RejectsMalformedDocuments(string json)
    {
        TrustDiagnostic? problem = Parse(json, out List<PluginApproval> approvals);

        Assert.Equal(TrustDiagnosticCode.StoreInvalid, problem?.Code);
        Assert.Empty(approvals);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(123456789)]
    public void ReportsUnsupportedSchemaVersionsDistinctly(int version)
    {
        string json = $$$"""{"schemaVersion":{{{version}}},"approvals":"a future layout","future":{"x":[1,2,3]}}""";

        TrustDiagnostic? problem = Parse(json, out List<PluginApproval> approvals);

        Assert.Equal(TrustDiagnosticCode.StoreUnsupportedVersion, problem?.Code);
        Assert.Contains(version.ToString(CultureInfo.InvariantCulture), problem!.Message, StringComparison.Ordinal);
        Assert.Empty(approvals);
    }

    [Fact]
    public void RejectsByteOrderMarkAndInvalidUtf8()
    {
        byte[] valid = Encoding.UTF8.GetBytes(Store(Record()));
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. valid];
        byte[] invalidUtf8 = Encoding.UTF8.GetBytes(Store(Record(pluginId: "XX.ps1")));
        int index = Encoding.UTF8.GetString(invalidUtf8).IndexOf("XX", StringComparison.Ordinal);
        invalidUtf8[index] = 0xC3;
        invalidUtf8[index + 1] = 0x28;

        Assert.Equal(TrustDiagnosticCode.StoreInvalid, ApprovalStoreSerializer.Parse(withBom, out _)?.Code);
        Assert.Equal(TrustDiagnosticCode.StoreInvalid, ApprovalStoreSerializer.Parse(invalidUtf8, out _)?.Code);
        Assert.Null(ApprovalStoreSerializer.Parse(valid, out _));
    }

    public static TheoryData<string> InvalidRecords => new()
    {
        // Structure.
        Record(extra: ",\"note\":\"x\""),
        Record(extra: ",\"root\":\"C:\\\\other\""),
        Record(extra: ",\"r\\u006fot\":\"C:\\\\other\""),
        Record(extra: ",\"sha256\":\"" + Hash1 + "\""),
        Record(extra: ",\"Root\":\"C:\\\\plugins\""),
        """{"root":"C:\\plugins","pluginId":"clock.ps1","sha256":"1111111111111111111111111111111111111111111111111111111111111111","generation":"0123456789abcdef0123456789abcdef"}""",
        """{"root":"C:\\plugins","pluginId":"clock.ps1","sha256":"1111111111111111111111111111111111111111111111111111111111111111","approvedAtUtc":"2026-11-26T10:20:30.1234567Z"}""",
        """{"root":null,"pluginId":"clock.ps1","sha256":"1111111111111111111111111111111111111111111111111111111111111111","generation":"0123456789abcdef0123456789abcdef","approvedAtUtc":"2026-11-26T10:20:30.1234567Z"}""",
        """{"root":["C:\\plugins"],"pluginId":"clock.ps1","sha256":"1111111111111111111111111111111111111111111111111111111111111111","generation":"0123456789abcdef0123456789abcdef","approvedAtUtc":"2026-11-26T10:20:30.1234567Z"}""",

        // Roots: relative, drive-relative, UNC, device, trailing separator, forward slashes, traversal, streams.
        Record(root: "plugins"),
        Record(root: "C:plugins"),
        Record(root: "\\\\plugins"),
        Record(root: "\\\\\\\\server\\\\share"),
        Record(root: "\\\\\\\\?\\\\C:\\\\plugins"),
        Record(root: "\\\\\\\\.\\\\C:\\\\plugins"),
        Record(root: "C:\\\\plugins\\\\"),
        Record(root: "C:/plugins"),
        Record(root: "C:\\\\plugins\\\\..\\\\other"),
        Record(root: "C:\\\\plugins\\\\.\\\\x"),
        Record(root: "C:\\\\plugins\\\\\\\\x"),
        Record(root: "C:\\\\plugins:stream"),
        Record(root: "C:\\\\plugins\\\\CON"),
        Record(root: "C:\\\\plugins."),
        Record(root: "C:\\\\plugins\\u0000"),
        Record(root: ""),

        // Identities.
        Record(pluginId: ""),
        Record(pluginId: "work\\\\clock.ps1"),
        Record(pluginId: "../clock.ps1"),
        Record(pluginId: "/clock.ps1"),
        Record(pluginId: "C:/clock.ps1"),
        Record(pluginId: "clock.ps1:stream"),
        Record(pluginId: ".hidden/clock.ps1"),
        Record(pluginId: ".clock.ps1"),
        Record(pluginId: "readme.md"),
        Record(pluginId: "clock.0s.ps1"),
        Record(pluginId: "a/b/c/d/e/f/g/h/i/deep.ps1"),
        Record(pluginId: "work//clock.ps1"),

        // Fingerprints.
        Record(sha256: Hash1.ToUpperInvariant().Replace('1', 'A')),
        Record(sha256: Hash1[..63]),
        Record(sha256: Hash1 + "1"),
        Record(sha256: "sha256:" + Hash1[7..]),
        Record(sha256: " " + Hash1[1..]),
        Record(sha256: Hash1[..63] + "g"),

        // Generations.
        Record(generation: "00000000000000000000000000000000"),
        Record(generation: Gen1.ToUpperInvariant()),
        Record(generation: "01234567-89ab-cdef-0123-456789abcdef"),
        Record(generation: "{" + Gen1 + "}"),
        Record(generation: Gen1[..31]),

        // Timestamps.
        Record(approvedAtUtc: "2026-11-26T10:20:30Z"),
        Record(approvedAtUtc: "2026-11-26T10:20:30.1234567"),
        Record(approvedAtUtc: "2026-11-26T10:20:30.1234567+00:00"),
        Record(approvedAtUtc: "2026-11-26T11:20:30.1234567+01:00"),
        Record(approvedAtUtc: "2026-02-30T10:20:30.1234567Z"),
        Record(approvedAtUtc: "26 November 2026"),
        Record(approvedAtUtc: ""),
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public void RejectsInvalidRecords(string record)
    {
        TrustDiagnostic? problem = Parse(Store(Record(pluginId: "valid.ps1", generation: Gen2), record), out List<PluginApproval> approvals);

        Assert.Equal(TrustDiagnosticCode.StoreInvalid, problem?.Code);
        Assert.Empty(approvals);
    }

    [Fact]
    public void RejectsDuplicateLogicalIdentitiesIgnoringCase()
    {
        string first = Record(root: "C:\\\\Plugins", pluginId: "Work/Clock.ps1", generation: Gen1);
        string second = Record(root: "c:\\\\plugins", pluginId: "work/clock.PS1", generation: Gen2);

        TrustDiagnostic? problem = Parse(Store(first, second), out List<PluginApproval> approvals);

        Assert.Equal(TrustDiagnosticCode.StoreInvalid, problem?.Code);
        Assert.Contains("record 2", problem!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(approvals);
    }

    [Fact]
    public void RejectsDuplicateGenerations()
    {
        TrustDiagnostic? problem = Parse(Store(Record(pluginId: "a.ps1"), Record(pluginId: "b.ps1")), out List<PluginApproval> approvals);

        Assert.Equal(TrustDiagnosticCode.StoreInvalid, problem?.Code);
        Assert.Empty(approvals);
    }

    [Fact]
    public void AcceptsSameIdentityUnderDifferentRoots()
    {
        Assert.Null(Parse(Store(Record(root: "C:\\\\a"), Record(root: "C:\\\\b", generation: Gen2)), out List<PluginApproval> approvals));
        Assert.Equal(2, approvals.Count);
    }

    [Fact]
    public void RejectsMoreThanTheRecordLimit()
    {
        string[] records = [.. Enumerable.Range(0, PluginTrustService.MaxApprovals + 1).Select(i =>
            Record(pluginId: $"p{i}.ps1", generation: (i + 1).ToString("x32", CultureInfo.InvariantCulture)))];

        TrustDiagnostic? atLimit = Parse(Store(records[..PluginTrustService.MaxApprovals]), out List<PluginApproval> accepted);
        TrustDiagnostic? overLimit = Parse(Store(records), out List<PluginApproval> rejected);

        Assert.Null(atLimit);
        Assert.Equal(PluginTrustService.MaxApprovals, accepted.Count);
        Assert.Equal(TrustDiagnosticCode.StoreTooManyRecords, overLimit?.Code);
        Assert.Empty(rejected);
    }

    [Fact]
    public void RejectsExcessiveNesting()
    {
        string within = new string('[', ApprovalStoreSerializer.MaxDepth - 1) + new string(']', ApprovalStoreSerializer.MaxDepth - 1);
        string beyond = new string('[', ApprovalStoreSerializer.MaxDepth) + new string(']', ApprovalStoreSerializer.MaxDepth);

        // Unknown properties are rejected anyway; nesting is limited even where content is skipped.
        Assert.Equal(TrustDiagnosticCode.StoreUnsupportedVersion, Parse($$"""{"schemaVersion":2,"x":{{within}}}""", out _)?.Code);
        Assert.Equal(TrustDiagnosticCode.StoreInvalid, Parse($$"""{"schemaVersion":2,"x":{{beyond}}}""", out _)?.Code);
    }

    [Fact]
    public void RejectsOverlongAndUnrepresentableStrings()
    {
        string longRoot = "C:\\\\" + new string('a', PluginRoot.MaxLength);
        string escapedLongRoot = "C:\\\\" + string.Concat(Enumerable.Repeat("\\u0061", PluginRoot.MaxLength));

        Assert.Equal(TrustDiagnosticCode.StoreInvalid, Parse(Store(Record(root: longRoot)), out _)?.Code);
        Assert.Equal(TrustDiagnosticCode.StoreInvalid, Parse(Store(Record(root: escapedLongRoot)), out _)?.Code);
        Assert.Equal(TrustDiagnosticCode.StoreInvalid, Parse(Store(Record(pluginId: "a\\ud800.ps1")), out _)?.Code);
    }

    [Fact]
    public void DiagnosticsDoNotRepeatStoreContent()
    {
        const string Secret = "TopSecretValue";
        string[] documents =
        [
            Store(Record(extra: $",\"{Secret}\":\"{Secret}\"")),
            Store(Record(root: Secret)),
            Store(Record(pluginId: Secret + ".md")),
            $$"""{"schemaVersion":1,"approvals":[{{Record()}}],"{{Secret}}":1}""",
            $$"""{"schemaVersion":1,"approvals":[{{Record()}}] {{Secret}}""",
            $$"""{"schemaVersion":1,"approvals":[{{Secret}}]}""",
        ];

        foreach (string document in documents)
        {
            TrustDiagnostic? problem = Parse(document, out _);
            Assert.NotNull(problem);
            Assert.DoesNotContain(Secret, problem.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static TheoryData<string, TrustDiagnosticCode> StoresFailingAfterValidRecords => new()
    {
        // A valid, non-empty approvals array followed by an unknown top-level property.
        { $$"""{"schemaVersion":1,"approvals":[{{Record()}}],"extra":true}""", TrustDiagnosticCode.StoreInvalid },

        // ... followed by a duplicated approvals property.
        { $$"""{"schemaVersion":1,"approvals":[{{Record()}}],"approvals":[{{Record(pluginId: "other.ps1", generation: Gen2)}}]}""", TrustDiagnosticCode.StoreInvalid },

        // Malformed JSON after valid records.
        { $$"""{"schemaVersion":1,"approvals":[{{Record()}},""", TrustDiagnosticCode.StoreInvalid },
        { $$"""{"schemaVersion":1,"approvals":[{{Record()}}]} trailing""", TrustDiagnosticCode.StoreInvalid },

        // A later record that is invalid.
        { Store(Record(), Record(root: "relative", pluginId: "other.ps1", generation: Gen2)), TrustDiagnosticCode.StoreInvalid },

        // An unsupported schema version stated after valid-looking records.
        { $$"""{"approvals":[{{Record()}}],"schemaVersion":2}""", TrustDiagnosticCode.StoreUnsupportedVersion },
    };

    [Theory]
    [MemberData(nameof(StoresFailingAfterValidRecords))]
    public void UnsuccessfulParseNeverReturnsApprovals(string json, TrustDiagnosticCode code)
    {
        TrustDiagnostic? problem = Parse(json, out List<PluginApproval> approvals);

        Assert.Equal(code, problem?.Code);
        Assert.Empty(approvals);
    }

    [Fact]
    public void SupplementaryCharactersRoundTripExactly()
    {
        // U+1F680 and U+20000, as surrogate pairs.
        string rocket = "\uD83D\uDE80";
        string cjk = "\uD840\uDC00";
        PluginApproval approval = Approval($@"C:\plugins {rocket}\{cjk}", $"{rocket}/{cjk} clock.5s.ps1", 'c', Guid.NewGuid());

        byte[] bytes = ApprovalStoreSerializer.Serialize([approval]);

        Assert.Null(ApprovalStoreSerializer.Parse(bytes, out List<PluginApproval> read));
        PluginApproval single = Assert.Single(read);
        Assert.Equal(approval.Key.Root.Value, single.Key.Root.Value, StringComparer.Ordinal);
        Assert.Equal(approval.Key.Id.Value, single.Key.Id.Value, StringComparer.Ordinal);
        Assert.Equal(approval, single);
        Assert.DoesNotContain("\uFFFD", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void UnpairedSurrogatesCannotReachTheStore()
    {
        // JSON cannot represent an unpaired surrogate: the writer substitutes or rejects it, so it would not
        // read back as the same text. Such roots and identities are therefore rejected before any approval exists.
        string lone = "a" + (char)0xD800 + "b";
        string text = string.Empty;
        try
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                writer.WriteStringValue(lone);
            }

            text = System.Text.Json.JsonSerializer.Deserialize<string>(buffer.WrittenSpan) ?? string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Rejected by the writer.
        }

        Assert.NotEqual(lone, text, StringComparer.Ordinal);
        Assert.False(PluginRoot.TryCreate(@"C:\plugins\" + lone, out _));
        Assert.True(PluginId.TryCreate(lone + ".ps1", out PluginId? id));
        Assert.False(PluginTrustKey.IsSupportedPluginId(id));
        Assert.Equal(TrustDiagnosticCode.StoreInvalid, Parse(Store(Record(root: @"C:\\plugins\\a\ud800b")), out _)?.Code);
        Assert.Equal(TrustDiagnosticCode.StoreInvalid, Parse(Store(Record(pluginId: "a\\udc00.ps1")), out _)?.Code);
    }
}
