using System.Runtime.Versioning;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Tests.Plugins;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Trust;

// Root and plugin path validation. Tests that need junctions, symbolic links, case-sensitive directories,
// 8.3 short names or the offline attribute skip with a message when the machine does not provide them.
[SupportedOSPlatform("windows")]
public sealed class PluginPathValidationTests
{
    private static CancellationToken Token => TrustTestEnvironment.Token;

    private static PluginInspectionResult Inspect(TrustTestEnvironment env, string relativePath, string? root = null, TrustTestHooks? hooks = null) =>
        env.Service(hooks: hooks).Inspect(root ?? env.Root, PluginId.Create(relativePath), Token);

    private static void AssertRejected(PluginInspectionResult result, PluginInspectionStatus status, TrustDiagnosticCode code)
    {
        Assert.Equal(status, result.Status);
        Assert.Null(result.Plugin);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plugins")]
    [InlineData(@".\plugins")]
    [InlineData("C:plugins")]
    [InlineData(@"\plugins")]
    [InlineData(@"\\server\share\plugins")]
    [InlineData(@"\\localhost\C$\plugins")]
    [InlineData("//server/share/plugins")]
    [InlineData(@"\\?\C:\plugins")]
    [InlineData(@"\\?\UNC\server\share\plugins")]
    [InlineData(@"\\.\C:\plugins")]
    [InlineData(@"\??\C:\plugins")]
    [InlineData(@"C:\plugins\..\other")]
    [InlineData(@"C:\plugins\.\sub")]
    [InlineData(@"C:\plugins\\sub")]
    [InlineData(@"C:\plugins:stream")]
    [InlineData(@"C:\plugins::$INDEX_ALLOCATION")]
    [InlineData(@"C:\plugins\CON")]
    [InlineData(@"C:\plugins.")]
    [InlineData(@"C:\plugins ")]
    public void UnsupportedRootsAreRejectedByEveryOperation(string root)
    {
        using var env = TrustTestEnvironment.Create();
        PluginTrustService service = env.Service();
        PluginId id = PluginId.Create("clock.ps1");

        PluginInspectionResult inspection = service.Inspect(root, id, Token);
        PluginTrustDecision decision = service.CheckTrust(root, id, Token);
        PluginApprovalResult approval = service.Approve(root, id, ContentFingerprint.Parse(new string('0', 64)), Token);
        PluginRevocationResult revocation = service.Revoke(root, id, Token);

        AssertRejected(inspection, PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.InvalidRootPath);
        Assert.Null(inspection.Key);
        Assert.Equal(PluginTrustStatus.UnsafePath, decision.Status);
        Assert.Equal(PluginApprovalStatus.UnsafePath, approval.Status);
        Assert.Equal(PluginRevocationStatus.InvalidPath, revocation.Status);
        Assert.All(decision.Diagnostics.Concat(approval.Diagnostics).Concat(revocation.Diagnostics), d => Assert.Equal(TrustDiagnosticCode.InvalidRootPath, d.Code));
        Assert.False(Directory.Exists(env.StoreDirectory));
    }

    [Fact]
    public void RootDiagnosticsEscapeUntrustedText()
    {
        using var env = TrustTestEnvironment.Create();
        string root = "C:\\a\nfake" + (char)0x202E + "txt";

        PluginInspectionResult result = env.Service().Inspect(root, PluginId.Create("a.ps1"), Token);

        string message = Assert.Single(result.Diagnostics).Message;
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain((char)0x202E, message);
        Assert.Contains("\\u000A", message, StringComparison.Ordinal);
        Assert.Contains("\\u202E", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".hidden/clock.ps1")]
    [InlineData("work/.cache/clock.ps1")]
    [InlineData(".clock.ps1")]
    [InlineData("readme.md")]
    [InlineData("tool.exe")]
    [InlineData("clock.0s.ps1")]
    [InlineData("a/b/c/d/e/f/g/h/i/deep.ps1")]
    public void IdentitiesDiscoveryCannotProduceAreRejected(string id)
    {
        using var env = TrustTestEnvironment.Create();
        env.Write(id, "1");

        AssertRejected(Inspect(env, id), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.InvalidPluginPath);
        Assert.Equal(PluginTrustStatus.UnsafePath, env.Check(env.Service(), id).Status);
    }

    [Fact]
    public void DeepestDiscoverableDirectoryIsAccepted()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a/b/c/d/e/f/g/h/deep.ps1", "1");

        Assert.Contains(PluginDiscovery.Discover(env.Root, cancellationToken: Token).Plugins, plugin => plugin.Id.Value == "a/b/c/d/e/f/g/h/deep.ps1");
        Assert.Equal(PluginInspectionStatus.Inspected, Inspect(env, "a/b/c/d/e/f/g/h/deep.ps1").Status);
    }

    [Fact]
    public void MissingRootAndAncestorAreUnavailable()
    {
        using var env = TrustTestEnvironment.Create();

        PluginInspectionResult missingRoot = Inspect(env, "a.ps1", Path.Combine(env.Base, "missing"));
        PluginInspectionResult missingAncestor = Inspect(env, "a.ps1", Path.Combine(env.Base, "missing", "plugins"));

        AssertRejected(missingRoot, PluginInspectionStatus.PluginUnavailable, TrustDiagnosticCode.RootNotFound);
        AssertRejected(missingAncestor, PluginInspectionStatus.PluginUnavailable, TrustDiagnosticCode.RootNotFound);
        Assert.Contains("ancestor", missingAncestor.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RootThatIsAFileIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        string file = Path.Combine(env.Base, "file.txt");
        File.WriteAllText(file, "x");

        AssertRejected(Inspect(env, "a.ps1", file), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.RootNotDirectory);
    }

    [Fact]
    public void JunctionRootIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        string link = Path.Combine(env.Base, "link");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(link, env.Root), "Directory junctions cannot be created on this machine.");

        AssertRejected(Inspect(env, "a.ps1", link), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.RootUnsafe);
    }

    [Fact]
    public void SymbolicLinkRootIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        string link = Path.Combine(env.Base, "link");
        Assert.SkipUnless(TemporaryDirectory.TryCreateDirectorySymbolicLink(link, env.Root), "Symbolic links cannot be created here (Developer Mode or elevation is required).");

        AssertRejected(Inspect(env, "a.ps1", link), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.RootUnsafe);
    }

    [Fact]
    public void JunctionInAnAncestorOfTheRootIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        string link = Path.Combine(env.Base, "link");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(link, env.Base), "Directory junctions cannot be created on this machine.");

        PluginInspectionResult result = Inspect(env, "a.ps1", Path.Combine(link, "plugins"));

        AssertRejected(result, PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.RootUnsafe);
        Assert.Contains("ancestor", result.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JunctionInsideTheRootIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        string outside = Directory.CreateDirectory(Path.Combine(env.Base, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "a.ps1"), "1");
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(env.PluginPath("linked"), outside), "Directory junctions cannot be created on this machine.");

        AssertRejected(Inspect(env, "linked/a.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.PluginPathUnsafe);
    }

    [Fact]
    public void SymbolicLinkPluginFileIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        string target = Path.Combine(env.Base, "target.ps1");
        File.WriteAllText(target, "1");
        Assert.SkipUnless(TemporaryDirectory.TryCreateFileSymbolicLink(env.PluginPath("link.ps1"), target), "Symbolic links cannot be created here (Developer Mode or elevation is required).");

        AssertRejected(Inspect(env, "link.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.PluginPathUnsafe);
    }

    [Fact]
    public void ApprovedPluginBehindANewJunctionIsDeniedWithoutInvalidation()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("work/a.ps1", "1");
        PluginTrustService service = env.Service();
        env.Approve(service, "work/a.ps1");

        // Replace the directory by a junction to an identical copy elsewhere.
        string copy = Directory.CreateDirectory(Path.Combine(env.Base, "copy")).FullName;
        File.WriteAllText(Path.Combine(copy, "a.ps1"), "1");
        Directory.Move(env.PluginPath("work"), Path.Combine(env.Base, "original"));
        Assert.SkipUnless(TemporaryDirectory.TryCreateJunction(env.PluginPath("work"), copy), "Directory junctions cannot be created on this machine.");

        PluginTrustDecision decision = env.Check(service, "work/a.ps1");

        Assert.Equal(PluginTrustStatus.UnsafePath, decision.Status);
        Assert.False(decision.ApprovalInvalidated);
        Assert.Single(ApprovalStoreSerializerTests.ReadStore(env.StorePath));
    }

    [Fact]
    public void HiddenFilesAndDirectoriesUnderTheRootAreRejected()
    {
        using var env = TrustTestEnvironment.Create();
        string file = env.Write("hidden.ps1", "1");
        env.Write("secret/a.ps1", "1");
        File.SetAttributes(file, FileAttributes.Hidden);
        File.SetAttributes(env.PluginPath("secret"), FileAttributes.Directory | FileAttributes.Hidden);

        AssertRejected(Inspect(env, "hidden.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.PluginPathUnsafe);
        AssertRejected(Inspect(env, "secret/a.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.PluginPathUnsafe);
        Assert.Empty(PluginDiscovery.Discover(env.Root, cancellationToken: Token).Plugins);
    }

    [Fact]
    public void HiddenRootIsAccepted()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        File.SetAttributes(env.Root, FileAttributes.Directory | FileAttributes.Hidden);

        Assert.Equal(PluginInspectionStatus.Inspected, Inspect(env, "a.ps1").Status);
    }

    [Fact]
    public void OfflineFileIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        string path = env.Write("offline.ps1", "1");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        Assert.SkipUnless((File.GetAttributes(path) & FileAttributes.Offline) != 0, "The file system does not keep the offline attribute.");

        AssertRejected(Inspect(env, "offline.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.PluginPathUnsafe);
    }

    [Fact]
    public void WrongKindsOfEntriesAreRejected()
    {
        using var env = TrustTestEnvironment.Create();
        Directory.CreateDirectory(env.PluginPath("folder.ps1"));
        env.Write("file", "1");

        AssertRejected(Inspect(env, "folder.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.PluginPathUnsafe);
        AssertRejected(Inspect(env, "file/a.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.PluginPathUnsafe);
    }

    [Fact]
    public void CaseVariantFilesInACaseSensitiveDirectoryAreAmbiguous()
    {
        using var env = TrustTestEnvironment.Create();
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(env.Root), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");
        env.Write("clock.ps1", "1");
        env.Write("Clock.ps1", "2");

        AssertRejected(Inspect(env, "clock.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.IdentityCollision);
        AssertRejected(Inspect(env, "Clock.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.IdentityCollision);
        Assert.Empty(PluginDiscovery.Discover(env.Root, cancellationToken: Token).Plugins);
    }

    [Fact]
    public void CaseVariantDirectoriesInACaseSensitiveDirectoryAreAmbiguous()
    {
        using var env = TrustTestEnvironment.Create();
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(env.Root), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");
        env.Write("work/a.ps1", "1");
        env.Write("Work/b.ps1", "2");

        AssertRejected(Inspect(env, "work/a.ps1"), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.IdentityCollision);
    }

    [Fact]
    public void CaseCollisionAppearingAfterApprovalDeniesWithoutInvalidation()
    {
        using var env = TrustTestEnvironment.Create();
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(env.Root), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");
        env.Write("clock.ps1", "1");
        PluginTrustService service = env.Service();
        env.Approve(service, "clock.ps1");

        env.Write("CLOCK.ps1", "evil twin");
        PluginTrustDecision decision = env.Check(service, "clock.ps1");

        Assert.Equal(PluginTrustStatus.UnsafePath, decision.Status);
        Assert.False(decision.ApprovalInvalidated);
    }

    [Fact]
    public void SinglePluginInACaseSensitiveRootIsResolvedLikeDiscovery()
    {
        using var env = TrustTestEnvironment.Create();
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(env.Root), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");
        env.Write("clock.ps1", "visible");
        string hidden = env.Write("CLOCK.ps1", "hidden twin");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        PluginDescriptor discovered = Assert.Single(PluginDiscovery.Discover(env.Root, cancellationToken: Token).Plugins);
        PluginInspectionResult byDiscoveredId = Inspect(env, discovered.Id.Value);
        PluginInspectionResult byOtherCase = Inspect(env, "CLOCK.ps1");

        // Both spellings are the same identity and resolve to the one entry discovery uses, never the hidden twin.
        Assert.Equal(byDiscoveredId.Plugin!.Fingerprint, byOtherCase.Plugin!.Fingerprint);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData("visible"u8), Convert.FromHexString(byOtherCase.Plugin.Fingerprint.Value));
        Assert.Equal(Path.Combine(env.Root, "clock.ps1"), byOtherCase.ValidatedFilePath);
    }

    [Fact]
    public void CaseSensitiveAncestorOfTheRootIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        string parent = Directory.CreateDirectory(Path.Combine(env.Base, "cs")).FullName;
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(parent), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");
        string root = Directory.CreateDirectory(Path.Combine(parent, "plugins")).FullName;
        File.WriteAllText(Path.Combine(root, "a.ps1"), "1");

        AssertRejected(Inspect(env, "a.ps1", root), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.CaseSensitiveAncestor);
    }

    [Fact]
    public void ShortNameAliasOfTheRootIsRejected()
    {
        using var env = TrustTestEnvironment.Create();
        string longRoot = Directory.CreateDirectory(Path.Combine(env.Base, "longdirectoryname")).FullName;
        File.WriteAllText(Path.Combine(longRoot, "a.ps1"), "1");
        string shortRoot = Path.Combine(env.Base, "LONGDI~1");
        Assert.SkipUnless(Directory.Exists(shortRoot), "8.3 short names are disabled on this volume.");

        PluginInspectionResult result = Inspect(env, "a.ps1", shortRoot);

        AssertRejected(result, PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.RootAlias);
        Assert.Contains("longdirectoryname", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(PluginInspectionStatus.Inspected, Inspect(env, "a.ps1", longRoot).Status);
    }

    [Fact]
    public void ShortNameAliasInsideTheRootIsNotResolved()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("longdirectoryname/a.ps1", "1");
        Assert.SkipUnless(Directory.Exists(env.PluginPath("LONGDI~1")), "8.3 short names are disabled on this volume.");

        AssertRejected(Inspect(env, "LONGDI~1/a.ps1"), PluginInspectionStatus.PluginUnavailable, TrustDiagnosticCode.PluginNotFound);
    }

    [Fact]
    public void RootsOverlappingTheTrustStoreAreRejected()
    {
        using var env = TrustTestEnvironment.Create();
        Directory.CreateDirectory(env.StoreDirectory);
        File.WriteAllText(Path.Combine(env.StoreDirectory, "a.ps1"), "1");
        File.WriteAllText(Path.Combine(env.Base, "b.ps1"), "1");

        AssertRejected(Inspect(env, "a.ps1", env.StoreDirectory), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.RootOverlapsTrustStore);
        AssertRejected(Inspect(env, "b.ps1", env.Base), PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.RootOverlapsTrustStore);
        Assert.Equal(PluginTrustStatus.UnsafePath, env.Check(env.Service(), "b.ps1", env.Base.ToUpperInvariant()).Status);
        Assert.Equal(PluginApprovalStatus.UnsafePath, env.Service().Approve(env.Base, PluginId.Create("b.ps1"), ContentFingerprint.Parse(new string('0', 64)), Token).Status);
    }

    [Fact]
    public void StoreInsideAPluginRootIsRejectedForThatRoot()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("a.ps1", "1");
        var service = new PluginTrustService(Path.Combine(env.Root, "trust", "approvals.json"));

        Assert.Equal(PluginTrustStatus.UnsafePath, service.CheckTrust(env.Root, PluginId.Create("a.ps1"), Token).Status);
        Assert.False(Directory.Exists(Path.Combine(env.Root, "trust")));
    }

    [Fact]
    public void DirectoryListingsAreBounded()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("sub/a.ps1", "1");
        for (int i = 0; i < 3; i++)
        {
            env.Write($"sub/other{i}.txt", string.Empty);
        }

        // The root has 1 entry and sub has 4: 5 in total fit, 4 do not.
        PluginInspectionResult fits = Inspect(env, "sub/a.ps1", hooks: new TrustTestHooks { MaxDirectoryEntries = 5 });
        PluginInspectionResult exceeds = Inspect(env, "sub/a.ps1", hooks: new TrustTestHooks { MaxDirectoryEntries = 4 });

        Assert.Equal(PluginInspectionStatus.Inspected, fits.Status);
        AssertRejected(exceeds, PluginInspectionStatus.UnsafePath, TrustDiagnosticCode.DirectoryLimitReached);
        Assert.Equal(PluginDiscoveryOptions.Default.MaxEntries, PluginFileInspector.MaxEntries);
        Assert.Equal(PluginTrustKey.MaxDirectoryDepth, PluginDiscoveryOptions.Default.MaxDepth);
    }

    [Fact]
    public void UnlistableDirectoryIsUnavailable()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("denied/a.ps1", "1");
        env.DenyListing(env.PluginPath("denied"));

        AssertRejected(Inspect(env, "denied/a.ps1"), PluginInspectionStatus.PluginUnavailable, TrustDiagnosticCode.DirectoryUnreadable);
    }

    [Fact]
    public void InspectionReportsTheValidatedKeyWithOriginalSpelling()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("Work/Clock.ps1", "1");

        PluginInspectionResult result = Inspect(env, "Work/Clock.ps1", env.Root + "\\");

        Assert.Equal(env.Root, result.Key!.Root.Value);
        Assert.Equal("Work/Clock.ps1", result.Plugin!.Key.Id.Value);
    }

    [Fact]
    public void SuccessfulInspectionReturnsTheExactOnDiskPath()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("Work/Sub/Clock.ps1", "1");

        PluginInspectionResult result = Inspect(env, "work/SUB/clock.ps1", env.Root.ToUpperInvariant() + "\\");

        // The caller's spelling is kept in the key; the validated path has the spelling found on disk.
        Assert.Equal(PluginInspectionStatus.Inspected, result.Status);
        Assert.Equal("work/SUB/clock.ps1", result.Plugin!.Key.Id.Value);
        Assert.Equal(Path.Combine(env.Root, "Work", "Sub", "Clock.ps1"), result.ValidatedFilePath);
    }

    [Fact]
    public void ExactPathOfTheVisibleFileIsReturnedInACaseSensitiveRoot()
    {
        using var env = TrustTestEnvironment.Create();
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(env.Root), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");
        string visible = env.Write("clock.ps1", "visible");
        string hidden = env.Write("CLOCK.ps1", "hidden twin");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        PluginTrustService service = env.Service();

        PluginInspectionResult inspection = Inspect(env, "CLOCK.ps1");
        PluginApproval approval = env.Approve(service, "CLOCK.ps1");
        PluginTrustDecision decision = env.Check(service, "CLOCK.ps1");

        // Rebuilding a path from the identity would name the hidden twin; the validated path names the
        // visible file that was hashed and approved.
        Assert.Equal(visible, inspection.ValidatedFilePath);
        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
        Assert.Equal(visible, decision.ValidatedFilePath);
        Assert.Equal(hidden, Path.Combine(env.Root, decision.Key!.Id.Value));
        Assert.Equal(Sha256("visible"u8), approval.Fingerprint);
        Assert.Equal(approval.Identity, decision.Approval);
        Assert.Equal(Sha256(File.ReadAllBytes(decision.ValidatedFilePath!)), decision.Approval!.Fingerprint);
        Assert.NotEqual(Sha256(File.ReadAllBytes(hidden)), decision.Approval.Fingerprint);
    }

    [Fact]
    public void ExactPathThroughNestedCaseVariantDirectoriesIsReturned()
    {
        using var env = TrustTestEnvironment.Create();
        Assert.SkipUnless(TemporaryDirectory.TryMakeCaseSensitive(env.Root), "Case-sensitive directories are unavailable (they need the Windows Subsystem for Linux component).");
        string work = MakeCaseSensitiveDirectory(env.PluginPath("Work"));
        string sub = MakeCaseSensitiveDirectory(Path.Combine(work, "Sub"));
        string hiddenWork = MakeCaseSensitiveDirectory(env.PluginPath("WORK"));
        File.WriteAllText(Path.Combine(MakeCaseSensitiveDirectory(Path.Combine(hiddenWork, "Sub")), "Clock.ps1"), "hidden directory");
        File.SetAttributes(hiddenWork, FileAttributes.Directory | FileAttributes.Hidden);
        string visible = Path.Combine(sub, "Clock.ps1");
        File.WriteAllText(visible, "visible");
        string hiddenFile = Path.Combine(sub, "CLOCK.ps1");
        File.WriteAllText(hiddenFile, "hidden twin");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        PluginTrustService service = env.Service();

        PluginInspectionResult inspection = Inspect(env, "work/sub/CLOCK.ps1");
        PluginApproval approval = env.Approve(service, "WORK/SUB/clock.ps1");
        PluginTrustDecision decision = env.Check(service, "work/Sub/CLOCK.ps1");

        Assert.Equal(PluginInspectionStatus.Inspected, inspection.Status);
        Assert.Equal(visible, inspection.ValidatedFilePath);
        Assert.Equal(Sha256("visible"u8), inspection.Plugin!.Fingerprint);
        Assert.Equal(Sha256("visible"u8), approval.Fingerprint);
        Assert.Equal(PluginTrustStatus.Approved, decision.Status);
        Assert.Equal(visible, decision.ValidatedFilePath);
        Assert.Equal(Sha256(File.ReadAllBytes(decision.ValidatedFilePath!)), decision.Approval!.Fingerprint);
    }

    [Fact]
    public void FailedInspectionsAndDecisionsProvideNoPath()
    {
        using var env = TrustTestEnvironment.Create();
        env.Write("pending.ps1", "1");
        env.Write("changed.ps1", "v1");
        PluginTrustService service = env.Service();
        PluginApproval approval = env.Approve(service, "changed.ps1");
        env.Write("changed.ps1", "v2");

        PluginInspectionResult missing = Inspect(env, "missing.ps1");
        PluginTrustDecision pending = env.Check(service, "pending.ps1");
        PluginTrustDecision changed = env.Check(service, "changed.ps1");
        PluginTrustDecision stale = service.CheckTrust(approval.Identity, Token);
        PluginTrustDecision unavailable = env.Check(service, "missing.ps1");

        Assert.Null(missing.ValidatedFilePath);
        foreach (PluginTrustDecision decision in new[] { pending, changed, stale, unavailable })
        {
            Assert.False(decision.IsApproved);
            Assert.Null(decision.Approval);
            Assert.Null(decision.ValidatedFilePath);
        }

        // An inspected but unapproved plugin still reports what was observed, but nothing launchable.
        Assert.NotNull(pending.Current);
    }

    private static ContentFingerprint Sha256(ReadOnlySpan<byte> content) =>
        ContentFingerprint.FromHash(System.Security.Cryptography.SHA256.HashData(content));

    private static string MakeCaseSensitiveDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Assert.True(TemporaryDirectory.TryMakeCaseSensitive(path));
        return path;
    }
}
