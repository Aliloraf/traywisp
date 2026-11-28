using System.Globalization;
using TrayWisp.Cli.CommandLine;
using TrayWisp.Cli.Output;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Runtime.Execution;

namespace TrayWisp.Cli.Commands;

// traywisp validate-output: decodes and parses plugin output from redirected standard input with exactly the engine's
// decoder and the Phase 2 parser. Nothing is run, opened or resolved, and the trust store is not touched.
internal static class ValidateOutputCommand
{
    // The execution engine's standard output limit, so anything that validates here fits a real run's capture.
    public const int MaxInputBytes = PluginExecutionService.MaxStandardOutputBytes;

    public static async Task<CommandResult> RunAsync(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var result = new CommandResult();
        if (!environment.InputRedirected)
        {
            CommandSupport.Fail(result, ExitCodes.Usage, CliDiagnostic.Error(
                CliDiagnosticCode.InputNotRedirected,
                "validate-output reads plugin output from redirected standard input, and standard input is a console. "
                + "Pipe or redirect the output, for example: Get-Content -Raw -Encoding utf8 .\\output.txt | traywisp validate-output"),
                "standard input is not redirected; nothing was read");
            return result;
        }

        byte[] buffer;
        int length;
        try
        {
            (buffer, length) = await ReadBoundedAsync(environment.OpenInput, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            CommandSupport.Fail(result, ExitCodes.Failure, CliDiagnostic.Error(
                CliDiagnosticCode.InputUnreadable,
                string.Create(CultureInfo.InvariantCulture, $"Standard input could not be read ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}). Nothing was parsed.")),
                "standard input could not be read");
            return result;
        }

        bool overflow = length > MaxInputBytes;
        if (overflow)
        {
            CommandSupport.Fail(result, ExitCodes.OutputInvalid, CliDiagnostic.Error(
                CliDiagnosticCode.InputTooLarge,
                string.Create(CultureInfo.InvariantCulture, $"The input is larger than {MaxInputBytes} bytes, the execution engine's standard output limit. It was not read further and nothing was parsed.")),
                "the input is too large; nothing was parsed");
            result.Json = writer =>
            {
                writer.WriteStartObject();
                WriteInput(writer, MaxInputBytes + 1L, exceeded: true, encoding: null, request.Truncated);
                writer.WriteNull("parseStatus");
                writer.WriteNull("diagnosticsTruncated");
                writer.WriteNull("output");
                writer.WriteEndObject();
            };
            result.Human = writer => writer.Value("Input", string.Create(CultureInfo.InvariantCulture, $"more than {MaxInputBytes} bytes (rejected, not parsed)"));
            return result;
        }

        if (!PluginOutputDecoder.TryDecodeStandardOutput(buffer.AsSpan(0, length), out string? text, out StandardOutputEncoding encoding, out string? decodeError))
        {
            CommandSupport.Fail(result, ExitCodes.OutputInvalid, CliDiagnostic.Error(CliDiagnosticCode.OutputInvalid, decodeError + " Nothing was parsed."), "the input is not valid UTF-8 or BOM-marked UTF-16");
            result.Json = writer =>
            {
                writer.WriteStartObject();
                WriteInput(writer, length, exceeded: false, encoding: null, request.Truncated);
                writer.WriteNull("parseStatus");
                writer.WriteNull("diagnosticsTruncated");
                writer.WriteNull("output");
                writer.WriteEndObject();
            };
            result.Human = writer => writer.Value("Input", CommandSupport.Bytes(length) + ", not decodable (rejected, not parsed)");
            return result;
        }

        PluginOutputParseResult parsed = PluginOutputParser.Parse(text, isTruncated: request.Truncated);
        result.AddRange(parsed.Diagnostics.Select(CliDiagnostic.From));
        bool valid = parsed.Status == OutputParseStatus.Complete;
        if (!valid)
        {
            result.ExitCode = ExitCodes.OutputInvalid;
            result.Summary = request.Truncated
                ? "the input was marked as truncated, so it is rejected as a whole"
                : "the output was rejected as a whole (see the diagnostics)";
        }

        result.Json = writer =>
        {
            writer.WriteStartObject();
            WriteInput(writer, length, exceeded: false, encoding, request.Truncated);
            writer.WriteString("parseStatus", parsed.Status.ToString());
            writer.WriteBoolean("diagnosticsTruncated", parsed.DiagnosticsTruncated);
            writer.WritePropertyName("output");
            if (valid)
            {
                OutputRenderer.WriteJson(writer, parsed.Output);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Value("Input", $"{CommandSupport.Bytes(length)}, {encoding}" + (request.Truncated ? ", marked as truncated" : string.Empty));
            writer.Value("Result", valid ? "valid" : "INVALID (rejected as a whole; nothing is shown)");
            if (valid)
            {
                OutputRenderer.WriteHuman(writer, parsed.Output);
            }

            writer.Line("Valid output proves neither that a script is safe nor that it is authorized to run.");
        };
        return result;
    }

    // Reads at most MaxInputBytes + 1 bytes: one more than the limit, to tell exact-limit input from overflow, and never
    // more. The blocking read runs on its own thread. After cancellation the reader stops before its next read and never
    // returns what it has read, so partial input is never parsed; a read the operating system keeps blocked cannot be
    // interrupted and is abandoned (it ends with the process). Its eventual failure is observed, so an abandoned read never
    // surfaces as an unobserved task exception. Nothing is decoded into a string before the size is known.
    internal static async Task<(byte[] Buffer, int Length)> ReadBoundedAsync(Func<Stream> openInput, CancellationToken cancellationToken)
    {
        Task<(byte[], int)> read = Task.Factory.StartNew(
            () =>
            {
                byte[] buffer = new byte[MaxInputBytes + 1];
                int total = 0;
                using Stream input = openInput();
                int count;
                while (total < buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((count = input.Read(buffer, total, buffer.Length - total)) <= 0)
                    {
                        break;
                    }

                    total += count;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return (buffer, total);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            return await read.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveAbandoned(read);
            throw;
        }
    }

    // Reads the exception of a task nobody waits for any more, whenever it fails.
    internal static void ObserveAbandoned(Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static void WriteInput(System.Text.Json.Utf8JsonWriter writer, long bytes, bool exceeded, StandardOutputEncoding? encoding, bool truncated)
    {
        writer.WriteStartObject("input");
        if (exceeded)
        {
            writer.WriteNull("bytes");
        }
        else
        {
            writer.WriteNumber("bytes", bytes);
        }

        writer.WriteNumber("limitBytes", MaxInputBytes);
        writer.WriteBoolean("limitExceeded", exceeded);
        writer.NullableEnum("encoding", encoding);
        writer.WriteBoolean("markedTruncated", truncated);
        writer.WriteEndObject();
    }
}
