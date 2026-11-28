# TrayWisp

TrayWisp is a planned open-source, lightweight, script-powered desktop customization platform for Windows. When it is ready, you will place a script in a plugin folder and approve it explicitly. TrayWisp will then run it on a schedule and show its output in the Windows system tray, and later in a customizable status bar. A plugin management interface will cover approval and diagnostics. Today TrayWisp has a usable **command-line interface**, `traywisp`, on top of its libraries: it discovers plugin files and their metadata, shows the SHA-256 fingerprint of a plugin for review, records explicit, content-bound approvals and revocations, makes authoritative trust checks, runs one approved PowerShell, Python or JavaScript plugin once through its execution engine and shows the parsed output as inert data, validates plugin output, diagnoses the host configuration, and keeps a bounded local history of run summaries. A scheduler with hot reload exists as a library. **Nothing displays plugins in the tray yet, and no long-running host uses the scheduler.**

TrayWisp takes ideas from SwiftBar, xbar, Crossbar, YASB and Zebar. It is an independent implementation, not affiliated with any of those projects, and contains none of their source code.

> **Status: early development.** Phase 0 (project foundation), Phase 1 (plugin models, discovery and metadata), Phase 2 (text output parser), Phase 3 (plugin trust and approval), Phase 4 (script execution engine), Phase 5 (scheduling and hot reload) and Phase 6 (CLI and plugin diagnostics) are implemented. The system tray, the plugin management interface and the status bar are planned for later phases. See [docs/roadmap.md](docs/roadmap.md).

## What exists today

| Area | Status |
| --- | --- |
| Solution structure, shared build configuration, Windows CI | Implemented |
| Build-time enforcement of project dependency rules | Implemented |
| `traywisp` CLI | Implemented: `list`, `inspect`, `approve`, `revoke`, `status`, `run` (one approved plugin, once), `validate-output`, `doctor`, `logs`, with versioned JSON output, documented exit codes and terminal-safe text. See [docs/cli.md](docs/cli.md). |
| Desktop application (WPF) | Implemented: an informational window only, no tray icon |
| Plugin models, discovery and metadata | Implemented: `name.interval.ext` file names, language classification, SwiftBar/xbar-compatible metadata tags, read-only recursive discovery. See [docs/plugin-format.md](docs/plugin-format.md). Exposed by `traywisp list` and `inspect`. |
| SwiftBar/xbar-compatible text output parser | Implemented: header titles, nested menus and separators, formatting parameters, and URL, command and refresh actions parsed into inert data, with strict quoting, limits and diagnostics. A documented subset, not full compatibility. See [docs/output-protocol.md](docs/output-protocol.md). Used by the engine and by `traywisp run` and `validate-output`, which show the result as inert text or JSON; nothing displays it in the tray yet. |
| Plugin trust and approval | Implemented: explicit approval of a reviewed SHA-256 fingerprint of the whole file, bound to the plugin root and identity; one authoritative trust check that rehashes the file every time and invalidates approvals when content changes; stale-approval detection for future output actions; strict path validation (no links, aliases, network or hidden paths); a versioned, locked, atomically replaced JSON store in `%LOCALAPPDATA%\TrayWisp\trust`. See [docs/trust-model.md](docs/trust-model.md). Exposed by `traywisp inspect`, `approve`, `revoke` and `status`. |
| Script execution engine | Implemented: runs an approved `.ps1`, `.py` or `.js` plugin once, after an authoritative trust check made immediately before the launch, with a deterministically resolved installed interpreter (PowerShell 7 or Windows PowerShell, Python 3, Node.js), fixed arguments and no shell, a filtered environment, a timeout, cancellation, 1 MiB stdout and 64 KiB stderr limits, strict UTF-8 decoding, the output parser, and job-object and process-tree termination. Bash and batch are not run. See [docs/execution-engine.md](docs/execution-engine.md). Exposed by `traywisp run`. |
| Scheduling and hot reload | Implemented (library only): one scheduler per plugin folder runs approved interval plugins on their schedule and manual plugins on request, through the execution engine and its launch-time trust check, with a global concurrency limit (default 4), no overlapping runs of one plugin, failure backoff, a debounced file system watcher plus periodic reconciliation, cancellation and output withdrawal when a plugin is changed, revoked, disabled or removed, permanent blocking of plugins whose process cleanup could not be confirmed, immutable snapshots and bounded shutdown. See [docs/scheduling.md](docs/scheduling.md). Library only: the CLI runs one plugin per invocation and does not use it; no tray icon or status bar. |
| Bounded local diagnostics | Implemented: a run history of structured summaries (no output, no messages, no paths of roots or interpreters) in `%LOCALAPPDATA%\TrayWisp\diagnostics`, at most 200 records and 1 MiB, locked, atomically replaced, never sent anywhere. See [docs/diagnostics.md](docs/diagnostics.md). |
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

## Quick start with the CLI

The commands below use PowerShell and the default locations under `%LOCALAPPDATA%\TrayWisp`. Nothing is created automatically except TrayWisp's own trust and diagnostics files when they are first needed.

1. **Build and locate the CLI** (from the repository root):

   ```powershell
   dotnet build src/TrayWisp.Cli --configuration Release
   Set-Alias traywisp "$PWD\src\TrayWisp.Cli\bin\Release\net10.0\traywisp.exe"
   traywisp --help
   ```

2. **Create the plugin directory** explicitly (TrayWisp never creates it):

   ```powershell
   New-Item -ItemType Directory -Path "$env:LOCALAPPDATA\TrayWisp\plugins"
   ```

3. **Place a plugin** in it, for example the JavaScript sample (needs Node.js) or the Python sample (needs Python 3):

   ```powershell
   Copy-Item .\plugins\examples\hello.js "$env:LOCALAPPDATA\TrayWisp\plugins\"
   ```

   PowerShell samples such as `clock.1m.ps1` run under your effective PowerShell execution policy, which TrayWisp neither changes nor bypasses. On a Windows client with the default policy, PowerShell scripts are refused; use a policy your organisation permits or a Python or JavaScript plugin. `traywisp doctor` shows which interpreters TrayWisp would use.

4. **List** the discovered plugins (discovery only; nothing runs):

   ```powershell
   traywisp list
   ```

5. **Inspect** the plugin to see its metadata and the SHA-256 fingerprint of the whole file:

   ```powershell
   traywisp inspect hello.js
   ```

6. **Review the script.** Read the file shown as "Validated file" (for example `Get-Content "$env:LOCALAPPDATA\TrayWisp\plugins\hello.js"`). Approval lets it run as you, with all of your access: it is consent, not a sandbox.

7. **Approve exactly the fingerprint you reviewed** (copy it from `inspect`). If the file changed in between, nothing is approved:

   ```powershell
   traywisp approve hello.js --sha256 <fingerprint shown by inspect>
   ```

8. **Run it once.** The launch-time trust check runs again, and the output is shown only if a check after the run still confirms the approval. Actions in the output are shown, never executed:

   ```powershell
   traywisp run hello.js
   ```

9. **Check its status and the recent run history:**

   ```powershell
   traywisp status hello.js
   traywisp logs
   ```

10. **Revoke** the approval when you no longer want it to run (the file is not touched):

    ```powershell
    traywisp revoke hello.js
    ```

Every command accepts `--json` for one versioned JSON document on standard output, and returns documented exit codes (`0` success, `4` approval denied, `9` cleanup incomplete, …). See [docs/cli.md](docs/cli.md) for every command, option, exit code and the JSON schema.

## Plugin files

A plugin is a script whose file name follows `name[.interval].extension`, for example `clock.5s.ps1` (every 5 seconds) or `hello.js` (manual refresh). PowerShell, Python and JavaScript plugins can be executed by the engine. Bash and batch files are recognized but their execution is deferred. Optional metadata goes in comments (tags on code lines are ignored; see [plugin-format.md](docs/plugin-format.md#comments) for the exact, line-based rule):

```powershell
# <traywisp.title>Clock</traywisp.title>
# <traywisp.version>1.0</traywisp.version>
```

`xbar.*`, `bitbar.*` and `swiftbar.*` tags are also read. TrayWisp supports a documented subset of the SwiftBar/xbar conventions. It is not fully compatible. See [docs/plugin-format.md](docs/plugin-format.md) for the grammar, limits, diagnostics and compatibility notes, and [plugins/examples](plugins/examples) for sample scripts. `traywisp run` can run the samples once after approval; no tray displays them yet.

## Repository layout

```text
src/
  TrayWisp.Core/       Platform-independent plugin models, file-name, metadata and output parsers, trust models
  TrayWisp.Runtime/    Plugin discovery, trust, the execution engine, the scheduler and the run history
  TrayWisp.Cli/        Command-line application (traywisp.exe): commands, parser, terminal-safe text and JSON output
  TrayWisp.Desktop/    WPF desktop application (TrayWisp.Desktop.exe)
tests/
  TrayWisp.Core.Tests/
  TrayWisp.Runtime.Tests/
  TrayWisp.Cli.Tests/  In-process and process-level tests of traywisp
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
  cli.md               The traywisp commands, options, exit codes, JSON output, terminal safety
  diagnostics.md       The bounded local run history and the read-only host checks
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
- [Command-line interface](docs/cli.md)
- [Diagnostics storage](docs/diagnostics.md)
- [Roadmap](docs/roadmap.md)
- [Security model](docs/security.md)
- [Contributing](CONTRIBUTING.md)
- [Reporting vulnerabilities](SECURITY.md)

## License

TrayWisp is released under the [MIT License](LICENSE).
