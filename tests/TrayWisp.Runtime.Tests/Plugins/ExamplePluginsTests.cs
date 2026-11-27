using TrayWisp.Core.Plugins;
using TrayWisp.Runtime.Plugins;
using Xunit;

namespace TrayWisp.Runtime.Tests.Plugins;

// Keeps plugins/examples consistent with the documented format.
public sealed class ExamplePluginsTests
{
    [Fact]
    public void ExamplePluginsAreDiscoveredWithoutDiagnostics()
    {
        string? examples = FindExamplesDirectory();
        Assert.SkipWhen(examples is null, "The repository's plugins/examples directory was not found next to the test output.");

        PluginDiscoveryResult result = PluginDiscovery.Discover(examples, cancellationToken: TestContext.Current.CancellationToken);

        string[] expected = ["clock.1m.ps1", "day-progress.10m.py", "hello.js"];
        Assert.Equal(expected, result.Plugins.Select(plugin => plugin.Id.Value));
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Plugins, plugin =>
        {
            Assert.Empty(plugin.Diagnostics);
            Assert.NotNull(plugin.Metadata.Title);
            Assert.NotNull(plugin.Metadata.Description);
            Assert.Equal(PluginExecutionSupport.Planned, plugin.FileName.ExecutionSupport);
        });
        string[] titles = ["Clock", "Day progress", "Hello"];
        Assert.Equal(titles, result.Plugins.Select(plugin => plugin.DisplayTitle));
    }

    private static string? FindExamplesDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TrayWisp.slnx")))
            {
                string examples = Path.Combine(directory.FullName, "plugins", "examples");
                return Directory.Exists(examples) ? examples : null;
            }
        }

        return null;
    }
}
