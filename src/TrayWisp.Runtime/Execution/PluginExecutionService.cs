using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Execution;

/// <summary>
/// Runs approved plugins: the one execution entry point for every host (scheduler, CLI, tray, management UI). See
/// docs/execution-engine.md.
/// </summary>
/// <remarks>
/// <para>Every run calls the authoritative trust check of the supplied <see cref="PluginTrustService"/> immediately
/// before the launch, and launches only when it returns <see cref="PluginTrustStatus.Approved"/>, using exactly the
/// file path that check validated. Nothing is cached between runs: no decision, interpreter or result.</para>
/// <para>A run launches the interpreter directly (no shell) with fixed arguments, closes its standard input, captures
/// standard output and standard error with byte limits, enforces a timeout, terminates the process tree on timeout,
/// cancellation or overflow, terminates tracked processes the plugin leaves running after it exits, and parses
/// complete standard output with <see cref="PluginOutputParser"/>. Parsed actions are never executed.</para>
/// <para>Approval is consent, not sandboxing: a plugin runs with the full rights of the user's account.</para>
/// <para>Instances are thread-safe; concurrent runs are independent. Global concurrency limits and the prevention of
/// overlapping runs of one plugin are the scheduler's responsibility.</para>
/// </remarks>
public sealed class PluginExecutionService
{
    /// <summary>Most bytes of standard output kept: 1 MiB. Output beyond it fails the run with <see cref="PluginExecutionStatus.OutputLimitExceeded"/>.</summary>
    public const int MaxStandardOutputBytes = 1024 * 1024;

    /// <summary>Most bytes of standard error kept: the last 64 KiB.</summary>
    public const int MaxStandardErrorBytes = 64 * 1024;

    /// <summary>Most execution diagnostics one result holds, including a final <see cref="ExecutionDiagnosticCode.DiagnosticLimitReached"/>.</summary>
    public const int MaxDiagnostics = 64;

    /// <summary>
    /// How long TrayWisp waits, after terminating a run, for the process to exit and its output streams to close: 5
    /// seconds. Reads still pending after it are stopped and given at most one more second.
    /// </summary>
    public static TimeSpan CleanupGracePeriod { get; } = TimeSpan.FromSeconds(5);

    private readonly PluginTrustService _trust;
    private readonly TimeSpan _defaultTimeout;
    private readonly ConfiguredExecutable? _powerShell;
    private readonly ConfiguredExecutable? _python;
    private readonly ConfiguredExecutable? _node;
    private readonly TimeProvider _time;
    private readonly ExecutionTestHooks? _hooks;

    /// <summary>Creates a service that authorizes every run with <paramref name="trustService"/>.</summary>
    /// <param name="trustService">
    /// The host's long-lived trust service for its approval store. It must be the same instance for every run, so that
    /// invalidations it could not save keep being enforced (see docs/trust-model.md, "Unsaved invalidations").
    /// </param>
    /// <param name="options">Trusted host configuration; <see cref="PluginExecutionOptions.Default"/> when <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">A configured interpreter path is not a fully qualified local path to an <c>.exe</c> file.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The default timeout is out of range.</exception>
    public PluginExecutionService(PluginTrustService trustService, PluginExecutionOptions? options = null)
        : this(trustService, options, TimeProvider.System, hooks: null)
    {
    }

    internal PluginExecutionService(PluginTrustService trustService, PluginExecutionOptions? options, TimeProvider timeProvider, ExecutionTestHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(trustService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= PluginExecutionOptions.Default;
        _hooks = hooks;
        ValidateTimeout(options.DefaultTimeout, nameof(options));
        _trust = trustService;
        _defaultTimeout = options.DefaultTimeout;
        _powerShell = ParseConfigured(options.PowerShellPath, nameof(options.PowerShellPath));
        _python = ParseConfigured(options.PythonPath, nameof(options.PythonPath));
        _node = ParseConfigured(options.NodePath, nameof(options.NodePath));
        _time = timeProvider;
    }

    /// <summary>
    /// Runs a plugin once, if and only if the authoritative trust check made immediately before the launch approves it.
    /// </summary>
    /// <param name="rootPath">The plugin root, as for <see cref="PluginTrustService.CheckTrust(string, PluginId, CancellationToken)"/>.</param>
    /// <param name="pluginId">The plugin's root-relative identity.</param>
    /// <param name="runOptions">Settings for this run; <see cref="PluginRunOptions.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">
    /// Cancels the run. Before the launch, it prevents it; afterwards, it terminates the process. Cancellation is
    /// reported as <see cref="PluginExecutionStatus.Cancelled"/>, not thrown.
    /// </param>
    /// <returns>
    /// The result. Every failure, denial and cancellation is a result, including failures to set up or maintain
    /// supervision of a started process (<see cref="PluginExecutionStatus.SupervisionFailed"/>); this method does not
    /// throw for them. Only fatal runtime failures (such as running out of memory) and programming errors propagate,
    /// after TrayWisp has attempted to terminate a started process.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="rootPath"/> or <paramref name="pluginId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout in <paramref name="runOptions"/> is out of range.</exception>
    public Task<PluginExecutionResult> RunAsync(string rootPath, PluginId pluginId, PluginRunOptions? runOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(pluginId);
        TimeSpan timeout = (runOptions ?? PluginRunOptions.Default).Timeout ?? _defaultTimeout;
        ValidateTimeout(timeout, nameof(runOptions));
        return RunCoreAsync(rootPath, pluginId, timeout, expectedApproval: null, cancellationToken);
    }

    // The trust service every run of this instance is authorized with. The scheduler makes its own trust checks with it,
    // so that scheduling decisions and launches can never use two different trust stores or service instances.
    internal PluginTrustService TrustService => _trust;

    // RunAsync for a launch selected under a known approval: the launch-time check (unchanged otherwise) additionally
    // requires the current approval to be exactly expectedApproval, so a run selected under one approval generation is
    // never launched under another. Used by the scheduler; not a reusable authorization, and no weaker than RunAsync.
    internal Task<PluginExecutionResult> RunAsync(string rootPath, PluginId pluginId, PluginRunOptions? runOptions, PluginApprovalIdentity expectedApproval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(expectedApproval);
        TimeSpan timeout = (runOptions ?? PluginRunOptions.Default).Timeout ?? _defaultTimeout;
        ValidateTimeout(timeout, nameof(runOptions));
        return RunCoreAsync(rootPath, pluginId, timeout, expectedApproval, cancellationToken);
    }

    private async Task<PluginExecutionResult> RunCoreAsync(string rootPath, PluginId pluginId, TimeSpan timeout, PluginApprovalIdentity? expectedApproval, CancellationToken cancellationToken)
    {
        // Leave the caller's thread and synchronization context (for example a UI thread) before any blocking work:
        // interpreter resolution and the trust check, which hashes the plugin file, are synchronous.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var diagnostics = new ExecutionDiagnosticBag();
        var details = new ResultDetails();
        try
        {
            return OperatingSystem.IsWindows()
                ? await RunOnWindowsAsync(rootPath, pluginId, timeout, expectedApproval, details, diagnostics, cancellationToken).ConfigureAwait(false)
                : Fail(PluginExecutionStatus.UnsupportedPlatform, ExecutionDiagnosticCode.UnsupportedPlatform, "Plugin execution is only supported on Windows.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only thrown before a process exists: resolution, the trust check, or the final check before the launch.
            diagnostics.Add(PluginDiagnosticSeverity.Info, ExecutionDiagnosticCode.Cancelled, "The run was cancelled before the plugin was started.");
            return Build(PluginExecutionStatus.Cancelled);
        }
        finally
        {
            // The lease never outlives its run.
            details.Lease?.Dispose();
        }

        PluginExecutionResult Fail(PluginExecutionStatus status, ExecutionDiagnosticCode code, string message)
        {
            diagnostics.Add(PluginDiagnosticSeverity.Error, code, message);
            return Build(status);
        }

        PluginExecutionResult Build(PluginExecutionStatus status)
        {
            details.Diagnostics = diagnostics.ToImmutable();
            return new PluginExecutionResult(status, pluginId, details);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<PluginExecutionResult> RunOnWindowsAsync(string rootPath, PluginId pluginId, TimeSpan timeout, PluginApprovalIdentity? expectedApproval, ResultDetails details, ExecutionDiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. The language comes from the identity's file name only: never from metadata, shebangs or output.
        PluginFileNameParseResult name = PluginFileNameParser.Parse(pluginId.FileName);
        if (!name.IsParsed)
        {
            // Not a plugin identity: let the trust check reject it with its own diagnostics, without resolving anything.
            return Deny(_trust.CheckTrust(rootPath, pluginId, cancellationToken));
        }

        PluginLanguage language = name.FileName!.Language;
        details.Language = language;
        if (PluginLanguages.GetExecutionSupport(language) != PluginExecutionSupport.Supported)
        {
            diagnostics.Add(
                PluginDiagnosticSeverity.Error,
                ExecutionDiagnosticCode.UnsupportedLanguage,
                $"{TrustPaths.Quote(pluginId.Value)} is a {language} plugin. Only PowerShell, Python and JavaScript plugins can be run; nothing was started.");
            return Build(PluginExecutionStatus.UnsupportedLanguage);
        }

        // 2. Interpreter resolution and the start information: everything that does not authorize, before the check.
        PluginRoot.TryCreate(rootPath, out PluginRoot? root);
        ConfiguredExecutable? configured = language switch
        {
            PluginLanguage.PowerShell => _powerShell,
            PluginLanguage.Python => _python,
            _ => _node,
        };
        var resolver = new InterpreterResolver(_hooks?.SearchLocations ?? InterpreterSearchLocations.FromCurrentProcess(), root, diagnostics, cancellationToken);
        InterpreterResolution resolution = resolver.Resolve(language, configured, out PluginInterpreter? interpreter);
        if (resolution != InterpreterResolution.Resolved)
        {
            return Build(resolution == InterpreterResolution.ConfigurationInvalid ? PluginExecutionStatus.InterpreterConfigurationInvalid : PluginExecutionStatus.InterpreterUnavailable);
        }

        details.Interpreter = interpreter;
        ProcessStartInfo startInfo = InterpreterCommand.Prepare(interpreter!, _hooks?.BaseEnvironment);
        cancellationToken.ThrowIfCancellationRequested();

        // 3. The authoritative trust check, immediately before the launch. On approval, the inspected file and its
        // directories stay held for the whole run. With an expected approval, only exactly that approval authorizes.
        PluginTrustDecision decision = _trust.CheckTrustForExecution(rootPath, pluginId, expectedApproval, cancellationToken, out PluginExecutionLease? lease);
        details.Lease = lease;
        if (lease is null)
        {
            return Deny(decision);
        }

        details.Approval = lease.Approval;
        details.ValidatedFilePath = lease.ValidatedFilePath;

        details.Key = decision.Key;
        details.TrustStatus = decision.Status;
        details.TrustDiagnostics = decision.Diagnostics;

        // 4. Only the validated path is launched. Its language must be the identity's language.
        string scriptPath = lease.ValidatedFilePath;
        PluginFileNameParseResult validatedName = PluginFileNameParser.Parse(Path.GetFileName(scriptPath));
        if (!validatedName.IsParsed || validatedName.FileName!.Language != language)
        {
            diagnostics.Add(
                PluginDiagnosticSeverity.Error,
                ExecutionDiagnosticCode.LanguageMismatch,
                $"The validated file {TrustPaths.Quote(scriptPath)} does not have the language of {TrustPaths.Quote(pluginId.Value)}; nothing was started.");
            return Build(PluginExecutionStatus.TrustDenied);
        }

        if (!PluginRoot.TryCreate(Path.GetDirectoryName(interpreter!.Path), out PluginRoot? interpreterDirectory) || decision.Key!.Root.Contains(interpreterDirectory))
        {
            // Resolution already excludes the root; this guards against a root spelled differently by the caller.
            diagnostics.Add(
                PluginDiagnosticSeverity.Error,
                ExecutionDiagnosticCode.InterpreterCandidateRejected,
                $"The interpreter {TrustPaths.Quote(interpreter.Path)} is inside the plugin root; nothing was started.");
            return Build(PluginExecutionStatus.InterpreterConfigurationInvalid);
        }

        InterpreterCommand.SetScript(startInfo, interpreter.Kind, scriptPath);

        // 5. Launch and supervise.
        var run = new PluginProcessRun(_time, _hooks, diagnostics);
        ProcessRunOutcome outcome = await run.RunAsync(lease, startInfo, timeout, _hooks?.CleanupGracePeriod ?? CleanupGracePeriod, cancellationToken).ConfigureAwait(false);
        return Complete(outcome);

        PluginExecutionResult Deny(PluginTrustDecision denied)
        {
            details.Key = denied.Key;
            details.TrustStatus = denied.Status;
            details.TrustDiagnostics = denied.Diagnostics;
            diagnostics.Add(
                PluginDiagnosticSeverity.Error,
                ExecutionDiagnosticCode.TrustDenied,
                $"{TrustPaths.Quote(pluginId.Value)} was not started: the trust check returned {denied.Status}. Only an approved plugin whose current content matches its approval can run.");
            return Build(PluginExecutionStatus.TrustDenied);
        }

        PluginExecutionResult Build(PluginExecutionStatus status)
        {
            details.Diagnostics = diagnostics.ToImmutable();
            return new PluginExecutionResult(status, pluginId, details);
        }

        PluginExecutionResult Complete(ProcessRunOutcome outcome)
        {
            if (!outcome.Started)
            {
                return Build(PluginExecutionStatus.ProcessStartFailed);
            }

            details.StartedAtUtc = outcome.StartedAtUtc;
            details.Elapsed = outcome.Elapsed;
            details.ExitCode = outcome.ExitCode;
            details.TerminationReason = outcome.TerminationReason;
            details.Cleanup = outcome.Cleanup;
            details.StandardOutputBytes = outcome.StandardOutputBytes;
            details.StandardOutputComplete = outcome.StandardOutputComplete;
            details.StandardOutputTruncated = outcome.StandardOutputOverflowed;
            details.StandardErrorBytes = outcome.StandardErrorBytes;
            details.StandardError = PluginOutputDecoder.FormatStandardError(outcome.StandardError, startsMidStream: outcome.StandardErrorOverflowed, out bool shortened);
            details.StandardErrorTruncated = outcome.StandardErrorOverflowed || shortened;
            if (outcome.StandardErrorOverflowed)
            {
                diagnostics.Add(
                    PluginDiagnosticSeverity.Warning,
                    ExecutionDiagnosticCode.StandardErrorTruncated,
                    string.Create(CultureInfo.InvariantCulture, $"The plugin wrote {outcome.StandardErrorBytes} bytes to standard error; only the last {MaxStandardErrorBytes} were kept."));
            }

            // Incomplete cleanup takes precedence over every other outcome and suppresses output: processes started
            // for the plugin may still be running. This includes the cleanup of tracked processes after a normal exit.
            PluginExecutionStatus status = ReportTermination(outcome);
            bool cleanupIncomplete = outcome.Cleanup.Status == ProcessCleanupStatus.Incomplete;
            if (!outcome.StandardOutputComplete)
            {
                // Incomplete capture is never parsed as complete input, however complete the text looks.
                details.OutputDiagnostics = PluginOutputParser.Parse([], isTruncated: true).Diagnostics;
                return Final(status);
            }

            if (outcome.ExitCode != 0)
            {
                diagnostics.Add(
                    PluginDiagnosticSeverity.Error,
                    ExecutionDiagnosticCode.NonZeroExitCode,
                    string.Create(CultureInfo.InvariantCulture, $"The plugin exited with code {outcome.ExitCode}. Its output is not shown."));
                status = PluginExecutionStatus.NonZeroExitCode;
            }

            if (!PluginOutputDecoder.TryDecodeStandardOutput(outcome.StandardOutput, out string? text, out StandardOutputEncoding encoding, out string? decodeError))
            {
                if (status == PluginExecutionStatus.Succeeded)
                {
                    diagnostics.Add(PluginDiagnosticSeverity.Error, ExecutionDiagnosticCode.OutputDecodingFailed, decodeError + " The output is not shown.");
                    status = PluginExecutionStatus.OutputDecodingFailed;
                }

                return Final(status);
            }

            details.StandardOutputEncoding = encoding;
            PluginOutputParseResult parsed = PluginOutputParser.Parse(text, isTruncated: false);
            details.OutputDiagnostics = parsed.Diagnostics;
            if (status == PluginExecutionStatus.Succeeded)
            {
                if (parsed.Status == OutputParseStatus.Failed)
                {
                    diagnostics.Add(PluginDiagnosticSeverity.Error, ExecutionDiagnosticCode.OutputParsingFailed, "The output could not be parsed as a whole (see the output diagnostics). Nothing is shown.");
                    status = PluginExecutionStatus.OutputParsingFailed;
                }
                else if (!cleanupIncomplete)
                {
                    details.Output = parsed.Output;
                }
            }

            return Final(status);

            PluginExecutionResult Final(PluginExecutionStatus reported) => Build(cleanupIncomplete ? PluginExecutionStatus.CleanupIncomplete : reported);
        }

        PluginExecutionStatus ReportTermination(ProcessRunOutcome outcome)
        {
            switch (outcome.TerminationReason)
            {
                case ExecutionTerminationReason.OutputLimit:
                    diagnostics.Add(
                        PluginDiagnosticSeverity.Error,
                        ExecutionDiagnosticCode.OutputLimitExceeded,
                        string.Create(CultureInfo.InvariantCulture, $"The plugin wrote more than {MaxStandardOutputBytes} bytes to standard output, so the run was terminated and its output rejected."));
                    return PluginExecutionStatus.OutputLimitExceeded;

                case ExecutionTerminationReason.CaptureFailure:
                    diagnostics.Add(PluginDiagnosticSeverity.Error, ExecutionDiagnosticCode.OutputCaptureFailed, "Reading the plugin's output failed, so the run was terminated and its output rejected.");
                    return PluginExecutionStatus.OutputCaptureFailed;

                case ExecutionTerminationReason.SupervisionFailure:
                    // Its diagnostic (TW634) was reported when the failure happened.
                    return PluginExecutionStatus.SupervisionFailed;

                case ExecutionTerminationReason.Cancellation:
                    diagnostics.Add(PluginDiagnosticSeverity.Info, ExecutionDiagnosticCode.Cancelled, "The run was cancelled while the plugin was running; it was terminated and its output rejected.");
                    return PluginExecutionStatus.Cancelled;

                case ExecutionTerminationReason.Timeout:
                    diagnostics.Add(
                        PluginDiagnosticSeverity.Error,
                        ExecutionDiagnosticCode.TimedOut,
                        string.Create(CultureInfo.InvariantCulture, $"The plugin did not exit and close its output within {timeout.TotalSeconds:0.###} s, so it was terminated and its output rejected."));
                    return PluginExecutionStatus.TimedOut;

                default:
                    return PluginExecutionStatus.Succeeded;
            }
        }
    }

    private void ValidateTimeout(TimeSpan timeout, string parameter)
    {
        TimeSpan min = _hooks?.MinTimeout ?? PluginExecutionOptions.MinTimeout;
        if (timeout < min || timeout > PluginExecutionOptions.MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(parameter, timeout, "The timeout must be between PluginExecutionOptions.MinTimeout and PluginExecutionOptions.MaxTimeout.");
        }
    }

    private static ConfiguredExecutable? ParseConfigured(string? path, string parameter)
    {
        if (path is null)
        {
            return null;
        }

        return ConfiguredExecutable.TryParse(path, out ConfiguredExecutable? executable)
            ? executable
            : throw new ArgumentException(
                "A configured interpreter must be a fully qualified path on a local drive to an .exe file, without '.' or '..' segments, doubled separators, "
                + "alternate data streams, device or UNC prefixes.",
                parameter);
    }
}
