# Security policy

## Supported versions

TrayWisp is in early development and has no releases yet. Until the first release, only the latest commit on `main` is supported. A supported-versions table will be added with the first release.

## Reporting a vulnerability

**Do not report security vulnerabilities through public issues, discussions or pull requests.**

Report them privately through GitHub's private vulnerability reporting: open the repository's **Security** tab and choose **Report a vulnerability**.

Please include:

- A description of the issue and its impact.
- Steps to reproduce, or a proof of concept.
- The affected commit or version, and your Windows version.

We will acknowledge reports as soon as we can, work with you on a fix, and credit you in the advisory unless you prefer otherwise.

## Scope

TrayWisp runs plugin scripts with your user account's privileges by design. Plugin approval is consent, not sandboxing. A plugin you approved doing something harmful is therefore not, by itself, a TrayWisp vulnerability.

Examples of issues that **are** in scope:

- A plugin running without approval, or after its approved file changed, through any execution path: scheduled runs, manual runs, CLI runs or output-driven actions. Exception: the documented time-of-check/time-of-use limitations.
- TrayWisp adding shell interpretation of its own to plugin launches or actions, or plugin output, file names or arguments otherwise causing code execution beyond what the plugin itself can do.
- Plugin discovery executing, modifying or following links to files, or being driven into unbounded memory or time by plugin files or metadata, beyond the limitations documented in [docs/plugin-format.md](docs/plugin-format.md#security-limitations).
- Timeouts, output limits, cancellation or process-tree termination failing in ways not covered by their documented limitations.
- Diagnostics or logs exposing data beyond what is documented.

See [docs/security.md](docs/security.md) for the trust model and the planned safeguards.
