using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Plugins;

public sealed class PluginDescriptorTests
{
    [Fact]
    public void DisplayTitleFallsBackToFileName()
    {
        PluginDescriptor descriptor = Create("work/github.notifications.5m.py", PluginMetadata.Empty);

        Assert.Equal("github.notifications", descriptor.DisplayTitle);
        Assert.Equal(PluginLanguage.Python, descriptor.FileName.Language);
        Assert.Empty(descriptor.Diagnostics);
    }

    [Fact]
    public void DisplayTitleUsesMetadataTitle()
    {
        PluginDescriptor descriptor = Create("clock.5s.ps1", new PluginMetadata { Title = "World clock" });

        Assert.Equal("World clock", descriptor.DisplayTitle);
    }

    [Fact]
    public void RequiresIdentityToMatchFileName()
    {
        PluginFileName fileName = Parse("clock.5s.ps1");

        Assert.Throws<ArgumentException>(() => new PluginDescriptor(PluginId.Create("other.5s.ps1"), fileName, PluginMetadata.Empty, []));
        Assert.Throws<ArgumentException>(() => new PluginDescriptor(PluginId.Create("CLOCK.5S.PS1"), fileName, PluginMetadata.Empty, []));
    }

    [Fact]
    public void DefaultDiagnosticsBecomeEmpty()
    {
        PluginDescriptor descriptor = new(PluginId.Create("clock.ps1"), Parse("clock.ps1"), PluginMetadata.Empty, default);

        Assert.False(descriptor.Diagnostics.IsDefault);
        Assert.Empty(descriptor.Diagnostics);
    }

    private static PluginDescriptor Create(string path, PluginMetadata metadata)
    {
        PluginId id = PluginId.Create(path);
        return new PluginDescriptor(id, Parse(id.FileName), metadata, []);
    }

    private static PluginFileName Parse(string fileName)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);
        Assert.True(result.IsParsed);
        return result.FileName;
    }
}
