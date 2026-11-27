# Security model

TrayWisp's core job is to run scripts on your behalf, so its security model has to be stated plainly. This document describes the trust model, the discovery, output parsing, approval and execution safeguards that are **implemented**, and the safeguards that are still **planned**. **As of Phase 4, TrayWisp discovers plugin files, reads their metadata, parses plugin output text, records explicit approvals with an authoritative trust check, and has an execution engine that runs an approved plugin under that check. The engine is not exposed through the CLI or the desktop app yet, and TrayWisp never acts on plugin output.** [Plugin discovery](#plugin-discovery-implemented), [Output parsing](#output-parsing-implemented), [Plugin approval](#plugin-approval-implemented) and [Plugin execution](#plugin-execution-implemented) describe implemented controls. Everything under [Planned safeguards](#planned-safeguards) is not implemented yet. They are design commitments that later phases (see [roadmap.md](roadmap.md)) must meet or explicitly revise.

To report a vulnerability, see [SECURITY.md](../SECURITY.md).

## Trust model

- **A plugin is arbitrary code.** A plugin script runs as a normal process under your Windows user account, with the same access you have: your files, your network, your credentials stored for your account, and the ability to start other programs. Running a plugin through TrayWisp is equivalent to running the script yourself.
- **Approval is consent, not containment.** TrayWisp does not run a plugin until you approve it, and checks the approval again immediately before every run. Approval only records that you chose to trust that script. It does not restrict what the script can do. **User approval is not sandboxing.**
- **TrayWisp does not sandbox plugins.** See [Process isolation limitations](#process-isolation-limitations).
- **Plugin output is untrusted data.** Even an approved plugin may print content it fetched from elsewhere, such as web pages or API responses. TrayWisp will treat output as data to display, never as code to evaluate, except for the explicit action features described below, which run with the plugin's own trust.

Only install plugins you have read or whose author you trust, just as you would for any script.

## What TrayWisp aims to guarantee, and what it does not

TrayWisp aims to:

- Run a plugin only after you have approved it, with the approval check applied on every execution path.
- Avoid adding shell interpretation of its own to plugin launches and output-driven actions.
- Bound how long each plugin run lasts and how much output TrayWisp captures from it.
- Attempt to terminate a plugin's processes when a run times out or is cancelled.

TrayWisp does **not**:

- Sandbox plugins or restrict their access to files, the registry, the network or other processes.
- Guarantee that the content you approved is exactly the content that runs (see [Plugin approval](#plugin-approval-implemented)).
- Guarantee that every process a plugin starts is terminated (see [Process-tree termination](#process-tree-termination)).
- Undo anything a plugin has already done.

## Plugin discovery (implemented)

Discovery (Phase 1, specified in [plugin-format.md](plugin-format.md#discovery)) treats the plugin directory and everything in it as untrusted input:

- **No execution.** Discovery never starts a process or interpreter, never evaluates script content, and never resolves interpreters. A discovered plugin is neither approved nor runnable.
- **Read-only.** Candidate files are opened for reading only, with sharing that does not block other programs. Nothing is created, written, deleted, renamed, locked exclusively or re-permissioned. A test checks hashes, timestamps and attributes before and after discovery.
- **Bounded.** At most 64 KiB + 1 byte is read per file. Directory depth (8), entries enumerated (10,000, one budget for the whole scan, charged at listing time) and candidates (500) are capped by default. Metadata parsing caps tag count (128), value length (1,024) and tag-name length (64), and uses neither regular expressions nor an XML parser.
- **Inert metadata.** Metadata is read only from comment-shaped lines, in the comment syntax of the plugin's language, so ordinary code lines cannot supply it. The parser is line-based, not a tokenizer: a comment-shaped line inside a multi-line string literal can supply tags. Metadata is therefore untrusted descriptive text. It never establishes identity or approval. Values are opaque text: no entity expansion, no DTDs, no URL fetching or validation, no control characters.
- **Safe diagnostics.** Messages never repeat metadata values. File names, paths and system messages in them are length-limited, invisible or reordering characters are escaped, and literal backslashes are doubled so they cannot imitate escapes. `PluginDiagnostic.ToString()` escapes and bounds the raw `Path` the same way.
- **No link following.** Symbolic links, junctions and other reparse points are skipped, and a root that is itself a reparse point is refused. Each directory and file is re-checked immediately before it is listed or opened, and is skipped if it has turned into a link, a remote-storage entry or a different kind of entry. It is checked again after it is listed or read, and the result is discarded on the same conditions.
- **No remote fetches.** Entries marked offline or recall-on-access (cloud placeholders, hierarchical storage) are not read, and a root marked this way is not listed, because doing so could trigger a download.
- **No ambiguous identities.** Plugin files or directories whose names differ only by case are all excluded before anything is read. A locked or broken twin therefore cannot let the other one be selected. A directory whose listing was cut off by the entry budget contributes nothing, because an unseen entry could be a case variant.
- **No spoofed names.** File and directory names containing Unicode bidirectional controls, which can disguise an extension, are rejected.
- **Failure isolation.** Unreadable, locked, vanished or malformed files produce diagnostics and do not stop discovery of other plugins. A scan in which a plugin's state could not be determined is marked incomplete.
- **No arbitrary executables.** Only `.ps1`, `.py`, `.js`, `.sh`, `.bat` and `.cmd` files are candidates. `.exe` and other binaries are ignored.

Limitations. These checks are **not a complete security boundary**:

- **Races.** The pre-use and post-use re-checks narrow, but do not eliminate, link-swap races (TOCTOU). They are path-based: an entry swapped for a link just after the pre-use check and back before the post-use check, or a parent directory replaced by a junction during the scan, can make discovery read up to 64 KiB of a file outside the root. Closing this fully needs handle-relative opens, which .NET does not provide. Nothing is executed or written as a result. See [plugin-format.md](plugin-format.md#security-limitations).
- **Ancestors and hard links.** Only the root itself is checked for being a reparse point, not its parent directories. Hard links to files elsewhere on the volume cannot be distinguished from ordinary files.
- **Lookalike names.** Identity comparison approximates NTFS case-insensitivity. Homoglyphs and zero-width characters in file names, and format characters in metadata values, are not filtered. Display surfaces must account for them.
- **Incomplete scans.** A plugin missing from an incomplete scan may still exist. Later phases must not treat that absence as removal, for example by revoking approval or dropping a schedule.

## Output parsing (implemented)

The output parser (Phase 2, specified in [output-protocol.md](output-protocol.md)) treats plugin output as untrusted data, even from an approved plugin:

- **Inert.** Parsing never evaluates, expands or executes anything, and never touches the file system, network, clock or environment. `CoreIsolationTests` checks the compiled Core assembly for such references.
- **Actions are requests, not permissions.** A command is an executable plus an explicit argument list, never a shell command line. `shell` and `bash` are just names for the executable. URLs are limited to `http` and `https`. `refresh` only ever means the producing plugin. Nothing is resolved, checked for existence, opened or launched. Every action must pass the [trust checks](#trust-checks-on-every-execution-path) and execution safeguards when it is activated (planned, Phase 7).
- **Fail closed.** Malformed syntax, duplicated or conflicting action parameters, unknown parameters on action lines and unsupported behavioural parameters (`alternate`, `key`, `webview`, …) suppress the whole action, including its refresh request. A parsed action is never partial. An invalid `disabled` value disables the item. A disabled item or an item with a submenu exposes no action.
- **Bounded.** Input length, lines, line length, titles, menu nodes, depth, parameters, value length, arguments and diagnostics are capped. Output that exceeds a global cap, including a single over-long physical line, exposes no menu and no action. Problems confined to one item or parameter only drop that item, ignore those parameters or suppress that action. Processing is linear and uses no regular expressions.
- **Incomplete output exposes nothing.** A cut can leave syntactically valid but different values (`param1=document.txt` cut to `param1=doc`), and the parser cannot detect this. A caller that passes `isTruncated: true` gets a failed result (`TW405`) with no menu and no action. The parser never infers truncation, for example from a missing final line feed, so the execution engine reports every incomplete capture and never passes truncated stdout as complete input.
- **Streaming is rejected.** A SwiftBar `~~~` streaming delimiter fails the whole parse (`TW416`), so actions from earlier frames can never be shown alongside later ones.
- **Display text is sanitized.** Control characters (including terminal escape sequences and a lone CR), line separators and invalid UTF-16 become U+FFFD, and directional formatting characters are removed. Action values containing them are rejected. Labels and tooltips made only of invisible characters (white space, format characters, default-ignorable code points such as Hangul fillers and variation selectors, and U+2800) are dropped. This is a best-effort safeguard, not a guarantee that every font renders every accepted label visibly.
- **Strict URL syntax.** URLs must be well formed (`Uri.IsWellFormedOriginalString`), use only characters RFC 3986 allows, have valid two-digit percent escapes, and have no user information or rewritten hosts. Raw `|`, spaces, backslashes and control characters are rejected. Percent escapes, including escaped control characters such as `%0A`, are kept exactly as written and never decoded by the parser; future code must hand off `Uri.OriginalString`, never `Uri.ToString()`.
- **Parsing is not authorization.** A complete parse only says the text is syntactically acceptable. It never authorizes running a command, opening a URL or refreshing a plugin.
- **Safe diagnostics.** Messages never repeat labels, URLs, executables or argument values, so logging them does not leak plugin output.

Limitations: homoglyphs, zero-width characters inside visible text and lookalike domains are not detected; display surfaces should show the exact URL or command behind an action. If a plugin passes untrusted data (for example an API response) into its own action parameters, the parser cannot tell.

## Plugin approval (implemented)

Approval (Phase 3, specified in [trust-model.md](trust-model.md)) records explicit consent and provides the one trust check that execution will use:

- **Pending by default.** Newly discovered plugins have no approval and are `PendingApproval`. Discovery, metadata, successful parsing, file existence, timestamps and earlier approvals never authorize anything. There is no implicit, wildcard, directory-wide or author-based approval.
- **Bound to root, identity and content.** An approval is bound to the normalized **plugin root**, the plugin's **identity** (its root-relative path, see [plugin-format.md](plugin-format.md#identity)) and the SHA-256 of the **entire raw file**. The identity alone is not enough: the same relative path under another root is a different plugin.
- **Explicit approval of reviewed content.** `Approve` requires the fingerprint the user reviewed. Under the store lock it validates the path again and rehashes the file immediately before saving, and refuses if the content differs. A caller-supplied hash is never taken as proof of the file's content.
- **One authoritative check.** `PluginTrustService.CheckTrust` reads the persisted approval and hashes the complete current file under the store lock every time. Only `Approved` permits execution. Nothing is cached. An approved decision also returns `ValidatedFilePath`, the exact file (with its on-disk spelling) that was validated and hashed; execution must launch that path from the immediate check, never a path rebuilt from the case-insensitive identity.
- **Invalidation.** When a check, or an approval request, observes different content, the obsolete approval is removed persistently and is not restored if the old bytes return. Explicit reapproval is required. A rejected approval request does not preserve an obsolete approval. Missing, locked, unreadable, oversized or unsafe files deny the plugin at that moment but are not treated as a change, so their approval is kept. Absence from discovery results never removes an approval.
- **Unsaved invalidations.** If the removal cannot be written, the service instance that observed the change keeps denying that approval, even if the old bytes return, and retries the removal. This memory is bounded and only denies, but it exists only in that instance: other instances, other processes and a restarted TrayWisp see the old approval in the store. Hosts must therefore use one long-lived trust service per store. See [trust-model.md](trust-model.md#unsaved-invalidations).
- **Stale output.** Each approval has a random generation. Revocation followed by reapproval, even of the same bytes, creates a new one, and `CheckTrust(approvalIdentity)` rejects identities that are no longer current. Output-driven actions will be checked this way.
- **Conservative paths.** Only local drive paths are supported. Relative, drive-relative, UNC, device-namespace and alternate-data-stream paths are rejected, as are reparse points, offline or cloud placeholders, hidden or dot-prefixed entries, case collisions, case-sensitive ancestors of the root, short-name and `subst` aliases, and network drives. Each component is opened without following links, checked through its handle and held open, and the file's final path is verified with `GetFinalPathNameByHandle`.
- **Bounded, strict, atomic store.** Approvals live in a versioned JSON file under `%LOCALAPPDATA%\TrayWisp\trust`, read strictly and all or nothing (unknown fields, duplicates and invalid values reject the whole store, which is then never partially trusted or silently replaced), synchronized between processes with a lock file, and replaced atomically through a flushed temporary file in the same directory. The store directory is held open during each operation; the lock, store and temporary files must be single-link regular files at their configured paths; case-sensitive store directories are rejected; and the directory's final path is checked again before publication. Roots and identities that are not well-formed UTF-16 are rejected, so every stored string reads back exactly.
- **Bounded.** 16 MiB per plugin file (never partially hashed), 4 MiB and 4,096 records per store, 10,000 directory entries per validation, 64 diagnostics per result, a bounded lock wait.

Limitations (details in [trust-model.md](trust-model.md#security-guarantees-and-limitations)):

- **Time of check to time of use (TOCTOU).** TrayWisp checks the hash, but the interpreter opens and reads the file by path on its own. During a trust operation, TrayWisp holds the file and its directories open so they cannot be changed or renamed. Public trust operations release them when they return; the execution engine re-checks immediately before every launch and keeps them held until the run ends (see [Authorization lease and remaining races](#authorization-lease-and-remaining-races)). This narrows the window but does **not** eliminate it. The validated path only guarantees that the launched path names the file that was checked. Read an approval as "this content was trusted when it was checked", not as a guarantee that exactly this content runs.
- **Memory-mapped writers.** A process that mapped the plugin file for writing before the check can change it despite the sharing mode, also during hashing.
- **Unsaved invalidations across instances.** An invalidation that could not be written is unknown to other service instances, other processes and later sessions. If the old bytes are restored before it is saved, they approve the old approval.
- **Same-user tampering.** The approval store is an ordinary file in your profile. Any program running as you can edit it. It is not signed or encrypted. The store directory is held during an operation, but its ancestors are not, and a short window remains between the last directory check and the rename that publishes an update. Approval detects unnoticed changes to plugins. It does not defend against malware already running as you.
- **Hard links.** Hard-linked plugin files are accepted (changes through another link are still detected by the hash). Hard-linked store and lock files are rejected.
- **Scope of the hash.** It covers only the plugin file. It does not cover modules the script imports, files it reads, the interpreter, or anything the script downloads. An approved script can still change its behaviour through any of those.
- **Not network isolation.** Only what Windows reports as remote (UNC paths, network drives) is rejected. A local-looking volume backed by network storage is not detected.
- In practice, anyone who can write to your plugin directory can usually run code as you anyway, so these risks matter most when that directory's permissions are weaker than expected (see [Plugin directory](#plugin-directory)).

## Plugin execution (implemented)

The execution engine (Phase 4, specified in [execution-engine.md](execution-engine.md)) is the only code that starts plugins. It is a library: no CLI command, scheduler or tray calls it yet.

### Trust checks on every execution path

- `PluginExecutionService.RunAsync` is the single public way to run a plugin. It calls the authoritative trust check (`CheckTrust` semantics, through the internal `CheckTrustForExecution`) **immediately before every launch** and starts a process only when it returns `Approved`. Pending, changed, stale, unavailable, unsafe, store-unavailable and unsupported-platform outcomes, and the service's unsaved invalidations, start nothing. Tests observe process creation to verify this, not only the returned status.
- It launches exactly the `ValidatedFilePath` of that check, never a path rebuilt from the identity, a discovery result or a caller, and associates the run's output with that check's approval identity.
- The internal process runner requires the lease that only an approved check produces. No public member exposes processes, start information, command lines or interpreter arguments. There are no overrides or force-run flags.
- The engine uses the host's long-lived `PluginTrustService`, so unsaved invalidations keep being enforced.

*Planned:* scheduled runs, hot reload, manual runs from the tray or management interface and CLI runs must all go through this engine (Phases 5 to 8). Output-driven actions must be checked with `CheckTrust(approvalIdentity)` when activated. If an override is ever added, for example a one-off CLI run of an unapproved plugin, it must be an explicit per-invocation user action and be recorded in diagnostics. Disabling or revoking a plugin must cancel its in-flight runs; the engine supports cancellation, the host must request it.

### Process launch: explicit arguments, not shell command strings

These are two different things, and TrayWisp only does the first:

- **Launching an interpreter directly with explicit arguments.** TrayWisp starts the interpreter executable, such as `pwsh.exe` or `python.exe`, by its fully qualified path, and passes fixed options and the script path as separate arguments (`ProcessStartInfo.ArgumentList`, `UseShellExecute = false`). There is no `cmd.exe`, no PowerShell `-Command` string, no `-ExecutionPolicy Bypass` and no string concatenation. Spaces, quotes and characters such as `&` or `|` in paths are therefore not interpreted by a shell (tested with the argument vector the child actually receives).
- **Evaluating shell command strings.** Passing a constructed string to `cmd.exe /c`, `powershell -Command` or `sh -c` makes the shell parse it, so any untrusted fragment becomes code. TrayWisp does not do this for plugin launches, and will not for output-driven actions.

Caveats:

- On Windows, a process receives one command-line string. .NET serializes the argument list using the standard quoting conventions, and the target program parses it again. PowerShell, Python and Node.js follow the standard rules and receive the intended arguments. `cmd.exe` and batch files parse differently, which is one reason batch execution stays deferred.
- The interpreter executes the script itself. That is the plugin's code, covered by the trust model above, not a shell-injection issue.

Further rules:

- Interpreters are resolved deterministically to validated, fully qualified local paths: an explicitly configured path (no fallback when invalid), PowerShell 7's installation folder, validated PATH directories, the system directory. Relative, empty, remote, device and alias PATH entries, the current directory, plugin-local interpreters, links, app execution aliases and files that are not Windows programs are never used. The resolved path and its source are in every result. **Resolution is not authentication**: PATH directories and installed interpreters are part of your trusted environment.
- Plugins, their metadata and their output can never choose the interpreter or add interpreter options.
- The working directory is the script's directory. The environment is the TrayWisp process's environment minus documented interpreter startup, import-path and profiler variables (`PYTHON*`, `NODE_*`, .NET startup hooks and profilers). This filter is not a sandbox.
- PowerShell execution policy is **not** a security boundary, as Microsoft itself documents. TrayWisp neither relies on it nor bypasses it, and neither tightens nor weakens it: an inherited process-scope policy (`PSExecutionPolicyPreference`) is passed on unchanged, so a restrictive process-level policy still applies to plugins. TrayWisp never passes `-ExecutionPolicy`, never uses `-Command` wrappers and never changes a policy.
- Interpreter candidates are validated in order: syntax, then the drive type, then every directory from the drive root down (no links, not offline or remote), and only then the candidate file, so nothing below an unsafe directory is opened. PATH is cut only between complete entries. Validation narrows races; it is not file system isolation (see [execution-engine.md](execution-engine.md#candidate-validation)).

### Execution timeouts

- Every run has a timeout: 30 s by default, between 1 s and 5 min. It includes the end of both output streams, so a background process that keeps a pipe open cannot make a run wait indefinitely.
- Completion, the deadline, cancellation and failures are decided by the monotonic time at which each was observed, not by when TrayWisp's supervising code resumes: a run that completes after its deadline is never published, and a cancellation observed first is never lost (see [execution-engine.md](execution-engine.md#timeout-and-cancellation)).
- When a run times out, it is terminated (see [Process-tree termination](#process-tree-termination)) and reported (`TW633`). A per-plugin timeout setting is planned with scheduling.

### Bounded output

- Captured stdout is capped at 1 MiB and stderr at its last 64 KiB, by bytes actually read. Exceeding the stdout limit terminates the run and rejects its output.
- Truncated or otherwise incomplete stdout (byte limit, timeout, cancellation, read failure) is passed to the parser with `isTruncated: true`, so it produces no menu and no action. It is never passed as complete input.
- Stdout is decoded strictly (UTF-8, or UTF-16 with a byte order mark). Malformed bytes reject the output instead of becoming different text, arguments or URLs.
- Output is published only for a run that exited with code 0, was captured completely, decoded and parsed. A failed or denied run never exposes a menu or an action.
- The stderr text kept for diagnostics is bounded (100 lines of 500 characters) and sanitized.
- These limits protect the host from memory exhaustion and UI stalls caused by runaway or malicious output. They do not limit what the plugin itself does.

### Cancellation

- Runs accept a cancellation token. Cancellation before the launch prevents it; afterwards, it terminates the run. A cancellation that arrives while the process is being created cannot stop the creation, so the process is then terminated at once.
- *Planned:* runs are cancelled when a plugin is disabled, revoked, removed or modified, and when TrayWisp exits; the same plugin never has overlapping runs, and a global concurrency limit bounds the number of runs (Phase 5).

### Process-tree termination

Terminating a plugin's process tree is a **best-effort** safeguard, not an unconditional guarantee. When a run is stopped, TrayWisp terminates the run's Windows job object and calls `Process.Kill(entireProcessTree: true)`, then waits a bounded 5 s for the process to exit, its output pipes to close and the job to empty, and reports what it could confirm. A cleanup that cannot be confirmed is reported as `CleanupIncomplete`, because processes may still be running, and no output is published.

A run owns the processes tracked in its job. After the interpreter exits by itself, processes it started that are still in the job are terminated, their exit is confirmed (`TW652`), and an unconfirmed cleanup, including a job that cannot be queried, is `CleanupIncomplete`. Ordinary plugin runs do not support intentional persistent background processes. Kill-on-close is never cleared. Once a process has been started, a failure to set up its supervision (streams, capture, exit monitoring) terminates it and is reported as `SupervisionFailed`; a failed exit monitor is never taken as proof of exit.

The job object (one per run, `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, no breakaway) catches descendants whose parent has already exited, and terminates the run's processes when it is closed, including if TrayWisp exits abnormally. Limitations:

- The process is assigned to the job **after** `Process.Start` returns. Anything it starts before that is not in the job, so it is neither tracked nor cleaned up after a normal exit. Closing that window needs a native launcher that creates the process inside the job; that is deferred. A post-start job is not complete containment.
- Without a job (assignment failed or unavailable), descendants that released the output pipes are untracked: nothing is confirmed about them.
- If the assignment fails, this is reported and only `Process.Kill(entireProcessTree: true)` applies. That call finds descendants from parent-child relationships that exist at the time, so processes whose parent has exited, or that are created during termination, can be missed.
- Processes started outside the plugin's process tree are not descendants and are not in the job. Examples are tasks started through Task Scheduler, services, COM or WMI, or work handed to an application instance that is already running.
- Processes running elevated or under another account may not be terminable.
- Termination is abrupt. The plugin gets no chance to clean up, so temporary files, locks or partially written files may remain.
- A job does not restrict file, registry or network access, and TrayWisp sets no resource limits.

See [execution-engine.md](execution-engine.md#process-cleanup) for details and the investigation.

### Authorization lease and remaining races

During a run, the checked plugin file and the directories on its path inside the root are kept open, so the file cannot be written, replaced, renamed or deleted, and the directories cannot be renamed or deleted, until the run ends. The trust store is not locked during the run. Memory-mapped writers, imported modules, the interpreter, ancestors of the root and same-user malware are not covered. See [execution-engine.md](execution-engine.md#remaining-time-of-check-to-time-of-use-windows).

### Execution diagnostics

- Each result records the start time, monotonic duration, exit code, termination reason, capture and truncation state, cleanup outcome, the resolved interpreter and a bounded, sanitized stderr tail, with stable codes `TW600`–`TW699` next to the unchanged trust and parser diagnostics.
- Diagnostics never contain environment variable values, credentials, script content, stdout or arbitrary exception messages. The stderr tail can contain whatever a plugin prints, including sensitive data; treat it like the plugin's own output.
- Phase 4 keeps diagnostics in memory and returns them. Nothing is logged, stored or sent.

## Planned safeguards

### Output-driven actions

The SwiftBar/xbar text format lets output lines define actions, such as running a program with parameters or opening a URL when a menu item is clicked. The planned rules:

- Actions are parsed into structured data: a program and an argument list, or a URL (implemented in Phase 2, see [Output parsing](#output-parsing-implemented)).
- Program actions are launched directly with explicit arguments. TrayWisp never joins them into a command string. If a plugin names a shell as the program, for example `cmd.exe` with `/c`, that shell will interpret its arguments. That is the plugin's own request, within the plugin's trust, and TrayWisp displays it as such.
- Trust checks apply when the action is activated (see [Trust checks on every execution path](#trust-checks-on-every-execution-path)).
- URL actions are restricted to an allow-list of schemes, initially `http` and `https`. The parser already rejects every other scheme.
- The interface and diagnostics show the exact program and arguments, or the URL, behind each action.
- If a plugin copies untrusted data, such as API responses, into its action parameters, TrayWisp cannot tell. That is the plugin author's responsibility.

### Diagnostics storage

- Run diagnostics will be stored locally with bounded retention and never sent anywhere by TrayWisp (Phase 6). Environment variables will not be recorded.

### Plugin directory

- The default plugin directory will be per-user. If it, or a plugin in it, is writable by other users, those users can effectively run code as you. TrayWisp will warn about this where it can detect it.

## Process isolation limitations

TrayWisp is designed to bound how long plugin runs last and how much output it captures, and to attempt to clean up plugin processes. It does **not** contain what plugins can do:

- **Job Objects**, which TrayWisp uses for each run, terminate the processes assigned to a job. They could also limit CPU, memory and process count; TrayWisp sets no such limits. They do **not** restrict access to files, the registry, the network or other processes running as you.
- **Stronger isolation** exists on Windows: AppContainer, restricted or low-integrity tokens, Windows Sandbox, separate user accounts. It is **not planned** for the initial releases. These mechanisms break many interpreters and the typical things plugins do, such as reading files, calling local tools and accessing the network. Any future opt-in isolation would be documented with its exact guarantees.
- Plugins can start other processes, schedule tasks, install software or persist themselves outside TrayWisp. TrayWisp cannot reliably stop or undo those effects.

## Out of scope for initial releases

- Downloading or auto-updating plugins from the internet. Plugins are files you place in the plugin directory yourself.
- Code signing or reputation checks for plugins.
