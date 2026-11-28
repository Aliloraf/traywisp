using System.Text;
using TrayWisp.Cli;

// traywisp: see docs/cli.md. The entry point only connects the console to the command application.
var cancellation = new ConsoleCancellation();
Console.CancelKeyPress += cancellation.OnCancelKeyPress;

// A console gets text through the console API (Unicode); redirected output is UTF-8 without a byte order mark.
TextWriter output = Console.IsOutputRedirected ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) : Console.Out;
TextWriter error = Console.IsErrorRedirected ? new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) : Console.Error;
int exitCode = ExitCodes.Failure;
try
{
    var environment = new CliEnvironment
    {
        Out = output,
        Error = error,
        OpenInput = Console.OpenStandardInput,
        InputRedirected = Console.IsInputRedirected,
        LocalApplicationData = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        CloseCancellation = cancellation.CloseAsync,
    };
    exitCode = await CliApplication.RunAsync(args, environment, cancellation.Token).ConfigureAwait(false);
}
finally
{
    // The source is released and both streams are flushed whatever happened before; see ProcessFinalization.
    Console.CancelKeyPress -= cancellation.OnCancelKeyPress;
    exitCode = await ProcessFinalization.CompleteAsync(cancellation, output, error, exitCode).ConfigureAwait(false);
}

return exitCode;
