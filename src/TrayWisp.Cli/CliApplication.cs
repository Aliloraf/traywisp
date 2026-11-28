using System.Reflection;
using System.Text;
using TrayWisp.Cli.CommandLine;
using TrayWisp.Cli.Commands;
using TrayWisp.Cli.Output;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Cli;

// The command application: parses the complete request, runs one command, and renders exactly one report. Help, version
// and invalid requests are answered before anything touches the file system, a store or a process.
internal static class CliApplication
{
    public static string Version { get; } =
        typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ParseResult parsed = CommandLineParser.Parse(args);
        try
        {
            if (parsed.ShowHelp)
            {
                environment.Out.Write(HelpText.Global());
                return ExitCodes.Success;
            }

            if (parsed.HelpFor is CommandSpec helpFor)
            {
                environment.Out.Write(HelpText.ForCommand(helpFor));
                return ExitCodes.Success;
            }

            if (parsed.ShowVersion)
            {
                environment.Out.Write($"traywisp {Version}\n");
                return ExitCodes.Success;
            }

            if (parsed.Request is not CliRequest request)
            {
                return ReportUsageError(parsed, environment);
            }

            CommandResult result = await ExecuteAsync(request, environment, cancellationToken).ConfigureAwait(false);
            await CloseCancellationAsync(environment, result).ConfigureAwait(false);
            try
            {
                Emit(request, result, environment);
            }
#pragma warning disable CA1031 // A rendering failure is still reported as one structured document, without its message.
            catch (Exception exception) when (exception is not (IOException or OutOfMemoryException))
#pragma warning restore CA1031
            {
                result = Unexpected(exception);
                Emit(request, result, environment);
            }

            return result.ExitCode;
        }
        catch (IOException)
        {
            // Standard output or standard error could not be written (for example a closed pipe). Nothing more can be
            // reported; no valid JSON is promised in this case.
            return ExitCodes.Failure;
        }
    }

    private static async Task<CommandResult> ExecuteAsync(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return request.Command.Kind switch
            {
                CommandKind.List => PluginCommands.List(request, environment, cancellationToken),
                CommandKind.Inspect => PluginCommands.Inspect(request, environment, cancellationToken),
                CommandKind.Approve => PluginCommands.Approve(request, environment, cancellationToken),
                CommandKind.Revoke => PluginCommands.Revoke(request, environment, cancellationToken),
                CommandKind.Status => PluginCommands.Status(request, environment, cancellationToken),
                CommandKind.Run => await RunCommand.RunAsync(request, environment, cancellationToken).ConfigureAwait(false),
                CommandKind.ValidateOutput => await ValidateOutputCommand.RunAsync(request, environment, cancellationToken).ConfigureAwait(false),
                CommandKind.Doctor => DiagnosticCommands.Doctor(request, environment, Version, cancellationToken),
                _ => DiagnosticCommands.Logs(request, environment, cancellationToken),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new CommandResult { ExitCode = ExitCodes.Cancelled, Summary = "cancelled" };
            cancelled.Add(CliDiagnostic.Cli(PluginDiagnosticSeverity.Info, CliDiagnosticCode.Cancelled, "The command was cancelled before it completed."));
            return cancelled;
        }
#pragma warning disable CA1031 // Any other failure is reported as a structured result without its message or stack trace.
        catch (Exception exception) when (exception is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return Unexpected(exception);
        }
    }

    // Cancellation ends before the report is built, so a failed cancellation callback is reported in it (both as text and
    // as JSON, so nothing is written to standard error in JSON mode). It does not change the command's exit code: the
    // command, including any run's cleanup, has already completed and its result is accurate.
    private static async Task CloseCancellationAsync(CliEnvironment environment, CommandResult result)
    {
        if (environment.CloseCancellation is not { } close || await close().ConfigureAwait(false) is not { } failure)
        {
            return;
        }

        Exception reported = failure is AggregateException { InnerExceptions: [Exception first, ..] } ? first : failure;
        result.Add(CliDiagnostic.Warning(
            CliDiagnosticCode.UnexpectedFailure,
            $"A cancellation callback failed with an unexpected {reported.GetType().Name}. The command had already completed; its result and exit code are unaffected."));
    }

    // Only the exception's type is reported: messages and stack traces can contain paths or plugin text.
    private static CommandResult Unexpected(Exception exception)
    {
        var failed = new CommandResult { ExitCode = ExitCodes.Failure, Summary = "an unexpected internal error occurred" };
        failed.Add(CliDiagnostic.Error(CliDiagnosticCode.UnexpectedFailure, $"An unexpected {exception.GetType().Name} occurred. The command did not complete."));
        return failed;
    }

    private static void Emit(CliRequest request, CommandResult result, CliEnvironment environment)
    {
        if (request.Json)
        {
            environment.Out.Write(JsonReport.Write(request.Command.Name, result.ExitCode, result.Json, result.Diagnostics));
            return;
        }

        var writer = new HumanWriter();
        result.Human?.Invoke(writer);
        if (result.Diagnostics.Count > 0)
        {
            writer.Line();
            writer.Diagnostics(result.Diagnostics);
        }

        environment.Out.Write(writer.ToString());
        if (result.ExitCode != ExitCodes.Success && result.Summary is not null)
        {
            environment.Error.Write($"traywisp {request.Command.Name}: {result.Summary}\n");
        }
    }

    private static int ReportUsageError(ParseResult parsed, CliEnvironment environment)
    {
        CliDiagnostic diagnostic = CliDiagnostic.Error(CliDiagnosticCode.UsageError, parsed.Error!);
        if (parsed.ErrorWantsJson)
        {
            environment.Out.Write(JsonReport.Write(parsed.ErrorCommand?.Name, ExitCodes.Usage, result: null, [diagnostic]));
            return ExitCodes.Usage;
        }

        var text = new StringBuilder();
        text.Append("traywisp: error: ").Append(TerminalText.Inline(parsed.Error)).Append('\n');
        text.Append(parsed.ErrorCommand is CommandSpec command
            ? $"Usage: {command.Synopsis}\nRun 'traywisp {command.Name} --help' for details.\n"
            : "Run 'traywisp --help' for the list of commands.\n");
        environment.Error.Write(text.ToString());
        return ExitCodes.Usage;
    }
}
