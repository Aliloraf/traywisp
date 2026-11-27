using System.Globalization;
using System.Text;
using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Plugins;

public sealed class PluginFileNameParserTests
{
    [Theory]
    [InlineData("clock.5s.ps1", "clock", 5L, RefreshIntervalUnit.Seconds, 5_000L, PluginLanguage.PowerShell)]
    [InlineData("cpu.500ms.ps1", "cpu", 500L, RefreshIntervalUnit.Milliseconds, 500L, PluginLanguage.PowerShell)]
    [InlineData("weather.10m.py", "weather", 10L, RefreshIntervalUnit.Minutes, 600_000L, PluginLanguage.Python)]
    [InlineData("github.notifications.5m.py", "github.notifications", 5L, RefreshIntervalUnit.Minutes, 300_000L, PluginLanguage.Python)]
    [InlineData("CLOCK.5S.PS1", "CLOCK", 5L, RefreshIntervalUnit.Seconds, 5_000L, PluginLanguage.PowerShell)]
    [InlineData("backup.2h.js", "backup", 2L, RefreshIntervalUnit.Hours, 7_200_000L, PluginLanguage.JavaScript)]
    [InlineData("report.1d.sh", "report", 1L, RefreshIntervalUnit.Days, 86_400_000L, PluginLanguage.Bash)]
    [InlineData("disk.30s.bat", "disk", 30L, RefreshIntervalUnit.Seconds, 30_000L, PluginLanguage.Batch)]
    [InlineData("disk.30s.cmd", "disk", 30L, RefreshIntervalUnit.Seconds, 30_000L, PluginLanguage.Batch)]
    public void ParsesNameIntervalAndExtension(string fileName, string name, long value, RefreshIntervalUnit unit, long milliseconds, PluginLanguage language)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

        Assert.True(result.IsParsed);
        Assert.Null(result.Diagnostic);
        Assert.Equal(fileName, result.FileName.FileName);
        Assert.Equal(name, result.FileName.Name);
        Assert.Equal(language, result.FileName.Language);
        Assert.False(result.FileName.IsManualRefresh);
        RefreshInterval interval = Assert.IsType<RefreshInterval>(result.FileName.RefreshInterval);
        Assert.Equal(value, interval.Value);
        Assert.Equal(unit, interval.Unit);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), interval.Duration);
    }

    [Theory]
    [InlineData("clock.ps1", "clock")]
    [InlineData("github.notifications.py", "github.notifications")]
    [InlineData("a.b.c.d.js", "a.b.c.d")]
    [InlineData("tool.v2.ps1", "tool.v2")]
    [InlineData("chart.3d-view.ps1", "chart.3d-view")]
    [InlineData("2024.ps1", "2024")]
    [InlineData("-5s.ps1", "-5s")]
    [InlineData("my clock.ps1", "my clock")]
    public void MissingIntervalMeansManualRefresh(string fileName, string expectedName)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

        Assert.True(result.IsParsed);
        Assert.Equal(expectedName, result.FileName.Name);
        Assert.Null(result.FileName.RefreshInterval);
        Assert.True(result.FileName.IsManualRefresh);
    }

    [Theory]
    [InlineData("x.250ms.ps1", "x.250MS.ps1")]
    [InlineData("x.5s.ps1", "x.5S.ps1")]
    [InlineData("x.5m.ps1", "x.5M.ps1")]
    [InlineData("x.5h.ps1", "x.5H.ps1")]
    [InlineData("x.5d.ps1", "x.5D.ps1")]
    public void IntervalUnitsAreCaseInsensitive(string lowerCase, string upperCase) =>
        Assert.Equal(ParseInterval(lowerCase), ParseInterval(upperCase));

    [Theory]
    [InlineData("x.250mS.ps1", RefreshIntervalUnit.Milliseconds)]
    [InlineData("x.250Ms.ps1", RefreshIntervalUnit.Milliseconds)]
    [InlineData("x.5M.ps1", RefreshIntervalUnit.Minutes)]
    [InlineData("x.5H.Py", RefreshIntervalUnit.Hours)]
    [InlineData("x.5D.jS", RefreshIntervalUnit.Days)]
    public void MixedCaseUnitsAndExtensionsAreAccepted(string fileName, RefreshIntervalUnit unit) =>
        Assert.Equal(unit, ParseInterval(fileName).Unit);

    [Theory]
    [InlineData("x.250ms.ps1")]
    [InlineData("x.1s.ps1")]
    [InlineData("x.1m.ps1")]
    [InlineData("x.1h.ps1")]
    [InlineData("x.1d.ps1")]
    [InlineData("x.30d.ps1")]
    [InlineData("x.720h.ps1")]
    [InlineData("x.43200m.ps1")]
    [InlineData("x.2592000s.ps1")]
    [InlineData("x.2592000000ms.ps1")]
    [InlineData("x.0005s.ps1")]
    public void AcceptsBoundaryIntervals(string fileName) => Assert.True(PluginFileNameParser.Parse(fileName).IsParsed);

    [Theory]
    [InlineData("x.249ms.ps1")]
    [InlineData("x.1ms.ps1")]
    [InlineData("x.31d.ps1")]
    [InlineData("x.721h.ps1")]
    [InlineData("x.43201m.ps1")]
    [InlineData("x.2592001s.ps1")]
    [InlineData("x.2592000001ms.ps1")]
    [InlineData("clock.999999999999h.ps1")]
    [InlineData("x.99999999999999999999999999999999s.ps1")]
    [InlineData("x.9223372036854775807ms.ps1")]
    [InlineData("x.9223372036854775808ms.ps1")]
    public void RejectsOutOfRangeAndOverflowingIntervals(string fileName) =>
        AssertRejected(fileName, PluginDiagnosticCode.IntervalOutOfRange);

    [Theory]
    [InlineData("clock.0s.ps1")]
    [InlineData("clock.0ms.ps1")]
    [InlineData("clock.000d.ps1")]
    public void RejectsZeroIntervals(string fileName) => AssertRejected(fileName, PluginDiagnosticCode.ZeroInterval);

    [Theory]
    [InlineData("clock.-1s.ps1")]
    [InlineData("clock.-0s.ps1")]
    [InlineData("clock.-500ms.py")]
    [InlineData("clock.-5.ps1")]
    public void RejectsNegativeIntervals(string fileName) => AssertRejected(fileName, PluginDiagnosticCode.NegativeInterval);

    [Theory]
    [InlineData("clock.1.5s.ps1")] // fractional
    [InlineData("1.5s.ps1")] // fractional with no other name
    [InlineData("clock.0.5m.ps1")]
    [InlineData("server.2.5m.ps1")] // documented: digits-only segment before an interval is fractional
    [InlineData("clock.1,5s.ps1")] // decimal comma
    [InlineData("clock.5sec.ps1")] // unknown unit
    [InlineData("clock.10min.ps1")]
    [InlineData("clock.1w.ps1")]
    [InlineData("clock.5x.ps1")]
    [InlineData("clock.1e3s.ps1")]
    [InlineData("clock.1_000s.ps1")]
    [InlineData("clock.+5s.ps1")] // sign
    [InlineData("clock.+5.ps1")] // sign without unit
    [InlineData("clock. 5s.ps1")] // whitespace
    [InlineData("clock.5s .ps1")]
    [InlineData("chapter.2nd.ps1")] // documented false positive: digit-initial alphanumeric segment
    [InlineData("clock.٥s.ps1")] // Arabic-Indic digit five
    [InlineData("clock.５s.ps1")] // fullwidth digit five
    [InlineData("clock.5ｓ.ps1")] // fullwidth letter s
    public void RejectsMalformedIntervals(string fileName) => AssertRejected(fileName, PluginDiagnosticCode.MalformedInterval);

    [Theory]
    [InlineData("backup.2024.ps1", "backup.2024", "'2024'")]
    [InlineData("clock.5.ps1", "clock.5", "'5'")]
    [InlineData("release.1.2.ps1", "release.1.2", "'2'")]
    [InlineData("report.\u0662\u0660.ps1", "report.\u0662\u0660", "'\u0662\u0660'")]
    public void DigitsOnlyFinalSegmentIsANameSegmentWithAnInformationalNote(string fileName, string expectedName, string quotedSegment)
    {
        // A number without a unit is not an interval (intervals always have a unit), so it is kept as part
        // of the name. The note keeps a forgotten unit from going unnoticed.
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

        Assert.True(result.IsParsed);
        Assert.Equal(expectedName, result.FileName.Name);
        Assert.True(result.FileName.IsManualRefresh);
        PluginDiagnostic note = Assert.IsType<PluginDiagnostic>(result.Diagnostic);
        Assert.Equal(PluginDiagnosticCode.NumericNameSegment, note.Code);
        Assert.Equal(PluginDiagnosticSeverity.Info, note.Severity);
        Assert.Contains(quotedSegment, note.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("backup.2024.v2.ps1")]
    [InlineData("2024.ps1")]
    [InlineData("tool.v2.ps1")]
    [InlineData("clock.5s.ps1")]
    public void NoNoteWhenTheFinalSegmentIsNotABareNumber(string fileName)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

        Assert.True(result.IsParsed);
        Assert.Null(result.Diagnostic);
    }

    [Theory]
    [InlineData("report\u202Eslx.ps1")] // right-to-left override: displays as "reportsp1.xls"
    [InlineData("clock\u202D.ps1")]
    [InlineData("a\u2066b.5s.ps1")]
    [InlineData("a\u200Fb.py")]
    [InlineData("a\u061Cb.js")]
    public void RejectsBidirectionalControlCharacters(string fileName) => AssertRejected(fileName, PluginDiagnosticCode.InvalidName);

    [Fact]
    public void DiagnosticsQuoteAndBoundUntrustedText()
    {
        // Before the fix, this message repeated about 480 characters of the file name.
        string longUnit = "x.9" + new string('z', 240) + ".ps1";

        string message = PluginFileNameParser.Parse(longUnit).Diagnostic!.Message;

        Assert.True(message.Length < 300, message);
        Assert.Contains("...'", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5s.ps1")]
    [InlineData("0ms.py")]
    [InlineData(".5s.ps1")]
    public void RejectsIntervalWithoutName(string fileName) => AssertRejected(fileName, PluginDiagnosticCode.MissingName);

    [Theory]
    [InlineData(".ps1")]
    [InlineData(" .ps1")]
    [InlineData(" clock.ps1")]
    [InlineData("clock .5s.ps1")]
    [InlineData("a..5s.ps1")]
    [InlineData("a..b.ps1")]
    [InlineData(".clock.ps1")]
    [InlineData("CON.ps1")]
    [InlineData("nul.5s.py")]
    [InlineData("com1.ps1")]
    [InlineData("LPT¹.ps1")]
    [InlineData("a<b.ps1")]
    [InlineData("a|b.ps1")]
    [InlineData("a:b.ps1")]
    [InlineData("a\\b.ps1")]
    [InlineData("a/b.ps1")]
    [InlineData("tab\there.ps1")]
    [InlineData("nul\0char.ps1")]
    public void RejectsInvalidNames(string fileName)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

        Assert.Equal(PluginFileNameParseStatus.Rejected, result.Status);
        PluginDiagnostic diagnostic = Assert.IsType<PluginDiagnostic>(result.Diagnostic);
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(diagnostic.Code, new[] { PluginDiagnosticCode.InvalidName, PluginDiagnosticCode.MissingName });
    }

    [Fact]
    public void RejectsOverlongFileNames()
    {
        string tooLong = new string('a', PluginFileNameParser.MaxFileNameLength - 3) + ".ps1";
        string longest = new string('a', PluginFileNameParser.MaxFileNameLength - 4) + ".ps1";

        AssertRejected(tooLong, PluginDiagnosticCode.InvalidName);
        Assert.True(PluginFileNameParser.Parse(longest).IsParsed);
    }

    [Theory]
    [InlineData("météo du jour.10m.py", "météo du jour")]
    [InlineData("日本語の時計.5s.js", "日本語の時計")]
    [InlineData("Ünïcödé.Nämé.1h.ps1", "Ünïcödé.Nämé")]
    [InlineData("rocket 🚀 status.30s.ps1", "rocket 🚀 status")]
    [InlineData("İstanbul.5m.ps1", "İstanbul")]
    [InlineData("  inner  spaces  .ps1", null)]
    [InlineData("inner  double  spaces.ps1", "inner  double  spaces")]
    public void SupportsUnicodeAndSpacesInNames(string fileName, string? expectedName)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

        if (expectedName is null)
        {
            // Leading and trailing whitespace is rejected; inner whitespace is kept.
            Assert.Equal(PluginDiagnosticCode.InvalidName, result.Diagnostic?.Code);
            return;
        }

        Assert.True(result.IsParsed);
        Assert.Equal(expectedName, result.FileName.Name);
    }

    [Theory]
    [InlineData("readme.md")]
    [InlineData("tool.exe")]
    [InlineData("tool.5s.exe")]
    [InlineData("script.ps1.txt")]
    [InlineData("module.psm1")]
    [InlineData("script.pyw")]
    [InlineData("module.mjs")]
    [InlineData("clock")]
    [InlineData("clock.")]
    [InlineData("clock.5s")]
    [InlineData("clock.ps1 ")]
    [InlineData("")]
    [InlineData(null)]
    public void UnrelatedFilesAreNotCandidates(string? fileName)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

        Assert.Equal(PluginFileNameParseStatus.NotCandidate, result.Status);
        Assert.Null(result.FileName);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void ExtensionCasingIsPreserved()
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse("Clock.5S.Ps1");

        Assert.True(result.IsParsed);
        Assert.Equal("Ps1", result.FileName.Extension);
        Assert.Equal("Clock", result.FileName.Name);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void ParsingIsCultureIndependent(string cultureName)
    {
        string[] fileNames =
        [
            "CLOCK.5S.PS1", "clock.250MS.ps1", "İNDEX.10M.PY", "ınfo.1h.js", "clock.1.5s.ps1", "clock.٥s.ps1",
            "x.2592000000ms.ps1", "x.43201m.ps1",
        ];
        string[] invariant = Describe(fileNames);

        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

            Assert.Equal(invariant, Describe(fileNames));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        static string[] Describe(string[] names) =>
        [
            .. names.Select(name =>
            {
                PluginFileNameParseResult result = PluginFileNameParser.Parse(name);
                return $"{result.Status}|{result.FileName?.Name}|{result.FileName?.RefreshInterval}|{result.Diagnostic?.Code}";
            }),
        ];
    }

    [Fact]
    public void ArbitraryInputNeverThrows()
    {
        // Deterministic pseudo-random file names built from characters that exercise every rule.
        string[] alphabet =
        [
            ".", ".", "..", "0", "1", "5", "9", "-", "+", "_", ",", " ", "\t", "\0", "ms", "s", "m", "h", "d", "S",
            "ps1", "py", "js", "sh", "bat", "cmd", "a", "Z", "é", "٥", "５", "🚀", "\uD800", "\uDC00",
            "<", ">", ":", "\"", "/", "\\", "|", "?", "*", "CON", "999999999999999999999", "\u202E", "'",
        ];
        var random = new Random(20261126);
        var builder = new StringBuilder();

        for (int i = 0; i < 20_000; i++)
        {
            builder.Clear();
            int parts = random.Next(0, 12);
            for (int j = 0; j < parts; j++)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            if (random.Next(2) == 0)
            {
                builder.Append('.').Append(alphabet[random.Next(20, 26)]);
            }

            string fileName = builder.ToString();
            PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

            switch (result.Status)
            {
                case PluginFileNameParseStatus.Parsed:
                    Assert.NotNull(result.FileName);
                    Assert.Equal(fileName, result.FileName.FileName);
                    Assert.NotEmpty(result.FileName.Name);
                    Assert.False(char.IsWhiteSpace(result.FileName.Name[0]) || char.IsWhiteSpace(result.FileName.Name[^1]));
                    Assert.DoesNotContain('\u202E', result.FileName.Name);
                    if (result.FileName.RefreshInterval is RefreshInterval interval)
                    {
                        Assert.InRange(interval.Duration, RefreshInterval.Minimum, RefreshInterval.Maximum);
                    }

                    Assert.True(result.Diagnostic is null || result.Diagnostic.Severity == PluginDiagnosticSeverity.Info);
                    break;
                case PluginFileNameParseStatus.Rejected:
                    Assert.NotNull(result.Diagnostic);
                    Assert.Equal(PluginDiagnosticSeverity.Error, result.Diagnostic.Severity);
                    break;
                default:
                    Assert.Null(result.Diagnostic);
                    break;
            }

            if (result.Diagnostic is PluginDiagnostic diagnostic)
            {
                Assert.True(diagnostic.Message.Length < 400, diagnostic.Message);
                Assert.DoesNotContain(diagnostic.Message, c => char.IsControl(c) || c == '\u202E');
            }
        }
    }

    private static RefreshInterval ParseInterval(string fileName)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);
        Assert.True(result.IsParsed, $"'{fileName}' should parse: {result.Diagnostic}");
        return Assert.IsType<RefreshInterval>(result.FileName.RefreshInterval);
    }

    private static void AssertRejected(string fileName, PluginDiagnosticCode code)
    {
        PluginFileNameParseResult result = PluginFileNameParser.Parse(fileName);

        Assert.Equal(PluginFileNameParseStatus.Rejected, result.Status);
        Assert.Null(result.FileName);
        PluginDiagnostic diagnostic = Assert.IsType<PluginDiagnostic>(result.Diagnostic);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
    }
}
