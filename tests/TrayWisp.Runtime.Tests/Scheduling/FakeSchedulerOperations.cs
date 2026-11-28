using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Scheduling;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Tests.Scheduling;

// Deterministic stand-ins for discovery, the trust store and the execution engine. A small in-memory "file system" holds
// plugins (identity, content version, title); an in-memory "trust store" holds approvals bound to a content fingerprint
// and a generation, and behaves like PluginTrustService: a check that sees other content invalidates the approval
// persistently, and a check with an expected identity rejects any other generation. A run is "launched" only when that
// check approves; it then stays active until the test completes it (or, if it honors cancellation, until it is cancelled).
internal sealed class FakeSchedulerOperations(PluginRoot root) : SchedulerOperations
{
    private readonly object _lock = new();
    private readonly Dictionary<PluginId, FakeFile> _files = [];
    private readonly Dictionary<PluginId, PluginApprovalIdentity> _approvals = [];
    private readonly Dictionary<PluginId, int> _activePerKey = [];
    private readonly HashSet<PluginId> _unsafe = [];
    private readonly List<FakeRun> _runs = [];
    private readonly List<FakeWatcher> _watchers = [];
    private int _activeRuns;

    public PluginRoot Root { get; } = root;

    // File system state.
    public bool ScanComplete { get; set; } = true;

    public SchedulerDiagnostic? RootProblem { get; set; }

    public RootIdentity Identity { get; set; } = new(1, 1, 1);

    public string? WatcherProblem { get; set; }

    // Trust store state.
    public bool StoreUnavailable { get; set; }

    // Blocking points: a gate blocks the operation until released (observing its cancellation token unless told not to).
    public Gate? DiscoverGate { get; set; }

    public Gate? CheckGate { get; set; }

    // Blocks a trust check after its decision has been made (and the trust store updated), before it is returned: the
    // obtained result is delayed, not the start of the check.
    public Gate? CheckResultGate { get; set; }

    public Gate? ApprovalCheckGate { get; set; }

    public Action<PluginApprovalIdentity>? BeforeApprovalCheck { get; set; }

    public Func<PluginId, Exception?>? RunThrows { get; set; }

    // When set, every launched run completes at once with this outcome.
    public Func<FakeRun, RunOutcome?>? AutoComplete { get; set; }

    public bool RunsHonorCancellation { get; set; } = true;

    public int Scans { get; private set; }

    public int TrustChecks { get; private set; }

    public int ApprovalChecks { get; private set; }

    // Calls of RunAsync, whether or not they launched anything.
    public int EngineCalls { get; private set; }

    // Calls of RunAsync that received an already cancelled token.
    public int CancelledEngineCalls { get; private set; }

    // When set, every launched run registers it as a cancellation callback, as the engine registers its own.
    public Action? CancellationCallback { get; set; }

    // Called with the token each operation receives ("discover", "trust", "approval", "run"), at the start of the
    // operation, so a test can use it as the trust store does (for example read its WaitHandle).
    public Action<string, CancellationToken>? TokenObserved { get; set; }

    public int DeniedRuns { get; private set; }

    public int MaxConcurrentRuns { get; private set; }

    public int MaxConcurrentPerKey { get; private set; }

    public IReadOnlyList<FakeRun> Runs
    {
        get
        {
            lock (_lock)
            {
                return [.. _runs];
            }
        }
    }

    public IReadOnlyList<FakeWatcher> Watchers
    {
        get
        {
            lock (_lock)
            {
                return [.. _watchers];
            }
        }
    }

    public int Launches(string id) => Runs.Count(run => run.Id == PluginId.Create(id));

    public FakeRun LastRun(string id) => Runs.Last(run => run.Id == PluginId.Create(id));

    public IReadOnlyList<FakeRun> ActiveRuns => [.. Runs.Where(run => !run.IsCompleted)];

    // File system -----------------------------------------------------------------------------------------------------

    public void Add(string id, int content = 1, string? title = null)
    {
        lock (_lock)
        {
            PluginId pluginId = PluginId.Create(id);
            _files.Remove(pluginId);
            _files.Add(pluginId, new FakeFile(pluginId, content, title));
        }
    }

    // Changes the content and the metadata (title), as an edit of the metadata comment would.
    public void Modify(string id)
    {
        lock (_lock)
        {
            FakeFile file = _files[PluginId.Create(id)];
            _files[file.Id] = file with { Content = file.Content + 1, Title = (file.Title ?? "t") + "'" };
        }
    }

    // Changes the bytes but not the metadata (same length, same timestamp: only the hash can tell).
    public void ModifyContentOnly(string id)
    {
        lock (_lock)
        {
            FakeFile file = _files[PluginId.Create(id)];
            _files[file.Id] = file with { Content = file.Content + 1 };
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _files.Remove(PluginId.Create(id));
        }
    }

    // Renames a file. The approval stays with the old identity, as in the real store.
    public void Rename(string from, string to)
    {
        lock (_lock)
        {
            FakeFile file = _files[PluginId.Create(from)];
            _files.Remove(file.Id);
            PluginId target = PluginId.Create(to);
            _files.Add(target, file with { Id = target });
        }
    }

    // Trust store ------------------------------------------------------------------------------------------------------

    public PluginApprovalIdentity Approve(string id)
    {
        lock (_lock)
        {
            FakeFile file = _files[PluginId.Create(id)];
            var identity = new PluginApprovalIdentity(Key(file.Id), Fingerprint(file), ApprovalGeneration.FromGuid(Guid.NewGuid()));
            _approvals[file.Id] = identity;
            return identity;
        }
    }

    // The trust check reports the plugin's path as unsafe from now on.
    public void MarkUnsafe(string id)
    {
        lock (_lock)
        {
            _unsafe.Add(PluginId.Create(id));
        }
    }

    public void Revoke(string id)
    {
        lock (_lock)
        {
            _approvals.Remove(PluginId.Create(id));
        }
    }

    public PluginApprovalIdentity? ApprovalOf(string id)
    {
        lock (_lock)
        {
            return _approvals.GetValueOrDefault(PluginId.Create(id));
        }
    }

    // Operations -------------------------------------------------------------------------------------------------------

    public override RootValidation ValidateRoot(PluginRoot pluginRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return RootProblem is { } problem ? new RootValidation(problem) : new RootValidation(handle: null, Identity);
        }
    }

    public override PluginDiscoveryResult Discover(PluginRoot pluginRoot, CancellationToken cancellationToken)
    {
        TokenObserved?.Invoke("discover", cancellationToken);

        // The listing is taken first, as a real scan reads the directory before anything else can change it.
        ImmutableArray<PluginDescriptor> plugins;
        bool complete;
        lock (_lock)
        {
            Scans++;
            plugins = [.. _files.Values.OrderBy(file => file.Id).Select(Descriptor)];
            complete = ScanComplete;
        }

        DiscoverGate?.Wait(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<PluginDiagnostic> diagnostics = complete ? [] : [new PluginDiagnostic(PluginDiagnosticSeverity.Error, PluginDiagnosticCode.FileUnreadable, "locked")];
        return new PluginDiscoveryResult(Root.Value, plugins, diagnostics, complete, plugins.Length);
    }

    public override PluginTrustDecision CheckTrust(PluginRoot pluginRoot, PluginId id, CancellationToken cancellationToken)
    {
        TokenObserved?.Invoke("trust", cancellationToken);
        CheckGate?.Wait(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        PluginTrustDecision decision;
        lock (_lock)
        {
            TrustChecks++;
            decision = Evaluate(id, expected: null);
        }

        CheckResultGate?.Wait(cancellationToken);
        return decision;
    }

    public override PluginTrustDecision CheckApproval(PluginApprovalIdentity approval, CancellationToken cancellationToken)
    {
        TokenObserved?.Invoke("approval", cancellationToken);
        BeforeApprovalCheck?.Invoke(approval);
        ApprovalCheckGate?.Wait(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ApprovalChecks++;
            return Evaluate(approval.Key.Id, approval);
        }
    }

    public override Task<PluginExecutionResult> RunAsync(PluginRoot pluginRoot, PluginId id, PluginApprovalIdentity expectedApproval, CancellationToken cancellationToken)
    {
        if (RunThrows?.Invoke(id) is { } exception)
        {
            throw exception;
        }

        TokenObserved?.Invoke("run", cancellationToken);
        FakeRun run;
        lock (_lock)
        {
            EngineCalls++;
            if (cancellationToken.IsCancellationRequested)
            {
                // As the engine: cancelled before anything was started.
                CancelledEngineCalls++;
                return Task.FromResult(Result(id, PluginExecutionStatus.Cancelled, approval: null, trustStatus: null));
            }

            PluginTrustDecision decision = Evaluate(id, expectedApproval);
            if (!decision.IsApproved)
            {
                DeniedRuns++;
                return Task.FromResult(Result(id, PluginExecutionStatus.TrustDenied, approval: null, decision.Status));
            }

            run = new FakeRun(this, id, decision.Approval!, cancellationToken);
            _runs.Add(run);
            _activeRuns++;
            MaxConcurrentRuns = Math.Max(MaxConcurrentRuns, _activeRuns);
            _activePerKey[id] = _activePerKey.GetValueOrDefault(id) + 1;
            MaxConcurrentPerKey = Math.Max(MaxConcurrentPerKey, _activePerKey[id]);
        }

        if (CancellationCallback is { } callback)
        {
            cancellationToken.Register(callback);
        }

        if (RunsHonorCancellation)
        {
            run.HonorCancellation();
        }

        if (AutoComplete?.Invoke(run) is { } outcome)
        {
            run.Complete(outcome);
        }

        return run.Task;
    }

    public override IDisposable? CreateWatcher(PluginRoot pluginRoot, Action<WatcherNotification> sink, out string? problem)
    {
        lock (_lock)
        {
            problem = WatcherProblem;
            if (problem is not null)
            {
                return null;
            }

            var watcher = new FakeWatcher(sink);
            _watchers.Add(watcher);
            return watcher;
        }
    }

    internal void RunEnded(FakeRun run)
    {
        lock (_lock)
        {
            _activeRuns--;
            _activePerKey[run.Id]--;
        }
    }

    // Models PluginTrustService.CheckTrust (and the expected-identity variants). Called under the lock.
    private PluginTrustDecision Evaluate(PluginId id, PluginApprovalIdentity? expected)
    {
        PluginTrustKey key = Key(id);
        if (StoreUnavailable)
        {
            return Decision(PluginTrustStatus.StoreUnavailable, key, approval: null);
        }

        if (expected is not null && expected.Key != key)
        {
            return Decision(PluginTrustStatus.StaleApproval, key, approval: null);
        }

        if (_unsafe.Contains(id))
        {
            return Decision(PluginTrustStatus.UnsafePath, key, approval: null);
        }

        if (!_files.TryGetValue(id, out FakeFile? file))
        {
            return Decision(PluginTrustStatus.PluginUnavailable, key, approval: null);
        }

        PluginApprovalIdentity? approval = _approvals.GetValueOrDefault(id);
        if (approval is not null && approval.Fingerprint != Fingerprint(file))
        {
            // An observed change invalidates the approval persistently.
            _approvals.Remove(id);
            return Decision(PluginTrustStatus.ContentChanged, key, approval: null, invalidated: true);
        }

        if (approval is null)
        {
            return Decision(expected is null ? PluginTrustStatus.PendingApproval : PluginTrustStatus.StaleApproval, key, approval: null);
        }

        if (expected is not null && expected != approval)
        {
            return Decision(PluginTrustStatus.StaleApproval, key, approval: null);
        }

        return Decision(PluginTrustStatus.Approved, key, approval);
    }

    private PluginTrustKey Key(PluginId id) => new(Root, id);

    private static ContentFingerprint Fingerprint(FakeFile file) =>
        ContentFingerprint.FromHash(SHA256.HashData(Encoding.UTF8.GetBytes(file.Id.Value.ToUpperInvariant() + "|" + file.Content)));

    private static PluginDescriptor Descriptor(FakeFile file) =>
        new(file.Id, PluginFileNameParser.Parse(file.Id.FileName).FileName!, new PluginMetadata { Title = file.Title }, []);

    private static PluginTrustDecision Decision(PluginTrustStatus status, PluginTrustKey key, PluginApprovalIdentity? approval, bool invalidated = false) =>
        new(status, key, current: null, approval, approval is null ? null : @"C:\fake\" + key.Id.Value.Replace('/', '\\'), invalidated, []);

    public static PluginExecutionResult Result(PluginId id, PluginExecutionStatus status, PluginApprovalIdentity? approval, PluginTrustStatus? trustStatus, string? output = null, ProcessCleanupStatus? cleanup = null)
    {
        var details = new ResultDetails
        {
            Key = approval?.Key,
            Approval = approval,
            TrustStatus = trustStatus,
            Cleanup = cleanup is { } value ? new PluginProcessCleanup(value, value != ProcessCleanupStatus.Incomplete, jobObjectAssigned: true, value == ProcessCleanupStatus.Incomplete ? null : 0) : PluginProcessCleanup.NotStarted,
            ExitCode = status == PluginExecutionStatus.NonZeroExitCode ? 1 : 0,
            StandardOutputComplete = output is not null,
            StandardOutputBytes = output?.Length ?? 0,
        };
        if (status == PluginExecutionStatus.Succeeded)
        {
            details.Output = PluginOutputParser.Parse(output ?? string.Empty).Output;
        }

        return new PluginExecutionResult(status, id, details);
    }

    private sealed record FakeFile(PluginId Id, int Content, string? Title);
}

internal enum RunOutcome
{
    Succeeded,
    Empty,
    Failed,
    CleanupIncomplete,
    TimedOutIncomplete,
}

// One launched run. The test decides how it ends.
internal sealed class FakeRun
{
    private readonly FakeSchedulerOperations _owner;
    private readonly TaskCompletionSource<PluginExecutionResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completed;

    public FakeRun(FakeSchedulerOperations owner, PluginId id, PluginApprovalIdentity approval, CancellationToken token)
    {
        _owner = owner;
        Id = id;
        Approval = approval;
        Token = token;
        token.Register(() => _cancelled.TrySetResult());
    }

    public PluginId Id { get; }

    public PluginApprovalIdentity Approval { get; }

    public CancellationToken Token { get; }

    public Task<PluginExecutionResult> Task => _result.Task;

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public bool CancellationRequested => Token.IsCancellationRequested;

    public Task Cancelled => _cancelled.Task;

    public void HonorCancellation() =>
        Token.Register(() => Finish(FakeSchedulerOperations.Result(Id, PluginExecutionStatus.Cancelled, Approval, PluginTrustStatus.Approved, cleanup: ProcessCleanupStatus.Terminated)));

    public void Succeed(string output = "ok") => Finish(FakeSchedulerOperations.Result(Id, PluginExecutionStatus.Succeeded, Approval, PluginTrustStatus.Approved, output, ProcessCleanupStatus.Exited));

    public void Complete(RunOutcome outcome) =>
        Finish(outcome switch
        {
            RunOutcome.Succeeded => FakeSchedulerOperations.Result(Id, PluginExecutionStatus.Succeeded, Approval, PluginTrustStatus.Approved, "ok", ProcessCleanupStatus.Exited),
            RunOutcome.Empty => FakeSchedulerOperations.Result(Id, PluginExecutionStatus.Succeeded, Approval, PluginTrustStatus.Approved, string.Empty, ProcessCleanupStatus.Exited),
            RunOutcome.Failed => FakeSchedulerOperations.Result(Id, PluginExecutionStatus.NonZeroExitCode, Approval, PluginTrustStatus.Approved, "x", ProcessCleanupStatus.Exited),
            RunOutcome.CleanupIncomplete => FakeSchedulerOperations.Result(Id, PluginExecutionStatus.CleanupIncomplete, Approval, PluginTrustStatus.Approved, cleanup: ProcessCleanupStatus.Incomplete),
            _ => FakeSchedulerOperations.Result(Id, PluginExecutionStatus.TimedOut, Approval, PluginTrustStatus.Approved, cleanup: ProcessCleanupStatus.Incomplete),
        });

    private void Finish(PluginExecutionResult result)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _owner.RunEnded(this);
            _result.SetResult(result);
        }
    }
}

internal sealed class FakeWatcher(Action<WatcherNotification> sink) : IDisposable
{
    public bool Disposed { get; private set; }

    // When set, Dispose blocks until the gate is released, as a disposal that waits for an operating system call might.
    public Gate? DisposeGate { get; set; }

    public bool ThrowOnDispose { get; set; }

    public void Raise(WatcherChangeKind kind, string? name = null, string? oldName = null) => sink(new WatcherNotification(kind, name, oldName));

    public void Dispose()
    {
        DisposeGate?.Wait(CancellationToken.None);
        Disposed = true;
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("dispose");
        }
    }
}

// Blocks a synchronous operation until released.
internal sealed class Gate(bool observeCancellation = true)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Wait(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        if (observeCancellation)
        {
            _released.Task.Wait(cancellationToken);
        }
        else
        {
            _released.Task.Wait(TestTimeout, CancellationToken.None);
        }
    }

    public void Release() => _released.TrySetResult();

    public static TimeSpan TestTimeout { get; } = TimeSpan.FromSeconds(30);
}
