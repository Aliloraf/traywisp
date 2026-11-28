# Architecture

This document covers how TrayWisp is structured and the rules that hold it together. Each section says whether it describes something **implemented** or **planned**. As of Phase 6, the project structure, build configuration, dependency enforcement, plugin models, metadata parsing, discovery, the text output parser, plugin trust and approval, the script execution engine, scheduling with hot reload, the command-line interface and the bounded run history are implemented. The CLI runs one plugin per invocation through the engine; no long-lived host uses the scheduler yet, and nothing displays plugin output in the tray or a status bar.

## Projects

| Project | Target | Output | Purpose |
| --- | --- | --- | --- |
| `TrayWisp.Core` | `net10.0` | Class library | Plugin models, file-name and metadata parsing, text output parsing, trust models and policy, execution statuses, diagnostics and output decoding (implemented). No file system, process, network, clock or environment access. |
| `TrayWisp.Runtime` | `net10.0` | Class library | Plugin discovery, plugin trust and the approval store, the execution engine, scheduling and hot reload, the run history and read-only host checks (implemented). |
| `TrayWisp.Cli` | `net10.0` | `traywisp.exe` | Command-line interface (implemented): `list`, `inspect`, `approve`, `revoke`, `status`, `run`, `validate-output`, `doctor`, `logs`, help and version. See [cli.md](cli.md). |
| `TrayWisp.Desktop` | `net10.0-windows` (WPF) | `TrayWisp.Desktop.exe` | Desktop host. Implemented: an informational window. Planned: system tray, plugin management interface, customizable status bar. |
| `TrayWisp.Core.Tests` | `net10.0` | xUnit v3 test app | Tests for Core. |
| `TrayWisp.Runtime.Tests` | `net10.0` | xUnit v3 test app | Tests for Runtime. |
| `TrayWisp.Cli.Tests` | `net10.0` | xUnit v3 test app | Tests for the CLI: in-process through its internals, and process-level with the built `traywisp.exe`, which is copied next to the tests. References the CLI, Runtime and the test helper. |
| `TrayWisp.TestHelper` | `net10.0` | Console app | A controlled stand-in interpreter that the execution tests configure explicitly. Test-only, referenced only by `TrayWisp.Runtime.Tests` and `TrayWisp.Cli.Tests`, never shipped. |

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
2. **Compiled assemblies.** `DependencyRulesTests` in the Core and Runtime test projects, and `TheCliReferencesOnlyCoreAndRuntime` in `TrayWisp.Cli.Tests`, inspect the compiled assemblies' references. They catch references added by other means, such as a direct `Reference` to a built DLL. The CLI assembly exports no public types.
3. **Core isolation.** `CoreIsolationTests` reads the metadata of `TrayWisp.Core.dll` and fails if Core references file system types (`File`, `Directory`, `FileStream`, `Path`, …), process types, `Stopwatch`, `TimeProvider`, timers, `DateTime.Now`/`UtcNow`, `Environment.TickCount`, environment variable and folder lookups (`GetEnvironmentVariable`, `ExpandEnvironmentVariables`, `GetFolderPath`, `CurrentDirectory`), network types (`HttpClient`, `Dns`, `Socket`, `WebRequest`, `WebClient`), `System.Drawing.Color`, XML readers or `Regex`. For trust, it also rejects `RandomAccess`, `SafeFileHandle`, `Marshal`, the SHA-256 and incremental hashing types (hashing file content belongs to Runtime) and `Guid.NewGuid` (approval generations are created in Runtime). For execution, it also rejects `SafeProcessHandle`, pipe streams, `Console`, `Environment.SystemDirectory`, `ProcessPath` and `ProcessId`, `Task.Delay` and `Task.Run`, and checks that no type in `TrayWisp.Core.Execution` is disposable or returns a task or stream. The same purity check covers `TrayWisp.Core.Scheduling`, whose policy reads no clock: Runtime measures time and passes durations in.
4. **Execution and scheduling boundaries.** `SchedulerApiTests` checks that `TrayWisp.Runtime.Scheduling` exports only the documented types, that the scheduler is built only from an execution service and a root (no public seam, delegate or trust service parameter), that snapshot types cannot be created or changed outside Runtime, and that publishable output is reachable only through `ScheduledPlugin.Output`. `ExecutionApiTests` checks that `TrayWisp.Runtime.Execution` exports only the documented types, that `RunAsync` is the only public way to run a plugin, that no public Runtime member exposes a `Process`, `ProcessStartInfo` or handle, that result types have no public constructors, and that the authorization lease is internal.
5. **Diagnostics boundary.** `HostDiagnosticsTests` checks that `TrayWisp.Runtime.Diagnostics` exports only the documented types, that its result types cannot be created or changed outside Runtime, and that no member takes a delegate.

The CLI tests use `InternalsVisibleTo` on Runtime (for the engine's internal test hooks) and on the CLI; no production assembly sees another's internals.

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
- A discovered plugin is neither trusted nor runnable. Approval (Phase 3) builds on `PluginId`. Execution (Phase 4) builds on the root, the `PluginId` and the trust check, never on a discovery descriptor.

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
- The execution engine (Phase 4) caps captured stdout bytes and decodes them. The parser only sees decoded text and applies its own character-based limits. The parser cannot detect a cut, because a cut value can still be valid (`param1=document.txt` cut to `param1=doc`), and it never infers truncation from a missing final line feed. The engine therefore passes `isTruncated: true` whenever capture was incomplete (byte limit, timeout, cancellation, read failure), and never passes incomplete stdout as complete input.
- Invisible-label detection (white space, format characters, default-ignorable code points, U+2800) is best effort. It cannot guarantee that every font renders every accepted label visibly.
- A successful parse is not authorization. It never permits running a command, opening a URL or refreshing a plugin.
- Executing actions is out of scope. Phase 7 must route every action through the trust check (with the producing run's approval identity) and the execution safeguards.

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

Core types live in `TrayWisp.Core.Trust`: `ContentFingerprint`, `ApprovalGeneration`, `PluginRoot`, `PluginTrustKey`, `PluginFingerprint`, `PluginApprovalIdentity`, `PluginApproval`, `PluginTrustStatus`, `PluginTrustPolicy` (with `PluginTrustEvaluation` and `PluginApprovalAction`), and `TrustDiagnostic`/`TrustDiagnosticCode`. Runtime types live in `TrayWisp.Runtime.Trust`: `PluginTrustService`, `PluginTrustOptions` and the four result types and their status enums. Everything else (`ApprovalStore`, `StoreLock`, `ApprovalStoreSerializer`, `PendingInvalidations`, `PluginFileInspector`, `TrustPaths`, `WindowsFileApi`, and since Phase 4 `PluginExecutionLease` and `CheckTrustForExecution`) is internal. An approved `PluginTrustDecision` carries `ValidatedFilePath`, the exact file the check hashed; it is produced only by Runtime from the open file handle, never from caller input.

Design choices:

- Core holds immutable values and pure decisions. Runtime holds every file system access, hashing, the clock (`TimeProvider`), GUID creation and the store.
- Result types have internal constructors, so only Runtime can produce an approved decision. There is a single authoritative check and no bypass.
- Every operation that reads or changes approvals holds the store lock for its whole duration and reads the store from disk. There is no cache of approvals. The only state in a service instance is its set of unsaved invalidations, which can only deny; hosts therefore keep one long-lived instance per store (see [trust-model.md](trust-model.md#unsaved-invalidations)).
- Discovery's own listing and classification helpers are reused (made `internal`), so a targeted trust check applies the same exclusion and collision rules as a full scan, without rescanning unrelated plugins.
- Windows-specific APIs are confined to `WindowsFileApi`: five `kernel32` functions through `LibraryImport`, which is why `TrayWisp.Runtime` sets `AllowUnsafeBlocks`. Callers are annotated `[SupportedOSPlatform("windows")]`, and on other platforms every trust operation fails with `TW501`.
- `System.Text.Json` (in the shared framework) is the only serializer. No packages were added.

## Script execution (implemented)

The engine, its policies and limits are specified in [execution-engine.md](execution-engine.md). In short:

```text
Runtime: PluginExecutionService(trustService, options).RunAsync(root, id, runOptions, token)
  ├─ language from the identity's extension (Core: PluginLanguages); Bash and batch → UnsupportedLanguage
  ├─ InterpreterResolver: configured path | Program Files\PowerShell\7 | validated PATH | system directory,
  │    validated with WindowsFileApi/TrustPaths in order: syntax → drive type → directory chain (held) → file,
  │    (no links, aliases, remote or plugin-local paths, PE header), never run; PATH cut only between entries
  ├─ InterpreterCommand: fixed arguments, filtered environment (execution policy variable preserved), no shell
  ├─ PluginTrustService.CheckTrustForExecution → Approved + PluginExecutionLease (file and directories held, store released)
  ├─ PluginProcessRun: job object, Process.Start(validated path), then one cleanup boundary:
  │    stdin closed, StreamCapture ×2 (dedicated threads, byte limits, interruptible reads), exit monitoring,
  │    RunArbiter (earliest recorded event: completion, deadline, cancellation, overflow, failures),
  │    termination (job + process tree) or, after a normal exit, termination of remaining tracked descendants,
  │    with bounded, confirmed cleanup
  └─ Core: PluginOutputDecoder (strict) → PluginOutputParser.Parse(text, isTruncated)
→ PluginExecutionResult (TW600–TW699 + trust + parser diagnostics)
```

Core types live in `TrayWisp.Core.Execution`: `PluginExecutionStatus`, `ExecutionTerminationReason`, `ProcessCleanupStatus`, `InterpreterKind`, `InterpreterSource`, `ExecutionDiagnostic`/`ExecutionDiagnosticCode`, `PluginOutputDecoder`, `StandardOutputEncoding`. Runtime types live in `TrayWisp.Runtime.Execution`: `PluginExecutionService`, `PluginExecutionOptions`, `PluginRunOptions`, `PluginExecutionResult`, `PluginInterpreter`, `PluginProcessCleanup`. Everything else is internal.

Design choices:

- One entry point, one pipeline. The runner can only be called with a lease, and a lease only comes from an approved authoritative check. Results have internal constructors; output is set only for successful, authorized runs.
- Non-authorizing work (resolution, start information) happens before the trust check, so the check is as late as possible.
- Windows-specific code (`WindowsJobObject`, the thread cancellation in `StreamCapture`) is guarded and annotated like the trust code; on other platforms every run returns `UnsupportedPlatform`.
- A run owns its tracked processes: kill-on-close is never cleared, and descendants left in the job after a normal exit are terminated and confirmed before output is published.
- Why a run ended is decided by `RunArbiter` from monotonic timestamps recorded where each event is observed, so a late supervising continuation cannot change the outcome.
- No new packages. The six job-object functions and four thread functions are `LibraryImport`s in Runtime.

## Scheduling and hot reload (implemented)

The scheduler, its policies, limits and guarantees are specified in [scheduling.md](scheduling.md). In short:

```text
Runtime: PluginScheduler(executionService, rootPath, options)       one per root; trust = executionService's trust service
  ├─ coordinator: one lock over in-memory state (no I/O, no callbacks under it), coalesced pump, one timer,
  │    immutable snapshots with revisions, WaitForChangeAsync (no consumer code runs on scheduler threads)
  ├─ reconciliation pass (one at a time): root validation (drive type → directory chain → short names) → watcher
  │    → PluginDiscovery.Discover → apply (complete: add/update/remove; incomplete: uncertain) → CheckTrust per plugin
  ├─ FileSystemWatcher: classified hints → debounced rescan (250 ms quiet, ≤ 1 s) + targeted trust checks
  ├─ attempts (≤ MaxConcurrency, one per plugin identity): RunAsync with the expected approval → output CheckTrust
  │    → atomic commit if the attempt is still current → publish, or reject and withdraw
  └─ cleanup registry: unresolved cleanup blocks the identity for the instance's lifetime and holds a slot
Core: PluginSchedulingPolicy (fixed-delay, backoff, eligibility, status, output cost), states, TW700–TW799
```

Core types live in `TrayWisp.Core.Scheduling`; Runtime types in `TrayWisp.Runtime.Scheduling`: `PluginScheduler`, `PluginSchedulerOptions` and the snapshot types. Everything else is internal: the operations seam (`SchedulerOperations`, replaced by fakes in tests), the watcher adapter, the coordinator's state, the test hooks, and two small internal extensions of earlier phases: `PluginExecutionService.RunAsync(…, expectedApproval, …)` with `PluginTrustService.CheckTrustForExecution(…, expectedApproval, …)`, so a run selected under one approval generation cannot launch under another, and `PluginExecutionService.TrustService`, so the scheduler checks trust with exactly the engine's trust service. `WindowsFileApi` gained one query (`FILE_ID_INFO`, through the existing `GetFileInformationByHandleEx`) to notice a replaced root.

Design choices:

- Scheduler state never authorizes. Eligibility only decides whether the engine is asked; the engine's launch-time check authorizes each launch, and a fresh `CheckTrust(result.Approval)` precedes each publication.
- Separate state dimensions (eligibility, activity, presence) with a derived display status, instead of one large state enumeration.
- Lifecycle revisions per plugin and per-check epochs reject every late or stale result atomically at commit time.
- Pure decisions (delays, backoff, eligibility precedence, output cost) are in Core and unit-tested without a clock; Runtime owns time (`TimeProvider`, injected internally for tests), timers, the watcher and all I/O.
- No new packages; no interfaces; deterministic tests use an internal abstract seam and a manual `TimeProvider` written for the tests.

## Command-line interface and diagnostics (implemented)

The commands, options, exit codes, JSON schema and terminal rules are specified in [cli.md](cli.md), the run history in [diagnostics.md](diagnostics.md). In short:

```text
traywisp.exe: Program (console streams, Ctrl+C → ConsoleCancellation) → CliApplication.RunAsync(args, environment, token)
  ├─ CommandLineParser: exact commands and options, validated values (PluginId, ContentFingerprint, PluginRoot, ranges)
  │    → help | version | usage error | CliRequest          (nothing touches the file system before this is complete)
  ├─ paths: --root/--data-dir or %LOCALAPPDATA%\TrayWisp; overlap check (syntax); PluginRootValidation.Check;
  │    StorageLocation.Check of the trust directory before the trust service is created
  ├─ commands, each a thin orchestration of existing services:
  │    list → PluginDiscovery                inspect → PluginDiscovery + PluginTrustService.Inspect
  │    approve/revoke/status → PluginTrustService (one instance per invocation)
  │    run → PluginExecutionService.RunAsync → CheckTrust(result.Approval) → PluginRunHistory.Record
  │    validate-output → bounded stdin → PluginOutputDecoder → PluginOutputParser
  │    doctor → PluginRootValidation, StorageLocation, InterpreterLookup      logs → PluginRunHistory.Read
  └─ CommandResult → one report: text through TerminalText, or one versioned JSON document (System.Text.Json)
Runtime.Diagnostics: PluginRunHistory (history.json + its own lock, strict parse, atomic replacement, 200 records / 1 MiB),
  StorageLocation (read-only), InterpreterLookup (the engine's InterpreterResolver, nothing launched)
Runtime.Plugins: PluginRootValidation (shared with the scheduler)
```

Runtime types: `TrayWisp.Runtime.Diagnostics` holds `PluginRunHistory`, `PluginRunHistoryOptions`, `PluginRunRecord`, `RecordedDiagnostic`, `RunPublication`, the read and write results and statuses, `StorageLocation` with its report and statuses, `InterpreterLookup` with its result and status, and `StorageDiagnostic`/`StorageDiagnosticCode` (`TW800`–`TW849`); `TrayWisp.Runtime.Plugins` gained `PluginRootValidation`, `PluginRootCheckResult` and `PluginRootStatus`. Everything else (`StorageFiles`, `RunHistorySerializer`, the history test hooks) is internal. The CLI's own types are internal; its diagnostics are `TW850`–`TW899`.

Design choices:

- No second implementation of discovery, trust, interpreter resolution, execution or parsing. The shared pieces the CLI needed were extracted or exposed in Runtime: the scheduler's root validation (`PluginRootValidation.Validate`, now used by both), a read-only view of the engine's resolver, and the storage checks built on the trust store's `TrustPaths` and `WindowsFileApi` primitives. The approval store and its serializer are unchanged.
- The run history lives in Runtime so a future desktop host can reuse it. It never authorizes anything and stores only structured summaries.
- The CLI takes its streams, `%LOCALAPPDATA%` and service factories from an internal `CliEnvironment`, so the tests run commands in-process without global console state; the only execution seam is a factory for `PluginExecutionService`, which authorizes every run itself.
- No new packages: the parser and the JSON output use only the standard library.

## Planned component responsibilities

Nothing in this section is implemented. It records the intended direction so that early decisions stay consistent with it. Phase numbers refer to [roadmap.md](roadmap.md).

**Core**
- A versioned JSON output schema and parser (Phase 10).
- Pure functions only. No file system, process or clock access, so behaviour is fully unit-testable.

**Runtime**
- Calling the trust check, with the producing run's approval identity, before every output-driven action (Phase 7).
- Recording scheduled runs in the run history, when a long-lived host uses the scheduler (the CLI records its runs since Phase 6; the scheduler keeps its run summaries in memory only).

**Desktop**
- Notification area (system tray) icons with menus rendered from the Core menu model, plus output-driven actions (Phase 7).
- Plugin management interface: approval, enable/disable, manual runs, diagnostics (Phase 8).
- Customizable status bar for persistent text widgets (Phase 9, see the next section).

The CLI launches plugins only through `PluginExecutionService`, and the desktop host will too (scheduled and manual refreshes through `PluginScheduler`, which uses it), so no execution path, whether scheduled, manual, CLI or an output-driven action, can bypass the trust check. The [security model](security.md) describes the checks and their limits.

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

- **Race-free job assignment.** Phase 4 assigns each plugin process to a job object after `Process.Start` (see [execution-engine.md](execution-engine.md#windows-job-objects)). Creating processes inside the job from the start would need a native launcher; whether that is worth its complexity is undecided.
- **Status bar surface type:** app bar or floating window, and how it behaves with multiple monitors (Phase 9).
- **Interaction between the CLI and a running desktop instance**, if any.
