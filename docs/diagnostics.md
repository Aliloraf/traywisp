# Diagnostics storage

This document specifies TrayWisp's bounded, local run history and the read-only host checks that `traywisp doctor` uses: what is stored, the file format, its bounds, locking and replacement, corruption handling, privacy and the Runtime API.

**Status (Phase 6).** Everything in this document is **implemented and tested** in `TrayWisp.Runtime/Diagnostics`, except where a section says *planned*. The CLI (`traywisp run`, `traywisp logs`, `traywisp doctor`; see [cli.md](cli.md)) is its only user. The Phase 5 scheduler does not record its runs yet; a future long-lived host can use the same service.

The history is for local troubleshooting of recent runs. It is not analytics or telemetry: **nothing is ever sent anywhere**, and TrayWisp has no network code for it.

## Contents

- [Principles](#principles)
- [Runtime API](#runtime-api)
- [What is stored](#what-is-stored)
- [Format](#format)
- [Bounds and retention](#bounds-and-retention)
- [Locking and replacement](#locking-and-replacement)
- [Path safety](#path-safety)
- [Corruption and versions](#corruption-and-versions)
- [Failure semantics](#failure-semantics)
- [Storage location checks](#storage-location-checks)
- [Interpreter lookup](#interpreter-lookup)
- [Diagnostic codes](#diagnostic-codes)
- [Removing the history](#removing-the-history)
- [Tests](#tests)
- [Limitations](#limitations)

## Principles

- **Never an authorization.** A record never permits, blocks or changes a run. Execution decides from the trust check alone; the history is written after the run and read only for display.
- **Summaries only.** Structured fields and diagnostic codes; no plugin output, no messages.
- **Bounded on every read and write**, whatever the file claims.
- **Never repaired silently.** An invalid or unsupported history file is reported and left exactly as it is.
- **Separate from trust.** Its own directory, file and lock; the approval store's lock is never held while the history is written, and the approval store and its serializer are unchanged.

## Runtime API

Namespace `TrayWisp.Runtime.Diagnostics`:

| Member | Role |
| --- | --- |
| `PluginRunHistory(directoryPath, options)` | A history kept in `directoryPath\history.json`. The path follows the `PluginRoot` syntax rules (`ArgumentException` otherwise). Nothing is accessed by the constructor. Thread-safe; instances and processes may share a directory. |
| `PluginRunHistory.GetDefaultDirectory()` | `%LOCALAPPDATA%\TrayWisp\diagnostics`, next to the default trust directory. No fallback. |
| `Record(rootPath, result, publication, publicationTrustStatus, token)` → `RunHistoryWriteResult` | Adds a summary of one `PluginExecutionResult`. `publication` is the host's output decision as it stands when the record is written (`NotApplicable`, `Published`, `Withheld`); `publicationTrustStatus` is the status of the host's post-run trust check, if any (see [What publication means](#what-publication-means)). |
| `Read(maxRecords, token)` → `RunHistoryReadResult` | The most recent `maxRecords` (0–200) records, oldest first, and the total count. |
| `PluginRunHistoryOptions.LockTimeout` | How long to wait for the history lock (default 5 s, at most 1 min). |
| `PluginRunRecord`, `RecordedDiagnostic`, `RunPublication` | One record; a code and severity; the publication decision. |
| `RunHistoryWriteStatus` | `Recorded`, `Rejected` (the record itself is unacceptable), `Unavailable` (the history could not be updated). |
| `RunHistoryReadStatus` | `NoHistory`, `Empty`, `Loaded`, `Corrupt`, `UnsupportedVersion`, `Unavailable`. |
| `StorageLocation.Check(directory, fileName, token)` → `StorageLocationReport` | Read-only check of a storage location (see [Storage location checks](#storage-location-checks)). |
| `InterpreterLookup.Find(language, options, rootPath, token)`, `IsValidConfiguredPath(path)` | The engine's interpreter resolution without running anything (see [Interpreter lookup](#interpreter-lookup)). |
| `StorageDiagnostic`, `StorageDiagnosticCode` | Diagnostics `TW800`–`TW849`. |
| `MaxRecords` (200), `MaxHistoryBytes` (1 MiB), `MaxRecordBytes` (8 KiB), `MaxDiagnosticCodesPerRecord` (32), `FileName` (`history.json`) | Limits. |

`TrayWisp.Runtime.Plugins.PluginRootValidation.Check(rootPath, token)` → `PluginRootCheckResult` (`Valid`, `NotFound`, `Unsafe`, `Inaccessible`, `InvalidPath`, `UnsupportedPlatform`, with one `TW500`–`TW508` diagnostic) is the read-only plugin root validation shared with the scheduler.

Result types have internal constructors. Caller errors throw (`null`, an out-of-range limit or option, an unacceptable directory, `Published` for a run that did not succeed); every file system and format problem is a result with a diagnostic. Cancellation throws `OperationCanceledException` before anything is written. All storage operations are Windows-only; elsewhere they return `UnsupportedPlatform` (`TW801`).

## What is stored

One record per run the host reports:

| Field | Content |
| --- | --- |
| `recordedAtUtc` | When the record was written (UTC). |
| `startedAtUtc` | When the process was started (UTC), or `null` if nothing was started. |
| `rootId` | 32 lowercase hexadecimal characters: the first 128 bits of SHA-256 over `TrayWisp.RootId.v1\n` followed by the root path in upper case (invariant). It tells roots apart without writing the path. |
| `pluginId` | The plugin's root-relative identity (for example `work/clock.5s.ps1`). |
| `status` | `PluginExecutionStatus`. |
| `trustStatus` | The launch-time `PluginTrustStatus`, or `null` when no trust check was made. |
| `exitCode` | The plugin process's exit code, or `null`. |
| `elapsedMilliseconds` | The run's monotonic duration. |
| `terminationReason`, `cleanupStatus`, `jobObjectAssigned`, `remainingJobProcesses` | How the run ended and how its cleanup went. |
| `stdoutBytes`, `stdoutComplete`, `stdoutTruncated`, `stderrBytes`, `stderrTruncated` | Capture sizes and completeness. |
| `interpreterKind`, `interpreterSource` | Which kind of interpreter was used and where it was found; not its path. |
| `publication`, `publicationTrustStatus` | The output decision observed when the record was written, and the status of that post-run trust check. Not proof that the output was displayed (see [What publication means](#what-publication-means)). |
| `diagnostics`, `diagnosticsTruncated` | Up to 32 distinct `(code, severity)` pairs: execution, then trust, then output codes, in first-seen order. |

### What publication means

`traywisp run` makes two trust checks of the run's approval after a successful run. The record is written between them, so it holds the decision of the **first** check: `Published` means "the approval was still confirmed right after the run, when this record was written", `Withheld` means that check denied it or was cancelled. The output is then shown only if the **second** check, made after the record was written, confirms the approval again; if another invocation revoked or replaced the approval, the script changed, the trust store became unavailable or Ctrl+C arrived in between, the output is withheld although the record says `Published`. The record is not updated with the second check (that would need another check after it, and so on), and it never proves that output reached a terminal or a reader: the report can still be lost afterwards (a closed pipe, a killed process). The `run` command's own report says which decision applied (`publication.withheldByFinalCheck`, `history.recordedPublication`); `logs` labels the field "output decision when recorded".

**Never stored:** standard output or any part of it, titles, menu labels, tooltips, action executables, arguments or URLs, standard error, script content, fingerprints, approval generations, environment variables or their values, credentials, interpreter paths, command lines, diagnostic messages (which can contain paths), exception messages or stack traces, and the plugin root's path. Tests write recognizable values into the output, labels, tooltips, action arguments and executables, standard error, diagnostic messages and interpreter paths, and check that none of them, nor the root path or the process's environment values, appears in the file.

**Still sensitive.** The plugin identity is a relative path that can reveal names you chose ("payroll/check.ps1"), and the root identifier is not anonymous: anyone who knows or guesses a root path can compute it. Times and outcomes reveal when you used TrayWisp. The file lives in your profile with your profile's permissions (TrayWisp sets no ACLs).

## Format

`history.json` (schema version 1), UTF-8 without a byte order mark, every non-ASCII character escaped as `\uXXXX`, one record per line (the canonical form: each record compact, `\n` after the header, `,\n` between records, `\n` before the footer):

```json
{"schemaVersion":1,"records":[
{"recordedAtUtc":"2026-11-28T10:20:31.0000000Z","startedAtUtc":"2026-11-28T10:20:30.5000000Z","rootId":"4f0c2b8e9d1a7c3e5b6a8d9f0e1c2b3a","pluginId":"clock.1m.ps1","status":"Succeeded","trustStatus":"Approved","exitCode":0,"elapsedMilliseconds":412,"terminationReason":"None","cleanupStatus":"Exited","jobObjectAssigned":true,"remainingJobProcesses":0,"stdoutBytes":57,"stdoutComplete":true,"stdoutTruncated":false,"stderrBytes":0,"stderrTruncated":false,"interpreterKind":"PowerShell","interpreterSource":"KnownLocation","publication":"Published","publicationTrustStatus":"Approved","diagnostics":[],"diagnosticsTruncated":false}
]}
```

Reading is strict and all or nothing, like the approval store:

- Valid UTF-8 throughout (checked before parsing, because System.Text.Json validates UTF-8 only when a string is decoded): invalid bytes, overlong forms, truncated sequences and encoded surrogates are `Corrupt`.
- JSON without comments or trailing commas, nesting at most 8 levels, nothing after the top-level object, no byte order mark.
- Every string and every property name must decode to well-formed UTF-16: an escaped unpaired surrogate (`"\uD800"`, `"\uDC00"`, a reversed pair) in a record field, a diagnostic, a status name or any property name is `Corrupt`. Escaped and raw surrogate pairs (supplementary characters such as emoji) are accepted.
- `schemaVersion` is examined first: a canonical positive integer; any value other than 1 is `UnsupportedVersion` and the rest is not interpreted.
- Top-level properties: exactly `schemaVersion` and `records` (an array), each once; property names are compared after unescaping, so `"records"` is a duplicate.
- Each record has exactly the 23 properties above, each once. Statuses must be exact member names (no numbers, no other case). Numbers must be written canonically (no fraction, exponent, sign on zero or leading zeros) and in range. Timestamps must round-trip in exactly the `yyyy-MM-ddTHH:mm:ss.fffffffZ` form. `rootId` must be 32 lowercase hexadecimal characters; `pluginId` a normalized identity that `PluginTrustKey.IsSupportedPluginId` accepts. Diagnostics are `{"code":0–9999,"severity":"Info|Warning|Error"}`, distinct, at most 32.
- More than 200 records, a record larger than 8 KiB (see [Bounds and retention](#bounds-and-retention)), or a file larger than 1 MiB, is rejected.
- Corruption diagnostics name the problem and the record's position, never the file's content or an exception message.

## Bounds and retention

| Limit | Value | Enforcement |
| --- | --- | --- |
| Records | 200 | On read (more is `Corrupt`) and on write (oldest removed first). |
| File size | 1 MiB (1,048,576 bytes) | On read: at most 1 MiB + 1 byte is read, whatever the file length says; more is `Corrupt` (`TW813`). On write: oldest records removed until the serialized file fits. |
| One record | 8 KiB (8,192 bytes) | Measured in UTF-8 bytes, not characters. On write: the record's canonical form (compact, exactly as written); a larger record (only possible with very long non-ASCII identities) is `Rejected` (`TW818`) and the history is unchanged. On read: both the record as stored (its JSON object from `{` to `}`, including any whitespace inside it, excluding the separators between records) and its canonical form must fit; otherwise the history is `Corrupt` (`TW811`). A record read from a file can therefore always be written back within the limit. |
| Diagnostic codes per record | 32 distinct | Further codes are dropped and `diagnosticsTruncated` is set. |
| Lock wait | 5 s default, at most 1 min | `TW815`. |

Each write measures every record and the whole file exactly before anything is written, so the file never exceeds its bounds. Existing records are written back in canonical form; because escaping can make a record read as raw UTF-8 larger when written back, a write may then remove more of the oldest records to stay within 1 MiB. Malformed or oversized existing history is never trimmed: it makes the write fail. There is no rotation, no second file and no background queue: a record is written synchronously by the call that adds it.

## Locking and replacement

The protocol mirrors the approval store's, with its own files:

1. Validate the directory chain (creating missing levels one at a time for writes) and **hold the directory open** for the operation, so it cannot be renamed or deleted.
2. Take the exclusive lock file `history.json.lock` (opened with no sharing, created by writers if missing, never deleted), retrying every 10–100 ms until the lock timeout; it must be a single-link regular file at its configured path. It excludes other writers and readers in this and other processes. The approval store's lock is not involved.
3. Read and strictly parse the current history. If it is unusable, stop (see [Corruption and versions](#corruption-and-versions)).
4. Append the new record, remove the oldest records while the bounds are exceeded.
5. Write a new file `history.json.<random>.tmp` in the same directory (created exclusively, checked through its handle like the history file), flush it to disk, check the held directory again (still an ordinary directory at its configured path), and rename it over `history.json` with `MoveFileEx(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)` (without replacement for the first history).
6. On any failure, the previous history is unchanged and the operation's own temporary file is deleted. Temporary files left by a crashed process are never read or deleted.

Readers take the same lock, but **open it only if it exists** (`OPEN_EXISTING`; writers use `OPEN_ALWAYS`), so reading creates nothing:

- No history directory, or no `history.json`: `NoHistory`, without taking the lock; nothing is created.
- `history.json` exists but `history.json.lock` does not (something other than TrayWisp removed it; every writer creates it before its first write): `Unavailable` with `TW816` (`HistoryLockFailed`, "lock file … is missing"). The history is not read, because reading it without the lock could race with a writer, and the lock is not created. The next recorded run creates the lock file again.
- A lock file that is not a single-link regular file (a directory, a hard link, a link) is refused (`TW816` or `TW805`) and left as it is.

A read changes neither the history nor the lock file (content and times). Concurrent writers therefore never corrupt or lose committed records: each one reads the latest committed history under the lock. Atomic replacement is not durability: power loss or a disk that ignores flushes can still lose the latest update.

## Path safety

The history uses the trust store's file system primitives (`TrustPaths`, `WindowsFileApi`) and rules:

- The directory and every ancestor: local fixed, removable or RAM drive (checked first), then opened from the drive root down without following links: no reparse points (junctions, symbolic links, mount points), not offline or remote, no case-sensitive ancestor and not case-sensitive itself, final path equal to the configured path (which also rejects `subst` drives and mapped network drives); only then the 8.3 short-name check.
- `history.json`, its lock file and every temporary file: regular local files (not links, directories, offline or remote entries), exactly one hard link, not being deleted, final path equal to their configured path.
- File names are fixed; no plugin identity, root or output ever chooses a file name.
- A record whose plugin root overlaps the history directory (in either direction) is rejected, so the history is never written inside a plugin root.

The same race limitations as the trust store apply: ancestors are checked, not held, and a short window remains between the last directory check and the rename (see [trust-model.md](trust-model.md#security-guarantees-and-limitations)).

## Corruption and versions

An invalid (`TW811`), oversized (`TW813`), over-full (`TW814`) or newer-version (`TW812`) history file is reported by `Read` (`Corrupt` or `UnsupportedVersion`) and makes `Record` fail (`Unavailable`) **without writing**: the file stays exactly as it is, and new runs are not recorded until you fix or remove it. TrayWisp never repairs, truncates, replaces or recreates it, and keeps no backups.

## Failure semantics

- A history failure never changes the outcome of the run it describes, its exit code, or whether its output is shown, and never exposes withheld output. The CLI reports it as a warning (`TW858`) next to the storage diagnostic.
- A failure is never silent: every failed `Record` returns a diagnostic.
- Nothing ever depends on a record being present.

## Storage location checks

`StorageLocation.Check(directory, fileName)` reports, without creating, writing, locking or reading anything, whether a directory is `Ready`, `Missing` (with every existing ancestor validated), `Unsafe`, `Inaccessible` or `InvalidPath`, and whether the named file is `Missing`, `Present` (a single-link regular file at its path), `Unsafe` or `Inaccessible`. It never reads the file's content. `doctor` uses it for the trust store and history locations; the CLI also uses it before it constructs a trust service, so a store directory is validated before its path is normalized.

## Interpreter lookup

`InterpreterLookup.Find` calls the execution engine's own internal `InterpreterResolver` with the engine's own configuration parsing, so it applies exactly the rules of a run ([execution-engine.md](execution-engine.md#interpreter-resolution)): a configured path is used or reported as `ConfigurationInvalid` with no fallback; otherwise PowerShell 7's installation folder, validated PATH directories and the system directory are searched; directories inside the plugin root are never used. Nothing is launched, probed for its version, installed or searched through a shell. The result holds the validated path, kind and source, and the resolution diagnostics; it returns no environment variable values. A found path does not authenticate the program or prove that it runs the expected language version, and a later run resolves again.

## Diagnostic codes

| Code | Name | Typical result |
| --- | --- | --- |
| TW800 | InvalidStoragePath | `InvalidPath` |
| TW801 | UnsupportedPlatform | not Windows |
| TW802 | StorageDirectoryMissing (Info) | `Missing` (storage location check) |
| TW803 | StorageDirectoryUnsafe | link, alias, remote, case-sensitive, not a directory, moved during an update |
| TW804 | StorageDirectoryInaccessible | could not be examined or created |
| TW805 | StorageFileUnsafe | history, lock or temporary file is a link, directory, hard-linked or elsewhere |
| TW806 | StorageFileInaccessible | a file could not be examined |
| TW810 | HistoryUnreadable | the history could not be opened or read |
| TW811 | HistoryCorrupt | invalid version 1 content |
| TW812 | HistoryUnsupportedVersion | another schema version |
| TW813 | HistoryTooLarge | over 1 MiB |
| TW814 | HistoryTooManyRecords | over 200 records |
| TW815 | HistoryLockTimeout | the lock was busy |
| TW816 | HistoryLockFailed | the lock file could not be opened |
| TW817 | HistoryWriteFailed | the replacement failed; the previous history is unchanged |
| TW818 | RecordTooLarge | one record over 8 KiB |
| TW819 | RecordRejected | unsupported identity or root, or a root overlapping the history |
| TW820 | RecordsEvicted (Info) | oldest records removed |
| TW821 | HistoryNotFound (Info) | no history yet |

Messages are English, never contain plugin output, and quote paths with `PluginDiagnosticText`.

## Removing the history

There is no clear command in this phase. To remove the history, close every TrayWisp process and delete `%LOCALAPPDATA%\TrayWisp\diagnostics\history.json` (or the file under your `--data-dir`); `history.json.lock` may be deleted too. Delete nothing in the `trust` directory: it holds your approvals, and deleting it revokes all of them.

## Tests

`TrayWisp.Runtime.Tests/Diagnostics` against real temporary directories:

- `RunHistoryTests`: malformed Unicode (invalid UTF-8, overlong and truncated sequences, encoded surrogates, escaped unpaired high, low and reversed surrogates in every kind of field, status names, diagnostic severities and property names at every level) always `Corrupt`, without records, content or exception text in the diagnostic, and never overwritten; valid supplementary characters accepted escaped and raw; records of exactly 8,192 bytes accepted (several in one file) and 8,193 rejected on write; oversized existing records rejected on read, measured both as stored (whitespace inside counts, between records does not) and canonically; long emoji identities bounded in bytes; reads that never create the lock file (missing lock with existing history, missing history next to a lock), leave history and lock unchanged, refuse directory and hard-linked lock files, and hold the lock against writers; concurrent readers and writers; missing and empty history (nothing created), append and read, every field round-tripping, nothing sensitive stored (recognizable values in standard error, messages, interpreter paths, output diagnostics and the process's environment values), the exact record bound and oldest-first eviction, the byte bound, an oversized record, bounded and de-duplicated codes, 8 concurrent writers with 10 records each (all 80 kept, no temporary files left), lock contention with timeout and cancellation, invalid and unsupported files left byte-for-byte unchanged (including escaped duplicate names, a byte order mark and trailing content), strict field validation, a file over the byte limit, a failed publication keeping the committed history, records of overlapping roots, junctions, hard links and a directory in place of the file.
- `HostDiagnosticsTests`: root validation (valid roots not held, missing roots not created, files, junctions, an 8.3 short name), storage location checks (missing, present, hard-linked, junctions, directories), interpreter lookup (configured, invalid without fallback, inside the root, deferred languages) and the public API surface of the namespace.

## Limitations

- The scheduler does not record its runs yet; only `traywisp run` does.
- Records are ordered by commit, which is chronological for one process; concurrent processes may commit in a slightly different order than they started.
- The root identifier and plugin identities are not anonymous (see [What is stored](#what-is-stored)).
- Same-user programs can read, edit or delete the history; it is not signed or encrypted.
- *Planned, not implemented:* a clear command, history in the plugin management interface (Phase 8).
