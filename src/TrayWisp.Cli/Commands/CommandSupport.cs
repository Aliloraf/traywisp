using System.Globalization;
using System.Text.Json;
using TrayWisp.Cli.CommandLine;
using TrayWisp.Cli.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Cli.Commands;

// Steps shared by the commands: resolving and checking locations, creating the trust service, and common rendering. Each
// returns false after recording the failure (exit code, diagnostics, summary) in the result.
internal static class CommandSupport
{
    public const string ApprovalIsNotSandboxing =
        "Approval lets this exact script run as your Windows user, with all of your access. It is consent, not a sandbox.";

    // Resolves --root and --data-dir, or their defaults under %LOCALAPPDATA%\TrayWisp. Syntax only: nothing is accessed.
    // The local application data folder is consulted only when one of them is absent.
    public static CliPaths? ResolvePaths(CliRequest request, CliEnvironment environment, CommandResult result)
    {
        PluginRoot? baseDirectory = null;
        if (request.Root is null || request.DataDirectory is null)
        {
            baseDirectory = ResolveBase(environment, result, "Use --root and --data-dir.", "use --root and --data-dir");
            if (baseDirectory is null)
            {
                return null;
            }
        }

        return new CliPaths(
            request.Root ?? CliPaths.Child(baseDirectory!, "plugins"),
            request.Root is null,
            request.DataDirectory ?? baseDirectory!,
            request.DataDirectory is null);
    }

    // Resolves only --root, or its default, for commands that use no storage (list): with --root, the local application
    // data folder is not consulted at all, so its absence cannot make them fail. Syntax only: nothing is accessed.
    public static PluginRoot? ResolveRoot(CliRequest request, CliEnvironment environment, CommandResult result)
    {
        if (request.Root is not null)
        {
            return request.Root;
        }

        PluginRoot? baseDirectory = ResolveBase(environment, result, "Use --root.", "use --root");
        return baseDirectory is null ? null : CliPaths.Child(baseDirectory, "plugins");
    }

    private static PluginRoot? ResolveBase(CliEnvironment environment, CommandResult result, string advice, string summaryAdvice)
    {
        string? local = environment.LocalApplicationData();
        if (!string.IsNullOrEmpty(local) && PluginRoot.TryCreate(local.TrimEnd('\\', '/') + "\\TrayWisp", out PluginRoot? baseDirectory))
        {
            return baseDirectory;
        }

        Fail(result, ExitCodes.Configuration, CliDiagnostic.Error(
            CliDiagnosticCode.ConfigurationInvalid,
            "The per-user local application data folder is not available or not a supported local path, so there is no default plugin root or data directory. " + advice),
            "no default locations are available; " + summaryAdvice);
        return null;
    }

    // The trust store and run history directories must not lie inside the plugin root, nor contain it.
    public static bool CheckStorageOverlap(CliPaths paths, CommandResult result)
    {
        foreach ((string what, PluginRoot directory) in paths.StorageDirectories)
        {
            if (paths.Root.Overlaps(directory))
            {
                Fail(result, ExitCodes.Configuration, CliDiagnostic.Error(
                    CliDiagnosticCode.StorageOverlapsPluginRoot,
                    $"The {what} directory {PluginDiagnosticText.Quote(directory.Value, 260)} overlaps the plugin root {PluginDiagnosticText.Quote(paths.Root.Value, 260)}. "
                    + "Keep plugins and TrayWisp's data apart: choose another --root or --data-dir."),
                    $"the {what} directory overlaps the plugin root");
                return false;
            }
        }

        return true;
    }

    // Read-only validation of the plugin root before anything else reads it (drive type, directory chain, short names).
    public static bool ValidateRoot(CliPaths paths, CommandResult result, CancellationToken cancellationToken) =>
        ValidateRoot(paths.Root, result, cancellationToken);

    public static bool ValidateRoot(PluginRoot root, CommandResult result, CancellationToken cancellationToken)
    {
        PluginRootCheckResult check = PluginRootValidation.Check(root.Value, cancellationToken);
        if (check.IsValid)
        {
            return true;
        }

        result.AddRange(CliDiagnostic.From(check.Diagnostics));
        string summary = check.Status switch
        {
            PluginRootStatus.NotFound => "the plugin root does not exist. TrayWisp never creates it automatically; create it explicitly, for example with "
                + "New-Item -ItemType Directory -Path followed by the path, or choose another --root",
            PluginRootStatus.Unsafe => "the plugin root is not an ordinary local directory (see the diagnostics)",
            PluginRootStatus.UnsupportedPlatform => "plugin roots are only supported on Windows",
            _ => "the plugin root could not be examined (see the diagnostics)",
        };
        result.ExitCode = ExitCodes.PluginUnavailable;
        result.Summary = summary;
        string status = check.Status.ToString();
        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("root", root.Value);
            writer.WriteString("rootStatus", status);
            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("Plugin root", root.Value);
            writer.Value("Root status", status);
        };
        return false;
    }

    // Read-only check of a storage directory before its path is normalized by the store. A missing directory is fine:
    // the store creates it when it first needs it.
    public static bool CheckStorageDirectory(PluginRoot directory, string what, CommandResult result, CancellationToken cancellationToken)
    {
        StorageLocationReport report = StorageLocation.Check(directory.Value, cancellationToken: cancellationToken);
        if (report.Status is StorageLocationStatus.Ready or StorageLocationStatus.Missing)
        {
            return true;
        }

        result.AddRange(report.Diagnostics.Select(CliDiagnostic.From));
        result.ExitCode = ExitCodes.StorageFailed;
        result.Summary = $"the {what} directory cannot be used (see the diagnostics)";
        return false;
    }

    public static PluginTrustService? CreateTrustService(CliPaths paths, CliEnvironment environment, CommandResult result)
    {
        try
        {
            return environment.CreateTrustService(paths.TrustStorePath);
        }
        catch (ArgumentException)
        {
            Fail(result, ExitCodes.Configuration, CliDiagnostic.Error(
                CliDiagnosticCode.ConfigurationInvalid,
                $"The trust store path {PluginDiagnosticText.Quote(paths.TrustStorePath, 260)} is not acceptable (for example it is reached through a short 8.3 name). Use the full path with --data-dir."),
                "the trust store path is not acceptable");
            return null;
        }
    }

    public static void Fail(CommandResult result, int exitCode, CliDiagnostic diagnostic, string summary)
    {
        result.ExitCode = exitCode;
        result.Add(diagnostic);
        result.Summary = summary;
    }

    public static void WriteMetadataJson(Utf8JsonWriter writer, PluginMetadata metadata)
    {
        writer.WriteStartObject();
        writer.SafeString("title", metadata.Title);
        writer.SafeString("version", metadata.Version);
        writer.SafeString("author", metadata.Author);
        writer.SafeString("authorGitHub", metadata.AuthorGitHub);
        writer.SafeString("description", metadata.Description);
        writer.SafeString("dependencies", metadata.Dependencies);
        writer.SafeString("aboutUrl", metadata.AboutUrl);
        writer.SafeString("image", metadata.Image);
        writer.WriteEndObject();
    }

    // Metadata is untrusted, descriptive text: it is shown, never fetched, loaded or acted on.
    public static void WriteMetadataHuman(HumanWriter writer, PluginMetadata metadata)
    {
        (string Label, string? Value)[] fields =
        [
            ("Title", metadata.Title), ("Version", metadata.Version), ("Author", metadata.Author), ("Author GitHub", metadata.AuthorGitHub),
            ("Description", metadata.Description), ("Dependencies", metadata.Dependencies), ("About URL (not opened)", metadata.AboutUrl),
            ("Image (not loaded)", metadata.Image),
        ];
        bool any = false;
        foreach ((string label, string? value) in fields)
        {
            if (value is not null)
            {
                writer.Field(label, value, TerminalText.Kind.Label, 512);
                any = true;
            }
        }

        if (!any)
        {
            writer.Line("(no metadata)");
        }
    }

    public static void WriteFileNameJson(Utf8JsonWriter writer, PluginFileName fileName)
    {
        writer.SafeString("name", fileName.Name);
        writer.WriteString("language", fileName.Language.ToString());
        writer.WriteString("executionSupport", fileName.ExecutionSupport.ToString());
        if (fileName.RefreshInterval is RefreshInterval interval)
        {
            writer.WriteStartObject("refreshInterval");
            writer.WriteString("text", interval.ToString());
            writer.WriteNumber("milliseconds", (long)interval.Duration.TotalMilliseconds);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("refreshInterval");
        }
    }

    public static void WriteFileNameHuman(HumanWriter writer, PluginFileName fileName)
    {
        writer.Value("Language", fileName.ExecutionSupport == PluginExecutionSupport.Supported
            ? $"{fileName.Language} (can be run)"
            : $"{fileName.Language} (recognized; running it is not supported yet)");
        writer.Value("Refresh", fileName.RefreshInterval is RefreshInterval interval ? $"every {interval}" : "manual only (no interval in the file name)");
    }

    public static void WriteDiagnosticsJson(Utf8JsonWriter writer, string name, IEnumerable<CliDiagnostic> diagnostics)
    {
        writer.WritePropertyName(name);
        JsonReport.WriteDiagnostics(writer, diagnostics);
    }

    // A ready-to-paste PowerShell command: "traywisp COMMAND OPTIONS... -- 'OPERAND'", only when every value is printable
    // ASCII; otherwise null, and the caller shows a template instead. Untrusted values are never placed in a suggestion that
    // could be misread or contain invisible characters. All options come first and "--" ends them, so an operand that
    // starts with "-" (for example -clock.ps1) is never read as an option. The operand is always single-quoted, and so is
    // every option value that is not plain ASCII letters, digits and - _ . / (single quotes are doubled): in single quotes
    // PowerShell expands nothing ($, backticks, ;, &, parentheses and spaces are literal), and Windows PowerShell 5.1 does
    // not split a quoted argument at '.' as it can an unquoted one that starts with '-'. Option values never start with
    // "-" (the command line would not accept them as separate arguments), so such a value also gives null.
    public static string? PowerShellCommand(string command, string operand, params string[] options)
    {
        var parts = new List<string> { "traywisp", command };
        for (int i = 0; i < options.Length; i++)
        {
            string argument = options[i];
            bool isName = i % 2 == 0;
            if (!IsPrintableAscii(argument) || (isName ? !argument.StartsWith("--", StringComparison.Ordinal) : argument.StartsWith('-')))
            {
                return null;
            }

            bool plain = argument.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');
            parts.Add(plain ? argument : SingleQuoted(argument));
        }

        if (options.Length % 2 != 0 || !IsPrintableAscii(operand))
        {
            return null;
        }

        parts.Add("--");
        parts.Add(SingleQuoted(operand));
        return string.Join(' ', parts);
    }

    private static bool IsPrintableAscii(string value) => value.Length > 0 && value.All(c => c >= ' ' && c <= '~');

    private static string SingleQuoted(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // The --root and --data-dir arguments that reproduce this invocation's explicit locations.
    public static List<string> LocationArguments(CliPaths paths)
    {
        var arguments = new List<string>();
        if (!paths.RootIsDefault)
        {
            arguments.AddRange(["--root", paths.Root.Value]);
        }

        if (!paths.DataDirectoryIsDefault)
        {
            arguments.AddRange(["--data-dir", paths.DataDirectory.Value]);
        }

        return arguments;
    }

    public static string Bytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
}
