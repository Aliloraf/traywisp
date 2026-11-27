using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Plugins;

public sealed class PluginDiagnosticTextTests
{
    [Theory]
    [InlineData("clock.ps1", "'clock.ps1'")]
    [InlineData("météo 日本 🚀", "'météo 日本 🚀'")]
    [InlineData("", "''")]
    [InlineData("it's", "'it\\u0027s'")]
    [InlineData("a\u202Eb", "'a\\u202Eb'")]
    [InlineData("a\u2066b\u200Fc", "'a\\u2066b\\u200Fc'")]
    [InlineData("line\r\nbreak\t", "'line\\u000D\\u000Abreak\\u0009'")]
    [InlineData("\u001B[31mred", "'\\u001B[31mred'")]
    [InlineData("a\u2028b", "'a\\u2028b'")]
    [InlineData("zero\u200Bwidth", "'zero\\u200Bwidth'")]
    [InlineData("C:\\plugins\\clock.ps1", "'C:\\\\plugins\\\\clock.ps1'")]
    [InlineData("\\", "'\\\\'")]
    public void EscapesCharactersThatHideOrReorderText(string value, string expected) =>
        Assert.Equal(expected, PluginDiagnosticText.Quote(value));

    [Fact]
    public void LiteralBackslashesCannotImitateEscapes()
    {
        // Text that looks like an escape is shown with a doubled backslash, so it differs from the
        // escape of the real character.
        string imitation = PluginDiagnosticText.Quote("a\\u202Eb");
        string real = PluginDiagnosticText.Quote("a\u202Eb");

        Assert.Equal("'a\\\\u202Eb'", imitation);
        Assert.Equal("'a\\u202Eb'", real);
        Assert.NotEqual(imitation, real);
        Assert.Equal("'\\\\u0027'", PluginDiagnosticText.Quote("\\u0027"));
        Assert.Equal("'\\u0027'", PluginDiagnosticText.Quote("'"));
    }

    [Fact]
    public void EscapesLoneSurrogates()
    {
        // Built in code: attribute arguments cannot carry lone surrogates.
        Assert.Equal("'lone\\uD800'", PluginDiagnosticText.Quote("lone" + (char)0xD800));
        Assert.Equal("'\\uDC00lone'", PluginDiagnosticText.Quote((char)0xDC00 + "lone"));
        Assert.Equal("'\\uDC00\\uD800'", PluginDiagnosticText.Quote(new string([(char)0xDC00, (char)0xD800])));
    }

    [Fact]
    public void LimitsLength()
    {
        string quoted = PluginDiagnosticText.Quote(new string('a', 1000), 10);

        Assert.Equal("'aaaaaaaaaa...'", quoted);
        Assert.Equal("'abc'", PluginDiagnosticText.Quote("abc", 3));
    }

    [Fact]
    public void DoesNotSplitSurrogatePairsAtTheLimit()
    {
        string quoted = PluginDiagnosticText.Quote("ab🚀cd", 3);

        Assert.Equal("'ab...'", quoted);
    }

    [Fact]
    public void RejectsNonPositiveLimits() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginDiagnosticText.Quote("x", 0));
}
