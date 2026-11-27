# Output protocol

This document specifies how TrayWisp parses the text a plugin prints: the SwiftBar/xbar-style format of header titles, a dropdown menu, formatting parameters and action parameters.

**Status (Phase 2, used by Phase 4).** The parser described here is **implemented and tested** as a library in `TrayWisp.Core/Output`. The execution engine ([execution-engine.md](execution-engine.md)) captures, decodes and parses plugin output with it. TrayWisp still **cannot display** titles or menus (Phases 7 and 9). Parsed actions are inert data. Nothing in this phase opens a URL, starts a process or refreshes a plugin.

Related documents: [plugin-format.md](plugin-format.md) (file names, metadata, discovery) and [security.md](security.md) (trust model).

## Contents

- [Overview](#overview)
- [Input](#input)
- [Structure](#structure)
- [Line grammar](#line-grammar)
- [Formatting parameters](#formatting-parameters)
- [Text and Unicode](#text-and-unicode)
- [Actions](#actions)
- [Duplicates, conflicts and unknown parameters](#duplicates-conflicts-and-unknown-parameters)
- [Limits](#limits)
- [Error handling and recovery](#error-handling-and-recovery)
- [Diagnostics](#diagnostics)
- [Compatibility with SwiftBar, xbar and Crossbar](#compatibility-with-swiftbar-xbar-and-crossbar)
- [Security](#security)
- [Examples](#examples)

## Overview

```text
PluginOutputParser.Parse(text, isTruncated = false)   Core, pure function, never throws
→ PluginOutputParseResult
    Status        Complete | Failed
    Output        PluginOutput (PluginOutput.Empty when Failed)
      Titles      OutputTitle[]   → MenuItem Item, bool ShowInDropdown
      Items       MenuNode[]      → MenuItem | MenuSeparator
    Diagnostics   OutputDiagnostic[] (TW400–TW499, at most 256)
    DiagnosticsTruncated
```

| Type (`TrayWisp.Core.Output`) | Role |
| --- | --- |
| `PluginOutputParser` | `Parse(ReadOnlySpan<char> text, bool isTruncated = false)` and the limit constants. |
| `PluginOutputParseResult` | Status, output, diagnostics. |
| `PluginOutput` | Header titles and top-level menu nodes. `PluginOutput.Empty` has neither. |
| `OutputTitle` | A header title: a `MenuItem` (always without children) and its `dropdown` preference. |
| `MenuNode`, `MenuItem`, `MenuSeparator` | The menu tree. A `MenuItem` has `Text` (the full label), `Format`, `Action`, `IsDisabled`, `IsChecked`, `Children` and `ToDisplayText()`. Every node has its one-based `Line`. |
| `TextFormat`, `TextColor`, `OutputColor` | Formatting preferences. Colours are plain RGB values, not WPF or System.Drawing types. |
| `OutputAction`: `RefreshAction`, `UrlAction`, `CommandAction` | Action requests. `RefreshesPlugin` says whether the producing plugin should be refreshed afterwards. |
| `OutputDiagnostic`, `OutputDiagnosticCode` | Structured diagnostics with stable codes, using the existing `PluginDiagnosticSeverity`. |

The model is immutable. Collections are `ImmutableArray`, properties are get-only (or `internal init`), and constructors of types with invariants are internal, so only the parser creates them. These combinations cannot be constructed:

- a `MenuItem` with an action that is disabled or has children;
- an `OutputTitle` with children;
- a failed result with output.

The parser has no file system, process, network, clock or environment access. `CoreIsolationTests` enforces this for all of Core.

## Input

- **Decoded standard output only.** The input is the plugin's stdout as text. Standard error must not be mixed in. Choosing the encoding, decoding bytes and enforcing byte limits are the execution engine's job: 1 MiB of stdout, strict UTF-8 (or UTF-16 with a byte order mark), never replacement characters (see [execution-engine.md](execution-engine.md#output-encoding)). The parser's own limits (below) apply to the decoded text.
- **Complete output only.** The parser cannot tell a complete output from a prefix of one. A cut can leave a line that is still valid but means something else: `Run | shell=editor.exe param1=document.txt` cut after `doc` is a valid command with the argument `doc`. A missing final line feed is **not** treated as a sign of truncation, because many plugins legitimately omit it. The caller must therefore say when the text is incomplete by passing `isTruncated: true`. The parse then fails with `TW405`, and nothing is exposed, not even a syntactically valid prefix. **The execution engine knows whether capture was complete** (implemented in Phase 4). It passes `isTruncated: true` whenever stdout exceeded its byte limit, the process was stopped by a timeout or cancellation, reading failed, or the run ended before stdout reached end-of-file, and it never passes such stdout as complete input. It also publishes parsed output only for a run that exited with code 0 (see [execution-engine.md](execution-engine.md#parser-integration)).
- **Byte order mark.** One leading U+FEFF is skipped. U+FEFF anywhere else is ordinary text. The BOM counts toward the input length limit.
- **Line endings.** Lines end at LF. A CR immediately before an LF is part of the line ending, so LF and CRLF give identical results. A final LF ends the last line and does not start an empty one.
- **Lone CR.** A CR that is not followed by LF is **not** a line break. It is a control character inside the line, and is replaced in display text (see [Text and Unicode](#text-and-unicode)). Because CR is white space, a line that consists only of CRs is blank, and a trailing CR does not stop a line from being a separator.
- **Blank lines.** A line that is empty or consists only of white space (`char.IsWhiteSpace`, which includes spaces, tabs, CR and no-break spaces) is ignored everywhere, without a diagnostic. It still counts toward the line limit, the line-length limit and the line numbers. A blank line does not end a submenu.
- **One frame.** The whole input is one output. A SwiftBar streaming delimiter makes the whole parse fail (`TW416`), because combining frames would show a menu the plugin never printed (see [Unsupported features](#unsupported-features)).

## Structure

### Header and body

- Lines before the first separator line `---` are **header titles**. The first `---` is consumed and is not a menu separator.
- Without any `---`, every non-blank line is a header title, and the menu is empty.
- Output that starts with `---` has no titles. **The parser never invents a default title.** Presentation code may supply a fallback, such as the plugin's display title.
- Titles keep their order (`PluginOutput.Titles`). Several titles are meant to rotate in the status area. Rotation itself is future presentation work.
- Header lines are never nested. A title starting with hyphens, such as `--:--` or `-----`, is literal text.
- `dropdown=false` on a title sets `OutputTitle.ShowInDropdown` to `false`: the title should appear only in the status area, not again at the top of the dropdown. Titles are stored once, in `Titles`, and are not duplicated into `Items`.
- Titles may have formatting and actions like menu items. A title's action can only be reached through its dropdown entry, so a title with `dropdown=false` keeps its action but presentation code may never offer it.

### Separators and nesting

Every line, in the header and the body, is first checked against the line-length limit (blank lines included; an over-long line fails the whole parse, `TW410`), then skipped if blank, then checked for a streaming delimiter (fails the whole parse, `TW416`). After the header, each remaining line is classified in this order:

1. **Blank** lines are skipped.
2. **Separator.** The line, with trailing white space removed, consists only of hyphens and has no leading white space. With *h* hyphens:
   - *h* = 3, 5, 7, ... (2*d* + 3): a separator at depth *d* = (*h* − 3) / 2;
   - *h* even (`--`, `----`, ...): **not** a separator. Reported as `TW414` and dropped;
   - *h* = 1: an ordinary item labelled `-`.
3. **Item.** The depth *d* is the number of leading hyphens divided by two, rounded down. The label starts after 2*d* hyphens. An odd hyphen count leaves one hyphen in the label (`---5 °C` is `-5 °C` at depth 1). Leading white space means depth 0 and is part of the label, so `  --Item` is not nested.

| Line | Meaning |
| --- | --- |
| `---` | separator at depth 0 (the first one ends the header) |
| `-----` | separator inside a depth-0 item's submenu (depth 1) |
| `-------` | separator at depth 2 |
| `Item` | item at depth 0 |
| `--Item` | item at depth 1 |
| `----Item` | item at depth 2 |
| `---Item` | item at depth 1 labelled `-Item` |
| `--`, `----` | dropped (`TW414`) |
| `  --Item` | item at depth 0 labelled `  --Item` (`--Item` when trimmed) |
| `--- \| size=10` | not a separator: item at depth 1 labelled `- ` |

### Hierarchy

The parser keeps a stack of *open* items: the most recent item at each depth that can still receive children.

- A node at depth 0 becomes a top-level node.
- A node at depth *d* > 0 becomes the last child of the open item at depth *d* − 1. If there is none, the node is an **orphan**. It is dropped and reported as `TW412`. Missing parents are never invented, and an orphan is never attached to an unrelated item.
- After a node is placed, items deeper than it are closed. An item then becomes open at its own depth. **Separators never become open**, so they cannot be parents.
- When a line at depth *d* is dropped for any reason (empty label, too deep, malformed separator), items at depth *d* and deeper are closed. The dropped line's intended children are then orphans too. They never attach to the dropped line's previous sibling. Malformed subtrees are discarded rather than repaired.
- Order is preserved at every level. Leading, consecutive and trailing separators are kept as written. Collapsing them is left to presentation.
- **Parent actions.** An item that receives a child (an item or a separator) loses its action, because opening a submenu and running an action would conflict. This is reported once, as `TW415`, on the parent's line. The children are kept.

The algorithm is a single pass with a stack of at most `MaxDepth` + 1 entries. Freezing the tree recurses at most `MaxDepth` + 1 levels.

### Empty labels

A line whose label shows nothing is dropped with its parameters and is reported as `TW413`. A label shows nothing when, after unescaping and after removing directional formatting characters, it is empty or every character in it is one of:

- white space;
- a format character (Unicode category Cf: zero-width spaces and joiners, directional marks, soft hyphens, U+FEFF, tag characters);
- a Default_Ignorable_Code_Point (Unicode 16.0), which adds among others the Hangul fillers U+115F, U+1160, U+3164 and U+FFA0, the variation selectors U+FE00–U+FE0F and U+E0100–U+E01EF, the Mongolian free variation selectors U+180B–U+180F and U+034F COMBINING GRAPHEME JOINER;
- U+2800 BRAILLE PATTERN BLANK.

Examples are `| href=...`, ` | refresh=true`, and labels consisting only of U+200B, U+202E, U+3164, U+2800 or a standalone U+FE0F. A tooltip made only of such characters is treated as absent. This prevents invisible, clickable items.

These characters only decide whether a **whole** label or tooltip is invisible. They are never removed from visible text: `a` U+2800 `b`, an emoji followed by U+FE0F, or a Hangul filler followed by a vowel are kept exactly as written. Combining marks, letters of any script, ZWJ emoji sequences and valid surrogate pairs are not rejected.

This is a **best-effort** check, not a guarantee. A font may lack a glyph, render a character as empty space, or render an accepted label too small or faint to see. Presentation code must not rely on the parser to guarantee that every accepted label is visible.

## Line grammar

The content of a line, after any nesting prefix, is a label optionally followed by parameters:

```text
line        = label [ "|" parameters ]
label       = *( "\|" / any-character-except-"|" )      ; the first unescaped "|" ends the label
parameters  = *delimiter [ parameter *( 1*delimiter parameter ) ] *delimiter
delimiter   = SP / HTAB / "|"
parameter   = name "=" value                             ; no white space around "="
name        = ALPHA *( ALPHA / DIGIT / "_" / "-" / "." ) ; ASCII only, at most 64 characters
value       = quoted / bare
quoted      = DQUOTE *( not-DQUOTE / 2DQUOTE ) DQUOTE    ; followed by a delimiter or the end
            / "'" *( not-"'" / "''" ) "'"
bare        = 1*( any-character-except-delimiter-or-quote )
```

### Labels

- The **first unescaped `|`** separates the label from the parameters. `\|` stands for a literal `|` in a label. This is the only escape in labels. Every other backslash is literal, so `C:\Temp`, `\\server\share` and `50\%` are kept as written.
- Consequence: a label cannot end with a literal backslash immediately followed by the separating `|`. Write a space before it: `Path C:\ | color=red`.
- `\\|` is a backslash followed by an escaped pipe, not a backslash and a separator.
- The label keeps its surrounding white space, including the space before `|`. `trim` (default on) removes it for display.
- Labels may contain `=`, quotes and any other text.

### Parameters

- Parameters are separated by **any run of spaces, tabs and pipes**, so `a=1 b=2`, `a=1 | b=2` and `a=1|b=2` are equivalent. Leading, trailing and repeated delimiters (`Item |`, `Item || a=1 |`) are allowed.
- **Names** are ASCII and matched case-insensitively (`COLOR=red` is `color=red`). A name must start with a letter.
- **No white space around `=`.** `color = red` and `color= red` are malformed. This stops `param1= param2=x` from being read as `param1` = `param2=x`.
- **Bare values** run to the next space, tab or `|`. They may contain `=` (`href=https://example.com/?a=b`) and backslashes. They must not contain quotes. `font=O'Brien` is malformed: quote the whole value instead.
- **Quoted values** use `"…"` or `'…'`. Inside them, spaces, tabs, pipes, `=` and the other quote character are literal. The matching quote character is written **doubled**: `"say ""hi"""` is `say "hi"`, and `'it''s'` is `it's`. A closing quote must be followed by a delimiter or the end of the line (`"a"b` is malformed).
- **An empty value must be quoted**: `param1=""` or `param1=''`. An unquoted empty value is malformed.
- **Backslashes are always literal in values.** `"C:\Temp\"` is `C:\Temp\`: the backslash does not escape the closing quote. `\n`, `\t` and `\x41` are two or four literal characters.
- **Nothing is expanded.** Environment variables (`%PATH%`, `$HOME`), `~`, wildcards, command substitution (`$(...)`, backticks) and escape sequences are kept as written. This is neither Unix shell nor Windows command-line tokenization.

### Malformed parameter syntax

Any of the following makes the parameter section **malformed** (`TW430`, Error):

- an unmatched quote;
- a missing `=`;
- a missing value;
- white space around `=`;
- a quote inside a bare value;
- text directly after a closing quote;
- a name that does not start with an ASCII letter, contains other characters, or is longer than 64.

Then **no parameter on the line is used**. The label is kept with default formatting, and there is **no action**. Parameters that came before the error are not applied either, so malformed syntax can never yield a partial action or a refresh request. The diagnostic gives the column of the problem.

## Formatting parameters

| Parameter | Value | Default | Invalid value |
| --- | --- | --- | --- |
| `color` | A colour, or `light,dark` (see [Colours](#colours)). | none | ignored (`TW433`) |
| `font` | A font family name, 1–128 characters, without control or bidirectional characters. Not checked for availability. | none | ignored |
| `size` | Points, from 1 to 256: ASCII digits with an optional `.` and one or two decimals (`12`, `12.5`). | none | ignored |
| `length` | Whole number from 1 to 1,024: the maximum number of user-perceived characters to display. | none | ignored |
| `trim` | `true` or `false`: remove surrounding white space for display. | `true` | `true` |
| `tooltip` | Text, sanitized like labels. A tooltip that shows nothing is absent. | none | — |
| `dropdown` | `true` or `false`. **Titles only.** | `true` | `true` |
| `disabled` | `true` or `false`: greyed out, not activatable. | `false` | **`true`** |
| `checked` | `true` or `false`: show a check mark. | `false` | `false` |

- **Booleans** are exactly `true` or `false`, ASCII case-insensitive. `yes`, `1`, `on` and `" true"` are invalid.
- **Numbers** are parsed culture-independently. Only ASCII digits are accepted: no sign, exponent, group separator, decimal comma or white space. Arbitrarily long digit strings are rejected without overflow.
- **`disabled` fails safe.** An invalid, duplicated or over-long `disabled` value counts as `disabled=true`, so a typo cannot make an item that was meant to be disabled clickable.
- **`dropdown` on a menu item** has no meaning and is ignored with `TW437`. The item is still shown.
- **Display text.** `MenuItem.Text` keeps the full label. `MenuItem.ToDisplayText()` applies the preferences: it trims surrounding white space (if `trim`), then, if `length` is set and the text is longer, keeps the first `length` **extended grapheme clusters** and appends `…` (U+2026). Surrogate pairs, ZWJ emoji sequences, flags, skin-tone modifiers and combining marks are never split.
- Rendering, font availability and theme selection are deferred to the presentation phases.

### Colours

`OutputColor` is a 24-bit RGB value. `TextColor` holds a `Light` and a `Dark` colour.

- **Names:** the 148 named colours of [CSS Color Module Level 4](https://www.w3.org/TR/css-color-4/#named-colors), including both `gray` and `grey` spellings and `rebeccapurple`, matched ASCII case-insensitively. Not accepted: `transparent`, `currentcolor`, system colours (`ButtonText`), and non-ASCII lookalikes.
- **Hexadecimal:** `#RGB` or `#RRGGBB`, with digits in either case. `#f80` is `#FF8800`. Not accepted: a missing `#`, alpha (`#RGBA`, `#RRGGBBAA`), `rgb()`/`hsl()` and other functions.
- **Light/dark pair:** `color=black,white`, exactly one comma and no white space. A single colour applies to both appearances.

## Text and Unicode

Labels and tooltips are untrusted display text. They are converted as follows, and any change except the tab rule is reported once per value (`TW420`, with the column of the first affected character):

| Characters | Treatment |
| --- | --- |
| Tab (U+0009) | replaced by a space, silently |
| Other control characters: C0 (including NUL, ESC and a lone CR), DEL, C1 (including NEL and CSI) | replaced by U+FFFD |
| Line and paragraph separators (U+2028, U+2029) | replaced by U+FFFD |
| Lone (unpaired) UTF-16 surrogates | replaced by U+FFFD |
| Directional formatting characters: embeddings, overrides, isolates and their terminators (U+202A–U+202E, U+2066–U+2069) | removed |
| Everything else | kept |

- Raw terminal control sequences therefore cannot reach display text. `ESC[31m` becomes `U+FFFD[31m`: it is visible and inert, never interpreted.
- Text is kept otherwise: letters of any script, valid surrogate pairs, emoji including ZWJ sequences, flags and modifiers, combining marks, ZWJ/ZWNJ, soft hyphens and the bidirectional *marks* ALM, LRM and RLM (U+061C, U+200E, U+200F). These marks have no scope and are needed in right-to-left text. No Unicode normalization is applied.
- ANSI colours, Markdown, `:emoji:` shortcodes and SF Symbols are **not** interpreted. The text stays literal.
- **Action values are stricter.** Executables, arguments and font names must contain no control characters (tab included), no U+2028/U+2029, no lone surrogates and no bidirectional controls at all, including ALM, LRM and RLM. Otherwise the value is rejected, not repaired. URLs must be strictly well-formed ASCII (see [URLs](#urls)).

Homoglyphs and invisible characters inside otherwise visible text (such as zero-width spaces) are not filtered. Display surfaces must keep this in mind.

## Actions

An action is an **untrusted request**, not permission to do anything. The parser only produces data. Any future code that carries out an action must first pass the Phase 3 trust check (the producing plugin is still approved under the same approval identity, `PluginExecutionResult.Approval`, see [trust-model.md](trust-model.md#approval-generations-and-stale-output)) and use the same execution safeguards as plugin runs (direct launch with explicit arguments, timeouts, bounded output). The Phase 4 engine executes no action. See [security.md](security.md#output-driven-actions).

| Parameters | Result |
| --- | --- |
| `refresh=true` | `RefreshAction`: run the producing plugin again. |
| `href=URL` | `UrlAction`. |
| `href=URL refresh=true` | `UrlAction` with `RefreshesPlugin`: refresh the plugin after the URL has been handed off. |
| `shell=EXE` (alias `bash`), optional `paramN`, `terminal` | `CommandAction`. |
| `shell=EXE refresh=true` | `CommandAction` with `RefreshesPlugin`: refresh after the command completes. |
| `refresh=false` alone | no action |

`refresh` never means "refresh all plugins".

### Commands

- `shell` and `bash` are **two names for one parameter**: the executable. Neither name implies evaluation by a shell or by bash. Using both on one line is a duplicate.
- **The executable is kept exactly as written**: it is not split into a command and arguments, not trimmed, not resolved against `PATH`, not checked for existence and not rewritten for another operating system. `shell="notepad.exe C:\notes.txt"` names an executable called `notepad.exe C:\notes.txt`. It must not be empty or have leading or trailing white space, and must not contain control or bidirectional characters (`TW441`).
- **Arguments** are `param0`, `param1`, ... `param32`, ASCII case-insensitive:
  - numbering starts at `param0` **or** `param1`, because existing plugins use both;
  - the indices present must be **contiguous** from that start. A gap (`param1`, `param3`) is rejected, never closed by shifting arguments;
  - at most **32 arguments**, so the indices are `param0`–`param31` or `param1`–`param32`;
  - rejected (`TW442`, or `TW432` for duplicates): a duplicated index, leading zeros (`param01`), a missing index (`param`), an index above 32 (including indices that would overflow an integer), and values with control characters, lone surrogates or bidirectional controls;
  - arguments are ordered by **numeric** index (`param10` after `param9`). Empty arguments (`param1=""`) are kept.
- `terminal=true|false` is recorded as a **preference** (`CommandAction.RunInTerminal`, `null` when not given). Nothing is launched. SwiftBar and xbar open a terminal when `terminal` is omitted. The Phase 4 engine executes no action; whether TrayWisp will honour terminals for actions is decided with output-driven actions (Phase 7).
- `paramN` or `terminal` without `shell`/`bash` is an orphan (`TW444`). The line's whole action is suppressed, including `refresh`.

### URLs

`href` must be an absolute URL that meets every rule below, otherwise `TW440` and no action:

- it starts with `http://` or `https://` (ASCII case-insensitive), followed by a non-empty authority. `https:example.com`, `https:///x`, `//example.com` and relative URLs are rejected;
- it consists only of characters RFC 3986 allows unescaped: ASCII letters and digits, `-._~`, the reserved characters `:/?#[]@!$&'()*+,;=`, and `%`. Spaces, control characters, non-ASCII characters, backslashes and the URI-illegal characters `|`, `{`, `}`, `^`, `` ` ``, `<`, `>` and `"` are rejected. International domain names must be written in punycode (`xn--...`), and other characters percent-encoded (`https://example.com/a%7Cb`, not `"https://example.com/a|b"`);
- every `%` starts a percent escape of exactly two hexadecimal digits. `%zz`, a trailing `%` or `%2`, and `%%41` are rejected;
- it has no user information (`https://user@host`, `https://example.com@evil.example`);
- .NET accepts it as an absolute URI with a host and an `http` or `https` scheme, and `Uri.IsWellFormedOriginalString()` is `true` for it. On .NET 10 this also rejects structural errors such as a second `#` or `[`/`]` outside an IPv6 host;
- .NET's host is the host as written, ignoring case. This rejects forms that would silently denote another spelling of the destination, such as numeric IPv4 shorthands (`http://2130706433/`, `http://127.1/`, `http://0x7f.0.0.1/`), percent-encoded hosts (`https://exa%6Dple.com/`) and IPv6 zone identifiers, which .NET drops from the host (`http://[fe80::1%25eth0]/`).

Ordinary IPv4 addresses (`http://127.0.0.1:8080/`), IPv6 literals (`http://[::1]:8080/`, `http://[2001:db8::1]/`), punycode domains, query strings, fragments and percent escapes such as `%20` and `%3A` are accepted.

Other schemes, such as `file`, `javascript`, `data`, `mailto`, `ftp`, `ms-settings` and custom protocols, are rejected. No network access happens.

**Percent escapes are never decoded, expanded or normalized by the parser.** `UrlAction.Url.OriginalString` is exactly the text written. Percent-encoded control characters (`%00`–`%1F`, `%7F`) are valid URI syntax and are accepted: they are common in query strings (`?body=line1%0Aline2`), and they stay escaped. On .NET 10, `AbsoluteUri` keeps them escaped as well. `AbsoluteUri` is still a normalized form, though: it may lower-case the scheme and host, drop a default port, unescape unreserved characters (`%41` becomes `A`) and remove dot segments (`/a/../b` becomes `/b`, `%2e%2e` included). Under RFC 3986 these forms are equivalent and the host does not change. `Uri.ToString()` goes further and unescapes characters such as `%20` and `%7C`, so it must never be used to display or hand off the URL. Presentation should display and hand off `OriginalString`.

## Duplicates, conflicts and unknown parameters

| Situation | Result |
| --- | --- |
| A formatting parameter appears twice (any case), even with the same value | It is ignored (its default applies; `disabled` counts as `true`). `TW432` Warning. |
| An action parameter appears twice (`href`, `shell`/`bash`, `terminal`, `refresh`, the same `paramN`) | No action. `TW432` Error. |
| `href` and `shell`/`bash` on one line | No action. `TW443`. The parser never chooses one. |
| `paramN` or `terminal` without `shell`/`bash` | No action. `TW444`. |
| Invalid `href`, executable, arguments, `refresh` or `terminal` value, or an over-long action value | No action, **including** any `refresh` request. |
| `disabled=true` (or an invalid `disabled`) with a valid action | The action is not exposed. `TW445` Info. |
| Item with children and an action | The action is removed and the children kept. `TW415`. |
| Malformed parameter syntax or more than 64 parameters | No parameters, no action. `TW430`/`TW431`. |
| Unknown parameter, line has no action parameters | Ignored. `TW434` Warning. |
| Unknown parameter, line has an action parameter (`href`, `shell`, `bash`, `paramN`, `terminal` or `refresh`, any value) | **No action**: TrayWisp cannot tell whether the parameter would have changed the action. `TW434` Error. |

Any action problem suppresses the **whole** action. A partial action is never produced.

### Unsupported features

| Parameters or feature | Kind | Treatment |
| --- | --- | --- |
| `image`, `templateImage`, `sfimage`, `sfcolor`, `sfcolorN`, `sfsize`, `sfconfig`, `badge` | appearance | ignored, `TW435` Info. Images are not decoded, symbols not resolved. |
| `ansi`, `md`, `emojize`, `symbolize` | appearance | ignored, `TW435` Info. The text stays literal. |
| `alternate` | behaviour (an Option-key alternative to the previous item) | suppresses the action, `TW436`. Ignoring it would leave a hidden alternative clickable as an ordinary item. |
| `key`, `shortcut` | behaviour (keyboard activation) | suppresses the action, `TW436` |
| `webview`, `webvieww`, `webviewh` | behaviour (open `href` in an embedded web view) | suppresses the action, `TW436`. A browser would be a different behaviour. |
| `stdin` | behaviour (streamable plugins) | suppresses the action, `TW436` |
| `~~~` line (SwiftBar streaming delimiter) | streaming | **the whole parse fails**, `TW416` Error: no titles, menu items or actions from any frame are exposed. A delimiter is a line that is exactly `~~~`, optionally followed by white space, in the header or the body. A label that merely contains `~~~` (`~~~~`, ` ~~~`, `~~~ x`, `~~~ \| refresh=true`) is an ordinary item. |
| JSON output (first non-blank line starts with `{`) | protocol | `TW417`. The text is still parsed as text, usually as titles. The versioned JSON protocol is planned for Phase 10. Crossbar's JSON is not adopted. |

On a line without action parameters, behavioural parameters are ignored with a `TW436` Warning.

## Limits

These are parser-level limits on decoded text. The execution engine separately caps captured stdout at 1 MiB before decoding, so a run can capture more text than the parser accepts; the parser's limits still apply.

| Limit | Value | When exceeded |
| --- | --- | --- |
| Input length (UTF-16 code units, BOM included) | 262,144 | **Fails** (`TW400`). Checked before anything else. |
| Lines (physical, blank lines included) | 4,096 | **Fails** (`TW401`). Counted before parsing. |
| Line length (physical line without its LF or CRLF ending; blank lines and a lone CR included) | 16,384 | **Fails** (`TW410`, with the line number). Checked before blank lines are skipped. A line exactly at the limit is accepted. |
| Header titles | 64 | **Fails** (`TW402`). |
| Menu nodes (items and separators at all levels; titles not included; dropped lines not counted) | 2,048 | **Fails** (`TW403`). |
| Submenu depth (top level is 0) | 8 | The node is dropped with its children (`TW411`). |
| Parameters per line | 64 | The line's parameters are malformed (`TW431`). |
| Parameter value length (after unquoting) | 4,096 | The value is invalid (`TW433`). |
| Parameter name length | 64 | Malformed (`TW430`). |
| Command arguments | 32 | No action (`TW442`). |
| Diagnostics | 256 | See [Diagnostics](#diagnostics). |

An over-long line fails the parse instead of being dropped, because a dropped line cannot be classified reliably: a separator padded with trailing spaces and an item have different depth rules, and an oversized first separator would otherwise leave the whole menu in the header.

Processing is linear in the input: no regular expressions, no recursion except the bounded freeze of the tree, no splitting of the whole input into arrays, and integer parsing that cannot overflow. Malformed quoted values are scanned once. Tests run adversarial inputs at every limit.

## Error handling and recovery

| Outcome | When | Result |
| --- | --- | --- |
| `Complete`, no diagnostics | Well-formed output. | Full model. |
| `Complete`, with diagnostics | Malformed lines or parameters. | Usable lines are kept. Dropped lines, ignored parameters and suppressed actions are reported. |
| `Failed` | A global limit was exceeded (input length, line count, **line length**, title count or menu node count), the caller reported the output as **truncated** (`TW405`), or the output contains an unsupported **streaming delimiter** (`TW416`). | `Output` is `PluginOutput.Empty`: **no titles, no menu, no actions**. A truncated menu could hide a separator or a disabled flag, or run actions out of context, so no partial menu is exposed. The diagnostics found so far are returned, followed by the fatal error. For truncated input, the parse stops before reading any line, so `TW405` is the only diagnostic. |

Recovery rules for single lines:

- **Label kept, parameters ignored:** malformed syntax, more than 64 parameters.
- **Label kept, parameter ignored:** an invalid formatting value (safe default applies).
- **Label kept, action suppressed:** any action problem (see above).
- **Line dropped:** empty or invisible label, even hyphen run.
- **Line and its children dropped:** orphan, too deep.

The parser never throws for any input. It contains no exception handlers: invariant violations would be programming errors and are not hidden.

## Diagnostics

`OutputDiagnostic` has a severity, a code (shown as `TW` + number), an English message, and an optional one-based `Line` and `Column` (UTF-16 code units in the physical line, including any nesting prefix). `ToString()` gives `Severity TWcode line:column: message`.

- **Range.** Output codes are `TW400`–`TW499`, separate from the file-name, metadata and discovery codes `TW100`–`TW399` in [plugin-format.md](plugin-format.md#diagnostics). Numbers are stable.
- **No leaks.** Messages never repeat labels, tooltips, URLs, executables, argument values or any other values. They name parameters only, by their documented name, or for unknown parameters by the name as written, quoted, escaped and cut to 32 characters with `PluginDiagnosticText.Quote` (names are ASCII by grammar).
- **Order.** Diagnostics are in detection order, then the fatal error, if any, and then the summary. Detection order is mostly, but **not strictly**, ascending physical line order: `TW415` names the parent's earlier line but is reported when its first child is processed, after any diagnostics of the lines in between. Within a line, diagnostics follow the parser's checks, not column order. Sort by `Line` and `Column` if line order is needed.
- **Bound.** At most 256 diagnostics are returned. At most 254 ordinary diagnostics are kept. Two places are reserved, so a fatal error and a `TW404` summary ("*N* further diagnostics were not recorded") always fit. `DiagnosticsTruncated` is `true` when the summary is present. **Whether a diagnostic is recorded never affects parsing**: a malformed action is suppressed even if its diagnostic was not recorded.
- **Severity.** *Error*: output rejected, a line or subtree dropped because it is defective, or an action not created. *Warning*: something ignored, replaced or defaulted while the line is kept. *Info*: unsupported appearance parameters, and actions hidden by `disabled`.

| Code | Name | Severity | Meaning |
| --- | --- | --- | --- |
| TW400 | OutputTooLong | Error | Input over 262,144 characters. Fatal. |
| TW401 | TooManyLines | Error | Over 4,096 lines. Fatal. |
| TW402 | TooManyTitles | Error | Over 64 titles. Fatal. |
| TW403 | TooManyMenuNodes | Error | Over 2,048 menu nodes. Fatal. |
| TW404 | DiagnosticLimitReached | Warning | Further diagnostics were not recorded. |
| TW405 | OutputTruncated | Error | The caller reported the output as truncated. Fatal. |
| TW410 | LineTooLong | Error | Physical line over 16,384 characters, blank or not. Fatal. |
| TW411 | MenuDepthExceeded | Error | Node deeper than 8 dropped with its children. |
| TW412 | OrphanedMenuNode | Error | Node without a parent one level up dropped with its children. |
| TW413 | EmptyLabel | Warning | Line with an empty or invisible label dropped. |
| TW414 | MalformedSeparator | Warning | Even run of hyphens dropped. |
| TW415 | ParentActionRemoved | Warning | Action removed from an item with a submenu. |
| TW416 | StreamingNotSupported | Error | `~~~` streaming delimiter. Fatal. |
| TW417 | JsonNotSupported | Warning | JSON-looking output parsed as text. |
| TW420 | UnsafeText | Warning | Characters in a label or tooltip replaced or removed. |
| TW430 | MalformedParameters | Error | Parameter syntax error; all parameters of the line ignored. |
| TW431 | TooManyParameters | Error | Over 64 parameters; all ignored. |
| TW432 | DuplicateParameter | Warning (formatting) or Error (action) | Parameter repeated. |
| TW433 | InvalidParameterValue | Warning (formatting) or Error (action) | Invalid, out-of-range or over-long value. |
| TW434 | UnknownParameter | Warning, or Error when it suppresses an action | Unrecognized parameter. |
| TW435 | UnsupportedParameter | Info | Known appearance parameter ignored. |
| TW436 | UnsupportedBehavior | Warning, or Error when it suppresses an action | Known behavioural parameter not supported. |
| TW437 | ParameterNotApplicable | Warning | `dropdown` on a menu item. |
| TW440 | InvalidUrl | Error | Unacceptable `href`. |
| TW441 | InvalidCommand | Error | Unacceptable executable. |
| TW442 | InvalidCommandArguments | Error | Malformed, non-contiguous or too many `paramN`, or an unsafe argument. |
| TW443 | ConflictingActions | Error | Both `href` and `shell`/`bash`. |
| TW444 | OrphanedActionParameter | Error | `paramN` or `terminal` without a command. |
| TW445 | DisabledItemAction | Info | Action of a disabled item not exposed. |

## Compatibility with SwiftBar, xbar and Crossbar

TrayWisp implements a **documented subset** of the SwiftBar and xbar text output format, written independently from their public documentation (the [SwiftBar documentation](https://swiftbar.github.io/SwiftBar/) and README, and the [xbar plugin guide](https://github.com/matryer/xbar-plugins/blob/main/CONTRIBUTING.md)). [Crossbar](https://github.com/verseles/crossbar) was consulted as a further reference. TrayWisp contains no code from these projects and **does not claim full compatibility**. Where their documentation is silent, TrayWisp chooses the stricter interpretation.

| Topic | SwiftBar / xbar (as documented) | TrayWisp |
| --- | --- | --- |
| Header, `---`, title cycling | Lines before the first `---` are titles, cycled in the menu bar. Later `---` lines are separators. | Same. Rotation is left to presentation. |
| Header titles in the dropdown | SwiftBar shows them unless `dropdown=false`. | `OutputTitle.ShowInDropdown`. |
| Submenus | xbar: `--` per level. SwiftBar's docs do not describe nesting. | `--` per level, depth ≤ 8. Orphans and jumps are dropped, never re-parented. |
| Nested separators | Not documented. | `-----`, `-------`, ... (2*d* + 3 hyphens). Even runs are rejected. |
| Parameter separators | `\|` then parameters. SwiftBar: separated by spaces. xbar examples also use `\|` between parameters. | Spaces, tabs and pipes, interchangeably. |
| Quoting | xbar examples use double quotes and backslash-escaped inner quotes. Not specified further. | Double or single quotes. Inner quotes are **doubled**. **Backslashes are literal**, so xbar's `\"` escape is not supported. This keeps Windows paths intact. |
| Literal `\|` in labels | Not documented. | `\\|`. |
| Empty and blank lines | Not documented. | Ignored. |
| `color` | "CSS color or HEX", SwiftBar `light,dark`. | 148 CSS names, `#RGB`, `#RRGGBB`, `light,dark`. No alpha. |
| `font`, `size` | Font name, number. | Font name (not checked), size 1–256. |
| `length`, `trim` | Truncate with `…` and full-text tooltip; trim defaults to `true`. | Same preferences; truncation by grapheme cluster. The full label is kept. Showing it as a tooltip is a presentation decision. |
| `dropdown` | Header only. | Header only; ignored on menu items (`TW437`). |
| `disabled`, `checked`, `tooltip` | xbar documents `disabled`; SwiftBar `checked`, `tooltip`. | Supported. An invalid `disabled` counts as disabled. |
| `href` | Absolute URL. | `http`/`https` only, with the restrictions above. |
| `bash`, `shell`, `paramN` | SwiftBar documents `bash` and `param0`, `param1`, ... xbar documents `shell` and `param1`, ... | Both names, both numberings, contiguous, at most 32. The executable is never split. |
| `terminal` | `false` runs without opening a terminal. Default opens one. | Recorded as a preference only. |
| `refresh` | Refreshes the plugin; with a command, after it finishes (xbar). Crossbar's docs contradict each other ("all plugins" vs "plugin"). | Refreshes **only the producing plugin**, also with `href`. Refresh-all is not adopted. |
| `alternate`, `key`/`shortcut`, `webview*`, `stdin` | Supported by SwiftBar or xbar. | Not supported; they suppress the action. |
| `image`, `templateImage`, `sfimage`, `sfconfig`, `sfcolor`, `sfsize`, `badge`, `ansi`, `md`, `emojize`, `symbolize` | Supported. | Not supported; ignored. No Base64 decoding, ANSI, Markdown, emoji shortcodes or symbols. |
| Streaming (`~~~`, `swiftbar.type=streamable`) | SwiftBar. | Not supported; a `~~~` line fails the whole parse (`TW416`). |
| Crossbar JSON output | Crossbar. | Not supported (planned TrayWisp JSON protocol in Phase 10 is separate). |
| Crossbar `bash='cmd --flag'` command strings | Crossbar examples pass a whole command line. | **Not adopted.** The value is one executable name; `bash=/usr/bin/open -a Safari` is malformed (unquoted spaces). |

**Uncertainties in the reference behaviour.** The references do not specify the following, and the TrayWisp rules above are deliberate choices rather than reproductions:

- quoting and escaping inside values;
- white space around `=`;
- duplicate parameters;
- how empty lines and empty labels are treated;
- nested separators;
- whether `param0` or `param1` starts the numbering (SwiftBar's examples start at `param0`, xbar's at `param1`);
- what `refresh` does alongside `href`;
- the accepted colour syntax;
- whether invalid values fall back or fail.

Plugins that rely on another tool's exact behaviour in these areas may behave differently in TrayWisp, and the diagnostics will say so.

## Security

- **Output is untrusted data.** Even an approved plugin may print text it fetched from elsewhere. The parser treats every byte as data. It never evaluates, expands or executes anything, never accesses the file system, network, clock or environment, and never resolves executables or fetches URLs.
- **Actions are requests.** Every action must pass the Phase 3 trust check and the Phase 4 execution safeguards when it is activated. A `CommandAction` names an executable and an argument list for a direct launch. It is never a shell command line. If a plugin names a shell (for example `shell=cmd.exe param1=/c ...`), that shell interprets its arguments, within the plugin's own trust (see [security.md](security.md#output-driven-actions)).
- **Fail closed.** Malformed, duplicated, conflicting, unknown or unsupported behavioural parameters suppress the action instead of guessing. An invalid `disabled` value disables. Over-limit, truncated and streaming output exposes nothing.
- **Spoofing.** Control characters, line separators and directional formatting characters are removed from display text. Action values containing them are rejected. Labels made only of invisible characters are dropped (best effort, see [Empty labels](#empty-labels)). Homoglyphs, zero-width characters inside visible text and lookalike URLs (for example `examp1e.com`) are **not** detected. Future interfaces should show the exact URL or executable and arguments behind an action.
- **Diagnostics** never contain labels or values, so they can be logged without echoing plugin output, including secrets that a plugin passes as arguments.
- **Parsing is not authorization.** A `Complete` result only means the text is syntactically acceptable. It never authorizes running a command, opening a URL or refreshing a plugin.
- **Not a sandbox.** Parsing does not make running a plugin or its actions safe. See [security.md](security.md).

## Examples

A clock with rotating titles:

```text
14:05 | font="Cascadia Mono" size=13
Thu 26 Nov | dropdown=false
---
Thursday, 26 November 2026 | disabled=true
Time zones
--Tokyo 22:05
--New York 08:05
-----
--Open world clock | href=https://time.example/world
Refresh | refresh=true
```

Commands with Windows paths:

```text
---
Task Manager | shell=taskmgr.exe terminal=false
Open editor | shell="C:\Program Files\Editor\editor.exe" param1="D:\My Files\notes.txt"
Open folder | shell="C:\Windows\explorer.exe" param1="C:\Users\Public\Documents\Logs\"
Mark all read | shell="C:\Program Files\GitHub CLI\gh.exe" param0=api param1=--method param2=PUT param3=/notifications refresh=true
Path with a literal pipe \| in its label | tooltip="A | B"
```

`param1="...\Logs\"` keeps its final backslash, because backslashes never escape. Arguments are passed as separate values, so the spaces in `D:\My Files\notes.txt` need no further quoting.

Lines TrayWisp rejects or changes:

```text
Bad | shell=cmd.exe /c del x          → malformed (unquoted space): no action
Bad | href=file:///C:/Windows         → TW440: no action
Bad | shell=a.exe param1=x param3=y   → TW442 gap: no action, arguments never shifted
Bad | href=https://example.com shell=a.exe → TW443: no action
Bad | href="https://example.com/a|b" → TW440: raw | is not allowed in a URL; write %7C
Bad | refresh=true alternate=true     → TW436: no action
--Orphan                              → TW412 when no item precedes it
~~~                                   → TW416: the whole output is rejected
```

The test fixtures in `tests/TrayWisp.Core.Tests/Output/Fixtures` contain the expected output of the sample plugins and longer original examples (clock rotation, system monitoring, GitHub-style notifications).
