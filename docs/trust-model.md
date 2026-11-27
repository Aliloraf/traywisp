# Trust model

This document specifies plugin trust and approval: how TrayWisp identifies a plugin for approval, fingerprints its content, records explicit approvals, invalidates them, and decides whether a plugin may run.

**Status (Phase 3).** Everything in this document is **implemented and tested**, except where a section says *planned*. TrayWisp still **cannot run plugins**. Nothing calls the trust check yet. Phase 4 (execution) and later phases must use it as described in [Integration obligations](#integration-obligations-for-later-phases).

**Approval is consent, not sandboxing.** An approved plugin runs with all of your user account's access. Approval only records that you chose to trust exactly this content at this location. See [security.md](security.md#trust-model).

Code: `TrayWisp.Core/Trust` (immutable models, pure policy) and `TrayWisp.Runtime/Trust` (file system validation, hashing, the approval store and the service).

## Overview

```text
Runtime: PluginTrustService(storePath)
  Inspect(root, id)            validate root and path → hash the whole file → PluginFingerprint
  Approve(root, id, expected)  lock store → read store → validate and hash again → compare with expected → save
  Revoke(root, id)             lock store → read store → remove → save
  CheckTrust(root, id)         lock store → read store → validate and hash → Core policy → invalidate if changed
  CheckTrust(approvalIdentity) the same, and the current approval must be exactly that identity
```

Only `CheckTrust` authorizes, and only for that moment. Discovery, successful parsing, metadata, file existence, timestamps, earlier results and earlier approvals never authorize anything.

## Public API

Core (`TrayWisp.Core.Trust`), pure and immutable:

| Type | Role |
| --- | --- |
| `ContentFingerprint` | SHA-256 of a file's complete raw bytes: exactly 64 lowercase hexadecimal characters. Strict parsing, ordinal value equality. Holding one proves nothing. |
| `ApprovalGeneration` | Identifies one act of approval: 32 lowercase hexadecimal characters (a GUID in `N` format), never all zero. Created only by Runtime. |
| `PluginRoot` | A normalized, fully qualified local Windows directory path. Syntax only, see [Roots](#roots). Case-preserving, compared ordinal case-insensitively. |
| `PluginTrustKey` | What an approval is bound to besides content: `PluginRoot` + `PluginId`. Accepts only identities discovery can produce. |
| `PluginFingerprint` | An inspection: key, fingerprint and byte length. What a user interface shows for review. |
| `PluginApprovalIdentity` | Key + fingerprint + generation. Associates future output with the approval under which it was produced. Not a permission token. |
| `PluginApproval` | A persisted approval record: approval identity + informational UTC timestamp. |
| `PluginTrustStatus` | Trust decision outcome (see [Trust states](#trust-states)). |
| `PluginTrustPolicy`, `PluginTrustEvaluation`, `PluginApprovalAction` | Pure decisions from the current approval and a fresh inspection. |
| `TrustDiagnostic`, `TrustDiagnosticCode` | Diagnostics `TW500`–`TW599`, separate from `PluginDiagnostic` and `OutputDiagnostic`. |

Runtime (`TrayWisp.Runtime.Trust`):

| Member | Role |
| --- | --- |
| `PluginTrustService(storePath, options)` | A service bound to one store file. Thread-safe. Several instances and processes can share a store. Its only state is its [unsaved invalidations](#unsaved-invalidations), so a host uses one long-lived instance per store. |
| `PluginTrustService.GetDefaultStorePath()`, `CreateForCurrentUser()` | The per-user default store, `%LOCALAPPDATA%\TrayWisp\trust\approvals.json`. |
| `Inspect(root, id)` → `PluginInspectionResult` | Validates and fingerprints a plugin. Does not touch the store. |
| `Approve(root, id, expectedFingerprint)` → `PluginApprovalResult` | Explicit approval of reviewed content. |
| `Revoke(root, id)` → `PluginRevocationResult` | Explicit revocation. |
| `CheckTrust(root, id)` → `PluginTrustDecision` | **The authoritative trust check.** |
| `CheckTrust(approvalIdentity)` → `PluginTrustDecision` | The trust check for something produced under an earlier approval, such as an output-driven action. |
| `PluginTrustOptions.LockTimeout` | How long to wait for the store lock (default 10 s, at most 5 min). |
| `MaxPluginFileBytes`, `MaxStoreBytes`, `MaxApprovals`, `MaxDiagnostics` | Limits (see [Resource limits](#resource-limits)). |

Result types have internal constructors, so code outside Runtime cannot fabricate an approved decision. Invalid roots, unsafe paths and every file or store problem are reported as results with diagnostics. Only caller errors throw: `null` arguments (`ArgumentNullException`), an unacceptable store path or option at construction (`ArgumentException`), and cancellation (`OperationCanceledException`).

There are no interfaces, no dependency injection, and no wildcard, directory-wide or author-based approval.

## Trust states

| `PluginTrustStatus` | Meaning | Changes the store |
| --- | --- | --- |
| `Approved` | A current approval exists and a complete inspection made now found exactly the approved bytes. **The only status that permits execution.** | no |
| `PendingApproval` | No current approval: never approved, revoked, or invalidated earlier. | no |
| `ContentChanged` | An approval exists, but a complete inspection found different bytes; or this service instance found that earlier and could not save the removal yet ([unsaved invalidations](#unsaved-invalidations)). | removes the approval (if the removal cannot be saved, this instance remembers it) |
| `StaleApproval` | `CheckTrust(approvalIdentity)` only: the expected identity is not the current approval (revoked, reapproved, or replaced). | no |
| `PluginUnavailable` | The plugin could not be fingerprinted now: missing, locked, unreadable, larger than the limit, or changing during the inspection. **Not** a confirmed change. | no |
| `UnsafePath` | The root or identity is invalid or unsupported, or the path is unsafe: link, junction, alias, network drive, hidden or remote entry, case collision, case-sensitive ancestor, overlap with the store. | no |
| `StoreUnavailable` | The store could not be locked, read or updated, or is invalid or of an unsupported version. | no |

The checks run in this order, and the first failure decides the result: root and identity syntax (no file system access, no lock), store overlap, lock acquisition, store read, the instance's [unsaved invalidations](#unsaved-invalidations), path validation and fingerprinting, then the policy decision. An unsafe or unavailable plugin is never reported as changed, so its approval is kept. When the path becomes safe and the content is unchanged again, the plugin is approved again without reapproval. This is intended, because nothing was observed to change.

`PluginTrustDecision` carries the status, `IsApproved`, the validated `Key`, the observed `Current` fingerprint (when inspection succeeded), the current `Approval` identity and the `ValidatedFilePath` (both only when approved), `ApprovalInvalidated` and bounded `Diagnostics`.

### Validated file path

`PluginTrustDecision.ValidatedFilePath` is the exact file an approved check validated and hashed. It is the final path of the plugin file, read with `GetFinalPathNameByHandle` from the open handle after hashing (and required to be identical to the final path read before hashing), so it is a fully qualified local path with the **on-disk spelling** of every directory and of the file name, including case. It never comes from the caller's spelling of the root or identity.

It matters in case-sensitive directories inside the root. There, the case-insensitive identity `CLOCK.ps1` can resolve, like discovery, to the one visible `clock.ps1` while a hidden `CLOCK.ps1` with other bytes sits next to it. Rebuilding a path from the identity would name the hidden file. Only the validated path names the file whose bytes were approved.

- It is set only when the status is `Approved`, together with `Approval`. Every failed or denied decision has neither, so a denial never carries a launchable path.
- `PluginInspectionResult.ValidatedFilePath` reports the same for a successful inspection. An inspection authorizes nothing: that path is for display, never for launching.
- Approval identity and equality are unchanged (case-insensitive root and identity, fingerprint, generation), and the store keeps keying records by normalized root, `PluginId`, fingerprint and generation. The path is not stored.
- The file is no longer held once the result is returned. The validated path does **not** close the window between the check and the moment an interpreter opens the file (see [Limitations](#security-guarantees-and-limitations)).

## Approval identity

An approval is bound to three things:

1. the **normalized, fully qualified plugin root** (`PluginRoot`);
2. the existing **root-relative `PluginId`**, with its Phase 1 semantics (`/` separators, ordinal case-insensitive equality);
3. the **SHA-256 of the entire plugin file**.

The same identity under two roots is two keys. Two spellings of one key that differ only by case, by `/` versus `\`, or by a trailing separator are the same key. The stored root and identity keep the spelling of the approval that created the record, for display.

`PluginTrustKey` accepts only identities that discovery can produce with its default options: at most 4,096 characters, at most 8 directories deep (`PluginDiscoveryOptions.Default.MaxDepth`), no segment beginning with `.`, and a file name that `PluginFileNameParser` accepts (recognized extension, valid name and interval). It also requires **well-formed UTF-16**: an identity with an unpaired surrogate is rejected (`TW510`). Windows allows such names, and `PluginId` (Phase 1) accepts them, but JSON cannot represent them: the writer silently substitutes U+FFFD, so the stored record would name a different identity. Supplementary characters (correctly paired surrogates) are accepted and round-trip exactly. The trust service takes a root and a `PluginId`, never a caller-supplied full path to the plugin.

### Roots

`PluginRoot` normalizes and validates syntax only:

| Input | Result |
| --- | --- |
| `C:\Users\me\plugins` | `C:\Users\me\plugins` |
| `C:\Users\me\plugins\` | `C:\Users\me\plugins` (one trailing separator removed) |
| `C:/Users/me/plugins` | `C:\Users\me\plugins` (`/` becomes `\`) |
| `c:\USERS\me\plugins` | kept as written; equal to the above |
| `C:\` | `C:\` (a drive root keeps its separator) |
| `plugins`, `.\plugins` | rejected: relative |
| `C:plugins` | rejected: drive-relative |
| `\plugins` | rejected: rooted but without a drive |
| `\\server\share\plugins`, `//server/share` | rejected: UNC |
| `\\?\C:\plugins`, `\\.\C:\plugins`, `\??\C:\plugins`, `\\?\UNC\...` | rejected: device and extended-length namespaces |
| `C:\a\\b`, `C:\a\\` | rejected: empty segment |
| `C:\a\.\b`, `C:\a\..\b` | rejected: `.` and `..` segments are not resolved |
| `C:\plugins:stream`, `C:\plugins::$DATA` | rejected: `:` after the drive (alternate data streams) |
| `C:\plugins\CON`, `C:\plugins.`, `C:\plugins ` | rejected: segments `PluginId.IsValidSegment` rejects (reserved device names, trailing dot or space, control or reserved characters, bidirectional controls) |
| a path containing an unpaired surrogate | rejected: not well-formed UTF-16, so it could not be stored exactly |

Paths longer than 4,096 UTF-16 code units are rejected. No Unicode normalization is applied. Equivalent representations are therefore limited to case, separator style and one trailing separator. Everything else that could make two strings name one directory is rejected rather than normalized.

Runtime adds two checks when a root is used:

- `Path.GetFullPath` must return the normalized path unchanged. On Windows it expands 8.3 short names (`C:\PROGRA~1`), so a short-name root is rejected as an alias (`TW505`), with the long path in the message.
- The [file system validation](#file-system-validation) below, including the final path of the opened root directory.

### Local, mapped and network paths

What is detected, and how:

| Case | Detection | Result |
| --- | --- | --- |
| UNC syntax (`\\server\share`, `\\?\UNC\`) | syntax | `TW500` |
| Mapped network drive (`Z:` → `\\server\share`) | `DriveInfo.DriveType == Network`, and the root's final path is a UNC path | `TW506` |
| Drive letters that are not fixed, removable or RAM drives (CD-ROM, unknown) | `DriveInfo.DriveType` | `TW506` |
| Missing drive | `DriveType.NoRootDirectory` | `TW502` |
| `subst` drive (`S:` → `C:\data`) | the root's final path is `C:\data\...`, not `S:\...` | `TW505` |
| Symbolic link, junction or mount point in the root or an ancestor | reparse-point attribute on the opened entry (and a final-path mismatch) | `TW504` |

TrayWisp does **not** claim network isolation. A volume that Windows presents as a local fixed disk can be backed by network storage (iSCSI, a virtual disk on a share, some cloud drive products), and nothing in the path or drive type reveals that. The checks only reject what Windows reports as remote.

## File system validation

Trust operations are implemented for Windows only. On other platforms every operation fails with `TW501`.

Root (every time it is used):

1. The drive must be a local fixed, removable or RAM drive.
2. Every ancestor, from the drive root down to the root's parent, is opened through its handle without following reparse points or recalling remote data. It must be a directory, must not be a reparse point, must not be offline or recall-on-access, and must not be a **case-sensitive directory**. Under a case-sensitive parent, `Plugins` and `plugins` could be two different roots, which the case-insensitive key could not tell apart. Hidden ancestors are allowed, because `%LOCALAPPDATA%` is under the hidden `AppData`.
3. The root itself is opened the same way and **held open** for the whole operation, with sharing that prevents it from being deleted or renamed. It must be an ordinary directory, and its final path, read from the handle with `GetFinalPathNameByHandle`, must equal the normalized root (ignoring case). This rejects `subst` drives, mapped drives, and links or mount points the attribute checks missed. A hidden root is allowed, as in discovery.

Plugin path (the same rules discovery applies, for this one path only):

1. For each directory on the path, starting with the root: list it with discovery's own `ListDirectory` and classify its entries with discovery's own `Classify`. Exactly one entry must equal the next identity segment ignoring case **and** be of the expected kind (an ordinary directory, or a candidate plugin file). Two such entries are an identity collision (`TW513`), as in discovery. A matching entry that is a reparse point, offline, hidden, or of the wrong kind is rejected (`TW512`). No match means not found (`TW511`).
2. That entry is opened by its on-disk name, without following reparse points or recalling remote data, and **held open**: directories so they cannot be deleted or renamed, the plugin file so it cannot be written, deleted or renamed. The opened entry's own attributes are checked again through the handle, and the file must be a regular disk file that is not being deleted.
3. The plugin file's final path must equal the root plus the on-disk names (`TW514` otherwise). The part inside the root must match exactly, including case.
4. After hashing, the file's attributes and final path, and the root's final path, are checked again, and every directory on the path is **listed again**. The same unique names must still be found. A change is reported as `TW523`, or `TW513` for a new collision.

Listing is bounded: all listings of one validation pass share discovery's default budget of 10,000 entries. A listing that does not fit is rejected (`TW515`), because an unseen entry could be a case variant. A plugin that default discovery can list therefore always fits. Unrelated plugins are not read.

These checks replicate discovery's exclusion rules (dot-prefixed, hidden, reparse points, remote storage, invalid names, collisions, truncated listings, depth), so a targeted check cannot approve a plugin that discovery would have excluded. Discovery's candidate limit (500) and its global scan budget are scan-wide resource limits, not identity rules, and do not apply to a single targeted check.

Case-sensitive directories *inside* the root are supported as in discovery: case variants of the same kind are excluded, and a single entry is found whatever the case of the identity. A hidden case variant is never opened in place of the visible entry, because the entry is opened by its listed on-disk name.

### Handle-based verification: what it achieves

The investigation's outcome. Opening each component with `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL` and checking its attributes through the handle means a link swapped in after the listing is opened as a link and rejected, not followed, and opening a cloud placeholder does not hydrate it. `GetFinalPathNameByHandle` confirms that the opened file is reached through the expected path. Holding the root, the intermediate directories and the file open with restrictive sharing prevents them from being renamed, deleted or (for the file) written **while the operation runs**. Approval and invalidation are saved while these handles are still held, so the approved bytes are the file's bytes at the moment the approval is committed.

This uses five Win32 functions through `LibraryImport` (`CreateFileW`, `GetFileInformationByHandleEx`, `GetFinalPathNameByHandleW`, `GetFileType`, `MoveFileExW`). No other native code is involved.

What it does **not** achieve:

- Ancestors of the root are checked, then closed. An ancestor swapped for a junction after its check is caught by the final-path checks, as long as the swap happens before the last of them.
- The listing is path-based. .NET cannot list a directory through a held handle. Because the directory is held open and cannot be renamed, the path still names the same directory unless an ancestor of the root is swapped.
- A process that mapped the file into memory for writing before the inspection can still change it. Sharing modes do not cover existing mapped views.
- Nothing holds the file after the operation returns. **Every result describes one moment.** The interpreter that Phase 4 starts opens the file by path on its own, later (see [Limitations](#security-guarantees-and-limitations)).

## Fingerprinting

- SHA-256 over the **entire raw byte sequence** of the file, read through the validated handle. Not the metadata prefix, not decoded text, not normalized line endings, not the name or timestamps. A BOM, an encoding, a line ending or a single byte anywhere in the file changes the fingerprint.
- Streaming in 64 KiB chunks with `IncrementalHash`. Memory use is bounded by the chunk size.
- Files larger than **16 MiB** (`MaxPluginFileBytes`, 16,777,216 bytes) are rejected (`TW522`), never partially hashed. The limit is enforced on the bytes actually read: at most limit + 1 bytes are read, so a file that grows cannot cause unbounded work. A file of exactly 16 MiB is accepted.
- The file is opened for reading with sharing that denies writes and deletion. If another program has it open for writing, the open fails with a sharing violation and the file is reported as locked (`TW520`). It is never approved.
- After reading, the number of bytes read must equal the file length, measured before and after (`TW523` otherwise).
- Cancellation is checked before each chunk and between validation steps.
- Missing, locked, unreadable, oversized and changing files produce `PluginUnavailable` results. Unsafe paths produce `UnsafePath` results.
- A plugin is never executed to establish its fingerprint. Tests check that inspecting and approving a script that would write a marker file leaves no marker, and does not change the file's timestamp or attributes.

## Explicit approval workflow

1. `Inspect(root, id)` returns the validated `PluginTrustKey` and current `PluginFingerprint`. A future CLI or user interface shows the plugin (and ideally its content) for review.
2. The user approves. The caller passes the **fingerprint the user reviewed** to `Approve(root, id, expectedFingerprint)`.
3. Under the store lock, `Approve` reads the current store, validates the root and path again, and hashes the file again. It saves the approval only if the new hash equals the expected fingerprint. Otherwise it returns `FingerprintMismatch` with the current fingerprint and approves nothing. A file changed after review is never approved silently. A caller-supplied hash is only a statement of intent, never proof of the file's content.
4. If the plugin is already approved for exactly these bytes, the result is `AlreadyApproved`. The existing record, including its generation and timestamp, is kept, and the store is not rewritten.
5. Otherwise a new `PluginApproval` with a **new random generation** (`Guid.NewGuid`, created in Runtime, unique within the store) and the current UTC time replaces any previous approval for the key. The store is written atomically while the plugin file is still held open.

**Obsolete approvals observed while approving.** The fresh hash in step 3 is a complete inspection, so the [observed-content-change rule](#invalidation-and-revocation) applies to it exactly as to `CheckTrust`. If the key has an approval of other bytes than the current ones, that approval is obsolete, whatever the request asks for:

| Current bytes vs. expected | Existing approval | Outcome |
| --- | --- | --- |
| equal | of the current bytes | `AlreadyApproved`, kept |
| equal | of other bytes (obsolete) | `Approved`: a new generation replaces it in one atomic write; `ApprovalInvalidated = true` |
| differ | of the current bytes | `FingerprintMismatch`; the approval is **kept** (a wrong expected fingerprint is not a content change) |
| differ | of other bytes (obsolete) | `FingerprintMismatch`; the obsolete approval is **removed** (`TW534`, `TW531`, `TW532`; `ApprovalInvalidated = true`) |
| either | one this instance already invalidated without saving | treated as obsolete: never kept with its old generation, even through the idempotent path |

If saving the replacement or removal fails, nothing is approved, the result reports the failure (`TW547`, `TW533`), and the obsolete approval becomes an [unsaved invalidation](#unsaved-invalidations) of this service instance. If the plugin cannot be inspected (missing, locked, unreadable, unsafe path), nothing is concluded about its content and its approval is kept, except that an approval already remembered as invalidated is removed if possible. `Inspect` never reads or changes the store.

Cancellation is observed up to the moment saving starts. After that the operation completes and reports what happened. A cancelled approval never leaves behind an approval it did not report. If an obsolete approval was observed, its removal is still saved (or remembered) before the cancellation is reported.

The approval timestamp is informational. Time never establishes or extends trust.

This phase has no approval dialog, no CLI commands and no source viewer.

## Invalidation and revocation

**Observed content change.** When `CheckTrust` completes a fingerprint inspection and the bytes differ from the approved fingerprint, it returns `ContentChanged`, removes the approval from the store before returning (`ApprovalInvalidated = true`, diagnostics `TW531`, `TW532`), and the plugin needs explicit reapproval. The removal is a deletion of the record, so later checks report `PendingApproval`. If the previously approved bytes return later, the plugin stays pending. Approval is **not** restored.

**Failed invalidation.** If the removal cannot be saved, the check still returns `ContentChanged` (denied), with `ApprovalInvalidated = false` and `TW547` plus `TW533`, and the invalidation becomes an [unsaved invalidation](#unsaved-invalidations) of the service instance that observed it.

### Unsaved invalidations

Each `PluginTrustService` instance keeps a bounded in-memory set of approval **generations** whose invalidation it confirmed (by observing different bytes, in `CheckTrust` or `Approve`) but could not save. For a generation in the set, that instance:

- denies the approval on every authoritative check (`ContentChanged`, `TW531`), **even if the previously approved bytes have returned**, before it inspects the file;
- retries the removal on its next `CheckTrust` or `Approve` of that plugin, also when the file is now unavailable or holds the original bytes. When the removal is saved, the result has `ApprovalInvalidated = true` and `TW532`, the generation leaves the set, and the removal persists across restarts;
- never keeps that generation through the idempotent `AlreadyApproved` path: an explicit approval creates a new generation that replaces it atomically, so identities of the old generation stay stale;
- forgets the generation once the store, read under the lock, no longer holds it (removed or replaced by a successful write of this or any other instance, or revoked).

The set only ever denies. It never grants an approval, and every operation still reads the store from disk. Access is synchronized for concurrent operations (which the store lock also serializes).

It is bounded by `MaxApprovals` (4,096). Because generations the store no longer holds are dropped on every read, it cannot hold more entries than the store holds records. If an entry still cannot be recorded, the instance fails closed for the rest of its lifetime: every `CheckTrust` is denied (`StoreUnavailable`, `TW533`), and `Approve` never keeps an existing generation.

**Scope, honestly.** The set exists only in the memory of one instance. Other instances, other processes and later sessions (after a restart) do not know about an invalidation that could not be written to disk. They read the old approval from the store. If the bytes still differ, they observe the change themselves. If the previously approved bytes have been restored in the meantime, they **approve the old generation**. Therefore:

- a host must use **one long-lived `PluginTrustService` per store** for all of its authoritative checks, never a fresh instance per check;
- a store that cannot be written is a real problem to fix, not only a diagnostic. `TW533` says so.

**Not a content change.** A missing, locked, unreadable, oversized or changing file, an unsafe path, an unavailable store, or a cancelled check denies the plugin at that moment and changes nothing.

**Incomplete discovery.** Trust operations never consult discovery results. A plugin missing from an incomplete (or complete) discovery result keeps its approval. Phase 3 has no automatic pruning of records. Removal reconciliation and hot reload belong to Phase 5.

**Observed versus unobserved changes.** Only changes that a check observes invalidate an approval. If a file is changed and changed back between two checks, no check sees the difference, and the approval stands, because the bytes are again the approved bytes. Phase 4 must check immediately before every launch to keep this window small.

**Revocation.** `Revoke(root, id)` removes the approval (`Revoked`) or reports `NotApproved`. It validates only the syntax of the root and identity, so approvals of plugins or roots that no longer exist can be revoked. A later approval gets a new generation, even for the same bytes.

## Approval generations and stale output

Phase 2 parses command, URL and refresh requests into inert data. A future output-driven action must not be authorized just because the producing plugin's current file is approved: the output may predate a revocation, a reapproval or a content change.

- A successful `CheckTrust` returns `Approval`, a `PluginApprovalIdentity` (key, fingerprint, generation). Phase 4 must associate the output of a run with the identity that authorized that run.
- `CheckTrust(expectedApprovalIdentity)` performs the full check and additionally requires that the current approval is exactly that identity. Otherwise the result is `StaleApproval`, or `ContentChanged` (with invalidation) if the file changed.
- Revocation followed by reapproval of the **same bytes** creates a new generation, so output produced before the revocation stays stale. An idempotent approval of unchanged content keeps the generation, because it is the same approval, except for an approval the instance has already invalidated without saving that: it gets a new generation.
- An identity is not a capability. Constructing one, for example with the current fingerprint and a guessed generation, grants nothing. It is only compared with the persisted approval.

The output parser and its grammar are unchanged, and no action is executed in Phase 3.

## Storage

### Location

- Production default: `%LOCALAPPDATA%\TrayWisp\trust\approvals.json` (`Environment.SpecialFolder.LocalApplicationData`). If that folder is unavailable, there is no default (`InvalidOperationException`). TrayWisp never falls back to the working directory, the plugin directory or anywhere else.
- A fully qualified store path can be configured explicitly (tests always do). It follows the `PluginRoot` syntax rules for its directory, plus a file name ending in `.json` that does not begin with `.`. A short-name alias is rejected at construction.
- An operation on a plugin root that contains the store directory, or lies inside it, fails with `TW517`. Revocation skips this check so stray records can still be removed.
- Missing directories of the store path are created one level at a time, each after its parent has been validated.
- Every operation that uses the store (all but `Inspect`) validates the store directory and all its ancestors (ordinary local directories, no reparse points, not offline, final path equal to the configured path, **no case-sensitive ancestor, and not case-sensitive itself**), and checks through their handles that the store file and the lock file are regular local files, not links or directories, with **exactly one hard link** and a **final path equal to their configured path** (`TW548`). In a case-sensitive directory, `approvals.json` and `Approvals.json` would be two stores with two lock files, so operations using differently cased store paths would not be synchronized. A hard-linked lock or store file has a second name outside TrayWisp's control (and the lock file could even be the store file).

**Permissions.** TrayWisp does not change ACLs. The default location inherits the per-user ACL of `%LOCALAPPDATA%`, where normally only the user, SYSTEM and Administrators have access. For an explicitly configured location, its permissions are the configurer's responsibility. Checking or managing ACLs was investigated and deliberately left out: inheritance, groups and deny rules make a reliable check complex, and anything running as the same user can modify the store anyway (see [Limitations](#security-guarantees-and-limitations)).

### Schema (version 1)

```json
{
  "schemaVersion": 1,
  "approvals": [
    {
      "root": "C:\\Users\\me\\plugins",
      "pluginId": "work/clock.5s.ps1",
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "generation": "0f8e4c1a9b2d4e6f8a1b3c5d7e9f0a2b",
      "approvedAtUtc": "2026-11-26T10:20:30.1234567Z"
    }
  ]
}
```

| Property | Content |
| --- | --- |
| `schemaVersion` | The integer `1`: no sign, fraction, exponent or leading zeros. |
| `approvals` | An array of at most 4,096 records. |
| `root` | A normalized `PluginRoot` exactly as normalization produces it. |
| `pluginId` | A normalized `PluginId` with `/` separators, satisfying `PluginTrustKey.IsSupportedPluginId`. |
| `sha256` | A canonical `ContentFingerprint`. |
| `generation` | A canonical `ApprovalGeneration`. |
| `approvedAtUtc` | UTC time in exactly the form `yyyy-MM-ddTHH:mm:ss.fffffffZ`. Informational. |

Nothing else is stored: no script content, metadata, arguments, environment variables or credentials.

Writing is deterministic: records are sorted by root, then identity (both ordinal case-insensitive). Properties are written in the order above, indented by two spaces, with LF line endings, a final line feed, no BOM, and all non-ASCII characters escaped as `\uXXXX`.

Reading is strict and **all or nothing**: an unsuccessful parse returns a diagnostic and no approvals at all, however many valid records preceded the problem. This is enforced once, where the parser returns. The whole store is rejected, never partially used, if any of these hold:

- it is not UTF-8 JSON (a BOM, comments, trailing commas, content after the object, nesting deeper than 16 levels);
- a required property is missing, a property appears twice (including escaped spellings such as `"r\u006fot"`), or a property is unknown. **Unknown fields are rejected** in version 1;
- any field fails validation, or a string is longer than 4,096 characters or contains an escaped lone surrogate (a root or identity that is not well-formed UTF-16 is never accepted, so every accepted string round-trips exactly);
- two records have the same root and identity ignoring case, or the same generation;
- there are more than 4,096 records, or the file is larger than 4 MiB.

Record order in the file does not matter when reading. A `schemaVersion` other than 1 is reported as unsupported (`TW542`), distinct from invalid (`TW541`), and the rest of such a store is not interpreted.

### Persistence and synchronization

- **Lock.** Every read-modify-write operation, and every authoritative check, holds `approvals.json.lock` (in the store directory) for its whole duration. The lock file is opened with no sharing at all, which excludes other TrayWisp operations in this and other processes. It is created if missing, never deleted, and never followed if it is a link. Acquisition retries every 10–100 ms until `LockTimeout` (default 10 s, `TW545`) and can be cancelled. TrayWisp never proceeds without the lock.
- **Held store directory.** The validated store directory is opened first and **held open** for the whole transaction, with sharing that denies its deletion and renaming. The directory handle and the lock handle are released together when the operation ends, on success, failure, timeout and cancellation alike.
- **Reads.** The store is read from disk by every operation, under the lock. There is no cache, and an in-memory copy is never used when the store cannot be read.
- **Missing versus invalid.** A missing store file is an empty set of approvals. An unreadable, invalid, oversized or unsupported store is `StoreUnavailable`, and every approval in it is unusable until it is fixed.
- **Writes.** The new content is serialized and size-checked (at most 4 MiB), written to a new file `approvals.json.<random>.tmp` in the same directory (created exclusively, never overwriting anything, and checked through its handle like the store file), flushed to disk (`FlushFileBuffers`). Immediately before publication, the held store directory is checked again: still an ordinary directory whose final path is the configured path, which detects an ancestor that was renamed or replaced. Only then is the temporary file renamed over the store with `MoveFileExW(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)`; otherwise the write fails (`TW548`). The first store is created by the same rename without replacement, which fails if a file appeared meanwhile. The live store is never truncated, overwritten in place, or deleted.
- **Failures.** A failed write returns `TW547`. The previous store is unchanged, and the operation's own temporary file is deleted. Other files in the directory, including temporary files left behind by a crashed process, are never deleted.

Atomic replacement is not the same as durability. On NTFS the rename is atomic with respect to other readers, and flushing plus write-through make it very likely to survive a crash. But power loss, disk write caches that ignore flush requests, file system corruption, or file systems without these guarantees can still lose or damage the latest update.

### Recovering from a malformed store

TrayWisp never repairs, replaces or recreates a malformed store, and never rebuilds approvals from plugin metadata. While the store is invalid, every plugin is denied and approvals and revocations fail. To recover, fix the file by hand, restore it from your own backup, or delete it. Deleting it revokes every approval, which is safe: all plugins become pending. TrayWisp makes no backups.

## Diagnostics

Every operation returns at most 64 diagnostics (in practice one to three). When more are produced, the last slot is `TW599`. Messages are English. Paths, identities and system messages in them are cut to 260 characters and escaped with `PluginDiagnosticText.Quote`, as in discovery. Messages never contain plugin content or store content. Store parsing errors report only a line and byte position, and never the offending text. Fingerprints appear in `TW531` and `TW534` messages. They are not secret.

| Code | Name | Typical status |
| --- | --- | --- |
| TW500 | InvalidRootPath | UnsafePath |
| TW501 | UnsupportedPlatform | UnsafePath (revocation: InvalidPath) |
| TW502 | RootNotFound | PluginUnavailable |
| TW503 | RootNotDirectory | UnsafePath |
| TW504 | RootUnsafe (reparse point, offline or remote, in the root or an ancestor) | UnsafePath |
| TW505 | RootAlias (short name, `subst`, final path differs) | UnsafePath |
| TW506 | RemoteRoot (network or unsupported drive) | UnsafePath |
| TW507 | RootInaccessible | PluginUnavailable |
| TW508 | CaseSensitiveAncestor | UnsafePath |
| TW510 | InvalidPluginPath | UnsafePath |
| TW511 | PluginNotFound | PluginUnavailable |
| TW512 | PluginPathUnsafe (link, offline, hidden, wrong kind, not a disk file) | UnsafePath |
| TW513 | IdentityCollision | UnsafePath |
| TW514 | PluginPathAlias | UnsafePath |
| TW515 | DirectoryLimitReached | UnsafePath |
| TW516 | DirectoryUnreadable | PluginUnavailable |
| TW517 | RootOverlapsTrustStore | UnsafePath |
| TW520 | FileLocked | PluginUnavailable |
| TW521 | FileUnreadable | PluginUnavailable |
| TW522 | FileTooLarge | PluginUnavailable |
| TW523 | FileChangedDuringInspection | PluginUnavailable |
| TW530 | ApprovalPending (Info) | PendingApproval |
| TW531 | ContentChanged (also: an unsaved invalidation of this instance) | ContentChanged; approval: alongside FingerprintMismatch or StoreUnavailable |
| TW532 | ApprovalInvalidated (Info) | ContentChanged; approval: FingerprintMismatch |
| TW533 | InvalidationNotPersisted | ContentChanged; approval: FingerprintMismatch or StoreUnavailable; StoreUnavailable when the instance can track no more |
| TW534 | FingerprintMismatch | approval: FingerprintMismatch |
| TW535 | StaleApproval | StaleApproval |
| TW540 | StoreUnreadable | StoreUnavailable |
| TW541 | StoreInvalid | StoreUnavailable |
| TW542 | StoreUnsupportedVersion | StoreUnavailable |
| TW543 | StoreTooLarge | StoreUnavailable |
| TW544 | StoreTooManyRecords | StoreUnavailable |
| TW545 | StoreLockTimeout | StoreUnavailable |
| TW546 | StoreLockFailed | StoreUnavailable |
| TW547 | StoreWriteFailed | StoreUnavailable (or ContentChanged with TW533) |
| TW548 | StorePathUnsafe | StoreUnavailable |
| TW599 | DiagnosticLimitReached | — |

## Resource limits

| Limit | Value | When exceeded |
| --- | --- | --- |
| Plugin file content | 16 MiB (16,777,216 bytes), enforced on bytes read | `TW522`, not hashed |
| Hash buffer | 64 KiB | — (memory bound) |
| Serialized store | 4 MiB (4,194,304 bytes), on read and before publishing | `TW543` |
| Approval records | 4,096 | `TW544`; replacing an existing record is allowed at the limit |
| Root path, `PluginId`, stored strings | 4,096 UTF-16 code units each; stricter Windows rules still apply | `TW500`/`TW510`/`TW541` |
| Identity depth | 8 directories (discovery default) | `TW510` |
| JSON nesting depth | 16 | `TW541` |
| Directory entries listed per validation pass | 10,000 (discovery default) | `TW515` |
| Diagnostics per operation | 64 | `TW599` |
| Unsaved invalidations per service instance | 4,096 (`MaxApprovals`), and never more than the store's records | the instance denies every plugin (`TW533`) |
| Lock wait | 10 s default, 0 to 5 min | `TW545` |
| Final path length | 32,767 characters (the Windows maximum) | `TW521`/`TW507` |

Every limit fails closed: nothing over a limit is partially trusted.

## Security guarantees and limitations

What Phase 3 guarantees, within the limits below:

- Only `CheckTrust` returning `Approved` authorizes, and it does so only after reading the persisted store and hashing the complete current file, both under the store lock.
- An approval requires an explicit `Approve` with the expected fingerprint, revalidated and rehashed immediately before saving.
- An observed content change removes the approval persistently, or reports clearly that it could not. A removed approval is never restored automatically. An approval whose removal could not be saved stays denied **by the same service instance**, which retries the removal (see [Unsaved invalidations](#unsaved-invalidations) for what it does not cover).
- An approval request never preserves an approval of content other than the current content.
- An approved decision names the exact file that was validated and hashed (`ValidatedFilePath`).
- An unsuccessful store parse yields no approvals.
- Revocation and reapproval make earlier approval identities stale.
- A malformed store grants nothing, and is never replaced or partially used.
- Concurrent TrayWisp operations do not lose each other's updates.
- No input can cause unbounded memory, reads or waits.

What it does not guarantee:

- **Time of check to time of use (TOCTOU).** A decision describes one moment. Phase 4's interpreter will open the script by path, after the check, without TrayWisp's handles. Anything that can write to the plugin file or the directories on its path in between can change what runs. Re-checking immediately before each launch narrows the window but does not close it. `ValidatedFilePath` makes sure the path names the file that was checked; it does not make that file stay the same. Read an approval as "this content was trusted when it was checked".
- **Unsaved invalidations.** Known only to the service instance that observed them. Other instances, other processes and later sessions see only the persisted store, and approve the old generation if its bytes have been restored before the removal could be saved.
- **Same-user modification.** The store is an ordinary file in your profile. Any program running as you, including an approved plugin, can edit or replace it, approve anything, or remove approvals. The store is not signed, encrypted or protected with DPAPI. Approval protects against unnoticed changes to plugins, not against malware already running as you.
- **Store replacement.** The store directory is validated and then held open for the transaction, so it cannot be deleted or renamed meanwhile, and the lock, store and temporary files are checked through their handles. Its ancestors are only checked, not held. Whether NTFS refuses to rename an ancestor of a directory with open handles is not relied upon. An ancestor swapped during the transaction is detected by the final-path check before publication, but between that check and the path-based rename there remains a short window in which a same-user process could redirect the rename. This is not resistance against same-user malicious processes, which can edit the store directly anyway.
- **Path-based checks.** Ancestors of the root are checked, then closed. Listings are by path. A program racing the inspection can still cause a spurious rejection. Swaps that happen after the last check are, by design, outside one operation.
- **Hard links.** For plugin files, hard links are not reparse points and are accepted. The hash still covers the bytes, so a change made through another link is detected as a content change, but the user may not know that the file is also reachable elsewhere. Hard-linked store, lock and temporary files are rejected.
- **Short names.** A short-name root is rejected. Short names inside the root are never used, because entries are matched by their listed long names. 8.3 aliases of the plugin's own path are therefore not a way around the checks, but the same file can still be reached through them from outside TrayWisp.
- **Memory-mapped writers.** A process that mapped the file for writing before the inspection can change it during hashing despite the sharing mode. The post-read length check does not detect same-length changes made this way.
- **Case and Unicode.** Ordinal case-insensitive comparison approximates NTFS's case table. Case-sensitive ancestors are rejected. Lookalike names are not detected.
- **Scope of the hash.** Only the plugin file is covered. Not modules it imports, files it reads, the interpreter, or anything it downloads.
- **Crash durability.** See [Persistence and synchronization](#persistence-and-synchronization).
- **Network isolation.** See [Local, mapped and network paths](#local-mapped-and-network-paths).

## Tests

Windows-specific behaviour is tested against real temporary directories and isolated stores. Tests never touch the per-user default store: one test computes the default path without opening it. Tests that need junctions, symbolic links (Developer Mode or elevation), case-sensitive directories (the Windows Subsystem for Linux component), 8.3 short names or the offline attribute skip with a message where these are unavailable, so a skipped test is not evidence of the guarantee it covers. Races are tested with deterministic internal hooks (after the store lock is acquired, after the plugin is opened, after each hashed chunk, before the store is published, and a smaller capacity for unsaved invalidations) instead of timing, except for one test that confirms a second operation cannot finish within 300 ms while the first holds the lock. No test executes a script.

## Integration obligations for later phases

*Planned, not implemented.* Later phases must:

- **Phase 4 (execution).** Call `CheckTrust` immediately before every launch, on every path: scheduled, manual, CLI and output-driven. Launch only when it returns `Approved`, and launch **exactly the `ValidatedFilePath` of that immediate check**. Never reconstruct a launch path from the root and the case-insensitive identity, from an approval identity, or from an earlier discovery descriptor or decision. Use one long-lived `PluginTrustService` per store for all authoritative checks, so that [unsaved invalidations](#unsaved-invalidations) keep being enforced and retried. Never cache a decision, never treat a fingerprint, a discovery result or an earlier decision as authorization, and add no bypass. If a one-off override is ever added, it must be an explicit per-invocation user action recorded in diagnostics (see [security.md](security.md#trust-checks-on-every-execution-path)). Associate each run's output with the decision's `Approval` identity. Narrow the TOCTOU window further where practical, and document what remains.
- **Phase 4/7 (output-driven actions).** When an action is activated, call `CheckTrust(approvalIdentity)` with the identity of the run that produced the output, and act only on `Approved`. A `StaleApproval` disables the action.
- **Phase 5 (scheduling and hot reload).** Cancel in-flight runs when a check denies a plugin. Never revoke or prune an approval because a plugin is missing from an incomplete discovery result. Any reconciliation of records for removed plugins must use complete results only and must be designed explicitly.
- **Phases 6 and 8 (CLI and UI).** Present `Inspect` results for review, preferably with the content itself, and pass the reviewed fingerprint to `Approve`. Show diagnostics escaped. Explain that approval is consent, not sandboxing.
