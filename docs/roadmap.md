# Roadmap

This roadmap is **tentative**. Scope and order beyond the current phase may change as design work goes on. Each phase should leave the repository buildable, tested and accurately documented. Every phase that touches discovery, approval, execution, output handling or diagnostics must meet the commitments in [security.md](security.md).

| Phase | Theme | Status |
| --- | --- | --- |
| 0 | Project foundation | **Complete** |
| 1 | Plugin models, discovery and metadata | Planned |
| 2 | SwiftBar/xbar-compatible text output parser | Planned |
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

## Phase 1: Plugin models, discovery and metadata

- Core: plugin identity and metadata models, including the refresh interval parsed from the file name (`name.interval.ext`).
- Core: parsing of metadata that plugin authors embed in plugin files. The supported conventions are decided during the phase.
- Runtime: discovery of candidate plugin files in a plugin directory, and classification by file extension.
- Discovery only reads files. It never executes a plugin.
- Unit tests for metadata edge cases, and discovery tests against temporary directories.

## Phase 2: SwiftBar/xbar-compatible text output parser

- Core: a parser that turns SwiftBar/xbar-style text output into a structured menu model, written from the publicly documented format.
- Action parameters, such as commands and URLs, are parsed into structured data only. Nothing is executed in this phase.
- Behaviour for malformed, unsupported and adversarial input is defined and covered by thorough unit tests.

## Phase 3: Plugin trust and approval

- An approval store bound to the plugin's path and SHA-256 content hash. Approval lapses when the file changes.
- One trust check that every later execution path must use: scheduled runs, manual runs, CLI runs and output-driven actions.
- The time-of-check/time-of-use limits of this design are documented in [security.md](security.md#explicit-approval).

## Phase 4: Script execution engine

- Resolves PowerShell, Python and other installed interpreters to absolute paths.
- Launches the interpreter directly with explicit arguments, never by evaluating shell command strings.
- Enforces a per-run timeout and bounded stdout/stderr capture, and supports cancellation.
- Terminates the process tree on a best-effort basis with `Process.Kill(entireProcessTree: true)`, plus an investigation of Windows Job Objects whose outcome is documented (see [security.md](security.md#process-tree-termination)).
- Re-checks trust immediately before every launch.
- Records diagnostics for each run.

## Phase 5: Scheduling and hot reload

- Interval scheduling with no overlapping runs of the same plugin and a global concurrency limit.
- Hot reload: plugin files that are added, changed or removed are detected. In-flight runs of changed or removed plugins are cancelled. Changed plugins lose approval until you approve them again.

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
