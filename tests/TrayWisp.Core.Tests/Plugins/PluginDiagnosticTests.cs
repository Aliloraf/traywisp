using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Plugins;

public sealed class PluginDiagnosticTests
{
    [Fact]
    public void ToStringIncludesCodePathAndLine()
    {
        var diagnostic = new PluginDiagnostic(PluginDiagnosticSeverity.Warning, PluginDiagnosticCode.MetadataFieldConflict, "Conflict.")
        {
            Path = "work/clock.5s.ps1",
            Line = 3,
        };

        // The path is quoted since it is untrusted text.
        Assert.Equal("Warning TW230 'work/clock.5s.ps1':3: Conflict.", diagnostic.ToString());
        Assert.Equal("Error TW300: Missing.", new PluginDiagnostic(PluginDiagnosticSeverity.Error, PluginDiagnosticCode.RootNotFound, "Missing.").ToString());
    }

    [Fact]
    public void ToStringEscapesBidirectionalControlsInThePath()
    {
        // Displayed raw, U+202E would reverse the rest of the line, so "evil<RLO>sp1.exe" would read
        // as "evilexe.1ps" and the diagnostic text after it would be reordered too.
        string text = Info("sub/evil\u202Esp1.exe").ToString();

        Assert.Equal("Info TW311 'sub/evil\\u202Esp1.exe': Skipped.", text);
        Assert.DoesNotContain('\u202E', text);
        Assert.Equal("Info TW311 'a\\u2066b\\u200Fc\\u061C': Skipped.", Info("a\u2066b\u200Fc\u061C").ToString());
    }

    [Fact]
    public void ToStringDistinguishesLiteralBackslashesFromEscapes()
    {
        // A name that literally contains the text "\u202E" (possible on non-Windows file systems) must
        // not look like a name containing the escaped character.
        string literal = Info("a\\u202Eb.ps1").ToString();
        string escaped = Info("a\u202Eb.ps1").ToString();

        Assert.Equal("Info TW311 'a\\\\u202Eb.ps1': Skipped.", literal);
        Assert.Equal("Info TW311 'a\\u202Eb.ps1': Skipped.", escaped);
        Assert.NotEqual(literal, escaped);
    }

    [Fact]
    public void ToStringEscapesControlCharactersAndQuotesInThePath()
    {
        // A path must not be able to end the line, start a fake diagnostic or close the quotes early.
        var diagnostic = Info("x.ps1\r\nError TW300 'y.ps1': forged\u001B[2K") with { Line = 7 };

        string text = diagnostic.ToString();

        Assert.Equal("Info TW311 'x.ps1\\u000D\\u000AError TW300 \\u0027y.ps1\\u0027: forged\\u001B[2K':7: Skipped.", text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\u001B', text);
    }

    [Fact]
    public void ToStringBoundsLongPaths()
    {
        string path = string.Join('/', Enumerable.Repeat("directory", 2000)) + "/clock.ps1";
        var diagnostic = Info(path);

        string text = diagnostic.ToString();

        string expectedPath = "'" + path[..PluginDiagnostic.MaxPathLengthInText] + "...'";
        Assert.Equal($"Info TW311 {expectedPath}: Skipped.", text);
        Assert.Equal(path, diagnostic.Path);
    }

    [Fact]
    public void ToStringLeavesThePathPropertyRaw()
    {
        string path = "a\u202Eb/it's.ps1";
        var diagnostic = Info(path);

        _ = diagnostic.ToString();

        Assert.Equal(path, diagnostic.Path);
    }

    [Fact]
    public void CodesAreUniqueAndGroupedByArea()
    {
        int[] values = [.. Enum.GetValues<PluginDiagnosticCode>().Select(code => (int)code)];

        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.All(values, value => Assert.InRange(value, 100, 399));
    }

    private static PluginDiagnostic Info(string path) =>
        new(PluginDiagnosticSeverity.Info, PluginDiagnosticCode.ReparsePointSkipped, "Skipped.") { Path = path };
}
