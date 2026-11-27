using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Trust;

/// <summary>
/// Inspects, approves and revokes plugins, and performs the authoritative trust check that every execution
/// path must call immediately before running a plugin or acting on its output. See docs/trust-model.md.
/// </summary>
/// <remarks>
/// <para>Every operation that reads or changes approvals holds the trust store lock for its whole duration,
/// reads the current store from disk (never a cached copy) and, where the plugin file matters, validates
/// the root and path and hashes the complete current file under that lock.</para>
/// <para>Results are point-in-time observations. Discovery results, metadata, file existence, timestamps and
/// earlier results never authorize anything.</para>
/// <para>Trust operations are supported on Windows only. Elsewhere every operation fails with
/// <see cref="TrustDiagnosticCode.UnsupportedPlatform"/>.</para>
/// <para>Instances may be used from several threads, and several instances, in one or several processes, may
/// share a store. The only state an instance keeps is the set of its unsaved invalidations: approvals it found
/// invalidated by a content change but could not remove from the store. It keeps denying those and retries
/// removing them. That state can only deny, and other instances, other processes and later sessions do not
/// know about it, so a host must use one long-lived instance per store for all of its authoritative checks
/// rather than a new instance for every check (see docs/trust-model.md, "Unsaved invalidations").</para>
/// </remarks>
public sealed class PluginTrustService
{
    /// <summary>Largest plugin file that is fingerprinted: 16 MiB. Larger files are rejected, never partially hashed.</summary>
    public const long MaxPluginFileBytes = 16L * 1024 * 1024;

    /// <summary>Largest trust store file that is read or written: 4 MiB.</summary>
    public const int MaxStoreBytes = 4 * 1024 * 1024;

    /// <summary>Most approval records a store may hold.</summary>
    public const int MaxApprovals = 4096;

    /// <summary>Most diagnostics one result holds, including a final <see cref="TrustDiagnosticCode.DiagnosticLimitReached"/>.</summary>
    public const int MaxDiagnostics = 64;

    private readonly PluginRoot _storeDirectory;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly TrustTestHooks? _hooks;
    private readonly PendingInvalidations _pending;

    /// <summary>Creates a service that keeps approvals in the store file at <paramref name="storePath"/>.</summary>
    /// <param name="storePath">
    /// A fully qualified path on a local drive whose file name ends in <c>.json</c>, under the same syntax rules
    /// as <see cref="PluginRoot"/>. The directory is created when first needed. It must not overlap any plugin
    /// root: operations on overlapping roots fail.
    /// </param>
    /// <param name="options">Settings; <see cref="PluginTrustOptions.Default"/> when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="storePath"/> is not an acceptable store path.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public PluginTrustService(string storePath, PluginTrustOptions? options = null)
        : this(storePath, options, TimeProvider.System, hooks: null)
    {
    }

    internal PluginTrustService(string storePath, PluginTrustOptions? options, TimeProvider timeProvider, TrustTestHooks? hooks)
    {
        ArgumentException.ThrowIfNullOrEmpty(storePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= PluginTrustOptions.Default;
        if (options.LockTimeout < TimeSpan.Zero || options.LockTimeout > PluginTrustOptions.MaxLockTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The lock timeout must be between zero and PluginTrustOptions.MaxLockTimeout.");
        }

        int separator = storePath.LastIndexOfAny(['\\', '/']);
        string fileName = separator < 0 ? string.Empty : storePath[(separator + 1)..];
        string directory = separator == 2 ? storePath[..3] : (separator < 0 ? string.Empty : storePath[..separator]);
        bool doubledSeparator = separator > 2 && storePath[separator - 1] is '\\' or '/';
        if (doubledSeparator
            || !PluginRoot.TryCreate(directory, out PluginRoot? root)
            || !PluginId.IsValidSegment(fileName)
            || fileName.StartsWith('.')
            || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The trust store path must be a fully qualified path on a local drive, with normalized segments and a file name ending in .json.",
                nameof(storePath));
        }

        string normalized = root.IsDriveRoot ? root.Value + fileName : root.Value + "\\" + fileName;
        if (OperatingSystem.IsWindows() && !TrustPaths.IsUnchangedByFullPath(normalized))
        {
            throw new ArgumentException("The trust store path is an alias, for example through a short (8.3) name. Use the full path.", nameof(storePath));
        }

        StorePath = normalized;
        _storeDirectory = root;
        _lockTimeout = options.LockTimeout;
        _timeProvider = timeProvider;
        _hooks = hooks;
        _pending = new PendingInvalidations(hooks?.MaxPendingInvalidations ?? MaxApprovals);
    }

    /// <summary>The normalized store file path. The lock file is this path plus <c>.lock</c>.</summary>
    public string StorePath { get; }

    /// <summary>
    /// The per-user default store location, <c>%LOCALAPPDATA%\TrayWisp\trust\approvals.json</c>. There is no
    /// fallback: if the folder is unavailable, no default exists.
    /// </summary>
    /// <exception cref="InvalidOperationException">The local application data folder is not available.</exception>
    public static string GetDefaultStorePath()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(local) || !Path.IsPathFullyQualified(local))
        {
            throw new InvalidOperationException("The per-user local application data folder is not available, so there is no default trust store location.");
        }

        return Path.Combine(local, "TrayWisp", "trust", "approvals.json");
    }

    /// <summary>Creates a service that uses <see cref="GetDefaultStorePath"/>.</summary>
    /// <exception cref="InvalidOperationException">The local application data folder is not available.</exception>
    public static PluginTrustService CreateForCurrentUser(PluginTrustOptions? options = null) => new(GetDefaultStorePath(), options);

    /// <summary>
    /// Validates a plugin's root and path and fingerprints its current content, for presenting it to the user
    /// for review. Does not read or change approvals and authorizes nothing.
    /// </summary>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public PluginInspectionResult Inspect(string rootPath, PluginId pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(pluginId);
        cancellationToken.ThrowIfCancellationRequested();

        TrustFailure? failure = TryCreateKey(rootPath, pluginId, out PluginTrustKey? key);
        PluginFingerprint? plugin = null;
        string? filePath = null;
        if (failure is null && OperatingSystem.IsWindows())
        {
            failure = new PluginFileInspector(_hooks).Inspect(key!, cancellationToken, out InspectedPlugin? inspected);
            using (inspected)
            {
                plugin = inspected?.Plugin;
                filePath = inspected?.FilePath;
            }
        }

        if (failure is null)
        {
            return new PluginInspectionResult(PluginInspectionStatus.Inspected, key, plugin, filePath, []);
        }

        PluginInspectionStatus status = failure.Kind == TrustFailureKind.PluginUnavailable ? PluginInspectionStatus.PluginUnavailable : PluginInspectionStatus.UnsafePath;
        return new PluginInspectionResult(status, key, plugin: null, validatedFilePath: null, [failure.Diagnostic]);
    }

    /// <summary>
    /// Explicitly approves a plugin's content. Immediately before saving, under the store lock, the root and
    /// path are validated again and the current file is hashed again; the approval is saved only if that hash
    /// equals <paramref name="expectedFingerprint"/>, the fingerprint the user reviewed. A plugin already
    /// approved for exactly this content keeps its approval and generation. Otherwise a new approval with a
    /// new generation replaces any previous one.
    /// </summary>
    /// <remarks>
    /// An existing approval of content other than the current content is obsolete. It is never kept: a
    /// successful approval replaces it, and a rejected one (<see cref="PluginApprovalStatus.FingerprintMismatch"/>)
    /// removes it. An approval of the current content is not affected by a request with a wrong expected
    /// fingerprint. An approval this instance invalidated earlier without being able to save that is treated as
    /// obsolete too, so it is never kept with its old generation. If the plugin cannot be inspected, nothing is
    /// decided about its content.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// The operation was cancelled before the approval was saved. Cancellation is not observed once saving has
    /// started, so a cancelled operation never leaves an approval behind that it did not report.
    /// </exception>
    public PluginApprovalResult Approve(string rootPath, PluginId pluginId, ContentFingerprint expectedFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        cancellationToken.ThrowIfCancellationRequested();

        TrustFailure? failure = TryCreateKey(rootPath, pluginId, out PluginTrustKey? key);
        if (failure is not null || !OperatingSystem.IsWindows())
        {
            return ApprovalFailed(failure!, key, current: null);
        }

        return ApproveCore(key!, expectedFingerprint, cancellationToken);
    }

    /// <summary>
    /// Removes a plugin's approval, if any. Only the root and identity syntax are validated, so approvals of
    /// plugins or roots that no longer exist can be revoked. A later approval, even of the same content, gets a
    /// new generation.
    /// </summary>
    /// <exception cref="OperationCanceledException">The operation was cancelled before the revocation was saved.</exception>
    public PluginRevocationResult Revoke(string rootPath, PluginId pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(pluginId);
        cancellationToken.ThrowIfCancellationRequested();

        TrustFailure? failure = TryCreateKey(rootPath, pluginId, out PluginTrustKey? key, checkStoreOverlap: false);
        if (failure is not null || !OperatingSystem.IsWindows())
        {
            PluginRevocationStatus status = failure!.Kind == TrustFailureKind.UnsafePath ? PluginRevocationStatus.InvalidPath : PluginRevocationStatus.StoreUnavailable;
            return new PluginRevocationResult(status, key, revoked: null, [failure.Diagnostic]);
        }

        return RevokeCore(key!, cancellationToken);
    }

    /// <summary>
    /// The authoritative trust check. Returns <see cref="PluginTrustStatus.Approved"/> only if the store holds a
    /// current approval for the root and identity and a complete inspection of the current file, made now under
    /// the store lock, yields exactly the approved fingerprint. If the inspection shows different content, the
    /// approval is removed (and stays removed if the old content returns). If that removal cannot be saved,
    /// this instance denies that approval from then on and retries the removal on its next operation for the
    /// plugin. If the plugin cannot be inspected (missing, locked, unreadable, unsafe path), the plugin is
    /// denied but its approval is kept. When approved, <see cref="PluginTrustDecision.ValidatedFilePath"/> is
    /// the exact file that was checked, and the only path an execution path may launch.
    /// </summary>
    /// <exception cref="OperationCanceledException">The check was cancelled. A cancelled check never reports approval.</exception>
    public PluginTrustDecision CheckTrust(string rootPath, PluginId pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(pluginId);
        cancellationToken.ThrowIfCancellationRequested();

        TrustFailure? failure = TryCreateKey(rootPath, pluginId, out PluginTrustKey? key);
        if (failure is not null || !OperatingSystem.IsWindows())
        {
            return DecisionFailed(failure!, key);
        }

        return CheckCore(key!, expected: null, cancellationToken);
    }

    /// <summary>
    /// The trust check for something associated with an earlier approval, such as an output-driven action:
    /// performs <see cref="CheckTrust(string, PluginId, CancellationToken)"/> for the identity's root and plugin,
    /// and additionally requires the current approval to be exactly <paramref name="expectedApproval"/> (same
    /// fingerprint and generation). After a revocation, a reapproval (even of the same bytes) or a content change,
    /// the result is <see cref="PluginTrustStatus.StaleApproval"/> (or <see cref="PluginTrustStatus.ContentChanged"/>).
    /// </summary>
    /// <exception cref="OperationCanceledException">The check was cancelled. A cancelled check never reports approval.</exception>
    public PluginTrustDecision CheckTrust(PluginApprovalIdentity expectedApproval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedApproval);
        cancellationToken.ThrowIfCancellationRequested();

        TrustFailure? failure = TryCreateKey(expectedApproval.Key.Root.Value, expectedApproval.Key.Id, out PluginTrustKey? key);
        if (failure is not null || !OperatingSystem.IsWindows())
        {
            return DecisionFailed(failure!, key);
        }

        return CheckCore(key!, expectedApproval, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private PluginTrustDecision CheckCore(PluginTrustKey key, PluginApprovalIdentity? expected, CancellationToken cancellationToken)
    {
        var store = new ApprovalStore(StorePath, _storeDirectory, _hooks);
        TrustFailure? failure = store.AcquireLock(_lockTimeout, cancellationToken, out StoreLock? storeLock);
        if (failure is not null)
        {
            return DecisionFailed(failure, key);
        }

        using (storeLock)
        {
            failure = store.Read(out ApprovalSnapshot? snapshot);
            if (failure is not null)
            {
                return DecisionFailed(failure, key);
            }

            _pending.RetainOnly(snapshot!);
            var diagnostics = new TrustDiagnosticBag();
            string what = Describe(key);
            PluginApproval? approval = snapshot!.Find(key);
            if (approval is not null && _pending.Contains(approval.Generation))
            {
                // This instance already observed a content change for this approval but could not save its
                // removal. The approval is denied whatever the file holds now, and the removal is retried.
                diagnostics.Add(UnsavedInvalidation(what));
                bool removed = SaveInvalidation(store, storeLock!, snapshot, approval, what, diagnostics);
                return new PluginTrustDecision(PluginTrustStatus.ContentChanged, key, current: null, approval: null, validatedFilePath: null, removed, diagnostics.ToImmutable());
            }

            if (_pending.IsSaturated)
            {
                return DecisionFailed(Saturated(), key);
            }

            failure = new PluginFileInspector(_hooks).Inspect(key, cancellationToken, out InspectedPlugin? inspected);
            if (failure is not null)
            {
                // Not a confirmed content change: the approval, if any, is kept.
                return DecisionFailed(failure, key);
            }

            // The decision, and an invalidation, are made while the inspected file is still held open.
            using InspectedPlugin held = inspected!;
            PluginFingerprint current = held.Plugin;
            PluginTrustEvaluation evaluation = PluginTrustPolicy.Evaluate(approval, current, expected);
            switch (evaluation.Status)
            {
                case PluginTrustStatus.Approved:
                    return new PluginTrustDecision(PluginTrustStatus.Approved, key, current, approval!.Identity, held.FilePath, approvalInvalidated: false, []);

                case PluginTrustStatus.PendingApproval:
                    diagnostics.Add(PluginDiagnosticSeverity.Info, TrustDiagnosticCode.ApprovalPending, $"{what} has not been approved.");
                    break;

                case PluginTrustStatus.StaleApproval:
                    diagnostics.Add(
                        PluginDiagnosticSeverity.Error,
                        TrustDiagnosticCode.StaleApproval,
                        approval is null
                            ? $"The expected approval of {what} no longer exists; the plugin was revoked or its approval was invalidated."
                            : $"The expected approval of {what} has been replaced by a newer approval.");
                    break;

                case PluginTrustStatus.ContentChanged when evaluation.InvalidatesApproval:
                    // Cancellation is not observed here: once a change has been observed, its invalidation is saved
                    // (or, failing that, remembered by this instance).
                    diagnostics.Add(ContentChanged(what, current, approval!));
                    bool removed = SaveInvalidation(store, storeLock!, snapshot, approval!, what, diagnostics);
                    return new PluginTrustDecision(PluginTrustStatus.ContentChanged, key, current, approval: null, validatedFilePath: null, removed, diagnostics.ToImmutable());

                default:
                    // Not produced by the current policy. Deny without changing anything.
                    break;
            }

            return new PluginTrustDecision(evaluation.Status, key, current, approval: null, validatedFilePath: null, approvalInvalidated: false, diagnostics.ToImmutable());
        }
    }

    [SupportedOSPlatform("windows")]
    private PluginApprovalResult ApproveCore(PluginTrustKey key, ContentFingerprint expected, CancellationToken cancellationToken)
    {
        var store = new ApprovalStore(StorePath, _storeDirectory, _hooks);
        TrustFailure? failure = store.AcquireLock(_lockTimeout, cancellationToken, out StoreLock? storeLock);
        if (failure is not null)
        {
            return ApprovalFailed(failure, key, current: null);
        }

        using (storeLock)
        {
            failure = store.Read(out ApprovalSnapshot? snapshot);
            if (failure is not null)
            {
                return ApprovalFailed(failure, key, current: null);
            }

            _pending.RetainOnly(snapshot!);
            var diagnostics = new TrustDiagnosticBag();
            string what = Describe(key);
            PluginApproval? existing = snapshot!.Find(key);
            bool alreadyInvalidated = existing is not null && _pending.Contains(existing.Generation);

            failure = new PluginFileInspector(_hooks).Inspect(key, cancellationToken, out InspectedPlugin? inspected);
            if (failure is not null)
            {
                // Not a confirmed content change. Only an approval this instance already invalidated is removed.
                diagnostics.Add(failure.Diagnostic);
                bool removed = false;
                if (alreadyInvalidated)
                {
                    diagnostics.Add(UnsavedInvalidation(what));
                    removed = SaveInvalidation(store, storeLock!, snapshot, existing!, what, diagnostics);
                }

                return new PluginApprovalResult(ApprovalStatus(failure), key, current: null, approval: null, removed, diagnostics.ToImmutable());
            }

            // The approval is saved while the inspected file is still held open, so it cannot be written,
            // deleted or renamed between the final hash and the commit.
            using InspectedPlugin held = inspected!;
            PluginFingerprint current = held.Plugin;

            // An approval of other content is obsolete, whatever this request asks for.
            bool obsolete = alreadyInvalidated || (existing is not null && PluginTrustPolicy.Evaluate(existing, current).InvalidatesApproval);
            TrustDiagnostic? obsoleteDiagnostic = !obsolete ? null : alreadyInvalidated ? UnsavedInvalidation(what) : ContentChanged(what, current, existing!);

            PluginApprovalAction action = PluginTrustPolicy.EvaluateApproval(obsolete ? null : existing, current, expected);
            if (action == PluginApprovalAction.KeepExistingApproval && _pending.IsSaturated)
            {
                // This instance may have lost track of an invalidation, so no existing generation is kept.
                action = PluginApprovalAction.CreateApproval;
            }

            switch (action)
            {
                case PluginApprovalAction.KeepExistingApproval:
                    return new PluginApprovalResult(PluginApprovalStatus.AlreadyApproved, key, current, existing, approvalInvalidated: false, []);

                case PluginApprovalAction.CreateApproval:
                    if (existing is null && snapshot.Count >= MaxApprovals)
                    {
                        return ApprovalFailed(
                            TrustFailure.Store(TrustDiagnosticCode.StoreTooManyRecords, $"The trust store already holds the maximum of {MaxApprovals} approvals."),
                            key,
                            current);
                    }

                    ApprovalGeneration generation;
                    do
                    {
                        generation = ApprovalGeneration.FromGuid(Guid.NewGuid());
                    }
                    while (snapshot.ContainsGeneration(generation));

                    var approval = new PluginApproval(key, current.Fingerprint, generation, _timeProvider.GetUtcNow().ToUniversalTime());

                    // The last point at which cancellation is observed. An observed obsolete approval is still
                    // invalidated before the cancellation is reported.
                    if (cancellationToken.IsCancellationRequested && obsolete)
                    {
                        SaveInvalidation(store, storeLock!, snapshot, existing!, what, new TrustDiagnosticBag());
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Replaces an obsolete approval, if any, in the same atomic update.
                    snapshot.Set(approval);
                    failure = store.Write(storeLock!, snapshot);
                    if (failure is null)
                    {
                        if (obsolete)
                        {
                            _pending.Remove(existing!.Generation);
                        }

                        return new PluginApprovalResult(PluginApprovalStatus.Approved, key, current, approval, approvalInvalidated: obsolete, []);
                    }

                    if (obsolete)
                    {
                        diagnostics.Add(obsoleteDiagnostic!);
                    }

                    diagnostics.Add(failure.Diagnostic);
                    if (obsolete)
                    {
                        RememberUnsavedInvalidation(existing!, what, diagnostics);
                    }

                    return new PluginApprovalResult(PluginApprovalStatus.StoreUnavailable, key, current, approval: null, approvalInvalidated: false, diagnostics.ToImmutable());

                default:
                    diagnostics.Add(
                        PluginDiagnosticSeverity.Error,
                        TrustDiagnosticCode.FingerprintMismatch,
                        $"The current content of {what} (sha256 {current.Fingerprint.Value}) is not the content to be approved (sha256 {expected.Value}). "
                        + "It may have changed after it was reviewed; inspect and review it again.");
                    bool invalidated = false;
                    if (obsolete)
                    {
                        // Cancellation is not observed: the observed obsolete approval is removed (or remembered).
                        diagnostics.Add(obsoleteDiagnostic!);
                        invalidated = SaveInvalidation(store, storeLock!, snapshot, existing!, what, diagnostics);
                    }

                    return new PluginApprovalResult(PluginApprovalStatus.FingerprintMismatch, key, current, approval: null, invalidated, diagnostics.ToImmutable());
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private PluginRevocationResult RevokeCore(PluginTrustKey key, CancellationToken cancellationToken)
    {
        var store = new ApprovalStore(StorePath, _storeDirectory, _hooks);
        TrustFailure? failure = store.AcquireLock(_lockTimeout, cancellationToken, out StoreLock? storeLock);
        if (failure is not null)
        {
            return new PluginRevocationResult(PluginRevocationStatus.StoreUnavailable, key, revoked: null, [failure.Diagnostic]);
        }

        using (storeLock)
        {
            failure = store.Read(out ApprovalSnapshot? snapshot);
            if (snapshot is not null)
            {
                _pending.RetainOnly(snapshot);
            }

            PluginApproval? existing = snapshot?.Find(key);
            if (failure is null && existing is not null)
            {
                // The last point at which cancellation is observed.
                cancellationToken.ThrowIfCancellationRequested();
                snapshot!.Remove(key);
                failure = store.Write(storeLock!, snapshot);
                if (failure is null)
                {
                    _pending.Remove(existing.Generation);
                }
            }

            if (failure is not null)
            {
                return new PluginRevocationResult(PluginRevocationStatus.StoreUnavailable, key, revoked: null, [failure.Diagnostic]);
            }

            return existing is null
                ? new PluginRevocationResult(PluginRevocationStatus.NotApproved, key, revoked: null, [])
                : new PluginRevocationResult(PluginRevocationStatus.Revoked, key, existing, []);
        }
    }

    // Removes an invalidated approval from the store. If that cannot be saved, this instance remembers the
    // invalidation, so it keeps denying the approval and retries. Returns whether the removal was saved.
    [SupportedOSPlatform("windows")]
    private bool SaveInvalidation(ApprovalStore store, StoreLock storeLock, ApprovalSnapshot snapshot, PluginApproval approval, string what, TrustDiagnosticBag diagnostics)
    {
        snapshot.Remove(approval.Key);
        TrustFailure? writeFailure = store.Write(storeLock, snapshot);
        if (writeFailure is null)
        {
            _pending.Remove(approval.Generation);
            diagnostics.Add(PluginDiagnosticSeverity.Info, TrustDiagnosticCode.ApprovalInvalidated, $"The previous approval of {what} was removed from the trust store.");
            return true;
        }

        diagnostics.Add(writeFailure.Diagnostic);
        RememberUnsavedInvalidation(approval, what, diagnostics);
        return false;
    }

    private void RememberUnsavedInvalidation(PluginApproval approval, string what, TrustDiagnosticBag diagnostics)
    {
        diagnostics.Add(
            PluginDiagnosticSeverity.Error,
            TrustDiagnosticCode.InvalidationNotPersisted,
            _pending.Add(approval.Generation)
                ? $"The invalidation of the approval of {what} could not be saved. The plugin is denied. This trust service keeps denying that approval, even if "
                  + "the earlier content returns, and retries saving the invalidation on its next operation for the plugin. Other trust service instances, "
                  + "other processes and later sessions only see the old approval in the store."
                : $"The invalidation of the approval of {what} could not be saved, and this trust service cannot track more unsaved invalidations. "
                  + "It now denies every plugin.");
    }

    private static TrustDiagnostic ContentChanged(string what, PluginFingerprint current, PluginApproval approval) =>
        new(
            PluginDiagnosticSeverity.Error,
            TrustDiagnosticCode.ContentChanged,
            $"The content of {what} (sha256 {current.Fingerprint.Value}) differs from the approved content (sha256 {approval.Fingerprint.Value}). It must be approved again.");

    private static TrustDiagnostic UnsavedInvalidation(string what) =>
        new(
            PluginDiagnosticSeverity.Error,
            TrustDiagnosticCode.ContentChanged,
            $"The approval of {what} was invalidated earlier by this trust service because the plugin's content changed, but the invalidation has not been "
            + "saved yet. It is denied whatever the file holds now, and must be approved again.");

    private static TrustFailure Saturated() =>
        TrustFailure.Store(
            TrustDiagnosticCode.InvalidationNotPersisted,
            "This trust service has more unsaved invalidations than it can track, so it denies every plugin.");

    // Validates the root and identity without touching the file system, and rejects roots that overlap the
    // store directory.
    private TrustFailure? TryCreateKey(string rootPath, PluginId pluginId, out PluginTrustKey? key, bool checkStoreOverlap = true)
    {
        key = null;
        if (!OperatingSystem.IsWindows())
        {
            return TrustFailure.Unsafe(TrustDiagnosticCode.UnsupportedPlatform, "Plugin trust operations are only supported on Windows.");
        }

        TrustFailure? failure = TrustPaths.TryParseRoot(rootPath, out PluginRoot? root);
        if (failure is not null)
        {
            return failure;
        }

        if (!PluginTrustKey.IsSupportedPluginId(pluginId))
        {
            return TrustFailure.Unsafe(
                TrustDiagnosticCode.InvalidPluginPath,
                $"{TrustPaths.Quote(pluginId.Value)} is not a plugin path that discovery can produce: it is longer than {PluginTrustKey.MaxPluginIdLength} characters, "
                + $"more than {PluginTrustKey.MaxDirectoryDepth} directories deep, has a segment beginning with '.', or is not a valid plugin file name.");
        }

        if (checkStoreOverlap && root!.Overlaps(_storeDirectory))
        {
            return TrustFailure.Unsafe(
                TrustDiagnosticCode.RootOverlapsTrustStore,
                $"The plugin root {TrustPaths.Quote(root.Value)} overlaps the trust store directory {TrustPaths.Quote(_storeDirectory.Value)}. Keep plugins and the trust store apart.");
        }

        key = new PluginTrustKey(root!, pluginId);
        return null;
    }

    private static string Describe(PluginTrustKey key) => $"{TrustPaths.Quote(key.Id.Value)} in {TrustPaths.Quote(key.Root.Value)}";

    private static PluginTrustDecision DecisionFailed(TrustFailure failure, PluginTrustKey? key)
    {
        PluginTrustStatus status = failure.Kind switch
        {
            TrustFailureKind.PluginUnavailable => PluginTrustStatus.PluginUnavailable,
            TrustFailureKind.StoreUnavailable => PluginTrustStatus.StoreUnavailable,
            _ => PluginTrustStatus.UnsafePath,
        };
        return new PluginTrustDecision(status, key, current: null, approval: null, validatedFilePath: null, approvalInvalidated: false, [failure.Diagnostic]);
    }

    private static PluginApprovalStatus ApprovalStatus(TrustFailure failure) => failure.Kind switch
    {
        TrustFailureKind.PluginUnavailable => PluginApprovalStatus.PluginUnavailable,
        TrustFailureKind.StoreUnavailable => PluginApprovalStatus.StoreUnavailable,
        _ => PluginApprovalStatus.UnsafePath,
    };

    private static PluginApprovalResult ApprovalFailed(TrustFailure failure, PluginTrustKey? key, PluginFingerprint? current) =>
        new(ApprovalStatus(failure), key, current, approval: null, approvalInvalidated: false, [failure.Diagnostic]);
}
