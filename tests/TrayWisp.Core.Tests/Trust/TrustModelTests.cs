using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using Xunit;

namespace TrayWisp.Core.Tests.Trust;

public sealed class TrustModelTests
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void FingerprintFromHashIsCanonicalLowercaseHex()
    {
        byte[] hash = Convert.FromHexString(EmptySha256);

        ContentFingerprint fingerprint = ContentFingerprint.FromHash(hash);

        Assert.Equal(EmptySha256, fingerprint.Value);
        Assert.Equal(EmptySha256, fingerprint.ToString());
        Assert.Equal(ContentFingerprint.Parse(EmptySha256), fingerprint);
        Assert.True(ContentFingerprint.Parse(EmptySha256) == fingerprint);
        Assert.Equal(ContentFingerprint.Parse(EmptySha256).GetHashCode(), fingerprint.GetHashCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b8555")]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852")]
    [InlineData(" e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    [InlineData("g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void FingerprintParsingIsStrict(string? text)
    {
        Assert.False(ContentFingerprint.TryParse(text, out ContentFingerprint? fingerprint));
        Assert.Null(fingerprint);
        if (text is not null)
        {
            Assert.Throws<FormatException>(() => ContentFingerprint.Parse(text));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void FingerprintRequiresExactlyAThirtyTwoByteHash(int length) =>
        Assert.Throws<ArgumentException>(() => ContentFingerprint.FromHash(new byte[length]));

    [Fact]
    public void FingerprintsDifferingInOneDigitAreNotEqual()
    {
        ContentFingerprint a = ContentFingerprint.Parse(EmptySha256);
        ContentFingerprint b = ContentFingerprint.Parse(EmptySha256[..^1] + "4");

        Assert.NotEqual(a, b);
        Assert.True(a != b);
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void GenerationFromGuidIsCanonical()
    {
        var guid = Guid.Parse("01234567-89AB-CDEF-0123-456789ABCDEF");

        ApprovalGeneration generation = ApprovalGeneration.FromGuid(guid);

        Assert.Equal("0123456789abcdef0123456789abcdef", generation.Value);
        Assert.Equal(ApprovalGeneration.Parse("0123456789abcdef0123456789abcdef"), generation);
        Assert.NotEqual(ApprovalGeneration.FromGuid(Guid.NewGuid()), generation);
        Assert.Throws<ArgumentException>(() => ApprovalGeneration.FromGuid(Guid.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("{0123456789abcdef0123456789abcdef}")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdefa")]
    public void GenerationParsingIsStrict(string? text) => Assert.False(ApprovalGeneration.TryParse(text, out _));

    [Theory]
    [InlineData(@"C:\plugins", @"C:\plugins")]
    [InlineData(@"C:\plugins\", @"C:\plugins")]
    [InlineData("C:/plugins/", @"C:\plugins")]
    [InlineData(@"c:\Users\Me\My Plugins", @"c:\Users\Me\My Plugins")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("d:/", @"d:\")]
    [InlineData(@"C:\a\.config\plugins", @"C:\a\.config\plugins")]
    [InlineData(@"C:\a\b.c\d", @"C:\a\b.c\d")]
    public void RootIsNormalized(string input, string expected)
    {
        PluginRoot root = PluginRoot.Create(input);

        Assert.Equal(expected, root.Value);
        Assert.Equal(expected.Length == 3, root.IsDriveRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plugins")]
    [InlineData(@".\plugins")]
    [InlineData(@"..\plugins")]
    [InlineData("C:")]
    [InlineData("C:plugins")]
    [InlineData(@"\plugins")]
    [InlineData(@"\\server\share\plugins")]
    [InlineData("//server/share/plugins")]
    [InlineData(@"\\?\C:\plugins")]
    [InlineData(@"\\.\C:\plugins")]
    [InlineData(@"\??\C:\plugins")]
    [InlineData(@"\\?\UNC\server\share")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"1:\plugins")]
    [InlineData(@"CC:\plugins")]
    [InlineData(@"C:\\")]
    [InlineData(@"C:\plugins\\")]
    [InlineData(@"C:\a\\b")]
    [InlineData(@"C:\a\.\b")]
    [InlineData(@"C:\a\..\b")]
    [InlineData(@"C:\a\..")]
    [InlineData(@"C:\plugins:stream")]
    [InlineData(@"C:\plugins::$DATA")]
    [InlineData(@"C:\plugins\a*")]
    [InlineData(@"C:\plugins\a?")]
    [InlineData(@"C:\plugins\a""")]
    [InlineData(@"C:\plugins\a<b")]
    [InlineData(@"C:\plugins\a|b")]
    [InlineData(@"C:\plugins\CON")]
    [InlineData(@"C:\plugins\nul.txt")]
    [InlineData(@"C:\plugins\COM1")]
    [InlineData(@"C:\plugins.")]
    [InlineData(@"C:\plugins ")]
    [InlineData(@"C:\plugins\a.\b")]
    [InlineData("C:\\a\tb")]
    public void RootRejectsUnsupportedPaths(string input)
    {
        Assert.False(PluginRoot.TryCreate(input, out PluginRoot? root));
        Assert.Null(root);
        Assert.Throws<ArgumentException>(() => PluginRoot.Create(input));
    }

    [Fact]
    public void RootRejectsBidiControlsAndExcessiveLength()
    {
        Assert.False(PluginRoot.TryCreate(@"C:\a" + (char)0x202E + "b", out _));
        Assert.False(PluginRoot.TryCreate(null, out _));

        string atLimit = @"C:\" + new string('a', PluginRoot.MaxLength - 3);
        Assert.True(PluginRoot.TryCreate(atLimit, out _));
        Assert.False(PluginRoot.TryCreate(atLimit + "a", out _));
        Assert.False(PluginRoot.TryCreate(atLimit + "\\", out _));
    }

    [Fact]
    public void RootEqualityIsCaseInsensitiveButPreservesSpelling()
    {
        PluginRoot a = PluginRoot.Create(@"C:\Users\Me\Plugins");
        PluginRoot b = PluginRoot.Create(@"c:\users\me\plugins\");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(0, a.CompareTo(b));
        Assert.Equal(@"C:\Users\Me\Plugins", a.Value);
        Assert.NotEqual(a, PluginRoot.Create(@"C:\Users\Me\Plugins2"));
    }

    [Fact]
    public void RootContainmentComparesWholeSegments()
    {
        PluginRoot plugins = PluginRoot.Create(@"C:\data\plugins");

        Assert.True(plugins.Contains(plugins));
        Assert.True(plugins.Contains(PluginRoot.Create(@"c:\DATA\plugins\sub")));
        Assert.False(plugins.Contains(PluginRoot.Create(@"C:\data\plugins2")));
        Assert.False(plugins.Contains(PluginRoot.Create(@"C:\data")));
        Assert.False(plugins.Contains(PluginRoot.Create(@"D:\data\plugins")));
        Assert.True(PluginRoot.Create(@"C:\").Contains(plugins));
        Assert.False(PluginRoot.Create(@"D:\").Contains(plugins));

        Assert.True(plugins.Overlaps(PluginRoot.Create(@"C:\data")));
        Assert.True(PluginRoot.Create(@"C:\data").Overlaps(plugins));
        Assert.False(plugins.Overlaps(PluginRoot.Create(@"C:\data\other")));
    }

    [Theory]
    [InlineData("clock.ps1")]
    [InlineData("work/github.notifications.5m.py")]
    [InlineData("a/b/c/d/e/f/g/h/deep.js")]
    [InlineData("CLOCK.5S.PS1")]
    [InlineData("my plugins/météo.10m.py")]
    public void SupportedIdentitiesAreThoseDiscoveryCanProduce(string id) =>
        Assert.True(PluginTrustKey.IsSupportedPluginId(PluginId.Create(id)));

    [Theory]
    [InlineData("readme.md")]
    [InlineData("tool.exe")]
    [InlineData("clock.0s.ps1")]
    [InlineData("5s.ps1")]
    [InlineData(".clock.ps1")]
    [InlineData(".git/clock.ps1")]
    [InlineData("work/.cache/clock.ps1")]
    [InlineData("a/b/c/d/e/f/g/h/i/too-deep.js")]
    public void UnsupportedIdentitiesAreRejected(string id)
    {
        PluginId pluginId = PluginId.Create(id);

        Assert.False(PluginTrustKey.IsSupportedPluginId(pluginId));
        Assert.Throws<ArgumentException>(() => new PluginTrustKey(PluginRoot.Create(@"C:\plugins"), pluginId));
    }

    [Fact]
    public void OverlongIdentitiesAreRejected()
    {
        // A directory segment plus "/b.ps1": 4,090 + 6 = 4,096 characters is the limit.
        PluginId atLimit = PluginId.Create(new string('a', 4090) + "/b.ps1");
        PluginId overLimit = PluginId.Create(new string('a', 4091) + "/b.ps1");

        Assert.Equal(PluginTrustKey.MaxPluginIdLength, atLimit.Value.Length);
        Assert.True(PluginTrustKey.IsSupportedPluginId(atLimit));
        Assert.False(PluginTrustKey.IsSupportedPluginId(overLimit));
        Assert.False(PluginTrustKey.IsSupportedPluginId(null));
    }

    [Fact]
    public void SameIdentityUnderDifferentRootsIsADifferentKey()
    {
        PluginId id = PluginId.Create("work/clock.ps1");
        var a = new PluginTrustKey(PluginRoot.Create(@"C:\plugins"), id);
        var b = new PluginTrustKey(PluginRoot.Create(@"C:\other"), id);
        var c = new PluginTrustKey(PluginRoot.Create(@"c:\PLUGINS\"), PluginId.Create(@"Work\Clock.PS1"));

        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a.GetHashCode(), c.GetHashCode());
        Assert.Equal(0, a.CompareTo(c));
        Assert.True(b < a);
        Assert.Equal(@"C:\plugins | work/clock.ps1", a.ToString());
    }

    [Fact]
    public void ApprovalTimeMustBeUtc()
    {
        var key = new PluginTrustKey(PluginRoot.Create(@"C:\plugins"), PluginId.Create("a.ps1"));
        ContentFingerprint fingerprint = ContentFingerprint.Parse(EmptySha256);
        ApprovalGeneration generation = ApprovalGeneration.FromGuid(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => new PluginApproval(key, fingerprint, generation, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1))));

        var approval = new PluginApproval(key, fingerprint, generation, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new PluginApprovalIdentity(key, fingerprint, generation), approval.Identity);
    }

    [Fact]
    public void TrustDiagnosticFormatsCodeInTheTrustRange()
    {
        var diagnostic = new TrustDiagnostic(PluginDiagnosticSeverity.Error, TrustDiagnosticCode.ContentChanged, "Changed.");

        Assert.Equal("Error TW531: Changed.", diagnostic.ToString());
    }

    [Fact]
    public void TrustDiagnosticCodesAreInTheirOwnRange()
    {
        int[] trust = [.. Enum.GetValues<TrustDiagnosticCode>().Select(code => (int)code)];
        int[] plugin = [.. Enum.GetValues<PluginDiagnosticCode>().Select(code => (int)code)];

        Assert.All(trust, code => Assert.InRange(code, 500, 599));
        Assert.Equal(trust.Length, trust.Distinct().Count());
        Assert.All(plugin, code => Assert.InRange(code, 100, 399));
    }

    [Fact]
    public void RootsAndIdentitiesMustBeWellFormedUtf16()
    {
        string high = ((char)0xD800).ToString();
        string low = ((char)0xDC00).ToString();
        string pair = "\uD83D\uDE80";

        foreach (string broken in new[] { high, low, low + high, "a" + high + "b", "a" + low })
        {
            Assert.False(PluginRoot.TryCreate(@"C:\plugins\" + broken, out _));
            Assert.False(PluginTrustKey.IsSupportedPluginId(PluginId.Create("dir" + broken + "/clock.ps1")));
            Assert.False(PluginTrustKey.IsSupportedPluginId(PluginId.Create("clock" + broken + ".ps1")));
        }

        // Supplementary characters are correctly paired surrogates and stay accepted.
        Assert.Equal(@"C:\plugins\" + pair, PluginRoot.Create(@"C:\plugins\" + pair).Value);
        Assert.True(PluginTrustKey.IsSupportedPluginId(PluginId.Create(pair + "/clock " + pair + ".ps1")));
    }
}
