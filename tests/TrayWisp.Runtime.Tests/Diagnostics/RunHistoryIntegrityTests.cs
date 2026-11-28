using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Tests.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Diagnostics;

// The run history's read contract (docs/diagnostics.md): malformed Unicode, the per-record size bound measured as stored
// and in canonical form, and reads that create nothing, not even the lock file. Every rejected history is left exactly
// as it was, by readers and by writers.
public sealed partial class RunHistoryTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    private string LockPath => FilePath + ".lock";

    public static TheoryData<string> EscapedMalformedUnicode =>
    [
        "pluginIdHigh", "pluginIdLow", "pluginIdReversed", "rootId", "recordedAtUtc", "status", "trustStatus",
        "severity", "publicationTrustStatus", "interpreterKind", "recordPropertyName", "diagnosticPropertyName",
        "topLevelName", "schemaVersionName",
    ];

    public static TheoryData<string> InvalidUtf8 => ["pluginIdByte", "recordPropertyName", "truncatedSequence", "overlong", "encodedSurrogate", "topLevelName"];

    [Theory]
    [MemberData(nameof(EscapedMalformedUnicode))]
    public void EscapedUnpairedSurrogatesAreRejectedEverywhere(string where)
    {
        string valid = ValidHistoryText();
        string corrupt = where switch
        {
            "pluginIdHigh" => ReplaceFirst(valid, "\"pluginId\":\"a.ps1\"", "\"pluginId\":\"a\\uD800.ps1\""),
            "pluginIdLow" => ReplaceFirst(valid, "\"pluginId\":\"a.ps1\"", "\"pluginId\":\"a\\uDC00.ps1\""),
            "pluginIdReversed" => ReplaceFirst(valid, "\"pluginId\":\"a.ps1\"", "\"pluginId\":\"\\uDE80\\uD83D.ps1\""),
            "rootId" => ReplaceFirst(valid, "\"rootId\":\"", "\"rootId\":\"\\uD800"),
            "recordedAtUtc" => ReplaceFirst(valid, "\"recordedAtUtc\":\"", "\"recordedAtUtc\":\"\\uDBFF"),
            "status" => ReplaceFirst(valid, "\"status\":\"TrustDenied\"", "\"status\":\"Trust\\uD800Denied\""),
            "trustStatus" => ReplaceFirst(valid, "\"trustStatus\":\"PendingApproval\"", "\"trustStatus\":\"PendingApproval\\uDFFF\""),
            "severity" => ReplaceFirst(valid, "\"severity\":\"Error\"", "\"severity\":\"Err\\uD800or\""),
            "publicationTrustStatus" => ReplaceFirst(valid, "\"publicationTrustStatus\":null", "\"publicationTrustStatus\":\"\\uD800\""),
            "interpreterKind" => ReplaceFirst(valid, "\"interpreterKind\":null", "\"interpreterKind\":\"\\uDC00\""),
            "recordPropertyName" => ReplaceFirst(valid, "\"stdoutBytes\":", "\"stdout\\uD800Bytes\":"),
            "diagnosticPropertyName" => ReplaceFirst(valid, "\"code\":", "\"co\\uDC00de\":"),
            "topLevelName" => ReplaceFirst(valid, "\"records\":", "\"rec\\uD800ords\":"),
            _ => ReplaceFirst(valid, "\"schemaVersion\":", "\"schema\\uDC00Version\":"),
        };

        AssertCorruptAndUnchanged(Encoding.UTF8.GetBytes(corrupt));
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8))]
    public void InvalidUtf8IsRejectedEverywhere(string where)
    {
        byte[] valid = Encoding.UTF8.GetBytes(ValidHistoryText());
        byte[] corrupt = where switch
        {
            "pluginIdByte" => ReplaceFirst(valid, "\"pluginId\":\"a.ps1\""u8, [.. "\"pluginId\":\"a"u8, 0xFF, .. ".ps1\""u8]),
            "recordPropertyName" => ReplaceFirst(valid, "\"rootId\":"u8, [.. "\"root"u8, 0xC3, .. "Id\":"u8]),
            "truncatedSequence" => ReplaceFirst(valid, "\"pluginId\":\"a.ps1\""u8, [.. "\"pluginId\":\"a.ps1"u8, 0xE2, 0x82, .. "\""u8]),
            "overlong" => ReplaceFirst(valid, "\"pluginId\":\"a.ps1\""u8, [.. "\"pluginId\":\"a"u8, 0xC0, 0xAE, .. "ps1\""u8]),
            "encodedSurrogate" => ReplaceFirst(valid, "\"pluginId\":\"a.ps1\""u8, [.. "\"pluginId\":\"a"u8, 0xED, 0xA0, 0x80, .. ".ps1\""u8]),
            _ => ReplaceFirst(valid, "\"records\":"u8, [.. "\"rec"u8, 0xFF, .. "ords\":"u8]),
        };

        StorageDiagnostic diagnostic = AssertCorruptAndUnchanged(corrupt);
        if (where != "topLevelName")
        {
            Assert.Contains("not valid UTF-8", diagnostic.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidSupplementaryCharactersAreAccepted()
    {
        string id = "\uD83D\uDE80/clock\uD83D\uDE80.ps1";
        Assert.True(Record(History(), Result(id)).Succeeded);
        Assert.Equal(id, Assert.Single(History().Read(10, Token).Records).PluginId.Value);

        // The same identity written as raw UTF-8 instead of escapes is equally valid.
        string escaped = File.ReadAllText(FilePath);
        Assert.Contains("\\uD83D\\uDE80", escaped, StringComparison.Ordinal);
        File.WriteAllBytes(FilePath, Encoding.UTF8.GetBytes(escaped.Replace("\\uD83D\\uDE80", "\uD83D\uDE80", StringComparison.Ordinal)));
        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Loaded, read.Status);
        Assert.Equal(id, Assert.Single(read.Records).PluginId.Value);
    }

    [Fact]
    public void RecordsOfExactlyTheLimitAreKeptAndOneByteMoreIsRejected()
    {
        string exact = IdOfRecordSize(PluginRunHistory.MaxRecordBytes);
        string over = IdOfRecordSize(PluginRunHistory.MaxRecordBytes + 1);
        PluginRunHistory history = History();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Record(history, Result(exact)).Succeeded);
        }

        byte[] before = File.ReadAllBytes(FilePath);
        RunHistoryWriteResult rejected = Record(history, Result(over));
        Assert.Equal(RunHistoryWriteStatus.Rejected, rejected.Status);
        Assert.Equal(StorageDiagnosticCode.RecordTooLarge, Assert.Single(rejected.Diagnostics).Code);
        Assert.Equal(before, File.ReadAllBytes(FilePath));

        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Loaded, read.Status);
        Assert.Equal([exact, exact, exact], read.Records.Select(r => r.PluginId.Value));
        Assert.All(StoredRecordSizes(before), size => Assert.Equal(PluginRunHistory.MaxRecordBytes, size));
    }

    [Fact]
    public void AnOversizedExistingRecordMakesTheHistoryCorrupt()
    {
        Assert.True(Record(History()).Succeeded);
        byte[] small = Canonical("a.ps1");
        byte[] oversized = Canonical(IdOfRecordSize(PluginRunHistory.MaxRecordBytes + 1));
        byte[] content = RunHistorySerializer.Serialize([small, oversized, small]);
        Assert.True(content.Length < PluginRunHistory.MaxHistoryBytes);
        StorageDiagnostic diagnostic = AssertCorruptAndUnchanged(content);
        Assert.Contains("Record 2 of the run history is larger than 8192 bytes", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsAreMeasuredBothAsStoredAndInCanonicalForm()
    {
        Assert.True(Record(History()).Succeeded);

        // Canonical exactly at the limit, stored smaller (raw UTF-8 instead of \u00E9 escapes): accepted.
        string exact = IdOfRecordSize(PluginRunHistory.MaxRecordBytes);
        byte[] raw = Unescaped(Canonical(exact));
        Assert.True(raw.Length < PluginRunHistory.MaxRecordBytes);
        WriteHistoryFile(RunHistorySerializer.Serialize([raw, raw]));
        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Loaded, read.Status);
        Assert.Equal([exact, exact], read.Records.Select(r => r.PluginId.Value));

        // Rewriting it keeps every record within the limit: the writer re-serializes them canonically.
        Assert.True(Record(History()).Succeeded);
        Assert.All(StoredRecordSizes(File.ReadAllBytes(FilePath)), size => Assert.True(size <= PluginRunHistory.MaxRecordBytes));

        // Stored under the limit, but one byte over it in canonical form: rejected.
        byte[] overCanonical = Unescaped(Canonical(IdOfRecordSize(PluginRunHistory.MaxRecordBytes + 1)));
        Assert.True(overCanonical.Length < PluginRunHistory.MaxRecordBytes);
        StorageDiagnostic canonical = AssertCorruptAndUnchanged(RunHistorySerializer.Serialize([overCanonical]));
        Assert.Contains("in its canonical form", canonical.Message, StringComparison.Ordinal);

        // Canonical at the limit, but one whitespace byte inside the stored record takes it over: rejected.
        byte[] padded = [(byte)'{', (byte)' ', .. Canonical(exact).AsSpan(1)];
        Assert.Equal(PluginRunHistory.MaxRecordBytes + 1, padded.Length);
        AssertCorruptAndUnchanged(RunHistorySerializer.Serialize([padded]));

        // Whitespace between records is not part of any record.
        byte[] separated = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(RunHistorySerializer.Serialize([Canonical(exact), Canonical(exact)])).Replace(",\n", " ,\n\n   ", StringComparison.Ordinal));
        WriteHistoryFile(separated);
        Assert.Equal(RunHistoryReadStatus.Loaded, History().Read(10, Token).Status);
    }

    [Fact]
    public void LongSupplementaryIdentitiesAreBoundInBytes()
    {
        // 320 emoji: 640 UTF-16 code units, 3,840 bytes as escapes. 720 emoji: 8,640 bytes, over the limit although
        // the identity is only 1,448 characters long.
        string Make(int perSegment) => string.Join("/", Enumerable.Repeat(string.Concat(Enumerable.Repeat("\uD83D\uDE80", perSegment)), 8)) + ".ps1";
        string fits = Make(40);
        string tooLarge = Make(90);
        Assert.True(tooLarge.Length < PluginTrustKey.MaxPluginIdLength);
        Assert.True(Record(History(), Result(fits)).Succeeded);
        Assert.Equal(fits, Assert.Single(History().Read(10, Token).Records).PluginId.Value);
        RunHistoryWriteResult rejected = Record(History(), Result(tooLarge));
        Assert.Equal(StorageDiagnosticCode.RecordTooLarge, Assert.Single(rejected.Diagnostics).Code);

        byte[] content = RunHistorySerializer.Serialize([Canonical(fits), Canonical(tooLarge)]);
        AssertCorruptAndUnchanged(content);
    }

    [Fact]
    public void AnExistingHistoryWithoutItsLockFileIsNotReadAndNothingIsCreated()
    {
        Assert.True(Record(History()).Succeeded);
        File.Delete(LockPath);
        string[] before = DirectorySnapshot();

        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Unavailable, read.Status);
        Assert.Equal(StorageDiagnosticCode.HistoryLockFailed, Assert.Single(read.Diagnostics).Code);
        Assert.Contains("is missing", read.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Empty(read.Records);
        Assert.False(File.Exists(LockPath));
        Assert.Equal(before, DirectorySnapshot());

        // The next recorded run creates the lock file again.
        Assert.True(Record(History()).Succeeded);
        Assert.True(File.Exists(LockPath));
        Assert.Equal(2, History().Read(10, Token).TotalRecords);
    }

    [Fact]
    public void AMissingHistoryNextToALockFileCreatesNothing()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(LockPath, []);
        string[] before = DirectorySnapshot();
        Assert.Equal(RunHistoryReadStatus.NoHistory, History().Read(10, Token).Status);
        Assert.Equal(before, DirectorySnapshot());
    }

    [Fact]
    public void ReadsLeaveTheHistoryAndItsLockUnchanged()
    {
        Assert.True(Record(History()).Succeeded);
        File.WriteAllText(LockPath, "lock content");
        var time = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(LockPath, time);
        File.SetLastWriteTimeUtc(FilePath, time);
        string[] before = DirectorySnapshot();

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(RunHistoryReadStatus.Loaded, History().Read(10, Token).Status);
        }

        Assert.Equal(before, DirectorySnapshot());
        Assert.Equal("lock content", File.ReadAllText(LockPath));
    }

    [Fact]
    public void ADirectoryWhereTheLockFileBelongsIsRefused()
    {
        Assert.True(Record(History()).Succeeded);
        byte[] history = File.ReadAllBytes(FilePath);
        File.Delete(LockPath);
        System.IO.Directory.CreateDirectory(LockPath);

        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Unavailable, read.Status);
        Assert.Empty(read.Records);
        Assert.Equal(RunHistoryWriteStatus.Unavailable, Record(History()).Status);
        Assert.True(System.IO.Directory.Exists(LockPath));
        Assert.Equal(history, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void AHardLinkedLockFileIsRefused()
    {
        Assert.True(Record(History()).Succeeded);
        byte[] history = File.ReadAllBytes(FilePath);
        string elsewhere = Path.Combine(_env.Base, "elsewhere.lock");
        File.Move(LockPath, elsewhere);
        Assert.SkipUnless(TrustTestEnvironment.TryCreateHardLink(LockPath, elsewhere), "Hard links are not available.");

        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Unavailable, read.Status);
        Assert.Equal(StorageDiagnosticCode.StorageFileUnsafe, Assert.Single(read.Diagnostics).Code);
        Assert.Equal(StorageDiagnosticCode.StorageFileUnsafe, Assert.Single(Record(History()).Diagnostics).Code);
        Assert.True(File.Exists(LockPath));
        Assert.True(File.Exists(elsewhere));
        Assert.Equal(history, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void AReaderHoldsTheLockAgainstWriters()
    {
        Assert.True(Record(History()).Succeeded);
        RunHistoryWriteResult? blocked = null;
        var hooks = new RunHistoryTestHooks { Locked = () => blocked = Record(History(TimeSpan.FromMilliseconds(200)), Result("blocked.ps1")) };

        RunHistoryReadResult read = History(hooks: hooks).Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Loaded, read.Status);
        Assert.Equal(StorageDiagnosticCode.HistoryLockTimeout, Assert.Single(blocked!.Diagnostics).Code);
        Assert.Equal(1, History().Read(10, Token).TotalRecords);
    }

    [Fact]
    public async Task ConcurrentReadersAndWritersSeeOnlyCompleteHistories()
    {
        Task[] writers = [.. Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            PluginRunHistory history = History(TimeSpan.FromSeconds(30));
            for (int i = 0; i < 10; i++)
            {
                Assert.True(Record(history, Result($"w{writer}-{i}.ps1")).Succeeded);
            }
        }, Token))];
        Task[] readers = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            PluginRunHistory history = History(TimeSpan.FromSeconds(30));
            int previous = 0;
            for (int i = 0; i < 20; i++)
            {
                RunHistoryReadResult read = history.Read(PluginRunHistory.MaxRecords, Token);
                Assert.True(read.Succeeded, $"{read.Status}: {string.Join(" ", read.Diagnostics)}");
                Assert.True(read.TotalRecords >= previous);
                Assert.Equal(read.TotalRecords, read.Records.Length);
                previous = read.TotalRecords;
            }
        }, Token))];
        await Task.WhenAll([.. writers, .. readers]);
        Assert.Equal(40, History().Read(PluginRunHistory.MaxRecords, Token).TotalRecords);
    }

    // A valid one-record history in the writer's canonical form.
    private string ValidHistoryText()
    {
        Assert.True(Record(History()).Succeeded);
        return File.ReadAllText(FilePath);
    }

    // Writes the content, then checks that readers report it as corrupt without records, and that neither readers nor
    // writers change it, create anything or leak its content into the diagnostic.
    private StorageDiagnostic AssertCorruptAndUnchanged(byte[] content)
    {
        WriteHistoryFile(content);
        string[] before = DirectorySnapshot();

        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Corrupt, read.Status);
        Assert.False(read.Succeeded);
        Assert.Empty(read.Records);
        Assert.Equal(0, read.TotalRecords);
        StorageDiagnostic diagnostic = Assert.Single(read.Diagnostics);
        Assert.Equal(StorageDiagnosticCode.HistoryCorrupt, diagnostic.Code);
        foreach (string leaked in new[] { "a.ps1", "D800", "DC00", "DBFF", "DFFF", "\u00E9", "\uFFFD", "Exception" })
        {
            Assert.DoesNotContain(leaked, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        }

        RunHistoryWriteResult write = Record(History());
        Assert.Equal(RunHistoryWriteStatus.Unavailable, write.Status);
        Assert.Equal(StorageDiagnosticCode.HistoryCorrupt, Assert.Single(write.Diagnostics).Code);
        Assert.Equal(content, File.ReadAllBytes(FilePath));
        Assert.Equal(before, DirectorySnapshot());
        return diagnostic;
    }

    private byte[] Canonical(string id) =>
        RunHistorySerializer.SerializeRecord(PluginRunHistory.CreateRecord(PluginRoot.Create(_env.Root), Result(id), RunPublication.NotApplicable, null, FixedTime));

    // An identity whose record (of the default test result) takes exactly targetBytes in canonical form: six
    // directories of 200 \u00E9 characters (6 bytes each as escapes) and a file name of some more \u00E9 characters and
    // ASCII letters, within the 255-character file name limit.
    private string IdOfRecordSize(int targetBytes)
    {
        static string Make(int accented, int asciiLength) =>
            string.Join("/", Enumerable.Repeat(new string('\u00E9', 200), 6)) + "/" + new string('\u00E9', accented) + new string('a', asciiLength) + ".ps1";

        for (int accented = 0; accented <= 200; accented++)
        {
            int asciiLength = 1 + targetBytes - Canonical(Make(accented, 1)).Length;
            if (asciiLength >= 1 && accented + asciiLength + 4 <= 255)
            {
                string id = Make(accented, asciiLength);
                Assert.Equal(targetBytes, Canonical(id).Length);
                return id;
            }
        }

        throw new InvalidOperationException("No identity of that record size.");
    }

    private static byte[] Unescaped(byte[] record) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(record).Replace("\\u00E9", "\u00E9", StringComparison.Ordinal));

    private static int[] StoredRecordSizes(byte[] history)
    {
        using JsonDocument document = JsonDocument.Parse(history);
        return [.. document.RootElement.GetProperty("records").EnumerateArray().Select(record => JsonMarshal.GetRawUtf8Value(record).Length)];
    }

    private string[] DirectorySnapshot() =>
        [.. System.IO.Directory.EnumerateFileSystemEntries(Directory)
            .Select(path => File.Exists(path)
                ? $"{Path.GetFileName(path)}|{Convert.ToHexString(File.ReadAllBytes(path))}|{File.GetLastWriteTimeUtc(path).Ticks}"
                : Path.GetFileName(path) + "|dir")
            .Order(StringComparer.Ordinal)];

    private static string ReplaceFirst(string text, string find, string replacement)
    {
        int index = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(index >= 0, find);
        return string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + find.Length));
    }

    private static byte[] ReplaceFirst(byte[] bytes, ReadOnlySpan<byte> find, byte[] replacement)
    {
        int index = bytes.AsSpan().IndexOf(find);
        Assert.True(index >= 0);
        return [.. bytes.AsSpan(0, index), .. replacement, .. bytes.AsSpan(index + find.Length)];
    }
}
