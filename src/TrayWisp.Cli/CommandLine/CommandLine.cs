using System.Globalization;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Execution;

namespace TrayWisp.Cli.CommandLine;

internal enum CommandKind
{
    List,
    Inspect,
    Approve,
    Revoke,
    Status,
    Run,
    ValidateOutput,
    Doctor,
    Logs,
}

internal enum CliOption
{
    Root,
    DataDir,
    Json,
    Sha256,
    Timeout,
    PowerShell,
    Python,
    Node,
    ShowStderr,
    Truncated,
    Limit,
}

internal enum OperandRule
{
    None,
    Required,
    Optional,
}

internal sealed record OptionSpec(CliOption Option, string Name, string? ValueName, string Description);

internal sealed record CommandSpec(CommandKind Kind, string Name, OperandRule Operand, CliOption[] Options, string Synopsis, string Summary, string[] Details);

// A complete, validated request. Everything in it has passed syntax validation; nothing has touched the file system.
internal sealed record CliRequest(CommandSpec Command)
{
    public bool Json { get; init; }

    public PluginId? PluginId { get; init; }

    public PluginRoot? Root { get; init; }

    public PluginRoot? DataDirectory { get; init; }

    public ContentFingerprint? Sha256 { get; init; }

    public TimeSpan? Timeout { get; init; }

    public string? PowerShellPath { get; init; }

    public string? PythonPath { get; init; }

    public string? NodePath { get; init; }

    public bool ShowStandardError { get; init; }

    public bool Truncated { get; init; }

    public int? Limit { get; init; }
}

// What the parser decided: global help, help for one command, the version, a request, or a usage error.
internal sealed record ParseResult
{
    public bool ShowHelp { get; init; }

    public CommandSpec? HelpFor { get; init; }

    public bool ShowVersion { get; init; }

    public CliRequest? Request { get; init; }

    public string? Error { get; init; }

    // For usage errors: the command, when the first argument named one, and whether --json was requested.
    public CommandSpec? ErrorCommand { get; init; }

    public bool ErrorWantsJson { get; init; }
}

// A small, exact parser written for TrayWisp (docs/cli.md, "Command-line syntax"): the command comes first; options and
// the operand follow in any order; "--" ends options; option values are the next argument or follow "=". Nothing is
// expanded (no wildcards, environment variables or response files), and unrecognized arguments are never echoed raw.
internal static class CommandLineParser
{
    public const int MaxArguments = 32;
    public const int MaxArgumentLength = 8192;
    public const int MinLimit = 1;
    public const int DefaultLimit = 20;

    public static readonly OptionSpec[] Options =
    [
        new(CliOption.Root, "--root", "PATH", @"Plugin root directory (default: %LOCALAPPDATA%\TrayWisp\plugins)."),
        new(CliOption.DataDir, "--data-dir", "PATH", @"Data directory holding trust\ and diagnostics\ (default: %LOCALAPPDATA%\TrayWisp)."),
        new(CliOption.Json, "--json", null, "Write one JSON document (schema version 1) to standard output."),
        new(CliOption.Sha256, "--sha256", "FINGERPRINT", "The reviewed SHA-256 fingerprint: 64 lowercase hexadecimal characters."),
        new(CliOption.Timeout, "--timeout", "SECONDS", "Run timeout in whole seconds, 1 to 300 (default 30)."),
        new(CliOption.PowerShell, "--powershell", "PATH", "Use exactly this PowerShell executable (no fallback)."),
        new(CliOption.Python, "--python", "PATH", "Use exactly this Python executable (no fallback)."),
        new(CliOption.Node, "--node", "PATH", "Use exactly this Node.js executable (no fallback)."),
        new(CliOption.ShowStderr, "--show-stderr", null, "Also show the plugin's bounded, sanitized standard error tail (never stored)."),
        new(CliOption.Truncated, "--truncated", null, "Treat the input as incomplete, so it is rejected as a whole."),
        new(CliOption.Limit, "--limit", "COUNT", "Number of most recent records, 1 to 200 (default 20)."),
    ];

    private static readonly CliOption[] Interpreters = [CliOption.PowerShell, CliOption.Python, CliOption.Node];

    public static readonly CommandSpec[] Commands =
    [
        new(CommandKind.List, "list", OperandRule.None, [CliOption.Root, CliOption.Json],
            "traywisp list [--root PATH] [--json]",
            "List discovered plugins. Discovery only: nothing is run, hashed or approved.",
            ["Shows each plugin's identity, title, language, refresh interval and execution support, and whether the scan was complete.",
             "Use 'traywisp status' for authoritative trust checks."]),
        new(CommandKind.Inspect, "inspect", OperandRule.Required, [CliOption.Root, CliOption.DataDir, CliOption.Json],
            "traywisp inspect PLUGIN_ID [--root PATH] [--data-dir PATH] [--json]",
            "Show a plugin's metadata and the SHA-256 fingerprint of its whole file, for review before approval.",
            ["Nothing is run and nothing is approved. Review the script itself, then approve the fingerprint shown."]),
        new(CommandKind.Approve, "approve", OperandRule.Required, [CliOption.Root, CliOption.DataDir, CliOption.Sha256, CliOption.Json],
            "traywisp approve PLUGIN_ID --sha256 FINGERPRINT [--root PATH] [--data-dir PATH] [--json]",
            "Approve exactly the reviewed content of a plugin.",
            ["The file is hashed again immediately before the approval is saved; if it differs from FINGERPRINT, nothing is approved.",
             "Approval lets the script run as your Windows user with all of your access. It is consent, not a sandbox.",
             "The plugin is not run."]),
        new(CommandKind.Revoke, "revoke", OperandRule.Required, [CliOption.Root, CliOption.DataDir, CliOption.Json],
            "traywisp revoke PLUGIN_ID [--root PATH] [--data-dir PATH] [--json]",
            "Remove a plugin's approval. Works when the plugin file or the root no longer exists.",
            ["The plugin file is never deleted. A run already started by another TrayWisp process is not stopped."]),
        new(CommandKind.Status, "status", OperandRule.Optional, [CliOption.Root, CliOption.DataDir, CliOption.Json],
            "traywisp status [PLUGIN_ID] [--root PATH] [--data-dir PATH] [--json]",
            "Make the authoritative trust check of one plugin, or of every discovered plugin.",
            ["Each check rehashes the file. A check that finds changed content removes the obsolete approval, so status can change the trust store.",
             "The result describes this moment only; it is not an authorization."]),
        new(CommandKind.Run, "run", OperandRule.Required, [CliOption.Root, CliOption.DataDir, CliOption.Json, CliOption.Timeout, .. Interpreters, CliOption.ShowStderr],
            "traywisp run PLUGIN_ID [--timeout SECONDS] [--powershell PATH] [--python PATH] [--node PATH] [--show-stderr] [--root PATH] [--data-dir PATH] [--json]",
            "Run one approved plugin once, through the execution engine and its launch-time trust check.",
            ["Only the named plugin runs. Actions in its output are shown as inert data and never executed.",
             "Its output is shown only if a fresh trust check after the run still confirms the approval it ran under.",
             "A summary of the run (no output) is added to the local run history."]),
        new(CommandKind.ValidateOutput, "validate-output", OperandRule.None, [CliOption.Truncated, CliOption.Json],
            "traywisp validate-output [--truncated] [--json] < FILE",
            "Parse plugin output read from redirected standard input, without running anything.",
            ["Input must be UTF-8, or UTF-16 with a byte order mark, and at most 1,048,576 bytes.",
             "A valid result proves neither that a script is safe nor that it is authorized."]),
        new(CommandKind.Doctor, "doctor", OperandRule.None, [CliOption.Root, CliOption.DataDir, CliOption.Json, .. Interpreters],
            "traywisp doctor [--powershell PATH] [--python PATH] [--node PATH] [--root PATH] [--data-dir PATH] [--json]",
            "Check the host configuration without running scripts or interpreters and without creating anything.",
            ["A found interpreter path is validated, not authenticated; its version and PowerShell execution policy are not checked."]),
        new(CommandKind.Logs, "logs", OperandRule.None, [CliOption.Root, CliOption.DataDir, CliOption.Limit, CliOption.Json],
            "traywisp logs [--limit COUNT] [--root PATH] [--data-dir PATH] [--json]",
            "Show the most recent run summaries from the local run history, oldest first.",
            ["Reading never creates, repairs or clears the history."]),
    ];

    public static ParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new ParseResult { ShowHelp = true };
        }

        CommandSpec? command = Commands.FirstOrDefault(spec => string.Equals(spec.Name, args[0], StringComparison.Ordinal));
        bool wantsJson = WantsJson(args);
        if (args.Count > MaxArguments)
        {
            return Fail(command, wantsJson, $"Too many arguments: at most {MaxArguments} are accepted.");
        }

        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].Length > MaxArgumentLength || args[i].Contains('\0', StringComparison.Ordinal))
            {
                return Fail(command, wantsJson, string.Create(CultureInfo.InvariantCulture, $"Argument {i + 1} is longer than {MaxArgumentLength} characters or contains a NUL character."));
            }
        }

        if (command is null)
        {
            return args[0] switch
            {
                "-h" or "--help" when args.Count == 1 => new ParseResult { ShowHelp = true },
                "--version" when args.Count == 1 => new ParseResult { ShowVersion = true },
                "-h" or "--help" or "--version" => Fail(null, wantsJson, $"{args[0]} takes no other arguments. Use 'traywisp COMMAND --help' for help on a command."),
                _ when args[0].StartsWith('-') => Fail(null, wantsJson, $"The command must come first; {Describe(args[0])} is not a command. Run 'traywisp --help'."),
                _ => Fail(null, wantsJson, $"Unknown command {Describe(args[0])}. Run 'traywisp --help' for the list of commands."),
            };
        }

        // Help wins over everything else after the command, up to "--".
        for (int i = 1; i < args.Count && args[i] != "--"; i++)
        {
            if (args[i] is "-h" or "--help")
            {
                return new ParseResult { HelpFor = command };
            }
        }

        return ParseCommand(command, args, wantsJson);
    }

    // Whether --json appears as an option (before "--"), so usage errors can be reported as JSON.
    public static bool WantsJson(IReadOnlyList<string> args)
    {
        for (int i = 1; i < args.Count && args[i] != "--"; i++)
        {
            if (args[i] == "--json")
            {
                return true;
            }
        }

        return false;
    }

    public static OptionSpec Spec(CliOption option) => Options.First(spec => spec.Option == option);

    private static ParseResult ParseCommand(CommandSpec command, IReadOnlyList<string> args, bool wantsJson)
    {
        var values = new Dictionary<CliOption, string?>();
        var operands = new List<string>();
        bool endOfOptions = false;
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (endOfOptions || !arg.StartsWith('-') || arg == "-")
            {
                operands.Add(arg);
                continue;
            }

            int equals = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=', StringComparison.Ordinal) : -1;
            string name = equals < 0 ? arg : arg[..equals];
            OptionSpec? spec = Options.FirstOrDefault(option => string.Equals(option.Name, name, StringComparison.Ordinal));
            if (spec is null)
            {
                return Fail(command, wantsJson, $"Unknown option {Describe(name)}. Run 'traywisp {command.Name} --help'.");
            }

            if (!command.Options.Contains(spec.Option))
            {
                return Fail(command, wantsJson, $"The option {spec.Name} is not accepted by '{command.Name}'. Run 'traywisp {command.Name} --help'.");
            }

            if (values.ContainsKey(spec.Option))
            {
                return Fail(command, wantsJson, $"The option {spec.Name} was given more than once.");
            }

            string? value = null;
            if (spec.ValueName is null)
            {
                if (equals >= 0)
                {
                    return Fail(command, wantsJson, $"The option {spec.Name} takes no value.");
                }
            }
            else if (equals >= 0)
            {
                value = arg[(equals + 1)..];
                if (value.Length == 0)
                {
                    return Fail(command, wantsJson, $"The option {spec.Name} needs a value: {spec.Name} {spec.ValueName}.");
                }
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
            {
                value = args[++i];
            }
            else
            {
                return Fail(command, wantsJson, $"The option {spec.Name} needs a value: {spec.Name} {spec.ValueName}.");
            }

            values.Add(spec.Option, value);
        }

        int maxOperands = command.Operand == OperandRule.None ? 0 : 1;
        if (operands.Count > maxOperands)
        {
            return Fail(command, wantsJson, maxOperands == 0
                ? $"'{command.Name}' takes no operands, but {operands.Count} were given."
                : $"'{command.Name}' takes one plugin identity, but {operands.Count} operands were given.");
        }

        if (command.Operand == OperandRule.Required && operands.Count == 0)
        {
            return Fail(command, wantsJson, $"'{command.Name}' needs a plugin identity: {command.Synopsis}");
        }

        var request = new CliRequest(command) { Json = values.ContainsKey(CliOption.Json) };
        string? error = null;
        if (operands.Count == 1)
        {
            if (!PluginId.TryCreate(operands[0], out PluginId? id) || !PluginTrustKey.IsSupportedPluginId(id))
            {
                return Fail(command, wantsJson,
                    "The plugin identity is not valid. Use the plugin's path relative to the plugin root, for example clock.5s.ps1 or work/clock.5s.ps1, "
                    + "as shown by 'traywisp list': a supported plugin file name, at most 8 directories deep, no segment starting with '.'.");
            }

            request = request with { PluginId = id };
        }

        foreach ((CliOption option, string? value) in values)
        {
            request = Apply(request, option, value!, ref error);
            if (error is not null)
            {
                return Fail(command, wantsJson, error);
            }
        }

        if (command.Kind == CommandKind.Approve && request.Sha256 is null)
        {
            return Fail(command, wantsJson,
                "'approve' needs the fingerprint you reviewed: --sha256 FINGERPRINT. Run 'traywisp inspect PLUGIN_ID' to see it. Nothing is approved without it.");
        }

        if (command.Kind == CommandKind.Logs && request.Limit is null)
        {
            request = request with { Limit = DefaultLimit };
        }

        return new ParseResult { Request = request };
    }

    private static CliRequest Apply(CliRequest request, CliOption option, string value, ref string? error)
    {
        switch (option)
        {
            case CliOption.Json:
                return request;
            case CliOption.ShowStderr:
                return request with { ShowStandardError = true };
            case CliOption.Truncated:
                return request with { Truncated = true };
            case CliOption.Root or CliOption.DataDir:
                if (!PluginRoot.TryCreate(value, out PluginRoot? directory))
                {
                    error = $"The value of {Spec(option).Name} is not a supported path. Use a fully qualified path on a local drive, such as C:\\plugins: relative and "
                        + "drive-relative paths, '.' and '..' segments, doubled separators, UNC, device and alternate-data-stream paths are not accepted or expanded.";
                    return request;
                }

                return option == CliOption.Root ? request with { Root = directory } : request with { DataDirectory = directory };
            case CliOption.Sha256:
                if (!ContentFingerprint.TryParse(value, out ContentFingerprint? fingerprint))
                {
                    error = "The value of --sha256 is not a SHA-256 fingerprint: it must be exactly 64 lowercase hexadecimal characters, as shown by 'traywisp inspect'.";
                    return request;
                }

                return request with { Sha256 = fingerprint };
            case CliOption.Timeout:
                int min = (int)PluginExecutionOptions.MinTimeout.TotalSeconds;
                int max = (int)PluginExecutionOptions.MaxTimeout.TotalSeconds;
                if (!TryParseInteger(value, min, max, out int seconds))
                {
                    error = string.Create(CultureInfo.InvariantCulture, $"The value of --timeout must be a whole number of seconds from {min} to {max}.");
                    return request;
                }

                return request with { Timeout = TimeSpan.FromSeconds(seconds) };
            case CliOption.Limit:
                if (!TryParseInteger(value, MinLimit, PluginRunHistory.MaxRecords, out int limit))
                {
                    error = string.Create(CultureInfo.InvariantCulture, $"The value of --limit must be a whole number from {MinLimit} to {PluginRunHistory.MaxRecords}.");
                    return request;
                }

                return request with { Limit = limit };
            default:
                if (!InterpreterLookup.IsValidConfiguredPath(value))
                {
                    error = $"The value of {Spec(option).Name} must be a fully qualified local path to an .exe file, such as C:\\Program Files\\PowerShell\\7\\pwsh.exe.";
                    return request;
                }

                return option switch
                {
                    CliOption.PowerShell => request with { PowerShellPath = value },
                    CliOption.Python => request with { PythonPath = value },
                    _ => request with { NodePath = value },
                };
        }
    }

    // ASCII digits only: no sign, white space, group separators, exponent or culture-specific digits.
    private static bool TryParseInteger(string text, int min, int max, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 9 || !text.All(char.IsAsciiDigit))
        {
            return false;
        }

        value = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return value >= min && value <= max;
    }

    // A short, escaped description of an unrecognized argument. Long or unusual arguments are not repeated at all.
    private static string Describe(string arg) =>
        arg.Length <= 32 && arg.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? $"'{arg}'"
            : "(not shown)";

    private static ParseResult Fail(CommandSpec? command, bool wantsJson, string message) =>
        new() { Error = message, ErrorCommand = command, ErrorWantsJson = wantsJson };
}
