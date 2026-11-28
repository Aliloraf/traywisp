# Command-line interface

This document specifies `traywisp`, the command-line host: its commands and options, locations, the approval workflow, execution and output validation, the exit codes, the versioned JSON output and the terminal safety rules.

**Status (Phase 6).** Everything in this document is **implemented and tested** in `src/TrayWisp.Cli` (the commands) and `src/TrayWisp.Runtime` (the shared root validation, storage checks, interpreter lookup and run history), except where a section says *planned*. The CLI is an orchestration and presentation layer: discovery, trust, interpreter resolution, execution and output parsing are the existing Phase 1 to 5 implementations, not copies.

**Approval is consent, not sandboxing.** An approved plugin runs as your Windows user, with all of your access. Approval only records that you chose to trust exactly that content. See [security.md](security.md#trust-model).

Related specifications: [plugin-format.md](plugin-format.md), [output-protocol.md](output-protocol.md), [trust-model.md](trust-model.md), [execution-engine.md](execution-engine.md), [scheduling.md](scheduling.md) and [diagnostics.md](diagnostics.md) (the run history).

## Contents

- [Quick reference](#quick-reference)
- [Command-line syntax](#command-line-syntax)
- [Locations](#locations)
- [Commands](#commands)
- [Exit codes](#exit-codes)
- [JSON output](#json-output)
- [Terminal safety](#terminal-safety)
- [Cancellation](#cancellation)
- [CLI runs and the scheduler](#cli-runs-and-the-scheduler)
- [Diagnostics](#diagnostics)
- [Tests](#tests)
- [Limitations](#limitations)

## Quick reference

| Command | Operand | Options | Purpose |
| --- | --- | --- | --- |
| `list` | none | `--root`, `--json` | Discovered plugins. Discovery only. |
| `inspect` | `PLUGIN_ID` | `--root`, `--data-dir`, `--json` | Metadata and the SHA-256 fingerprint of the whole file, for review. |
| `approve` | `PLUGIN_ID` | `--sha256` (required), `--root`, `--data-dir`, `--json` | Approve exactly the reviewed content. |
| `revoke` | `PLUGIN_ID` | `--root`, `--data-dir`, `--json` | Remove an approval. |
| `status` | optional `PLUGIN_ID` | `--root`, `--data-dir`, `--json` | Authoritative trust check of one plugin or every discovered plugin. |
| `run` | `PLUGIN_ID` | `--timeout`, `--powershell`, `--python`, `--node`, `--show-stderr`, `--root`, `--data-dir`, `--json` | Run one approved plugin once. |
| `validate-output` | none (reads standard input) | `--truncated`, `--json` | Parse plugin output without running anything. |
| `doctor` | none | `--powershell`, `--python`, `--node`, `--root`, `--data-dir`, `--json` | Check the host configuration without running anything. |
| `logs` | none | `--limit`, `--root`, `--data-dir`, `--json` | Recent run summaries from the local run history. |
| `--help`, `-h` | | | Global help. `traywisp COMMAND --help` shows a command's help. |
| `--version` | | | `traywisp ` followed by the assembly's informational version. |

Options:

| Option | Value | Meaning |
| --- | --- | --- |
| `--root PATH` | fully qualified local directory | The plugin root. Default `%LOCALAPPDATA%\TrayWisp\plugins`. |
| `--data-dir PATH` | fully qualified local directory | TrayWisp's data directory: the trust store is `PATH\trust\approvals.json`, the run history `PATH\diagnostics\history.json`. Default `%LOCALAPPDATA%\TrayWisp`. |
| `--json` | none | One JSON document on standard output (see [JSON output](#json-output)). |
| `--sha256 FINGERPRINT` | 64 lowercase hexadecimal characters | The fingerprint you reviewed (`ContentFingerprint` rules: no uppercase, prefix or other length). |
| `--timeout SECONDS` | whole seconds, 1 to 300 | The run timeout (the engine's 1 s to 5 min range). Default 30. |
| `--powershell PATH`, `--python PATH`, `--node PATH` | fully qualified local path to an `.exe` | Use exactly this interpreter. If it cannot be used, nothing runs: there is no fallback to automatic resolution. |
| `--show-stderr` | none | Also show the plugin's bounded, sanitized standard error tail. It is shown to this caller only and never stored. |
| `--truncated` | none | Treat the input as incomplete, so the parser rejects it as a whole (`TW405`). |
| `--limit COUNT` | whole number, 1 to 200 | How many of the most recent records `logs` shows. Default 20. |

## Command-line syntax

```text
traywisp COMMAND [OPTION | PLUGIN_ID]... [-- PLUGIN_ID]
traywisp --help | -h | --version
```

The parser is a small, exact parser written for TrayWisp with the standard library (no `System.CommandLine` or other dependency):

- **The command comes first**, spelled exactly (lowercase). After it, options and the operand may come **in any order**. `traywisp run --timeout 5 clock.ps1` and `traywisp run clock.ps1 --timeout=5` are the same request.
- **Option values** follow as the next argument (`--root C:\plugins`) or after `=` (`--root=C:\plugins`). A separate value must not begin with `-`; `--root --json` is a missing value, not a root named `--json`. Flags take no value (`--json=true` is an error).
- `--` ends the options: every later argument is an operand, so a plugin named `-x.ps1` is `traywisp inspect -- -x.ps1`.
- `--help` or `-h` anywhere after the command (before `--`) shows that command's help and does nothing else, whatever the other arguments are. Help is always plain text, also with `--json`.
- `--help`, `-h` and `--version` before a command take no other arguments. No arguments at all shows global help and succeeds.
- **Rejected** with exit code 2: unknown commands and options (exact match only; no abbreviations or case variants), options a command does not accept, duplicated options, missing values, missing or extra operands, invalid values, more than 32 arguments, an argument longer than 8,192 characters or containing NUL.
- **Nothing is expanded.** No wildcards, no environment variables (`%LOCALAPPDATA%` in an argument is literal and rejected), no `~`, no relative paths, no response files, no shell. Arguments are never joined into a command line.
- **Unrecognized arguments are not echoed raw.** An unknown command or option is repeated only when it is at most 32 characters of letters, digits, `-`, `_` and `.`; anything else is reported as `(not shown)`.
- **The complete request is validated before anything happens.** Help, version and invalid requests create no directories, take no locks, read no stores and start no processes (tested by comparing the file system before and after).

Validation of values, all without file system access:

| Value | Rule |
| --- | --- |
| `PLUGIN_ID` | A root-relative plugin path accepted by `PluginId` and `PluginTrustKey.IsSupportedPluginId`: a supported plugin file name (`.ps1`, `.py`, `.js`, `.sh`, `.bat`, `.cmd`), at most 8 directories deep, no segment beginning with `.`, no reserved names, no unpaired surrogates. `\` and `/` are both accepted and normalized to `/`. Matching is case-insensitive, like the trust model. Spaces, `&`, `;`, `$` and non-ASCII letters are fine. |
| `--root`, `--data-dir` | The `PluginRoot` syntax rules: fully qualified, local drive, no `.` or `..` segments, no doubled separators, no UNC, device or alternate-data-stream paths, no segments ending in space or dot. One trailing separator is removed. |
| `--sha256` | `ContentFingerprint.TryParse`: exactly 64 lowercase hexadecimal characters. |
| `--timeout`, `--limit` | ASCII digits only (no sign, spaces, separators, exponent or non-ASCII digits), parsed with the invariant culture, then range-checked. |
| interpreters | The engine's own configured-interpreter rule (`InterpreterLookup.IsValidConfiguredPath`). |

## Locations

| Location | Default | With `--data-dir D` |
| --- | --- | --- |
| Plugin root | `%LOCALAPPDATA%\TrayWisp\plugins` | (set with `--root`) |
| Trust store | `%LOCALAPPDATA%\TrayWisp\trust\approvals.json` (the Phase 3 default, `PluginTrustService.GetDefaultStorePath()`) | `D\trust\approvals.json` |
| Run history | `%LOCALAPPDATA%\TrayWisp\diagnostics\history.json` | `D\diagnostics\history.json` |

`%LOCALAPPDATA%` is `Environment.SpecialFolder.LocalApplicationData`, read only when a location the command actually uses is missing: `--root` or `--data-dir` for the commands that use storage, only `--root` for `list`, which uses none (so `list --root PATH` works even when `%LOCALAPPDATA%` is unavailable). If it is needed and unavailable, the command fails with exit code 5 (`TW853`) and asks for explicit options. There is no other fallback: the current directory is never searched, and nothing in plugin metadata or output can change a location. There is no configuration file.

**Validation order.** Path syntax is checked when the request is parsed. Before a command reads anything below a location, it validates the location read-only, with the same order the scheduler and the trust checks use: the drive type (local fixed, removable or RAM), then every directory from the drive root down, opened without following links (no reparse points, not offline or remote, no case-sensitive ancestor, final path equal to the configured path), and only then the 8.3 short-name expansion check. This is `PluginRootValidation.Check` for the plugin root (extracted from the scheduler's root validation, which now uses it too) and `StorageLocation.Check` for the trust and history directories (see [diagnostics.md](diagnostics.md#storage-location-checks)). Nothing is held open afterwards, so later steps validate again; the documented path-race limitations of discovery and trust validation are unchanged (see [security.md](security.md#plugin-discovery-implemented)).

**Nothing creates the plugin root.** `list`, `inspect`, `approve`, `status`, `run` and `doctor` report a missing root (exit code 3, or a problem in `doctor`) with the advice to create it explicitly, for example `New-Item -ItemType Directory -Path "$env:LOCALAPPDATA\TrayWisp\plugins"`.

**Storage directories are created only for writes.** The trust store directory and its lock file are created by the trust service the first time a command needs the store (`approve`, `revoke`, `status`, `run`: every authoritative check takes the store lock). The history directory is created when the first run is recorded. Each missing level is created only after its parent was validated. `list`, `inspect`, `validate-output`, `doctor` and `logs` create nothing.

**No overlap.** For `inspect`, `approve`, `status` and `run`, the trust store directory and the history directory must not overlap the plugin root in either direction (`PluginRoot.Overlaps`): a root that is, contains or lies inside `D\trust` or `D\diagnostics` fails with exit code 5 (`TW854`) before anything else happens. The default root and data directory share the ancestor `%LOCALAPPDATA%\TrayWisp`, which is fine. Aliases (a `subst` drive, a junction or a short name naming the same directory) are not compared by text; they are rejected by the final-path checks when the location is used. `revoke` skips the overlap check, like the trust service, so stray records stay removable; `list` uses no storage.

## Commands

### list

```text
traywisp list [--root PATH] [--json]
```

Runs `PluginDiscovery.Discover` on the validated root, with its default limits and its deterministic order (ordinal case-insensitive identity order). For each plugin it shows the identity, the display title (metadata title or file name), the language and whether it can be run, the refresh interval or "manual only", and the plugin's metadata diagnostics, followed by discovery's own diagnostics and whether the scan was complete.

Listing is discovery only. It runs nothing, hashes nothing, reads no trust store and shows no approval state, and with `--root` it does not consult `%LOCALAPPDATA%` at all. Use `status` for trust. An incomplete scan is reported as such (exit code 11): a plugin missing from it may still exist.

### inspect

```text
traywisp inspect PLUGIN_ID [--root PATH] [--data-dir PATH] [--json]
```

1. Discovery of the root, for the plugin's descriptor and metadata.
2. `PluginTrustService.Inspect`: the validated file path and the SHA-256 of the whole file, through the trust service's own validated, held-open file inspection.

It shows the identity, the root, the validated file, the fingerprint and size, the metadata, whether discovery was complete and found the plugin, and the next steps. Metadata and fingerprint are read **separately**, metadata first, so they are not claimed to describe the same version of the file; only the fingerprint identifies the content. Inspection approves nothing, reads no approval (`approvalChecked: false`) and runs nothing. It never shows the script, fetches metadata URLs or loads images. The suggested `approve` command is printed ready to paste only when every value is printable ASCII; otherwise a template is shown (`traywisp approve --sha256 … -- 'PLUGIN_ID'`). The suggestion puts every option first, then `--`, then the plugin identity in single quotes (`traywisp approve --sha256 FINGERPRINT --root '…' --data-dir '…' -- '-clock.ps1'`), so an identity that starts with `-` is never read as an option, and nothing in it (`$`, backticks, `;`, `&`, parentheses, spaces) is interpreted by PowerShell; single quotes are doubled. It is inert text: it always contains the fingerprint just shown, so pasting it approves only that content, and nothing is approved until you run it. A non-ASCII identity adds `TW864`, because lookalike characters are not detected.

Seeing a fingerprint does not mean you reviewed the script. Read the file, then approve the fingerprint you read.

### approve

```text
traywisp approve PLUGIN_ID --sha256 FINGERPRINT [--root PATH] [--data-dir PATH] [--json]
```

An identity that starts with `-` must follow `--`, as for every command: `traywisp approve --sha256 FINGERPRINT -- '-clock.ps1'`.

Calls `PluginTrustService.Approve` with the given fingerprint. Under the store lock the trust service validates the path again and rehashes the file immediately before saving, and approves only if the current content equals `FINGERPRINT`. There is no way to approve without a fingerprint, to approve "whatever is there now", a wildcard or a directory, or to override a mismatch. A file changed after inspection is refused (`FingerprintMismatch`, exit code 4) with the current fingerprint shown, and an obsolete approval of other content is removed, exactly as specified in [trust-model.md](trust-model.md#explicit-approval-workflow). Approving unchanged, already approved content keeps the existing approval and generation (`AlreadyApproved`). A corrupt store is never repaired or replaced (exit code 10).

The command itself is the confirmation: there is no interactive prompt, so it can be scripted. It does not run the plugin. The result shows the approval generation and time.

### revoke

```text
traywisp revoke PLUGIN_ID [--root PATH] [--data-dir PATH] [--json]
```

Calls `PluginTrustService.Revoke`. Only the syntax of the root and identity matters, so an approval whose plugin file, or whole root, no longer exists can be revoked; discovery and inspection are not used. The result distinguishes `Revoked` (with the revoked generation and fingerprint), `NotApproved` (nothing to remove; exit code 0), a store that is unavailable or could not be updated (exit code 10) and an unsupported root (exit code 3). The plugin file is never touched, and the rest of the store is never reset.

Revocation does not stop a run that another TrayWisp process has already started; the next trust check of any host denies the plugin. A future long-lived desktop host must notice external changes to the store through the scheduler's periodic reconciliation (see [scheduling.md](scheduling.md#detection-latency)) or call `NotifyTrustChanged` after its own changes.

### status

```text
traywisp status [PLUGIN_ID] [--root PATH] [--data-dir PATH] [--json]
```

With `PLUGIN_ID`: one targeted `PluginTrustService.CheckTrust`; the plugin need not appear in a discovery scan. Without: discovery, then one `CheckTrust` per discovered plugin, sequentially, in discovery order (bounded by discovery's 500-candidate limit). One trust service instance serves the whole command.

Every trust status is reported by its existing name: `Approved`, `PendingApproval`, `ContentChanged`, `StaleApproval` (not produced by this command, which does not pass an expected approval), `PluginUnavailable`, `UnsafePath`, `StoreUnavailable`, with the current fingerprint, the current approval generation and the trust diagnostics.

Status is an authoritative check made now: it hashes every file and takes the store lock. It is **not read-only**: a check that observes changed content removes the obsolete approval persistently (`approvalInvalidated: true`), as every authoritative check does. A status is a point-in-time observation, never an authorization. Approvals of plugins that discovery did not find are not listed. No scheduler or background process is involved or implied.

### run

```text
traywisp run PLUGIN_ID [--timeout SECONDS] [--powershell PATH] [--python PATH] [--node PATH] [--show-stderr]
             [--root PATH] [--data-dir PATH] [--json]
```

Runs exactly the named plugin once through `PluginExecutionService.RunAsync`, with a trust service and an execution service created for this invocation:

1. The request, the root and the trust store directory are validated (see [Locations](#locations)).
2. `RunAsync`: interpreter resolution (an explicit, invalid interpreter fails with no fallback), the launch-time authoritative trust check with its execution lease, process supervision, capture limits, cleanup and parsing, all exactly as in [execution-engine.md](execution-engine.md). Unapproved, changed, unsafe and unavailable plugins start nothing.
3. If the run succeeded: a fresh `CheckTrust(result.Approval)` on the same trust service. If it does not return `Approved` (or is cancelled), the output is **withheld** for good.
4. A summary of the run, without its output, is added to the run history (see [diagnostics.md](diagnostics.md)), with the decision of step 3. This can wait up to 5 s for the history lock. A history failure is reported as a warning (`TW858` plus the storage diagnostic) and never changes the exit code, the run's result or what is shown.
5. If step 3 approved: the **final** `CheckTrust(result.Approval)`, as the last step before the report is built (in memory) and written in one piece. Only this check decides whether the output is shown, so a decision made before the history I/O is never enough: if another invocation revoked or replaced the approval, the script changed, the trust store became unavailable or Ctrl+C arrived meanwhile, the output is withheld. No approval-store lock is held during the history write, and there is no transaction between the two stores.

Withheld output (`TW857`; exit code 4, 10 for an unavailable store, 130 when the check was cancelled) appears nowhere, neither in the text nor in the JSON. The run's own status, exit code and cleanup are still reported as they happened; incomplete cleanup keeps precedence (exit code 9); the plugin is never run again. `publication` in the JSON is the final decision; `publication.withheldByFinalCheck` is `true` when the first check approved and the final check did not. The history keeps the first decision (`history.recordedPublication`, "output decision when recorded" in the text), so a record can say `Published` for a run whose output this report withheld. A record never proves that output was displayed: see [diagnostics.md](diagnostics.md#what-publication-means).

What it never does: start a scheduler, run any other plugin (interval plugins included), execute, open or resolve output actions, accept plugin arguments or shell commands, retry, add a force or unsafe flag, or touch the PowerShell execution policy. Output actions are shown as inert data (see [Terminal safety](#terminal-safety)).

The report shows the execution status, the launch-time trust status, the plugin's exit code (distinct from TrayWisp's exit code), the duration, any termination by TrayWisp, the resolved interpreter path, kind and source ("validated path; not authenticated"), the cleanup outcome and job object, the captured byte counts and completeness, and the diagnostics. **Incomplete cleanup** is reported prominently (`WARNING: PROCESS CLEANUP INCOMPLETE`, `TW863`, exit code 9): processes started by the plugin may still be running.

The windows between the launch-time check and the interpreter opening the file, and between the final post-run check and the display, remain and are documented in [execution-engine.md](execution-engine.md#remaining-time-of-check-to-time-of-use-windows). The second one is now only the time to render the report in memory and write it: a revocation in that moment is not noticed, and a terminal or pipe may receive the report later still. A successful exit does not mean that the approval is still valid; that is what the final post-run check decides.

PowerShell plugins run under your effective execution policy, which TrayWisp neither changes nor bypasses. On a Windows client whose policy is the default (`Restricted`), PowerShell plugins fail with the policy error; choose a policy your organisation permits, sign your scripts, or use a Python or JavaScript plugin. TrayWisp's documentation never suggests weakening the policy. PowerShell plugins that print non-ASCII text must select UTF-8 (see [execution-engine.md](execution-engine.md#output-encoding)).

### validate-output

```text
traywisp validate-output [--truncated] [--json] < FILE
```

Reads plugin output from **redirected standard input** and decodes and parses it exactly like the engine: `PluginOutputDecoder` (UTF-8, UTF-8 with a byte order mark, UTF-16 LE or BE with a byte order mark; invalid sequences and UTF-32 rejected) and `PluginOutputParser.Parse(text, isTruncated)`.

- **Bounded.** At most 1,048,576 bytes (the engine's standard output limit) are accepted. The command reads at most one byte more, never further: exactly 1,048,576 bytes are accepted, 1,048,577 are rejected without parsing (`TW860`, exit code 8). Bytes are read into a fixed buffer; no string is created before the size is known. Short reads are assembled.
- **Results.** A complete parse succeeds (exit code 0), also with recoverable diagnostics, which are shown; empty input is valid, empty output. Undecodable input (`TW862`), a fatal parser failure (over-long line, too many lines, a streaming delimiter, …) and input marked `--truncated` (`TW405`) reject the whole output (exit code 8).
- **Inert.** Nothing is run, opened or resolved, and no store, history or directory is touched.
- **Interactive input is refused.** If standard input is a console, the command fails at once (exit code 2, `TW859`) without reading. A pipe whose writer never closes it is waited for until Ctrl+C (exit code 130).
- A valid result proves neither that a script is safe nor that it is authorized.

Examples. PowerShell re-encodes text it pipes to programs, using `$OutputEncoding` (US-ASCII by default in Windows PowerShell 5.1, UTF-8 in PowerShell 7), and adds a final line break, so it does not pass a file's original bytes:

```powershell
# Text, re-encoded as UTF-8 by PowerShell (non-ASCII text survives; byte order marks and invalid bytes do not):
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Get-Content -Raw -Encoding utf8 .\sample-output.txt | traywisp validate-output

# The file's exact bytes, through cmd.exe redirection:
cmd /c "traywisp validate-output < sample-output.txt"
```

### doctor

```text
traywisp doctor [--powershell PATH] [--python PATH] [--node PATH] [--root PATH] [--data-dir PATH] [--json]
```

Reports the TrayWisp version; the platform (Windows 10 or later is supported); the plugin root, its source and its `PluginRootValidation` status, and any overlap with storage; the trust store and run history locations, their sources, their `StorageLocation` status (directory missing, ready, unsafe, inaccessible) and whether their file exists and is a single-link regular file; for PowerShell, Python and Node.js, the interpreter the engine would use now (`InterpreterLookup.Find`, the engine's own resolver: explicit paths with no fallback, then PowerShell 7's folder, validated PATH directories and the system directory) with its diagnostics; Bash and batch as recognized but not run; the limits; and a list of problems found.

What it does **not** do, and says so in its report (`contentChecked`, `authenticated`, `versionChecked` and `executionPolicyChecked` are always `false`):

- It runs no script and no interpreter: an interpreter's version and whether it actually works are unknown. A resolved path is a validated local file with a portable executable header; that does not authenticate it.
- It does not determine whether the PowerShell execution policy allows scripts (that would require starting PowerShell).
- It does not read, lock, create or repair the trust store or the run history. `status` reads the store under its lock; `logs` reads the history. A read-only parse of the store without its lock would have needed a second store-reading path in the trust service; that was judged not worth it, and the report names the unchecked condition instead.
- It installs, downloads and creates nothing, uses no shell or `where.exe`, and shows no environment variable values (resolution reads `PATH` internally; only validated paths and counts appear in its diagnostics).

`doctor` exits with 0 when it could produce its report, whatever it found; the `problems` list and the statuses carry the findings.

### logs

```text
traywisp logs [--limit COUNT] [--root PATH] [--data-dir PATH] [--json]
```

Shows the most recent `COUNT` records (default 20, at most 200) of the run history, **oldest first** (the order in which they were recorded). Each record shows its time, the plugin identity, whether it belongs to the configured plugin root (records store a root identifier, not the path; see [diagnostics.md](diagnostics.md#what-is-stored)), the execution and trust status, the plugin's exit code, the duration, termination, cleanup, output size, publication and the diagnostic codes.

It distinguishes `NoHistory` (no directory or file), `Empty`, `Loaded`, `Corrupt`, `UnsupportedVersion` and `Unavailable` (the last three exit with 10). Reading never creates, repairs, clears or rewrites the history; when no history file exists it creates nothing at all. There is no command to read other files, no clear command and no upload. To remove the history, delete `history.json` in the diagnostics directory (and, if you like, `history.json.lock`) while no TrayWisp command is running. Do not delete the `trust` directory: it holds your approvals.

## Exit codes

TrayWisp's own exit code. A plugin's exit code is never returned as TrayWisp's; it is reported as `pluginExitCode`.

| Code | Meaning |
| --- | --- |
| 0 | The command did what was asked. Includes reports of negative observations (see below). |
| 1 | Unexpected internal failure (`TW851`), unreadable standard input (`TW861`), or standard output that could not be written. |
| 2 | Invalid command-line usage (`TW850`), and `validate-output` with interactive input (`TW859`). |
| 3 | The plugin root is missing, unsafe or inaccessible; the plugin is unavailable, unsafe or of an unsupported language; the platform is not supported. |
| 4 | Approval missing, pending, stale or changed: a run denied by the trust check, an `approve` whose fingerprint does not match, or output withheld after the run. |
| 5 | Configuration: an interpreter unavailable or explicitly configured but unusable, storage overlapping the plugin root (`TW854`), no default locations (`TW853`), an unacceptable store path. |
| 6 | The run failed: process start failure, non-zero plugin exit code, output limit exceeded, capture or supervision failure. |
| 7 | The run timed out and was terminated. |
| 8 | Output decoding or parsing failed (`run`, `validate-output`), or `validate-output` input over the limit. |
| 9 | Process cleanup could not be confirmed. Processes may still be running. |
| 10 | The trust store or the run history could not be used (unavailable, invalid, unsupported version, unsafe location, lock timeout, write failure). |
| 11 | Discovery was incomplete (`list`, `status` without a plugin). |
| 130 | Cancelled (Ctrl+C), including a run cancelled while it ran, after its cleanup ended. |

**Precedence** for `run`, highest first: 9 (incomplete cleanup, also when the run was cancelled or timed out), 130 (cancellation, also of the post-run check), the run's own status (4, 3, 10, 5, 7, 8 or 6), then the publication decision (4, or 10 for an unavailable store). A history failure never changes the code. For `status`, 10 (a store failure) takes precedence over 11.

By command:

| Command | Success (0) | Failures |
| --- | --- | --- |
| `list` | a complete scan | 3 root, 11 incomplete scan (results still shown), 5 no default root |
| `inspect` | inspected (also when discovery was incomplete or did not find the plugin, with a warning) | 3 unavailable or unsafe plugin or root, 5, 10 unsafe trust directory |
| `approve` | `Approved`, `AlreadyApproved` | 4 mismatch, 3 unavailable or unsafe, 10 store, 5 |
| `revoke` | `Revoked`, `NotApproved` | 10 store, 3 unsupported root, 5 |
| `status` | every requested check completed, **whatever the trust status**: `PendingApproval`, `ContentChanged`, `PluginUnavailable` and `UnsafePath` are reported observations | 10 any `StoreUnavailable`, 11 incomplete scan, 3 root, 5 |
| `run` | succeeded and output published | see precedence |
| `validate-output` | complete parse, with or without recoverable diagnostics | 8, 2 interactive input, 1 read failure, 130 |
| `doctor` | the report was produced, whatever it found | 5 no default locations |
| `logs` | `NoHistory`, `Empty`, `Loaded` | 10, 5 no default locations |

## JSON output

With `--json`, standard output receives exactly **one JSON document** for every command that completes, including usage errors and operational failures, and nothing else: no banners, progress, tables or warnings, and nothing on standard error. The document is built in memory and written in one piece, so it is never partial or doubled. Help is always text; `--version` accepts no options.

Not promised: a valid document after the process is terminated externally, or when standard output is a closed pipe (the command then ends with exit code 1).

### Envelope (schema version 1)

```json
{
  "schemaVersion": 1,
  "command": "run",
  "success": false,
  "exitCode": 4,
  "result": { },
  "diagnostics": [
    {
      "source": "execution",
      "code": "TW620",
      "severity": "error",
      "message": "...",
      "path": null,
      "line": null,
      "column": null
    }
  ]
}
```

| Field | Content |
| --- | --- |
| `schemaVersion` | `1`. A future incompatible change increments it. |
| `command` | The command name, or `null` for a usage error before a command was recognized. |
| `success` | `exitCode == 0`. |
| `exitCode` | TrayWisp's exit code (see [Exit codes](#exit-codes)). |
| `result` | The command's result object, or `null` (usage errors, unexpected failures, cancellation before a result existed). |
| `diagnostics` | The diagnostics of the command, in the order they were produced. For `list` and `status` without a plugin, per-plugin diagnostics are inside each plugin entry instead. |

Diagnostic objects always have all seven fields. `source` is `cli`, `discovery`, `trust`, `execution`, `output` or `storage`; `code` is `TW` and the stable number; `severity` is `info`, `warning` or `error`; `path` is a root-relative path for discovery diagnostics; `line` and `column` are one-based where known; absent values are `null`.

Conventions:

- Properties are written in a fixed order, and lists are in a deterministic order (discovery order; records oldest first; diagnostics in production order).
- Every documented property is always present; a value that does not apply is `null`, never omitted. (`executionPolicyChecked` exists only in the PowerShell entry of `doctor`.)
- Status values are the names of the existing enumerations (`PluginTrustStatus`, `PluginExecutionStatus`, `PluginApprovalStatus`, `PluginRevocationStatus`, `PluginInspectionStatus`, `PluginRootStatus`, `StorageLocationStatus`, `StorageFileStatus`, `InterpreterLookupStatus`, `RunHistoryReadStatus`, `RunHistoryWriteStatus`, `RunPublication`, `ProcessCleanupStatus`, `ExecutionTerminationReason`, `InterpreterKind`, `InterpreterSource`, `StandardOutputEncoding`, `PluginLanguage`, `PluginExecutionSupport`). New members may be added; existing names do not change.
- Timestamps are UTC, `yyyy-MM-ddTHH:mm:ss.fffffffZ`. Durations are `…Milliseconds`, sizes `…Bytes` or `bytes`.
- The document is ASCII: `System.Text.Json` with `JavaScriptEncoder.Default` escapes every non-ASCII, control and HTML-sensitive character (`\u00E9`, `\u001B`, `\u0027`). Strings keep their exact values otherwise (identities, paths, labels, arguments, URLs), so consumers get machine-readable identities, but must still render them as untrusted text. Unpaired UTF-16 surrogates, which JSON cannot carry, become U+FFFD; such identities can never be approved (`TW510`).
- No exception, stack trace or exception message is ever serialized; unexpected failures report only the exception type (`TW851`).
- Output-defined actions keep their structure: `{"type":"command","executable":…,"arguments":[…],"terminal":…,"refresh":…}`, `{"type":"url","url":<Uri.OriginalString>,"refresh":…}`, `{"type":"refresh","refresh":true}`, or `null`. There is no command-line string.
- Size: results are bounded by the existing limits (500 plugins with bounded metadata, 2,048 menu nodes of at most 4,096-character values, 200 records, 64 diagnostics per operation and 256 parser diagnostics). With escaping, the largest possible documents are tens of megabytes (a full `list` of 500 plugins with maximal metadata); typical documents are a few kilobytes.

### Results

`list`:

```json
{ "root": "C:\\Users\\me\\AppData\\Local\\TrayWisp\\plugins", "complete": true, "entriesInspected": 3,
  "plugins": [ { "pluginId": "clock.1m.ps1", "displayTitle": "Clock", "name": "clock", "language": "PowerShell",
                 "executionSupport": "Supported", "refreshInterval": { "text": "1m", "milliseconds": 60000 },
                 "metadata": { "title": "Clock", "version": "1.0", "author": null, "authorGitHub": null, "description": null,
                               "dependencies": null, "aboutUrl": null, "image": null },
                 "diagnostics": [] } ] }
```

`inspect`: `root`, `pluginId`, `inspectionStatus`, `sha256`, `contentBytes`, `validatedFilePath` (for display only, never for launching), `approvalRequired` (`true`), `approvalChecked` (`false`), `discoveryComplete`, `metadataFound`, `metadata`, `fileName` (`name`, `language`, `executionSupport`, `refreshInterval`), `metadataDiagnostics`.

`approve`: `root`, `pluginId`, `approvalStatus`, `approved`, `expectedSha256`, `currentSha256`, `approval` (`sha256`, `generation`, `approvedAtUtc`, or `null`), `approvalInvalidated`, `pluginRun` (`false`).

`revoke`: `root`, `pluginId`, `revocationStatus`, `revoked`, `revokedApproval` (or `null`).

`status`: `root`, `discoveryComplete` (`null` for one plugin), `plugins`: `pluginId`, `trustStatus`, `approved`, `currentSha256`, `contentBytes`, `approval` (`sha256`, `generation`, or `null`), `approvalInvalidated`, `validatedFilePath`, `diagnostics`.

A command stopped by an invalid root has the result `{ "root": …, "rootStatus": "NotFound" }`.

`run`:

```json
{ "root": "...", "pluginId": "clock.1m.ps1",
  "execution": { "status": "Succeeded", "succeeded": true, "trustStatus": "Approved", "pluginExitCode": 0,
                 "startedAtUtc": "2026-11-28T10:20:30.1234567Z", "elapsedMilliseconds": 412, "terminationReason": "None",
                 "timedOut": false, "cancelled": false,
                 "interpreter": { "path": "C:\\Program Files\\PowerShell\\7\\pwsh.exe", "kind": "PowerShell", "source": "KnownLocation" },
                 "approval": { "sha256": "...", "generation": "..." }, "validatedFilePath": "...",
                 "stdout": { "bytes": 57, "complete": true, "truncated": false, "encoding": "Utf8" },
                 "stderr": { "bytes": 0, "truncated": false, "shown": false, "text": null },
                 "cleanup": { "status": "Exited", "rootExitConfirmed": true, "jobObjectAssigned": true, "remainingJobProcesses": 0 } },
  "publication": { "status": "Published", "trustStatus": "Approved", "trustCheckCancelled": false, "withheldByFinalCheck": false },
  "output": { "titles": [ { "text": "...", "displayText": "...", "tooltip": null, "disabled": false, "checked": false,
                            "action": null, "showInDropdown": true } ],
              "items": [ { "type": "item", "line": 3, "text": "...", "displayText": "...", "tooltip": null, "disabled": false,
                           "checked": false, "action": { "type": "url", "url": "https://example.com/", "refresh": false },
                           "children": [] },
                         { "type": "separator", "line": 4 } ] },
  "history": { "recorded": true, "status": "Recorded", "evictedRecords": 0,
               "recordedPublication": "Published", "recordedPublicationTrustStatus": "Approved" } }
```

`output` is `null` unless `publication.status` is `Published`. `publication.status`, `trustStatus` and `trustCheckCancelled` describe the final decision. `history.recordedPublication` and `recordedPublicationTrustStatus` are what the record holds (the check right after the run), or `null` when nothing was recorded. `stderr.text` is `null` unless `--show-stderr` was given.

`validate-output`: `input` (`bytes` (`null` over the limit), `limitBytes`, `limitExceeded`, `encoding`, `markedTruncated`), `parseStatus` (`Complete`, `Failed`, or `null` when nothing was parsed), `diagnosticsTruncated` (`null` when nothing was parsed), `output` (as for `run`, or `null`).

`doctor`: `version`, `platform` (`windows`, `supported`, `osVersion`, `runtime`), `pluginRoot` (`path`, `source` `default`/`option`, `status`, `overlapsStorage`), `trustStore` and `runHistory` (`directory`, `source`, `directoryStatus`, `file`, `fileStatus`, `contentChecked`), `interpreters` (`language`, `status`, `configured`, `path`, `kind`, `source`, `authenticated`, `versionChecked`, for PowerShell `executionPolicyChecked`, `diagnostics`), `deferredLanguages`, `limits`, `problems` (strings).

`logs`: `historyDirectory`, `status`, `totalRecords`, `order` (`oldestFirst`), `root`, `records`: `recordedAtUtc`, `startedAtUtc`, `rootId`, `isConfiguredRoot`, `pluginId`, `status`, `trustStatus`, `pluginExitCode`, `elapsedMilliseconds`, `terminationReason`, `cleanupStatus`, `jobObjectAssigned`, `remainingJobProcesses`, `stdoutBytes`, `stdoutComplete`, `stdoutTruncated`, `stderrBytes`, `stderrTruncated`, `interpreterKind`, `interpreterSource`, `publication`, `publicationTrustStatus`, `diagnostics` (`code`, `severity`), `diagnosticsTruncated`.

## Terminal safety

Everything that comes from plugin files, metadata, plugin output, standard error, the file system, the environment or the command line is untrusted. Human-readable output applies one policy (`TerminalText`) to all of it:

- Every untrusted value is written after a fixed label written by TrayWisp (`Plugin: …`, `Title: …`, `- …`) on a single line. Newlines and other separators are escaped, so a value can never start a line, imitate a heading, a table, a diagnostic (`Error TW…`) or another field.
- **Escaped** as `\uXXXX` (one per UTF-16 code unit, so a supplementary character becomes two escapes): C0 and C1 control characters and DEL (so ESC sequences, CSI `U+009B`, OSC hyperlinks `ESC ] 8`, bells, carriage returns and backspaces cannot act), format characters (bidirectional controls such as U+202E and U+2066–U+2069, zero-width characters, the soft hyphen, tag characters and other supplementary format characters), line and paragraph separators, unpaired surrogates and unassigned code points.
- **Also escaped in identities, paths, messages and other text, but not in display labels:** the other invisible characters, with the same list the output parser uses to reject invisible labels (Unicode Default_Ignorable_Code_Point plus U+2800): Hangul fillers (U+115F, U+1160, U+3164, U+FFA0), variation selectors (U+FE00–U+FE0F, U+E0100–U+E01EF), the combining grapheme joiner (U+034F), Mongolian free variation selectors, U+2800 BRAILLE PATTERN BLANK, and every space other than U+0020 (no-break, ideographic, figure and other spaces). Display labels from plugin output (titles, menu text, tooltips, metadata) keep U+200D (zero-width joiner) and variation selectors, which emoji sequences need. Only the presentation changes: identities, fingerprints, approvals and JSON values are exact, and such file names are not rejected.
- **Identities and paths are unambiguous** (plugin identity, plugin root, data directory, validated file, interpreter and history paths): a backslash directly followed by `u` is shown as `\u005C` (so `C:\x\u200Bdir`, a directory literally named `u200Bdir`, is shown as `C:\x\u005Cu200Bdir`, unlike a name holding an invisible U+200B, shown as `C:\x\u200Bdir`), a space at the start or end is shown as `\u0020`, and they are never shortened. Other backslashes are kept, so Windows paths stay readable and pasteable. In diagnostics, paths are quoted with `PluginDiagnosticText` (which doubles every backslash and escapes `'`) and then get the same invisible-character escapes.
- **Kept:** letters of every script, combining marks, emoji, spaces (including runs of spaces inside a value) and punctuation. In other text than identities and paths, backslashes are kept as they are, so a value may look as if it contained an escape, but it can never hide a character.
- **Bounded:** labels and messages are cut at 512 to 2,048 characters with `...`; identities and paths are bounded only by their own limits (4,096 characters); standard error is shown as its last 100 lines of at most 500 characters, already sanitized by the engine and escaped again here.
- **Actions** are shown as inert data: `executable: "…"` and `argument[0]: "…"` on separate lines, in JSON-style quotes with `"` and `\` escaped, never joined into a command line; URLs as `Uri.OriginalString` in quotes, so escaped characters such as `%0A` are never decoded. Every action is marked as not run or not opened.
- Diagnostics are TrayWisp's own messages (which never repeat plugin output; paths in them are quoted and escaped with `PluginDiagnosticText`) and are escaped once more.
- TrayWisp writes no colour, cursor or other terminal formatting of its own.
- Text written to a console uses the console's Unicode API; redirected text is UTF-8 without a byte order mark. JSON is pure ASCII.
- A suggested command line (`inspect`'s next step) is produced only from printable ASCII values, with every option before `--` and the plugin identity single-quoted after it.

Remaining limitations: homoglyphs (Cyrillic `а` for Latin `a`), mixed scripts, combining marks and lookalike punctuation are shown as they are, and so are default-ignorable characters inside display labels; only an `info` note (`TW864`) warns about non-ASCII identities in `inspect` and `approve`. Compare identities and paths carefully. Fonts may still render some accepted characters as blank.

## Cancellation

Ctrl+C requests cancellation; the command then finishes normally:

- The console handler only marks the process as staying alive and calls `CancellationTokenSource.CancelAsync`, which runs cancellation callbacks on the thread pool, not on the console's signal thread. Further Ctrl+C presses are ignored.
- The token reaches discovery, inspection, trust operations, the run, both post-run checks, `validate-output`'s wait for input and the history read. Ctrl+C during the history write withholds the output (the final check is then reported as cancelled, exit code 130); the record keeps the first check's decision. A run being cancelled is terminated by the engine, and the command **waits for the engine's cleanup result** before reporting it (exit code 130, or 9 if cleanup could not be confirmed). The process is never abandoned.
- The run history record after a run is written with its own bounded lock wait (5 s) and without the cancelled token, so cancelled runs are recorded too.
- When the command has ended, and before its report is built, cancellation is closed: later Ctrl+C presses are ignored (the process still stays alive until the report is written), and a started delivery is awaited. If a cancellation callback failed, the failure is observed and reported in the report itself as a warning (`TW851`, exception type only; in text and JSON, so nothing extra reaches standard error in JSON mode); the command's exit code and result, including a completed run and incomplete cleanup (exit code 9), are unchanged.
- Then the handler is removed, the token source is disposed (also after a failed delivery) and both streams are flushed, each step even if an earlier one failed. Only a failed flush (the report could not be delivered) changes the exit code, to 1. Because the process exits immediately afterwards, the scheduler's additional `WaitHandle` disposal (see [scheduling.md](scheduling.md#cancellation-resources)) is not needed here.
- `validate-output`: after cancellation the reader stops before its next read and never returns what it read, so partial input is never parsed or shown (exit code 130, `TW852`). A read the operating system keeps blocked cannot be interrupted: it is abandoned and ends with the process, and its eventual failure is observed, never left as an unobserved task exception.

Limitations: Ctrl+Break and closing the console window end the process at once. The run's job object is closed with the process (kill on close), which terminates the plugin's tracked processes, but no result or history record is written. Termination by Task Manager behaves the same way. An operating-system call that cannot be interrupted (such as process creation already in progress) completes before cancellation takes effect, as documented in [execution-engine.md](execution-engine.md#timeout-and-cancellation). A read of standard input that never returns is abandoned when the process exits.

## CLI runs and the scheduler

The CLI runs one command per invocation and keeps nothing running. It does not start `PluginScheduler`, does not watch files, and does not talk to other TrayWisp processes (there is no IPC in this phase).

- The scheduler's guarantees (no overlapping runs of one plugin, the global concurrency limit, the incomplete-cleanup registry that permanently blocks a plugin whose cleanup could not be confirmed) hold **within one scheduler instance**. Separate CLI processes are not coordinated by them: two `traywisp run` invocations can run the same plugin at the same time, and a plugin whose cleanup was incomplete in one invocation can be run again by the next. Check for leftover processes after exit code 9.
- Each invocation creates its own `PluginTrustService`. An invalidation that a run could not save (see [trust-model.md](trust-model.md#unsaved-invalidations)) is remembered only by that invocation's service, which ends with the process; the next invocation reads the store again. A store that cannot be written is therefore a problem to fix, not only a diagnostic (`TW533`).
- The trust store lock and the history lock are inter-process locks, so concurrent invocations never corrupt or lose each other's updates.
- A future long-lived desktop host will own scheduling, and must notice approvals and revocations made by CLI invocations through its periodic reconciliation. Whether the CLI will talk to such a host is undecided (see [architecture.md](architecture.md#open-design-questions)).

## Diagnostics

CLI diagnostics have the codes `TW850`–`TW899` (source `cli`); the run history and storage checks use `TW800`–`TW849` (source `storage`, see [diagnostics.md](diagnostics.md#diagnostic-codes)). Discovery and metadata (`TW100`–`TW399`, `PluginDiagnosticCode`), output (`TW4xx`), trust (`TW5xx`) and execution (`TW6xx`) diagnostics are passed through unchanged.

| Code | Name | Severity | Meaning |
| --- | --- | --- | --- |
| TW850 | UsageError | Error | Invalid command line (exit code 2). |
| TW851 | UnexpectedFailure | Error | An unexpected failure; only the exception type is reported. As a warning: a cancellation callback failed after the command completed; its result is unaffected. |
| TW852 | Cancelled | Info | The command was cancelled before it completed. |
| TW853 | ConfigurationInvalid | Error | No default locations, or an unacceptable trust store path. |
| TW854 | StorageOverlapsPluginRoot | Error | The trust store or history directory overlaps the plugin root. |
| TW855 | DiscoveryIncomplete | Warning | The discovery scan was incomplete. |
| TW856 | PluginNotInDiscovery | Warning | `inspect`: discovery did not find the plugin, so no metadata is shown. |
| TW857 | OutputWithheld | Error | `run`: a post-run trust check (right after the run, or the final one after the history write) did not confirm the approval; output withheld. |
| TW858 | RunNotRecorded | Warning | `run`: the run history was not updated; the run's result is unaffected. |
| TW859 | InputNotRedirected | Error | `validate-output`: standard input is a console. |
| TW860 | InputTooLarge | Error | `validate-output`: more than 1,048,576 bytes. |
| TW861 | InputUnreadable | Error | `validate-output`: standard input could not be read. |
| TW862 | OutputInvalid | Error | `validate-output`: not valid UTF-8 or BOM-marked UTF-16. |
| TW863 | CleanupIncomplete | Error | `run`: process cleanup could not be confirmed. |
| TW864 | LookalikeCharacters | Info | The identity contains non-ASCII characters; lookalikes are not detected. |

## Tests

`tests/TrayWisp.Cli.Tests` (xUnit v3 on Microsoft.Testing.Platform, like the other test projects; referenced by `TrayWisp.slnx`, so CI runs it):

- **In-process** tests run `CliApplication` with in-memory streams, isolated temporary roots, data directories and a stand-in `%LOCALAPPDATA%`, and execution services built with the engine's internal test hooks (process-start counting, injected failures). The controlled helper program (`tests/TrayWisp.TestHelper`) is the explicitly configured interpreter; process creation and its absence are checked through the start counter and marker files.
- **Process-level** tests start the built `traywisp.exe` (copied next to the tests) with redirected streams and a 60 s bound (killed on timeout): exit codes, standard output and error separation, help, version, JSON, approval persistence between invocations, a real authorized run with the helper interpreter, `validate-output` with redirected input, `doctor`, and real Python (automatic resolution) and Windows PowerShell (explicit path) runs, which are optional locally and required when `TRAYWISP_REQUIRED_INTERPRETERS` names them (CI: `all`).
- Coverage: the parser (every rule above, culture independence, limits, no echo), help and version without side effects, every exit code, discovery, inspection, approval, stale fingerprints, revocation of missing plugins, idempotent revocation, every trust status, persistent invalidation, corrupt and unsafe stores, overlaps, unsafe roots, runs (success, empty output, non-zero exit, timeout, cancellation, overflow, capture failure, incomplete cleanup, unsupported language, missing and invalid interpreters, post-run denial, inert actions, standard error, history failures), `validate-output` (encodings, limits, short reads, endless and blocking input, truncation, fatal and recoverable diagnostics, interactive input), `doctor` (no process, no file system change, unchecked conditions, no environment values), `logs`, terminal escaping and the JSON envelope.
- Publication after the history write (`RunPublicationTests`): the history operation is held after the first, approved check while the approval is revoked, reapproved, the script is modified, the trust store is made unavailable or the command is cancelled; the output is then withheld in text and JSON, the run's own result kept, and the record says `Published`. Also: a failing history still gets the final check, and a denial right after the run is final.
- Terminal identity safety and the approval suggestion (`TerminalIdentityTests`): every class of invisible character, literal escape text, edge spaces, untruncated identities, diagnostic paths, labels that keep emoji, and the suggested command for `-clock.ps1`, `--example.ps1`, apostrophes, spaces, PowerShell metacharacters, non-ASCII and invisible characters, each suggested command executed in Windows PowerShell 5.1.
- Finalization (`FinalizationTests`): failing cancellation callbacks (observed, reported as `TW851`, exit code kept, source disposed, streams flushed), flush failures, abandoned input reads (no partial input, no further reads, late faults observed). `list --root` without `%LOCALAPPDATA%`.
- `TrayWisp.Runtime.Tests/Diagnostics` covers the new Runtime APIs (see [diagnostics.md](diagnostics.md#tests)).

## Limitations

- Validation narrows but does not eliminate file system races; the limitations in [security.md](security.md) and [trust-model.md](trust-model.md#security-guarantees-and-limitations) apply unchanged.
- No coordination between CLI invocations or with a future desktop host beyond the store and history locks (see [CLI runs and the scheduler](#cli-runs-and-the-scheduler)).
- Lookalike characters are not detected.
- `doctor` cannot know whether an interpreter works, which version it is, or whether PowerShell's execution policy allows scripts.
- *Planned, not implemented:* a tray host, a plugin management interface, a persistent scheduler, IPC between the CLI and a desktop host, persistent settings, a clear-history command.
