using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Plugins;

public sealed class PluginIdTests
{
    [Theory]
    [InlineData("clock.5s.ps1", "clock.5s.ps1", "clock.5s.ps1")]
    [InlineData("work\\github.5m.py", "work/github.5m.py", "github.5m.py")]
    [InlineData("a/b\\c/d.js", "a/b/c/d.js", "d.js")]
    [InlineData("Météo/日本.ps1", "Météo/日本.ps1", "日本.ps1")]
    public void NormalizesSeparators(string input, string expected, string fileName)
    {
        PluginId id = PluginId.Create(input);

        Assert.Equal(expected, id.Value);
        Assert.Equal(expected, id.ToString());
        Assert.Equal(fileName, id.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/clock.ps1")]
    [InlineData("\\clock.ps1")]
    [InlineData("C:\\plugins\\clock.ps1")]
    [InlineData("C:clock.ps1")]
    [InlineData("\\\\server\\share\\clock.ps1")]
    [InlineData("a//clock.ps1")]
    [InlineData("a/")]
    [InlineData("./clock.ps1")]
    [InlineData("../clock.ps1")]
    [InlineData("a/../clock.ps1")]
    [InlineData("dir /clock.ps1")]
    [InlineData("dir./clock.ps1")]
    [InlineData("a?b/clock.ps1")]
    [InlineData("a\tb/clock.ps1")]
    [InlineData("CON/clock.ps1")]
    [InlineData("work/nul.ps1")]
    [InlineData("com1.txt/clock.ps1")]
    [InlineData("a\u202Eb/clock.ps1")]
    public void RejectsInvalidPaths(string? input)
    {
        Assert.False(PluginId.TryCreate(input, out PluginId? id));
        Assert.Null(id);
    }

    [Fact]
    public void CreateThrowsForInvalidPaths() => Assert.Throws<ArgumentException>(() => PluginId.Create("../x.ps1"));

    [Fact]
    public void EqualityIsCaseInsensitiveAndCasePreserving()
    {
        PluginId lower = PluginId.Create("work/clock.5s.ps1");
        PluginId upper = PluginId.Create("WORK\\CLOCK.5S.PS1");
        PluginId other = PluginId.Create("home/clock.5s.ps1");

        Assert.Equal(lower, upper);
        Assert.True(lower == upper);
        Assert.False(lower != upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.Equal("WORK/CLOCK.5S.PS1", upper.Value);
        Assert.NotEqual(lower, other);
        Assert.False(lower.Equals(null));
        Assert.True((PluginId?)null == null);
    }

    [Fact]
    public void DistinguishesIdenticalFileNamesInDifferentDirectories()
    {
        var ids = new HashSet<PluginId> { PluginId.Create("a/clock.ps1"), PluginId.Create("b/clock.ps1"), PluginId.Create("clock.ps1") };

        Assert.Equal(3, ids.Count);
        Assert.Single(ids.Select(id => id.FileName).Distinct());
    }

    [Fact]
    public void DoesNotApplyUnicodeNormalization()
    {
        // NTFS stores names as given, so composed and decomposed forms are different files.
        PluginId composed = PluginId.Create("caf\u00E9.ps1");
        PluginId decomposed = PluginId.Create("cafe\u0301.ps1");

        Assert.NotEqual(composed, decomposed);
    }

    [Fact]
    public void OrderingIsCaseInsensitiveAndCultureIndependent()
    {
        PluginId[] ids =
        [
            PluginId.Create("b.ps1"),
            PluginId.Create("A/z.ps1"),
            PluginId.Create("a/Y.ps1"),
            PluginId.Create("a.ps1"),
            PluginId.Create("I.ps1"),
        ];

        string[] sorted = [.. ids.Order().Select(id => id.Value)];

        string[] expected = ["a.ps1", "a/Y.ps1", "A/z.ps1", "b.ps1", "I.ps1"];
        Assert.Equal(expected, sorted);
        Assert.True(PluginId.Create("a.ps1") < PluginId.Create("b.ps1"));
        Assert.Equal(1, PluginId.Create("a.ps1").CompareTo(null));
    }

    [Theory]
    [InlineData("clock.ps1", "CLOCK.PS1")]
    [InlineData("a/b.ps1", "A\\B.ps1")]
    [InlineData("x.ps1", "y.ps1")]
    [InlineData("caf\u00E9.ps1", "cafe\u0301.ps1")]
    public void ComparisonIsConsistentWithEquality(string left, string right)
    {
        PluginId a = PluginId.Create(left);
        PluginId b = PluginId.Create(right);
        bool equal = a.Equals(b);

        Assert.Equal(equal, a.CompareTo(b) == 0);
        Assert.Equal(equal, b.CompareTo(a) == 0);
        Assert.Equal(Math.Sign(a.CompareTo(b)), -Math.Sign(b.CompareTo(a)));
        Assert.Equal(equal, a <= b && a >= b);
        Assert.Equal(new HashSet<PluginId> { a, b }.Count, new SortedSet<PluginId> { a, b }.Count);
    }
}
