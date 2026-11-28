using System.Runtime.Versioning;
using System.Text;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Tests.Plugins;
using TrayWisp.Runtime.Tests.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Diagnostics;

// The bounded run history: format, bounds, eviction, locking, concurrency, corruption handling and path safety.
[SupportedOSPlatform("windows")]
public sealed partial class RunHistoryTests : IDisposable
{
    private readonly TrustTestEnvironment _env = TrustTestEnvironment.Create();

    public RunHistoryTests()
    {
        Directory = Path.Combine(_env.Base, "diagnostics");
    }

    private string Directory { get; }

    private string FilePath => Path.Combine(Directory, PluginRunHistory.FileName);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _env.Dispose();

    private PluginRunHistory History(TimeSpan? lockTimeout = null, RunHistoryTestHooks? hooks = null, TimeProvider? time = null) =>
        new(Directory, new PluginRunHistoryOptions { LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5) }, time ?? TimeProvider.System, hooks);

    private PluginExecutionResult Result(string id = "a.ps1", PluginExecutionStatus status = PluginExecutionStatus.TrustDenied, Action<ResultDetails>? configure = null)
    {
        var details = new ResultDetails
        {
            Key = new PluginTrustKey(PluginRoot.Create(_env.Root), PluginId.Create(id)),
            TrustStatus = PluginTrustStatus.PendingApproval,
            Diagnostics = [new ExecutionDiagnostic(PluginDiagnosticSeverity.Error, ExecutionDiagnosticCode.TrustDenied, "secret message")],
            TrustDiagnostics = [new TrustDiagnostic(PluginDiagnosticSeverity.Info, TrustDiagnosticCode.ApprovalPending, "secret message")],
        };
        configure?.Invoke(details);
        return new PluginExecutionResult(status, PluginId.Create(id), details);
    }

    // A history file written by hand, with the (empty) lock file every writer leaves next to it: reads never create it.
    private void WriteHistoryFile(byte[] content)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(FilePath, content);
        File.WriteAllBytes(FilePath + ".lock", []);
    }

    private RunHistoryWriteResult Record(PluginRunHistory history, PluginExecutionResult? result = null) =>
        history.Record(_env.Root, result ?? Result(), RunPublication.NotApplicable, null, Token);

    [Fact]
    public void AMissingHistoryIsReportedAndNotCreated()
    {
        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.NoHistory, read.Status);
        Assert.True(read.Succeeded);
        Assert.False(System.IO.Directory.Exists(Directory));

        System.IO.Directory.CreateDirectory(Directory);
        Assert.Equal(RunHistoryReadStatus.NoHistory, History().Read(10, Token).Status);
        Assert.Empty(System.IO.Directory.GetFileSystemEntries(Directory));
    }

    [Fact]
    public void RecordsAreAppendedAndReadOldestFirst()
    {
        PluginRunHistory history = History();
        Assert.Equal(RunHistoryWriteStatus.Recorded, Record(history, Result("first.ps1")).Status);
        Assert.Equal(RunHistoryWriteStatus.Recorded, Record(history, Result("second.ps1")).Status);
        Assert.Equal(RunHistoryWriteStatus.Recorded, Record(history, Result("third.ps1")).Status);

        RunHistoryReadResult read = history.Read(2, Token);
        Assert.Equal(RunHistoryReadStatus.Loaded, read.Status);
        Assert.Equal(3, read.TotalRecords);
        Assert.Equal(["second.ps1", "third.ps1"], read.Records.Select(r => r.PluginId.Value));
        PluginRunRecord record = read.Records[^1];
        Assert.Equal(PluginExecutionStatus.TrustDenied, record.Status);
        Assert.Equal(PluginTrustStatus.PendingApproval, record.TrustStatus);
        Assert.Equal([new RecordedDiagnostic(620, PluginDiagnosticSeverity.Error), new RecordedDiagnostic(530, PluginDiagnosticSeverity.Info)], record.Diagnostics);
        Assert.True(record.IsForRoot(_env.Root));
        Assert.True(record.IsForRoot(_env.Root.ToUpperInvariant()));
        Assert.False(record.IsForRoot(_env.Base));
        Assert.Equal(32, record.RootId.Length);
        Assert.Empty(history.Read(0, Token).Records);
    }

    [Fact]
    public void AllFieldsRoundTrip()
    {
        DateTimeOffset started = new(2026, 11, 28, 10, 20, 30, TimeSpan.Zero);
        PluginExecutionResult result = Result("x/b.5s.py", PluginExecutionStatus.CleanupIncomplete, details =>
        {
            details.StartedAtUtc = started.AddTicks(1234567);
            details.Elapsed = TimeSpan.FromMilliseconds(1500.7);
            details.ExitCode = -1;
            details.TerminationReason = ExecutionTerminationReason.Timeout;
            details.Cleanup = new PluginProcessCleanup(ProcessCleanupStatus.Incomplete, rootExitConfirmed: true, jobObjectAssigned: true, remainingJobProcesses: 2);
            details.StandardOutputBytes = 77;
            details.StandardOutputComplete = false;
            details.StandardOutputTruncated = true;
            details.StandardErrorBytes = 9;
            details.StandardErrorTruncated = true;
            details.Interpreter = new PluginInterpreter(@"C:\secret\python.exe", InterpreterKind.Python, InterpreterSource.SearchPath);
        });
        PluginRunHistory history = History();
        Assert.True(history.Record(_env.Root, result, RunPublication.NotApplicable, PluginTrustStatus.StaleApproval, Token).Succeeded);

        PluginRunRecord record = Assert.Single(history.Read(10, Token).Records);
        Assert.Equal(started.AddTicks(1234567), record.StartedAtUtc);
        Assert.Equal(1500, record.ElapsedMilliseconds);
        Assert.Equal(-1, record.ExitCode);
        Assert.Equal(ExecutionTerminationReason.Timeout, record.TerminationReason);
        Assert.Equal(ProcessCleanupStatus.Incomplete, record.CleanupStatus);
        Assert.True(record.JobObjectAssigned);
        Assert.Equal(2, record.RemainingJobProcesses);
        Assert.Equal(77, record.StandardOutputBytes);
        Assert.False(record.StandardOutputComplete);
        Assert.True(record.StandardOutputTruncated);
        Assert.Equal(9, record.StandardErrorBytes);
        Assert.True(record.StandardErrorTruncated);
        Assert.Equal(InterpreterKind.Python, record.InterpreterKind);
        Assert.Equal(InterpreterSource.SearchPath, record.InterpreterSource);
        Assert.Equal(PluginTrustStatus.StaleApproval, record.PublicationTrustStatus);
        Assert.DoesNotContain("secret", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.DoesNotContain(_env.Root, File.ReadAllText(FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingSensitiveIsStored()
    {
        PluginExecutionResult result = Result(configure: details =>
        {
            details.StandardError = "SECRET-STDERR";
            details.Interpreter = new PluginInterpreter(@"C:\SECRET-DIR\pwsh.exe", InterpreterKind.PowerShell, InterpreterSource.Configured);
            details.Diagnostics = [new ExecutionDiagnostic(PluginDiagnosticSeverity.Error, ExecutionDiagnosticCode.NonZeroExitCode, "SECRET-MESSAGE")];
            details.OutputDiagnostics = [new Core.Output.OutputDiagnostic(PluginDiagnosticSeverity.Warning, Core.Output.OutputDiagnosticCode.EmptyLabel, "SECRET-OUTPUT") { Line = 3 }];
        });
        Assert.True(Record(History(), result).Succeeded);
        string stored = File.ReadAllText(FilePath);
        Assert.DoesNotContain("SECRET", stored, StringComparison.Ordinal);
        Assert.Contains("\"code\":413", stored, StringComparison.Ordinal);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Value is string { Length: > 12 } value)
            {
                Assert.DoesNotContain(value, stored, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheRecordBoundEvictsTheOldestRecords()
    {
        PluginRunHistory history = History();
        for (int i = 0; i < PluginRunHistory.MaxRecords; i++)
        {
            Assert.Equal(0, Record(history, Result($"p{i}.ps1")).EvictedRecords);
        }

        Assert.Equal(PluginRunHistory.MaxRecords, history.Read(PluginRunHistory.MaxRecords, Token).TotalRecords);
        RunHistoryWriteResult extra = Record(history, Result("newest.ps1"));
        Assert.Equal(1, extra.EvictedRecords);
        Assert.Contains(extra.Diagnostics, d => d.Code == StorageDiagnosticCode.RecordsEvicted);

        RunHistoryReadResult read = history.Read(PluginRunHistory.MaxRecords, Token);
        Assert.Equal(PluginRunHistory.MaxRecords, read.TotalRecords);
        Assert.Equal("p1.ps1", read.Records[0].PluginId.Value);
        Assert.Equal("newest.ps1", read.Records[^1].PluginId.Value);
    }

    [Fact]
    public void TheByteBoundEvictsTheOldestRecords()
    {
        PluginRunHistory history = History();
        // About 1,100 non-ASCII characters, each stored as a 6-byte \uXXXX escape: roughly 7 KB per record.
        string directories = string.Concat(Enumerable.Repeat(new string('\u00E9', 140) + "/", 7));
        for (int i = 0; i < 160; i++)
        {
            string id = directories + new string('\u00E9', 100) + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".ps1";
            Assert.True(Record(history, Result(id)).Succeeded);
            Assert.True(new FileInfo(FilePath).Length <= PluginRunHistory.MaxHistoryBytes);
        }

        RunHistoryReadResult read = history.Read(PluginRunHistory.MaxRecords, Token);
        Assert.True(read.TotalRecords < 160);
        Assert.True(new FileInfo(FilePath).Length > PluginRunHistory.MaxHistoryBytes - PluginRunHistory.MaxRecordBytes);
    }

    [Fact]
    public void AnOversizedRecordIsRejectedAndTheHistoryKept()
    {
        PluginRunHistory history = History();
        Assert.True(Record(history).Succeeded);
        byte[] before = File.ReadAllBytes(FilePath);

        // About 3,700 non-ASCII characters are written as \uXXXX escapes: about 24 KB, over the 8 KiB record limit.
        string huge = string.Concat(Enumerable.Repeat(new string('\u00E9', 500) + "/", 7)) + new string('\u00E9', 200) + ".ps1";
        RunHistoryWriteResult result = Record(history, Result(huge));
        Assert.Equal(RunHistoryWriteStatus.Rejected, result.Status);
        Assert.Equal(StorageDiagnosticCode.RecordTooLarge, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void ManyDiagnosticsAreBoundedAndDeduplicated()
    {
        ExecutionDiagnostic[] many = [.. Enum.GetValues<ExecutionDiagnosticCode>().Select(code => new ExecutionDiagnostic(PluginDiagnosticSeverity.Warning, code, "m"))];
        PluginExecutionResult result = Result(configure: details => details.Diagnostics = [.. many, .. many]);
        Assert.True(Record(History(), result).Succeeded);
        PluginRunRecord record = Assert.Single(History().Read(1, Token).Records);
        Assert.Equal(Math.Min(PluginRunHistory.MaxDiagnosticCodesPerRecord, many.Length + 1), record.Diagnostics.Length);
        Assert.Equal(record.Diagnostics.Length, record.Diagnostics.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentWritersLoseNothing()
    {
        Task[] writers = [.. Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            PluginRunHistory history = History(TimeSpan.FromSeconds(30));
            for (int i = 0; i < 10; i++)
            {
                Assert.True(Record(history, Result($"w{writer}-{i}.ps1")).Succeeded);
            }
        }, Token))];
        await Task.WhenAll(writers);

        RunHistoryReadResult read = History().Read(PluginRunHistory.MaxRecords, Token);
        Assert.Equal(80, read.TotalRecords);
        Assert.Equal(80, read.Records.Select(r => r.PluginId.Value).Distinct().Count());
        Assert.Empty(System.IO.Directory.GetFiles(Directory, "*.tmp"));
    }

    [Fact]
    public void LockContentionTimesOutAndCancellationIsObserved()
    {
        Assert.True(Record(History()).Succeeded);
        byte[] before = File.ReadAllBytes(FilePath);
        using (new FileStream(FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            RunHistoryWriteResult timedOut = Record(History(TimeSpan.FromMilliseconds(200)));
            Assert.Equal(RunHistoryWriteStatus.Unavailable, timedOut.Status);
            Assert.Equal(StorageDiagnosticCode.HistoryLockTimeout, Assert.Single(timedOut.Diagnostics).Code);
            Assert.Equal(RunHistoryReadStatus.Unavailable, History(TimeSpan.FromMilliseconds(200)).Read(5, Token).Status);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.ThrowsAny<OperationCanceledException>(() => History(TimeSpan.FromSeconds(30)).Record(_env.Root, Result(), RunPublication.NotApplicable, null, cancellation.Token));
        }

        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Theory]
    [InlineData("{ not json", RunHistoryReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1}", RunHistoryReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1,\"records\":[],\"extra\":1}", RunHistoryReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1,\"records\":[],\"r\\u0065cords\":[]}", RunHistoryReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1.0,\"records\":[]}", RunHistoryReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1,\"records\":[{}]}", RunHistoryReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1,\"records\":[]} []", RunHistoryReadStatus.Corrupt)]
    [InlineData("\uFEFF{\"schemaVersion\":1,\"records\":[]}", RunHistoryReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":2,\"records\":\"anything\"}", RunHistoryReadStatus.UnsupportedVersion)]
    public void UnusableHistoryIsReportedAndNeverOverwritten(string content, RunHistoryReadStatus expected)
    {
        WriteHistoryFile(Encoding.UTF8.GetBytes(content));
        byte[] before = File.ReadAllBytes(FilePath);

        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(expected, read.Status);
        Assert.False(read.Succeeded);
        Assert.Empty(read.Records);

        RunHistoryWriteResult write = Record(History());
        Assert.Equal(RunHistoryWriteStatus.Unavailable, write.Status);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Empty(System.IO.Directory.GetFiles(Directory, "*.tmp"));
    }

    [Fact]
    public void RecordFieldsAreValidatedStrictly()
    {
        Assert.True(Record(History()).Succeeded);
        string valid = File.ReadAllText(FilePath);
        string[] corruptions =
        [
            valid.Replace("\"status\":\"TrustDenied\"", "\"status\":\"1\"", StringComparison.Ordinal),
            valid.Replace("\"status\":\"TrustDenied\"", "\"status\":\"trustdenied\"", StringComparison.Ordinal),
            valid.Replace("\"elapsedMilliseconds\":0", "\"elapsedMilliseconds\":-1", StringComparison.Ordinal),
            valid.Replace("\"elapsedMilliseconds\":0", "\"elapsedMilliseconds\":0.0", StringComparison.Ordinal),
            valid.Replace("\"pluginId\":\"a.ps1\"", "\"pluginId\":\"a.txt\"", StringComparison.Ordinal),
            valid.Replace("\"pluginId\":\"a.ps1\"", "\"pluginId\":\"a.ps1\",\"pluginId\":\"b.ps1\"", StringComparison.Ordinal),
            valid.Replace("\"jobObjectAssigned\":false", "\"jobObjectAssigned\":0", StringComparison.Ordinal),
            valid.Replace("\"code\":620", "\"code\":620,\"message\":\"x\"", StringComparison.Ordinal),
            valid.Replace("Z\",\"startedAtUtc\"", "+00:00\",\"startedAtUtc\"", StringComparison.Ordinal),
        ];
        foreach (string corrupt in corruptions)
        {
            Assert.NotEqual(valid, corrupt);
            File.WriteAllText(FilePath, corrupt);
            Assert.Equal(RunHistoryReadStatus.Corrupt, History().Read(10, Token).Status);
        }
    }

    [Fact]
    public void AFileOverTheByteLimitIsNotReadInFull()
    {
        WriteHistoryFile(new byte[PluginRunHistory.MaxHistoryBytes + 1]);
        RunHistoryReadResult read = History().Read(10, Token);
        Assert.Equal(RunHistoryReadStatus.Corrupt, read.Status);
        Assert.Equal(StorageDiagnosticCode.HistoryTooLarge, Assert.Single(read.Diagnostics).Code);
        Assert.Equal(RunHistoryWriteStatus.Unavailable, Record(History()).Status);
        Assert.Equal(PluginRunHistory.MaxHistoryBytes + 1, new FileInfo(FilePath).Length);
    }

    [Fact]
    public void AFailedPublicationKeepsTheCommittedHistory()
    {
        PluginRunHistory history = History();
        Assert.True(Record(history, Result("kept.ps1")).Succeeded);
        byte[] before = File.ReadAllBytes(FilePath);
        FileStream? blocker = null;
        var hooks = new RunHistoryTestHooks { BeforePublish = _ => blocker = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read) };
        try
        {
            RunHistoryWriteResult failed = Record(History(hooks: hooks), Result("lost.ps1"));
            Assert.Equal(RunHistoryWriteStatus.Unavailable, failed.Status);
            Assert.Equal(StorageDiagnosticCode.HistoryWriteFailed, Assert.Single(failed.Diagnostics).Code);
        }
        finally
        {
            blocker?.Dispose();
        }

        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Empty(System.IO.Directory.GetFiles(Directory, "*.tmp"));
        Assert.Equal(["kept.ps1"], history.Read(10, Token).Records.Select(r => r.PluginId.Value));
    }

    [Fact]
    public void RecordsOfOverlappingOrMismatchedRootsAreRejected()
    {
        var inside = new PluginRunHistory(Path.Combine(_env.Root, "logs"));
        Assert.Equal(RunHistoryWriteStatus.Rejected, inside.Record(_env.Root, Result(), RunPublication.NotApplicable, null, Token).Status);
        Assert.False(System.IO.Directory.Exists(Path.Combine(_env.Root, "logs")));
        Assert.Equal(RunHistoryWriteStatus.Rejected, History().Record(_env.Base, Result(), RunPublication.NotApplicable, null, Token).Status);
        Assert.Equal(RunHistoryWriteStatus.Rejected, History().Record("relative", Result(), RunPublication.NotApplicable, null, Token).Status);
        Assert.Throws<ArgumentException>(() => History().Record(_env.Root, Result(), RunPublication.Published, null, Token));
    }

    [Fact]
    public void UnsafeLocationsAreRefused()
    {
        string target = System.IO.Directory.CreateDirectory(Path.Combine(_env.Base, "target")).FullName;
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(Directory, target), "Junctions are not available.");
        RunHistoryWriteResult write = Record(History());
        Assert.Equal(RunHistoryWriteStatus.Unavailable, write.Status);
        Assert.Equal(StorageDiagnosticCode.StorageDirectoryUnsafe, Assert.Single(write.Diagnostics).Code);
        Assert.Equal(RunHistoryReadStatus.Unavailable, History().Read(1, Token).Status);
        Assert.Empty(System.IO.Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void HardLinkedHistoryFilesAreRefused()
    {
        Assert.True(Record(History()).Succeeded);
        string elsewhere = Path.Combine(_env.Base, "elsewhere.json");
        File.Move(FilePath, elsewhere);
        Assert.SkipUnless(TrustTestEnvironment.TryCreateHardLink(FilePath, elsewhere), "Hard links are not available.");
        byte[] before = File.ReadAllBytes(elsewhere);

        Assert.Equal(RunHistoryReadStatus.Unavailable, History().Read(1, Token).Status);
        RunHistoryWriteResult write = Record(History());
        Assert.Equal(StorageDiagnosticCode.StorageFileUnsafe, Assert.Single(write.Diagnostics).Code);
        Assert.Equal(before, File.ReadAllBytes(elsewhere));
    }

    [Fact]
    public void ADirectoryWhereTheFileBelongsIsRefused()
    {
        System.IO.Directory.CreateDirectory(FilePath);
        Assert.Equal(RunHistoryReadStatus.Unavailable, History().Read(1, Token).Status);
        Assert.Equal(RunHistoryWriteStatus.Unavailable, Record(History()).Status);
    }

    [Fact]
    public void InvalidDirectoriesAndOptionsAreCallerErrors()
    {
        Assert.Throws<ArgumentException>(() => new PluginRunHistory("relative"));
        Assert.Throws<ArgumentException>(() => new PluginRunHistory(@"\\server\share"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginRunHistory(Directory, new PluginRunHistoryOptions { LockTimeout = TimeSpan.FromMinutes(2) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => History().Read(PluginRunHistory.MaxRecords + 1, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => History().Read(-1, Token));
    }

    [Fact]
    public void TheDocumentedLimitsHold()
    {
        Assert.Equal(200, PluginRunHistory.MaxRecords);
        Assert.Equal(1024 * 1024, PluginRunHistory.MaxHistoryBytes);
        Assert.Equal(8 * 1024, PluginRunHistory.MaxRecordBytes);
        Assert.Equal(32, PluginRunHistory.MaxDiagnosticCodesPerRecord);
        Assert.Equal(TimeSpan.FromSeconds(5), PluginRunHistoryOptions.Default.LockTimeout);
        Assert.All(Enum.GetValues<StorageDiagnosticCode>(), code => Assert.InRange((int)code, 800, 849));
    }
}
