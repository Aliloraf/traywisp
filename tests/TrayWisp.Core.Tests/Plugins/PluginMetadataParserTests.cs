using System.Text;
using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Plugins;

public sealed class PluginMetadataParserTests
{
    private const string FullXbarHeader = """
        #!/usr/bin/env python3
        # <xbar.title>Weather</xbar.title>
        # <xbar.version>v1.0</xbar.version>
        # <xbar.author>Jane Doe, John Roe</xbar.author>
        # <xbar.author.github>janedoe,johnroe</xbar.author.github>
        # <xbar.desc>Shows the current weather.</xbar.desc>
        # <xbar.image>https://example.invalid/weather.png</xbar.image>
        # <xbar.dependencies>python3,requests</xbar.dependencies>
        # <xbar.abouturl>https://example.invalid/weather</xbar.abouturl>
        print("Sunny")
        """;

    [Fact]
    public void ParsesAllSupportedFields()
    {
        PluginMetadataParseResult result = Parse(FullXbarHeader);

        Assert.Empty(result.Diagnostics);
        PluginMetadata metadata = result.Metadata;
        Assert.Equal("Weather", metadata.Title);
        Assert.Equal("v1.0", metadata.Version);
        Assert.Equal("Jane Doe, John Roe", metadata.Author);
        Assert.Equal("janedoe,johnroe", metadata.AuthorGitHub);
        Assert.Equal("Shows the current weather.", metadata.Description);
        Assert.Equal("https://example.invalid/weather.png", metadata.Image);
        Assert.Equal("python3,requests", metadata.Dependencies);
        Assert.Equal("https://example.invalid/weather", metadata.AboutUrl);
    }

    [Theory]
    [InlineData("traywisp")]
    [InlineData("swiftbar")]
    [InlineData("xbar")]
    [InlineData("bitbar")]
    [InlineData("XBar")]
    [InlineData("TRAYWISP")]
    public void AcceptsEveryNamespace(string ns)
    {
        PluginMetadataParseResult result = Parse($"# <{ns}.title>Clock</{ns}.title>\n# <{ns}.AUTHOR.GitHub>octo</{ns}.author.github>");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Clock", result.Metadata.Title);
        Assert.Equal("octo", result.Metadata.AuthorGitHub);
    }

    [Fact]
    public void MetadataIsOptional()
    {
        PluginMetadataParseResult result = Parse("Write-Output 'hello'\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(PluginMetadata.Empty, result.Metadata);
    }

    [Fact]
    public void EmptyContentHasNoMetadata()
    {
        PluginMetadataParseResult result = PluginMetadataParser.Parse([], PluginLanguage.PowerShell);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(PluginMetadata.Empty, result.Metadata);
    }

    [Fact]
    public void NamespacePrecedenceIsTrayWispSwiftBarXbarBitBar()
    {
        // Lowest precedence first, so source order cannot be what decides.
        PluginMetadataParseResult result = Parse("""
            # <bitbar.title>BitBar</bitbar.title>
            # <xbar.title>xbar</xbar.title>
            # <swiftbar.title>SwiftBar</swiftbar.title>
            # <traywisp.title>TrayWisp</traywisp.title>
            # <bitbar.version>1</bitbar.version>
            # <xbar.version>2</xbar.version>
            # <swiftbar.version>3</swiftbar.version>
            # <bitbar.desc>bitbar only</bitbar.desc>
            # <xbar.author>xbar</xbar.author>
            # <bitbar.author>bitbar</bitbar.author>
            """);

        Assert.Equal("TrayWisp", result.Metadata.Title);
        Assert.Equal("3", result.Metadata.Version);
        Assert.Equal("bitbar only", result.Metadata.Description);
        Assert.Equal("xbar", result.Metadata.Author);

        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(PluginDiagnosticCode.MetadataFieldOverridden, diagnostic.Code);
            Assert.Equal(PluginDiagnosticSeverity.Info, diagnostic.Severity);
        });
        int[] expectedLines = [1, 2, 3, 5, 6, 10];
        Assert.Equal(expectedLines, result.Diagnostics.Select(diagnostic => diagnostic.Line!.Value).Order());
    }

    [Fact]
    public void IdenticalDuplicatesAreMergedSilently()
    {
        PluginMetadataParseResult result = Parse("""
            # <xbar.title>Clock</xbar.title>
            # <bitbar.title>Clock</bitbar.title>
            # <xbar.title>Clock</xbar.title>
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Clock", result.Metadata.Title);
    }

    [Fact]
    public void ConflictingDuplicatesInOneNamespaceKeepTheFirstValueAndWarn()
    {
        PluginMetadataParseResult result = Parse("""
            # <xbar.title>First</xbar.title>
            # <xbar.title>Second</xbar.title>
            # <xbar.title>Third</xbar.title>
            """);

        Assert.Equal("First", result.Metadata.Title);
        Assert.Equal(2, result.Diagnostics.Length);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(PluginDiagnosticCode.MetadataFieldConflict, diagnostic.Code);
            Assert.Equal(PluginDiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Contains("line 1", diagnostic.Message, StringComparison.Ordinal);
        });
        int[] expectedLines = [2, 3];
        Assert.Equal(expectedLines, result.Diagnostics.Select(diagnostic => diagnostic.Line!.Value));
    }

    [Fact]
    public void DiagnosticsDoNotEchoValues()
    {
        PluginMetadataParseResult result = Parse("""
            # <xbar.title>SECRET-ONE</xbar.title>
            # <xbar.title>SECRET-TWO</xbar.title>
            # <bitbar.title>SECRET-THREE</bitbar.title>
            """);

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, diagnostic => Assert.DoesNotContain("SECRET", diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyValuesAreTreatedAsAbsent()
    {
        PluginMetadataParseResult result = Parse("""
            # <traywisp.title>   </traywisp.title>
            # <xbar.title>Fallback</xbar.title>
            # <xbar.desc></xbar.desc>
            """);

        Assert.Equal("Fallback", result.Metadata.Title);
        Assert.Null(result.Metadata.Description);
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Code == PluginDiagnosticCode.MetadataEmptyValue));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == PluginDiagnosticCode.MetadataFieldOverridden);
    }

    [Fact]
    public void ValuesAreTrimmedButOtherwiseKeptVerbatim()
    {
        PluginMetadataParseResult result = Parse("""
            # <xbar.title>  Tom &amp; Jerry &lt;3  </xbar.title>
            # <xbar.version>2024.11.26-beta+build.7</xbar.version>
            # <xbar.desc>a > b and c < d</xbar.desc>
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Tom &amp; Jerry &lt;3", result.Metadata.Title);
        Assert.Equal("2024.11.26-beta+build.7", result.Metadata.Version);
        Assert.Equal("a > b and c < d", result.Metadata.Description);
    }

    [Fact]
    public void TabsInsideValuesAreKept() =>
        Assert.Equal("a\tb", Parse("# <xbar.desc>\ta\tb\t</xbar.desc>").Metadata.Description);

    [Theory]
    [InlineData("v1.0")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("2.0.0-rc.1+sha.abc")]
    [InlineData("nightly")]
    [InlineData("v1.0 (beta)")]
    public void PreservesAuthorVersionStrings(string version) =>
        Assert.Equal(version, Parse($"# <xbar.version>{version}</xbar.version>").Metadata.Version);

    [Theory]
    [InlineData(PluginLanguage.PowerShell, "# <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.PowerShell, "    #<xbar.title>Clock</xbar.title>#")]
    [InlineData(PluginLanguage.PowerShell, "<# <xbar.title>Clock</xbar.title> #>")]
    [InlineData(PluginLanguage.Python, "# <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.Python, "\t#  <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.Bash, "# <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.JavaScript, "// <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.JavaScript, "/* <xbar.title>Clock</xbar.title> */")]
    [InlineData(PluginLanguage.JavaScript, "/** <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.Batch, "REM <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.Batch, "rem <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.Batch, "@REM <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.Batch, ":: <xbar.title>Clock</xbar.title>")]
    [InlineData(PluginLanguage.Batch, "  ::<xbar.title>Clock</xbar.title>")]
    public void ReadsTagsFromTheLanguagesComments(PluginLanguage language, string line)
    {
        PluginMetadataParseResult result = Parse(line, language);

        Assert.Equal("Clock", result.Metadata.Title);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(PluginLanguage.PowerShell, "Write-Output \"<xbar.title>Fake</xbar.title>\"")]
    [InlineData(PluginLanguage.PowerShell, "$title = '<xbar.title>Fake</xbar.title>'")]
    [InlineData(PluginLanguage.PowerShell, "Get-Date # <xbar.title>Fake</xbar.title>")]
    [InlineData(PluginLanguage.PowerShell, "// <xbar.title>Fake</xbar.title>")]
    [InlineData(PluginLanguage.Python, "print(\"<xbar.title>Fake</xbar.title>\")")]
    [InlineData(PluginLanguage.Python, "<xbar.title>Fake</xbar.title>")]
    [InlineData(PluginLanguage.Python, "// <xbar.title>Fake</xbar.title>")]
    [InlineData(PluginLanguage.Python, "<# <xbar.title>Fake</xbar.title> #>")]
    [InlineData(PluginLanguage.Bash, "echo '<xbar.title>Fake</xbar.title>'")]
    [InlineData(PluginLanguage.JavaScript, "console.log(\"<xbar.title>Fake</xbar.title>\");")]
    [InlineData(PluginLanguage.JavaScript, "# <xbar.title>Fake</xbar.title>")]
    [InlineData(PluginLanguage.JavaScript, "const x = 1; // <xbar.title>Fake</xbar.title>")]
    [InlineData(PluginLanguage.Batch, "echo <xbar.title>Fake</xbar.title>")]
    [InlineData(PluginLanguage.Batch, "REMARK <xbar.title>Fake</xbar.title>")]
    [InlineData(PluginLanguage.Batch, "# <xbar.title>Fake</xbar.title>")]
    public void IgnoresTagsOutsideComments(PluginLanguage language, string line)
    {
        // Before the fix, tags anywhere in a line were read, including in code and string literals.
        PluginMetadataParseResult result = Parse(line, language);

        Assert.Null(result.Metadata.Title);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataTagOutsideComment, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal(1, diagnostic.Line);
    }

    [Fact]
    public void TagsOutsideCommentsAreReportedOnceAndDoNotAffectCommentTags()
    {
        PluginMetadataParseResult result = Parse("""
            # <xbar.title>Real</xbar.title>
            Write-Output "<xbar.title>Fake</xbar.title>"
            Write-Output "<xbar.desc>Fake</xbar.desc>"
            # <xbar.desc>Real description</xbar.desc>
            """);

        Assert.Equal("Real", result.Metadata.Title);
        Assert.Equal("Real description", result.Metadata.Description);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataTagOutsideComment, diagnostic.Code);
        Assert.Equal(2, diagnostic.Line);
    }

    [Fact]
    public void JavaScriptBlockCommentsSpanLines()
    {
        PluginMetadataParseResult result = Parse("""
            #!/usr/bin/env node
            /**
             * <xbar.title>Clock</xbar.title>
             * <xbar.version>2</xbar.version> */ const s = "<xbar.desc>Fake</xbar.desc>";
            const t = "<xbar.author>Fake</xbar.author>";
            """, PluginLanguage.JavaScript);

        Assert.Equal("Clock", result.Metadata.Title);
        Assert.Equal("2", result.Metadata.Version);
        Assert.Null(result.Metadata.Description);
        Assert.Null(result.Metadata.Author);
        Assert.Equal(PluginDiagnosticCode.MetadataTagOutsideComment, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void DocstringLinesWithoutACommentMarkerAreIgnored()
    {
        PluginMetadataParseResult result = Parse("\"\"\"\n<xbar.title>Docstring</xbar.title>\n\"\"\"\n", PluginLanguage.Python);

        Assert.Null(result.Metadata.Title);
    }

    // Documented limitation: the parser recognizes comment-shaped lines and is not a tokenizer, so a
    // line inside a multi-line string literal that starts with a comment marker is read as a comment.
    [Theory]
    [InlineData(PluginLanguage.Python, "text = \"\"\"\n# <xbar.title>In string</xbar.title>\n\"\"\"\n")]
    [InlineData(PluginLanguage.PowerShell, "$text = @'\n# <xbar.title>In string</xbar.title>\n'@\n")]
    [InlineData(PluginLanguage.Bash, "cat <<EOF\n# <xbar.title>In string</xbar.title>\nEOF\n")]
    public void CommentShapedLinesInsideMultiLineStringsAreRead(PluginLanguage language, string text)
    {
        PluginMetadataParseResult result = Parse(text, language);

        Assert.Equal("In string", result.Metadata.Title);
    }

    [Fact]
    public void UnknownLanguageIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginMetadataParser.Parse([], (PluginLanguage)42));

    [Fact]
    public void ParsesTagsInsidePowerShellBlockComment()
    {
        PluginMetadataParseResult result = Parse("""
            <#
            <xbar.title>Disk usage</xbar.title>
            <xbar.version>v2</xbar.version>
            #>
            Get-PSDrive
            """);

        Assert.Equal("Disk usage", result.Metadata.Title);
        Assert.Equal("v2", result.Metadata.Version);
    }

    [Fact]
    public void ParsesSeveralTagsOnOneLine()
    {
        PluginMetadataParseResult result = Parse("# <xbar.title>A</xbar.title> <xbar.version>1</xbar.version><xbar.desc>D</xbar.desc>");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("A", result.Metadata.Title);
        Assert.Equal("1", result.Metadata.Version);
        Assert.Equal("D", result.Metadata.Description);
    }

    [Fact]
    public void ClosingTagIsCaseInsensitive() =>
        Assert.Equal("Clock", Parse("# <xbar.title>Clock</XBAR.TITLE>").Metadata.Title);

    [Theory]
    [InlineData("# <xbar.title>Clock")]
    [InlineData("# <xbar.title>Clock</xbar.desc>")]
    [InlineData("# <xbar.title lang=\"en\">Clock</xbar.title>")]
    [InlineData("# <xbar.title/>")]
    [InlineData("# <xbar.title >Clock</xbar.title>")]
    [InlineData("# <xbar.title>Clock</xbar.title")]
    [InlineData("# <xbar.title>Outer <xbar.desc>Inner</xbar.desc></xbar.title>")]
    [InlineData("# <xbar.title>A</b>B</xbar.title>")]
    public void ReportsMalformedTags(string line)
    {
        PluginMetadataParseResult result = Parse(line);

        Assert.Null(result.Metadata.Title);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Code == PluginDiagnosticCode.MetadataMalformedTag);
        Assert.Equal(PluginDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, diagnostic.Line);
    }

    [Fact]
    public void NestedTagIsReportedAndInnerTagStillParsed()
    {
        PluginMetadataParseResult result = Parse("# <xbar.title>Outer <xbar.desc>Inner</xbar.desc></xbar.title>");

        Assert.Equal("Inner", result.Metadata.Description);
    }

    [Fact]
    public void TagsMustNotSpanLines()
    {
        PluginMetadataParseResult result = Parse("# <xbar.desc>first line\n# second line</xbar.desc>");

        Assert.Null(result.Metadata.Description);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == PluginDiagnosticCode.MetadataMalformedTag && diagnostic.Line == 1);
    }

    [Theory]
    [InlineData("<html><body>x</body></html>")]
    [InlineData("List<string> items = new List<string>();")]
    [InlineData("if (a < b && c > d) { }")]
    [InlineData("<foo.title>Not ours</foo.title>")]
    [InlineData("<xbartitle>Not ours</xbartitle>")]
    [InlineData("<xbar.>Not ours</xbar.>")]
    [InlineData("<xbar>Not ours</xbar>")]
    [InlineData("<<<<<<<<")]
    public void IgnoresMarkupOutsideRecognizedNamespaces(string line)
    {
        PluginMetadataParseResult result = Parse("# " + line);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(PluginMetadata.Empty, result.Metadata);
    }

    [Theory]
    [InlineData("xbar.color")]
    [InlineData("traywisp.schedule")]
    [InlineData("bitbar.hideAbout")]
    [InlineData("swiftbar.author.twitter")]
    public void ReportsUnknownFields(string tag)
    {
        PluginDiagnostic diagnostic = Assert.Single(Parse($"# <{tag}>x</{tag}>").Diagnostics);

        Assert.Equal(PluginDiagnosticCode.MetadataUnknownField, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Theory]
    [InlineData("swiftbar.hideAbout", "true")]
    [InlineData("swiftbar.hideRunInTerminal", "true")]
    [InlineData("swiftbar.hideLastUpdated", "true")]
    [InlineData("swiftbar.hideDisablePlugin", "true")]
    [InlineData("swiftbar.hideSwiftBar", "true")]
    [InlineData("swiftbar.schedule", "01,16,31,46 * * * *")]
    [InlineData("swiftbar.refreshOnOpen", "true")]
    [InlineData("swiftbar.collapseOnClose", "true")]
    [InlineData("swiftbar.runInBash", "false")]
    [InlineData("swiftbar.type", "streamable")]
    [InlineData("swiftbar.environment", "[VAR1=default]")]
    [InlineData("swiftbar.persistentWebView", "true")]
    [InlineData("SWIFTBAR.SCHEDULE", "* * * * *")]
    [InlineData("xbar.var", "string(VAR_NAME=\"x\"): Your name.")]
    [InlineData("bitbar.var", "number(VAR_COUNT=1): Count.")]
    [InlineData("xbar.droptypes", "public.url")]
    public void ReportsUnsupportedBehaviouralDirectives(string tag, string value)
    {
        PluginMetadataParseResult result = Parse($"# <{tag}>{value}</{tag}>");

        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataUnsupportedDirective, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(PluginMetadata.Empty, result.Metadata);
    }

    [Fact]
    public void ValueAtLengthLimitIsAcceptedAndLongerIsIgnored()
    {
        string longest = new('a', PluginMetadataParser.MaxValueLength);
        string tooLong = new('b', PluginMetadataParser.MaxValueLength + 1);

        PluginMetadataParseResult result = Parse($"# <xbar.title>{longest}</xbar.title>\n# <xbar.desc>{tooLong}</xbar.desc>");

        Assert.Equal(longest, result.Metadata.Title);
        Assert.Null(result.Metadata.Description);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataValueTooLong, diagnostic.Code);
        Assert.Equal(2, diagnostic.Line);
    }

    [Theory]
    [InlineData("\u0001")]
    [InlineData("\u001B[31m")]
    [InlineData("\u007F")]
    [InlineData("\u0085")]
    [InlineData("a\rb")]
    public void ValuesWithControlCharactersAreIgnored(string controlText)
    {
        PluginMetadataParseResult result = Parse($"# <xbar.title>A{controlText}B</xbar.title>");

        Assert.Null(result.Metadata.Title);
        Assert.Equal(PluginDiagnosticCode.MetadataInvalidValue, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void TagLimitStopsScanning()
    {
        var text = new StringBuilder();
        for (int i = 0; i < PluginMetadataParser.MaxTags; i++)
        {
            text.Append("# <xbar.title>Clock</xbar.title>\n");
        }

        text.Append("# <xbar.desc>Too late</xbar.desc>\n");
        PluginMetadataParseResult result = Parse(text.ToString());

        Assert.Equal("Clock", result.Metadata.Title);
        Assert.Null(result.Metadata.Description);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataTagLimitReached, diagnostic.Code);
        Assert.Equal(PluginMetadataParser.MaxTags + 1, diagnostic.Line);
    }

    [Fact]
    public void MalformedTagsCountTowardsTheTagLimit()
    {
        // A single 64 KiB line of unclosed tags: scanning must stay bounded and stop at the limit.
        string line = "#" + string.Concat(Enumerable.Repeat("<xbar.title>", (PluginMetadataParser.MaxScanBytes / 12) - 1));
        PluginMetadataParseResult result = Parse(line);

        Assert.Equal(PluginMetadataParser.MaxTags, result.Diagnostics.Count(d => d.Code == PluginDiagnosticCode.MetadataMalformedTag));
        Assert.Single(result.Diagnostics, d => d.Code == PluginDiagnosticCode.MetadataTagLimitReached);
    }

    [Fact]
    public void OverlongTagNamesAreMalformed()
    {
        string tag = "xbar." + new string('a', PluginMetadataParser.MaxTagNameLength);
        PluginMetadataParseResult result = Parse($"# <{tag}>x</{tag}>");

        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataMalformedTag, diagnostic.Code);
        Assert.True(diagnostic.Message.Length < 300);
    }

    [Fact]
    public void DoesNotResolveXmlEntitiesOrDoctypes()
    {
        PluginMetadataParseResult result = Parse("""
            <?xml version="1.0"?>
            <!DOCTYPE x [ <!ENTITY e SYSTEM "file:///c:/windows/win.ini"> ]>
            # <xbar.title>&e;</xbar.title>
            # <xbar.desc><![CDATA[x]]></xbar.desc>
            """);

        // Values are inert text: no entity expansion, no CDATA handling.
        Assert.Equal("&e;", result.Metadata.Title);
        Assert.Equal("<![CDATA[x]]>", result.Metadata.Description);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SupportsLfAndCrLf(string newLine)
    {
        string text = FullXbarHeader.ReplaceLineEndings(newLine);
        PluginMetadataParseResult result = Parse(text);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Weather", result.Metadata.Title);
        Assert.Equal("https://example.invalid/weather", result.Metadata.AboutUrl);
    }

    [Fact]
    public void ReportsLineNumbersWithCrLf()
    {
        PluginMetadataParseResult result = Parse("line1\r\nline2\r\n# <xbar.foo>x</xbar.foo>\r\n");

        Assert.Equal(3, Assert.Single(result.Diagnostics).Line);
    }

    [Fact]
    public void SupportsUtf8WithAndWithoutBom()
    {
        const string text = "# <xbar.title>Météo 日本 🚀</xbar.title>\r\n";

        PluginMetadataParseResult withoutBom = PluginMetadataParser.Parse(new UTF8Encoding(false).GetBytes(text), PluginLanguage.PowerShell);
        PluginMetadataParseResult withBom = PluginMetadataParser.Parse([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)], PluginLanguage.PowerShell);

        Assert.Equal("Météo 日本 🚀", withoutBom.Metadata.Title);
        Assert.Equal("Météo 日本 🚀", withBom.Metadata.Title);
        Assert.Empty(withoutBom.Diagnostics);
        Assert.Empty(withBom.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportsUtf16WithBom(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        const string text = "# <xbar.title>Windows PowerShell ✓</xbar.title>\r\nWrite-Output 'x'\r\n";

        PluginMetadataParseResult result = PluginMetadataParser.Parse([.. encoding.Preamble, .. encoding.GetBytes(text)], PluginLanguage.PowerShell);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Windows PowerShell ✓", result.Metadata.Title);
    }

    [Fact]
    public void Utf16WithoutBomIsReportedRatherThanMisread()
    {
        PluginMetadataParseResult result = PluginMetadataParser.Parse(Encoding.Unicode.GetBytes("# <xbar.title>X</xbar.title>"), PluginLanguage.PowerShell);

        Assert.Equal(PluginMetadata.Empty, result.Metadata);
        Assert.Equal(PluginDiagnosticCode.MetadataUnsupportedEncoding, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41 })]
    public void Utf32IsUnsupported(byte[] content)
    {
        PluginMetadataParseResult result = PluginMetadataParser.Parse(content, PluginLanguage.PowerShell);

        Assert.Equal(PluginDiagnosticCode.MetadataUnsupportedEncoding, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData(new byte[] { 0x23, 0x20, 0xC3, 0x28 })] // invalid continuation byte
    [InlineData(new byte[] { 0x23, 0xE9, 0x74, 0xE9 })] // Windows-1252 "été"
    [InlineData(new byte[] { 0xC0, 0xAF })] // overlong encoding
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })] // encoded surrogate
    [InlineData(new byte[] { 0x41, 0xE2, 0x82 })] // truncated sequence at end of file
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41 })] // UTF-16 with odd length
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0xD8, 0x41, 0x00 })] // UTF-16 lone high surrogate
    public void InvalidEncodingIsReported(byte[] content)
    {
        PluginMetadataParseResult result = PluginMetadataParser.Parse(content, PluginLanguage.PowerShell);

        Assert.Equal(PluginMetadata.Empty, result.Metadata);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataInvalidEncoding, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void BinaryContentIsReported()
    {
        byte[] content = [0x4D, 0x5A, 0x00, 0x00, 0x03, 0x00];

        Assert.Equal(PluginDiagnosticCode.MetadataUnsupportedEncoding, Assert.Single(PluginMetadataParser.Parse(content, PluginLanguage.PowerShell).Diagnostics).Code);
    }

    [Fact]
    public void ContentBeyondScanLimitIsIgnored()
    {
        string header = "# <xbar.title>Big</xbar.title>\n";
        string filler = new string('x', 100) + "\n";
        var text = new StringBuilder(header);
        while (text.Length < PluginMetadataParser.MaxScanBytes)
        {
            text.Append(filler);
        }

        text.Append("# <xbar.desc>Beyond the limit</xbar.desc>\n");
        PluginMetadataParseResult result = Parse(text.ToString());

        Assert.Equal("Big", result.Metadata.Title);
        Assert.Null(result.Metadata.Description);
        Assert.Equal(PluginDiagnosticCode.MetadataScanTruncated, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void TagCutByTheScanLimitIsNotReportedAsMalformed()
    {
        string prefix = "# <xbar.title>Big</xbar.title>\n";
        string tag = "# <xbar.desc>cut here</xbar.desc>\n";
        string padding = new('x', PluginMetadataParser.MaxScanBytes - prefix.Length - 1 - 10);
        byte[] content = Encoding.UTF8.GetBytes(prefix + padding + "\n" + tag);

        PluginMetadataParseResult result = PluginMetadataParser.Parse(content, PluginLanguage.PowerShell);

        Assert.Equal("Big", result.Metadata.Title);
        Assert.Null(result.Metadata.Description);
        Assert.Equal(PluginDiagnosticCode.MetadataScanTruncated, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void MultibyteCharacterCutByTruncationIsNotAnEncodingError()
    {
        byte[] line = Encoding.UTF8.GetBytes("# <xbar.title>Ok</xbar.title>\n");
        byte[] content = [.. line, 0xE2, 0x82];

        PluginMetadataParseResult result = PluginMetadataParser.Parse(content, PluginLanguage.PowerShell, isTruncated: true);

        Assert.Equal("Ok", result.Metadata.Title);
        Assert.Equal(PluginDiagnosticCode.MetadataScanTruncated, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void ArbitraryBytesNeverThrow()
    {
        var random = new Random(4242);
        byte[][] fragments =
        [
            Encoding.UTF8.GetBytes("<xbar.title>"), Encoding.UTF8.GetBytes("</xbar.title>"), Encoding.UTF8.GetBytes("<swiftbar."),
            Encoding.UTF8.GetBytes("\r\n"), Encoding.UTF8.GetBytes("\n"), Encoding.UTF8.GetBytes("<"), Encoding.UTF8.GetBytes(">"),
            Encoding.UTF8.GetBytes("é"), [0xEF, 0xBB, 0xBF], [0xFF, 0xFE], [0xFE, 0xFF], [0x00], [0xC3], [0x80], [0xFF],
            Encoding.UTF8.GetBytes("text"),
        ];

        for (int i = 0; i < 5_000; i++)
        {
            var buffer = new List<byte>();
            int parts = random.Next(0, 40);
            for (int j = 0; j < parts; j++)
            {
                buffer.AddRange(fragments[random.Next(fragments.Length)]);
            }

            PluginMetadataParseResult result = PluginMetadataParser.Parse(buffer.ToArray(), (PluginLanguage)random.Next(5), isTruncated: random.Next(2) == 0);
            Assert.NotNull(result.Metadata);
            Assert.False(result.Diagnostics.IsDefault);
        }
    }

    [Theory]
    [InlineData("# <xbar.title>X</bitbar.title>")]
    [InlineData("# <xbar.title>X</swiftbar.title>")]
    [InlineData("# <traywisp.title>X</xbar.title>")]
    [InlineData("# <xbar.author>X</xbar.author.github>")]
    [InlineData("# <xbar.author.github>X</xbar.author>")]
    public void ClosingTagMustMatchNamespaceAndField(string line)
    {
        PluginMetadataParseResult result = Parse(line);

        Assert.Equal(PluginMetadata.Empty, result.Metadata);
        Assert.Equal(PluginDiagnosticCode.MetadataMalformedTag, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16CharacterCutByTruncationIsNotAnEncodingError(bool splitSurrogatePair)
    {
        byte[] line = [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes("# <xbar.title>Ok</xbar.title>\n")];
        byte[] tail = splitSurrogatePair
            ? [0x3D, 0xD8] // U+D83D, the first half of a surrogate pair (UTF-16 LE)
            : [0x41]; // the first byte of a code unit
        PluginMetadataParseResult result = PluginMetadataParser.Parse([.. line, .. tail], PluginLanguage.PowerShell, isTruncated: true);

        Assert.Equal("Ok", result.Metadata.Title);
        Assert.Equal(PluginDiagnosticCode.MetadataScanTruncated, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Utf16LoneSurrogateAtTheEndOfAWholeFileIsAnEncodingError()
    {
        // Raw bytes: Encoding.Unicode.GetBytes would replace a lone surrogate with U+FFFD.
        byte[] content = [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes("# x\n"), 0x3D, 0xD8];

        Assert.Equal(PluginDiagnosticCode.MetadataInvalidEncoding, Assert.Single(PluginMetadataParser.Parse(content, PluginLanguage.PowerShell).Diagnostics).Code);
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<x")]
    [InlineData("<xbar.")]
    [InlineData("</xbar.title>")]
    [InlineData("<xbar.title><")]
    public void AdversarialMarkupStaysBoundedAndDiagnosticsStaySmall(string fragment)
    {
        // A full 64 KiB comment made of one fragment repeated. The parser must finish with a bounded
        // number of short diagnostics.
        string line = "#" + string.Concat(Enumerable.Repeat(fragment, (PluginMetadataParser.MaxScanBytes - 2) / fragment.Length));
        string text = line + "\n" + line;

        PluginMetadataParseResult result = Parse(text[..PluginMetadataParser.MaxScanBytes]);

        Assert.True(result.Diagnostics.Length <= PluginMetadataParser.MaxTags + 2, result.Diagnostics.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.All(result.Diagnostics, diagnostic => Assert.True(diagnostic.Message.Length < 300, diagnostic.Message));
    }

    [Fact]
    public void DiagnosticsForManyFieldsStayBounded()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"# <xbar.title>Title {i}</xbar.title><xbar.unknown{i}>x</xbar.unknown{i}>\n");
        }

        PluginMetadataParseResult result = Parse(text.ToString());

        Assert.Equal("Title 0", result.Metadata.Title);
        Assert.True(result.Diagnostics.Length <= (2 * PluginMetadataParser.MaxTags) + 2);
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == PluginDiagnosticCode.MetadataTagLimitReached);
    }

    private static PluginMetadataParseResult Parse(string text, PluginLanguage language = PluginLanguage.PowerShell) =>
        PluginMetadataParser.Parse(Encoding.UTF8.GetBytes(text), language);
}
