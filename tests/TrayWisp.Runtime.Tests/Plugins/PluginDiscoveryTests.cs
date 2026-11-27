using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using TrayWisp.Core.Plugins;
using TrayWisp.Runtime.Plugins;
using Xunit;

namespace TrayWisp.Runtime.Tests.Plugins;

public sealed class PluginDiscoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DiscoversPluginsRecursivelyInDeterministicOrder()
    {
        using var root = new TemporaryDirectory();
        // Created in an order unrelated to the expected result order.
        root.WriteFile("zeta.ps1");
        root.WriteFile("work/github.notifications.5m.py");
        root.WriteFile("Alpha.10s.js");
        root.WriteFile("work/deep/er/disk.1h.ps1");
        root.WriteFile("beta.sh");
        root.WriteFile("Work2/clock.cmd");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        string[] expected =
        [
            "Alpha.10s.js",
            "beta.sh",
            "work/deep/er/disk.1h.ps1",
            "work/github.notifications.5m.py",
            "Work2/clock.cmd",
            "zeta.ps1",
        ];
        Assert.Equal(expected, result.Plugins.Select(plugin => plugin.Id.Value));
        Assert.Empty(result.Diagnostics);
        Assert.True(result.IsComplete);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path)), result.RootPath);
    }

    [Fact]
    public void RepeatedDiscoveryGivesIdenticalResults()
    {
        using var root = new TemporaryDirectory();
        for (int i = 0; i < 30; i++)
        {
            root.WriteFile($"d{i % 4}/p{i}.{(i % 3) + 1}m.ps1", $"# <xbar.title>P{i}</xbar.title>\n# <xbar.foo>x</xbar.foo>\n");
            root.WriteFile($"d{i % 4}/bad{i}.0s.py");
        }

        string first = Describe(PluginDiscovery.Discover(root.Path, cancellationToken: Token));
        string second = Describe(PluginDiscovery.Discover(root.Path + Path.DirectorySeparatorChar, cancellationToken: Token));

        Assert.Equal(first, second);
    }

    [Fact]
    public void ParsesFileNamesAndMetadata()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("weather.10m.py", "#!/usr/bin/env python3\n# <xbar.title>Weather</xbar.title>\n# <xbar.version>v1.0</xbar.version>\n");
        root.WriteFile("clock.ps1", "Get-Date\n");
        root.WriteFile("legacy.5s.sh", "#!/bin/sh\n");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        PluginDescriptor clock = result.Plugins.Single(plugin => plugin.Id.Value == "clock.ps1");
        Assert.Equal("clock", clock.DisplayTitle);
        Assert.True(clock.FileName.IsManualRefresh);
        Assert.Equal(PluginExecutionSupport.Planned, clock.FileName.ExecutionSupport);

        PluginDescriptor weather = result.Plugins.Single(plugin => plugin.Id.Value == "weather.10m.py");
        Assert.Equal("Weather", weather.DisplayTitle);
        Assert.Equal("v1.0", weather.Metadata.Version);
        Assert.Equal(TimeSpan.FromMinutes(10), weather.FileName.RefreshInterval?.Duration);

        PluginDescriptor legacy = result.Plugins.Single(plugin => plugin.Id.Value == "legacy.5s.sh");
        Assert.Equal(PluginLanguage.Bash, legacy.FileName.Language);
        Assert.Equal(PluginExecutionSupport.Deferred, legacy.FileName.ExecutionSupport);
    }

    [Fact]
    public void MetadataDiagnosticsCarryThePluginPath()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("sub/clock.5s.ps1", "# <swiftbar.schedule>* * * * *</swiftbar.schedule>\n");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        PluginDescriptor plugin = Assert.Single(result.Plugins);
        PluginDiagnostic diagnostic = Assert.Single(plugin.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataUnsupportedDirective, diagnostic.Code);
        Assert.Equal("sub/clock.5s.ps1", diagnostic.Path);
        Assert.Equal(1, diagnostic.Line);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void DistinguishesIdenticalFileNamesInDifferentDirectories()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("clock.5s.ps1", "# <xbar.title>Root</xbar.title>");
        root.WriteFile("home/clock.5s.ps1", "# <xbar.title>Home</xbar.title>");
        root.WriteFile("work/clock.5s.ps1", "# <xbar.title>Work</xbar.title>");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Equal(3, result.Plugins.Length);
        Assert.Equal(3, result.Plugins.Select(plugin => plugin.Id).Distinct().Count());
        string[] titles = ["Root", "Home", "Work"];
        Assert.Equal(titles, result.Plugins.Select(plugin => plugin.DisplayTitle));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == PluginDiagnosticCode.IdentityCollision);
    }

    [Fact]
    public void IgnoresUnrelatedDotPrefixedAndHiddenEntries()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("visible.ps1");
        root.WriteFile("readme.md");
        root.WriteFile("tool.exe", "MZ");
        root.WriteFile("tool.5s.exe", "MZ");
        root.WriteFile("lib.psm1");
        root.WriteFile("notes.txt");
        root.WriteFile(".secret.5s.ps1");
        root.WriteFile(".git/hooks/pre-commit.sh");
        root.WriteFile(".hidden-dir/inside.ps1");
        root.WriteFile("ok/.dotfile.py");
        string hiddenDirectory = root.CreateDirectory("attr-hidden");
        root.WriteFile("attr-hidden/inside.ps1");
        string hiddenFile = root.WriteFile("attr-hidden-file.ps1");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hiddenDirectory, File.GetAttributes(hiddenDirectory) | FileAttributes.Hidden);
            File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
        }

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        string[] expected = OperatingSystem.IsWindows()
            ? ["visible.ps1"]
            : ["attr-hidden-file.ps1", "attr-hidden/inside.ps1", "visible.ps1"];
        Assert.Equal(expected, result.Plugins.Select(plugin => plugin.Id.Value));
        Assert.Empty(result.Diagnostics);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void RejectedFileNamesAreReportedWithoutBlockingHealthyPlugins()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("a-healthy.5s.ps1");
        root.WriteFile("clock.0s.ps1");
        root.WriteFile("clock.-1s.ps1");
        root.WriteFile("clock.1.5s.ps1");
        root.WriteFile("sub/clock.999999999999h.ps1");
        root.WriteFile("sub/z-healthy.py");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        string[] plugins = ["a-healthy.5s.ps1", "sub/z-healthy.py"];
        Assert.Equal(plugins, result.Plugins.Select(plugin => plugin.Id.Value));

        (string?, PluginDiagnosticCode)[] expected =
        [
            ("clock.-1s.ps1", PluginDiagnosticCode.NegativeInterval),
            ("clock.0s.ps1", PluginDiagnosticCode.ZeroInterval),
            ("clock.1.5s.ps1", PluginDiagnosticCode.MalformedInterval),
            ("sub/clock.999999999999h.ps1", PluginDiagnosticCode.IntervalOutOfRange),
        ];
        Assert.Equal(expected, result.Diagnostics.Select(diagnostic => (diagnostic.Path, diagnostic.Code)));
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity));
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void MalformedMetadataDoesNotPreventDiscovery()
    {
        using var root = new TemporaryDirectory();
        root.WriteBytes("binary.ps1", [0x4D, 0x5A, 0x90, 0x00, 0xFF, 0xFF]);
        root.WriteBytes("latin1.py", [0x23, 0x20, 0xE9, 0x74, 0xE9, 0x0A]);
        root.WriteFile("unclosed.js", "// <xbar.title>Oops\n");
        root.WriteFile("empty.ps1");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Equal(4, result.Plugins.Length);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.MetadataInvalidEncoding, Assert.Single(result.Plugins.Single(p => p.Id.Value == "binary.ps1").Diagnostics).Code);
        Assert.Equal(PluginDiagnosticCode.MetadataInvalidEncoding, Assert.Single(result.Plugins.Single(p => p.Id.Value == "latin1.py").Diagnostics).Code);
        Assert.Equal(PluginDiagnosticCode.MetadataMalformedTag, Assert.Single(result.Plugins.Single(p => p.Id.Value == "unclosed.js").Diagnostics).Code);
        Assert.Empty(result.Plugins.Single(p => p.Id.Value == "empty.ps1").Diagnostics);
        Assert.All(result.Plugins, plugin => Assert.Equal(plugin.FileName.Name, plugin.DisplayTitle));
    }

    [Fact]
    public void ReadsOnlyABoundedPrefixOfLargeFiles()
    {
        using var root = new TemporaryDirectory();
        var content = new StringBuilder("# <xbar.title>Large</xbar.title>\n");
        string filler = new string('#', 1023) + "\n";
        while (content.Length < 4 * 1024 * 1024)
        {
            content.Append(filler);
        }

        content.Append("# <xbar.desc>At the end</xbar.desc>\n");
        root.WriteFile("large.ps1", content.ToString());

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        PluginDescriptor plugin = Assert.Single(result.Plugins);
        Assert.Equal("Large", plugin.Metadata.Title);
        Assert.Null(plugin.Metadata.Description);
        Assert.Equal(PluginDiagnosticCode.MetadataScanTruncated, Assert.Single(plugin.Diagnostics).Code);
    }

    [Fact]
    public void FileOfExactlyTheScanLimitIsNotTruncated()
    {
        using var root = new TemporaryDirectory();
        string header = "# <xbar.title>Exact</xbar.title>\n";
        root.WriteFile("exact.ps1", header + new string('x', PluginMetadataParser.MaxScanBytes - header.Length));

        PluginDescriptor plugin = Assert.Single(PluginDiscovery.Discover(root.Path, cancellationToken: Token).Plugins);

        Assert.Equal("Exact", plugin.Metadata.Title);
        Assert.Empty(plugin.Diagnostics);
    }

    [Fact]
    public void MissingRootIsReported()
    {
        using var root = new TemporaryDirectory();
        string missing = Path.Combine(root.Path, "does-not-exist");

        PluginDiscoveryResult result = PluginDiscovery.Discover(missing, cancellationToken: Token);

        Assert.Empty(result.Plugins);
        Assert.False(result.IsComplete);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.RootNotFound, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Null(diagnostic.Path);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void RootWhoseParentIsMissingIsReported()
    {
        using var root = new TemporaryDirectory();

        PluginDiscoveryResult result = PluginDiscovery.Discover(Path.Combine(root.Path, "a", "b", "c"), cancellationToken: Token);

        Assert.Equal(PluginDiagnosticCode.RootNotFound, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void RootThatIsAFileIsReported()
    {
        using var root = new TemporaryDirectory();
        string file = root.WriteFile("clock.ps1");

        PluginDiscoveryResult result = PluginDiscovery.Discover(file, cancellationToken: Token);

        Assert.Empty(result.Plugins);
        Assert.Equal(PluginDiagnosticCode.RootNotDirectory, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plugins")]
    [InlineData("./plugins")]
    [InlineData("..\\plugins")]
    public void RootMustBeFullyQualified(string rootPath) =>
        Assert.ThrowsAny<ArgumentException>(() => PluginDiscovery.Discover(rootPath, cancellationToken: Token));

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        using var root = new TemporaryDirectory();

        Assert.Throws<ArgumentOutOfRangeException>(() => PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxDepth = -1 }, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxEntries = 0 }, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxCandidates = 0 }, Token));
    }

    [Fact]
    public void DepthLimitStopsDescending()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("level0.ps1");
        root.WriteFile("a/level1.ps1");
        root.WriteFile("a/b/level2.ps1");
        root.WriteFile("a/b/c/level3.ps1");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxDepth = 2 }, Token);

        string[] expected = ["a/b/level2.ps1", "a/level1.ps1", "level0.ps1"];
        Assert.Equal(expected, result.Plugins.Select(plugin => plugin.Id.Value));
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.DepthLimitReached, diagnostic.Code);
        Assert.Equal("a/b/c", diagnostic.Path);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void DepthZeroScansOnlyTheRoot()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("top.ps1");
        root.WriteFile("sub/nested.ps1");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxDepth = 0 }, Token);

        Assert.Equal("top.ps1", Assert.Single(result.Plugins).Id.Value);
        Assert.Equal(PluginDiagnosticCode.DepthLimitReached, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void DefaultDepthReachesEightLevels()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("1/2/3/4/5/6/7/8/deep.ps1");
        root.WriteFile("1/2/3/4/5/6/7/8/9/too-deep.ps1");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Equal("1/2/3/4/5/6/7/8/deep.ps1", Assert.Single(result.Plugins).Id.Value);
        Assert.Equal("1/2/3/4/5/6/7/8/9", Assert.Single(result.Diagnostics).Path);
    }

    [Fact]
    public void EntryLimitStopsDiscovery()
    {
        using var root = new TemporaryDirectory();
        for (int i = 0; i < 10; i++)
        {
            root.WriteFile($"p{i}.ps1");
        }

        PluginDiscoveryResult result = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = 4 }, out List<(string? Directory, string Name)> enumerated);

        // The root listing does not fit in the budget. An entry that was not seen could be a case
        // variant of one that was, so none of the root's entries is used. (Previously the four
        // entries that happened to be enumerated first were returned as plugins.)
        Assert.Empty(result.Plugins);
        Assert.Equal(4, result.EntriesInspected);
        Assert.Equal(5, enumerated.Count); // four counted entries and the look-ahead
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.EntryLimitReached, diagnostic.Code);
        Assert.Null(diagnostic.Path);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void EntryLimitCountsIgnoredEntriesAndDirectories()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("a.txt");
        root.WriteFile("b/c.ps1");
        root.WriteFile("d.ps1");

        // Entries: a.txt, b, d.ps1 in the root, and c.ps1 in b.
        PluginDiscoveryResult exact = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = 4 }, out var exactEnumerated);
        PluginDiscoveryResult short1 = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = 3 }, out var shortEnumerated);

        Assert.True(exact.IsComplete);
        Assert.Equal(2, exact.Plugins.Length);
        Assert.Equal(4, exact.EntriesInspected);
        Assert.Equal(4, exactEnumerated.Count);

        // The root listing (3 entries) uses the whole budget, so b cannot be listed: its look-ahead
        // finds c.ps1. b contributes nothing, but d.ps1 comes from the complete root listing and is
        // kept. (Previously b/c.ps1 was returned and d.ps1 was dropped, because entries were only
        // counted when processed.)
        Assert.False(short1.IsComplete);
        Assert.Equal("d.ps1", Assert.Single(short1.Plugins).Id.Value);
        Assert.Equal(3, short1.EntriesInspected);
        (string?, string)[] expected = [(null, "a.txt"), (null, "b"), (null, "d.ps1"), ("b", "c.ps1")];
        Assert.Equal(expected, shortEnumerated.OrderBy(entry => entry.Directory, StringComparer.Ordinal).ThenBy(entry => entry.Name, StringComparer.Ordinal));
        PluginDiagnostic diagnostic = Assert.Single(short1.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.EntryLimitReached, diagnostic.Code);
        Assert.Equal("b", diagnostic.Path);
    }

    [Fact]
    public void EntryBudgetCoversEntriesHeldForLaterProcessing()
    {
        // Three sibling directories of four files each. Before the fix, each child listing got the
        // budget minus the entries *processed* so far, while the root's unprocessed entries were held
        // in memory, so all 15 entries were enumerated against a budget of 6.
        using var root = new TemporaryDirectory();
        for (int d = 0; d < 3; d++)
        {
            for (int f = 0; f < 4; f++)
            {
                root.WriteFile($"d{d}/p{f}.ps1");
            }
        }

        PluginDiscoveryResult result = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = 6 }, out var enumerated);

        // Root: 3 entries. d0: 3 entries fit, the fourth is the look-ahead. d1 and d2 are not listed.
        Assert.Equal(6, result.EntriesInspected);
        Assert.Equal(7, enumerated.Count);
        Assert.Equal(3, enumerated.Count(entry => entry.Directory is null));
        Assert.Equal(4, enumerated.Count(entry => entry.Directory == "d0"));
        Assert.DoesNotContain(enumerated, entry => entry.Directory is "d1" or "d2");
        Assert.Empty(result.Plugins);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.EntryLimitReached, diagnostic.Code);
        Assert.Equal("d0", diagnostic.Path);
        Assert.False(result.IsComplete);
    }

    [Theory]
    [InlineData(8, true, 8, 8, new[] { "a.ps1", "d1/b.ps1", "d1/d2/c.ps1", "d1/d2/d3/e.ps1", "d1/d2/d3/f.ps1" }, null)]
    [InlineData(7, false, 7, 8, new[] { "a.ps1", "d1/b.ps1", "d1/d2/c.ps1" }, "d1/d2/d3")]
    [InlineData(6, false, 6, 7, new[] { "a.ps1", "d1/b.ps1", "d1/d2/c.ps1" }, "d1/d2/d3")]
    [InlineData(5, false, 5, 6, new[] { "a.ps1", "d1/b.ps1" }, "d1/d2")]
    [InlineData(1, false, 1, 2, new string[0], null)]
    [InlineData(int.MaxValue, true, 8, 8, new[] { "a.ps1", "d1/b.ps1", "d1/d2/c.ps1", "d1/d2/d3/e.ps1", "d1/d2/d3/f.ps1" }, null)]
    public void EntryBudgetIsGlobalAcrossNestedDirectories(int maxEntries, bool complete, int inspected, int enumeratedCount, string[] plugins, string? truncatedDirectory)
    {
        // Entries: root {a.ps1, d1}, d1 {b.ps1, d2}, d2 {c.ps1, d3}, d3 {e.ps1, f.ps1}: 8 in total.
        using var root = new TemporaryDirectory();
        root.WriteFile("a.ps1");
        root.WriteFile("d1/b.ps1");
        root.WriteFile("d1/d2/c.ps1");
        root.WriteFile("d1/d2/d3/e.ps1");
        root.WriteFile("d1/d2/d3/f.ps1");

        PluginDiscoveryResult result = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = maxEntries }, out var enumerated);

        Assert.Equal(complete, result.IsComplete);
        Assert.Equal(inspected, result.EntriesInspected);
        Assert.Equal(enumeratedCount, enumerated.Count);
        Assert.True(enumerated.Count <= (long)maxEntries + 1);
        Assert.Equal(plugins, result.Plugins.Select(plugin => plugin.Id.Value));
        if (complete)
        {
            Assert.Empty(result.Diagnostics);
        }
        else
        {
            PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(PluginDiagnosticCode.EntryLimitReached, diagnostic.Code);
            Assert.Equal(truncatedDirectory, diagnostic.Path);
        }
    }

    [Fact]
    public void EntryBudgetUsedUpExactlyStillListsEmptyDirectories()
    {
        // The budget is exactly used up by the root listing. An empty directory needs no budget, so
        // its look-ahead finds nothing and the scan is complete.
        using var root = new TemporaryDirectory();
        root.WriteFile("a.ps1");
        root.CreateDirectory("empty");

        PluginDiscoveryResult result = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = 2 }, out var enumerated);

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.EntriesInspected);
        Assert.Equal(2, enumerated.Count);
        Assert.Equal("a.ps1", Assert.Single(result.Plugins).Id.Value);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void TruncatedListingKeepsResultsFromCompleteListings()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("a.ps1");
        root.WriteFile("z.ps1");
        for (int i = 0; i < 5; i++)
        {
            root.WriteFile($"sub/p{i}.ps1");
        }

        // Root: a.ps1, sub, z.ps1 (3). sub: 2 fit, the third is the look-ahead.
        PluginDiscoveryResult result = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = 5 }, out var enumerated);

        // Every entry of sub is withheld, whichever two were enumerated, but the root listing was
        // complete, so z.ps1, processed after sub, is still returned.
        string[] expected = ["a.ps1", "z.ps1"];
        Assert.Equal(expected, result.Plugins.Select(plugin => plugin.Id.Value));
        Assert.Equal(5, result.EntriesInspected);
        Assert.Equal(6, enumerated.Count);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.EntryLimitReached, diagnostic.Code);
        Assert.Equal("sub", diagnostic.Path);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void TruncatedListingCannotHideAFileCollision()
    {
        // Before the fix, if the enumeration returned other.ps1 and one clock variant within the
        // budget, that variant was returned although its twin exists. Whichever entries the file
        // system returns first, nothing from the truncated listing may be used.
        using var root = new TemporaryDirectory();
        string directory = root.CreateDirectory("mixed");
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(directory), "Case-sensitive directories are unavailable.");
        root.WriteFile("a.ps1");
        root.WriteFile("mixed/clock.ps1");
        root.WriteFile("mixed/CLOCK.ps1");
        root.WriteFile("mixed/other.ps1");

        // Root: a.ps1, mixed (2). mixed: 2 of its 3 entries fit.
        PluginDiscoveryResult result = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = 4 }, out var enumerated);

        Assert.Equal("a.ps1", Assert.Single(result.Plugins).Id.Value);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.EntryLimitReached, diagnostic.Code);
        Assert.Equal("mixed", diagnostic.Path);
        Assert.False(result.IsComplete);
        Assert.Equal(4, result.EntriesInspected);
        Assert.Equal(3, enumerated.Count(entry => entry.Directory == "mixed"));
    }

    [Fact]
    public void TruncatedListingCannotHideADirectoryCollision()
    {
        using var root = new TemporaryDirectory();
        string directory = root.CreateDirectory("top");
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(directory), "Case-sensitive directories are unavailable.");
        root.WriteFile("top/Work/x.ps1");
        root.WriteFile("top/work/y.ps1");
        root.WriteFile("top/z.ps1");

        // Root: top (1). top: 2 of its 3 entries fit, so one of the twins may be among them.
        PluginDiscoveryResult result = DiscoverObserved(root, new PluginDiscoveryOptions { MaxEntries = 3 }, out var enumerated);

        Assert.Empty(result.Plugins);
        Assert.Equal("top", Assert.Single(result.Diagnostics).Path);
        Assert.False(result.IsComplete);

        // Neither twin was traversed.
        Assert.DoesNotContain(enumerated, entry => entry.Directory is "top/Work" or "top/work");
    }

    [Fact]
    public void CandidateLimitStopsDiscovery()
    {
        using var root = new TemporaryDirectory();
        for (int i = 0; i < 6; i++)
        {
            root.WriteFile($"p{i}.ps1");
            root.WriteFile($"p{i}.txt");
        }

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxCandidates = 3 }, Token);

        Assert.Equal(3, result.Plugins.Length);
        Assert.Equal(PluginDiagnosticCode.CandidateLimitReached, Assert.Single(result.Diagnostics).Code);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void CancelledTokenStopsDiscovery()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("clock.ps1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PluginDiscovery.Discover(root.Path, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void JunctionsAreNotFollowed()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        outside.WriteFile("outside.5s.ps1");
        root.WriteFile("inside.ps1");
        Assert.SkipUnless(
            TemporaryDirectory.TryCreateJunction(root.Combine("junction"), outside.Path),
            "Directory junctions cannot be created on this platform.");
        Assert.SkipUnless(
            TemporaryDirectory.TryCreateJunction(root.Combine("loop"), root.Path),
            "Directory junctions cannot be created on this platform.");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Equal("inside.ps1", Assert.Single(result.Plugins).Id.Value);
        string?[] skipped = ["junction", "loop"];
        Assert.Equal(skipped, result.Diagnostics.Select(diagnostic => diagnostic.Path));
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(PluginDiagnosticCode.ReparsePointSkipped, diagnostic.Code));
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void SymbolicLinksAreNotFollowed()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        string outsideFile = outside.WriteFile("target.ps1", "# <xbar.title>Outside</xbar.title>");
        root.WriteFile("inside.ps1");
        Assert.SkipUnless(
            TemporaryDirectory.TryCreateDirectorySymbolicLink(root.Combine("dirlink"), outside.Path)
            && TemporaryDirectory.TryCreateFileSymbolicLink(root.Combine("filelink.5s.ps1"), outsideFile),
            "Symbolic links cannot be created here (on Windows they need Developer Mode or elevation).");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Equal("inside.ps1", Assert.Single(result.Plugins).Id.Value);
        string?[] skipped = ["dirlink", "filelink.5s.ps1"];
        Assert.Equal(skipped, result.Diagnostics.Select(diagnostic => diagnostic.Path));
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(PluginDiagnosticCode.ReparsePointSkipped, diagnostic.Code));
    }

    [Fact]
    public void RootThatIsAJunctionIsNotScanned()
    {
        using var root = new TemporaryDirectory();
        string target = root.CreateDirectory("target");
        root.WriteFile("target/clock.ps1");
        string link = root.Combine("link");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(link, target), "Directory junctions cannot be created on this platform.");

        PluginDiscoveryResult result = PluginDiscovery.Discover(link, cancellationToken: Token);

        Assert.Empty(result.Plugins);
        Assert.Equal(PluginDiagnosticCode.RootIsReparsePoint, Assert.Single(result.Diagnostics).Code);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void RootThatIsASymbolicLinkIsNotScanned()
    {
        using var root = new TemporaryDirectory();
        string target = root.CreateDirectory("target");
        root.WriteFile("target/clock.ps1");
        string link = root.Combine("link");
        Assert.SkipUnless(TemporaryDirectory.TryCreateDirectorySymbolicLink(link, target), "Symbolic links cannot be created here.");

        PluginDiscoveryResult result = PluginDiscovery.Discover(link + Path.DirectorySeparatorChar, cancellationToken: Token);

        Assert.Empty(result.Plugins);
        Assert.Equal(PluginDiagnosticCode.RootIsReparsePoint, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void IdentityCollisionsExcludeEveryCollidingFile()
    {
        using var root = new TemporaryDirectory();
        string directory = root.CreateDirectory("mixed");
        Assert.SkipUnless(
            TemporaryDirectory.TryMakeCaseSensitive(directory),
            "Case-sensitive directories are unavailable (on Windows this needs the Windows Subsystem for Linux component).");
        root.WriteFile("mixed/clock.ps1", "# <xbar.title>lower</xbar.title>");
        root.WriteFile("mixed/CLOCK.ps1", "# <xbar.title>upper</xbar.title>");
        root.WriteFile("mixed/Clock.PS1", "# <xbar.title>mixed</xbar.title>");
        root.WriteFile("mixed/other.ps1");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Equal("mixed/other.ps1", Assert.Single(result.Plugins).Id.Value);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.IdentityCollision, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("mixed/CLOCK.ps1", diagnostic.Path);
        Assert.Contains("3 files", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("'CLOCK.ps1', 'Clock.PS1', 'clock.ps1'", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void CollisionIsDetectedEvenWhenOneCollidingFileIsUnreadable()
    {
        // Before the fix, the unreadable file was dropped before collision detection, so the readable
        // one was silently chosen.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Mandatory file locks are a Windows feature.");
        using var root = new TemporaryDirectory();
        string directory = root.CreateDirectory("mixed");
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(directory), "Case-sensitive directories are unavailable.");
        root.WriteFile("mixed/clock.ps1");
        string locked = root.WriteFile("mixed/CLOCK.ps1");

        PluginDiscoveryResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);
        }

        Assert.Empty(result.Plugins);
        Assert.Equal(PluginDiagnosticCode.IdentityCollision, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void CollisionIsDetectedEvenWhenOneCollidingFileHasBadMetadata()
    {
        using var root = new TemporaryDirectory();
        string directory = root.CreateDirectory("mixed");
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(directory), "Case-sensitive directories are unavailable.");
        root.WriteFile("mixed/clock.ps1", "# <xbar.title>Good</xbar.title>");
        root.WriteBytes("mixed/CLOCK.ps1", [0xFF, 0x00, 0xC3]);

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Empty(result.Plugins);
        Assert.Equal(PluginDiagnosticCode.IdentityCollision, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void DirectoriesThatDifferOnlyByCaseAreBothExcluded()
    {
        // Paths below such directories could collide, and one subtree being unreadable must not hide that.
        using var root = new TemporaryDirectory();
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(root.Path), "Case-sensitive directories are unavailable.");
        root.WriteFile("Work/x.ps1");
        root.WriteFile("work/y.ps1");
        root.WriteFile("work.ps1");
        root.WriteFile("other/z.ps1");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        string[] expected = ["other/z.ps1", "work.ps1"];
        Assert.Equal(expected, result.Plugins.Select(plugin => plugin.Id.Value));
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.IdentityCollision, diagnostic.Code);
        Assert.Equal("Work", diagnostic.Path);
        Assert.Contains("2 directories", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindCollisionsGroupsCaseVariantsOfTheSameKind()
    {
        // A sorted listing (case-insensitive, then ordinal), as discovery produces it.
        string[] names = ["a.ps1", "B", "b", "b.ps1", "CLOCK.ps1", "Clock.ps1", "clock.ps1", "readme.md", "README.md", "x", "X.ps1"];
        PluginDiscovery.EntryKind[] kinds =
        [
            PluginDiscovery.EntryKind.Candidate,
            PluginDiscovery.EntryKind.Directory,
            PluginDiscovery.EntryKind.Directory,
            PluginDiscovery.EntryKind.Candidate,
            PluginDiscovery.EntryKind.Candidate,
            PluginDiscovery.EntryKind.Candidate,
            PluginDiscovery.EntryKind.Candidate,
            PluginDiscovery.EntryKind.NotCandidate,
            PluginDiscovery.EntryKind.NotCandidate,
            PluginDiscovery.EntryKind.Directory,
            PluginDiscovery.EntryKind.Candidate,
        ];

        int[] groups = PluginDiscovery.FindCollisions(names, kinds);

        // "B"/"b" directories collide; the three clock files collide; README files are not candidates;
        // the directory "x" and the file "X.ps1" have different names.
        int[] expected = [-1, 1, 1, -1, 4, 4, 4, -1, -1, -1, -1];
        Assert.Equal(expected, groups);
    }

    [Fact]
    public void FindCollisionsIgnoresADirectoryAndAFileWithTheSameName()
    {
        string[] names = ["Clock.ps1", "clock.ps1"];
        PluginDiscovery.EntryKind[] kinds = [PluginDiscovery.EntryKind.Directory, PluginDiscovery.EntryKind.Candidate];

        int[] expected = [-1, -1];
        Assert.Equal(expected, PluginDiscovery.FindCollisions(names, kinds));
    }

    [Fact]
    public void LockedFileIsReportedAndOtherPluginsAreDiscovered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Mandatory file locks are a Windows feature.");
        using var root = new TemporaryDirectory();
        string locked = root.WriteFile("locked.ps1", "# <xbar.title>Locked</xbar.title>");
        root.WriteFile("open.ps1");

        PluginDiscoveryResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);
        }

        Assert.Equal("open.ps1", Assert.Single(result.Plugins).Id.Value);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.FileUnreadable, diagnostic.Code);
        Assert.Equal("locked.ps1", diagnostic.Path);

        // The locked plugin still exists, so its absence must not look like a removal.
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void UnlistableDirectoryIsReportedAndOtherPluginsAreDiscovered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test uses Windows access control lists.");
        using var root = new TemporaryDirectory();
        root.WriteFile("a-open.ps1");
        root.WriteFile("denied/inside.ps1");
        root.WriteFile("z-open/inside.ps1");
        DenyListing(root, root.Combine("denied"));

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        string[] expected = ["a-open.ps1", "z-open/inside.ps1"];
        Assert.Equal(expected, result.Plugins.Select(plugin => plugin.Id.Value));
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.DirectoryUnreadable, diagnostic.Code);
        Assert.Equal("denied", diagnostic.Path);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void UnlistableRootIsAnError()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test uses Windows access control lists.");
        using var root = new TemporaryDirectory();
        string pluginRoot = root.CreateDirectory("plugins");
        root.WriteFile("plugins/clock.ps1");
        DenyListing(root, pluginRoot);

        PluginDiscoveryResult result = PluginDiscovery.Discover(pluginRoot, cancellationToken: Token);

        Assert.Empty(result.Plugins);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.DirectoryUnreadable, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Null(diagnostic.Path);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void UnreadableFileIsReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test uses Windows access control lists.");
        using var root = new TemporaryDirectory();
        string denied = root.WriteFile("denied.ps1");
        root.WriteFile("open.ps1");
        DenyReading(root, denied);

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Equal("open.ps1", Assert.Single(result.Plugins).Id.Value);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.FileUnreadable, diagnostic.Code);
        Assert.Contains("denied", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void UnboundedEntryLimitDoesNotTruncateListings()
    {
        // Before the fix, MaxEntries = int.MaxValue overflowed the listing budget: one entry per
        // directory was read and the result still claimed to be complete.
        using var root = new TemporaryDirectory();
        for (int i = 0; i < 5; i++)
        {
            root.WriteFile($"p{i}.ps1");
            root.WriteFile($"sub/q{i}.ps1");
        }

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxEntries = int.MaxValue, MaxCandidates = int.MaxValue }, Token);

        Assert.Equal(10, result.Plugins.Length);
        Assert.True(result.IsComplete);
        Assert.Equal(11, result.EntriesInspected);
    }

    [Fact]
    public void MaxDepthIsCapped()
    {
        using var root = new TemporaryDirectory();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxDepth = PluginDiscoveryOptions.MaxDepthLimit + 1 }, Token));
        Assert.True(PluginDiscovery.Discover(root.Path, new PluginDiscoveryOptions { MaxDepth = PluginDiscoveryOptions.MaxDepthLimit }, Token).IsComplete);
    }

    [Fact]
    public void CancellationDuringTheScanStopsIt()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("a/one.ps1");
        root.WriteFile("b/two.ps1");
        using var cancellation = new CancellationTokenSource();
        var listed = new List<string?>();

        // Cancel deterministically once the first subdirectory has been listed.
        Assert.ThrowsAny<OperationCanceledException>(() => PluginDiscovery.Discover(
            root.Path,
            options: null,
            directoryListed: path =>
            {
                listed.Add(path);
                if (path is not null)
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token));

        string?[] expected = [null, "a"];
        Assert.Equal(expected, listed);
    }

    [Fact]
    public void ListingChecksCancellation()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("a.ps1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PluginDiscovery.ListDirectory(root.Path, null, 10, cancellation.Token, out _, out _));
    }

    [Fact]
    public void CancellationAfterListingAnEmptyDirectoryIsHonoured()
    {
        // Before the fix, an empty root left no entry loop to observe the cancellation, so a result
        // was returned after the token had been cancelled.
        using var root = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => PluginDiscovery.Discover(
            root.Path,
            options: null,
            directoryListed: _ => cancellation.Cancel(),
            cancellation.Token));
    }

    [Fact]
    public void CancellationAfterListingAnEmptySubdirectoryIsHonoured()
    {
        using var root = new TemporaryDirectory();
        root.CreateDirectory("empty");
        using var cancellation = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => PluginDiscovery.Discover(
            root.Path,
            options: null,
            directoryListed: path =>
            {
                if (path == "empty")
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token));
    }

    [Fact]
    public void ListingRetainsAtMostTheBudgetAndReportsTruncation()
    {
        using var root = new TemporaryDirectory();
        for (int i = 0; i < 5; i++)
        {
            root.WriteFile($"p{i}.ps1");
        }

        var seen = new List<string>();
        Assert.Null(PluginDiscovery.ListDirectory(root.Path, null, 3, Token, out List<FileSystemInfo> partial, out bool truncated, seen.Add));
        Assert.Equal(3, partial.Count);
        Assert.True(truncated);
        Assert.Equal(4, seen.Count);

        Assert.Null(PluginDiscovery.ListDirectory(root.Path, null, 0, Token, out List<FileSystemInfo> none, out bool noneTruncated));
        Assert.Empty(none);
        Assert.True(noneTruncated);

        Assert.Null(PluginDiscovery.ListDirectory(root.Path, null, 5, Token, out List<FileSystemInfo> exact, out bool exactTruncated));
        Assert.Equal(5, exact.Count);
        Assert.False(exactTruncated);

        Assert.Null(PluginDiscovery.ListDirectory(root.Path, null, int.MaxValue, Token, out List<FileSystemInfo> all, out bool allTruncated));
        Assert.Equal(5, all.Count);
        Assert.False(allTruncated);
    }

    [Fact]
    public void CandidateReplacedByADirectoryAfterListingIsNotOpened()
    {
        // Windows reports listing attributes from the enumeration itself, so the replacement is seen
        // only by the re-check. (.NET on other platforms may read them lazily, after the replacement.)
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on attributes captured by the Windows directory enumeration.");
        using var root = new TemporaryDirectory();
        root.WriteFile("a.ps1");
        string swapped = root.WriteFile("swap.ps1");

        PluginDiscoveryResult result = DiscoverObserved(
            root,
            options: null,
            out var enumerated,
            directoryListed: path =>
            {
                if (path is null)
                {
                    File.Delete(swapped);
                    Directory.CreateDirectory(swapped);
                    File.WriteAllText(Path.Combine(swapped, "inner.ps1"), string.Empty);
                }
            });

        // Without the pre-open check the directory was opened as a file, giving TW320.
        Assert.Equal("a.ps1", Assert.Single(result.Plugins).Id.Value);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.ReparsePointSkipped, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("swap.ps1", diagnostic.Path);
        Assert.Contains("not opened", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(enumerated, entry => entry.Directory == "swap.ps1");
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void DirectoryReplacedByAFileAfterListingIsNotListed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on attributes captured by the Windows directory enumeration.");
        using var root = new TemporaryDirectory();
        root.WriteFile("a.ps1");
        root.WriteFile("sub/inner.ps1");
        string sub = root.Combine("sub");

        PluginDiscoveryResult result = DiscoverObserved(
            root,
            options: null,
            out var enumerated,
            directoryListed: path =>
            {
                if (path is null)
                {
                    Directory.Delete(sub, recursive: true);
                    File.WriteAllText(sub, string.Empty);
                }
            });

        // Without the pre-listing check the file was enumerated as a directory, giving TW310.
        Assert.Equal("a.ps1", Assert.Single(result.Plugins).Id.Value);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.ReparsePointSkipped, diagnostic.Code);
        Assert.Equal("sub", diagnostic.Path);
        Assert.Contains("not listed", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(enumerated, entry => entry.Directory == "sub");
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void DirectoryReplacedByAJunctionAfterListingIsNotEnumerated()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        outside.WriteFile("outside.5s.ps1");
        root.WriteFile("sub/inner.ps1");
        string sub = root.Combine("sub");
        string probe = root.Combine("probe");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(probe, outside.Path), "Directory junctions cannot be created on this platform.");
        Directory.Delete(probe);
        bool replaced = false;

        PluginDiscoveryResult result = DiscoverObserved(
            root,
            options: null,
            out var enumerated,
            directoryListed: path =>
            {
                if (path is null)
                {
                    Directory.Delete(sub, recursive: true);
                    replaced = TemporaryDirectory.TryCreateJunction(sub, outside.Path);
                }
            });

        Assert.True(replaced);

        // Before the fix, the junction was enumerated (its target's entries were read) and the
        // listing was discarded only afterwards.
        Assert.DoesNotContain(enumerated, entry => entry.Directory == "sub");
        Assert.Empty(result.Plugins);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.ReparsePointSkipped, diagnostic.Code);
        Assert.Equal("sub", diagnostic.Path);
        Assert.Contains("not listed", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, result.EntriesInspected);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void CandidateReplacedByASymbolicLinkAfterListingIsNotOpened()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on attributes captured by the Windows directory enumeration.");
        using var root = new TemporaryDirectory();
        string swapped = root.WriteFile("swap.ps1");
        string probe = root.Combine("probe.ps1");
        Assert.SkipUnless(TemporaryDirectory.TryCreateFileSymbolicLink(probe, swapped), "Symbolic links cannot be created here.");
        File.Delete(probe);

        // The link target does not exist. Opening the link would follow it and fail with TW321;
        // the pre-open check sees the link itself and does not open it.
        PluginDiscoveryResult result = DiscoverObserved(
            root,
            options: null,
            out _,
            directoryListed: path =>
            {
                if (path is null)
                {
                    File.Delete(swapped);
                    File.CreateSymbolicLink(swapped, root.Combine("missing-target.ps1"));
                }
            });

        Assert.Empty(result.Plugins);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.ReparsePointSkipped, diagnostic.Code);
        Assert.Equal("swap.ps1", diagnostic.Path);
        Assert.Contains("not opened", diagnostic.Message, StringComparison.Ordinal);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void CandidateThatBecameOfflineAfterListingIsNotOpened()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The offline attribute is a Windows feature.");
        using var root = new TemporaryDirectory();
        root.WriteFile("a.ps1");
        string offline = root.WriteFile("offline.ps1");
        bool kept = false;

        PluginDiscoveryResult result = DiscoverObserved(
            root,
            options: null,
            out _,
            directoryListed: path =>
            {
                if (path is null)
                {
                    File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
                    kept = (File.GetAttributes(offline) & FileAttributes.Offline) != 0;
                }
            });

        Assert.SkipUnless(kept, "The file system does not keep the offline attribute.");

        // Without the pre-open check the file was read and only then discarded.
        Assert.Equal("a.ps1", Assert.Single(result.Plugins).Id.Value);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.RemoteStorageSkipped, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("offline.ps1", diagnostic.Path);
        Assert.Contains("not opened", diagnostic.Message, StringComparison.Ordinal);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void OfflineRootIsNotListed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The offline attribute is a Windows feature.");
        using var root = new TemporaryDirectory();
        string pluginRoot = root.CreateDirectory("plugins");
        root.WriteFile("plugins/clock.ps1");
        File.SetAttributes(pluginRoot, File.GetAttributes(pluginRoot) | FileAttributes.Offline);
        Assert.SkipUnless((File.GetAttributes(pluginRoot) & FileAttributes.Offline) != 0, "The file system does not keep the offline attribute on directories.");

        PluginDiscoveryResult result = PluginDiscovery.Discover(
            pluginRoot,
            options: null,
            directoryListed: null,
            Token,
            entryEnumerated: (_, name) => Assert.Fail($"The root was enumerated ({name})."));

        Assert.Empty(result.Plugins);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.RemoteStorageSkipped, diagnostic.Code);
        Assert.Equal(PluginDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Null(diagnostic.Path);
        Assert.Equal(0, result.EntriesInspected);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void OfflineFilesAreNotRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The offline attribute is a Windows feature.");
        using var root = new TemporaryDirectory();
        string offline = root.WriteFile("offline.ps1", "# <xbar.title>Offline</xbar.title>");
        root.WriteFile("online.ps1");
        File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
        Assert.SkipUnless((File.GetAttributes(offline) & FileAttributes.Offline) != 0, "The file system does not keep the offline attribute.");

        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);

        Assert.Equal("online.ps1", Assert.Single(result.Plugins).Id.Value);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(PluginDiagnosticCode.RemoteStorageSkipped, diagnostic.Code);
        Assert.Equal("offline.ps1", diagnostic.Path);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void FileNameNotesAreAttachedToThePlugin()
    {
        using var root = new TemporaryDirectory();
        root.WriteFile("backup.2024.ps1", "Write-Output \"<xbar.title>Fake</xbar.title>\"\n# <xbar.desc>Real</xbar.desc>\n");

        PluginDescriptor plugin = Assert.Single(PluginDiscovery.Discover(root.Path, cancellationToken: Token).Plugins);

        Assert.Equal("backup.2024", plugin.DisplayTitle);
        Assert.Equal("Real", plugin.Metadata.Description);
        PluginDiagnosticCode[] codes = [PluginDiagnosticCode.NumericNameSegment, PluginDiagnosticCode.MetadataTagOutsideComment];
        Assert.Equal(codes, plugin.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.All(plugin.Diagnostics, diagnostic => Assert.Equal("backup.2024.ps1", diagnostic.Path));
    }

    [Fact]
    public void FileDeletedBeforeReadingIsReported()
    {
        // The race itself cannot be triggered deterministically, so the read step is exercised directly.
        using var root = new TemporaryDirectory();
        byte[] buffer = new byte[16];

        PluginDiagnostic? diagnostic = PluginDiscovery.ReadPrefix(root.Combine("gone.ps1"), "gone.ps1", buffer, out int length);

        Assert.NotNull(diagnostic);
        Assert.Equal(PluginDiagnosticCode.FileDisappeared, diagnostic.Code);
        Assert.Equal("gone.ps1", diagnostic.Path);
        Assert.Equal(0, length);
    }

    [Fact]
    public void DirectoryDeletedBeforeListingIsReported()
    {
        using var root = new TemporaryDirectory();

        PluginDiagnostic? diagnostic = PluginDiscovery.ListDirectory(root.Combine("gone"), "gone", 10, Token, out List<FileSystemInfo> entries, out bool truncated);

        Assert.NotNull(diagnostic);
        Assert.Equal(PluginDiagnosticCode.DirectoryUnreadable, diagnostic.Code);
        Assert.Equal("gone", diagnostic.Path);
        Assert.Empty(entries);
        Assert.False(truncated);
    }

    [Fact]
    public void ReadPrefixReadsAtMostTheBufferLength()
    {
        using var root = new TemporaryDirectory();
        string file = root.WriteFile("big.ps1", new string('a', 1000));
        byte[] buffer = new byte[100];

        Assert.Null(PluginDiscovery.ReadPrefix(file, "big.ps1", buffer, out int length));
        Assert.Equal(100, length);
    }

    [Fact]
    public void DiscoveryNeverExecutesOrModifiesPlugins()
    {
        using var root = new TemporaryDirectory();
        using var markers = new TemporaryDirectory();
        string marker = markers.Combine("executed.txt");
        root.WriteFile("marker.5s.ps1", $"# <xbar.title>Marker</xbar.title>\nSet-Content -LiteralPath '{marker}' -Value ps1\n");
        root.WriteFile("marker.py", $"# <xbar.title>Marker</xbar.title>\nopen(r'{marker}', 'w').write('py')\n");
        root.WriteFile("marker.js", $"// <xbar.title>Marker</xbar.title>\nrequire('fs').writeFileSync(String.raw`{marker}`, 'js');\n");
        root.WriteFile("marker.cmd", $":: <xbar.title>Marker</xbar.title>\r\necho cmd > \"{marker}\"\r\n");
        root.WriteFile("marker.sh", $"#!/bin/sh\n# <xbar.title>Marker</xbar.title>\necho sh > '{marker}'\n");
        root.WriteFile("sub/marker.1h.bat", $"@echo off\r\necho bat > \"{marker}\"\r\n");
        string readOnly = root.WriteFile("sub/readonly.ps1", "Write-Output 'read-only'");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        root.WriteFile("bad.0s.ps1", "Write-Output 'rejected'");

        Dictionary<string, string> before = Snapshot(root.Path);
        PluginDiscoveryResult result = PluginDiscovery.Discover(root.Path, cancellationToken: Token);
        Dictionary<string, string> after = Snapshot(root.Path);

        Assert.Equal(7, result.Plugins.Length);
        Assert.Equal(before, after);

        Assert.False(File.Exists(marker), "A plugin was executed during discovery.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(markers.Path));

        // Discovery must not keep files open.
        foreach (string file in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories))
        {
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        }
    }

    // Runs discovery and records every entry the file system enumerator returned, as
    // (directory relative path or null for the root, entry name), including look-ahead entries.
    private static PluginDiscoveryResult DiscoverObserved(
        TemporaryDirectory root,
        PluginDiscoveryOptions? options,
        out List<(string? Directory, string Name)> enumerated,
        Action<string?>? directoryListed = null)
    {
        var seen = new List<(string? Directory, string Name)>();
        enumerated = seen;
        return PluginDiscovery.Discover(root.Path, options, directoryListed, Token, (directory, name) => seen.Add((directory, name)));
    }

    private static string Describe(PluginDiscoveryResult result)
    {
        var builder = new StringBuilder();
        foreach (PluginDescriptor plugin in result.Plugins)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{plugin.Id} {plugin.DisplayTitle} {plugin.FileName.RefreshInterval}");
            foreach (PluginDiagnostic diagnostic in plugin.Diagnostics)
            {
                builder.AppendLine("  " + diagnostic);
            }
        }

        foreach (PluginDiagnostic diagnostic in result.Diagnostics)
        {
            builder.AppendLine(diagnostic.ToString());
        }

        return builder.ToString();
    }

    // Path -> attributes, last write time, size and SHA-256, for every file and directory.
    // Metadata is queried from each entry itself: NTFS updates the copy kept in a directory listing
    // lazily, so listing-provided timestamps can change without any write.
    private static Dictionary<string, string> Snapshot(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            FileAttributes attributes = File.GetAttributes(path);
            string content = (attributes & FileAttributes.Directory) != 0
                ? "dir"
                : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + " " + new FileInfo(path).Length;
            snapshot[Path.GetRelativePath(root, path)] = $"{attributes} {File.GetLastWriteTimeUtc(path).Ticks} {content}";
        }

        return snapshot;
    }

    private static void DenyListing(TemporaryDirectory root, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            AddDenyRule(root, new DirectoryInfo(directory), FileSystemRights.ListDirectory | FileSystemRights.ReadData);
        }
    }

    private static void DenyReading(TemporaryDirectory root, string file)
    {
        if (OperatingSystem.IsWindows())
        {
            AddDenyRule(root, new FileInfo(file), FileSystemRights.ReadData);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddDenyRule(TemporaryDirectory root, FileSystemInfo target, FileSystemRights rights)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, rights, AccessControlType.Deny);

        if (target is DirectoryInfo directory)
        {
            DirectorySecurity security = directory.GetAccessControl();
            security.AddAccessRule(rule);
            directory.SetAccessControl(security);
            root.OnDispose(() =>
            {
                DirectorySecurity restored = directory.GetAccessControl();
                restored.RemoveAccessRule(rule);
                directory.SetAccessControl(restored);
            });
        }
        else if (target is FileInfo file)
        {
            FileSecurity security = file.GetAccessControl();
            security.AddAccessRule(rule);
            file.SetAccessControl(security);
            root.OnDispose(() =>
            {
                FileSecurity restored = file.GetAccessControl();
                restored.RemoveAccessRule(rule);
                file.SetAccessControl(restored);
            });
        }
    }
}
