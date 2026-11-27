# Example plugins

These scripts show the plugin file format described in [docs/plugin-format.md](../../docs/plugin-format.md).

> **TrayWisp cannot run plugins yet.** It discovers plugin files, reads their file names and metadata, and can parse output text, but it does not execute anything. These examples are here for future execution support (see [docs/roadmap.md](../../docs/roadmap.md)). Script execution and the system tray come in later phases.

| File | Language | Refresh | Metadata namespace |
| --- | --- | --- | --- |
| `clock.1m.ps1` | PowerShell | every minute | `traywisp` |
| `day-progress.10m.py` | Python | every 10 minutes | `xbar` |
| `hello.js` | JavaScript | manual (no interval) | `traywisp` |

The scripts are harmless. They read the local clock and print text. They do not touch files or the network, and they need nothing beyond their interpreter.

Output follows the SwiftBar/xbar text style: the first line is the title, and lines after `---` are menu items (see [docs/output-protocol.md](../../docs/output-protocol.md)).

An automated test discovers this directory and checks that every example is found with no diagnostics. Another test parses a recorded sample of each example's output, kept in `tests/TrayWisp.Core.Tests/Output/Fixtures/examples`, and checks that every example has such a fixture. The scripts themselves are not run.
