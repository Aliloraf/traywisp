using System.Text;

namespace TrayWisp.Cli.CommandLine;

// Help text. Plain text only; it never includes values from the environment or the file system.
internal static class HelpText
{
    public static string Global()
    {
        var text = new StringBuilder();
        text.Append("Usage: traywisp COMMAND [OPTIONS] [PLUGIN_ID]\n");
        text.Append("       traywisp --help | -h | --version\n\n");
        text.Append("Commands:\n");
        foreach (CommandSpec command in CommandLineParser.Commands)
        {
            text.Append("  ").Append(command.Name.PadRight(17)).Append(command.Summary).Append('\n');
        }

        text.Append('\n');
        text.Append("The command comes first. Options and PLUGIN_ID may follow in any order; '--' ends the options.\n");
        text.Append("PLUGIN_ID is the plugin's path relative to the plugin root, for example clock.5s.ps1 or work/clock.5s.ps1.\n");
        text.Append("Nothing is expanded: no wildcards, environment variables or relative paths.\n\n");
        text.Append("Default locations:\n");
        text.Append("  plugin root      %LOCALAPPDATA%\\TrayWisp\\plugins       (never created automatically)\n");
        text.Append("  trust store      %LOCALAPPDATA%\\TrayWisp\\trust\\approvals.json\n");
        text.Append("  run history      %LOCALAPPDATA%\\TrayWisp\\diagnostics\\history.json\n\n");
        text.Append("A plugin runs only after you approve the exact SHA-256 fingerprint of its content. Approval lets the script\n");
        text.Append("run as your Windows user with all of your access: it is consent, not a sandbox.\n\n");
        text.Append("Exit codes: 0 success, 1 failure, 2 usage, 3 plugin unavailable or unsafe, 4 approval denied,\n");
        text.Append("5 configuration or interpreter, 6 execution failed, 7 timeout, 8 output invalid, 9 cleanup incomplete,\n");
        text.Append("10 storage failed, 11 discovery incomplete, 130 cancelled. See docs/cli.md.\n\n");
        text.Append("Run 'traywisp COMMAND --help' for a command's options.\n");
        return text.ToString();
    }

    public static string ForCommand(CommandSpec command)
    {
        var text = new StringBuilder();
        text.Append("Usage: ").Append(command.Synopsis).Append("\n\n");
        text.Append(command.Summary).Append('\n');
        foreach (string detail in command.Details)
        {
            text.Append(detail).Append('\n');
        }

        if (command.Options.Length > 0)
        {
            text.Append("\nOptions:\n");
            foreach (CliOption option in command.Options)
            {
                OptionSpec spec = CommandLineParser.Spec(option);
                string name = spec.ValueName is null ? spec.Name : $"{spec.Name} {spec.ValueName}";
                text.Append("  ").Append(name.PadRight(26)).Append(spec.Description).Append('\n');
            }
        }

        text.Append("  ").Append("-h, --help".PadRight(26)).Append("Show this help.\n");
        return text.ToString();
    }
}
