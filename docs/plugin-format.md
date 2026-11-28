# Plugin format

This document specifies how TrayWisp recognizes plugin files, parses their file names and embedded metadata, and discovers them in a plugin directory.

**Status (Phase 1).** Everything in this document is **implemented and tested**, except where a section says *planned* or *not implemented*. Discovery never runs plugins. The execution engine (Phase 4, [execution-engine.md](execution-engine.md)) runs approved plugins, and the scheduler (Phase 5, [scheduling.md](scheduling.md)) refreshes them on their file-name intervals, reruns discovery when files change and reconciles its results. Plugin output text is specified separately in [output-protocol.md](output-protocol.md), and approval in [trust-model.md](trust-model.md). A discovered plugin is only a description of a file. It is not trusted and not runnable: discovery never approves anything.

Code: `TrayWisp.Core/Plugins` (models, file-name and metadata parsers) and `TrayWisp.Runtime/Plugins` (filesystem discovery).

## File names

### Grammar

A plugin file name has the form `name[.interval].extension`:

```text
file-name  = name [ "." interval ] "." extension
extension  = "ps1" | "py" | "js" | "sh" | "bat" | "cmd"        ; case-insensitive
interval   = digit+ unit                                       ; ASCII digits 0-9 only
unit       = "ms" | "s" | "m" | "h" | "d"                       ; case-insensitive
name       = one or more dot-separated segments (see "Name rules")
```

The parser applies these steps, in order:

1. **Extension.** The text after the last `.` is the extension. If it is not a recognized extension (see [Languages](#languages)), the file is **not a candidate** and is ignored silently. This includes files with no dot and files ending in a dot.
2. **Whole-name checks.** A candidate is rejected if its file name is longer than 255 UTF-16 code units, contains a control character or one of `< > : " / \ | ? *`, or contains a Unicode bidirectional control character (U+061C, U+200E, U+200F, U+202A–U+202E, U+2066–U+2069). Bidirectional controls can disguise a name or extension: `report` + U+202E + `slx.ps1` displays as `reportsp1.xls`.
3. **Interval segment.** Let *stem* be the text before the extension.
   - If the stem has no dot, the whole stem is the name. Exception: if the stem is nothing but a well-formed interval (`5s.ps1`, `0ms.py`), it is rejected as having no name.
   - Otherwise the last dot-separated segment of the stem is examined. If it is an **interval attempt**, it must be a valid interval and the rest of the stem is the name. Otherwise it is part of the name and the plugin has no interval. If that segment is a bare number (`backup.2024.ps1`), an informational `TW102` note says it was taken as part of the name, in case a unit was forgotten.
4. **Fractional check.** If the segment before an interval attempt consists only of digits (optionally signed), the pair is read as a fractional interval (`clock.1.5s.ps1`) and rejected.
5. **Name rules** (below).

A segment is an **interval attempt** if it begins with a decimal digit (in any script), optionally preceded by `+` or `-`, contains only letters, digits, `_` and `,`, and is **not just digits**: it has a sign or at least one non-digit character. Whitespace around the segment is ignored for this test, and then reported as malformed (`clock. 5s.ps1`).

Every interval has a unit, so a bare number such as `2024` or `5` cannot be an interval and is part of the name. Anything that starts like a number but carries letters or a sign (`5sec`, `10min`, `1w`, `-1s`, `+5`) is a recognizable interval expression. It is always validated and reported when malformed, never silently treated as a manual-refresh name.

### Intervals

| Unit | Meaning | Allowed values |
| --- | --- | --- |
| `ms` | milliseconds | 250 to 2,592,000,000 |
| `s` | seconds | 1 to 2,592,000 |
| `m` | minutes (not months) | 1 to 43,200 |
| `h` | hours | 1 to 720 |
| `d` | days | 1 to 30 |

- The interval must be a positive whole number between **250 milliseconds and 30 days, inclusive**.
- Units and extensions are case-insensitive: `CLOCK.5S.PS1` is valid. The name and extension keep their original casing in the parsed result.
- Leading zeros are allowed (`0005s` is 5 seconds). `5m` and `300s` are both 5 minutes but are recorded as written.
- A missing interval means **manual refresh**. TrayWisp does not use a default interval.
- Parsing uses ordinal comparisons and only the ASCII digits `0`–`9`. It does not depend on the current culture, and it never throws for any input.
- Arbitrarily long digit strings are rejected as out of range without overflow.

### Name rules

The name (everything before the interval and extension) is rejected if it:

- is empty (`.ps1`, `.5s.ps1`);
- consists only of whitespace, or begins or ends with whitespace (`clock .5s.ps1`);
- has an empty dot-separated segment: it begins or ends with `.`, or contains `..`;
- (whole file name) contains a bidirectional control character (see step 2);
- begins with a reserved Windows device name followed by the end of the name or a dot: `CON`, `PRN`, `AUX`, `NUL`, `COM0`–`COM9`, `LPT0`–`LPT9`, and the superscript-digit forms `COM¹`–`COM³` and `LPT¹`–`LPT³`, in any case.

Names may contain spaces inside them, any Unicode letters, symbols and emoji, and any number of dots. No Unicode normalization is applied.

### Examples

| File name | Result |
| --- | --- |
| `clock.5s.ps1` | name `clock`, every 5 s, PowerShell |
| `cpu.500ms.ps1` | name `cpu`, every 500 ms |
| `weather.10m.py` | name `weather`, every 10 min, Python |
| `github.notifications.5m.py` | name `github.notifications`, every 5 min |
| `clock.ps1` | name `clock`, manual refresh |
| `CLOCK.5S.PS1` | name `CLOCK`, every 5 s |
| `météo du jour.10m.py` | name `météo du jour`, every 10 min |
| `tool.v2.ps1` | name `tool.v2`, manual (`v2` is not an interval attempt) |
| `chart.3d-view.ps1` | name `chart.3d-view`, manual (`-` makes `3d-view` not an interval attempt) |
| `2024.ps1` | name `2024`, manual (a single segment is always the name) |
| `backup.2024.ps1` | name `backup.2024`, manual, with the informational note `TW102` |
| `clock.5.ps1` | name `clock.5`, manual, with `TW102` (a forgotten unit is not an error, but it is pointed out) |
| `clock.0s.ps1` | rejected: zero interval (`TW112`) |
| `clock.-1s.ps1` | rejected: negative interval (`TW111`) |
| `clock.1.5s.ps1` | rejected: fractional interval (`TW110`) |
| `clock.999999999999h.ps1` | rejected: out of range (`TW113`) |
| `clock.249ms.ps1`, `clock.31d.ps1` | rejected: out of range (`TW113`) |
| `clock.5sec.ps1`, `clock.1w.ps1` | rejected: unknown unit (`TW110`) |
| `clock.+5s.ps1`, `clock.+5.ps1`, `clock.1,5s.ps1`, `clock.٥s.ps1` | rejected: sign, decimal comma, non-ASCII digit (`TW110`) |
| `clock.-5.ps1` | rejected: negative (`TW111`) |
| `5s.ps1` | rejected: interval without a name (`TW101`) |
| `readme.md`, `tool.exe`, `tool.5s.exe`, `clock.5s` | not candidates: ignored silently |

### Ambiguous dotted names

The convention cannot distinguish every dotted name from an interval. TrayWisp resolves the ambiguity with the rules above. Where a guess would be needed, it rejects instead of guessing:

| File name | Possible meanings | TrayWisp |
| --- | --- | --- |
| `backup.2024.ps1` | "backup 2024", manual; or a forgotten unit | accepted as the name `backup.2024`, manual, with the `TW102` note. |
| `server.2.5m.ps1` | "server 2", every 5 min; or a fractional `2.5m` | rejected as fractional. Rename to `server-2.5m.ps1`. |
| `chapter.2nd.ps1`, `video.1080p.ps1`, `screen.4k.ps1` | a name; or a mistyped unit like `5sec` | rejected: digit-initial alphanumeric segments are interval attempts with an unknown unit. Rename to `chapter-2nd.ps1` and so on. |
| `model.3d.ps1` | "3D model", manual | accepted as **every 3 days**, inherent to the convention. Rename to `model-3d.ps1`. |
| `clock.5m.ps1` | — | always minutes. Months are not a unit. |

**Tradeoff.** A segment such as `1080p` has exactly the same shape as a mistyped interval such as `10min`, `5sec`, `2hr`, `1w` or `30secs`. A list of "time-like" words would be open-ended and language-dependent (`5seg`, `2std`). Accepting all of them as names would also silently turn interval typos into manual-refresh plugins. TrayWisp therefore treats every digit-initial alphanumeric segment as an interval attempt. A bare number is the one shape that can never be a valid interval, because intervals always have a unit, so it is accepted as part of the name, with a note.

## Languages

The extension alone decides the language. TrayWisp does not read shebang lines, resolve interpreters, inspect installed runtimes or execute anything during discovery.

| Extensions | Language | Execution support |
| --- | --- | --- |
| `.ps1` | PowerShell | **Supported** (Phase 4): PowerShell 7 or Windows PowerShell. |
| `.py` | Python | **Supported** (Phase 4): Python 3. |
| `.js` | JavaScript | **Supported** (Phase 4): Node.js. |
| `.sh` | Bash / POSIX shell | **Deferred.** Recognized and discovered, with no committed execution design. |
| `.bat`, `.cmd` | Windows batch | **Deferred.** `cmd.exe` parses arguments differently (see [security.md](security.md#process-launch-explicit-arguments-not-shell-command-strings)). |

`PluginExecutionSupport` is `Supported` or `Deferred`; interpreter availability is decided separately at run time (see [execution-engine.md](execution-engine.md#supported-languages)). Discovery itself never resolves interpreters or runs anything. Any other extension is not a plugin, including `.exe`, `.com`, `.msi`, `.vbs`, `.psm1`, `.pyw`, `.mjs` and compiled-plugin conventions such as xbar's `.cswift` and `.cgo`. TrayWisp never discovers arbitrary executables as plugins.

## Embedded metadata

### Syntax

Metadata is written as tags in **comments** of the plugin file, conventionally near the top:

```powershell
# <traywisp.title>Clock</traywisp.title>
# <traywisp.version>1.0</traywisp.version>
```

```python
# <xbar.title>Weather</xbar.title>
# <xbar.author>Jane Doe, John Roe</xbar.author>
```

Rules:

- A tag is `<namespace.field>value</namespace.field>`, **opened and closed on the same line**. Several tags may share a line.
- Tags are only read from lines that look like comments in the plugin's language (see [Comments](#comments)). Tags on code lines, such as `Write-Output "<xbar.title>x</xbar.title>"`, are ignored. The first such line is reported with the informational `TW215`, so an author can tell why a tag had no effect. Because the script is not tokenized, a comment-shaped line inside a multi-line string literal is read too (see below).
- Namespace and field names are matched case-insensitively. The closing tag must have the same namespace and field, ignoring case.
- Tag names consist of ASCII letters, digits, `.`, `_` and `-`, and may be at most 64 characters.
- Markup that is not in a recognized namespace (HTML, generics such as `List<string>`, comparisons) is ignored silently.
- Values are trimmed of surrounding whitespace and otherwise kept **verbatim**. XML entities and CDATA are not decoded (`&amp;` stays `&amp;`). Tabs inside values are kept.
- Metadata is entirely optional.

### Comments

A line counts as a comment if, after leading whitespace, it starts with a line-comment marker, if it starts a block comment, or if it lies inside one. A block comment's text ends at its closing marker.

| Language | Line comments | Block comments |
| --- | --- | --- |
| PowerShell | `#` | `<#` … `#>` |
| Python, Bash | `#` | none |
| JavaScript | `//` | `/*` … `*/` (including `/** … */`) |
| Batch | `::`, `REM`, `@REM` (case-insensitive, followed by whitespace or the end of the line) | none |

The parser recognizes **comment-shaped lines**. It is not a tokenizer for any of these languages, so the rules are deliberately simple, and they are line-based:

- Comment markers that appear later in a line are not recognized, because they could be inside a string literal. A trailing comment such as `Get-Date # <xbar.title>X</xbar.title>` is therefore ignored.
- A block comment must start at the beginning of a line (after whitespace) to be recognized.
- A string literal on a single line never supplies metadata, because the line does not start with a comment marker.
- **Multi-line string literals are not recognized.** A line *inside* a PowerShell here-string, a Python triple-quoted string (including a docstring) or a shell here-document that happens to start with a comment marker is read as a comment, and tags on it **are extracted**. Lines of such a literal that do not start with a comment marker are ignored like code.

xbar's convention ("the comment characters can be anything") is broader. TrayWisp's language-specific rule accepts every documented example of that convention and stops ordinary code lines from supplying metadata. It does not guarantee that every extracted tag came from a real comment. This is acceptable because metadata is inert, untrusted descriptive text: it is only displayed, and it never establishes a plugin's identity, approval or behaviour.

### Namespaces and fields

Recognized namespaces, in **precedence order**:

1. `traywisp`
2. `swiftbar`
3. `xbar`
4. `bitbar`

Descriptive fields, accepted in all four namespaces:

| Field | Model property | Notes |
| --- | --- | --- |
| `title` | `Title` | Display title. |
| `version` | `Version` | Kept exactly as written. Semantic versioning is not required (`v1.0`, `2024.11`, `nightly` are all fine). |
| `author` | `Author` | By convention comma-separated. Kept as one string. |
| `author.github` | `AuthorGitHub` | By convention comma-separated user names. Kept as one string. |
| `desc` | `Description` | Short description. |
| `dependencies` | `Dependencies` | By convention comma-separated. Kept as one string. |
| `abouturl` | `AboutUrl` | Text only. Not validated, never fetched. |
| `image` | `Image` | Text only. Not validated, never fetched. |

SwiftBar documents the descriptive fields under the `xbar`/`bitbar` namespaces. TrayWisp also accepts them under `swiftbar` and `traywisp`.

### Display title

`PluginDescriptor.DisplayTitle` is the resolved `title`, or otherwise the plugin **name** from the file name (for example `github.notifications`). The name is not otherwise prettified.

### Precedence and duplicates

For each field, independently:

1. Only non-empty, valid occurrences take part. Empty values are treated as absent (`TW212`), so an empty `traywisp.title` does not hide an `xbar.title`.
2. The occurrence in the **highest-precedence namespace** wins.
3. Within that namespace, the **first occurrence** in the file wins.
4. Ignored occurrences with a **different** value are reported:
   - same namespace: `TW230` *field conflict*, Warning;
   - lower-precedence namespace: `TW231` *field overridden*, Info. Overriding xbar metadata with `traywisp.*` tags is a legitimate use.
5. Ignored occurrences with an **identical** value (ordinal comparison) are merged silently. Many plugins carry both `xbar.*` and `bitbar.*` copies.

The outcome depends only on the file content, never on timing or environment.

### Unsupported directives

These documented SwiftBar and xbar tags change behaviour or UI. TrayWisp **does not implement** them. Each occurrence produces `TW221` (Warning) and has no effect:

| Namespace | Directives |
| --- | --- |
| `swiftbar` | `hideAbout`, `hideRunInTerminal`, `hideLastUpdated`, `hideDisablePlugin`, `hideSwiftBar`, `schedule` (cron), `refreshOnOpen`, `collapseOnClose`, `runInBash`, `type` (for example `streamable`), `environment`, `persistentWebView` |
| `xbar`, `bitbar` | `var` (user variables passed as environment variables), `droptypes` |

In particular, a plugin that relies on `swiftbar.schedule` instead of a file-name interval is a **manual-refresh** plugin in TrayWisp. Streaming, environment injection and the SwiftBar menu-control flags do not exist in TrayWisp yet. Other unknown fields in a recognized namespace produce `TW220` (Info).

SwiftBar's metadata stored in an extended attribute (for binary plugins) is not read.

### Encoding

The scanned bytes are decoded strictly:

| File starts with | Decoded as |
| --- | --- |
| `EF BB BF` | UTF-8 (BOM skipped) |
| `FF FE` | UTF-16 little-endian (BOM skipped). This is what Windows PowerShell 5.1 writes for "Unicode". |
| `FE FF` | UTF-16 big-endian (BOM skipped) |
| `FF FE 00 00` or `00 00 FE FF` | UTF-32: not supported (`TW201`) |
| anything else | UTF-8 without BOM |

- Invalid byte sequences (for example Windows-1252 text, overlong forms, encoded surrogates, lone UTF-16 surrogates, an odd UTF-16 byte count) produce `TW200` (Warning). The file's metadata is then ignored as a whole, and the plugin is still discovered with its file-name fallback title. Save scripts as UTF-8 to use non-ASCII metadata.
- Decoded text containing NUL characters (binary files, or UTF-16 without a BOM) produces `TW201` and metadata is ignored.
- Line endings may be LF or CRLF. A lone CR is not a line break. Inside a value it counts as a control character.

### Limits

| Limit | Value | When exceeded |
| --- | --- | --- |
| Bytes scanned from the start of a file | 65,536 (64 KiB) | Later content is ignored. The last, possibly cut-off line is dropped, and `TW202` (Info) is reported. A multi-byte character cut at the boundary is not an encoding error. |
| Tags in recognized namespaces inside comments, including malformed ones | 128 | `TW211` (Warning). Scanning stops. |
| Tags outside comments | not read | `TW215` (Info), first occurrence only. |
| Value length, after trimming | 1,024 UTF-16 code units | `TW213` (Warning). The value is ignored, not truncated. |
| Tag name length | 64 characters | `TW210` (Warning). |
| Control characters in a value (except tab) | none allowed | `TW214` (Warning). The value is ignored. |

The parser uses no regular expressions and no XML parser. Each scan is linear in the bounded input, apart from a closing-tag search and a nested-markup check per recognized tag, both capped by the tag limit. The number of diagnostics per file is bounded (at most about twice the tag limit), and each message stays short. Tests feed whole 64 KiB comments made of adversarial fragments to verify this.

### Treatment as untrusted data

Metadata is author-supplied text from a file that has not been approved. TrayWisp:

- never evaluates, executes or interprets it, and never resolves entities, DTDs or external references;
- never fetches `abouturl`, `image` or any other URL, and does not validate them. Later consumers must apply a scheme allow-list before using them;
- does not echo metadata values in diagnostic messages. Diagnostics name only tags, which are limited to 64 ASCII characters, and line numbers.

## Discovery

`PluginDiscovery.Discover(rootPath, options, cancellationToken)` in `TrayWisp.Runtime` scans one plugin root and returns a `PluginDiscoveryResult`.

### Root

- The root must be given explicitly as a **fully qualified** path. Relative paths throw `ArgumentException`. No default plugin directory exists yet.
- A missing root (`TW300`), a root that is a file (`TW301`), a root that is a symbolic link, junction or other reparse point (`TW302`), and an unreadable root are reported as diagnostics with no plugins. They do not throw.
- A root marked offline or stored remotely (*Offline*, *Recall on open*, *Recall on data access*) is refused with `TW313` (Error) before it is listed, because listing it could make Windows fetch it.
- Only the root itself is checked for being a reparse point. Its ancestors are not.

### Traversal

- Directories are scanned recursively, depth first. Each directory listing is sorted by name (ordinal case-insensitive, then ordinal), so traversal order and results are deterministic.
- These entries are **skipped silently**: names beginning with `.` (files and directories, for example `.git`), entries with the Windows *Hidden* attribute, and files whose extension is not a [recognized language](#languages).
- **Reparse points are never followed.** Symbolic links, junctions, mount points and other reparse points, whether files or directories, are skipped and reported with `TW311` (Info). Discovery therefore does not intentionally leave the root.
- **Remote storage is never read.** Entries with the *Offline*, *Recall on open* or *Recall on data access* attributes are skipped with `TW313` (Info), because reading them could make Windows fetch their content. Hierarchical storage and cloud placeholders set these attributes.
- A directory whose name cannot be part of an identity is skipped with `TW312`. This includes a trailing space or dot, a reserved device name, a bidirectional control character, or (on non-Windows platforms) characters such as `\` or `:`.
- Discovery is **read-only**. Candidate files are opened for reading only, with `FileShare.ReadWrite | FileShare.Delete`, and only the first 64 KiB + 1 byte are read. The extra byte detects truncation. Nothing is created, modified, deleted, locked exclusively or executed, and no permissions or attributes are changed. A test verifies this with content hashes, timestamps and attributes.
- **Change re-checks.** The attributes in a directory listing can be stale by the time an entry is used, so each entry's attributes are read again twice:
  - **before use**: immediately before a subdirectory is listed and immediately before a candidate file is opened. If it has become a reparse point or a remote-storage entry, or changed between file and directory, it is not listed or opened at all;
  - **after use**: after a directory is listed and after a file is read. If it has changed in the same way, the listing or the bytes read are discarded.

  A change is reported with `TW311` (Warning) for a link, reparse point or change of kind, and `TW313` (Warning) for remote storage, and the result is marked incomplete. The message says whether the entry was not used or what was read was discarded. These checks narrow link-swap races, but do not eliminate them (see [Security limitations](#security-limitations)).

### OneDrive and other cloud-synchronized folders

OneDrive Files On-Demand and similar providers represent synchronized files and folders as reparse points, and usually also set the attributes above. Under the current policy, a plugin root or plugin inside such a folder is skipped. A root of this kind is refused, and the `TW302`/`TW311` message points out the likely cloud provider. This is deliberate:

- reading a placeholder can trigger a download, and discovery never fetches anything;
- .NET does not expose the reparse tag, so a cloud placeholder cannot be told apart from other reparse types (for example app execution aliases) without Windows-specific interop;
- treating some reparse points as ordinary files would weaken the "never follow reparse points" rule, which the later approval phase relies on.

Keep plugins in a local folder that is not synchronized. A future default plugin directory should therefore live outside folders that OneDrive commonly redirects, such as Documents and Desktop. Supporting synchronized folders would need an explicit, documented design, for example reading the reparse tag through interop and accepting only fully hydrated cloud files.

### Identity

- A plugin's identity (`PluginId`) is its **path relative to the root**, including directories, file name and extension, with `/` as the separator: `work/github.notifications.5m.py`.
- The original casing is preserved, but identities **compare ordinal case-insensitively**, approximating Windows file-name semantics. (NTFS uses its own upper-case table, which can differ from .NET's ordinal case folding for a few characters.) No Unicode normalization is applied, matching NTFS. Equality, hashing and ordering are consistent: two identities compare as equal exactly when `Equals` is true.
- Files with the same name in different directories are different plugins (`home/clock.5s.ps1` and `work/clock.5s.ps1`).
- An identity is **relative to its root**. The same identity under two different roots refers to two different files. Anything that stores per-plugin state must be keyed by the normalized plugin root **and** the identity, never by the identity alone. Approval records (Phase 3, see [trust-model.md](trust-model.md#approval-identity)) are keyed this way and also bound to the content hash.
- Results are ordered by identity.

### Collisions

Case-sensitive directories are possible on Windows, for example ones created from WSL or with `fsutil file setCaseSensitiveInfo`. In them, several entries can have names that differ only in case, and their identities would collide. Two identities can collide only if, in the directory where their paths first differ, two entries' names differ only in case. TrayWisp therefore checks each directory listing **before** reading anything:

- two or more **candidate plugin files** whose names differ only in case: none of them is used;
- two or more **directories** whose names differ only in case: none of them is scanned, even if their contents would not collide. This way an unreadable or partly scanned twin cannot hide a collision.

Collision detection needs the **complete** listing of a directory: an entry that was not enumerated could be a case variant of one that was. A directory whose listing was cut off by the entry budget (see [Limits](#limits-and-cancellation)) therefore contributes **no** plugins and none of its subdirectories are scanned, whichever entries happened to be enumerated. This is reported with `TW341` and makes the result incomplete.

Each group produces one `TW330` error, listing up to five names, with the path of the first. Because the check runs on names, before files are opened, a colliding file that is locked, unreadable or has broken metadata cannot let its twin be selected. TrayWisp never picks an arbitrary winner. A file and a directory with the same name do not collide, because their identities differ. Collision exclusion is deliberate, so it does not make the result incomplete. Excluded plugins are simply absent until the names are fixed.

### Limits and cancellation

| Option (`PluginDiscoveryOptions`) | Default | Meaning |
| --- | --- | --- |
| `MaxDepth` | 8 (at most 64) | Deepest directory level scanned. The root is level 0. Deeper directories produce `TW340` and are not listed. The cap of 64 bounds recursion. |
| `MaxEntries` | 10,000 | Directory entries enumerated across the whole scan (files and directories, including ignored ones). A listing that does not fit is discarded with `TW341`, and no further directories are listed. |
| `MaxCandidates` | 500 | Candidate files (recognized extension and valid file name) read. Discovery stops with `TW342`. |

Any positive `MaxEntries` and `MaxCandidates` value, including `int.MaxValue`, is honoured exactly, without overflow.

**Entry budget.** `MaxEntries` is a single budget for the whole scan, charged as entries are **enumerated**, not when they are later processed. Each directory is listed with the budget that remains, so a parent's entries that are held in memory until its subdirectories have been scanned are already paid for. The budget includes ignored entries (dot-prefixed, hidden, unrelated files), entries of listings that are later discarded, and entries never processed because a limit stopped discovery. In total, at most `MaxEntries` entries are retained.

To tell a directory that exactly fits from one that does not, discovery enumerates **one look-ahead entry** beyond the remaining budget. It is not retained or counted. The first listing that needs it is truncated, after which no further directory is listed, so the look-ahead happens at most once per scan and at most `MaxEntries` + 1 entries are ever enumerated. A directory that is empty needs no budget, so a scan that uses the budget exactly is still complete.

**Truncated listings.** When a listing is truncated:

- none of that directory's entries are used, and none of its subdirectories are scanned (see [Collisions](#collisions)). `TW341` is reported with the directory's path (no path for the root);
- no further directories are listed. Entries of listings that were already complete, such as later files of a parent directory, are still processed, so their valid results are kept;
- `IsComplete` is `false`.

When the candidate limit stops discovery, nothing further is processed. In both cases the plugins found so far are returned. *Which* entries a truncated listing contained depends on the file system's enumeration order, but that no longer affects the result, because the whole listing is withheld. A scan that completes is deterministic.

**Cancellation** is cooperative. The token is checked before each directory, for each entry while a directory is being listed, after each listing, while classifying a listing's entries, before each entry is processed, and before the result is returned. A cancelled token throws `OperationCanceledException`, and no partial result is returned. A synchronous file-system call that is already in progress (listing a slow directory, opening or reading a file) is not interrupted; cancellation takes effect at the next check.

### Failure isolation

A single broken plugin never prevents discovery of the others:

| Situation | Result | `IsComplete` |
| --- | --- | --- |
| Rejected file name | `TW1xx` error with the path. The file is not a plugin. | unchanged |
| Malformed or badly encoded metadata | The plugin is discovered with fallback metadata. Diagnostics go in `PluginDescriptor.Diagnostics`. | unchanged |
| File cannot be opened (locked, access denied) | `TW320` error. The file is not a plugin. | `false` |
| File deleted between listing and reading | `TW321` warning. | `false` |
| Entry changed into a link, or between file and directory, after it was listed | `TW311` warning. The entry is not listed or opened (checked before use), or what was read is discarded (checked after use). | `false` |
| Entry became offline or stored remotely after it was listed | `TW313` warning, as above. | `false` |
| Directory listing exceeds the remaining entry budget | `TW341` warning. None of the directory's entries are used; no further directories are listed. | `false` |
| Directory cannot be listed (access denied, deleted, I/O error) | `TW310` warning, or error for the root. Siblings are still scanned. | `false` |

### Result

- `Plugins`: discovered `PluginDescriptor`s (identity, parsed file name, metadata, diagnostics, display title). `PluginDescriptor.Diagnostics` holds the plugin's file-name note (`TW102`) followed by its metadata diagnostics.
- `Diagnostics`: discovery-level diagnostics in traversal order.
- `IsComplete`: `false` if part of the tree was not examined, or a candidate's state is unknown (see the table above). Entries skipped on purpose (dot-prefixed, hidden, reparse points, remote storage, invalid names, collisions) do not count.
- `EntriesInspected`: directory entries enumerated and counted against `MaxEntries`, never more than `MaxEntries`. It includes ignored entries and entries of discarded listings, and excludes the single look-ahead entry.
- `RootPath` (normalized, without a trailing separator).

**Incomplete results.** A plugin missing from an incomplete result may still exist, for example because it was locked for a moment while an editor saved it. Later phases must **not** treat absence from an incomplete scan as removal: scheduling (Phase 5) must not cancel or forget a plugin on that basis, and approval handling must not discard a record on that basis. Only a complete result shows that a plugin is gone. The scheduler keeps such a plugin as *uncertain*, suspends its launches, withholds its output until a fresh trust check confirms it, and never touches its approval (see [scheduling.md](scheduling.md#applying-a-scan)).

## Diagnostics

Every diagnostic has a severity, a stable code (shown as `TW` + number), an English message, and optionally a root-relative `Path` and a one-based `Line`. Output-parsing diagnostics use the separate range `TW400`–`TW499` (see [output-protocol.md](output-protocol.md#diagnostics)), and trust diagnostics `TW500`–`TW599` (see [trust-model.md](trust-model.md#diagnostics)).

Messages are safe to display. Metadata values are never included. File names, paths and operating-system messages are cut to a bounded length and quoted with `PluginDiagnosticText.Quote`, which shows control, format (including bidirectional), separator, unassigned and lone-surrogate characters, and the quote character itself, as `\uXXXX`. A literal backslash is shown as `\\`, so text that merely looks like an escape (a file literally named `a\u202Eb.ps1`, possible outside Windows) is shown as `'a\\u202Eb.ps1'` and cannot be mistaken for the escaped character, shown as `'a\u202Eb.ps1'`. Windows paths in messages therefore show doubled backslashes.

`Path` holds the **raw** path for programmatic use. `PluginDiagnostic.ToString()` formats a diagnostic as `Severity TWcode 'path':line: message`, quoting and escaping the path the same way and cutting it to `PluginDiagnostic.MaxPathLengthInText` (260) characters, so a path cannot add line breaks, reorder the text or imitate another diagnostic. Any other display of `Path` must escape it too.

| Code | Name | Severity | Meaning |
| --- | --- | --- | --- |
| TW100 | InvalidName | Error | Invalid plugin name or file name (including bidirectional controls). |
| TW101 | MissingName | Error | No name before the interval or extension. |
| TW102 | NumericNameSegment | Info | The segment before the extension is a bare number, taken as part of the name. |
| TW110 | MalformedInterval | Error | Interval attempt that is malformed. |
| TW111 | NegativeInterval | Error | Negative interval. |
| TW112 | ZeroInterval | Error | Zero interval. |
| TW113 | IntervalOutOfRange | Error | Below 250 ms, above 30 days, or overflowing. |
| TW200 | MetadataInvalidEncoding | Warning | Not valid UTF-8/UTF-16. |
| TW201 | MetadataUnsupportedEncoding | Warning | UTF-32, or NUL characters present. |
| TW202 | MetadataScanTruncated | Info | Only the first 64 KiB were scanned. |
| TW210 | MetadataMalformedTag | Warning | Unclosed, mismatched, nested, attributed or overlong tag. |
| TW211 | MetadataTagLimitReached | Warning | More than 128 tags. |
| TW212 | MetadataEmptyValue | Info | Empty value treated as absent. |
| TW213 | MetadataValueTooLong | Warning | Value over 1,024 characters ignored. |
| TW214 | MetadataInvalidValue | Warning | Value with control characters ignored. |
| TW215 | MetadataTagOutsideComment | Info | A tag in code or a string literal was ignored (first occurrence only). |
| TW220 | MetadataUnknownField | Info | Unknown field in a recognized namespace. |
| TW221 | MetadataUnsupportedDirective | Warning | Known SwiftBar/xbar directive with no effect in TrayWisp. |
| TW230 | MetadataFieldConflict | Warning | Different values for one field in one namespace. The first wins. |
| TW231 | MetadataFieldOverridden | Info | Overridden by a higher-precedence namespace. |
| TW300 | RootNotFound | Error | Root does not exist. |
| TW301 | RootNotDirectory | Error | Root is not a directory. |
| TW302 | RootIsReparsePoint | Error | Root is a link or junction. |
| TW310 | DirectoryUnreadable | Warning (Error for the root) | Directory could not be listed. |
| TW311 | ReparsePointSkipped | Info, or Warning when the entry changed during the scan | Link or junction not followed. |
| TW312 | InvalidPathSegment | Warning | Path cannot form an identity. |
| TW313 | RemoteStorageSkipped | Info; Warning when the entry changed during the scan; Error for the root | Offline or remotely stored entry not read. |
| TW320 | FileUnreadable | Error | Candidate could not be read. |
| TW321 | FileDisappeared | Warning | Candidate deleted or made inaccessible during the scan. |
| TW330 | IdentityCollision | Error | Candidate files, or directories, whose names differ only by case. All of them excluded. |
| TW340 | DepthLimitReached | Warning | Directory deeper than `MaxDepth`. |
| TW341 | EntryLimitReached | Warning | A listing exceeded the remaining `MaxEntries` budget. Its entries were withheld and no further directories were listed. |
| TW342 | CandidateLimitReached | Warning | `MaxCandidates` reached. |

## Compatibility with SwiftBar, xbar and Crossbar

TrayWisp implements a **compatible subset** of the publicly documented SwiftBar and xbar plugin conventions. It is written independently, from their documentation, and contains no code from those projects. It does **not** claim full compatibility. Crossbar was consulted as a further reference.

| Topic | SwiftBar / xbar (as documented) | TrayWisp |
| --- | --- | --- |
| File name | `{name}.{time}.{ext}` | Same convention. |
| Interval units | SwiftBar: `ms`, `s`, `m`, `h`, `d`. xbar documents `s`, `m`, `h`, `d`. | `ms`, `s`, `m`, `h`, `d`, case-insensitive, limited to 250 ms – 30 days. |
| Malformed intervals | Not specified. | Rejected with a diagnostic, never treated as part of the name. A bare number (`backup.2024`) is part of the name, with an informational note. |
| Languages | Any executable file. The shebang selects the interpreter. | Only `.ps1`, `.py`, `.js` (executable) and `.sh`, `.bat`, `.cmd` (execution deferred). No arbitrary executables. Shebangs are not used. |
| Executable bit | Required (`chmod +x`). | Not applicable on Windows. Not checked. |
| Metadata tags | `<xbar.*>` / `<bitbar.*>` "usually in comments", with "any" comment characters: title, version, author, author.github, desc, image, dependencies, abouturl, plus `var`, `droptypes`. | Descriptive fields in the `traywisp`, `swiftbar`, `xbar`, `bitbar` namespaces, read only from comments in the language's own syntax (see [Comments](#comments)). `var` and `droptypes` are recognized but unsupported. |
| Namespace precedence | Not specified. | `traywisp` > `swiftbar` > `xbar` > `bitbar`, with documented duplicate handling. |
| SwiftBar directives | `hide*`, `schedule`, `refreshOnOpen`, `collapseOnClose`, `runInBash`, `type` (streamable), `environment`, `persistentWebView`. | Not implemented. Reported as `TW221`. |
| Metadata in extended attributes | SwiftBar, for binary plugins. | Not supported. |
| Subfolders | SwiftBar traverses nested folders, including symlinks. | Traverses nested folders up to `MaxDepth`. **Symlinks and junctions are never followed.** |
| Hidden entries | SwiftBar ignores hidden folders (dot prefix or the hidden flag). | Ignores dot-prefixed files and folders and entries with the Windows Hidden attribute. |
| `.swiftbarignore` | Supported by SwiftBar. | Not supported. |
| Plugin identity | SwiftBar uses the file name as plugin ID. | Root-relative path, case-insensitive, so equal file names in different folders do not clash. |
| Variables file | xbar stores `var` values in a JSON file alongside the plugin. | Not supported. Such `.json` files are ignored. |

**Crossbar** (Flutter/Dart, cross-platform) documents the same `<name>.<interval>.<extension>` convention with `s`, `m`, `h` and `d` units. It keeps plugin metadata and settings in a sidecar JSON file next to the script rather than in header tags. It also supports Lua, YAML, Dart, Go and Rust plugins. TrayWisp does not read Crossbar sidecar files and does not recognize those extensions.

## Security limitations

Discovery is designed not to execute, modify or follow anything, but it is **not a complete security boundary**:

- **Filesystem races (TOCTOU).** Reparse-point checks first use the attributes returned by the directory listing. Each directory and file is checked again immediately before it is listed or opened, and once more after, and is skipped or its result discarded if it has become a link (see [Traversal](#traversal)). These checks do **not** eliminate races; they are path-based, and the window between a check and the operation that follows it is small but real:
  - an entry swapped for a link just after the pre-use check and swapped back before the post-use check is not noticed;
  - a directory *above* the current one can be replaced by a junction while its subtree is being scanned, and later paths then resolve through it.

  Closing these windows needs handle-relative opens (opening each entry relative to an already-opened parent directory, without following links). .NET does not offer that, and discovery does not use Windows-specific interop. The Phase 3 trust checks do, for the one path being approved or checked: they open each component without following links, hold it open and verify the final path (see [trust-model.md](trust-model.md#file-system-validation)). Execution must re-verify trust before every launch anyway. The consequence for discovery is limited to reading at most 64 KiB of a file the current user can already read, and extracting inert metadata from it. Nothing is executed or written.
- **Ancestors of the root** are not checked for reparse points. A root reached through a linked parent directory is scanned.
- **Hard links** are not reparse points and cannot be detected cheaply. A hard link inside the root to a file elsewhere on the same volume is discovered like any other file.
- **Special files on non-Windows platforms.** On Linux and macOS, a FIFO or device file with a plugin extension is not detected before it is opened, and opening a FIFO can block. Windows directories cannot contain such entries, and the product targets Windows. Core and Runtime build on other platforms only for development and testing.
- **Case and Unicode.** Identity comparison approximates NTFS case-insensitivity and may differ for rare characters. Bidirectional controls are rejected in plugin file and directory names. Other lookalike characters (homoglyphs, zero-width characters) are not detected. Metadata values may contain format characters (control characters are rejected). Diagnostic messages escape them, but interfaces that display names and metadata must also take this into account (planned for the desktop phases).
- **Anyone who can write to the plugin directory** can add files that will be discovered. Discovery is not approval: no discovered plugin runs until the approval and execution phases exist, and the trust model in [security.md](security.md) then applies.
