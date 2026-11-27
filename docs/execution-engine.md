# Execution engine

This document specifies how TrayWisp runs a plugin: interpreter resolution, authorization, process creation, the child environment, output capture and decoding, timeouts, cancellation, process cleanup, results and diagnostics.

**Status (Phase 4).** Everything in this document is **implemented and tested** as a library in `TrayWisp.Runtime/Execution`, except where a section says *planned*. **The engine is not exposed yet**: no CLI command, scheduler, tray or user interface calls it. Those hosts come in Phases 5 to 8 and must use it as described in [Obligations for later phases](#obligations-for-phase-5-and-later-hosts).

**Approval is consent, not sandboxing.** A plugin that passes the trust check runs as an ordinary process with every right of your Windows account. Nothing in this engine restricts what it can read, write, start or send. See [security.md](security.md#trust-model).

Code: `TrayWisp.Core/Execution` (statuses, diagnostics and pure output decoding) and `TrayWisp.Runtime/Execution` (everything that touches processes, the file system, the environment or the clock). The trust check it relies on is specified in [trust-model.md](trust-model.md).

## Overview

```text
PluginExecutionService.RunAsync(root, pluginId, runOptions, token)       one entry point for every host
  1. validate run options (timeout)                                     caller errors throw; nothing else does
  2. language from the identity's file name                             .sh/.bat/.cmd → UnsupportedLanguage
  3. interpreter resolution (configured path, or fixed locations, or PATH), validated, never launched
  4. start information: fixed arguments, no shell, filtered environment, redirected streams
  5. PluginTrustService.CheckTrustForExecution (= the authoritative CheckTrust, under the store lock)
       └─ Approved only → an internal lease keeps the inspected file and its directories open; store lock released
  6. script path = the lease's validated path (on-disk spelling); working directory = its directory
  7. job object created; last cancellation check; Process.Start (direct launch)
       └─ from here, one cleanup boundary: job assignment, stdin closed, streams, capture threads, exit monitoring
  8. stdout and stderr read concurrently as bytes, on dedicated threads, with byte limits
  9. the earliest recorded event decides: exit AND end of both streams, deadline, cancellation, stdout overflow,
     read failure, supervision failure
 10. normal end: tracked processes still in the job are terminated and their exit confirmed;
     anything else: terminate the job and the process tree; bounded cleanup either way
 11. lease released (after the process is gone, or cleanup gave up)
 12. complete stdout only: strict decoding → PluginOutputParser.Parse(text, isTruncated: false)
     incomplete stdout: PluginOutputParser.Parse(…, isTruncated: true), which always fails (TW405)
→ PluginExecutionResult (immutable): status, approval identity, paths, timings, exit code, capture, cleanup,
  output (only when Succeeded), diagnostics TW600–TW699 + trust TW500–TW599 + parser TW400–TW499
```

Nothing is cached between runs: not a trust decision, not an interpreter, not a result.

## Public API

Runtime (`TrayWisp.Runtime.Execution`):

| Member | Role |
| --- | --- |
| `PluginExecutionService(trustService, options)` | The engine. Takes the host's **long-lived** `PluginTrustService` (one per store, never one per run; see [trust-model.md](trust-model.md#unsaved-invalidations)). Thread-safe; concurrent runs are independent. |
| `RunAsync(rootPath, pluginId, runOptions, cancellationToken)` → `Task<PluginExecutionResult>` | **The only way to run a plugin.** Reports every denial, failure and cancellation as a result, including failures to set up or maintain supervision of a started process. Throws for caller errors (`null` arguments, an out-of-range timeout); otherwise only fatal runtime failures (such as out of memory) and programming errors propagate, after TrayWisp has attempted to terminate a started process. |
| `PluginExecutionOptions` | Trusted host configuration: `DefaultTimeout` (30 s) and optional explicit `PowerShellPath`, `PythonPath`, `NodePath`. `MinTimeout` (1 s), `MaxTimeout` (5 min). |
| `PluginRunOptions` | Per run: `Timeout` (`null` = the default). |
| `PluginExecutionResult` | The immutable result (see [Results](#results)). Internal constructor. |
| `PluginInterpreter` | Resolved interpreter: `Path`, `Kind`, `Source`. |
| `PluginProcessCleanup` | `Status`, `RootExitConfirmed`, `JobObjectAssigned`, `RemainingJobProcesses`. |
| `MaxStandardOutputBytes` (1 MiB), `MaxStandardErrorBytes` (64 KiB), `MaxDiagnostics` (64), `CleanupGracePeriod` (5 s) | Limits. |

Core (`TrayWisp.Core.Execution`): `PluginExecutionStatus`, `ExecutionTerminationReason`, `ProcessCleanupStatus`, `InterpreterKind`, `InterpreterSource`, `ExecutionDiagnostic`/`ExecutionDiagnosticCode`, `PluginOutputDecoder` and `StandardOutputEncoding`. Core stays free of file system, process, environment and clock access (`CoreIsolationTests`).

There is **no public unchecked launch path.** The process runner, the interpreter resolver and the authorization lease are internal. The internal runner can only be called with a lease, and a lease can only come from an approved authoritative trust check. No public member accepts or returns a `Process`, `ProcessStartInfo`, process handle, command line, script path or interpreter arguments (`ExecutionApiTests`). There are no overrides, no force-run flags and no automatic approvals.

## Supported languages

| Extension | Language | Execution | Interpreter |
| --- | --- | --- | --- |
| `.ps1` | PowerShell | **Supported** | PowerShell 7 (`pwsh.exe`), else Windows PowerShell 5.1 (`powershell.exe`) |
| `.py` | Python | **Supported** | Python 3 (`python.exe`, `python3.exe`) |
| `.js` | JavaScript | **Supported** | Node.js (`node.exe`) |
| `.sh` | Bash | Deferred | none: no Bash, no WSL |
| `.bat`, `.cmd` | Batch | Deferred | none: no `cmd.exe` |

`PluginLanguages.GetExecutionSupport` returns `PluginExecutionSupport.Supported` or `Deferred`. Discovery is unchanged: deferred plugins are still discovered and can be approved, but `RunAsync` returns `UnsupportedLanguage` before resolving anything, and starts nothing.

The language comes **only** from the extension of the identity's file name, and the validated file's extension must give the same language (`TW621` otherwise). Metadata, shebang lines, plugin directives and output never select an interpreter or add arguments. Language support is not interpreter availability: a supported language whose interpreter is missing gives `InterpreterUnavailable`.

Arbitrary executables, shell command strings and plugin-supplied interpreter switches are not supported. Arguments for the script itself (from callers or output) are out of scope.

## Interpreter resolution

Resolution runs for every run, before the trust check, without ever launching or probing a candidate: no `--version`, no shell, no `where.exe`.

### Order

| Language | Order (first acceptable candidate wins) |
| --- | --- |
| PowerShell | 1. `PowerShellPath`, if configured (and then nothing else). 2. `%ProgramFiles%\PowerShell\7\pwsh.exe` (Program Files from the Windows known folder, not from an environment variable). 3. `pwsh.exe` in each validated PATH directory, in order. 4. `<system directory>\WindowsPowerShell\v1.0\powershell.exe` (from `GetSystemDirectory`). |
| Python | 1. `PythonPath`, if configured. 2. In each validated PATH directory, in order: `python.exe`, then `python3.exe`. |
| Node.js | 1. `NodePath`, if configured. 2. `node.exe` in each validated PATH directory, in order. |

PATH is the `PATH` variable of the TrayWisp process. The search is per directory: the first directory containing an acceptable candidate wins, so a `python3.exe` early in PATH beats a `python.exe` later. The `py.exe` launcher is not used.

### Explicit configuration

A configured path is trusted host configuration, never plugin input. It must be a fully qualified local path to an `.exe` file (syntax checked when the service is created, `ArgumentException` otherwise): no relative, drive-relative, UNC, device-namespace (`\\?\`, `\\.\`), alternate-data-stream, `.`/`..` or doubled-separator paths. It is validated like any candidate on every run. If it is missing or unsafe, the run fails with `InterpreterConfigurationInvalid` (`TW612`) and **no other interpreter is tried**. A configured `powershell.exe` is treated as Windows PowerShell; any other configured PowerShell executable as PowerShell 7.

### PATH entries

Entries are split on `;`. Parsing PATH is syntax only and never touches the file system: an entry is a candidate directory only if it is a fully qualified local directory path under the `PluginRoot` syntax rules. Every candidate directory is then validated as described in [Candidate validation](#candidate-validation) before any file in it is opened. Never searched:

- empty entries (which some programs read as the current directory), `.`, relative and drive-relative entries;
- UNC, device-namespace and alternate-data-stream entries; quoted entries (`"C:\Program Files\x"`) and unexpanded variables (`%SystemRoot%\system32`);
- 8.3 short-name aliases (`C:\PROGRA~1\…`) and directories that are, or lie below, a junction, symbolic link, mount point, offline or remote directory, or a directory that cannot be opened: rejected as a whole by directory validation and counted as *unsafe*;
- entries inside the plugin root;
- duplicates (case, separator style and a trailing separator do not make an entry new);
- entries beyond the first 256 non-empty entries, and entries that do not end within the first 32,767 characters.

**Length boundary.** Only complete entries are searched. When PATH is longer than 32,767 characters, it is cut after the last `;` at or before position 32,767 (the separator that ends the last complete entry may be the first character beyond the limit). An entry that the limit cuts is ignored as a whole and counted as beyond the limit: a cut entry is never shortened into a different directory (`C:\tools\late` cut to `C:\tools\lat`). An entry ending exactly at character 32,767 is complete. The order of the remaining entries is unchanged.

The current directory is never searched, explicitly or implicitly. Skipped entries are summarized in one `TW613` diagnostic with counts, not listed (empty and duplicate entries only when resolution failed). A directory that does not exist is skipped silently.

### Candidate validation

Validation runs in a fixed order, so that nothing unsafe is accessed before it has been checked. For each candidate location (a configured path, a fixed location or a PATH directory):

1. **Syntax**, with no file system access: a fully qualified local path (`PluginRoot` rules); not inside the plugin root (plugins cannot provide their own interpreter).
2. **Drive type**, before anything on the drive is opened: `GetDriveType` must report a local fixed, removable or RAM drive. Network, CD-ROM, unknown and other drive types are rejected; a drive that does not exist means the candidate is absent.
3. **Directory chain**, before the candidate file is opened (TrayWisp's existing `TrustPaths.OpenDirectoryChain`): the drive root, every intermediate directory and the directory itself are opened one at a time, from the root down, without following links, and each must be an ordinary directory: no junction, symbolic link or mount point, not offline or recall-on-access. The directory's final path (`GetFinalPathNameByHandle`) must equal its path, which also rejects 8.3 short-name aliases. A rejected intermediate directory stops the walk, so nothing below it is opened.
4. **Normalization:** `Path.GetFullPath` must leave the directory unchanged. It runs only now, because on Windows it expands 8.3 short names by querying the file system (`GetLongPathNameW`), and before this correction it ran first, so it could query a network drive, a junction's target or an offline directory before any of them had been validated (see [Investigations](#investigations)).
5. **The candidate file**, while the validated directory **stays open** without delete sharing (it cannot be renamed or deleted meanwhile): opened without following links; not a reparse point, which rejects symbolic links and **app execution aliases** (the `WindowsApps` shortcuts of the Microsoft Store, reported as such); not a directory; not offline or recall-on-access; a regular disk file; its final path equal to the expected path, ignoring case (the result uses the on-disk spelling);
6. a **portable executable header** (`MZ`, and `PE\0\0` at the offset stored at `0x3C`), read from the open handle. Scripts, text and DOS programs named `python.exe` are rejected. This was found necessary in testing: asking Windows to start a non-PE `.exe` can block inside process creation (see [Investigations](#investigations)).

A rejected PATH directory (steps 2 to 4) is skipped as a whole and counted as unsafe in `TW613`; nothing in it is opened, so TrayWisp cannot say whether it contained an interpreter. A rejected candidate file (steps 5 and 6) is skipped with `TW611`. A rejected configured path fails with `TW612`, and a rejected fixed location is reported with `TW611`.

**Remaining races.** Validation is a sequence of checks, not file system isolation. Ancestors are not held open: an ancestor renamed or replaced by a link after it was checked is detected by the final-path checks of the directory and of the candidate, not prevented. The candidate file is opened with full sharing, so it can change after its header was read and before (or while) Windows loads it. Between validation and `Process.Start`, the file is not held at all. Resolution narrows these windows; it does not close them.

The checks reuse the trust validation helpers (`WindowsFileApi`, `TrustPaths`) without treating interpreters as plugins: no fingerprint, no approval, no hidden-attribute or naming rules.

Consequences: interpreters reached only through links are not found automatically. This affects, for example, Microsoft Store Python (aliases), Scoop (`current` junctions) and nvm-windows (a symbolic link `C:\Program Files\nodejs`). Configure the real executable path explicitly in those cases.

**Resolution is not authentication.** A validated path names a local file with the expected format; it says nothing about which program or version it is, or who put it there. PATH directories, Program Files and configured paths are part of your trusted environment: anyone who can write to them can choose what runs as an interpreter. File names do not prove versions: a `python.exe` may be Python 2 (it then fails on `-X utf8`) or something else entirely.

## Fixed interpreter arguments

The interpreter is started directly (`UseShellExecute = false`) with its fully qualified validated path and an explicit `ArgumentList`. No command string is built by TrayWisp, and no shell is involved.

| Interpreter | Arguments |
| --- | --- |
| PowerShell 7, Windows PowerShell | `-NoLogo -NoProfile -NonInteractive -File <script>` |
| Python 3 | `-E -u -X utf8 <script>` |
| Node.js | `-- <script>` |

- **PowerShell:** `-File` runs the script under the **effective** execution policy, whatever scope it comes from. TrayWisp never passes `-ExecutionPolicy`, never uses `-Command`, never changes a policy, and **preserves** `PSExecutionPolicyPreference` (the process-scope policy) in the child environment, so it neither tightens nor weakens the policy that applies to TrayWisp itself. A policy that forbids scripts makes the run fail with the policy error on standard error (a nonzero exit). Execution policy is not a security boundary.
- **Python:** `-E` ignores all `PYTHON*` environment variables, `-u` unbuffers the output streams, `-X utf8` makes standard output UTF-8. `-I` and `-P` are deliberately **not** used: the script's directory stays first on `sys.path`, so plugin-local imports work. `-s` is not used either: the user site-packages directory stays enabled, because `pip install --user` is a common way to install plugin dependencies. Site-packages and `.pth` files are part of the trusted interpreter installation.
- **Node.js:** `--` ends Node's options, so the path can only be the script.

The script path is always one separate argument, exactly `ValidatedFilePath`. .NET quotes it with the standard Windows rules, and these interpreters parse their command line with the same rules. Spaces, Unicode and characters such as `& ; ' $ ^ % ( ) ! [ ]` therefore arrive unchanged and are never interpreted (tested for each interpreter with the actual argument vector received by the child process). Windows PowerShell 5.1 accepts `[` and `]` in `-File` paths.

## Working directory

The working directory is the directory of the validated script path, never the host's current directory. Relative paths in a plugin therefore resolve next to the script. The working directory is limited to the Windows maximum path length (about 260 characters) by `CreateProcess`; a deeper plugin fails to start (`TW631`).

## Environment

The child environment is built deliberately from the TrayWisp process's environment, with names compared ordinal case-insensitively, as Windows does:

- **Retained:** everything not listed below, including `PATH`, `PATHEXT`, `TEMP`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, proxy settings and `PSModulePath`. Plugins inherit the same `PATH` that interpreter resolution used, so programs a plugin starts are found the same way they would be in your own shell.
- **Deliberately retained: `PSExecutionPolicyPreference`**, in any casing. It is PowerShell's process-scope execution policy. Process scope takes precedence over the user and machine scopes (only Group Policy scopes override it), so removing it could replace a restrictive process-level policy with a less restrictive one from another scope. An inherited `Restricted` therefore still stops an approved script, even when the user or machine scope would allow it (tested with Windows PowerShell 5.1 on this machine; the PowerShell 7 variant runs where PowerShell 7 is installed).
- **Removed:** variables that inject startup code or change module or import resolution, for all supported interpreters (a plugin may start other interpreters):
  - every name starting with `PYTHON` (`PYTHONPATH`, `PYTHONHOME`, `PYTHONSTARTUP`, `PYTHONUSERBASE`, `PYTHONINSPECT`, …);
  - every name starting with `NODE_` (`NODE_OPTIONS`, which can `--require` code, `NODE_PATH`, `NODE_REPL_EXTERNAL_MODULE`, `NODE_EXTRA_CA_CERTS`, `NODE_TLS_REJECT_UNAUTHORIZED`, …);
  - .NET startup hooks and profilers, which load code into PowerShell: `DOTNET_STARTUP_HOOKS`, `DOTNET_ADDITIONAL_DEPS`, `DOTNET_SHARED_STORE`, `CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER*`, `COR_ENABLE_PROFILING`, `COR_PROFILER*`, `APPDOMAIN_MANAGER_ASM`, `APPDOMAIN_MANAGER_TYPE`.
- **Set:** nothing. Plugin metadata is never put into the environment, and neither plugins nor callers can supply variables.

This is **not a sandbox**. Interpreters have other configuration mechanisms that are not filtered: `powershell.config.json`, the registry, site-packages and `.pth` files, `node_modules` next to the script, and anything the plugin itself reads. Environment values are never recorded in diagnostics.

## Authorization

Every launch is preceded, immediately, by the authoritative trust check of the supplied `PluginTrustService`: the same code as `CheckTrust(root, id)`, which reads the persisted store and hashes the complete current file under the store lock. Only `Approved` launches. Every other status (pending, changed, stale, unavailable, unsafe path, store unavailable, unsupported platform), an unsaved invalidation of the service instance, and cancellation during the check, start nothing (`TrustDenied`, `TW620`, with the trust diagnostics unchanged in `TrustDiagnostics`).

- The launched path is **exactly** the `ValidatedFilePath` of that check: the final path of the file that was hashed, with its on-disk spelling. It is never rebuilt from the root and identity, a discovery descriptor, an earlier inspection or decision, or a caller-supplied path. In a case-sensitive directory, a hidden case variant is never launched (tested).
- Nothing else authorizes: not cached hashes or decisions, timestamps, file existence, metadata, an approval identity supplied by a caller, or earlier output.
- Interpreter resolution and the start information are prepared **before** the check, so the check is as close to the launch as possible. Only the script path, the arguments that contain it and the working directory are added after it.
- The result carries the approval identity of **that** check (`Approval`) and its path (`ValidatedFilePath`). Output is never associated with any other approval.

### Authorization lease

The Phase 3 check closed its file handles before returning. For execution, `PluginTrustService.CheckTrustForExecution` (internal) performs the identical check and, **only for an `Approved` decision**, hands the still-open inspection to an internal `PluginExecutionLease` instead of closing it:

- the plugin file, opened for reading with read-only sharing: other programs cannot open it for writing, delete it, rename it or replace it (`MoveFileEx` onto it fails), but can read it;
- the plugin root and each directory inside it on the file's path, opened without delete sharing: they cannot be renamed or deleted.

The lease does **not** hold the trust store lock: the store is released before `CheckTrustForExecution` returns, so other trust operations and runs proceed. The lease exposes only the decision's approval identity and validated path. It is created once per run, never stored, and disposed on every path (success, failure, timeout, cancellation, start failure), after the process has exited or cleanup has given up. It is held for the **whole run**, because `Process.Start` returning does not mean that the interpreter has already opened, let alone finished reading, the script.

Interpreters open the script with read access and sharing that includes reading, which is compatible with the lease. This is verified by tests with the helper program and with Python. The PowerShell tests that cover it are optional and need an execution policy that allows scripts; on a machine whose policy forbids scripts they skip.

Effects on users and plugins: **while a plugin runs, its file cannot be saved, replaced, renamed or deleted**, and the directories on its path inside the root cannot be renamed or deleted. Editors report the file as in use. A plugin that modifies or deletes its own script fails to do so. Creating, changing and deleting other files in those directories works. Concurrent runs of the same plugin are compatible with each other (preventing overlapping runs is the scheduler's job).

### Remaining time-of-check-to-time-of-use windows

The lease narrows the window between the hash and the interpreter reading the file. It does **not** close every window:

- a process that mapped the file into memory for writing before the check can still change it (sharing modes do not cover existing mapped views);
- modules, packages and files the script loads, and the interpreter binary itself, are not covered by the approval and can change at any time;
- ancestors of the plugin root are not held; only the directories inside the root are;
- a malicious process running as the same user can do anything the user can, including editing the trust store;
- a revocation or change made after the final check does not affect a run that has already been authorized. Stopping such a run is the host's responsibility (see [Obligations](#obligations-for-phase-5-and-later-hosts)).

## Process execution

- `UseShellExecute = false`, the fully qualified interpreter path, `ArgumentList`, `CreateNoWindow = true`, no error dialog, explicit working directory and environment.
- Standard input, output and error are redirected. Standard input is closed immediately after the start: TrayWisp never writes to it, so a plugin that reads input sees end-of-file at once, and no prompt can wait for an answer (PowerShell also runs `-NonInteractive`).
- Standard output and standard error are read **concurrently**, as raw bytes, each on its own dedicated background thread (see [Investigations](#investigations)), so a plugin can never block on one full pipe while TrayWisp waits on the other.
- A Windows job object is created before the start and the process is assigned to it immediately after (see [Windows job objects](#windows-job-objects)).

### Supervision after the start

The moment `Process.Start` returns a process, one cleanup boundary owns it. Everything that follows (the cancellation registration, job assignment, closing standard input, acquiring the two output streams, starting each capture thread, starting exit monitoring, waiting and cleanup) runs inside it. Every way out of it, including a failure at any of these steps, attempts bounded cleanup of the process and of whatever was initialized: TrayWisp never assumes that disposing `Process` terminates the operating system process.

- An **operational failure** of a setup step (an `IOException`, `Win32Exception`, `InvalidOperationException`, `ObjectDisposedException`, `NotSupportedException`, `UnauthorizedAccessException` or `ThreadStartException` from a stream, a thread or exit monitoring) is recorded as a supervision failure. The run is terminated and reported as `SupervisionFailed` (`TW634`, with the exception type and HRESULT, never its message). A failed job assignment is not a setup failure: the run continues with best-effort termination (`TW651`).
- When a stream was never read because setup failed before its capture started, TrayWisp cannot confirm that every process closed it, and says so: the cleanup is then `Incomplete` and the status `CleanupIncomplete` (with `TerminationReason` `SupervisionFailure`). A stream that was acquired but never read is disposed at once; one whose capture thread is still blocked is disposed when the thread ends.
- **Exit monitoring that faults is never taken as proof that the interpreter exited.** It is a supervision failure; root exit is then confirmed by asking the process itself (`HasExited`), polled within the grace period.
- **Cancellation during setup** is recorded at once (the registration is made first) and terminates the process like any other cancellation.
- Exceptions that are not operational failures, such as `OutOfMemoryException` or a programming error, are not converted into results: they propagate out of `RunAsync` after the process has been terminated (best effort, then the job closes with kill-on-close), the capture threads have been asked to stop and the lease has been released.
- `Process.Start` itself: only `Win32Exception` (`CreateProcess` failed, so no process exists) is a result (`ProcessStartFailed`). Its other documented exceptions mean an invalid `ProcessStartInfo`, which TrayWisp builds itself, and propagate as programming errors.
- Every task whose failure matters is observed: exit monitoring through its continuation, stream disposal continuations swallow only the `IOException` of an already broken pipe.

## Timeout and cancellation

| Setting | Value |
| --- | --- |
| Default timeout | 30 s |
| Minimum / maximum | 1 s / 5 min, validated before anything happens |
| Cleanup grace period | 5 s, then at most 1 s for interrupted reads |

- The timeout is measured with the monotonic clock (`TimeProvider.GetTimestamp`) from immediately before process creation. A run is complete only when the process has exited **and** both output streams have reached end-of-file. A process that exits while a descendant keeps a pipe open therefore does not complete; it times out and is terminated. It never makes TrayWisp wait indefinitely.
- Cancellation is checked before resolution, during the PATH search, before and during the trust check (which observes the token itself), and immediately before `Process.Start`. Cancellation before that last check starts nothing and is reported as `Cancelled`, not thrown. A cancellation that arrives while `Process.Start` runs cannot stop the start: the process is then started and immediately terminated as a cancelled run.
- After the start, cancellation (like a timeout, an output overflow or a read failure) triggers termination and cleanup; the process and its streams are never abandoned. Cleanup uses its own bounded period, not the caller's (already cancelled) token.
- **Races are decided by recorded event times.** Each event is recorded with the monotonic timestamp at which TrayWisp observed it, on the thread that observed it, not when the supervising continuation resumes: stdout overflow and read failures on the capture threads, end of each stream on its capture thread, process exit in the exit callback, cancellation in the token callback, supervision failures where they happen. **Completion** is the moment the last of exit, end of stdout and end of stderr was observed. The **deadline** is an event at start + timeout. The earliest event decides, once (`RunArbiter`); later events change nothing.
- **Genuinely simultaneous events** (equal timestamps) are decided by a fixed precedence: stdout overflow, read failure, supervision failure, cancellation, deadline, completion. A tie therefore never publishes output.
- Consequences: a run that completed **before** its deadline succeeds even if the supervisor resumes after the deadline; a run that completed **after** its deadline is a timeout even if it completed before the supervisor resumed; a cancellation observed while the run was incomplete is kept even if the run completes before the supervisor resumes; a run that completed before a cancellation stays a completion. Completion exactly at the deadline is a timeout.
- Observation can only be later than the event itself (a capture thread or the exit callback may be scheduled late), so this errs toward a timeout or a cancellation, never toward publishing output that arrived after the deadline.
- Bounds are not real-time deadlines. Some operating system calls cannot be interrupted: `CreateProcess`, which .NET performs under a process-wide lock, the trust check's file reads, and termination calls. The timeout and grace period bound TrayWisp's waits, not those calls. A pipe read blocked on a writer that cannot be terminated is interrupted with `CancelSynchronousIo`; if even that fails, TrayWisp stops waiting, reports `CleanupIncomplete`, and the reading thread ends when the writer exits, keeping nothing more.

## Output capture

| Stream | Limit | Kept | Over the limit |
| --- | --- | --- | --- |
| stdout | 1 MiB (1,048,576 bytes) | the first 1 MiB | `OutputLimitExceeded`: the run is terminated and nothing is published |
| stderr | 64 KiB (65,536 bytes) | the last 64 KiB (ring buffer) | reading continues to the end; `StandardErrorTruncated`, `TW641` |

- Limits are enforced on **bytes actually read**, never on decoded characters or estimates. `ReadToEnd` is never used.
- While stdout can still be complete, TrayWisp never asks for more than one byte beyond the limit. Output of exactly 1,048,576 bytes is complete; 1,048,577 bytes is an overflow. After an overflow, stdout is still drained (and discarded) until it ends or capture stops, so its end can be confirmed during cleanup.
- Memory is bounded: at most 1 MiB for stdout, 64 KiB for stderr and one 16 KiB read buffer per stream. After a run, the capture is frozen: a read loop that is still running keeps nothing more, and its stream is disposed when it ends.
- Read failures are recorded by exception type and HRESULT only, terminate the run (`OutputCaptureFailed`, `TW642`) and make stdout incomplete.
- Standard error is never mixed into standard output, and stderr alone never fails a run.

`StandardError` in the result is the retained tail made safe for display: decoded as UTF-8 with replacement characters (a leading byte order mark removed), the partial first line dropped when the tail starts mid-stream, CRLF and LF lines, control characters, line separators and lone surrogates replaced with U+FFFD, tabs shown as spaces, directional formatting characters removed, at most the last 100 lines and 500 UTF-16 code units per line (`PluginOutputDecoder.FormatStandardError`).

## Output encoding

Standard output is decoded strictly (`PluginOutputDecoder.TryDecodeStandardOutput`):

| Leading bytes | Encoding |
| --- | --- |
| `EF BB BF` | UTF-8, mark removed |
| `FF FE 00 00` | rejected: UTF-32 is not supported |
| `FF FE` | UTF-16 little endian, mark removed |
| `FE FF` | UTF-16 big endian, mark removed |
| anything else | UTF-8 |

Malformed or truncated UTF-8 (including overlong forms, encoded surrogates and values above U+10FFFF), an odd number of UTF-16 bytes and unpaired UTF-16 surrogates fail the run with `OutputDecodingFailed` (`TW643`). The message gives the byte offset, never the content. Nothing is replaced, so a malformed byte can never turn into a different label, command argument or URL. Code pages are never guessed. A byte order mark that is not at the start is kept as U+FEFF.

What each interpreter writes (when TrayWisp starts it with redirected output and no console window):

- **Python 3:** UTF-8 (`-X utf8`). Lone surrogates written with `surrogateescape` produce invalid UTF-8 and fail decoding.
- **Node.js:** `console.log` and `process.stdout` write UTF-8.
- **PowerShell 7 and Windows PowerShell 5.1:** the console output code page (`[Console]::OutputEncoding`), usually the OEM code page, not UTF-8. ASCII output works. Non-ASCII output does not: verified on Windows PowerShell 5.1 with code page 437, `é` is written as the byte `0x82` (invalid UTF-8, so the run fails with `TW643`) and characters outside the code page become `?`. **A PowerShell plugin that prints non-ASCII text must select UTF-8 itself**, as its first line:

  ```powershell
  [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
  ```

  With this, both PowerShell versions write UTF-8 without a byte order mark (verified on 5.1). `[System.Text.Encoding]::UTF8` may add a byte order mark, which is also accepted. TrayWisp does not add a `-Command` wrapper or bypass the execution policy to force an encoding. Windows PowerShell also reads a `.ps1` file without a byte order mark in the ANSI code page, so non-ASCII literals in such scripts should be saved as UTF-8 with a byte order mark or built from code points (`[char]0xE9`).

These are observations of how the interpreters behave, not guarantees TrayWisp can make for every configuration (for example, Windows' "Use Unicode UTF-8 for worldwide language support" setting changes the code pages).

## Parser integration

The decoded text goes to the unchanged Phase 2 parser, `PluginOutputParser.Parse`, with all of its limits and diagnostics (see [output-protocol.md](output-protocol.md)).

- **Incomplete stdout is never parsed as complete input.** Whenever capture was incomplete (overflow, timeout, cancellation, read failure, termination before end-of-file), the parser is called with `isTruncated: true` and returns its failed result (`TW405`). This does not depend on a final line feed or on how complete the text looks: a menu printed before a timeout, or `param1=doc` cut from `param1=document.txt`, is never published.
- Complete stdout that decoded successfully is parsed for every launched run, so its diagnostics are available. But **only a `Succeeded` run publishes output**: exit code 0, complete capture, successful decoding and a non-failed parse. A nonzero exit, invalid encoding or a fatal parser result (`OutputParsingFailed`, `TW644`) publishes nothing, even if the text parsed.
- Recoverable parser diagnostics (an invalid colour, an unknown parameter, a suppressed action) keep the rest of the output, as specified in Phase 2.
- Empty standard output with exit code 0 is a success with no titles and no items.
- The parser's own limits still apply: 1 MiB of output can be captured, but the parser accepts at most 262,144 characters and 16,384 characters per line.
- **Parsed actions stay inert.** Nothing runs commands, opens URLs or refreshes plugins (tested: only the plugin's own process is started).

## Results

`PluginExecutionResult` is immutable and created only by Runtime:

| Member | Meaning |
| --- | --- |
| `Status`, `Succeeded` | The outcome (below). |
| `PluginId`, `Key`, `Language` | The requested identity, the validated key, the language. |
| `Approval` | The approval identity that authorized the launch, from the immediate check; `null` when not authorized. |
| `ValidatedFilePath` | The file that was validated, hashed and launched. |
| `Interpreter` | Path, `InterpreterKind` and `InterpreterSource`, when resolved. |
| `TrustStatus` | The trust check's status, when it was made. |
| `StartedAtUtc` | When the process started (informational wall-clock time). |
| `Elapsed` | Monotonic time from immediately before process creation to the end of cleanup; zero without a process. |
| `ExitCode` | When the process's exit was confirmed, including after termination (1 when the job was terminated, -1 when only the process was killed). |
| `TerminationReason`, `TimedOut`, `Cancelled` | Why TrayWisp stopped the process, if it did. `None` when the interpreter exited by itself, even if TrayWisp then terminated descendants it left behind. |
| `StandardOutputBytes`, `StandardOutputComplete`, `StandardOutputTruncated`, `StandardOutputEncoding` | Capture of stdout. `StandardOutputBytes` counts bytes read, including bytes drained after an overflow. |
| `StandardErrorBytes`, `StandardErrorTruncated`, `StandardError` | Capture of stderr and its bounded display text. |
| `Cleanup` | See [Process cleanup](#process-cleanup). |
| `Output` | The parsed `PluginOutput`, **only** when `Succeeded` (and then always with `Approval`). |
| `Diagnostics`, `TrustDiagnostics`, `OutputDiagnostics` | `TW600`–`TW699`, `TW500`–`TW599` and `TW400`–`TW499`, each with its own bound. |

### Statuses and precedence

Before a process exists, the steps run in order and the first failure decides: `UnsupportedPlatform`, `UnsupportedLanguage`, `InterpreterConfigurationInvalid` or `InterpreterUnavailable`, `TrustDenied` (including `TW621`), `ProcessStartFailed`; `Cancelled` when the token is cancelled at any of these steps. None of them leaves a process behind (`ProcessStartFailed` means none was created).

After a process has started, exactly one status is reported, in this precedence:

1. `CleanupIncomplete`: termination, or the cleanup of tracked processes after a normal exit, could not be confirmed (processes may still run). No output is published. `TerminationReason` still says why the run was stopped (`None` when the interpreter exited by itself).
2. `OutputLimitExceeded`, `OutputCaptureFailed`, `SupervisionFailed`, `Cancelled`, `TimedOut`: the run was stopped (the reason chosen as described in [Timeout and cancellation](#timeout-and-cancellation)).
3. `NonZeroExitCode`.
4. `OutputDecodingFailed`.
5. `OutputParsingFailed`.
6. `Succeeded`.

A result describes one run. It is **not reusable authorization**: neither a successful result nor its `Approval` permits running the plugin again or acting on its output.

## Process cleanup

A run **owns the lifetime of the processes it tracks**. Whether the interpreter exits by itself or is stopped, TrayWisp does not intentionally leave processes in the run's job object running. Ordinary plugin runs therefore **do not support intentional persistent background processes or services**: a plugin that starts one sees it terminated when the run ends.

**When a run is stopped** (timeout, cancellation, stdout overflow, read failure, supervision failure), TrayWisp:

1. terminates every process in the run's job object (`TerminateJobObject`), if the process was assigned to it;
2. calls `Process.Kill(entireProcessTree: true)`, which also reaches descendants that escaped the job but are still linked to the root;
3. waits, within the 5 s grace period, for the root process to exit, for both output streams to reach end-of-file, and for the job to report no active process;
4. if the streams have not ended, interrupts the reads and waits at most one more second.

**When the interpreter exits by itself** (zero or nonzero exit code) and both streams have ended:

1. if the job is empty, nothing else happens (`Exited`). The console host that Windows creates for a windowless console process is in the job and exits just after the process, so the job is polled for a bounded **settle time of up to 1 s** first;
2. otherwise, the processes still in the job (descendants that no longer hold the output pipes, for example a program launched through the shell, or one that closed its inherited pipes) are **terminated** with the job, and TrayWisp waits, within the grace period, until the job reports **no** active process;
3. confirmed: `DescendantsTerminated`, with a `TW652` warning. The interpreter itself was not terminated: `TerminationReason` stays `None`, and valid complete output **is still published** (a nonzero exit is still `NonZeroExitCode`);
4. not confirmed (the job could not be terminated, still reports processes, or could not be **queried**): `Incomplete`, `TW650`, status `CleanupIncomplete`, and **no output is published**. An unknown process count is never taken as an empty job.

"Confirmed" means the job's count of active processes reached zero. A terminated process leaves that count as it exits; Windows may close its last handles (for example its current directory, the plugin's directory) a moment later, so a directory can briefly stay in use after a confirmed cleanup.

Kill-on-close (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) is set when the job is created and **never cleared**, so closing the job at the end of every run terminates whatever it still holds, including after an incomplete cleanup. There is no API to release tracked processes.

`PluginProcessCleanup` reports what was confirmed, separating the root from its descendants:

| Field | Meaning |
| --- | --- |
| `Status` | `NotStarted`; `Exited` (natural exit, nothing terminated: the job was empty, or there was no job); `DescendantsTerminated` (natural exit, remaining tracked descendants terminated and the empty job confirmed); `Terminated` (the run was stopped: root exit, both ends of stream and, with a job, an empty job confirmed); `Incomplete` (something not confirmed: the status is then `CleanupIncomplete`, `TW650`). |
| `RootExitConfirmed` | The interpreter process itself was seen to exit (by the exit monitor, or by asking the process when monitoring was unavailable or failed). Says nothing about its descendants. |
| `JobObjectAssigned` | Descendants created after the assignment were tracked and terminated with the run. |
| `RemainingJobProcesses` | Processes still in the job at the end: 0 for `Exited`, `DescendantsTerminated` and `Terminated` with a job; `null` without a job or when the job could not be queried. |

The cases are distinct: a **natural exit** (`Exited`, `TerminationReason` `None`); the **cleanup of remaining tracked descendants** after a natural exit (`DescendantsTerminated`, `TerminationReason` `None`, `TW652`); an **abnormal termination** of the interpreter (`Terminated`, with the reason); **incomplete cleanup** (`Incomplete`, `TW650`); and **untracked descendants** that cannot be confirmed: without a job object (`JobObjectAssigned` `false`, `RemainingJobProcesses` `null`), descendants that released the output pipes are invisible to TrayWisp. Nothing is claimed about them: `Exited` then means only that the root exited and both streams ended.

Confirmed end-of-file on both streams means that no process still holds the output pipes. Root exit alone is never reported as tree termination.

TrayWisp never terminates processes by name and never scans the machine for processes to kill.

Limitations:

- **Job assignment race.** The process is assigned to its job after `Process.Start` returns. Processes the plugin starts **before** the assignment (a window of microseconds to milliseconds, while the interpreter is still starting) are not in the job: they are neither tracked nor terminated after a normal exit. `Process.Kill(entireProcessTree: true)` finds them on a stopped run only while they are still linked to the root by parent process ID. A suspended native launcher would close this window; it is deliberately not implemented (see below).
- Processes started outside the plugin's process tree (Task Scheduler, services, COM or WMI, hand-off to an already running application) are never in the job or the tree.
- Elevated processes or processes of another user may not be terminable; the cleanup is then reported `Incomplete`.
- Termination is abrupt: the plugin cannot clean up.
- If TrayWisp itself exits without closing the job (for example a crash), Windows terminates the job's processes when the handle is closed (kill-on-close).
- **Capture cancellation.** A pipe read blocked on a writer is interrupted with `CancelSynchronousIo`, repeated for at most one second because a read can start just after a request. A read that still cannot be interrupted keeps its dedicated thread until the last writer exits; the run does not wait for it, reports `CleanupIncomplete` if the stream end was not confirmed, and the thread keeps nothing after the capture is frozen.

### Windows job objects

The investigation and its outcome:

- **Implemented:** one job object per run, created before the start, with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and no other limit. Breakaway is not allowed (no `JOB_OBJECT_LIMIT_BREAKAWAY_OK`), so descendants of an assigned process cannot leave the job. Descendants inherit the job, whether or not their parent is still running, which closes the "orphaned descendant" gap of `Process.Kill`. The job count of active processes gives the cleanup confirmation above. It is six `kernel32` functions in `WindowsJobObject`, used only by the internal runner; there is no second launch path.
- **Nested jobs:** Windows 8 and later support them, so the assignment also works when TrayWisp itself runs in a job (for example under a CI runner or a terminal that uses jobs). Assignment succeeded in every local test run; the tests assert it, so CI checks it on its runners. If the assignment fails anyway (tested with an injected failure), the run continues, `JobObjectUnavailable` (`TW651`) is reported, `JobObjectAssigned` is `false`, and only best-effort tree termination applies. A job is never claimed when it is not active.
- **The post-start race:** the assignment happens after `Process.Start` returns, so the job is **not complete containment**. Closing that gap requires creating the process suspended or with a job attribute (`PROC_THREAD_ATTRIBUTE_JOB_LIST`), which `System.Diagnostics.Process` does not offer. It would mean a native launcher reimplementing pipe creation, handle inheritance and process creation. That is deferred: it is a substantial and fragile subsystem for a window that interpreters, which take tens of milliseconds to start, rarely give a script.
- **Not a sandbox:** a job restricts no file, registry or network access. TrayWisp sets no CPU, memory or process-count limits.

## Investigations

Findings from implementing and testing this phase, and what they changed:

- **Cancelling reads of redirected pipes.** `Process` creates anonymous pipes, which are synchronous handles, so .NET's asynchronous reads on them run blocking reads on thread-pool threads, and cancelling the token does not reliably interrupt a read that is already blocked. TrayWisp therefore reads each stream on its own background thread with synchronous reads, and interrupts a blocked read with `CancelSynchronousIo` on that thread, repeating the request until the loop ends or a bound elapses (a read can start just after a request). A test blocks a real anonymous pipe read, through the same `FileStream` type `Process` uses, and checks that the stop request ends it. This also keeps blocked reads off the thread pool.
- **Timer-thread continuations.** .NET runs one timer callback on its timer thread, and `await` continuations run synchronously on the thread that completes the awaited task. A run resumed by its timeout would therefore continue, with the caller's continuation, on the timer thread, and delay every timer in the process. Testing showed this stalling all runs for minutes. Every wait that a timer can complete now resumes with `ConfigureAwaitOptions.ForceYielding`, and `RunAsync` leaves the caller's thread and synchronization context before its synchronous work (resolution, the trust check).
- **Non-programs as interpreters.** Starting a configured `python.exe` that was a text file blocked inside process creation, which .NET performs under a process-wide lock, so every other process start in TrayWisp waited too. Candidates are therefore required to have a portable executable header before anything is started. Other causes of a blocking `CreateProcess` cannot be excluded.
- **Windows PowerShell output encoding**, see [Output encoding](#output-encoding).
- **Short-name expansion before drive validation.** On Windows, `Path.GetFullPath` calls `GetFullPathNameW` (string processing only) and then, when the result contains `~`, `GetLongPathNameW`, which looks up each component on the file system and follows links. Interpreter resolution used to call it (through `TrustPaths.TryParseRoot`) before the drive type and the directory chain were validated, so a PATH entry such as `Z:\TOOLS~1` on a mapped network drive, or a short name below a junction, could make TrayWisp query a remote or unvalidated location. Resolution now checks syntax only, then the drive type, then the directory chain without following links, and only then `GetFullPath`. The same helper is still used for plugin roots (Phase 3), which a caller supplies explicitly; that path is unchanged.
- **Windows PowerShell wraps error records** at the console width, even with redirected standard error, so a message can be split across lines in `StandardError`.

## Diagnostics

Execution diagnostics have their own range and bound (64 per result, the last slot `TW699` when more were produced), independent of the trust (64) and parser (256) diagnostics, which are passed on unchanged.

| Code | Name | Severity | When |
| --- | --- | --- | --- |
| TW600 | UnsupportedPlatform | Error | not Windows |
| TW601 | UnsupportedLanguage | Error | `.sh`, `.bat`, `.cmd` |
| TW610 | InterpreterNotFound | Error | no acceptable candidate |
| TW611 | InterpreterCandidateRejected | Warning | a candidate file was skipped (app execution alias, link, not a program, …), or a fixed location's directory was rejected |
| TW612 | ConfiguredInterpreterInvalid | Error | a configured path is missing or unsafe; no fallback |
| TW613 | PathEntriesIgnored | Info | PATH entries were not searched (summary with counts: empty, not fully qualified, unsafe directories, inside the root, duplicates, beyond the limits) |
| TW620 | TrustDenied | Error | the trust check did not approve (see `TrustDiagnostics`) |
| TW621 | LanguageMismatch | Error | the validated file's language differs from the identity's |
| TW630 | Cancelled | Info | cancelled before or during the run |
| TW631 | ProcessStartFailed | Error | `CreateProcess` failed (Windows error code and system message) |
| TW632 | NonZeroExitCode | Error | the exit code was not 0 |
| TW633 | TimedOut | Error, plus Info | the deadline was the earliest event; the Info entry says whether the process was still running or a stream was still open |
| TW634 | SupervisionFailed | Error | setting up or maintaining supervision after the start failed (streams, capture threads, exit monitoring); exception type and HRESULT only |
| TW640 | OutputLimitExceeded | Error | more than 1 MiB of stdout |
| TW641 | StandardErrorTruncated | Warning | more than 64 KiB of stderr |
| TW642 | OutputCaptureFailed | Error | a read failed |
| TW643 | OutputDecodingFailed | Error | stdout is not valid in a supported encoding (byte offset only) |
| TW644 | OutputParsingFailed | Error | the parser failed as a whole (see `OutputDiagnostics`) |
| TW650 | CleanupIncomplete | Error | termination, or the cleanup of tracked processes after a normal exit, could not be confirmed (including a job that could not be queried or terminated) |
| TW651 | JobObjectUnavailable | Warning | no job object for this run |
| TW652 | DescendantsTerminated | Warning | tracked processes were still in the job after the interpreter exited by itself; TrayWisp terminated them and confirmed it (the interpreter was not terminated). Formerly *DescendantsRemaining*, which reported them as left running; the number is unchanged |
| TW699 | DiagnosticLimitReached | Warning | later diagnostics were dropped |

## Privacy

- Diagnostics contain paths (quoted, escaped and cut to 260 characters with `PluginDiagnosticText`), counts, exit codes and Windows error messages for error codes. They never contain environment variable values, credentials, script content, standard output, store content or the messages of arbitrary exceptions (read failures are reported by exception type and HRESULT).
- `StandardError` is bounded and sanitized, but it is whatever the plugin printed and **may contain sensitive information**. Treat it like the plugin's own output.
- Nothing is logged, persisted or sent anywhere: results live in memory and are returned to the caller. Execution history and log storage belong to Phase 6.

## Tests

`TrayWisp.Runtime.Tests/Execution` and `TrayWisp.Core.Tests/Execution`:

- A test-only helper program (`tests/TrayWisp.TestHelper`, not shipped, no dependencies) is configured explicitly as the interpreter of approved plugins whose files contain instructions for it. The engine launches it exactly as it launches a real interpreter, and it reports the argument vector, working directory, standard input and selected environment variables it actually received, writes exact byte sequences to either stream, sleeps, exits with codes, and starts descendants that hold or do not hold the output pipes. It reads the script; it never executes anything from it.
- Denied runs are verified through a process-start counter in the internal test hooks and a marker file the plugin would create, not only through the returned status.
- Every helper process that records its process ID is checked to have exited at the end of its test, and killed if not (the test then fails), so no controlled process outlives the suite.
- Unit tests cover capture limits with controlled streams (exact limits, the look-ahead, ring wrap-around, multibyte sequences split across reads, read failures, freeze, stop), decoding (BOMs, malformed and truncated sequences, UTF-16, UTF-32), standard error formatting, interpreter resolution with fake executables (minimal PE headers, never launched), and the public API surface.
- Deterministic seams (internal test hooks, never set in production) inject failures and delays: a failure before each supervision setup step, a faulted exit monitor, failed job assignment, query and termination, a delayed supervision continuation (`BeforeDecision`), and, for resolution, a recorded access order and a replaced drive type. Arbitration is unit-tested with a manual monotonic clock (`RunArbiterTests`).
- Tests use isolated temporary directories and trust stores, never the real per-user store, and these hooks instead of sleeps, except for short bounded polls for files a helper creates.

### Real-interpreter tests

`RealInterpreterTests` run the interpreters installed on the machine through the production resolution: Windows PowerShell 5.1, PowerShell 7, Python 3 and Node.js, including the example plugins. Nothing is installed and nothing uses the network.

- **Optional locally.** A test skips, with the reason, when its interpreter is missing or the effective execution policy forbids scripts. A skipped test verifies nothing and is never counted as passed.
- **Required in a dedicated environment.** `TRAYWISP_REQUIRED_INTERPRETERS` (a list of `WindowsPowerShell`, `PowerShell`, `Python`, `Node`, separated by `,` or `;`, or `all`) makes those interpreters required: a missing or blocked required interpreter **fails** its tests instead of skipping them.
- **Execution policy.** No test changes a persistent (user or machine) policy, and the production engine never sets one. A controlled test invocation may allow locally created test scripts for its own process only, by setting `PSExecutionPolicyPreference=RemoteSigned` in the test process environment; TrayWisp passes it on like every other variable. `RemoteSigned`, not `Bypass`, is used.
- **Policy denial, deterministic.** `TheExecutionPolicyIsNeverBypassed` (Windows PowerShell 5.1) and `TheExecutionPolicyIsNeverBypassedByPowerShell7` run the same approved script twice: with the unchanged environment (the control, which shows what the other scopes allow and, when it runs, that the effective policy is the inherited one), and with `PSExecutionPolicyPreference=Restricted` set, in unusual casing, in the child's base environment. The second run must fail with the policy error and must not have run the script.
- **Policy script independent of module autoloading.** The script sets `$ErrorActionPreference = 'Stop'` and imports `Microsoft.PowerShell.Security` explicitly from the interpreter's own `$PSHOME` before calling `Get-ExecutionPolicy`, and the control run must print exactly one title and nothing on standard error. In CI the test step runs under PowerShell 7, whose `PSModulePath` (PowerShell 7's module directories first) reaches Windows PowerShell 5.1 through the intermediate test processes and TrayWisp unchanged. Windows PowerShell 5.1 then resolves `Microsoft.PowerShell.Security` to PowerShell 7's copy, which it cannot load (its type data conflicts with the built-in type data), so autoloading `Get-ExecutionPolicy` fails with `CouldNotAutoloadMatchingModule` while the script still exits with 0. Microsoft documents this for Windows PowerShell started through an intermediate process (`about_PSModulePath`).

**CI.** The existing Windows job (`windows-latest`, Windows Server 2025) runs the whole suite with `TRAYWISP_REQUIRED_INTERPRETERS=all` and `PSExecutionPolicyPreference=RemoteSigned` on the test step only. The runner image provides Windows PowerShell 5.1, PowerShell 7.6, Python 3.12 and Node.js 22, and sets its LocalMachine policy to `Unrestricted`, so the restrictive-policy test runs where another scope does allow scripts. No setup actions, workflows or dependencies were added. Until that job has run, its interpreter results are **unverified**: the image's install locations (for example whether its Python directory is free of junctions) were not inspected.

**Verification on the development machine (Phase 4 corrections).** Windows PowerShell 5.1 is installed, every execution policy scope is `Undefined` (effectively `Restricted`), and neither PowerShell 7 nor Node.js is installed. With the plain test command, the PowerShell, PowerShell 7 and Node.js tests skip. With a process-scoped `PSExecutionPolicyPreference=RemoteSigned` for the test invocation only, Windows PowerShell 5.1 and Python 3 tests **ran and passed**, including the restrictive-policy test; the PowerShell 7 and Node.js tests skipped (not installed) and did not verify anything.

## Obligations for Phase 5 and later hosts

*Planned, not implemented.*

- Run plugins **only** through `PluginExecutionService.RunAsync`, with one long-lived `PluginTrustService` per store, shared with every other trust operation of the host.
- **Scheduler (Phase 5):** prevent overlapping runs of the same plugin and enforce a global concurrency limit; the engine does neither. Do not retry automatically in a way that bypasses the trust check (every run checks again anyway).
- **Revocation and changes:** cancel in-flight runs (through the token) when a plugin is revoked, disabled, removed or changed. A run already authorized is not stopped by the trust store changing.
- **Output-driven actions (Phase 7):** when an action is activated, call `PluginTrustService.CheckTrust(result.Approval)` and act only on `Approved`; a `StaleApproval` or `ContentChanged` disables it. Never treat a result, its output or its approval identity as permission. Launch commands with the same direct, explicit-argument approach.
- **Display:** show `StandardError` and diagnostics escaped, as plugin-controlled text; show `CleanupIncomplete` to the user, because processes may still be running.
- **CLI and UI (Phases 6 and 8):** expose runs only through this engine, show the resolved interpreter path and its source, and explain that approval is consent, not sandboxing, and that PowerShell plugins need to select UTF-8 for non-ASCII output.
