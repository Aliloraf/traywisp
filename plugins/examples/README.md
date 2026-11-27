# Example plugins

These scripts show the plugin file format described in [docs/plugin-format.md](../../docs/plugin-format.md).

> **TrayWisp cannot run plugins yet.** Phase 1 only discovers plugin files and reads their file names and metadata. These examples are here for future execution support (see [docs/roadmap.md](../../docs/roadmap.md)). Script execution, the output parser and the system tray come in later phases.

| File | Language | Refresh | Metadata namespace |
| --- | --- | --- | --- |
| `clock.1m.ps1` | PowerShell | every minute | `traywisp` |
| `day-progress.10m.py` | Python | every 10 minutes | `xbar` |
| `hello.js` | JavaScript | manual (no interval) | `traywisp` |

The scripts are harmless. They read the local clock and print text. They do not touch files or the network, and they need nothing beyond their interpreter.

Output follows the SwiftBar/xbar text style: the first line is the title, and lines after `---` are menu items. TrayWisp does not parse plugin output yet (planned for Phase 2).

An automated test discovers this directory and checks that every example is found with no diagnostics.
