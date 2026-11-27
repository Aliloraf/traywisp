using TrayWisp.Core.Output;
using Xunit;
using static TrayWisp.Core.Tests.Output.OutputTestHelpers;

namespace TrayWisp.Core.Tests.Output;

// Parses recorded plugin output from Output/Fixtures. Nothing is executed: the files are what the
// plugins print, written down by hand.
public sealed class OutputFixtureTests
{
    // Expected output of the sample plugins in plugins/examples, one fixture per plugin file name.
    [Theory]
    [InlineData("clock.1m.ps1", "14:05", new[] { "Thursday 26 November 2026", "Week 48" })]
    [InlineData("day-progress.10m.py", "58% of today", new[] { "9 h 59 min left" })]
    [InlineData("hello.js", "Good afternoon", new[] { "This is an example TrayWisp plugin." })]
    public void ExamplePluginOutputIsAcceptedWithoutDiagnostics(string plugin, string title, string[] items)
    {
        PluginOutputParseResult result = Parse(ReadFixture("examples", plugin + ".txt"));

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(title, Assert.Single(result.Output.Titles).Item.Text);
        Assert.Equal(items, result.Output.Items.Cast<MenuItem>().Select(item => item.Text));
        Assert.All(result.Output.Items.Cast<MenuItem>(), item => Assert.Null(item.Action));
    }

    [Fact]
    public void EveryExamplePluginHasAnOutputFixture()
    {
        string? examples = FindRepositoryDirectory("plugins", "examples");
        Assert.SkipWhen(examples is null, "The repository's plugins/examples directory was not found next to the test output.");

        string[] plugins = [.. Directory.GetFiles(examples).Select(Path.GetFileName).Where(name => name is not null && !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)!];
        string[] fixtures = [.. Directory.GetFiles(FixturePath("examples")).Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal)];

        Assert.NotEmpty(plugins);
        Assert.Equal(plugins, fixtures);
    }

    [Fact]
    public void SystemMonitorExample()
    {
        PluginOutputParseResult result = Parse(ReadFixture("system-monitor.txt"));

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            Lines(
                "title 'CPU 23% ' color=#2E8B57,#2E8B57",
                "title 'RAM 61% ' color=#FFA500,#FFA500",
                "item 'CPU'",
                "  item 'Usage 23% ' color=#008000,#008000",
                "  item 'Temperature 54 \u00B0C'",
                "  item 'Top processes'",
                "    item 'pwsh.exe 7.1% ' tooltip='C:\\Program Files\\PowerShell\\7\\pwsh.exe'",
                "    item 'msedge.exe 4.3%'",
                "item 'Memory'",
                "  item 'Used 9.8 GB of 16 GB'",
                "  item 'Committed 12.1 GB of 18.4 GB ' length=20",
                "item 'Disks'",
                "  item 'C:\\ 71% used ' color=#FFA500,#FFA500",
                "  item 'D:\\ 12% used'",
                "  separator",
                "  item 'Open Disk Management ' run=<diskmgmt.msc> terminal=false",
                "separator",
                "item 'Task Manager ' run=<taskmgr.exe> terminal=false",
                "item 'Resource Monitor ' run=<C:\\Windows\\System32\\perfmon.exe> </res>",
                "item 'Open log folder ' run=<C:\\Windows\\explorer.exe> <C:\\Users\\Public\\Documents\\Monitor Logs\\>",
                "item 'Refresh now ' refresh"),
            Render(result.Output));
        Assert.Equal("Committed 12.1 GB of\u2026", ((MenuItem)((MenuItem)result.Output.Items[1]).Children[1]).ToDisplayText());
    }

    [Fact]
    public void GitHubNotificationsExample()
    {
        PluginOutputParseResult result = Parse(ReadFixture("github-notifications.txt"));

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Empty(result.Diagnostics);

        TextColor color = Assert.Single(result.Output.Titles).Item.Format.Color!;
        Assert.Equal((new OutputColor(0xF7, 0x81, 0x66), new OutputColor(0xFF, 0xA1, 0x98)), (color.Light, color.Dark));

        MenuItem[] top = [.. result.Output.Items.OfType<MenuItem>()];
        Assert.Equal(["Notifications ", "octo-org/widget", "octo-org/docs", "Mark all as read ", "Last checked 14:05 "], top.Select(i => i.Text));
        Assert.Equal(2, result.Output.Items.OfType<MenuSeparator>().Count());

        MenuItem review = (MenuItem)top[1].Children[0];
        Assert.Equal("Review requested: Add dark mode | theming ", review.Text);
        Assert.Equal("Review requested: Add dark mode | themin\u2026", review.ToDisplayText());
        Assert.Equal("Add dark mode | theming", review.Format.Tooltip);
        Assert.Equal("https://github.com/octo-org/widget/pull/42", Assert.IsType<UrlAction>(review.Action).Url.OriginalString);
        Assert.Equal("https://github.com/octo-org/docs/issues/7", Assert.IsType<UrlAction>(((MenuItem)top[2].Children[0]).Action).Url.OriginalString);
        Assert.Equal("Mentioned in \"Release notes 2.0\" ", ((MenuItem)top[2].Children[0]).Text);

        CommandAction markRead = Assert.IsType<CommandAction>(top[3].Action);
        Assert.Equal("C:\\Program Files\\GitHub CLI\\gh.exe", markRead.Executable);
        Assert.Equal(["api", "--method", "PUT", "/notifications"], markRead.Arguments);
        Assert.True(markRead.RefreshesPlugin);
        Assert.False(markRead.RunInTerminal);

        Assert.True(top[4].IsDisabled);
        Assert.Equal(11, top[4].Format.Size);
    }

    [Fact]
    public void ClockRotationExample()
    {
        PluginOutputParseResult result = Parse(ReadFixture("clock-rotation.txt"));

        Assert.Equal(OutputParseStatus.Complete, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            Lines(
                "title '14:05 ' font=Cascadia Mono size=13",
                "title 'Thu 26 Nov ' [hidden]",
                "title '\U0001F551 UTC 13:05 ' color=#808080,#C0C0C0",
                "item 'Thursday, 26 November 2026 ' disabled",
                "item 'Week 48 \u00B7 Day 331 ' checked",
                "separator",
                "item 'Time zones'",
                "  item 'Tokyo 22:05 ' font=Cascadia Mono",
                "  item 'New York 08:05 ' font=Cascadia Mono",
                "  item 'S\u00E3o Paulo 10:05 ' font=Cascadia Mono",
                "  separator",
                "  item 'Open world clock ' url=https://time.example/world",
                "item 'Refresh ' refresh"),
            Render(result.Output));
    }

    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Output", "Fixtures", .. parts]);

    private static string ReadFixture(params string[] parts) => File.ReadAllText(FixturePath(parts));

    private static string? FindRepositoryDirectory(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TrayWisp.slnx")))
            {
                string path = Path.Combine([directory.FullName, .. parts]);
                return Directory.Exists(path) ? path : null;
            }
        }

        return null;
    }
}
