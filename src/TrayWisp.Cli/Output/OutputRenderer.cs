using System.Globalization;
using System.Text.Json;
using TrayWisp.Core.Output;

namespace TrayWisp.Cli.Output;

// Renders a parsed plugin output: titles, the nested menu and its actions, always as inert data. Command actions show the
// executable and each argument separately, quoted and escaped, never joined into a command line. URL actions show
// Uri.OriginalString exactly as the plugin wrote it, so escaped characters are never decoded.
internal static class OutputRenderer
{
    public const int MaxLabelLength = 1024;

    public static void WriteHuman(HumanWriter writer, PluginOutput output)
    {
        writer.Value("Titles", output.Titles.Length.ToString(CultureInfo.InvariantCulture));
        using (writer.Indent())
        {
            for (int i = 0; i < output.Titles.Length; i++)
            {
                OutputTitle title = output.Titles[i];
                writer.Entry(string.Create(CultureInfo.InvariantCulture, $"[{i + 1}]"), title.Item.ToDisplayText(), TerminalText.Kind.Label, MaxLabelLength);
                using (writer.Indent())
                {
                    WriteItemDetails(writer, title.Item);
                    if (!title.ShowInDropdown)
                    {
                        writer.Line("not shown in the menu (dropdown=false)");
                    }
                }
            }
        }

        writer.Value("Menu items", CountNodes(output.Items).ToString(CultureInfo.InvariantCulture));
        using (writer.Indent())
        {
            WriteNodes(writer, output.Items);
        }

        if (HasAction(output))
        {
            writer.Line("Actions are shown as data only. TrayWisp has not run, opened or resolved any of them.");
        }
    }

    public static void WriteJson(Utf8JsonWriter writer, PluginOutput output)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("titles");
        foreach (OutputTitle title in output.Titles)
        {
            writer.WriteStartObject();
            WriteItemProperties(writer, title.Item);
            writer.WriteBoolean("showInDropdown", title.ShowInDropdown);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("items");
        WriteNodesJson(writer, output.Items);
        writer.WriteEndObject();
    }

    public static int CountNodes(IEnumerable<MenuNode> nodes) =>
        nodes.Sum(node => 1 + (node is MenuItem item ? CountNodes(item.Children) : 0));

    // A JSON-style quoted string: quotes and backslashes escaped, unsafe characters as \uXXXX, so the exact value and its
    // boundaries are unambiguous.
    public static string Quoted(string value, int maxLength = MaxLabelLength)
    {
        string escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + TerminalText.Inline(escaped, maxLength) + "\"";
    }

    private static bool HasAction(PluginOutput output) =>
        output.Titles.Any(title => title.Item.Action is not null) || HasAction(output.Items);

    private static bool HasAction(IEnumerable<MenuNode> nodes) =>
        nodes.OfType<MenuItem>().Any(item => item.Action is not null || HasAction(item.Children));

    private static void WriteNodes(HumanWriter writer, IEnumerable<MenuNode> nodes)
    {
        foreach (MenuNode node in nodes)
        {
            if (node is not MenuItem item)
            {
                writer.Line("----- (separator)");
                continue;
            }

            writer.Entry("-", item.ToDisplayText(), TerminalText.Kind.Label, MaxLabelLength);
            using (writer.Indent())
            {
                WriteItemDetails(writer, item);
                WriteNodes(writer, item.Children);
            }
        }
    }

    private static void WriteItemDetails(HumanWriter writer, MenuItem item)
    {
        if (item.Format.Tooltip is not null)
        {
            writer.Field("tooltip", item.Format.Tooltip, TerminalText.Kind.Label, MaxLabelLength);
        }

        if (item.IsDisabled)
        {
            writer.Line("disabled");
        }

        if (item.IsChecked)
        {
            writer.Line("checked");
        }

        switch (item.Action)
        {
            case CommandAction command:
                writer.Line("action: command (inert, not run)");
                using (writer.Indent())
                {
                    writer.Line("executable: " + Quoted(command.Executable));
                    for (int i = 0; i < command.Arguments.Length; i++)
                    {
                        writer.Line(string.Create(CultureInfo.InvariantCulture, $"argument[{i}]: ") + Quoted(command.Arguments[i]));
                    }

                    if (command.RunInTerminal is bool terminal)
                    {
                        writer.Value("terminal", terminal ? "true" : "false");
                    }

                    if (command.RefreshesPlugin)
                    {
                        writer.Line("then refresh this plugin");
                    }
                }

                break;
            case UrlAction url:
                writer.Line("action: URL (inert, not opened): " + Quoted(url.Url.OriginalString));
                if (url.RefreshesPlugin)
                {
                    using (writer.Indent())
                    {
                        writer.Line("then refresh this plugin");
                    }
                }

                break;
            case RefreshAction:
                writer.Line("action: refresh this plugin (inert, not run)");
                break;
        }
    }

    private static void WriteNodesJson(Utf8JsonWriter writer, IEnumerable<MenuNode> nodes)
    {
        writer.WriteStartArray();
        foreach (MenuNode node in nodes)
        {
            writer.WriteStartObject();
            if (node is MenuItem item)
            {
                writer.WriteString("type", "item");
                writer.WriteNumber("line", item.Line);
                WriteItemProperties(writer, item);
                writer.WritePropertyName("children");
                WriteNodesJson(writer, item.Children);
            }
            else
            {
                writer.WriteString("type", "separator");
                writer.WriteNumber("line", node.Line);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteItemProperties(Utf8JsonWriter writer, MenuItem item)
    {
        writer.SafeString("text", item.Text);
        writer.SafeString("displayText", item.ToDisplayText());
        writer.SafeString("tooltip", item.Format.Tooltip);
        writer.WriteBoolean("disabled", item.IsDisabled);
        writer.WriteBoolean("checked", item.IsChecked);
        writer.WritePropertyName("action");
        switch (item.Action)
        {
            case CommandAction command:
                writer.WriteStartObject();
                writer.WriteString("type", "command");
                writer.SafeString("executable", command.Executable);
                writer.WriteStartArray("arguments");
                foreach (string argument in command.Arguments)
                {
                    writer.SafeStringValue(argument);
                }

                writer.WriteEndArray();
                if (command.RunInTerminal is bool terminal)
                {
                    writer.WriteBoolean("terminal", terminal);
                }
                else
                {
                    writer.WriteNull("terminal");
                }

                writer.WriteBoolean("refresh", command.RefreshesPlugin);
                writer.WriteEndObject();
                break;
            case UrlAction url:
                writer.WriteStartObject();
                writer.WriteString("type", "url");
                writer.SafeString("url", url.Url.OriginalString);
                writer.WriteBoolean("refresh", url.RefreshesPlugin);
                writer.WriteEndObject();
                break;
            case RefreshAction:
                writer.WriteStartObject();
                writer.WriteString("type", "refresh");
                writer.WriteBoolean("refresh", true);
                writer.WriteEndObject();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
