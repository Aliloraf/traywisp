# Example plugins

These scripts show the plugin file format described in [docs/plugin-format.md](../../docs/plugin-format.md).

> TrayWisp's execution engine can run these examples once you approve them and the interpreter is installed (see [docs/execution-engine.md](../../docs/execution-engine.md)). It is a library for now: no CLI command, scheduler or tray runs plugins yet (see [docs/roadmap.md](../../docs/roadmap.md)).

| File | Language | Refresh | Metadata namespace |
| --- | --- | --- | --- |
| `clock.1m.ps1` | PowerShell | every minute | `traywisp` |
| `day-progress.10m.py` | Python | every 10 minutes | `xbar` |
| `hello.js` | JavaScript | manual (no interval) | `traywisp` |

The scripts are harmless. They read the local clock and print text. They do not touch files or the network, and they need nothing beyond their interpreter.

Output follows the SwiftBar/xbar text style: the first line is the title, and lines after `---` are menu items (see [docs/output-protocol.md](../../docs/output-protocol.md)).

An automated test discovers this directory and checks that every example is found with no diagnostics. Another test parses a recorded sample of each example's output, kept in `tests/TrayWisp.Core.Tests/Output/Fixtures/examples`, and checks that every example has such a fixture. An optional test copies the examples to a temporary plugin root, approves them and runs them through the execution engine with the interpreters installed on the machine; it skips examples whose interpreter is missing or whose PowerShell execution policy forbids scripts.

The clock example prints day and month names in the user's culture, which may be non-ASCII, so it starts with `[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)`, as every PowerShell plugin that prints non-ASCII text must (see [docs/execution-engine.md](../../docs/execution-engine.md#output-encoding)). It computes the ISO 8601 week number itself, because `[System.Globalization.ISOWeek]` is not available in Windows PowerShell 5.1, and it works with both Windows PowerShell 5.1 and PowerShell 7.
