using System.Collections.Immutable;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Trust;

/// <summary>
/// The result of <see cref="PluginTrustService.CheckTrust(string, TrayWisp.Core.Plugins.PluginId, CancellationToken)"/>:
/// a point-in-time observation of whether a plugin may run. It is not a permission token and must not be
/// cached; every execution path must ask again.
/// </summary>
public sealed class PluginTrustDecision
{
    internal PluginTrustDecision(
        PluginTrustStatus status,
        PluginTrustKey? key,
        PluginFingerprint? current,
        PluginApprovalIdentity? approval,
        string? validatedFilePath,
        bool approvalInvalidated,
        ImmutableArray<TrustDiagnostic> diagnostics)
    {
        // Only an approval with its validated path is launchable; anything else denies.
        bool approved = status == PluginTrustStatus.Approved && approval is not null && validatedFilePath is not null;
        Status = approved || status != PluginTrustStatus.Approved ? status : PluginTrustStatus.StoreUnavailable;
        Key = key;
        Current = current;
        Approval = approved ? approval : null;
        ValidatedFilePath = approved ? validatedFilePath : null;
        ApprovalInvalidated = approvalInvalidated;
        Diagnostics = diagnostics;
    }

    /// <summary>The outcome. Only <see cref="PluginTrustStatus.Approved"/> permits execution.</summary>
    public PluginTrustStatus Status { get; }

    /// <summary><see langword="true"/> exactly when <see cref="Status"/> is <see cref="PluginTrustStatus.Approved"/>.</summary>
    public bool IsApproved => Status == PluginTrustStatus.Approved;

    /// <summary>The validated root and identity, or <see langword="null"/> when the root or identity was invalid.</summary>
    public PluginTrustKey? Key { get; }

    /// <summary>The fingerprint observed by this check, when the file could be inspected.</summary>
    public PluginFingerprint? Current { get; }

    /// <summary>
    /// The current approval identity, only when approved. Output produced by a run authorized by this decision
    /// should be associated with it, and later checked with
    /// <see cref="PluginTrustService.CheckTrust(PluginApprovalIdentity, CancellationToken)"/>.
    /// </summary>
    public PluginApprovalIdentity? Approval { get; }

    /// <summary>
    /// The exact file this check validated and fingerprinted, only when approved: a fully qualified local
    /// path, read from the open file handle after hashing, with the on-disk spelling (including case) of every
    /// directory inside the root and of the file name. In a case-sensitive directory it can differ from the
    /// spelling of <see cref="Key"/>, and only this path names the file that was approved.
    /// </summary>
    /// <remarks>
    /// A future execution path must launch exactly this path, taken from the authoritative check it makes
    /// immediately before running the plugin. It must never rebuild a path from the case-insensitive
    /// <see cref="Key"/> or <see cref="Approval"/>, or from an earlier discovery result. The file is no longer
    /// held open once this result is returned, so this does not close the window between this check and the
    /// moment an interpreter opens the file (see docs/trust-model.md).
    /// </remarks>
    public string? ValidatedFilePath { get; }

    /// <summary>
    /// <see langword="true"/> when this check removed an invalidated approval from the trust store and the
    /// removal was saved: because it observed changed content, or because an earlier check of this trust
    /// service had observed changed content but could not save the removal then. With
    /// <see cref="PluginTrustStatus.ContentChanged"/> and <see langword="false"/>, saving failed (see
    /// <see cref="Diagnostics"/>). The plugin is denied either way, and this trust service instance keeps
    /// denying that approval and retries the removal on its next operation for the plugin. Other instances,
    /// other processes and later sessions do not know about an unsaved removal (see docs/trust-model.md).
    /// </summary>
    public bool ApprovalInvalidated { get; }

    /// <summary>Why the plugin is not approved, and any storage problems. Empty when approved.</summary>
    public ImmutableArray<TrustDiagnostic> Diagnostics { get; }
}

/// <summary>Outcome of <see cref="PluginTrustService.Inspect"/>.</summary>
public enum PluginInspectionStatus
{
    /// <summary>The plugin was validated and fingerprinted.</summary>
    Inspected,

    /// <summary>The plugin could not be inspected right now (missing, locked, unreadable, too large, changing).</summary>
    PluginUnavailable,

    /// <summary>The root or plugin path is invalid, unsupported or unsafe.</summary>
    UnsafePath,
}

/// <summary>The result of <see cref="PluginTrustService.Inspect"/>.</summary>
public sealed class PluginInspectionResult
{
    internal PluginInspectionResult(PluginInspectionStatus status, PluginTrustKey? key, PluginFingerprint? plugin, string? validatedFilePath, ImmutableArray<TrustDiagnostic> diagnostics)
    {
        bool inspected = status == PluginInspectionStatus.Inspected;
        Status = status;
        Key = key;
        Plugin = inspected ? plugin : null;
        ValidatedFilePath = inspected ? validatedFilePath : null;
        Diagnostics = diagnostics;
    }

    /// <summary>The outcome.</summary>
    public PluginInspectionStatus Status { get; }

    /// <summary>The validated root and identity, or <see langword="null"/> when they were invalid.</summary>
    public PluginTrustKey? Key { get; }

    /// <summary>The validated key and current fingerprint, when <see cref="Status"/> is <see cref="PluginInspectionStatus.Inspected"/>.</summary>
    public PluginFingerprint? Plugin { get; }

    /// <summary>
    /// The exact file that was fingerprinted, when <see cref="Status"/> is <see cref="PluginInspectionStatus.Inspected"/>:
    /// a fully qualified local path read from the open file handle, with the on-disk spelling of every
    /// directory and of the file name. For display only. An inspection authorizes nothing, so this path must
    /// never be launched; use <see cref="PluginTrustDecision.ValidatedFilePath"/> of an approved trust check.
    /// </summary>
    public string? ValidatedFilePath { get; }

    /// <summary>Why the inspection failed. Empty on success.</summary>
    public ImmutableArray<TrustDiagnostic> Diagnostics { get; }
}

/// <summary>Outcome of <see cref="PluginTrustService.Approve"/>.</summary>
public enum PluginApprovalStatus
{
    /// <summary>A new approval with a new generation was saved.</summary>
    Approved,

    /// <summary>The plugin was already approved for exactly this content. The existing approval and its generation were kept.</summary>
    AlreadyApproved,

    /// <summary>
    /// The plugin's current content is not the content the caller asked to approve. Nothing was approved. If the
    /// plugin had an approval of content other than its current content, that obsolete approval was invalidated
    /// (see <see cref="PluginApprovalResult.ApprovalInvalidated"/>); an approval of the current content is kept.
    /// </summary>
    FingerprintMismatch,

    /// <summary>The plugin could not be inspected right now. Nothing was changed.</summary>
    PluginUnavailable,

    /// <summary>The root or plugin path is invalid, unsupported or unsafe. Nothing was changed.</summary>
    UnsafePath,

    /// <summary>
    /// The trust store could not be read, locked or updated, or is invalid. Nothing was approved or changed in
    /// the store. An obsolete approval this operation could not remove stays denied by this trust service
    /// instance (see <see cref="Diagnostics"/>).
    /// </summary>
    StoreUnavailable,
}

/// <summary>The result of <see cref="PluginTrustService.Approve"/>.</summary>
public sealed class PluginApprovalResult
{
    internal PluginApprovalResult(
        PluginApprovalStatus status,
        PluginTrustKey? key,
        PluginFingerprint? current,
        PluginApproval? approval,
        bool approvalInvalidated,
        ImmutableArray<TrustDiagnostic> diagnostics)
    {
        Status = status;
        Key = key;
        Current = current;
        Approval = Succeeded ? approval : null;
        ApprovalInvalidated = approvalInvalidated;
        Diagnostics = diagnostics;
    }

    /// <summary>The outcome.</summary>
    public PluginApprovalStatus Status { get; }

    /// <summary><see langword="true"/> for <see cref="PluginApprovalStatus.Approved"/> and <see cref="PluginApprovalStatus.AlreadyApproved"/>.</summary>
    public bool Succeeded => Status is PluginApprovalStatus.Approved or PluginApprovalStatus.AlreadyApproved;

    /// <summary>The validated root and identity, or <see langword="null"/> when they were invalid.</summary>
    public PluginTrustKey? Key { get; }

    /// <summary>The fingerprint observed immediately before the approval decision, when the file could be inspected.</summary>
    public PluginFingerprint? Current { get; }

    /// <summary>The saved (or kept) approval, when <see cref="Succeeded"/>.</summary>
    public PluginApproval? Approval { get; }

    /// <summary>
    /// <see langword="true"/> when this operation found that the plugin's previous approval was obsolete (it
    /// approved other content than the current content, or this trust service had already invalidated it
    /// without being able to save that) and its removal or replacement was saved. With
    /// <see cref="PluginApprovalStatus.Approved"/>, the new approval replaced it.
    /// </summary>
    public bool ApprovalInvalidated { get; }

    /// <summary>Why the approval failed. Empty on success.</summary>
    public ImmutableArray<TrustDiagnostic> Diagnostics { get; }
}

/// <summary>Outcome of <see cref="PluginTrustService.Revoke"/>.</summary>
public enum PluginRevocationStatus
{
    /// <summary>The approval was removed and the change saved.</summary>
    Revoked,

    /// <summary>There was no approval to remove. Nothing was changed.</summary>
    NotApproved,

    /// <summary>The root or identity is invalid. Nothing was changed.</summary>
    InvalidPath,

    /// <summary>The trust store could not be read, locked or updated, or is invalid. Nothing was changed.</summary>
    StoreUnavailable,
}

/// <summary>The result of <see cref="PluginTrustService.Revoke"/>.</summary>
public sealed class PluginRevocationResult
{
    internal PluginRevocationResult(PluginRevocationStatus status, PluginTrustKey? key, PluginApproval? revoked, ImmutableArray<TrustDiagnostic> diagnostics)
    {
        Status = status;
        Key = key;
        RevokedApproval = revoked;
        Diagnostics = diagnostics;
    }

    /// <summary>The outcome.</summary>
    public PluginRevocationStatus Status { get; }

    /// <summary><see langword="true"/> for <see cref="PluginRevocationStatus.Revoked"/> and <see cref="PluginRevocationStatus.NotApproved"/>: no approval remains.</summary>
    public bool Succeeded => Status is PluginRevocationStatus.Revoked or PluginRevocationStatus.NotApproved;

    /// <summary>The validated root and identity, or <see langword="null"/> when they were invalid.</summary>
    public PluginTrustKey? Key { get; }

    /// <summary>The approval that was removed, when <see cref="Status"/> is <see cref="PluginRevocationStatus.Revoked"/>.</summary>
    public PluginApproval? RevokedApproval { get; }

    /// <summary>Why the revocation failed. Empty on success.</summary>
    public ImmutableArray<TrustDiagnostic> Diagnostics { get; }
}
