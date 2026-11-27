using System.Collections.Immutable;
using System.Diagnostics;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Runtime.Execution;

// Collects the execution diagnostics of one run, keeping at most PluginExecutionService.MaxDiagnostics. When the
// limit is reached, the last slot reports that diagnostics were dropped. Independent of the trust and parser limits.
internal sealed class ExecutionDiagnosticBag
{
    private readonly List<ExecutionDiagnostic> _items = [];
    private bool _overflowed;

    public void Add(PluginDiagnosticSeverity severity, ExecutionDiagnosticCode code, string message)
    {
        if (_overflowed)
        {
            return;
        }

        if (_items.Count < PluginExecutionService.MaxDiagnostics - 1)
        {
            _items.Add(new ExecutionDiagnostic(severity, code, message));
            return;
        }

        _overflowed = true;
        _items.Add(new ExecutionDiagnostic(
            PluginDiagnosticSeverity.Warning,
            ExecutionDiagnosticCode.DiagnosticLimitReached,
            $"More than {PluginExecutionService.MaxDiagnostics - 1} execution diagnostics were produced; the rest were dropped."));
    }

    public bool Contains(ExecutionDiagnosticCode code) => _items.Exists(item => item.Code == code);

    public ImmutableArray<ExecutionDiagnostic> ToImmutable() => [.. _items];
}

// Deterministic seams for tests. Never set in production. A record, so tests can derive variants with "with".
internal sealed record class ExecutionTestHooks
{
    // Replaces the environment the child environment is built from (default: this process's environment).
    public IReadOnlyDictionary<string, string>? BaseEnvironment { get; init; }

    // Replaces the locations used for interpreter resolution (default: this process's PATH and folders).
    public InterpreterSearchLocations? SearchLocations { get; init; }

    // Immediately before the final cancellation check and Process.Start, with the prepared start information.
    public Action<ProcessStartInfo>? BeforeProcessStart { get; init; }

    // Immediately after Process.Start returned a process, inside the supervision cleanup boundary.
    public Action<Process>? ProcessStarted { get; init; }

    // Immediately before each supervision setup step, inside the cleanup boundary. Throwing injects a setup failure.
    public Action<SupervisionStep>? BeforeSupervisionStep { get; init; }

    // Replaces Process.WaitForExitAsync, so exit monitoring faults can be injected.
    public Func<Process, CancellationToken, Task>? WaitForExit { get; init; }

    // Awaited each time the supervisor resumes, before it decides why the run ended, so a delayed supervision
    // continuation can be reproduced deterministically.
    public Func<RunArbiter, Task>? BeforeDecision { get; init; }

    // After each chunk read from standard output, with the total number of bytes read so far.
    public Action<long>? StandardOutputRead { get; init; }

    // At the end of supervision, with a task that completes when every capture loop that was started has ended.
    public Action<Task>? CaptureLoopsEnded { get; init; }

    // Replaces the cleanup grace period, so incomplete cleanup can be tested quickly.
    public TimeSpan? CleanupGracePeriod { get; init; }

    // Replaces the minimum run timeout, so timeouts can be tested quickly.
    public TimeSpan? MinTimeout { get; init; }

    // When set, termination does nothing (no job termination, no kill), so incomplete cleanup can be tested.
    public bool SuppressTermination { get; init; }

    // When set, the process is not added to a job object.
    public bool DisableJobObject { get; init; }

    // When set, adding the process to its job object fails as if Windows refused (access denied).
    public bool FailJobAssignment { get; init; }

    // When set, querying the job object's process count fails.
    public bool FailJobQuery { get; init; }

    // When set, terminating the job object fails as if Windows refused (access denied); the job stays open.
    public bool FailJobTermination { get; init; }
}
