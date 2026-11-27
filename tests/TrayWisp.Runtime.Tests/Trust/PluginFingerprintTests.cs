using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Trust;

[SupportedOSPlatform("windows")]
public sealed class PluginFingerprintTests
{
    private static CancellationToken Token => TrustTestEnvironment.Token;

    private static PluginInspectionResult Inspect(TrustTestEnvironment env, string relativePath, TrustTestHooks? hooks = null, CancellationToken? token = null) =>
        env.Service(hooks: hooks).Inspect(env.Root, PluginId.Create(relativePath), token ?? Token);

    private static string FingerprintOf(TrustTestEnvironment env, string relativePath)
    {
        PluginInspectionResult result = Inspect(env, relativePath);
        Assert.Equal(PluginInspectionStatus.Inspected, result.Status);
        return result.Plugin!.Fingerprint.Value;
    }

    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("The quick brown fox jumps over the lazy dog", "d7a8fbb307d7809469ca9abcb0082e4f8d5651e46d3cdb762d02d0bf37c9e592")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    public void MatchesKnownSha256Vectors(string content, string expected)
    {
        using var env = TrustTestEnvironment.Create();
        env.WriteBytes("vector.ps1", Encoding.ASCII.GetBytes(content));

        PluginInspectionResult result = Inspect(env, "vector.ps1");

        Assert.Equal(expected, result.Plugin!.Fingerprint.Value);
        Assert.Equal(content.Length, result.Plugin.ContentLength);
    }

    [Fact]
    public void HashesBinaryContentAndFilesLargerThanOneChunk()
    {
        using var env = TrustTestEnvironment.Create();
        byte[] allBytes = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        byte[] large = new byte[(3 * 64 * 1024) + 17];
        new Random(42).NextBytes(large);
        env.WriteBytes("binary.ps1", allBytes);
        env.WriteBytes("large.py", large);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(allBytes)), FingerprintOf(env, "binary.ps1"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(large)), FingerprintOf(env, "large.py"));
    }

    [Fact]
    public void EncodingBomAndLineEndingChangesChangeTheFingerprint()
    {
        using var env = TrustTestEnvironment.Create();
        const string Text = "# <xbar.title>Clock</xbar.title>\nGet-Date\n";
        env.WriteBytes("utf8.ps1", new UTF8Encoding(false).GetBytes(Text));
        env.WriteBytes("utf8bom.ps1", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Text)]);
        env.WriteBytes("utf16.ps1", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Text)]);
        env.WriteBytes("crlf.ps1", Encoding.UTF8.GetBytes(Text.Replace("\n", "\r\n", StringComparison.Ordinal)));
        env.WriteBytes("trailing.ps1", Encoding.UTF8.GetBytes(Text + " "));
        env.WriteBytes("nofinal.ps1", Encoding.UTF8.GetBytes(Text.TrimEnd('\n')));

        string[] names = ["utf8.ps1", "utf8bom.ps1", "utf16.ps1", "crlf.ps1", "trailing.ps1", "nofinal.ps1"];
        string[] fingerprints = [.. names.Select(name => FingerprintOf(env, name))];

        Assert.Equal(fingerprints.Length, fingerprints.Distinct().Count());

        // Discovery decodes all of them to the same metadata; the fingerprint is over bytes, not text.
        PluginDiscoveryResult discovery = PluginDiscovery.Discover(env.Root, cancellationToken: Token);
        Assert.All(discovery.Plugins, plugin => Assert.Equal("Clock", plugin.Metadata.Title));
    }

    [Fact]
    public void ChangesBeyondTheMetadataPrefixChangeTheFingerprint()
    {
        using var env = TrustTestEnvironment.Create();
        byte[] content = Encoding.UTF8.GetBytes("# <xbar.title>T</xbar.title>\n" + new string('#', 100_000) + "\nGet-Date\n");
        env.WriteBytes("a.ps1", content);
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");
        string before = FingerprintOf(env, "a.ps1");

        content[^3] = (byte)'X';
        env.WriteBytes("a.ps1", content);

        Assert.NotEqual(before, FingerprintOf(env, "a.ps1"));
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(service, "a.ps1").Status);
    }

    [Fact]
    public void FileAtTheSizeLimitIsHashedAndOneByteMoreIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        byte[] content = new byte[PluginTrustService.MaxPluginFileBytes];
        content[^1] = 1;
        env.WriteBytes("limit.ps1", content);
        env.WriteBytes("over.ps1", [.. content, 2]);

        PluginInspectionResult atLimit = Inspect(env, "limit.ps1");
        PluginInspectionResult overLimit = Inspect(env, "over.ps1");

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), atLimit.Plugin!.Fingerprint.Value);
        Assert.Equal(PluginTrustService.MaxPluginFileBytes, atLimit.Plugin.ContentLength);
        Assert.Equal(PluginInspectionStatus.PluginUnavailable, overLimit.Status);
        Assert.Null(overLimit.Plugin);
        Assert.Equal(TrustDiagnosticCode.FileTooLarge, Assert.Single(overLimit.Diagnostics).Code);
    }

    [Fact]
    public void ApprovedFileThatGrowsBeyondTheLimitIsDeniedWithoutInvalidation()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "small");
        PluginTrustService service = env.Service();
        env.Approve(service, "a.ps1");

        env.WriteBytes("a.ps1", new byte[PluginTrustService.MaxPluginFileBytes + 1]);
        PluginTrustDecision decision = env.Check(service, "a.ps1");

        // The file was not fingerprinted, so this is not an observed content change.
        Assert.Equal(PluginTrustStatus.PluginUnavailable, decision.Status);
        Assert.False(decision.ApprovalInvalidated);
        Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
    }

    [Fact]
    public void CancelledTokenThrowsBeforeAnythingIsRead()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        bool opened = false;

        Assert.Throws<OperationCanceledException>(() => Inspect(env, "a.ps1", new TrustTestHooks { PluginOpened = () => opened = true }, new CancellationToken(canceled: true)));

        Assert.False(opened);
    }

    [Fact]
    public void CancellationDuringHashingStopsTheInspection()
    {
        using var env = TrustTestEnvironment.Create();
        env.WriteBytes("a.ps1", new byte[4 * 64 * 1024]);
        using var cancellation = new CancellationTokenSource();
        long hashed = 0;
        var hooks = new TrustTestHooks
        {
            ChunkHashed = total =>
            {
                hashed = total;
                cancellation.Cancel();
            },
        };

        Assert.Throws<OperationCanceledException>(() => Inspect(env, "a.ps1", hooks, cancellation.Token));

        Assert.Equal(64 * 1024, hashed);
    }

    [Fact]
    public void CancellationDuringACheckLeavesTheApprovalUnchanged()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        env.Approve(env.Service(), "a.ps1");
        string store = env.StoreText();
        env.Write("a.ps1", "v2");
        using var cancellation = new CancellationTokenSource();
        PluginTrustService service = env.Service(hooks: new TrustTestHooks { PluginOpened = cancellation.Cancel });

        Assert.Throws<OperationCanceledException>(() => service.CheckTrust(env.Root, PluginId.Create("a.ps1"), cancellation.Token));

        // Cancelled before the change was observed: nothing is invalidated, nothing is approved.
        Assert.Equal(store, env.StoreText());
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(env.Service(), "a.ps1").Status);
    }

    [Fact]
    public void CancellationDuringApprovalSavesNothing()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "v1");
        ContentFingerprint reviewed = Inspect(env, "a.ps1").Plugin!.Fingerprint;
        using var cancellation = new CancellationTokenSource();
        PluginTrustService service = env.Service(hooks: new TrustTestHooks { PluginOpened = cancellation.Cancel });

        Assert.Throws<OperationCanceledException>(() => service.Approve(env.Root, PluginId.Create("a.ps1"), reviewed, cancellation.Token));

        Assert.False(File.Exists(env.StorePath));
        Assert.Equal(PluginTrustStatus.PendingApproval, env.Check(env.Service(), "a.ps1").Status);
    }

    [Fact]
    public void FileOpenForWritingElsewhereIsNotFingerprinted()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("a.ps1", "1");

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            PluginInspectionResult result = Inspect(env, "a.ps1");
            Assert.Equal(PluginInspectionStatus.PluginUnavailable, result.Status);
            Assert.Equal(TrustDiagnosticCode.FileLocked, Assert.Single(result.Diagnostics).Code);
        }

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(TrustDiagnosticCode.FileLocked, Assert.Single(Inspect(env, "a.ps1").Diagnostics).Code);
        }

        Assert.Equal(PluginInspectionStatus.Inspected, Inspect(env, "a.ps1").Status);
    }

    [Fact]
    public void MissingFileIsUnavailable()
    {
        using var env = TrustTestEnvironment.Create();

        PluginInspectionResult result = Inspect(env, "missing.ps1");

        Assert.Equal(PluginInspectionStatus.PluginUnavailable, result.Status);
        Assert.Equal(TrustDiagnosticCode.PluginNotFound, Assert.Single(result.Diagnostics).Code);
        Assert.NotNull(result.Key);
    }

    [Fact]
    public void UnreadableFileIsUnavailable()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("denied.ps1", "1");
        env.DenyReading(path);

        PluginInspectionResult result = Inspect(env, "denied.ps1");

        Assert.Equal(PluginInspectionStatus.PluginUnavailable, result.Status);
        Assert.Equal(TrustDiagnosticCode.FileUnreadable, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void FileCannotBeModifiedDeletedOrRenamedWhileItIsHashed()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("dir/a.ps1", "original");
        string directory = Path.GetDirectoryName(path)!;
        var attempts = new List<string>();
        var hooks = new TrustTestHooks
        {
            PluginOpened = () =>
            {
                attempts.Add(Attempt(() => File.WriteAllText(path, "replaced")));
                attempts.Add(Attempt(() => File.AppendAllText(path, "appended")));
                attempts.Add(Attempt(() => File.Delete(path)));
                attempts.Add(Attempt(() => File.Move(path, path + ".moved")));
                attempts.Add(Attempt(() => Directory.Move(directory, directory + "-moved")));
                attempts.Add(Attempt(() => Directory.Move(env.Root, env.Root + "-moved")));
            },
        };

        PluginInspectionResult result = Inspect(env, "dir/a.ps1", hooks);

        Assert.All(attempts, outcome => Assert.Equal("blocked", outcome));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("original"u8)), result.Plugin!.Fingerprint.Value);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void FileStaysProtectedUntilTheApprovalIsSaved()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("dir/a.ps1", "reviewed");
        ContentFingerprint reviewed = Inspect(env, "dir/a.ps1").Plugin!.Fingerprint;
        string? writeDuringCommit = null;
        string? renameDuringCommit = null;
        var hooks = new TrustTestHooks
        {
            BeforeStorePublish = _ =>
            {
                writeDuringCommit = Attempt(() => File.WriteAllText(path, "swapped"));
                renameDuringCommit = Attempt(() => Directory.Move(Path.GetDirectoryName(path)!, Path.GetDirectoryName(path) + "-moved"));
            },
        };

        PluginApprovalResult result = env.Service(hooks: hooks).Approve(env.Root, PluginId.Create("dir/a.ps1"), reviewed, Token);

        Assert.Equal(PluginApprovalStatus.Approved, result.Status);
        Assert.Equal("blocked", writeDuringCommit);
        Assert.Equal("blocked", renameDuringCommit);
        Assert.Equal("reviewed", File.ReadAllText(path));

        // Released afterwards.
        File.WriteAllText(path, "edited later");
        Assert.Equal(PluginTrustStatus.ContentChanged, env.Check(env.Service(), "dir/a.ps1").Status);
    }

    [Fact]
    public void InspectionNeitherModifiesNorExecutesThePlugin()
    {
        using var env = TrustTestEnvironment.Create();
        // A script that would leave a marker if it ever ran.
        string marker = Path.Combine(env.Base, "ran.txt");
        string path = env.Write("marker.ps1", $"Set-Content -Path '{marker}' -Value ran\n");
        DateTime written = File.GetLastWriteTimeUtc(path);
        FileAttributes attributes = File.GetAttributes(path);

        PluginTrustService service = env.Service();
        env.Approve(service, "marker.ps1");
        env.Check(service, "marker.ps1");

        Assert.False(File.Exists(marker));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(attributes, File.GetAttributes(path));
    }

    private static string Attempt(Action action)
    {
        try
        {
            action();
            return "succeeded";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "blocked";
        }
    }
}
