# Security model

TrayWisp's core job is to run scripts on your behalf, so its security model has to be stated plainly. This document describes the trust model, the plugin discovery safeguards that are **implemented**, and the safeguards **planned** for plugin execution. **As of Phase 2, TrayWisp discovers plugin files, reads their metadata and can parse plugin output text, but it does not execute plugins or act on their output.** Apart from [Plugin discovery](#plugin-discovery-implemented) and [Output parsing](#output-parsing-implemented), none of the controls below are implemented yet. They are design commitments that later phases (see [roadmap.md](roadmap.md)) must meet or explicitly revise.

To report a vulnerability, see [SECURITY.md](../SECURITY.md).

## Trust model

- **A plugin is arbitrary code.** A plugin script runs as a normal process under your Windows user account, with the same access you have: your files, your network, your credentials stored for your account, and the ability to start other programs. Running a plugin through TrayWisp is equivalent to running the script yourself.
- **Approval is consent, not containment.** TrayWisp will not run a plugin until you approve it. Approval only records that you chose to trust that script. It does not restrict what the script can do. **User approval is not sandboxing.**
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
- Guarantee that the content you approved is exactly the content that runs (see [Explicit approval](#explicit-approval)).
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
- **Actions are requests, not permissions.** A command is an executable plus an explicit argument list, never a shell command line. `shell` and `bash` are just names for the executable. URLs are limited to `http` and `https`. `refresh` only ever means the producing plugin. Nothing is resolved, checked for existence, opened or launched. Every action must pass the planned [trust checks](#trust-checks-on-every-execution-path) and execution safeguards when activated.
- **Fail closed.** Malformed syntax, duplicated or conflicting action parameters, unknown parameters on action lines and unsupported behavioural parameters (`alternate`, `key`, `webview`, …) suppress the whole action, including its refresh request. A parsed action is never partial. An invalid `disabled` value disables the item. A disabled item or an item with a submenu exposes no action.
- **Bounded.** Input length, lines, line length, titles, menu nodes, depth, parameters, value length, arguments and diagnostics are capped. Output that exceeds a global cap, including a single over-long physical line, exposes no menu and no action. Problems confined to one item or parameter only drop that item, ignore those parameters or suppress that action. Processing is linear and uses no regular expressions.
- **Incomplete output exposes nothing.** A cut can leave syntactically valid but different values (`param1=document.txt` cut to `param1=doc`), and the parser cannot detect this. A caller that passes `isTruncated: true` gets a failed result (`TW405`) with no menu and no action. The parser never infers truncation, for example from a missing final line feed, so Phase 4 must report every incomplete capture and must never pass truncated stdout as complete input.
- **Streaming is rejected.** A SwiftBar `~~~` streaming delimiter fails the whole parse (`TW416`), so actions from earlier frames can never be shown alongside later ones.
- **Display text is sanitized.** Control characters (including terminal escape sequences and a lone CR), line separators and invalid UTF-16 become U+FFFD, and directional formatting characters are removed. Action values containing them are rejected. Labels and tooltips made only of invisible characters (white space, format characters, default-ignorable code points such as Hangul fillers and variation selectors, and U+2800) are dropped. This is a best-effort safeguard, not a guarantee that every font renders every accepted label visibly.
- **Strict URL syntax.** URLs must be well formed (`Uri.IsWellFormedOriginalString`), use only characters RFC 3986 allows, have valid two-digit percent escapes, and have no user information or rewritten hosts. Raw `|`, spaces, backslashes and control characters are rejected. Percent escapes, including escaped control characters such as `%0A`, are kept exactly as written and never decoded by the parser; future code must hand off `Uri.OriginalString`, never `Uri.ToString()`.
- **Parsing is not authorization.** A complete parse only says the text is syntactically acceptable. It never authorizes running a command, opening a URL or refreshing a plugin.
- **Safe diagnostics.** Messages never repeat labels, URLs, executables or argument values, so logging them does not leak plugin output.

Limitations: homoglyphs, zero-width characters inside visible text and lookalike domains are not detected; display surfaces should show the exact URL or command behind an action. If a plugin passes untrusted data (for example an API response) into its own action parameters, the parser cannot tell.

## Planned safeguards

### Explicit approval

- Newly discovered plugins start in a *pending* state and do not run.
- Approval is bound to the normalized **plugin root**, the plugin's **identity** (its root-relative path, see [plugin-format.md](plugin-format.md#identity)) and a SHA-256 hash of its contents. The identity alone is not enough: the same relative path under another root is a different file. If the file changes, approval lapses and the plugin stops running until you approve it again.
- **Time-of-check/time-of-use (TOCTOU).** TrayWisp checks the hash, but the interpreter later opens and reads the file by path on its own. Between the check and the read, anything that can write to the plugin file or to the directories on its path can change the contents, or redirect the path with a symbolic link or junction. Planned mitigations are to re-verify immediately before every launch and to resolve the path to its final target. These narrow the window but do **not** eliminate it. Read an approval as "this content was trusted when it was checked", not as a guarantee that exactly this content runs.
- **Scope of the hash.** It covers only the plugin file. It does not cover modules the script imports, files it reads, the interpreter, or anything the script downloads. An approved script can still change its behaviour through any of those.
- In practice, anyone who can write to your plugin directory can usually run code as you anyway, so these risks matter most when that directory's permissions are weaker than expected (see [Plugin directory](#plugin-directory)).

### Trust checks on every execution path

Approval is useful only if no execution path can skip it. The applicable trust checks will be enforced on:

- Scheduled runs, including runs triggered by hot reload.
- Manual runs from the system tray or the plugin management interface.
- CLI commands that run a plugin.
- Output-driven actions. An action is allowed only if the plugin that produced it is still approved with the same content hash at the moment you activate it. Actions from output produced before a revocation or a file change are disabled.

The trust check lives in `TrayWisp.Runtime`, and the CLI and desktop hosts launch plugins only through Runtime. If an override is ever added, for example a one-off CLI run of an unapproved plugin, it must be an explicit per-invocation user action and be recorded in diagnostics. Disabling or revoking a plugin cancels its in-flight runs.

### Process launch: explicit arguments, not shell command strings

These are two different things, and TrayWisp will only do the first:

- **Launching an interpreter directly with explicit arguments.** TrayWisp starts the interpreter executable, such as `pwsh.exe` or `python.exe`, and passes the script path and fixed options as separate arguments (`ProcessStartInfo.ArgumentList`). There is no `cmd.exe`, no PowerShell `-Command` string and no string concatenation. Spaces, quotes and characters such as `&` or `|` in file names are therefore not interpreted by a shell.
- **Evaluating shell command strings.** Passing a constructed string to `cmd.exe /c`, `powershell -Command` or `sh -c` makes the shell parse it, so any untrusted fragment becomes code. TrayWisp will not do this for plugin launches or for output-driven actions.

Caveats:

- On Windows, a process receives one command-line string. .NET serializes the argument list using the standard quoting conventions, and the target program parses it again. Programs that follow the standard Microsoft C/C++ parsing rules receive the intended arguments. `cmd.exe` and batch files (`.cmd`, `.bat`) parse differently, so explicit arguments do not give them the same protection. Discovery recognizes batch files, but their execution support is deferred and undecided.
- The interpreter executes the script itself. That is the plugin's code, covered by the trust model above, not a shell-injection issue.

Further rules:

- Interpreters are resolved to absolute paths, and the resolved path is shown in diagnostics, so it is visible which `python.exe` or `pwsh.exe` actually ran.
- Working directory and environment variables are set deliberately rather than inherited by accident. The exact policy will be documented when implemented.
- PowerShell execution policy is **not** a security boundary, as Microsoft itself documents. TrayWisp will not present it as one.

### Execution timeouts

- Every run has a timeout, with a default and an optional per-plugin override within an upper limit.
- When a run times out, TrayWisp records the timeout in diagnostics and attempts to terminate the plugin's process tree (see [Process-tree termination](#process-tree-termination)).

### Bounded output

- Captured stdout and stderr are capped by total bytes, line count and line length.
- Output beyond the limits is discarded, and the run is marked as truncated in diagnostics. Truncated or otherwise incomplete stdout (byte limit, timeout, cancellation, read failure) is passed to the parser with `isTruncated: true`, so it produces no menu and no action. It is never passed as complete input.
- Embedded content such as base64 images will have its own size limits. The Phase 2 parser does not decode images at all.
- The Phase 2 parser applies its own limits to decoded text (see [output-protocol.md](output-protocol.md#limits)). Byte limits on captured output come with execution in Phase 4.
- These limits protect the host from memory exhaustion and UI stalls caused by runaway or malicious output. They do not limit what the plugin itself does.

### Cancellation

- Runs are cancelled when a plugin is disabled, revoked, removed or modified, and when TrayWisp exits. Cancellation uses the best-effort termination described below.
- The same plugin never has overlapping runs. A global concurrency limit bounds the number of plugin runs TrayWisp starts at once.

### Process-tree termination

Terminating a plugin's process tree is a planned **best-effort** safeguard, not an unconditional guarantee.

The baseline mechanism is `Process.Kill(entireProcessTree: true)`. Its limitations:

- It finds descendants from the parent-child relationships of processes that exist at the time of the call. A process whose parent has already exited is no longer linked to the tree and may not be found, for example a background process started by a launcher script that then exits.
- Processes created while termination is in progress can be missed.
- Processes started outside the plugin's process tree are not descendants. Examples are tasks started through Task Scheduler, services, COM or WMI, or work handed to an application instance that is already running.
- Processes running elevated or under another account may not be terminable.
- Termination is abrupt. The plugin gets no chance to clean up, so temporary files, locks or partially written files may remain.
- It only runs if TrayWisp is still running. If TrayWisp exits abnormally, nothing terminates the plugin's processes.

**Future investigation: Windows Job Objects.** A job object configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` terminates every process in the job when the last handle to the job closes, including when TrayWisp crashes. Child processes join their parent's job by default, whether or not the parent is still running, which closes the orphaned-process gap above. A job can also limit memory, CPU and process count. Known limitations:

- A process must be in the job before it starts children. Assigning it after `Process.Start` leaves a window in which early children escape. Closing that window requires creating the process suspended or with a job attribute, which needs Win32 interop beyond `System.Diagnostics.Process`.
- The job must not allow breakaway.
- Processes started outside the tree, as listed above, are still not in the job.
- A job does not restrict file, registry or network access.

Whether and how TrayWisp uses job objects will be decided and documented in Phase 4.

### Output-driven actions

The SwiftBar/xbar text format lets output lines define actions, such as running a program with parameters or opening a URL when a menu item is clicked. The planned rules:

- Actions are parsed into structured data: a program and an argument list, or a URL (implemented in Phase 2, see [Output parsing](#output-parsing-implemented)).
- Program actions are launched directly with explicit arguments. TrayWisp never joins them into a command string. If a plugin names a shell as the program, for example `cmd.exe` with `/c`, that shell will interpret its arguments. That is the plugin's own request, within the plugin's trust, and TrayWisp displays it as such.
- Trust checks apply when the action is activated (see [Trust checks on every execution path](#trust-checks-on-every-execution-path)).
- URL actions are restricted to an allow-list of schemes, initially `http` and `https`. The parser already rejects every other scheme.
- The interface and diagnostics show the exact program and arguments, or the URL, behind each action.
- If a plugin copies untrusted data, such as API responses, into its action parameters, TrayWisp cannot tell. That is the plugin author's responsibility.

### Diagnostics

- Each run records its start time, duration, exit code, timeout and truncation flags, and a bounded tail of stderr.
- Diagnostics are stored locally with bounded retention and are never sent anywhere by TrayWisp.
- Diagnostics can contain whatever a plugin prints, including sensitive data. Treat them like the plugin's own output when sharing logs. Environment variables are not recorded.

### Plugin directory

- The default plugin directory will be per-user. If it, or a plugin in it, is writable by other users, those users can effectively run code as you. TrayWisp will warn about this where it can detect it.

## Process isolation limitations

TrayWisp is designed to bound how long plugin runs last and how much output it captures, and to attempt to clean up plugin processes. It does **not** contain what plugins can do:

- **Job Objects**, if adopted, can terminate the processes assigned to a job and limit CPU, memory and process count. They do **not** restrict access to files, the registry, the network or other processes running as you.
- **Stronger isolation** exists on Windows: AppContainer, restricted or low-integrity tokens, Windows Sandbox, separate user accounts. It is **not planned** for the initial releases. These mechanisms break many interpreters and the typical things plugins do, such as reading files, calling local tools and accessing the network. Any future opt-in isolation would be documented with its exact guarantees.
- Plugins can start other processes, schedule tasks, install software or persist themselves outside TrayWisp. TrayWisp cannot reliably stop or undo those effects.

## Out of scope for initial releases

- Downloading or auto-updating plugins from the internet. Plugins are files you place in the plugin directory yourself.
- Code signing or reputation checks for plugins.
