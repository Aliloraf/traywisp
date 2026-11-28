using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using TrayWisp.Cli.CommandLine;
using TrayWisp.Cli.Output;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Cli.Commands;

// doctor and logs: read-only reports. doctor runs no script and no interpreter, creates nothing and does not read or lock
// the trust store; logs reads the run history and never changes it.
internal static class DiagnosticCommands
{
    private static readonly PluginLanguage[] RunnableLanguages = [PluginLanguage.PowerShell, PluginLanguage.Python, PluginLanguage.JavaScript];

    private static readonly PluginLanguage[] DeferredLanguages = [PluginLanguage.Bash, PluginLanguage.Batch];

    public static CommandResult Doctor(CliRequest request, CliEnvironment environment, string version, CancellationToken cancellationToken)
    {
        var result = new CommandResult();
        CliPaths? paths = CommandSupport.ResolvePaths(request, environment, result);
        if (paths is null)
        {
            return result;
        }

        bool windows = OperatingSystem.IsWindows();
        bool supportedWindows = OperatingSystem.IsWindowsVersionAtLeast(10);
        string? overlap = paths.StorageDirectories.Where(entry => paths.Root.Overlaps(entry.Directory)).Select(entry => entry.What).FirstOrDefault();
        PluginRootCheckResult root = PluginRootValidation.Check(paths.Root.Value, cancellationToken);
        StorageLocationReport trust = StorageLocation.Check(paths.TrustDirectory.Value, CliPaths.StoreFileName, cancellationToken);
        StorageLocationReport history = StorageLocation.Check(paths.DiagnosticsDirectory.Value, PluginRunHistory.FileName, cancellationToken);
        var options = new PluginExecutionOptions { PowerShellPath = request.PowerShellPath, PythonPath = request.PythonPath, NodePath = request.NodePath };
        InterpreterLookupResult[] interpreters = [.. RunnableLanguages.Select(language => InterpreterLookup.Find(language, options, paths.Root.Value, cancellationToken))];

        var problems = new List<string>();
        if (!supportedWindows)
        {
            problems.Add("TrayWisp supports Windows 10 and later only.");
        }

        if (overlap is not null)
        {
            problems.Add($"The {overlap} directory overlaps the plugin root.");
        }

        if (!root.IsValid)
        {
            problems.Add(root.Status == PluginRootStatus.NotFound ? "The plugin root does not exist (TrayWisp never creates it)." : $"The plugin root is {root.Status}.");
        }

        foreach ((string what, StorageLocationReport report) in new[] { ("trust store", trust), ("run history", history) })
        {
            if (report.Status is not (StorageLocationStatus.Ready or StorageLocationStatus.Missing) || report.FileStatus is StorageFileStatus.Unsafe or StorageFileStatus.Inaccessible)
            {
                problems.Add($"The {what} location cannot be used ({report.Status}, file {report.FileStatus}).");
            }
        }

        foreach (InterpreterLookupResult interpreter in interpreters.Where(entry => entry.Status != InterpreterLookupStatus.Resolved))
        {
            problems.Add($"{interpreter.Language}: {interpreter.Status}.");
        }

        result.AddRange(CliDiagnostic.From(root.Diagnostics));
        result.AddRange(trust.Diagnostics.Concat(history.Diagnostics).Select(CliDiagnostic.From));
        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("version", version);
            writer.WriteStartObject("platform");
            writer.WriteBoolean("windows", windows);
            writer.WriteBoolean("supported", supportedWindows);
            writer.WriteString("osVersion", Environment.OSVersion.Version.ToString());
            writer.WriteString("runtime", RuntimeInformation.FrameworkDescription);
            writer.WriteEndObject();
            writer.WriteStartObject("pluginRoot");
            writer.SafeString("path", paths.Root.Value);
            writer.WriteString("source", paths.RootIsDefault ? "default" : "option");
            writer.WriteString("status", root.Status.ToString());
            writer.SafeString("overlapsStorage", overlap);
            writer.WriteEndObject();
            WriteStorage(writer, "trustStore", trust, paths.DataDirectoryIsDefault);
            WriteStorage(writer, "runHistory", history, paths.DataDirectoryIsDefault);
            writer.WriteStartArray("interpreters");
            foreach (InterpreterLookupResult interpreter in interpreters)
            {
                writer.WriteStartObject();
                writer.WriteString("language", interpreter.Language.ToString());
                writer.WriteString("status", interpreter.Status.ToString());
                writer.WriteBoolean("configured", interpreter.IsConfigured);
                writer.SafeString("path", interpreter.Interpreter?.Path);
                writer.NullableEnum("kind", interpreter.Interpreter?.Kind);
                writer.NullableEnum("source", interpreter.Interpreter?.Source);
                writer.WriteBoolean("authenticated", false);
                writer.WriteBoolean("versionChecked", false);
                if (interpreter.Language == PluginLanguage.PowerShell)
                {
                    writer.WriteBoolean("executionPolicyChecked", false);
                }

                CommandSupport.WriteDiagnosticsJson(writer, "diagnostics", interpreter.Diagnostics.Select(CliDiagnostic.From));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("deferredLanguages");
            foreach (PluginLanguage language in DeferredLanguages)
            {
                writer.WriteStringValue(language.ToString());
            }

            writer.WriteEndArray();
            WriteLimits(writer);
            writer.WriteStartArray("problems");
            foreach (string problem in problems)
            {
                writer.WriteStringValue(problem);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("TrayWisp", version);
            writer.Value("Platform", (supportedWindows ? "supported" : "NOT SUPPORTED") + string.Create(CultureInfo.InvariantCulture, $" ({(windows ? "Windows" : "not Windows")} {Environment.OSVersion.Version}, {RuntimeInformation.FrameworkDescription})"));
            writer.Line();
            writer.Field("Plugin root", paths.Root.Value);
            using (writer.Indent())
            {
                writer.Value("Source", paths.RootIsDefault ? "default" : "--root");
                writer.Value("Status", root.Status.ToString() + (root.Status == PluginRootStatus.NotFound ? " (create it yourself; TrayWisp never creates it)" : string.Empty));
                if (overlap is not null)
                {
                    writer.Line($"PROBLEM: the {overlap} directory overlaps the plugin root.");
                }
            }

            writer.Field("Data directory", paths.DataDirectory.Value);
            using (writer.Indent())
            {
                writer.Value("Source", paths.DataDirectoryIsDefault ? "default" : "--data-dir");
                WriteStorageHuman(writer, "Trust store", trust);
                WriteStorageHuman(writer, "Run history", history);
                writer.Line("Content not checked: doctor neither reads nor locks the trust store or the run history.");
                writer.Line("'traywisp status' reads the trust store under its lock; 'traywisp logs' reads the history.");
            }

            writer.Line();
            writer.Line("Interpreters (resolved exactly as a run would; nothing was started):");
            using (writer.Indent())
            {
                foreach (InterpreterLookupResult interpreter in interpreters)
                {
                    string configured = interpreter.IsConfigured ? ", explicitly configured (no fallback)" : string.Empty;
                    if (interpreter.Interpreter is PluginInterpreter found)
                    {
                        writer.Field($"{interpreter.Language}: found{configured}", found.Path);
                        using (writer.Indent())
                        {
                            writer.Value("Kind and source", $"{found.Kind}, {found.Source}");
                        }
                    }
                    else
                    {
                        writer.Value(interpreter.Language.ToString(), interpreter.Status + configured);
                    }

                    using (writer.Indent())
                    {
                        writer.Diagnostics([.. interpreter.Diagnostics.Select(CliDiagnostic.From)]);
                    }
                }

                writer.Line("A found path is a validated local Windows program. It is not authenticated, and its version was not checked.");
                writer.Line("Whether PowerShell's execution policy allows scripts was not checked; TrayWisp never changes or bypasses it.");
            }

            writer.Value("Deferred languages", "Bash (.sh) and batch (.bat, .cmd) are recognized but not run");
            writer.Line();
            writer.Line("Limits:");
            using (writer.Indent())
            {
                writer.Value("Run timeout", string.Create(CultureInfo.InvariantCulture, $"{PluginExecutionOptions.Default.DefaultTimeout.TotalSeconds:0} s default, {PluginExecutionOptions.MinTimeout.TotalSeconds:0} to {PluginExecutionOptions.MaxTimeout.TotalSeconds:0} s"));
                writer.Value("Captured output", string.Create(CultureInfo.InvariantCulture, $"stdout {PluginExecutionService.MaxStandardOutputBytes} bytes, stderr tail {PluginExecutionService.MaxStandardErrorBytes} bytes"));
                writer.Value("Plugin file", string.Create(CultureInfo.InvariantCulture, $"{PluginTrustService.MaxPluginFileBytes} bytes"));
                writer.Value("Run history", string.Create(CultureInfo.InvariantCulture, $"{PluginRunHistory.MaxRecords} records, {PluginRunHistory.MaxHistoryBytes} bytes"));
            }

            writer.Line();
            if (problems.Count == 0)
            {
                writer.Line("No problems found among the conditions checked.");
            }
            else
            {
                writer.Line("Problems:");
                using (writer.Indent())
                {
                    foreach (string problem in problems)
                    {
                        writer.Line("- " + problem);
                    }
                }
            }
        };
        return result;
    }

    public static CommandResult Logs(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var result = new CommandResult();
        CliPaths? paths = CommandSupport.ResolvePaths(request, environment, result);
        if (paths is null)
        {
            return result;
        }

        PluginRunHistory history = environment.CreateRunHistory(paths.DiagnosticsDirectory.Value);
        RunHistoryReadResult read = history.Read(request.Limit!.Value, cancellationToken);
        result.AddRange(read.Diagnostics.Where(diagnostic => diagnostic.Severity != PluginDiagnosticSeverity.Info).Select(CliDiagnostic.From));
        if (!read.Succeeded)
        {
            result.ExitCode = ExitCodes.StorageFailed;
            result.Summary = read.Status switch
            {
                RunHistoryReadStatus.Corrupt => "the run history is invalid; it was not changed (see docs/diagnostics.md to remove it)",
                RunHistoryReadStatus.UnsupportedVersion => "the run history was written by an unsupported TrayWisp version; it was not changed",
                _ => "the run history could not be read (see the diagnostics)",
            };
        }

        string rootValue = paths.Root.Value;
        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("historyDirectory", history.DirectoryPath);
            writer.WriteString("status", read.Status.ToString());
            writer.WriteNumber("totalRecords", read.TotalRecords);
            writer.WriteString("order", "oldestFirst");
            writer.SafeString("root", rootValue);
            writer.WriteStartArray("records");
            foreach (PluginRunRecord record in read.Records)
            {
                WriteRecordJson(writer, record, record.IsForRoot(rootValue));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("Run history", history.FilePath);
            writer.Value("Status", read.Status switch
            {
                RunHistoryReadStatus.NoHistory => "no history exists yet",
                RunHistoryReadStatus.Empty => "the history is empty",
                RunHistoryReadStatus.Loaded => string.Create(CultureInfo.InvariantCulture, $"showing {read.Records.Length} of {read.TotalRecords} records, oldest first"),
                RunHistoryReadStatus.Corrupt => "INVALID (not changed; never repaired automatically)",
                RunHistoryReadStatus.UnsupportedVersion => "UNSUPPORTED VERSION (not changed)",
                _ => "UNAVAILABLE",
            });
            foreach (PluginRunRecord record in read.Records)
            {
                writer.Line();
                writer.Value("Recorded (UTC)", record.RecordedAtUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture));
                using (writer.Indent())
                {
                    writer.Field("Plugin", record.PluginId.Value);
                    writer.Value("Root", record.IsForRoot(rootValue) ? "the configured plugin root" : $"another root (root id {record.RootId})");
                    writer.Value("Execution", record.Status.ToString() + (record.TrustStatus is { } trust ? $", trust {trust}" : string.Empty));
                    writer.Value("Plugin exit code", record.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "(none)");
                    writer.Value("Duration", string.Create(CultureInfo.InvariantCulture, $"{record.ElapsedMilliseconds} ms"));
                    if (record.TerminationReason != ExecutionTerminationReason.None)
                    {
                        writer.Value("Terminated by TrayWisp", record.TerminationReason.ToString());
                    }

                    writer.Value("Cleanup", record.CleanupStatus.ToString());
                    writer.Value("Output", string.Create(CultureInfo.InvariantCulture, $"{record.StandardOutputBytes} bytes{(record.StandardOutputComplete ? string.Empty : ", incomplete")}; output decision when recorded: {record.Publication}")
                        + (record.PublicationTrustStatus is { } after ? $" (trust check after the run: {after}; not proof that output was displayed)" : string.Empty));
                    if (!record.Diagnostics.IsEmpty)
                    {
                        writer.Value("Codes", string.Join(", ", record.Diagnostics.Select(code => $"{code} ({code.Severity})")) + (record.DiagnosticsTruncated ? ", ..." : string.Empty));
                    }
                }
            }
        };
        return result;
    }

    private static void WriteRecordJson(Utf8JsonWriter writer, PluginRunRecord record, bool forRoot)
    {
        writer.WriteStartObject();
        writer.Timestamp("recordedAtUtc", record.RecordedAtUtc);
        writer.Timestamp("startedAtUtc", record.StartedAtUtc);
        writer.WriteString("rootId", record.RootId);
        writer.WriteBoolean("isConfiguredRoot", forRoot);
        writer.SafeString("pluginId", record.PluginId.Value);
        writer.WriteString("status", record.Status.ToString());
        writer.NullableEnum("trustStatus", record.TrustStatus);
        writer.NullableNumber("pluginExitCode", record.ExitCode);
        writer.WriteNumber("elapsedMilliseconds", record.ElapsedMilliseconds);
        writer.WriteString("terminationReason", record.TerminationReason.ToString());
        writer.WriteString("cleanupStatus", record.CleanupStatus.ToString());
        writer.WriteBoolean("jobObjectAssigned", record.JobObjectAssigned);
        writer.NullableNumber("remainingJobProcesses", record.RemainingJobProcesses);
        writer.WriteNumber("stdoutBytes", record.StandardOutputBytes);
        writer.WriteBoolean("stdoutComplete", record.StandardOutputComplete);
        writer.WriteBoolean("stdoutTruncated", record.StandardOutputTruncated);
        writer.WriteNumber("stderrBytes", record.StandardErrorBytes);
        writer.WriteBoolean("stderrTruncated", record.StandardErrorTruncated);
        writer.NullableEnum("interpreterKind", record.InterpreterKind);
        writer.NullableEnum("interpreterSource", record.InterpreterSource);
        writer.WriteString("publication", record.Publication.ToString());
        writer.NullableEnum("publicationTrustStatus", record.PublicationTrustStatus);
        writer.WriteStartArray("diagnostics");
        foreach (RecordedDiagnostic diagnostic in record.Diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.ToString());
            writer.WriteString("severity", JsonReport.Severity(diagnostic.Severity));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("diagnosticsTruncated", record.DiagnosticsTruncated);
        writer.WriteEndObject();
    }

    private static void WriteStorage(Utf8JsonWriter writer, string name, StorageLocationReport report, bool isDefault)
    {
        writer.WriteStartObject(name);
        writer.SafeString("directory", report.DirectoryPath);
        writer.WriteString("source", isDefault ? "default" : "option");
        writer.WriteString("directoryStatus", report.Status.ToString());
        writer.SafeString("file", report.FileName);
        writer.WriteString("fileStatus", report.FileStatus.ToString());
        writer.WriteBoolean("contentChecked", false);
        writer.WriteEndObject();
    }

    private static void WriteStorageHuman(HumanWriter writer, string label, StorageLocationReport report)
    {
        string state = report.Status switch
        {
            StorageLocationStatus.Ready => report.FileStatus switch
            {
                StorageFileStatus.Present => "directory ready, file present",
                StorageFileStatus.Missing => "directory ready, no file yet",
                _ => $"directory ready, file {report.FileStatus.ToString().ToUpperInvariant()}",
            },
            StorageLocationStatus.Missing => "not created yet (created when first needed)",
            _ => report.Status.ToString().ToUpperInvariant(),
        };
        writer.Field($"{label} ({state})", report.FileName is null ? report.DirectoryPath : report.DirectoryPath + "\\" + report.FileName);
    }

    private static void WriteLimits(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("limits");
        writer.WriteNumber("defaultTimeoutSeconds", (long)PluginExecutionOptions.Default.DefaultTimeout.TotalSeconds);
        writer.WriteNumber("minTimeoutSeconds", (long)PluginExecutionOptions.MinTimeout.TotalSeconds);
        writer.WriteNumber("maxTimeoutSeconds", (long)PluginExecutionOptions.MaxTimeout.TotalSeconds);
        writer.WriteNumber("maxStdoutBytes", PluginExecutionService.MaxStandardOutputBytes);
        writer.WriteNumber("maxStderrBytes", PluginExecutionService.MaxStandardErrorBytes);
        writer.WriteNumber("maxPluginFileBytes", PluginTrustService.MaxPluginFileBytes);
        writer.WriteNumber("maxHistoryRecords", PluginRunHistory.MaxRecords);
        writer.WriteNumber("maxHistoryBytes", PluginRunHistory.MaxHistoryBytes);
        writer.WriteNumber("maxValidateOutputBytes", ValidateOutputCommand.MaxInputBytes);
        writer.WriteEndObject();
    }
}
