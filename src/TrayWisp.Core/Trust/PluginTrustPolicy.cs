namespace TrayWisp.Core.Trust;

/// <summary>The outcome of a trust check.</summary>
/// <remarks>Only <see cref="Approved"/> makes a plugin eligible for execution. Every other value denies.</remarks>
public enum PluginTrustStatus
{
    /// <summary>The plugin has no current approval. This includes plugins that were never approved, revoked ones and ones whose approval was invalidated earlier.</summary>
    PendingApproval,

    /// <summary>The plugin has a current approval and its current content matches the approved content exactly.</summary>
    Approved,

    /// <summary>
    /// The plugin was approved, but a complete inspection found different content. The approval is invalidated.
    /// Also reported when the same trust service observed such a change earlier but could not save the
    /// invalidation yet: that approval stays denied even if the earlier content returns.
    /// </summary>
    ContentChanged,

    /// <summary>The caller's expected approval identity is not the current approval (revoked, reapproved or invalidated since).</summary>
    StaleApproval,

    /// <summary>The plugin could not be inspected right now (missing, locked, unreadable, too large, changing). Not a confirmed content change.</summary>
    PluginUnavailable,

    /// <summary>The plugin root or plugin path is invalid, unsupported or unsafe (links, aliases, collisions, hidden or remote entries).</summary>
    UnsafePath,

    /// <summary>The trust store could not be read, locked or updated, or is invalid. Nothing is approved while it is unavailable.</summary>
    StoreUnavailable,
}

/// <summary>What an approval request leads to, as decided by <see cref="PluginTrustPolicy.EvaluateApproval"/>.</summary>
public enum PluginApprovalAction
{
    /// <summary>The current content is not the content the caller asked to approve. Nothing is approved.</summary>
    RejectFingerprintMismatch,

    /// <summary>The plugin is already approved for exactly this content. The existing approval and generation are kept.</summary>
    KeepExistingApproval,

    /// <summary>A new approval with a new generation is created, replacing any approval of other content.</summary>
    CreateApproval,
}

/// <summary>The outcome of <see cref="PluginTrustPolicy.Evaluate"/>.</summary>
/// <param name="Status">The trust status.</param>
/// <param name="InvalidatesApproval"><see langword="true"/> when the current approval must be removed because a complete inspection observed different content.</param>
public readonly record struct PluginTrustEvaluation(PluginTrustStatus Status, bool InvalidatesApproval);

/// <summary>
/// Pure trust decisions. They take the current persisted approval and a fresh, complete inspection of the
/// plugin file, both of which Runtime obtains under the trust store lock. They never authorize from cached
/// data, metadata, discovery results or time.
/// </summary>
public static class PluginTrustPolicy
{
    /// <summary>
    /// Decides whether a plugin is approved, given its current approval (or <see langword="null"/>) and a
    /// fresh inspection of its content, optionally requiring that the approval is still exactly
    /// <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>An approval whose fingerprint differs from the observed content yields
    /// <see cref="PluginTrustStatus.ContentChanged"/> and invalidates the approval, whatever was expected.</item>
    /// <item>Without an approval: <see cref="PluginTrustStatus.PendingApproval"/>, or
    /// <see cref="PluginTrustStatus.StaleApproval"/> when an approval identity was expected.</item>
    /// <item>With a matching approval: <see cref="PluginTrustStatus.Approved"/>, unless an expected identity
    /// differs from it in fingerprint or generation, which yields <see cref="PluginTrustStatus.StaleApproval"/>.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentException">The approval or expected identity belongs to a different key than <paramref name="observed"/>.</exception>
    public static PluginTrustEvaluation Evaluate(PluginApproval? approval, PluginFingerprint observed, PluginApprovalIdentity? expected = null)
    {
        ArgumentNullException.ThrowIfNull(observed);
        RequireSameKey(approval?.Key, observed.Key, nameof(approval));
        RequireSameKey(expected?.Key, observed.Key, nameof(expected));

        if (approval is null)
        {
            return new(expected is null ? PluginTrustStatus.PendingApproval : PluginTrustStatus.StaleApproval, InvalidatesApproval: false);
        }

        if (approval.Fingerprint != observed.Fingerprint)
        {
            return new(PluginTrustStatus.ContentChanged, InvalidatesApproval: true);
        }

        if (expected is not null && (expected.Fingerprint != approval.Fingerprint || expected.Generation != approval.Generation))
        {
            return new(PluginTrustStatus.StaleApproval, InvalidatesApproval: false);
        }

        return new(PluginTrustStatus.Approved, InvalidatesApproval: false);
    }

    /// <summary>
    /// Decides what an explicit approval of <paramref name="expected"/> does, given the current approval (or
    /// <see langword="null"/>) and a fresh inspection taken immediately before the approval is persisted.
    /// </summary>
    /// <remarks>
    /// This only decides the new approval. Whether the current approval is obsolete is decided by
    /// <see cref="Evaluate"/>: an approval of content other than the observed content is invalidated whether
    /// or not the request is rejected. Runtime then passes <see langword="null"/> as the current approval, so
    /// an obsolete approval is never kept.
    /// </remarks>
    /// <exception cref="ArgumentException">The approval belongs to a different key than <paramref name="observed"/>.</exception>
    public static PluginApprovalAction EvaluateApproval(PluginApproval? approval, PluginFingerprint observed, ContentFingerprint expected)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(expected);
        RequireSameKey(approval?.Key, observed.Key, nameof(approval));

        if (observed.Fingerprint != expected)
        {
            return PluginApprovalAction.RejectFingerprintMismatch;
        }

        return approval is not null && approval.Fingerprint == observed.Fingerprint
            ? PluginApprovalAction.KeepExistingApproval
            : PluginApprovalAction.CreateApproval;
    }

    private static void RequireSameKey(PluginTrustKey? key, PluginTrustKey observed, string parameterName)
    {
        if (key is not null && key != observed)
        {
            throw new ArgumentException("The approval belongs to a different plugin.", parameterName);
        }
    }
}
