using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Tests.Plugins;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Trust;

// Persistence, synchronization and failure handling of the approval store, always in an isolated
// temporary location.
[SupportedOSPlatform("windows")]
public sealed class ApprovalStoreTests
{
    private static CancellationToken Token => TrustTestEnvironment.Token;

    private static string[] StoreDirectoryEntries(TrustTestEnvironment env) =>
        [.. Directory.EnumerateFileSystemEntries(env.StoreDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static void AssertNoTemporaryFiles(TrustTestEnvironment env) =>
        Assert.DoesNotContain(StoreDirectoryEntries(env), name => name.EndsWith(".tmp", StringComparison.Ordinal));

    [Fact]
    public void FirstApprovalCreatesTheStoreAndItsDirectory()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Assert.False(Directory.Exists(env.StoreDirectory));

        PluginApproval approval = env.Approve(env.Service(), "a.ps1");

        Assert.Equal(["approvals.json", "approvals.json.lock"], StoreDirectoryEntries(env));
        PluginApproval stored = Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
        Assert.Equal(approval, stored);
        Assert.Equal(0, new FileInfo(env.StorePath + ".lock").Length);
    }

    [Fact]
    public void ApprovalsSurviveRestartsAcrossRootsAndPlugins()
    {
        using var env = TrustTestEnvironment.Create();
        string otherRoot = Directory.CreateDirectory(Path.Combine(env.Base, "other")).FullName;
        env.Write("a.ps1", "a");
        env.Write("work/b.py", "b");
        File.WriteAllText(Path.Combine(otherRoot, "a.ps1"), "other a");

        PluginTrustService first = env.Service();
        PluginApproval a = env.Approve(first, "a.ps1");
        PluginApproval b = env.Approve(first, "work/b.py");
        PluginApproval other = env.Approve(first, "a.ps1", otherRoot);

        PluginTrustService restarted = env.Service();
        Assert.Equal(a.Identity, env.Check(restarted, "a.ps1").Approval);
        Assert.Equal(b.Identity, env.Check(restarted, "work/b.py").Approval);
        Assert.Equal(other.Identity, env.Check(restarted, "a.ps1", otherRoot).Approval);
        Assert.Equal(3, ApprovalStoreSerializerTests.ReadStore(env.StorePath).Count);
    }

    [Fact]
    public void StoreRecordsAreWrittenInDeterministicOrder()
    {
        using var env = TrustTestEnvironment.Create();
        string[] ids = ["z.ps1", "b/a.ps1", "A.ps1", "m.js", "b.ps1"];
        foreach (string id in ids)
        {
            env.Write(id, id);
        }

        PluginTrustService service = env.Service();
        foreach (string id in ids)
        {
            env.Approve(service, id);
        }

        string text = env.StoreText();
        string[] order = [.. ApprovalStoreSerializerTests.ReadStore(env.StorePath).Select(approval => approval.Key.Id.Value)];
        Assert.Equal(["A.ps1", "b.ps1", "b/a.ps1", "m.js", "z.ps1"], order);
        Assert.Equal(Encoding.UTF8.GetString(ApprovalStoreSerializer.Serialize(ApprovalStoreSerializerTests.ReadStore(env.StorePath))), text);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreContainsOnlyTheRequiredFields()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "# <xbar.title>Secret Title</xbar.title>\nWrite-Output 'secret content'\n");

        env.Approve(env.Service(), "a.ps1");

        string text = env.StoreText();
        Assert.DoesNotContain("Secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Write-Output", text, StringComparison.Ordinal);

        using var document = System.Text.Json.JsonDocument.Parse(text);
        string[] topLevel = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
        string[] record = [.. document.RootElement.GetProperty("approvals")[0].EnumerateObject().Select(property => property.Name)];
        Assert.Equal(["schemaVersion", "approvals"], topLevel);
        Assert.Equal(["root", "pluginId", "sha256", "generation", "approvedAtUtc"], record);
    }

    public static TheoryData<string, TrustDiagnosticCode> CorruptStores => new()
    {
        { string.Empty, TrustDiagnosticCode.StoreInvalid },
        { "not json", TrustDiagnosticCode.StoreInvalid },
        { "{\"schemaVersion\":1,\"approvals\":[]", TrustDiagnosticCode.StoreInvalid },
        { "{\"schemaVersion\":2,\"approvals\":[]}", TrustDiagnosticCode.StoreUnsupportedVersion },
        { "{\"schemaVersion\":1,\"schemaVersion\":1,\"approvals\":[]}", TrustDiagnosticCode.StoreInvalid },
        { "{\"schemaVersion\":1,\"approvals\":[],\"extra\":1}", TrustDiagnosticCode.StoreInvalid },
    };

    [Theory]
    [MemberData(nameof(CorruptStores))]
    public void InvalidStoreFailsEveryOperationAndIsNeverReplaced(string content, TrustDiagnosticCode code)
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Directory.CreateDirectory(env.StoreDirectory);
        File.WriteAllText(env.StorePath, content);
        PluginTrustService service = env.Service();
        PluginId id = PluginId.Create("a.ps1");
        ContentFingerprint fingerprint = service.Inspect(env.Root, id, Token).Plugin!.Fingerprint;

        PluginTrustDecision decision = service.CheckTrust(env.Root, id, Token);
        PluginApprovalResult approval = service.Approve(env.Root, id, fingerprint, Token);
        PluginRevocationResult revocation = service.Revoke(env.Root, id, Token);

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(PluginApprovalStatus.StoreUnavailable, approval.Status);
        Assert.Equal(PluginRevocationStatus.StoreUnavailable, revocation.Status);
        Assert.All(decision.Diagnostics.Concat(approval.Diagnostics).Concat(revocation.Diagnostics), d => Assert.Equal(code, d.Code));
        Assert.Equal(content, File.ReadAllText(env.StorePath));
        AssertNoTemporaryFiles(env);
    }

    [Fact]
    public void ValidRecordsInAnInvalidStoreAreNotTrusted()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Write("b.ps1", "2");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");
        env.Approve(service, "b.ps1");

        // Corrupt only the second record.
        string text = env.StoreText();
        int last = text.LastIndexOf("\"sha256\": \"", StringComparison.Ordinal) + 11;
        File.WriteAllText(env.StorePath, text[..last] + "X" + text[(last + 1)..]);

        Assert.Equal(PluginTrustStatus.StoreUnavailable, env.Check(service, "a.ps1").Status);
        Assert.Equal(PluginTrustStatus.StoreUnavailable, env.Check(service, "b.ps1").Status);
    }

    [Fact]
    public void ApprovalIsNotServedFromMemoryWhenTheStoreBecomesUnreadable()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");
        Assert.True(env.Check(service, "a.ps1").IsApproved);
        string valid = env.StoreText();

        File.WriteAllText(env.StorePath, "{");
        Assert.Equal(PluginTrustStatus.StoreUnavailable, env.Check(service, "a.ps1").Status);

        using (new FileStream(env.StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            PluginTrustDecision locked = env.Check(service, "a.ps1");
            Assert.Equal(PluginTrustStatus.StoreUnavailable, locked.Status);
            Assert.Equal(TrustDiagnosticCode.StoreUnreadable, Assert.Single(locked.Diagnostics).Code);
        }

        File.Delete(env.StorePath);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "a.ps1").Status);

        File.WriteAllText(env.StorePath, valid);
        Assert.True(env.Check(service, "a.ps1").IsApproved);
    }

    [Fact]
    public void UnreadableStoreIsDistinguishedFromAMissingOne()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Approve(env.Service(), "a.ps1");
        env.DenyReading(env.StorePath);

        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StoreUnreadable, Assert.Single(decision.Diagnostics).Code);
    }

    [Fact]
    public void OversizedStoreIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Directory.CreateDirectory(env.StoreDirectory);
        string padding = new(' ', PluginTrustService.MaxStoreBytes - 34);
        string atLimit = "{\"schemaVersion\":1,\"approvals\":[]" + padding + "}";
        Assert.Equal(PluginTrustService.MaxStoreBytes, Encoding.UTF8.GetByteCount(atLimit));

        File.WriteAllText(env.StorePath, atLimit);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(env.Service(), "a.ps1").Status);

        File.WriteAllText(env.StorePath, atLimit + " ");
        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");
        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StoreTooLarge, Assert.Single(decision.Diagnostics).Code);
    }

    [Fact]
    public void RecordLimitIsEnforcedWhenApproving()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("new.ps1", "1");
        env.Write("existing.ps1", "1");
        PluginTrustService service = env.Service();
        PluginApproval existing = env.Approve(service, "existing.ps1");

        // Fill the store to the limit with approvals under another root.
        var approvals = new List<PluginApproval> { existing };
        for (int i = 1; i < PluginTrustService.MaxApprovals; i++)
        {
            approvals.Add(new PluginApproval(
                new PluginTrustKey(PluginRoot.Create(@"C:\elsewhere"), PluginId.Create($"p{i}.ps1")),
                existing.Fingerprint,
                ApprovalGeneration.FromGuid(Guid.NewGuid()),
                existing.ApprovedAtUtc));
        }

        File.WriteAllBytes(env.StorePath, ApprovalStoreSerializer.Serialize(approvals));
        ContentFingerprint fingerprint = service.Inspect(env.Root, PluginId.Create("new.ps1"), Token).Plugin!.Fingerprint;

        PluginApprovalResult rejected = service.Approve(env.Root, PluginId.Create("new.ps1"), fingerprint, Token);
        Assert.Equal(PluginApprovalStatus.StoreUnavailable, rejected.Status);
        Assert.Equal(TrustDiagnosticCode.StoreTooManyRecords, Assert.Single(rejected.Diagnostics).Code);

        // Replacing an existing approval does not add a record.
        env.Write("existing.ps1", "2");
        Assert.Equal(PluginApprovalStatus.Approved, service.Approve(env.Root, PluginId.Create("existing.ps1"), service.Inspect(env.Root, PluginId.Create("existing.ps1"), Token).Plugin!.Fingerprint, Token).Status);
        Assert.Equal(PluginTrustService.MaxApprovals, ApprovalStoreSerializerTests.ReadStore(env.StorePath).Count);
    }

    [Fact]
    public void FailedReplacementLeavesTheStoreUnchangedAndRemovesTheTemporaryFile()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Write("b.ps1", "2");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");
        string before = env.StoreText();
        File.SetAttributes(env.StorePath, FileAttributes.ReadOnly);

        ContentFingerprint fingerprint = service.Inspect(env.Root, PluginId.Create("b.ps1"), Token).Plugin!.Fingerprint;
        PluginApprovalResult result = service.Approve(env.Root, PluginId.Create("b.ps1"), fingerprint, Token);

        Assert.Equal(PluginApprovalStatus.StoreUnavailable, result.Status);
        Assert.Equal(TrustDiagnosticCode.StoreWriteFailed, Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Approval);
        Assert.Equal(before, env.StoreText());
        AssertNoTemporaryFiles(env);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "b.ps1").Status);
        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void ReplacementBlockedByAnotherProgramFailsWithoutPartialWrites()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Write("b.ps1", "2");
        env.Approve(env.Service(), "a.ps1");
        string before = env.StoreText();
        string? temporary = null;
        string? temporaryContent = null;
        FileStream? reader = null;
        var hooks = new TrustTestHooks
        {
            BeforeStorePublish = path =>
            {
                temporary = path;
                temporaryContent = File.ReadAllText(path);

                // Another program opens the live store without allowing it to be replaced.
                reader = new FileStream(env.StorePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            },
        };
        PluginTrustService service = env.Service(hooks: hooks);

        PluginApprovalResult result;
        try
        {
            result = service.Approve(env.Root, PluginId.Create("b.ps1"), env.Service().Inspect(env.Root, PluginId.Create("b.ps1"), Token).Plugin!.Fingerprint, Token);
        }
        finally
        {
            reader?.Dispose();
        }

        Assert.Equal(PluginApprovalStatus.StoreUnavailable, result.Status);
        Assert.Equal(TrustDiagnosticCode.StoreWriteFailed, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(env.StoreDirectory, Path.GetDirectoryName(temporary));
        Assert.Contains("b.ps1", temporaryContent, StringComparison.Ordinal);
        Assert.False(File.Exists(temporary));
        Assert.Equal(before, env.StoreText());
    }

    [Fact]
    public void FailedInvalidationDeniesAndIsReportedAndRetriedByTheNextCheckOfTheSameService()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");
        env.Write("a.ps1", "v2");
        File.SetAttributes(env.StorePath, FileAttributes.ReadOnly);

        PluginTrustDecision failed = env.Check(service, "a.ps1");

        Assert.Equal(PluginTrustStatus.ContentChanged, failed.Status);
        Assert.False(failed.IsApproved);
        Assert.False(failed.ApprovalInvalidated);
        Assert.Equal(
            [TrustDiagnosticCode.ContentChanged, TrustDiagnosticCode.StoreWriteFailed, TrustDiagnosticCode.InvalidationNotPersisted],
            failed.Diagnostics.Select(d => d.Code));
        Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));

        // The same service retries the removal once the store is writable (see UnsavedInvalidationTests).
        File.SetAttributes(env.StorePath, FileAttributes.Normal);
        PluginTrustDecision retried = env.Check(service, "a.ps1");
        Assert.Equal(PluginTrustStatus.ContentChanged, retried.Status);
        Assert.True(retried.ApprovalInvalidated);
        Assert.Empty(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
    }

    [Fact]
    public void RevocationFailureIsReported()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");
        File.SetAttributes(env.StorePath, FileAttributes.ReadOnly);

        PluginRevocationResult result = service.Revoke(env.Root, PluginId.Create("a.ps1"), Token);

        Assert.Equal(PluginRevocationStatus.StoreUnavailable, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(TrustDiagnosticCode.StoreWriteFailed, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void ConcurrentOperationsDoNotLoseUnrelatedApprovals()
    {
        using var env = TrustTestEnvironment.Create();
        const int Count = 24;
        for (int i = 0; i < Count; i++)
        {
            env.Write($"p{i}.ps1", i.ToString(CultureInfo.InvariantCulture));
        }

        // Half are approved first and then revoked concurrently with approvals of the other half.
        for (int i = 0; i < Count; i += 2)
        {
            env.Approve(env.Service(), $"p{i}.ps1");
        }

        var options = new PluginTrustOptions { LockTimeout = TimeSpan.FromMinutes(1) };
        Parallel.For(0, Count, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Token }, i =>
        {
            // A separate instance per operation, as separate processes would have.
            PluginTrustService service = env.Service(options);
            PluginId id = PluginId.Create($"p{i}.ps1");
            if (i % 2 == 0)
            {
                Assert.Equal(PluginRevocationStatus.Revoked, service.Revoke(env.Root, id, Token).Status);
            }
            else
            {
                ContentFingerprint fingerprint = service.Inspect(env.Root, id, Token).Plugin!.Fingerprint;
                Assert.Equal(PluginApprovalStatus.Approved, service.Approve(env.Root, id, fingerprint, Token).Status);
            }

            Assert.Equal(i % 2 == 0 ? PluginTrustStatus.PendingApproval : PluginTrustStatus.Approved, service.CheckTrust(env.Root, id, Token).Status);
        });

        string[] approved = [.. ApprovalStoreSerializerTests.ReadStore(env.StorePath).Select(a => a.Key.Id.Value).Order(StringComparer.Ordinal)];
        string[] expected = [.. Enumerable.Range(0, Count).Where(i => i % 2 == 1).Select(i => $"p{i}.ps1").Order(StringComparer.Ordinal)];
        Assert.Equal(expected, approved);
        AssertNoTemporaryFiles(env);
    }

    [Fact]
    public async Task AnOperationWaitsForTheLockHolderAndThenSeesItsChanges()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Write("b.ps1", "2");
        ContentFingerprint b = env.Service().Inspect(env.Root, PluginId.Create("b.ps1"), Token).Plugin!.Fingerprint;
        var options = new PluginTrustOptions { LockTimeout = TimeSpan.FromMinutes(1) };
        Task<PluginApprovalResult>? second = null;
        bool completedWhileLocked = true;
        var hooks = new TrustTestHooks
        {
            BeforeStorePublish = _ =>
            {
                // While the first approval holds the lock, start the second and confirm it cannot complete.
                second = Task.Run(() => env.Service(options).Approve(env.Root, PluginId.Create("b.ps1"), b, Token), Token);
                completedWhileLocked = SpinWait.SpinUntil(() => second.IsCompleted, TimeSpan.FromMilliseconds(300));
            },
        };

        env.Approve(env.Service(options, hooks), "a.ps1");

        Assert.False(completedWhileLocked);
        Assert.Equal(PluginApprovalStatus.Approved, (await second!).Status);
        Assert.Equal(2, ApprovalStoreSerializerTests.ReadStore(env.StorePath).Count);
    }

    [Fact]
    public void LockAcquisitionIsBounded()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Directory.CreateDirectory(env.StoreDirectory);

        using (new FileStream(env.StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (TimeSpan timeout in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(100) })
            {
                PluginTrustService service = env.Service(new PluginTrustOptions { LockTimeout = timeout });
                PluginTrustDecision decision = env.Check(service, "a.ps1");
                PluginRevocationResult revocation = service.Revoke(env.Root, PluginId.Create("a.ps1"), Token);

                Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
                Assert.Equal(TrustDiagnosticCode.StoreLockTimeout, Assert.Single(decision.Diagnostics).Code);
                Assert.Equal(PluginRevocationStatus.StoreUnavailable, revocation.Status);
            }
        }

        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(env.Service(), "a.ps1").Status);
    }

    [Fact]
    public void WaitingForTheLockCanBeCancelled()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Directory.CreateDirectory(env.StoreDirectory);
        PluginTrustService service = env.Service(new PluginTrustOptions { LockTimeout = PluginTrustOptions.MaxLockTimeout });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);

        using (new FileStream(env.StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            Assert.Throws<OperationCanceledException>(() => service.CheckTrust(env.Root, PluginId.Create("a.ps1"), cancellation.Token));
        }
    }

    [Fact]
    public void OnlyTheOperationsOwnTemporaryFilesAreRemoved()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Directory.CreateDirectory(env.StoreDirectory);
        string unrelated = env.StorePath + ".0123456789abcdef0123456789abcdef.tmp";
        File.WriteAllText(unrelated, "left by a crashed process");
        File.WriteAllText(Path.Combine(env.StoreDirectory, "notes.txt"), "user file");

        env.Approve(env.Service(), "a.ps1");
        env.Service().Revoke(env.Root, PluginId.Create("a.ps1"), Token);

        Assert.Equal("left by a crashed process", File.ReadAllText(unrelated));
        Assert.Equal(["approvals.json", "approvals.json.0123456789abcdef0123456789abcdef.tmp", "approvals.json.lock", "notes.txt"], StoreDirectoryEntries(env));
    }

    [Fact]
    public void StoreDirectoryJunctionIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        string target = Directory.CreateDirectory(Path.Combine(env.Base, "elsewhere")).FullName;
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(env.StoreDirectory, target), "Directory junctions cannot be created on this machine.");

        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void JunctionAboveTheStoreDirectoryIsRejectedBeforeAnythingIsCreated()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        string target = Directory.CreateDirectory(Path.Combine(env.Base, "elsewhere")).FullName;
        string link = Path.Combine(env.Base, "link");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(link, target), "Directory junctions cannot be created on this machine.");
        var service = new PluginTrustService(Path.Combine(link, "trust", "approvals.json"));

        PluginTrustDecision decision = service.CheckTrust(env.Root, PluginId.Create("a.ps1"), Token);

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void StoreFileSymbolicLinkIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Approve(env.Service(), "a.ps1");
        string elsewhere = Path.Combine(env.Base, "elsewhere.json");
        File.Move(env.StorePath, elsewhere);
        Assert.SkipUnless(TemporaryDirectory.TryCreateFileSymbolicLink(env.StorePath, elsewhere), "Symbolic links cannot be created here (Developer Mode or elevation is required).");

        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
    }

    [Fact]
    public void LockFileSymbolicLinkIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Directory.CreateDirectory(env.StoreDirectory);
        string elsewhere = Path.Combine(env.Base, "elsewhere.lock");
        File.WriteAllText(elsewhere, string.Empty);
        Assert.SkipUnless(TemporaryDirectory.TryCreateFileSymbolicLink(env.StorePath + ".lock", elsewhere), "Symbolic links cannot be created here (Developer Mode or elevation is required).");

        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
    }

    [Fact]
    public void UnexpectedDirectoriesInPlaceOfStoreFilesAreRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Directory.CreateDirectory(env.StorePath);

        PluginTrustDecision storeIsDirectory = env.Check(env.Service(), "a.ps1");
        Directory.Delete(env.StorePath);
        File.Delete(env.StorePath + ".lock");
        Directory.CreateDirectory(env.StorePath + ".lock");
        PluginTrustDecision lockIsDirectory = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.StoreUnavailable, storeIsDirectory.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(storeIsDirectory.Diagnostics).Code);
        Assert.Equal(PluginTrustStatus.StoreUnavailable, lockIsDirectory.Status);
        Assert.Equal(TrustDiagnosticCode.StoreLockFailed, Assert.Single(lockIsDirectory.Diagnostics).Code);
    }

    [Fact]
    public void StoreDirectoryThatIsAFileIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        File.WriteAllText(env.StoreDirectory, "not a directory");

        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
    }

    [Theory]
    [InlineData("approvals.json")]
    [InlineData(@"store\approvals.json")]
    [InlineData(@"C:store\approvals.json")]
    [InlineData(@"\\server\share\approvals.json")]
    [InlineData(@"\\?\C:\store\approvals.json")]
    [InlineData(@"C:\store\..\approvals.json")]
    [InlineData(@"C:\store\\approvals.json")]
    [InlineData(@"C:\\approvals.json")]
    [InlineData("C://approvals.json")]
    [InlineData(@"C:\store\")]
    [InlineData(@"C:\store\approvals.txt")]
    [InlineData(@"C:\store\approvals.json:stream")]
    [InlineData(@"C:\store\.json")]
    [InlineData(@"C:\store\approvals.json.")]
    [InlineData("")]
    public void UnsafeStoreLocationsAreRejectedAtConstruction(string storePath) =>
        Assert.ThrowsAny<ArgumentException>(() => new PluginTrustService(storePath));

    [Fact]
    public void StorePathIsNormalized()
    {
        Assert.Equal(@"C:\store\approvals.json", new PluginTrustService("C:/store/approvals.json").StorePath);
        Assert.Equal(@"D:\trust.json", new PluginTrustService(@"D:\trust.json").StorePath);
    }

    [Fact]
    public void DefaultStoreIsPerUserLocalApplicationData()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The default store location is defined for Windows.");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

        // Only the path is computed: no operation runs against the real per-user store.
        string path = PluginTrustService.GetDefaultStorePath();

        Assert.Equal(Path.Combine(local, "TrayWisp", "trust", "approvals.json"), path);
        Assert.Equal(path, new PluginTrustService(path).StorePath);
    }

    [Fact]
    public void LockTimeoutOptionIsValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginTrustService(@"C:\store\approvals.json", new PluginTrustOptions { LockTimeout = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginTrustService(@"C:\store\approvals.json", new PluginTrustOptions { LockTimeout = PluginTrustOptions.MaxLockTimeout + TimeSpan.FromTicks(1) }));
        Assert.Equal(TimeSpan.FromSeconds(10), PluginTrustOptions.Default.LockTimeout);
    }

    [Fact]
    public void InspectionDoesNotTouchTheStore()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");

        Assert.Equal(PluginInspectionStatus.Inspected, env.Service().Inspect(env.Root, PluginId.Create("a.ps1"), Token).Status);

        Assert.False(Directory.Exists(env.StoreDirectory));
    }

    [Fact]
    public void StoreDirectoryIsHeldForTheWholeTransaction()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        string moved = env.StoreDirectory + "-moved";
        var attempts = new List<Exception?>();
        var hooks = new TrustTestHooks
        {
            StoreLocked = () => attempts.Add(Record.Exception(() => Directory.Move(env.StoreDirectory, moved))),
            BeforeStorePublish = _ => attempts.Add(Record.Exception(() => Directory.Move(env.StoreDirectory, moved))),
        };
        PluginTrustService service = env.Service(hooks: hooks);

        env.Approve(service, "a.ps1");
        PluginTrustDecision decision = env.Check(service, "a.ps1");

        // Locked for the approval and for the check; published once.
        Assert.Equal(3, attempts.Count);
        Assert.All(attempts, attempt => Assert.True(attempt is IOException or UnauthorizedAccessException, attempt?.ToString()));
        Assert.False(Directory.Exists(moved));
        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
        env.AssertStoreDirectoryReleased();
    }

    [Fact]
    public void AncestorRenameDuringPublicationNeverPublishesElsewhere()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        string outer = Path.Combine(env.Base, "outer");
        string storePath = Path.Combine(outer, "store", "approvals.json");
        bool renamed = false;
        var hooks = new TrustTestHooks
        {
            BeforeStorePublish = _ =>
            {
                try
                {
                    // Move the validated directory's ancestor away and put a fresh directory in its place.
                    Directory.Move(outer, outer + "-moved");
                    renamed = true;
                    Directory.CreateDirectory(Path.Combine(outer, "store"));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The file system refused to rename a directory with open handles below it.
                }
            },
        };
        var service = new PluginTrustService(storePath, options: null, TimeProvider.System, hooks);
        ContentFingerprint fingerprint = service.Inspect(env.Root, PluginId.Create("a.ps1"), Token).Plugin!.Fingerprint;

        PluginApprovalResult result = service.Approve(env.Root, PluginId.Create("a.ps1"), fingerprint, Token);

        if (renamed)
        {
            Assert.Equal(PluginApprovalStatus.StoreUnavailable, result.Status);
            Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(result.Diagnostics).Code);
            Assert.False(File.Exists(storePath));
            Assert.False(File.Exists(Path.Combine(outer + "-moved", "store", "approvals.json")));
        }
        else
        {
            Assert.Equal(PluginApprovalStatus.Approved, result.Status);
            Assert.Single(ApprovalStoreSerializerTests.ReadStore(storePath));
        }
    }

    [Fact]
    public void HardLinkedStoreFileIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Approve(env.Service(), "a.ps1");
        string before = env.StoreText();
        Assert.SkipUnless(TrustTestEnvironment.TryCreateHardLink(Path.Combine(env.Base, "copy.json"), env.StorePath), "Hard links cannot be created here.");

        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");
        PluginRevocationResult revocation = env.Service().Revoke(env.Root, PluginId.Create("a.ps1"), Token);

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
        Assert.Equal(PluginRevocationStatus.StoreUnavailable, revocation.Status);
        Assert.Equal(before, env.StoreText());
        env.AssertStoreDirectoryReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HardLinkedLockFileIsRejected(bool linkToTheStore)
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Approve(env.Service(), "a.ps1");
        string lockPath = env.StorePath + ".lock";
        File.Delete(lockPath);
        string other = Path.Combine(env.Base, "other.txt");
        File.WriteAllText(other, "unrelated");
        Assert.SkipUnless(TrustTestEnvironment.TryCreateHardLink(lockPath, linkToTheStore ? env.StorePath : other), "Hard links cannot be created here.");

        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
        Assert.Equal("unrelated", File.ReadAllText(other));
        env.AssertStoreDirectoryReleased();
    }

    [Fact]
    public void CaseSensitiveStoreDirectoryIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        Directory.CreateDirectory(env.StoreDirectory);
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(env.StoreDirectory), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");

        PluginTrustDecision decision = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(env.StoreDirectory));
    }

    [Fact]
    public void CaseSensitiveAncestorOfTheStoreIsRejectedBeforeAnythingIsCreated()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        string parent = Directory.CreateDirectory(Path.Combine(env.Base, "cs")).FullName;
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(parent), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");
        var service = new PluginTrustService(Path.Combine(parent, "trust", "approvals.json"));

        PluginTrustDecision decision = service.CheckTrust(env.Root, PluginId.Create("a.ps1"), Token);

        Assert.Equal(PluginTrustStatus.StoreUnavailable, decision.Status);
        Assert.Equal(TrustDiagnosticCode.StorePathUnsafe, Assert.Single(decision.Diagnostics).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    [Fact]
    public void StoreHandlesAreReleasedOnSuccessFailureAndCancellation()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        PluginTrustService service = env.Service();

        // Success.
        env.Approve(service, "a.ps1");
        env.AssertStoreDirectoryReleased();

        // Cancellation after the lock was acquired.
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            PluginTrustService cancelling = env.Service(hooks: new TrustTestHooks { StoreLocked = cancellation.Cancel });
            Assert.Throws<OperationCanceledException>(() => cancelling.CheckTrust(env.Root, PluginId.Create("a.ps1"), cancellation.Token));
        }

        env.AssertStoreDirectoryReleased();

        // Lock timeout and cancellation while waiting for another holder.
        using (new FileStream(env.StorePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            PluginTrustService impatient = env.Service(new PluginTrustOptions { LockTimeout = TimeSpan.Zero });
            Assert.Equal(TrustDiagnosticCode.StoreLockTimeout, Assert.Single(env.Check(impatient, "a.ps1").Diagnostics).Code);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
            PluginTrustService patient = env.Service(new PluginTrustOptions { LockTimeout = PluginTrustOptions.MaxLockTimeout });
            Assert.Throws<OperationCanceledException>(() => patient.CheckTrust(env.Root, PluginId.Create("a.ps1"), cancellation.Token));
        }

        env.AssertStoreDirectoryReleased();

        // Failure: an invalid store.
        File.WriteAllText(env.StorePath, "{}");
        Assert.Equal(PluginTrustStatus.StoreUnavailable, env.Check(service, "a.ps1").Status);
        env.AssertStoreDirectoryReleased();

        // Failure: an unwritable store.
        File.Delete(env.StorePath);
        env.Approve(service, "a.ps1");
        File.SetAttributes(env.StorePath, FileAttributes.ReadOnly);
        Assert.Equal(PluginRevocationStatus.StoreUnavailable, service.Revoke(env.Root, PluginId.Create("a.ps1"), Token).Status);
        File.SetAttributes(env.StorePath, FileAttributes.Normal);
        env.AssertStoreDirectoryReleased();
    }
}
