# Contributing to TrayWisp

Thank you for your interest in TrayWisp. The project is in early development, and its structure and conventions are still settling. Please open an issue to discuss significant changes before you start work.

## Prerequisites

- Windows 10 or Windows 11. The desktop project targets `net10.0-windows`.
- .NET SDK 10.0.404 or a later .NET 10 feature band (see [`global.json`](global.json)).
- Any editor. Visual Studio, VS Code with the C# Dev Kit, and JetBrains Rider all work with `.slnx` solutions.

## Build and test

```powershell
dotnet restore TrayWisp.slnx
dotnet build TrayWisp.slnx --configuration Release --no-restore
dotnet test --solution TrayWisp.slnx --configuration Release --no-build
```

The build treats warnings as errors and enforces the code style in [`.editorconfig`](.editorconfig). CI runs the same commands on Windows for every pull request.

## Architecture rules

Read [docs/architecture.md](docs/architecture.md) before you add projects or references. In short:

- `TrayWisp.Core` references no other TrayWisp project and contains no I/O or process code.
- `TrayWisp.Runtime` may reference `TrayWisp.Core` only.
- `TrayWisp.Cli` and `TrayWisp.Desktop` may reference Core and Runtime, never each other.
- Core and Runtime stay platform-independent (`net10.0`). Windows UI and shell code belongs in Desktop.

The build fails with `TW0001` if a project reference breaks these rules.

## Guidelines

- **Keep it small.** Avoid speculative abstractions, placeholder types and unused extension points. Add structure when a real use needs it.
- **Dependencies.** Production projects currently have no third-party dependencies. A new package needs a clear justification in the pull request. Versions go in [`Directory.Packages.props`](Directory.Packages.props).
- **Tests.** Write tests that verify behaviour or enforce a documented rule. Do not add tests only to raise the test count. Core logic should be unit-testable without a desktop session.
- **Documentation.** Keep the docs accurate. Mark planned features as planned, and update the docs in the same change as the behaviour they describe.
- **Security.** Changes to plugin discovery, approval, execution, output handling or diagnostics must keep the commitments in [docs/security.md](docs/security.md), or update that document with an explanation.

## Independent implementation

TrayWisp takes ideas from SwiftBar, xbar, Crossbar, YASB and Zebar, but it is an independent implementation:

- Do not copy source code, assets or documentation from those or other projects, whatever their license.
- Compatibility with output formats such as SwiftBar/xbar text output is implemented from publicly documented behaviour.
- If you have worked on one of those projects, that is welcome. Just write TrayWisp code fresh.

## Pull requests

- Keep each pull request focused on one change.
- Describe what changed and why, and how you verified it.
- Make sure the build and tests pass locally on Windows.
- By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
