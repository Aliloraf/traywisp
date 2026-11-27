using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Plugins;

public sealed class PluginLanguagesTests
{
    [Theory]
    [InlineData("ps1", PluginLanguage.PowerShell, PluginExecutionSupport.Planned)]
    [InlineData(".PS1", PluginLanguage.PowerShell, PluginExecutionSupport.Planned)]
    [InlineData("py", PluginLanguage.Python, PluginExecutionSupport.Planned)]
    [InlineData("Py", PluginLanguage.Python, PluginExecutionSupport.Planned)]
    [InlineData("js", PluginLanguage.JavaScript, PluginExecutionSupport.Planned)]
    [InlineData("sh", PluginLanguage.Bash, PluginExecutionSupport.Deferred)]
    [InlineData("bat", PluginLanguage.Batch, PluginExecutionSupport.Deferred)]
    [InlineData(".CMD", PluginLanguage.Batch, PluginExecutionSupport.Deferred)]
    public void ClassifiesRecognizedExtensions(string extension, PluginLanguage language, PluginExecutionSupport support)
    {
        Assert.True(PluginLanguages.TryClassifyExtension(extension, out PluginLanguage actual));
        Assert.Equal(language, actual);
        Assert.Equal(support, PluginLanguages.GetExecutionSupport(actual));
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("com")]
    [InlineData("scr")]
    [InlineData("msi")]
    [InlineData("vbs")]
    [InlineData("psm1")]
    [InlineData("pyw")]
    [InlineData("mjs")]
    [InlineData("ps1 ")]
    [InlineData("..ps1")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(null)]
    public void RejectsOtherExtensions(string? extension) =>
        Assert.False(PluginLanguages.TryClassifyExtension(extension, out _));

    [Fact]
    public void EveryLanguageHasAnExecutionSupportLevel()
    {
        foreach (PluginLanguage language in Enum.GetValues<PluginLanguage>())
        {
            Assert.True(Enum.IsDefined(PluginLanguages.GetExecutionSupport(language)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => PluginLanguages.GetExecutionSupport((PluginLanguage)99));
    }
}
