# TrayWisp

TrayWisp is a planned open-source, lightweight, script-powered desktop customization platform for Windows. When it is ready, you will place a script in a plugin folder and approve it explicitly. TrayWisp will then run it on a schedule and show its output in the Windows system tray, and later in a customizable status bar. A plugin management interface will cover approval and diagnostics. **TrayWisp cannot run plugins yet.** It can currently discover plugin files, read their file names and embedded metadata, and parse SwiftBar/xbar-style output text into a menu model, all without executing anything.

TrayWisp takes ideas from SwiftBar, xbar, Crossbar, YASB and Zebar. It is an independent implementation, not affiliated with any of those projects, and contains none of their source code.

> **Status: early development.** Phase 0 (project foundation), Phase 1 (plugin models, discovery and metadata) and Phase 2 (text output parser) are complete. Approval, execution and the system tray are planned for later phases. See [docs/roadmap.md](docs/roadmap.md).

## What exists today

| Area | Status |
| --- | --- |
| Solution structure, shared build configuration, Windows CI | Implemented |
| Build-time enforcement of project dependency rules | Implemented |
| `traywisp` CLI | Implemented: `--help` and `--version` only |
| Desktop application (WPF) | Implemented: an informational window only, no tray icon |
| Plugin models, discovery and metadata | Implemented (library only): `name.interval.ext` file names, language classification, SwiftBar/xbar-compatible metadata tags, read-only recursive discovery. See [docs/plugin-format.md](docs/plugin-format.md). Not yet exposed in the CLI or desktop app. |
| SwiftBar/xbar-compatible text output parser | Implemented (library only): header titles, nested menus and separators, formatting parameters, and URL, command and refresh actions parsed into inert data, with strict quoting, limits and diagnostics. A documented subset, not full compatibility. See [docs/output-protocol.md](docs/output-protocol.md). Not yet connected to execution or any display. |
| Plugin trust and approval | Planned (Phase 3) |
| Script execution, scheduling and hot reload | Planned (Phases 4–5) |
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

Plugins will run as ordinary processes with your user account's privileges. Approving a plugin records your consent to run it. **It is not a sandbox**, and TrayWisp does not promise complete containment of plugin processes. See [docs/security.md](docs/security.md).

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

## Plugin files (format implemented, execution planned)

A plugin is a script whose file name follows `name[.interval].extension`, for example `clock.5s.ps1` (every 5 seconds) or `hello.js` (manual refresh). PowerShell, Python and JavaScript are the planned execution targets. Bash and batch files are recognized but their execution is deferred. Optional metadata goes in comments (tags on code lines are ignored; see [plugin-format.md](docs/plugin-format.md#comments) for the exact, line-based rule):

```powershell
# <traywisp.title>Clock</traywisp.title>
# <traywisp.version>1.0</traywisp.version>
```

`xbar.*`, `bitbar.*` and `swiftbar.*` tags are also read. TrayWisp supports a documented subset of the SwiftBar/xbar conventions. It is not fully compatible. See [docs/plugin-format.md](docs/plugin-format.md) for the grammar, limits, diagnostics and compatibility notes, and [plugins/examples](plugins/examples) for sample scripts. TrayWisp cannot run the samples yet.

## Repository layout

```text
src/
  TrayWisp.Core/       Platform-independent plugin models, file-name, metadata and output parsers
  TrayWisp.Runtime/    Plugin discovery (implemented); execution and scheduling (planned)
  TrayWisp.Cli/        Command-line application (traywisp.exe)
  TrayWisp.Desktop/    WPF desktop application (TrayWisp.Desktop.exe)
tests/
  TrayWisp.Core.Tests/
  TrayWisp.Runtime.Tests/
plugins/
  examples/            Sample plugin scripts (for future execution support)
docs/
  architecture.md      Projects, dependency rules, platform constraints
  plugin-format.md     File names, metadata, discovery, diagnostics, compatibility
  output-protocol.md   Plugin output text format, actions, limits, compatibility
  roadmap.md           Phased plan
  security.md          Plugin trust model, discovery limits and planned execution safeguards
```

## Documentation

- [Architecture](docs/architecture.md)
- [Plugin format](docs/plugin-format.md)
- [Output protocol](docs/output-protocol.md)
- [Roadmap](docs/roadmap.md)
- [Security model](docs/security.md)
- [Contributing](CONTRIBUTING.md)
- [Reporting vulnerabilities](SECURITY.md)

## License

TrayWisp is released under the [MIT License](LICENSE).
