namespace TrayWisp.Core.Trust;

/// <summary>
/// The minimal identity of one approval: which plugin (root and identity), which content (fingerprint)
/// and which act of approval (generation). Future output and output-driven actions are associated with the
/// approval identity under which the producing run was authorized.
/// </summary>
/// <remarks>
/// <para>An approval identity is not a permission token. Holding one grants nothing: Runtime accepts it only
/// as the <em>expected</em> state when checking trust, and authorizes only if the current, persisted
/// approval and the current file content match it exactly. After a revocation, a reapproval (even of the
/// same bytes) or a content change, an older identity no longer matches.</para>
/// <para>Equality is value equality on all three parts.</para>
/// </remarks>
public sealed record PluginApprovalIdentity
{
    /// <summary>Creates an approval identity.</summary>
    public PluginApprovalIdentity(PluginTrustKey key, ContentFingerprint fingerprint, ApprovalGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(generation);
        Key = key;
        Fingerprint = fingerprint;
        Generation = generation;
    }

    /// <summary>The approved plugin root and identity.</summary>
    public PluginTrustKey Key { get; }

    /// <summary>The approved content.</summary>
    public ContentFingerprint Fingerprint { get; }

    /// <summary>The act of approval.</summary>
    public ApprovalGeneration Generation { get; }
}

/// <summary>
/// A persisted approval record: the approval identity plus an informational UTC timestamp.
/// </summary>
/// <remarks>
/// There is at most one current approval per <see cref="PluginTrustKey"/>. <see cref="ApprovedAtUtc"/> is
/// for display only. Time never establishes or extends trust.
/// </remarks>
public sealed record PluginApproval
{
    /// <summary>Creates an approval record.</summary>
    /// <exception cref="ArgumentException"><paramref name="approvedAtUtc"/> is not in UTC (offset zero).</exception>
    public PluginApproval(PluginTrustKey key, ContentFingerprint fingerprint, ApprovalGeneration generation, DateTimeOffset approvedAtUtc)
    {
        if (approvedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The approval time must be in UTC.", nameof(approvedAtUtc));
        }

        Identity = new PluginApprovalIdentity(key, fingerprint, generation);
        ApprovedAtUtc = approvedAtUtc;
    }

    /// <summary>The approval identity (key, fingerprint and generation).</summary>
    public PluginApprovalIdentity Identity { get; }

    /// <summary>The approved plugin root and identity.</summary>
    public PluginTrustKey Key => Identity.Key;

    /// <summary>The approved content.</summary>
    public ContentFingerprint Fingerprint => Identity.Fingerprint;

    /// <summary>The act of approval.</summary>
    public ApprovalGeneration Generation => Identity.Generation;

    /// <summary>When the approval was granted, in UTC. Informational only.</summary>
    public DateTimeOffset ApprovedAtUtc { get; }
}
