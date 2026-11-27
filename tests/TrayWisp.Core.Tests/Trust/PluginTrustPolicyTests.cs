using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using Xunit;

namespace TrayWisp.Core.Tests.Trust;

public sealed class PluginTrustPolicyTests
{
    private static readonly PluginTrustKey Key = new(PluginRoot.Create(@"C:\plugins"), PluginId.Create("clock.ps1"));
    private static readonly PluginTrustKey OtherKey = new(PluginRoot.Create(@"C:\other"), PluginId.Create("clock.ps1"));
    private static readonly ContentFingerprint V1 = ContentFingerprint.Parse(new string('1', 64));
    private static readonly ContentFingerprint V2 = ContentFingerprint.Parse(new string('2', 64));
    private static readonly ApprovalGeneration G1 = ApprovalGeneration.Parse(new string('a', 32));
    private static readonly ApprovalGeneration G2 = ApprovalGeneration.Parse(new string('b', 32));

    private static PluginApproval Approval(ContentFingerprint fingerprint, ApprovalGeneration generation, PluginTrustKey? key = null) =>
        new(key ?? Key, fingerprint, generation, DateTimeOffset.UnixEpoch);

    private static PluginFingerprint Observed(ContentFingerprint fingerprint) => new(Key, fingerprint, 10);

    [Fact]
    public void NoApprovalIsPending() =>
        Assert.Equal(new PluginTrustEvaluation(PluginTrustStatus.PendingApproval, false), PluginTrustPolicy.Evaluate(null, Observed(V1)));

    [Fact]
    public void MatchingApprovalIsApproved() =>
        Assert.Equal(new PluginTrustEvaluation(PluginTrustStatus.Approved, false), PluginTrustPolicy.Evaluate(Approval(V1, G1), Observed(V1)));

    [Fact]
    public void ObservedDifferentContentInvalidates() =>
        Assert.Equal(new PluginTrustEvaluation(PluginTrustStatus.ContentChanged, true), PluginTrustPolicy.Evaluate(Approval(V1, G1), Observed(V2)));

    [Fact]
    public void ExpectedIdentityMustMatchTheCurrentApprovalExactly()
    {
        PluginApproval current = Approval(V1, G1);

        Assert.Equal(PluginTrustStatus.Approved, PluginTrustPolicy.Evaluate(current, Observed(V1), current.Identity).Status);

        // Reapproval of the same bytes: same fingerprint, new generation.
        Assert.Equal(PluginTrustStatus.StaleApproval, PluginTrustPolicy.Evaluate(current, Observed(V1), new PluginApprovalIdentity(Key, V1, G2)).Status);

        // Revoked: no current approval.
        Assert.Equal(PluginTrustStatus.StaleApproval, PluginTrustPolicy.Evaluate(null, Observed(V1), current.Identity).Status);

        // Approved for other content than expected (expected identity from before a change and reapproval).
        Assert.Equal(PluginTrustStatus.StaleApproval, PluginTrustPolicy.Evaluate(Approval(V2, G2), Observed(V2), current.Identity).Status);
    }

    [Fact]
    public void ContentChangeTakesPrecedenceOverExpectedIdentity()
    {
        PluginApproval current = Approval(V1, G1);

        Assert.Equal(new PluginTrustEvaluation(PluginTrustStatus.ContentChanged, true), PluginTrustPolicy.Evaluate(current, Observed(V2), current.Identity));
        Assert.Equal(new PluginTrustEvaluation(PluginTrustStatus.ContentChanged, true), PluginTrustPolicy.Evaluate(current, Observed(V2), new PluginApprovalIdentity(Key, V2, G2)));
    }

    [Fact]
    public void DefaultEvaluationDoesNotApprove() => Assert.NotEqual(PluginTrustStatus.Approved, default(PluginTrustEvaluation).Status);

    [Fact]
    public void ApprovalOfTheObservedContentIsCreated() =>
        Assert.Equal(PluginApprovalAction.CreateApproval, PluginTrustPolicy.EvaluateApproval(null, Observed(V1), V1));

    [Fact]
    public void ApprovalOfOtherContentThanObservedIsRejected()
    {
        Assert.Equal(PluginApprovalAction.RejectFingerprintMismatch, PluginTrustPolicy.EvaluateApproval(null, Observed(V2), V1));

        // Even when the expected fingerprint is the currently approved one.
        Assert.Equal(PluginApprovalAction.RejectFingerprintMismatch, PluginTrustPolicy.EvaluateApproval(Approval(V1, G1), Observed(V2), V1));
    }

    [Fact]
    public void RepeatedApprovalOfUnchangedContentKeepsTheApproval() =>
        Assert.Equal(PluginApprovalAction.KeepExistingApproval, PluginTrustPolicy.EvaluateApproval(Approval(V1, G1), Observed(V1), V1));

    [Fact]
    public void ApprovalOfNewContentReplacesTheOldApproval() =>
        Assert.Equal(PluginApprovalAction.CreateApproval, PluginTrustPolicy.EvaluateApproval(Approval(V1, G1), Observed(V2), V2));

    [Fact]
    public void DefaultApprovalActionRejects() => Assert.Equal(PluginApprovalAction.RejectFingerprintMismatch, default(PluginApprovalAction));

    [Fact]
    public void ApprovalsOfOtherPluginsAreCallerErrors()
    {
        Assert.Throws<ArgumentException>(() => PluginTrustPolicy.Evaluate(Approval(V1, G1, OtherKey), Observed(V1)));
        Assert.Throws<ArgumentException>(() => PluginTrustPolicy.Evaluate(null, Observed(V1), new PluginApprovalIdentity(OtherKey, V1, G1)));
        Assert.Throws<ArgumentException>(() => PluginTrustPolicy.EvaluateApproval(Approval(V1, G1, OtherKey), Observed(V1), V1));
    }
}
