# TrayWisp

TrayWisp is a planned open-source, lightweight, script-powered desktop customization platform for Windows. When it is ready, you will place a script in a plugin folder and approve it explicitly. TrayWisp will then run it on a schedule and show its output in the Windows system tray, and later in a customizable status bar. A plugin management interface will cover approval and diagnostics. Today TrayWisp is a set of libraries: it can discover plugin files, read their file names and embedded metadata, parse SwiftBar/xbar-style output text into a menu model, record explicit, content-bound approvals, and run an approved PowerShell, Python or JavaScript plugin through its execution engine, and keep the plugins of a folder discovered, watched, trust-checked and refreshed with a scheduler. **Neither the execution engine nor the scheduler is exposed through the CLI or the desktop app yet**, and nothing displays plugins.

TrayWisp takes ideas from SwiftBar, xbar, Crossbar, YASB and Zebar. It is an independent implementation, not affiliated with any of those projects, and contains none of their source code.

> **Status: early development.** Phase 0 (project foundation), Phase 1 (plugin models, discovery and metadata), Phase 2 (text output parser), Phase 3 (plugin trust and approval), Phase 4 (script execution engine) and Phase 5 (scheduling and hot reload) are complete. CLI commands and the system tray are planned for later phases. See [docs/roadmap.md](docs/roadmap.md).

## What exists today

| Area | Status |
| --- | --- |
| Solution structure, shared build configuration, Windows CI | Implemented |
| Build-time enforcement of project dependency rules | Implemented |
| `traywisp` CLI | Implemented: `--help` and `--version` only |
| Desktop application (WPF) | Implemented: an informational window only, no tray icon |
| Plugin models, discovery and metadata | Implemented (library only): `name.interval.ext` file names, language classification, SwiftBar/xbar-compatible metadata tags, read-only recursive discovery. See [docs/plugin-format.md](docs/plugin-format.md). Not yet exposed in the CLI or desktop app. |
| SwiftBar/xbar-compatible text output parser | Implemented (library only): header titles, nested menus and separators, formatting parameters, and URL, command and refresh actions parsed into inert data, with strict quoting, limits and diagnostics. A documented subset, not full compatibility. See [docs/output-protocol.md](docs/output-protocol.md). Not yet connected to execution or any display. |
| Plugin trust and approval | Implemented (library only): explicit approval of a reviewed SHA-256 fingerprint of the whole file, bound to the plugin root and identity; one authoritative trust check that rehashes the file every time and invalidates approvals when content changes; stale-approval detection for future output actions; strict path validation (no links, aliases, network or hidden paths); a versioned, locked, atomically replaced JSON store in `%LOCALAPPDATA%\TrayWisp\trust`. See [docs/trust-model.md](docs/trust-model.md). Not yet exposed in the CLI or desktop app. |
| Script execution engine | Implemented (library only): runs an approved `.ps1`, `.py` or `.js` plugin once, after an authoritative trust check made immediately before the launch, with a deterministically resolved installed interpreter (PowerShell 7 or Windows PowerShell, Python 3, Node.js), fixed arguments and no shell, a filtered environment, a timeout, cancellation, 1 MiB stdout and 64 KiB stderr limits, strict UTF-8 decoding, the output parser, and job-object and process-tree termination. Bash and batch are not run. See [docs/execution-engine.md](docs/execution-engine.md). Not yet exposed in the CLI or desktop app. |
| Scheduling and hot reload | Implemented (library only): one scheduler per plugin folder runs approved interval plugins on their schedule and manual plugins on request, through the execution engine and its launch-time trust check, with a global concurrency limit (default 4), no overlapping runs of one plugin, failure backoff, a debounced file system watcher plus periodic reconciliation, cancellation and output withdrawal when a plugin is changed, revoked, disabled or removed, permanent blocking of plugins whose process cleanup could not be confirmed, immutable snapshots and bounded shutdown. See [docs/scheduling.md](docs/scheduling.md). Not yet used by the CLI or desktop app; no tray icon or status bar. |
| CLI plugin commands and diagnostics | Planned (Phase 6) |
| Native system tray, plugin management interface, customizable status bar | Planned (Phases 7–9) |
| Versioned JSON output and extended plugin protocol | Planned (Phase 10) |

## Product direction (planned)

- **Drop-and-run plugins.** Scripts in a plugin folder will be picked up automatically. A script will never run until you approve it.
- **Familiar output format.** Plugins will print text in the SwiftBar/xbar style. A versioned JSON format is planned for richer output.
- **Any installed interpreter.** PowerShell, Python and other interpreters that are already installed. TrayWisp will not bundle interpreters.
- **Efficient and robust.** Interval scheduling, hot reload, execution timeouts, bounded output, cancellation and best-effort process-tree termination.
- **Native Windows surfaces.** System tray icons and menus, a customizable status bar for persistent text, and a plugin management interface.
- **Diagnostics built in.** Each run will be inspectable from the CLI and the plugin management interface.

Plugins will run as ordinary processes with your user account's privileges. Approving a plugin records your consent to run exactly that content. **It is not a sandbox**, and TrayWisp does not promise complete containment of plugin processes. See [docs/security.md](docs/security.md).

## Requirements

- Windows 10 or Windows 11. The desktop application targets `net10.0-windows`.
- .NET SDK 10.0.404 or a later feature band of .NET 10 (pinned by [`global.json`](global.json)).

`TrayWisp.Core`, `TrayWisp.Runtime` and the CLI target plain `net10.0` and build on any platform. The desktop project and the supported product are Windows-only.

## Build and test

```powershell
dotnet restore TrayWisp.slnx
dotnet build TrayWisp.slnx --configuration Release --no-restore
dotnet test --solution TrayWisp.slnx --configuration Release --no-build
```

Tests run on Microsoft.Testing.Platform (configured in `global.json`), so `dotnet test` takes `--solution` or `--project` rather than a bare path.

Run the applications:

```powershell
dotnet run --project src/TrayWisp.Cli -- --version
dotnet run --project src/TrayWisp.Desktop
```

## Plugin files

A plugin is a script whose file name follows `name[.interval].extension`, for example `clock.5s.ps1` (every 5 seconds) or `hello.js` (manual refresh). PowerShell, Python and JavaScript plugins can be executed by the engine. Bash and batch files are recognized but their execution is deferred. Optional metadata goes in comments (tags on code lines are ignored; see [plugin-format.md](docs/plugin-format.md#comments) for the exact, line-based rule):

```powershell
# <traywisp.title>Clock</traywisp.title>
# <traywisp.version>1.0</traywisp.version>
```

`xbar.*`, `bitbar.*` and `swiftbar.*` tags are also read. TrayWisp supports a documented subset of the SwiftBar/xbar conventions. It is not fully compatible. See [docs/plugin-format.md](docs/plugin-format.md) for the grammar, limits, diagnostics and compatibility notes, and [plugins/examples](plugins/examples) for sample scripts. The execution engine can run the samples after approval, but no CLI command or tray does so yet.

## Repository layout

```text
src/
  TrayWisp.Core/       Platform-independent plugin models, file-name, metadata and output parsers, trust models
  TrayWisp.Runtime/    Plugin discovery, trust, the execution engine and the scheduler
  TrayWisp.Cli/        Command-line application (traywisp.exe)
  TrayWisp.Desktop/    WPF desktop application (TrayWisp.Desktop.exe)
tests/
  TrayWisp.Core.Tests/
  TrayWisp.Runtime.Tests/
  TrayWisp.TestHelper/ Controlled stand-in interpreter used by the execution tests (not shipped)
plugins/
  examples/            Sample plugin scripts
docs/
  architecture.md      Projects, dependency rules, platform constraints
  plugin-format.md     File names, metadata, discovery, diagnostics, compatibility
  output-protocol.md   Plugin output text format, actions, limits, compatibility
  trust-model.md       Plugin approval, fingerprints, trust checks, approval store
  execution-engine.md  Interpreter resolution, process execution, capture, encoding, cleanup
  scheduling.md        Scheduling, hot reload, reconciliation, publication, shutdown
  roadmap.md           Phased plan
  security.md          Security model and the discovery, output, approval and execution safeguards
```

## Documentation

- [Architecture](docs/architecture.md)
- [Plugin format](docs/plugin-format.md)
- [Output protocol](docs/output-protocol.md)
- [Trust model](docs/trust-model.md)
- [Execution engine](docs/execution-engine.md)
- [Scheduling and hot reload](docs/scheduling.md)
- [Roadmap](docs/roadmap.md)
- [Security model](docs/security.md)
- [Contributing](CONTRIBUTING.md)
- [Reporting vulnerabilities](SECURITY.md)

## License

TrayWisp is released under the [MIT License](LICENSE).
