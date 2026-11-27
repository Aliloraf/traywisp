# Architecture

This document covers how TrayWisp is structured and the rules that hold it together. Each section says whether it describes something **implemented** or **planned**. As of Phase 1, the project structure, build configuration, dependency enforcement, and plugin models, metadata parsing and discovery are implemented. Nothing executes plugins yet.

## Projects

| Project | Target | Output | Purpose |
| --- | --- | --- | --- |
| `TrayWisp.Core` | `net10.0` | Class library | Plugin models, file-name and metadata parsing (implemented), output protocol parsing (planned). No file system, process or clock access. |
| `TrayWisp.Runtime` | `net10.0` | Class library | Plugin discovery (implemented), process execution and scheduling (planned). |
| `TrayWisp.Cli` | `net10.0` | `traywisp.exe` | Command-line interface. Implemented: `--help`, `--version`. |
| `TrayWisp.Desktop` | `net10.0-windows` (WPF) | `TrayWisp.Desktop.exe` | Desktop host. Implemented: an informational window. Planned: system tray, plugin management interface, customizable status bar. |
| `TrayWisp.Core.Tests` | `net10.0` | xUnit v3 test app | Tests for Core. |
| `TrayWisp.Runtime.Tests` | `net10.0` | xUnit v3 test app | Tests for Runtime. |

`TrayWisp.Core` and `TrayWisp.Runtime` are platform-independent so that most logic, including protocol parsing, scheduling decisions and output bounding, can be unit-tested without a Windows desktop session. Anything that needs WPF or the Windows shell belongs in `TrayWisp.Desktop`.

The CLI and desktop executables have deliberately different file names. Windows file names are case-insensitive, so `traywisp.exe` and `TrayWisp.exe` would collide in a shared installation directory, and NuGet restore also rejects project names that differ only by case.

## Dependency rules (implemented)

```text
TrayWisp.Cli ─────┐
                  ├──► TrayWisp.Runtime ──► TrayWisp.Core
TrayWisp.Desktop ─┘           (Cli and Desktop may also reference Core directly)
```

- `TrayWisp.Core` references no other TrayWisp project.
- `TrayWisp.Runtime` may reference `TrayWisp.Core` only.
- `TrayWisp.Cli` and `TrayWisp.Desktop` may reference `TrayWisp.Core` and `TrayWisp.Runtime`, but never each other.

Enforcement:

1. **Build time.** [`Directory.Build.targets`](../Directory.Build.targets) defines the allowed project references for each production project. Any other `ProjectReference` fails the build with error `TW0001`. To change the architecture, edit that file and this document in the same change.
2. **Compiled assemblies.** `DependencyRulesTests` in each test project inspects the compiled assembly's references. It catches references added by other means, such as a direct `Reference` to a built DLL.
3. **Core isolation.** `CoreIsolationTests` reads the metadata of `TrayWisp.Core.dll` and fails if Core references file system types (`File`, `Directory`, `FileStream`, `Path`, …), process types, `Stopwatch`, `TimeProvider`, timers, `DateTime.Now`/`UtcNow`, `Environment.TickCount`, `HttpClient`, XML readers or `Regex`.

If the CLI later needs to talk to a running desktop instance, the CLI will not reference the desktop project. Any such interaction would go through a protocol defined in Core and implemented by both sides (not designed yet).

## Build configuration (implemented)

| File | Purpose |
| --- | --- |
| `global.json` | Pins the .NET SDK (10.0.404, rolling forward to later .NET 10 feature bands) and selects Microsoft.Testing.Platform for `dotnet test`. |
| `Directory.Build.props` | Shared settings: nullable reference types, implicit usings, deterministic builds, `ContinuousIntegrationBuild` on GitHub Actions, recommended analyzers, code style enforced in build, warnings as errors (NuGet vulnerability advisories stay warnings), version `0.1.0-dev`. |
| `Directory.Build.targets` | Project dependency rule (`TW0001`). |
| `Directory.Packages.props` | Central package versions. The only external package is `xunit.v3`, used by the test projects. |
| `.editorconfig` | Formatting and C# style rules. Some are enforced as build errors through `EnforceCodeStyleInBuild`. |
| `.github/workflows/ci.yml` | Restore, Release build and tests on `windows-latest`. |

The production projects have no third-party package dependencies. New dependencies need a clear justification (see [CONTRIBUTING.md](../CONTRIBUTING.md)).

## Plugin model and discovery (implemented)

The file format and every rule are specified in [plugin-format.md](plugin-format.md). In short:

```text
Runtime: PluginDiscovery.Discover(root, options, token)
  ├─ lists directories (sorted, no reparse points, no hidden entries), charging each
  │  enumerated entry to one global MaxEntries budget, with a one-entry look-ahead
  ├─ withholds every entry of a listing truncated by the budget (collisions unknowable)
  ├─ Core: PluginFileNameParser.Parse(fileName)        → PluginFileName | rejection | not a candidate
  ├─ excludes case-variant names (identity collisions) before reading anything
  ├─ re-checks attributes immediately before listing a directory or opening a file
  ├─ reads at most 64 KiB + 1 byte of each candidate (read-only, shared)
  ├─ Core: PluginMetadataParser.Parse(bytes, language, truncated) → PluginMetadata + diagnostics
  ├─ re-checks each listed directory and read file again afterwards for link swaps
  └─ builds PluginDescriptor(PluginId, PluginFileName, PluginMetadata)
→ PluginDiscoveryResult(Plugins, Diagnostics, IsComplete, EntriesInspected, RootPath)
```

Core types live in `TrayWisp.Core.Plugins`:

| Type | Role |
| --- | --- |
| `PluginId` | Root-relative path with `/` separators. Case-preserving, compared ordinal case-insensitively. |
| `PluginFileName`, `PluginFileNameParser` | `name[.interval].extension` grammar. |
| `RefreshInterval`, `RefreshIntervalUnit` | Validated 250 ms – 30 day interval. |
| `PluginLanguage`, `PluginExecutionSupport`, `PluginLanguages` | Extension classification: planned vs deferred execution. |
| `PluginMetadata`, `PluginMetadataParser` | Optional, inert metadata from `traywisp`/`swiftbar`/`xbar`/`bitbar` tags on comment-shaped lines (line-based, not a tokenizer). |
| `PluginDescriptor` | Identity + file name + metadata + metadata diagnostics + display title. |
| `PluginDiagnostic`, `PluginDiagnosticCode`, `PluginDiagnosticSeverity` | Structured diagnostics with stable codes. |
| `PluginDiagnosticText` | Length-limits and escapes untrusted text (names, paths, system messages) in diagnostic messages and in `PluginDiagnostic.ToString()`, whose `Path` is otherwise kept raw. |

Runtime types live in `TrayWisp.Runtime.Plugins`: `PluginDiscovery`, `PluginDiscoveryOptions` (traversal limits) and `PluginDiscoveryResult`.

Design choices:

- Models are immutable: get-only properties and `ImmutableArray`. Types whose invariants matter (`RefreshInterval`, `PluginFileName`, `PluginId`) can only be created through validating factories or parsers.
- Parsers never throw on malformed input. They return diagnostics. Exceptions are reserved for caller errors (for example a relative plugin root) and cancellation.
- Core receives bytes, never paths. Runtime alone touches the file system, which keeps the parsers fully unit-testable.
- No dependency injection, no interfaces without a second implementation, and no new packages.
- A discovered plugin is neither trusted nor runnable. Approval (Phase 3) and execution (Phase 4) will build on `PluginId` and `PluginDescriptor`.

## Planned component responsibilities

Nothing in this section is implemented. It records the intended direction so that early decisions stay consistent with it. Phase numbers refer to [roadmap.md](roadmap.md).

**Core**
- A parser for SwiftBar/xbar-compatible text output, turning it into a structured menu model. Action parameters are parsed into data, not executed (Phase 2).
- A versioned JSON output schema and parser (Phase 10).
- Pure functions only. No file system, process or clock access, so behaviour is fully unit-testable.

**Runtime**
- An approval store, and a single trust check that every execution path uses (Phase 3).
- Interpreter resolution by file extension (for example PowerShell for `.ps1`, Python for `.py`), resolved to absolute paths. Only interpreters already installed on the machine are used. Bash and batch execution is deferred (Phase 4).
- Process execution (Phase 4):
  - The interpreter is launched directly with explicit arguments, never by evaluating a shell command string.
  - Each run has a timeout, bounded capture of stdout and stderr, and supports cancellation.
  - Process-tree termination is best effort.
- Scheduling and hot reload: interval-based refresh from `RefreshInterval`, no overlapping runs of the same plugin, a global concurrency limit, and reaction to added, changed or removed plugin files (Phase 5).
- Diagnostics records for each run: start time, duration, exit code, timeout, truncation, and the tail of stderr (Phases 4 and 6).

**Desktop**
- Notification area (system tray) icons with menus rendered from the Core menu model, plus output-driven actions (Phase 7).
- Plugin management interface: approval, enable/disable, manual runs, diagnostics (Phase 8).
- Customizable status bar for persistent text widgets (Phase 9, see the next section).

**CLI**
- Commands to list, validate, approve or revoke, run once and inspect diagnostics (Phase 6).

The CLI and desktop hosts launch plugins only through Runtime, so no execution path, whether scheduled, manual, CLI or an output-driven action, can bypass the trust check. The [security model](security.md) describes the checks and their limits.

## Platform constraint: the Windows notification area

The macOS menu bar, which SwiftBar and xbar target, lets an app show arbitrary text next to its status item. **The Windows notification area does not.** A tray icon (`Shell_NotifyIcon`) can only show:

- A small icon, 16×16 pixels at 100% scaling and scaled with DPI. A plugin could render one or two very short glyphs into it, but that is not a general text display.
- A tooltip, shown only on hover and limited to 128 characters.
- A context menu or flyout, shown only on click.
- Notifications, which are transient.

Other constraints:

- On Windows 11, new tray icons go into the overflow flyout by default. Only the user can choose to show an icon on the taskbar, and there is no supported API for an app to do it.
- Taskbar desk bands, the old way to put custom content in the taskbar, are not supported on Windows 11.

So persistent text widgets need a **separate display surface**. TrayWisp plans an optional, customizable status bar for this (Phase 9). Whether it registers as an application desktop toolbar ("app bar") or floats as a top-most window is undecided. In the tray, TrayWisp will offer an icon, a tooltip and a menu, not persistent text.

## Open design questions

- **Whether and where to use Windows Job Objects.** Best-effort process-tree termination with `Process.Kill(entireProcessTree: true)` is cross-platform and goes in Runtime. A Windows Job Object can also terminate processes that `Process.Kill` misses, including when TrayWisp crashes. It is Windows-specific and has its own limitations. If adopted, the choice is between a small `OperatingSystem.IsWindows()`-guarded code path in Runtime and a host-supplied hook. To be investigated in Phase 4. See [security.md](security.md#process-tree-termination).
- **Status bar surface type:** app bar or floating window, and how it behaves with multiple monitors (Phase 9).
- **Interaction between the CLI and a running desktop instance**, if any.
