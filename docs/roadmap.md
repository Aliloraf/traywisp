# Roadmap

This roadmap is **tentative**. Scope and order beyond the current phase may change as design work goes on. Each phase should leave the repository buildable, tested and accurately documented. Every phase that touches discovery, approval, execution, output handling or diagnostics must meet the commitments in [security.md](security.md).

| Phase | Theme | Status |
| --- | --- | --- |
| 0 | Project foundation | **Complete** |
| 1 | Plugin models, discovery and metadata | **Complete** |
| 2 | SwiftBar/xbar-compatible text output parser | **Complete** |
| 3 | Plugin trust and approval | **Complete** |
| 4 | Script execution engine | **Complete** |
| 5 | Scheduling and hot reload | **Complete** |
| 6 | CLI and plugin diagnostics | Planned |
| 7 | Native Windows system tray | Planned |
| 8 | Plugin management interface | Planned |
| 9 | Customizable status bar | Planned |
| 10 | Extended plugin protocol and ecosystem | Planned |
| 11 | Performance, security audit, packaging and release | Planned |

## Phase 0: Project foundation (complete)

- Solution with Core, Runtime, CLI, Desktop and test projects.
- Shared build configuration: nullable, deterministic builds, analyzers, warnings as errors, central package versions, SDK pinning.
- Build-time and test-time enforcement of project dependency rules.
- Minimal runnable WPF window and a CLI with `--help` and `--version`.
- Windows CI for restore, build and test.
- Documentation of the architecture, security model and platform constraints.

## Phase 1: Plugin models, discovery and metadata (complete)

Specified in [plugin-format.md](plugin-format.md).

- Core: immutable models for plugin identity (root-relative path, case-insensitive), file name, refresh interval, language, metadata, descriptor and structured diagnostics.
- Core: a culture-independent parser for `name[.interval].extension` with `ms`, `s`, `m`, `h` and `d` units, a 250 ms to 30 day range, and an unambiguous rule for dotted names and malformed intervals. Bare numbers such as `backup.2024` are part of the name.
- Core: classification of `.ps1`, `.py` and `.js` (execution planned) and `.sh`, `.bat` and `.cmd` (execution deferred). Other files, including executables, are ignored.
- Core: a bounded, regex-free and XML-free parser for `traywisp`, `swiftbar`, `xbar` and `bitbar` metadata tags in language-specific comments, with namespace precedence, duplicate and conflict diagnostics, UTF-8/UTF-16 BOM handling and size limits. SwiftBar/xbar behavioural directives are reported as unsupported.
- Runtime: read-only, deterministic, recursive discovery with traversal limits and cancellation. It skips hidden entries, reparse points and remote-storage entries, and re-checks entries immediately before and after listing or reading them. Case-variant names are excluded before anything is read, and a listing truncated by the entry budget contributes nothing. Per-file failures are isolated, and incomplete scans are flagged.
- Tests for parsing edge cases, encodings and limits, and filesystem tests against temporary directories (junctions, symbolic links, case-sensitive directories, ACL-denied and locked files). A test checks that discovery neither executes nor modifies plugins.
- Sample plugins in `plugins/examples`.

Not in Phase 1: output parsing, execution, scheduling, file watching, approval storage, interpreter resolution, CLI commands and UI.

## Phase 2: SwiftBar/xbar-compatible text output parser (complete)

Specified in [output-protocol.md](output-protocol.md).

- Core: a parser that turns SwiftBar/xbar-style text output into an immutable model of ordered header titles and a nested menu of items and separators, written independently from the publicly documented format. It supports a documented subset and does not claim full compatibility.
- An explicit line grammar: the first unescaped `|` ends the label (`\|` is a literal pipe), and space- or pipe-separated `name=value` parameters follow, with single or double quotes, doubled quotes inside quoted values and literal backslashes, so Windows paths are kept intact. Nothing is expanded.
- Typed formatting: CSS named and hexadecimal colours with light/dark pairs, font, size, length (grapheme-safe display truncation), trim, tooltip, dropdown, disabled and checked.
- Inert actions: refresh of the producing plugin, `http`/`https` URLs, and commands with a separate executable and a contiguous zero- or one-based argument list. Conflicting, duplicated, malformed, unknown or unsupported behavioural parameters suppress the whole action.
- Parser limits (input length, lines, line length, titles, menu nodes, depth, parameters, value length, arguments, diagnostics). Over-limit output, including any over-long physical line, exposes nothing. Output the caller reports as truncated (`TW405`) and output containing a `~~~` streaming delimiter (`TW416`) also fail as a whole. Diagnostics `TW400`–`TW499` are bounded, in detection order, and never repeat output values.
- Best-effort rejection of invisible-only labels and tooltips, and strict, non-normalizing `http`/`https` URL syntax validation.
- Sanitized display text: control characters, line separators and lone surrogates are replaced, and directional formatting characters removed.
- Tests for structure, grammar, formatting, actions, Unicode, every limit, determinism, seeded randomized malformed input, and recorded output of the sample plugins and of original examples. The sample plugins are not executed.

Not in Phase 2: execution of actions, rendering, rotation of titles, image decoding, ANSI/Markdown/emoji rendering, streaming, the JSON protocol and CLI commands.

## Phase 3: Plugin trust and approval (complete)

Specified in [trust-model.md](trust-model.md).

- Core: immutable trust models (validated SHA-256 fingerprint, approval generation, normalized local plugin root, root + `PluginId` key, inspection, approval identity, approval record), trust statuses, a pure trust policy and diagnostics `TW500`–`TW599`.
- Runtime: `PluginTrustService` with inspection, explicit approval of a reviewed fingerprint (revalidated and rehashed under the store lock immediately before saving), revocation, and one authoritative trust check, plus a variant that rejects stale approval identities for future output-driven actions.
- Full-content SHA-256 fingerprinting through a validated handle, with a 16 MiB limit enforced on bytes read, sharing that blocks concurrent writes, cancellation, and structured failures.
- Conservative path validation: local drives only; no relative, UNC, device-namespace or alternate-data-stream paths; no reparse points, offline entries, hidden or dot-prefixed components, case collisions or case-sensitive ancestors; short-name, `subst` and mapped-drive aliases rejected using final-path verification on opened handles; discovery's own classification and collision rules applied to the targeted path.
- Persistent invalidation when a check or an approval request observes changed content, never restored automatically. If the invalidation cannot be saved, the observing service instance keeps denying the approval and retries; other instances and restarts do not know about it. Transient failures and incomplete discovery never revoke approvals.
- Approved decisions return the exact validated file path (on-disk spelling), so execution never rebuilds a path from the case-insensitive identity.
- A strict, versioned JSON store in `%LOCALAPPDATA%\TrayWisp\trust`, with deterministic output, an inter-process lock file, a store directory held open during each operation, handle checks of the lock and store files (single link, final path), atomic replacement through a flushed same-directory temporary file, and all-or-nothing parsing (no partial trust of malformed stores).
- Tests for fingerprints, identities, path attacks (junctions, symbolic links, short names, case-sensitive directories), approval workflows, stale identities, persistence, corruption, concurrency, lock bounds and cancellation.

Not in Phase 3: execution, interpreter resolution, scheduling, hot reload, record pruning, CLI commands, approval UI, signing or encryption of the store.

## Phase 4: Script execution engine (complete)

Specified in [execution-engine.md](execution-engine.md).

- Runtime: `PluginExecutionService.RunAsync`, the one execution entry point, built on a long-lived `PluginTrustService`. No public unchecked launch path.
- Execution of PowerShell (PowerShell 7 preferred, Windows PowerShell 5.1 as fallback), Python 3 and Node.js plugins. Bash and batch stay deferred and start nothing.
- Deterministic interpreter resolution from explicit host configuration (no fallback when invalid), PowerShell 7's installation folder, validated PATH entries and the system directory. Rejects relative, empty, remote, device, alias and plugin-local entries, links, app execution aliases and files without a portable executable header. Nothing is launched or probed to resolve.
- The authoritative trust check immediately before every launch, with an internal lease that keeps the validated file and its directories open for the whole run, without holding the store lock. Exactly the validated path is launched.
- Direct launch with fixed arguments (`-NoLogo -NoProfile -NonInteractive -File`, `-E -u -X utf8`, `--`), no shell, closed standard input, the script's directory as working directory and a documented environment filter.
- Concurrent byte-bounded capture (1 MiB stdout with exact-limit detection, 64 KiB stderr tail) on dedicated threads with interruptible reads, a monotonic timeout that includes the end of both streams, cancellation, and deterministic race handling.
- Strict output decoding (UTF-8, UTF-16 with a byte order mark), the unchanged Phase 2 parser with `isTruncated: true` for every incomplete capture, and output published only for successful runs, with the authorizing approval identity.
- Process cleanup: a per-run Windows job object (assigned after the start, kill-on-close, no breakaway) plus `Process.Kill(entireProcessTree: true)`, with a bounded grace period and a report that separates root exit from descendant cleanup.
- Correction round after independent review: the inherited PowerShell execution policy is preserved; interpreter directories are validated (drive type, then directory chain) before candidate files are opened, and PATH is cut only between complete entries; tracked descendants left after a normal exit are terminated and confirmed (no release on exit); one cleanup boundary covers everything after `Process.Start` (`SupervisionFailed`, `TW634`); completion, deadline and cancellation are arbitrated by recorded monotonic times; real-interpreter tests are optional locally and required in CI.
- Diagnostics `TW600`–`TW699`, bounded, with no environment values or output content.
- Tests with a controlled helper program as interpreter (argument transport, environment, streams, limits, timeouts, cancellation, descendants, cleanup), the real interpreters where installed, and authorization tests that observe process creation.

Not in Phase 4: scheduling, overlapping-run prevention, concurrency limits, hot reload, file watching, retries, execution history, execution of output actions, CLI commands, UI, race-free job assignment through a native launcher, sandboxing.

## Phase 5: Scheduling and hot reload (complete)

Specified in [scheduling.md](scheduling.md).

- Runtime: `PluginScheduler`, one long-lived scheduler per plugin root, built from the host's execution service and making every trust check with that service's own trust service. Single-use lifecycle (`StartAsync`, `StopAsync`, `DisposeAsync`), manual refresh with accepted, coalesced and rejected results, rescans, trust-change notifications and in-memory enabling and disabling.
- Core: pure scheduling policy (fixed-delay intervals from the file name, no catch-up, failure backoff of 5 s doubling to 60 s and never shorter than the interval, a 250 ms minimum relaunch delay, eligibility precedence, derived display status, retained-output cost), state dimensions and diagnostics `TW700`–`TW799`.
- Immutable snapshots with monotonic revisions and coalesced change notification (`WaitForChangeAsync`), separating eligibility, activity and presence; bounded latest-run summaries without output; publishable output only in `ScheduledPlugin.Output`, with its approval identity.
- A global concurrency limit (default 4, 1–16) counted from dispatch to the end of cleanup and the output check, one attempt per plugin identity (also across removal and reappearance), and fairness by readiness time.
- Reconciliation with the unchanged discovery: root validation in a safe order (drive type, directory chain, short names), removal only after a complete scan, uncertain plugins kept (and their output withheld until a fresh check) after incomplete scans, every plugin suspended and its run cancelled when the root is unavailable or replaced, renames as new identities, case-only renames kept, descriptor changes treated as hints of content changes and decided by the trust service, bounded tracking (500) and a replaced-root check.
- A debounced recursive `FileSystemWatcher` (250 ms quiet, at most 1 s), periodic full reconciliation (30 s), overflow and failure recovery, storms bounded, auxiliary file writes ignored.
- Trust integration: launches only through the engine, with an internal expected-approval variant of the launch-time check so a run selected under one approval generation cannot launch under another; output published only after a fresh `CheckTrust(result.Approval)` and an atomic revision check; invalidation and cancellation after revocation, reapproval, content change, disabling, removal and shutdown; stale results rejected.
- Incomplete-cleanup protection: a permanent per-instance block, one reserved slot per incident, fail-closed registry.
- Bounded shutdown shared by concurrent callers, with an accurate incomplete result that includes teardown and cancellation delivery; faults observable and fail-closed, also in completion, finalization, cancellation callbacks and disposal; trust observations versioned per plugin so a stale approval never restores eligibility.
- Deterministic tests on fake operations with a manual clock, real Windows integration tests with the helper interpreter and the real watcher, and repeated runs to detect races.

Not in Phase 5: CLI commands, Desktop, tray or status bar integration, persistent plugin settings, execution of output actions, automatic approval, process enumeration, native launcher, sandboxing.

## Phase 6: CLI and plugin diagnostics

- `traywisp` commands to list plugins, show metadata, validate output, approve or revoke, run once (subject to the same trust checks) and show diagnostics.
- Bounded, local storage of diagnostics.

## Phase 7: Native Windows system tray

- A desktop host with a tray icon per plugin: icon, tooltip and menu rendered from the menu model. Persistent text is not possible in the tray (see [architecture.md](architecture.md#platform-constraint-the-windows-notification-area)).
- Output-driven actions under the rules in [security.md](security.md#output-driven-actions), with trust checked when the action is activated.

## Phase 8: Plugin management interface

- A desktop UI for plugin status, approval and revocation, enable/disable, manual runs and diagnostics.

## Phase 9: Customizable status bar

- A separate display surface for persistent text widgets, which the Windows notification area cannot provide.
- Decides between an app bar and a floating window, defines multi-monitor behaviour, and adds layout and appearance customization.

## Phase 10: Extended plugin protocol and ecosystem

- A documented, versioned JSON output protocol alongside the text format.
- Plugin authoring documentation and examples.
- Automatic downloading or updating of plugins stays out of scope unless [security.md](security.md#out-of-scope-for-initial-releases) is revised first.

## Phase 11: Performance, security audit, packaging and release

- Measuring and improving idle CPU, memory use and startup time.
- A security review of the trust, execution and output-handling paths against [security.md](security.md).
- Release builds, an installation method and release notes.
- A supported-versions policy in [SECURITY.md](../SECURITY.md).
