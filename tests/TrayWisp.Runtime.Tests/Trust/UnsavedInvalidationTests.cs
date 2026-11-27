using System.Runtime.Versioning;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Trust;

// Confirmed content changes whose invalidation cannot be saved, and obsolete approvals observed while
// approving. See docs/trust-model.md ("Unsaved invalidations").
[SupportedOSPlatform("windows")]
public sealed class UnsavedInvalidationTests
{
    private static CancellationToken Token => TrustTestEnvironment.Token;

    private static void SetStoreReadOnly(TrustTestEnvironment env, bool readOnly) =>
        File.SetAttributes(env.StorePath, readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal);

    // Approves v1, changes it to v2 and checks it while the store is read-only, so the invalidation is unsaved.
    private static PluginApproval ApproveThenChangeWithReadOnlyStore(TrustTestEnvironment env, PluginTrustService service, string relativePath = "a.ps1")
    {
        env.Write(relativePath, "v1");
        PluginApproval approval = env.Approve(service, relativePath);
        env.Write(relativePath, "v2");
        SetStoreReadOnly(env, readOnly: true);
        env.OnDispose(() => SetStoreReadOnly(env, readOnly: false));
        return approval;
    }

    [Fact]
    public void ConfirmedChangeWithAReadOnlyStoreIsDeniedAndReportedAsUnsaved()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        ApproveThenChangeWithReadOnlyStore(env, service);

        PluginTrustDecision decision = env.Check(service, "a.ps1");

        Assert.Equal(PluginTrustStatus.ContentChanged, decision.Status);
        Assert.False(decision.IsApproved);
        Assert.False(decision.ApprovalInvalidated);
        Assert.Null(decision.ValidatedFilePath);
        Assert.Equal(
            [TrustDiagnosticCode.ContentChanged, TrustDiagnosticCode.StoreWriteFailed, TrustDiagnosticCode.InvalidationNotPersisted],
            decision.Diagnostics.Select(d => d.Code));

        // The diagnostic does not promise that the change will be detected again.
        string message = decision.Diagnostics[^1].Message;
        Assert.DoesNotContain("detected again", message, StringComparison.Ordinal);
        Assert.Contains("Other trust service instances", message, StringComparison.Ordinal);
        Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
    }

    [Fact]
    public void RestoredOriginalBytesStayDeniedByTheSameServiceWhileTheInvalidationIsUnsaved()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        PluginApproval approval = ApproveThenChangeWithReadOnlyStore(env, service);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);

        env.Write("a.ps1", "v1");
        PluginTrustDecision byId = env.Check(service, "a.ps1");
        PluginTrustDecision byIdentity = service.CheckTrust(approval.Identity, Token);

        foreach (PluginTrustDecision decision in new[] { byId, byIdentity })
        {
            Assert.Equal(PluginTrustStatus.ContentChanged, decision.Status);
            Assert.Null(decision.Approval);
            Assert.Null(decision.ValidatedFilePath);
            Assert.False(decision.ApprovalInvalidated);
            Assert.Equal(
                [TrustDiagnosticCode.ContentChanged, TrustDiagnosticCode.StoreWriteFailed, TrustDiagnosticCode.InvalidationNotPersisted],
                decision.Diagnostics.Select(d => d.Code));
        }

        // An explicit approval with the old fingerprint cannot keep the invalidated generation either.
        PluginApprovalResult reapproval = service.Approve(env.Root, PluginId.Create("a.ps1"), approval.Fingerprint, Token);
        Assert.Equal(PluginApprovalStatus.StoreUnavailable, reapproval.Status);
        Assert.Null(reapproval.Approval);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void UnsavedInvalidationIsNotKnownToOtherServiceInstances()
    {
        // A documented limitation: the store still holds the old approval, and a different instance (or a
        // later process) only sees the store. With the old bytes back, it approves the old generation.
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        PluginApproval approval = ApproveThenChangeWithReadOnlyStore(env, service);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        env.Write("a.ps1", "v1");

        PluginTrustDecision other = env.Check(env.Service(), "a.ps1");

        Assert.Equal(PluginTrustStatus.Approved, other.Status);
        Assert.Equal(approval.Identity, other.Approval);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void InvalidationIsRetriedOnceTheStoreIsWritableEvenIfTheOriginalBytesReturned()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        ApproveThenChangeWithReadOnlyStore(env, service);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        env.Write("a.ps1", "v1");
        SetStoreReadOnly(env, readOnly: false);

        PluginTrustDecision retried = env.Check(service, "a.ps1");

        Assert.Equal(PluginTrustStatus.ContentChanged, retried.Status);
        Assert.True(retried.ApprovalInvalidated);
        Assert.Equal([TrustDiagnosticCode.ContentChanged, TrustDiagnosticCode.ApprovalInvalidated], retried.Diagnostics.Select(d => d.Code));
        Assert.Empty(ApprovalStoreSerializerTests.ReadStore(env.StorePath));

        // Saved: the same service, and a new one after a restart, see an unapproved plugin.
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "a.ps1").Status);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(env.Service(), "a.ps1").Status);
    }

    [Fact]
    public void ExplicitReapprovalAfterAnUnsavedInvalidationCreatesANewGeneration()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        PluginApproval old = ApproveThenChangeWithReadOnlyStore(env, service);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        env.Write("a.ps1", "v1");
        SetStoreReadOnly(env, readOnly: false);

        PluginApprovalResult reapproval = service.Approve(env.Root, PluginId.Create("a.ps1"), old.Fingerprint, Token);

        // Not the idempotent path: the invalidated generation is replaced, atomically, by a new one.
        Assert.Equal(PluginApprovalStatus.Approved, reapproval.Status);
        Assert.True(reapproval.ApprovalInvalidated);
        Assert.Empty(reapproval.Diagnostics);
        Assert.Equal(old.Fingerprint, reapproval.Approval!.Fingerprint);
        Assert.NotEqual(old.Generation, reapproval.Approval.Generation);
        Assert.Equal(reapproval.Approval, Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath)));

        // Output associated with the old approval stays stale, also after a restart.
        foreach (PluginTrustService checker in new[] { service, env.Service() })
        {
            Assert.Equal(PluginTrustStatus.StaleApproval, checker.CheckTrust(old.Identity, Token).Status);
            PluginTrustDecision current = checker.CheckTrust(reapproval.Approval.Identity, Token);
            Assert.Equal(PluginTrustStatus.Approved, current.Status);
            Assert.Equal(env.PluginPath("a.ps1"), current.ValidatedFilePath);
        }
    }

    [Fact]
    public void RevocationClearsAnUnsavedInvalidation()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        PluginApproval old = ApproveThenChangeWithReadOnlyStore(env, service);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        SetStoreReadOnly(env, readOnly: false);

        Assert.Equal(PluginRevocationStatus.Revoked, service.Revoke(env.Root, PluginId.Create("a.ps1"), Token).Status);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "a.ps1").Status);
        PluginApproval reapproved = env.Approve(service, "a.ps1");

        Assert.NotEqual(old.Generation, reapproved.Generation);
        Assert.Equal(PluginTrustStatus.Approved, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void InvalidationSavedByAnotherInstanceIsNoLongerPending()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        ApproveThenChangeWithReadOnlyStore(env, service);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        SetStoreReadOnly(env, readOnly: false);

        // Another instance observes the change too and saves the removal; then the content is approved again.
        Assert.True(env.Check(env.Service(), "a.ps1").ApprovalInvalidated);
        PluginApproval reapproved = env.Approve(env.Service(), "a.ps1");

        PluginTrustDecision decision = env.Check(service, "a.ps1");
        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
        Assert.Equal(reapproved.Identity, decision.Approval);
    }

    [Fact]
    public void ConcurrentChecksNeverApproveAnUnsavedInvalidation()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service(new PluginTrustOptions { LockTimeout = TimeSpan.FromMinutes(1) });
        ApproveThenChangeWithReadOnlyStore(env, service);
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        env.Write("a.ps1", "v1");

        PluginTrustDecision[] whileReadOnly = RunConcurrently(8, () => env.Check(service, "a.ps1"));
        Assert.All(whileReadOnly, decision => Assert.Equal(PluginTrustStatus.ContentChanged, decision.Status));

        SetStoreReadOnly(env, readOnly: false);
        PluginTrustDecision[] afterwards = RunConcurrently(8, () => env.Check(service, "a.ps1"));

        Assert.DoesNotContain(afterwards, decision => decision.IsApproved);
        Assert.Single(afterwards, decision => decision.ApprovalInvalidated);
        Assert.Equal(7, afterwards.Count(decision => decision.Status == PluginTrustStatus.PendingApproval));
        Assert.Empty(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
    }

    [Fact]
    public void ReachingTheBoundDeniesEveryPluginInsteadOfForgettingAnInvalidation()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service(hooks: new TrustTestHooks { MaxPendingInvalidations = 1 });
        env.Write("a.ps1", "v1");
        env.Write("b.ps1", "v1");
        env.Write("c.ps1", "unchanged");
        env.Approve(service, "a.ps1");
        env.Approve(service, "b.ps1");
        PluginApproval c = env.Approve(service, "c.ps1");
        env.Write("a.ps1", "v2");
        env.Write("b.ps1", "v2");
        SetStoreReadOnly(env, readOnly: true);
        env.OnDispose(() => SetStoreReadOnly(env, readOnly: false));

        PluginTrustDecision first = env.Check(service, "a.ps1");
        PluginTrustDecision second = env.Check(service, "b.ps1");

        Assert.Equal(PluginTrustStatus.ContentChanged, first.Status);
        Assert.Equal(PluginTrustStatus.ContentChanged, second.Status);
        Assert.Contains("cannot track more", second.Diagnostics[^1].Message, StringComparison.Ordinal);

        // b's invalidation could not be recorded, so the instance fails closed for every plugin.
        env.Write("a.ps1", "v1");
        env.Write("b.ps1", "v1");
        foreach (string id in new[] { "a.ps1", "b.ps1", "c.ps1" })
        {
            Assert.False(env.Check(service, id).IsApproved);
        }

        SetStoreReadOnly(env, readOnly: false);
        PluginTrustDecision saturated = env.Check(service, "c.ps1");
        Assert.Equal(PluginTrustStatus.StoreUnavailable, saturated.Status);
        Assert.Equal(TrustDiagnosticCode.InvalidationNotPersisted, Assert.Single(saturated.Diagnostics).Code);
        Assert.False(service.CheckTrust(c.Identity, Token).IsApproved);

        // Approving cannot keep an existing generation in this state either.
        PluginApprovalResult again = service.Approve(env.Root, PluginId.Create("c.ps1"), c.Fingerprint, Token);
        Assert.Equal(PluginApprovalStatus.Approved, again.Status);
        Assert.NotEqual(c.Generation, again.Approval!.Generation);
    }

    [Fact]
    public void PendingInvalidationsAreBoundedAndThreadSafe()
    {
        var pending = new PendingInvalidations(capacity: 64);
        ApprovalGeneration[] generations = [.. Enumerable.Range(0, 200).Select(_ => ApprovalGeneration.FromGuid(Guid.NewGuid()))];

        bool[] added = new bool[generations.Length];
        Parallel.For(0, generations.Length, i =>
        {
            added[i] = pending.Add(generations[i]);
            _ = pending.Contains(generations[i]);
        });

        Assert.Equal(64, added.Count(a => a));
        Assert.Equal(64, pending.Count);
        Assert.True(pending.IsSaturated);
        Assert.All(generations.Where((_, i) => added[i]), generation => Assert.True(pending.Contains(generation)));

        Parallel.ForEach(generations, pending.Remove);
        Assert.Equal(0, pending.Count);

        // Saturation is permanent for the instance: an invalidation it could not record is never forgotten silently.
        Assert.True(pending.IsSaturated);
        Assert.True(pending.Add(generations[0]));
    }

    [Fact]
    public void GenerationsTheStoreNoLongerHoldsAreForgotten()
    {
        var pending = new PendingInvalidations(capacity: 4);
        PluginApproval kept = Approval("kept.ps1");
        PluginApproval removed = Approval("removed.ps1");
        Assert.True(pending.Add(kept.Generation));
        Assert.True(pending.Add(removed.Generation));

        pending.RetainOnly(new ApprovalSnapshot([kept], exists: true));

        Assert.True(pending.Contains(kept.Generation));
        Assert.False(pending.Contains(removed.Generation));
        Assert.False(pending.IsSaturated);
    }

    [Fact]
    public void RejectedApprovalRemovesAnApprovalOfChangedContent()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval old = env.Approve(service, "a.ps1");
        env.Write("a.ps1", "v2");

        // A stale review of v1 is submitted after the file changed to v2.
        PluginApprovalResult result = service.Approve(env.Root, PluginId.Create("a.ps1"), old.Fingerprint, Token);

        Assert.Equal(PluginApprovalStatus.FingerprintMismatch, result.Status);
        Assert.True(result.ApprovalInvalidated);
        Assert.Null(result.Approval);
        Assert.Equal(
            [TrustDiagnosticCode.FingerprintMismatch, TrustDiagnosticCode.ContentChanged, TrustDiagnosticCode.ApprovalInvalidated],
            result.Diagnostics.Select(d => d.Code));
        Assert.Empty(ApprovalStoreSerializerTests.ReadStore(env.StorePath));

        // Reverting to the previously approved bytes does not bring the approval back.
        env.Write("a.ps1", "v1");
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(service, "a.ps1").Status);
        Assert.Equal(PluginTrustStatus.StaleApproval, service.CheckTrust(old.Identity, Token).Status);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(env.Service(), "a.ps1").Status);
    }

    [Fact]
    public void RejectedApprovalWhoseInvalidationCannotBeSavedIsRemembered()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        PluginApproval old = ApproveThenChangeWithReadOnlyStore(env, service);

        PluginApprovalResult result = service.Approve(env.Root, PluginId.Create("a.ps1"), old.Fingerprint, Token);

        Assert.Equal(PluginApprovalStatus.FingerprintMismatch, result.Status);
        Assert.False(result.ApprovalInvalidated);
        Assert.Equal(
            [TrustDiagnosticCode.FingerprintMismatch, TrustDiagnosticCode.ContentChanged, TrustDiagnosticCode.StoreWriteFailed, TrustDiagnosticCode.InvalidationNotPersisted],
            result.Diagnostics.Select(d => d.Code));
        Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));

        // Reverted bytes are still denied by this instance, and the removal is saved once the store is writable.
        env.Write("a.ps1", "v1");
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        SetStoreReadOnly(env, readOnly: false);
        Assert.True(env.Check(service, "a.ps1").ApprovalInvalidated);
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(env.Service(), "a.ps1").Status);
    }

    [Fact]
    public void FailedReplacementOfAnObsoleteApprovalIsRemembered()
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        PluginApproval old = ApproveThenChangeWithReadOnlyStore(env, service);
        ContentFingerprint v2 = service.Inspect(env.Root, PluginId.Create("a.ps1"), Token).Plugin!.Fingerprint;

        PluginApprovalResult result = service.Approve(env.Root, PluginId.Create("a.ps1"), v2, Token);

        Assert.Equal(PluginApprovalStatus.StoreUnavailable, result.Status);
        Assert.Equal(
            [TrustDiagnosticCode.ContentChanged, TrustDiagnosticCode.StoreWriteFailed, TrustDiagnosticCode.InvalidationNotPersisted],
            result.Diagnostics.Select(d => d.Code));
        env.Write("a.ps1", "v1");
        Assert.False(env.Check(service, "a.ps1").IsApproved);
        Assert.False(service.CheckTrust(old.Identity, Token).IsApproved);
    }

    [Fact]
    public void SuccessfulApprovalReplacesAnObsoleteApprovalAtomically()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval old = env.Approve(service, "a.ps1");
        env.Write("a.ps1", "v2");
        ContentFingerprint v2 = service.Inspect(env.Root, PluginId.Create("a.ps1"), Token).Plugin!.Fingerprint;

        PluginApprovalResult result = service.Approve(env.Root, PluginId.Create("a.ps1"), v2, Token);

        Assert.Equal(PluginApprovalStatus.Approved, result.Status);
        Assert.True(result.ApprovalInvalidated);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(result.Approval, Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath)));
        Assert.Equal(PluginTrustStatus.StaleApproval, service.CheckTrust(old.Identity, Token).Status);

        // Reverting to the old bytes now changes the content of the new approval; the old one does not return.
        env.Write("a.ps1", "v1");
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
        Assert.Equal(PluginTrustStatus.StaleApproval, service.CheckTrust(old.Identity, Token).Status);
    }

    [Fact]
    public void WrongExpectedFingerprintDoesNotInvalidateACurrentApproval()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval approval = env.Approve(service, "a.ps1");
        string store = env.StoreText();

        PluginApprovalResult result = service.Approve(env.Root, PluginId.Create("a.ps1"), ContentFingerprint.Parse(new string('a', 64)), Token);

        Assert.Equal(PluginApprovalStatus.FingerprintMismatch, result.Status);
        Assert.False(result.ApprovalInvalidated);
        Assert.Equal(TrustDiagnosticCode.FingerprintMismatch, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(store, env.StoreText());
        PluginTrustDecision decision = env.Check(service, "a.ps1");
        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
        Assert.Equal(approval.Identity, decision.Approval);
    }

    [Fact]
    public void UnavailablePluginDuringApprovalIsNotAConfirmedChange()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("a.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval approval = env.Approve(service, "a.ps1");
        string store = env.StoreText();

        PluginApprovalResult locked;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            locked = service.Approve(env.Root, PluginId.Create("a.ps1"), ContentFingerprint.Parse(new string('b', 64)), Token);
        }

        Assert.Equal(PluginApprovalStatus.PluginUnavailable, locked.Status);
        Assert.False(locked.ApprovalInvalidated);
        Assert.Equal(store, env.StoreText());
        Assert.Equal(approval.Identity, env.Check(service, "a.ps1").Approval);
    }

    private static PluginApproval Approval(string id) =>
        new(
            new PluginTrustKey(PluginRoot.Create(@"C:\plugins"), PluginId.Create(id)),
            ContentFingerprint.Parse(new string('1', 64)),
            ApprovalGeneration.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UnixEpoch);

    private static T[] RunConcurrently<T>(int count, Func<T> operation)
    {
        using var start = new Barrier(count);
        Task<T>[] tasks = [.. Enumerable.Range(0, count).Select(_ => Task.Run(
            () =>
            {
                start.SignalAndWait(Token);
                return operation();
            },
            Token))];
        Task.WaitAll(tasks, Token);
        return [.. tasks.Select(task => task.Result)];
    }
}
