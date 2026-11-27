# Architecture

This document covers how TrayWisp is structured and the rules that hold it together. Each section says whether it describes something **implemented** or **planned**. As of Phase 3, the project structure, build configuration, dependency enforcement, plugin models, metadata parsing, discovery, the text output parser, and plugin trust and approval are implemented. Nothing executes plugins or displays their output yet.

## Projects

| Project | Target | Output | Purpose |
| --- | --- | --- | --- |
| `TrayWisp.Core` | `net10.0` | Class library | Plugin models, file-name and metadata parsing, text output parsing, trust models and policy (implemented). No file system, process, network, clock or environment access. |
| `TrayWisp.Runtime` | `net10.0` | Class library | Plugin discovery, plugin trust and the approval store (implemented); process execution and scheduling (planned). |
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
3. **Core isolation.** `CoreIsolationTests` reads the metadata of `TrayWisp.Core.dll` and fails if Core references file system types (`File`, `Directory`, `FileStream`, `Path`, …), process types, `Stopwatch`, `TimeProvider`, timers, `DateTime.Now`/`UtcNow`, `Environment.TickCount`, environment variable and folder lookups (`GetEnvironmentVariable`, `ExpandEnvironmentVariables`, `GetFolderPath`, `CurrentDirectory`), network types (`HttpClient`, `Dns`, `Socket`, `WebRequest`, `WebClient`), `System.Drawing.Color`, XML readers or `Regex`. For trust, it also rejects `RandomAccess`, `SafeFileHandle`, `Marshal`, the SHA-256 and incremental hashing types (hashing file content belongs to Runtime) and `Guid.NewGuid` (approval generations are created in Runtime).

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
- A discovered plugin is neither trusted nor runnable. Approval (Phase 3) builds on `PluginId`. Execution (Phase 4) will build on `PluginDescriptor` and the trust check.

## Output parsing (implemented)

The text format, limits and recovery rules are specified in [output-protocol.md](output-protocol.md). In short:

```text
Core: PluginOutputParser.Parse(decoded stdout, isTruncated)
  ├─ rejects truncated input (TW405), over-long input and too many lines before parsing (fails: no partial output)
  ├─ one pass over LF-separated lines, no regular expressions, no input-wide splitting
  ├─ fails on any over-long physical line (TW410) and on a ~~~ streaming delimiter (TW416)
  ├─ header titles until the first ---, then menu items and separators
  ├─ submenus from -- prefixes, using a stack of open items; orphans and depth jumps are dropped
  ├─ per line: label (first unescaped |), then name=value parameters with "…"/'…' quoting
  ├─ typed formatting (colour, font, size, length, trim, tooltip, dropdown, disabled, checked)
  └─ inert actions: RefreshAction | UrlAction (http/https) | CommandAction (executable + argument list)
→ PluginOutputParseResult(Status, PluginOutput(Titles, Items), Diagnostics TW400–TW499)
```

Types live in `TrayWisp.Core.Output`:

| Type | Role |
| --- | --- |
| `PluginOutputParser` | The parser and its limits. Never throws for any input. |
| `PluginOutputParseResult`, `OutputParseStatus` | Complete, or Failed with `PluginOutput.Empty` when a global limit is exceeded (including the line-length limit), the caller reports truncated input, or the output uses unsupported streaming. Diagnostics are in detection order, which is not strictly line order. |
| `PluginOutput`, `OutputTitle`, `MenuNode`, `MenuItem`, `MenuSeparator` | Immutable title list and menu tree. A title wraps a `MenuItem` without children, plus its `dropdown` preference. |
| `TextFormat`, `TextColor`, `OutputColor` | Formatting preferences with platform-independent RGB colours. |
| `OutputAction`, `RefreshAction`, `UrlAction`, `CommandAction` | Untrusted action requests. |
| `OutputDiagnostic`, `OutputDiagnosticCode` | Diagnostics with line and column, separate from `PluginDiagnostic`, reusing `PluginDiagnosticSeverity` and `PluginDiagnosticText`. |

Design choices:

- Constructors of the model types are internal, so invalid combinations cannot be built: an action on a disabled item or an item with a submenu, a title with children, or a failed result with output.
- Any action problem (malformed syntax, duplicate, conflict, unknown or unsupported behavioural parameter, invalid value) suppresses the whole action. The parser never picks a winner or produces a partial action.
- Global problems fail the whole parse, while problems in single lines and parameters are recovered from: the line is dropped, its parameters are ignored or its action is suppressed, and the rest of the menu is kept.
- Phase 4 will cap captured stdout bytes and decode them. The parser only sees decoded text and applies its own character-based limits. The parser cannot detect a cut, because a cut value can still be valid (`param1=document.txt` cut to `param1=doc`), and it never infers truncation from a missing final line feed. Phase 4 must therefore pass `isTruncated: true` whenever capture was incomplete (byte limit, timeout, cancellation, read failure), and must never pass incomplete stdout as complete input.
- Invisible-label detection (white space, format characters, default-ignorable code points, U+2800) is best effort. It cannot guarantee that every font renders every accepted label visibly.
- A successful parse is not authorization. It never permits running a command, opening a URL or refreshing a plugin.
- Executing actions is out of scope. Phases 3, 4 and 7 must route every action through the trust check and the execution safeguards.

## Plugin trust and approval (implemented)

The model, storage format, validation rules and limitations are specified in [trust-model.md](trust-model.md). In short:

```text
Runtime: PluginTrustService(storePath)            one store file, shared by instances and processes;
  │                                                only state: bounded, deny-only unsaved invalidations
  ├─ root + PluginId syntax → PluginTrustKey       (Core: PluginRoot, PluginTrustKey; no file system access)
  ├─ ApprovalStore: store directory validated and held open, lock file (exclusive, bounded wait,
  │    single-link regular file at its final path) → strict JSON read (all or nothing)
  ├─ PendingInvalidations: deny (and retry removing) approvals this instance could not invalidate on disk
  ├─ PluginFileInspector: drive type, ancestors (no reparse, not remote, not case-sensitive),
  │    root and each path component opened without following links and held open,
  │    discovery's ListDirectory/Classify for collisions, final path via GetFinalPathNameByHandle,
  │    SHA-256 of the whole file (≤ 16 MiB), post-read re-checks and re-listing → validated final path
  ├─ Core: PluginTrustPolicy.Evaluate / EvaluateApproval → status, invalidate?, approval action
  └─ ApprovalStore: temporary file in the same directory, flush, held directory re-checked,
       MoveFileEx (replace, write-through)
→ PluginTrustDecision | PluginInspectionResult | PluginApprovalResult | PluginRevocationResult (TW500–TW599)
```

Core types live in `TrayWisp.Core.Trust`: `ContentFingerprint`, `ApprovalGeneration`, `PluginRoot`, `PluginTrustKey`, `PluginFingerprint`, `PluginApprovalIdentity`, `PluginApproval`, `PluginTrustStatus`, `PluginTrustPolicy` (with `PluginTrustEvaluation` and `PluginApprovalAction`), and `TrustDiagnostic`/`TrustDiagnosticCode`. Runtime types live in `TrayWisp.Runtime.Trust`: `PluginTrustService`, `PluginTrustOptions` and the four result types and their status enums. Everything else (`ApprovalStore`, `StoreLock`, `ApprovalStoreSerializer`, `PendingInvalidations`, `PluginFileInspector`, `TrustPaths`, `WindowsFileApi`) is internal. An approved `PluginTrustDecision` carries `ValidatedFilePath`, the exact file the check hashed; it is produced only by Runtime from the open file handle, never from caller input.

Design choices:

- Core holds immutable values and pure decisions. Runtime holds every file system access, hashing, the clock (`TimeProvider`), GUID creation and the store.
- Result types have internal constructors, so only Runtime can produce an approved decision. There is a single authoritative check and no bypass.
- Every operation that reads or changes approvals holds the store lock for its whole duration and reads the store from disk. There is no cache of approvals. The only state in a service instance is its set of unsaved invalidations, which can only deny; hosts therefore keep one long-lived instance per store (see [trust-model.md](trust-model.md#unsaved-invalidations)).
- Discovery's own listing and classification helpers are reused (made `internal`), so a targeted trust check applies the same exclusion and collision rules as a full scan, without rescanning unrelated plugins.
- Windows-specific APIs are confined to `WindowsFileApi`: five `kernel32` functions through `LibraryImport`, which is why `TrayWisp.Runtime` sets `AllowUnsafeBlocks`. Callers are annotated `[SupportedOSPlatform("windows")]`, and on other platforms every trust operation fails with `TW501`.
- `System.Text.Json` (in the shared framework) is the only serializer. No packages were added.

## Planned component responsibilities

Nothing in this section is implemented. It records the intended direction so that early decisions stay consistent with it. Phase numbers refer to [roadmap.md](roadmap.md).

**Core**
- A versioned JSON output schema and parser (Phase 10).
- Pure functions only. No file system, process or clock access, so behaviour is fully unit-testable.

**Runtime**
- Calling the Phase 3 trust check immediately before every launch and every output-driven action (Phases 4 and 7).
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
