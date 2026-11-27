using System.Runtime.Versioning;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Trust;

[SupportedOSPlatform("windows")]
public sealed class PluginApprovalWorkflowTests
{
    private static CancellationToken Token => TrustTestEnvironment.Token;

    [Fact]
    public void DiscoveredPluginIsPendingUntilApproved()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("clock.5s.ps1", "Get-Date\n");
        PluginTrustService service = env.Service();

        PluginDiscoveryResult discovery = PluginDiscovery.Discover(env.Root, cancellationToken: Token);
        PluginDescriptor plugin = Assert.Single(discovery.Plugins);

        PluginTrustDecision decision = service.CheckTrust(env.Root, plugin.Id, Token);

        Assert.Equal(PluginTrustStatus.PendingApproval, decision.Status);
        Assert.False(decision.IsApproved);
        Assert.Null(decision.Approval);
        Assert.Equal(TrustDiagnosticCode.ApprovalPending, Assert.Single(decision.Diagnostics).Code);
        Assert.NotNull(decision.Current);

        // A pending check neither approves nor creates a store.
        Assert.False(File.Exists(env.StorePath));
    }

    [Fact]
    public void ExplicitApprovalOfTheReviewedFingerprintApproves()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("work/clock.5s.ps1", "Get-Date\n");
        PluginTrustService service = env.Service();
        PluginId id = PluginId.Create("work/clock.5s.ps1");

        PluginInspectionResult inspection = service.Inspect(env.Root, id, Token);
        Assert.Equal(PluginInspectionStatus.Inspected, inspection.Status);
        Assert.Empty(inspection.Diagnostics);
        Assert.Equal(9, inspection.Plugin!.ContentLength);

        // Inspection alone does not approve.
        Assert.Equal(PluginTrustStatus.PendingApproval, service.CheckTrust(env.Root, id, Token).Status);

        PluginApprovalResult approval = service.Approve(env.Root, id, inspection.Plugin.Fingerprint, Token);
        Assert.Equal(PluginApprovalStatus.Approved, approval.Status);
        Assert.True(approval.Succeeded);
        Assert.Equal(inspection.Plugin.Fingerprint, approval.Approval!.Fingerprint);

        PluginTrustDecision decision = service.CheckTrust(env.Root, id, Token);
        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
        Assert.True(decision.IsApproved);
        Assert.Empty(decision.Diagnostics);
        Assert.Equal(approval.Approval.Identity, decision.Approval);
    }

    [Fact]
    public void RepeatedChecksOfUnchangedContentStayApproved()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        PluginTrustService service = env.Service();
        PluginApproval approval = env.Approve(service, "a.ps1");
        string store = env.StoreText();

        for (int i = 0; i < 3; i++)
        {
            PluginTrustDecision decision = env.Check(service, "a.ps1");
            Assert.Equal(PluginTrustStatus.Approved, decision.Status);
            Assert.Equal(approval.Identity, decision.Approval);
        }

        // Successful checks do not rewrite the store.
        Assert.Equal(store, env.StoreText());
    }

    [Fact]
    public void FileChangedBetweenInspectionAndApprovalIsNotApproved()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "reviewed\n");
        PluginTrustService service = env.Service();
        PluginId id = PluginId.Create("a.ps1");
        ContentFingerprint reviewed = service.Inspect(env.Root, id, Token).Plugin!.Fingerprint;

        env.Write("a.ps1", "malicious\n");
        PluginApprovalResult result = service.Approve(env.Root, id, reviewed, Token);

        Assert.Equal(PluginApprovalStatus.FingerprintMismatch, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Approval);
        Assert.NotEqual(reviewed, result.Current!.Fingerprint);
        Assert.Equal(TrustDiagnosticCode.FingerprintMismatch, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(PluginTrustStatus.PendingApproval, service.CheckTrust(env.Root, id, Token).Status);
        Assert.False(File.Exists(env.StorePath));
    }

    [Fact]
    public void ApprovalRequiresTheCurrentFingerprintNotAnyCallerSuppliedHash()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "content");
        PluginTrustService service = env.Service();
        ContentFingerprint arbitrary = ContentFingerprint.Parse(new string('a', 64));

        PluginApprovalResult result = service.Approve(env.Root, PluginId.Create("a.ps1"), arbitrary, Token);

        Assert.Equal(PluginApprovalStatus.FingerprintMismatch, result.Status);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void ModifiedContentInvalidatesTheApprovalPersistently()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");

        env.Write("a.ps1", "v2");
        PluginTrustDecision changed = env.Check(service, "a.ps1");

        Assert.Equal(PluginTrustStatus.ContentChanged, changed.Status);
        Assert.True(changed.ApprovalInvalidated);
        Assert.Null(changed.Approval);
        Assert.Equal([TrustDiagnosticCode.ContentChanged, TrustDiagnosticCode.ApprovalInvalidated], changed.Diagnostics.Select(d => d.Code));

        // The invalidation survives a restart (a new service instance reading the store from disk).
        PluginTrustDecision afterRestart = env.Check(env.Service(), "a.ps1");
        Assert.Equal(PluginTrustStatus.PendingApproval, afterRestart.Status);
        Assert.DoesNotContain("a.ps1", env.StoreText(), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoringPreviouslyApprovedBytesAfterInvalidationDoesNotRestoreApproval()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");

        env.Write("a.ps1", "v2");
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);

        env.Write("a.ps1", "v1");
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void ChangedContentCanBeReapprovedExplicitlyWithANewGeneration()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval first = env.Approve(service, "a.ps1");

        env.Write("a.ps1", "v2");
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        PluginApproval second = env.Approve(service, "a.ps1");

        Assert.NotEqual(first.Generation, second.Generation);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        PluginTrustDecision decision = env.Check(service, "a.ps1");
        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
        Assert.Equal(second.Identity, decision.Approval);
    }

    [Fact]
    public void ApprovingNewContentWithoutAnInterveningCheckReplacesTheOldApproval()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval first = env.Approve(service, "a.ps1");

        env.Write("a.ps1", "v2");
        PluginApproval second = env.Approve(service, "a.ps1");

        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "a.ps1").Status);
        Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
    }

    [Fact]
    public void RepeatedApprovalOfUnchangedContentKeepsTheGeneration()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval first = env.Approve(service, "a.ps1");
        string store = env.StoreText();

        PluginApprovalResult again = service.Approve(env.Root, PluginId.Create("a.ps1"), first.Fingerprint, Token);

        Assert.Equal(PluginApprovalStatus.AlreadyApproved, again.Status);
        Assert.True(again.Succeeded);
        Assert.Equal(first, again.Approval);
        Assert.Equal(store, env.StoreText());
    }

    [Fact]
    public void RevocationDeniesThePlugin()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval approval = env.Approve(service, "a.ps1");

        PluginRevocationResult revoked = service.Revoke(env.Root, PluginId.Create("a.ps1"), Token);

        Assert.Equal(PluginRevocationStatus.Revoked, revoked.Status);
        Assert.Equal(approval, revoked.RevokedApproval);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "a.ps1").Status);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(env.Service(), "a.ps1").Status);

        PluginRevocationResult again = service.Revoke(env.Root, PluginId.Create("a.ps1"), Token);
        Assert.Equal(PluginRevocationStatus.NotApproved, again.Status);
        Assert.True(again.Succeeded);
    }

    [Fact]
    public void RevocationWorksForAPluginThatNoLongerExists()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("gone.ps1", "v1");
        PluginTrustService service = env.Service();
        env.Approve(service, "gone.ps1");
        File.Delete(path);

        Assert.Equal(PluginRevocationStatus.Revoked, service.Revoke(env.Root, PluginId.Create("gone.ps1"), Token).Status);
        Assert.Empty(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
    }

    [Fact]
    public void ReapprovalOfTheSameBytesAfterRevocationCreatesANewGeneration()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "same bytes");
        PluginTrustService service = env.Service();
        PluginApproval first = env.Approve(service, "a.ps1");

        service.Revoke(env.Root, PluginId.Create("a.ps1"), Token);
        PluginApproval second = env.Approve(service, "a.ps1");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.Generation, second.Generation);
    }

    [Fact]
    public void ApprovalIdentityIsCurrentUntilRevoked()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApprovalIdentity identity = env.Approve(service, "a.ps1").Identity;

        PluginTrustDecision current = service.CheckTrust(identity, Token);
        Assert.Equal(PluginTrustStatus.Approved, current.Status);
        Assert.Equal(identity, current.Approval);

        service.Revoke(env.Root, PluginId.Create("a.ps1"), Token);
        PluginTrustDecision afterRevocation = service.CheckTrust(identity, Token);
        Assert.Equal(PluginTrustStatus.StaleApproval, afterRevocation.Status);
        Assert.Equal(TrustDiagnosticCode.StaleApproval, Assert.Single(afterRevocation.Diagnostics).Code);
    }

    [Fact]
    public void OldApprovalIdentityStaysStaleAfterSameContentIsReapproved()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "same bytes");
        PluginTrustService service = env.Service();
        PluginApprovalIdentity old = env.Approve(service, "a.ps1").Identity;

        service.Revoke(env.Root, PluginId.Create("a.ps1"), Token);
        PluginApprovalIdentity current = env.Approve(service, "a.ps1").Identity;

        Assert.Equal(PluginTrustStatus.StaleApproval, service.CheckTrust(old, Token).Status);
        Assert.Equal(PluginTrustStatus.Approved, service.CheckTrust(current, Token).Status);

        // The plain check of the same plugin is approved: only the old identity is stale.
        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void OldApprovalIdentityIsRejectedAfterContentChange()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApprovalIdentity old = env.Approve(service, "a.ps1").Identity;

        env.Write("a.ps1", "v2");
        PluginTrustDecision changed = service.CheckTrust(old, Token);
        Assert.Equal(PluginTrustStatus.ContentChanged, changed.Status);
        Assert.True(changed.ApprovalInvalidated);

        PluginApprovalIdentity reapproved = env.Approve(service, "a.ps1").Identity;
        Assert.Equal(PluginTrustStatus.StaleApproval, service.CheckTrust(old, Token).Status);
        Assert.Equal(PluginTrustStatus.Approved, service.CheckTrust(reapproved, Token).Status);
    }

    [Fact]
    public void ForgedApprovalIdentityWithTheCurrentFingerprintIsStale()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval approval = env.Approve(service, "a.ps1");

        var forged = new PluginApprovalIdentity(approval.Key, approval.Fingerprint, ApprovalGeneration.FromGuid(Guid.NewGuid()));

        Assert.Equal(PluginTrustStatus.StaleApproval, service.CheckTrust(forged, Token).Status);
    }

    [Fact]
    public void TransientFailuresDenyButKeepTheApproval()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval approval = env.Approve(service, "a.ps1");

        // Locked for writing by another program.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            PluginTrustDecision locked = env.Check(service, "a.ps1");
            Assert.Equal(PluginTrustStatus.PluginUnavailable, locked.Status);
            Assert.Equal(TrustDiagnosticCode.FileLocked, Assert.Single(locked.Diagnostics).Code);
            Assert.False(locked.ApprovalInvalidated);
        }

        // Temporarily missing.
        File.Move(path, path + ".bak");
        PluginTrustDecision missing = env.Check(service, "a.ps1");
        Assert.Equal(PluginTrustStatus.PluginUnavailable, missing.Status);
        Assert.Equal(TrustDiagnosticCode.PluginNotFound, Assert.Single(missing.Diagnostics).Code);
        File.Move(path + ".bak", path);

        PluginTrustDecision decision = env.Check(service, "a.ps1");
        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
        Assert.Equal(approval.Identity, decision.Approval);
    }

    [Fact]
    public void IncompleteDiscoveryDoesNotAffectApprovals()
    {
        using var env = TrustTestEnvironment.Create();
        string lockedPath = env.Write("locked.ps1", "1");
        env.Write("other.ps1", "2");
        PluginTrustService service = env.Service();
        env.Approve(service, "locked.ps1");
        env.Approve(service, "other.ps1");
        string store = env.StoreText();

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            PluginDiscoveryResult discovery = PluginDiscovery.Discover(env.Root, cancellationToken: Token);
            Assert.False(discovery.IsComplete);
            Assert.DoesNotContain(discovery.Plugins, plugin => plugin.Id.Value == "locked.ps1");

            Assert.Equal(PluginTrustStatus.PluginUnavailable, env.Check(service, "locked.ps1").Status);
            Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "other.ps1").Status);
        }

        Assert.Equal(store, env.StoreText());
        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "locked.ps1").Status);
    }

    [Fact]
    public void SameIdentityUnderDifferentRootsIsApprovedSeparately()
    {
        using var env = TrustTestEnvironment.Create();
        string otherRoot = Directory.CreateDirectory(Path.Combine(env.Base, "other")).FullName;
        env.Write("clock.ps1", "same");
        File.WriteAllText(Path.Combine(otherRoot, "clock.ps1"), "same");
        PluginTrustService service = env.Service();

        env.Approve(service, "clock.ps1");

        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "clock.ps1").Status);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "clock.ps1", otherRoot).Status);
    }

    [Fact]
    public void IdentityAndRootCompareCaseInsensitively()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("Work/Clock.ps1", "v1");
        PluginTrustService service = env.Service();
        env.Approve(service, "Work/Clock.ps1");

        PluginTrustDecision decision = service.CheckTrust(env.Root.ToUpperInvariant(), PluginId.Create("work/CLOCK.PS1"), Token);

        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
    }

    [Fact]
    public void EquivalentRootSpellingsShareOneApproval()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1", env.Root + "\\");

        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "a.ps1", env.Root).Status);
        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "a.ps1", env.Root.Replace('\\', '/')).Status);
        Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
        Assert.Contains(ApprovalStoreSerializerTests.EscapeJson(env.Root) + "\"", env.StoreText(), StringComparison.Ordinal);
    }

    [Fact]
    public void RenamedPluginIsADifferentPendingPlugin()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("old.ps1", "v1");
        PluginTrustService service = env.Service();
        env.Approve(service, "old.ps1");

        File.Move(path, env.PluginPath("new.ps1"));

        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "new.ps1").Status);
        Assert.Equal(PluginTrustStatus.PluginUnavailable, env.Check(service, "old.ps1").Status);

        // The old approval is not transferred, and not removed automatically.
        Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
    }

    [Fact]
    public void ApprovingOnePluginLeavesOthersPending()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        env.Write("sub/b.ps1", "1");
        PluginTrustService service = env.Service();

        env.Approve(service, "a.ps1");

        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "sub/b.ps1").Status);
    }

    [Fact]
    public void ApprovalTimestampIsRecordedInUtcButIsOnlyInformational()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(6));
        PluginTrustService service = env.Service(time: time);

        PluginApproval approval = env.Approve(service, "a.ps1");
        Assert.Equal(time.GetUtcNow(), approval.ApprovedAtUtc);
        Assert.Contains("\"approvedAtUtc\": \"2026-01-02T03:04:05.0000006Z\"", env.StoreText(), StringComparison.Ordinal);

        // Time moving on, or backwards, changes nothing.
        time.Now = DateTimeOffset.MinValue.ToUniversalTime();
        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "a.ps1").Status);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
