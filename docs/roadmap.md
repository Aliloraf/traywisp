# Roadmap

This roadmap is **tentative**. Scope and order beyond the current phase may change as design work goes on. Each phase should leave the repository buildable, tested and accurately documented. Every phase that touches discovery, approval, execution, output handling or diagnostics must meet the commitments in [security.md](security.md).

| Phase | Theme | Status |
| --- | --- | --- |
| 0 | Project foundation | **Complete** |
| 1 | Plugin models, discovery and metadata | **Complete** |
| 2 | SwiftBar/xbar-compatible text output parser | **Complete** |
| 3 | Plugin trust and approval | Planned |
| 4 | Script execution engine | Planned |
| 5 | Scheduling and hot reload | Planned |
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

## Phase 3: Plugin trust and approval

- An approval store bound to the normalized plugin root, the plugin's identity (its root-relative path, see [plugin-format.md](plugin-format.md#identity)) and its SHA-256 content hash. Approval lapses when the file changes. Approval must not be revoked merely because a plugin is missing from an incomplete discovery result.
- One trust check that every later execution path must use: scheduled runs, manual runs, CLI runs and output-driven actions.
- The time-of-check/time-of-use limits of this design are documented in [security.md](security.md#explicit-approval).

## Phase 4: Script execution engine

- Resolves PowerShell, Python and other installed interpreters to absolute paths.
- Launches the interpreter directly with explicit arguments, never by evaluating shell command strings.
- Enforces a per-run timeout and bounded stdout/stderr capture, and supports cancellation.
- Tracks whether stdout capture was complete, and passes `isTruncated: true` to the output parser whenever it was not (byte limit, timeout, cancellation, read failure). Incomplete stdout is never parsed as complete input.
- Terminates the process tree on a best-effort basis with `Process.Kill(entireProcessTree: true)`, plus an investigation of Windows Job Objects whose outcome is documented (see [security.md](security.md#process-tree-termination)).
- Re-checks trust immediately before every launch.
- Records diagnostics for each run.

## Phase 5: Scheduling and hot reload

- Interval scheduling with no overlapping runs of the same plugin and a global concurrency limit.
- Hot reload: plugin files that are added, changed or removed are detected. In-flight runs of changed or removed plugins are cancelled. Changed plugins lose approval until you approve them again.
- A plugin counts as removed only when it is absent from a **complete** discovery result (`PluginDiscoveryResult.IsComplete`). Absence from an incomplete scan, for example while a file is briefly locked, is not removal.

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
