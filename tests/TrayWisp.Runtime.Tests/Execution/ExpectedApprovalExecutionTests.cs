using System.Runtime.Versioning;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// The internal RunAsync overload the scheduler uses: the unchanged launch-time check, which additionally requires the
// approval the plugin was selected under. Verified with the real trust service and the helper program, observing
// process creation.
[SupportedOSPlatform("windows")]
public sealed class ExpectedApprovalExecutionTests : IDisposable
{
    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    private static CancellationToken Token => ExecutionTestEnvironment.Token;

    private string[] MarkerScript(string name) => [$"write-file {_env.MarkerPath(name)}|ran", "out ok"];

    private Task<PluginExecutionResult> RunExpectingAsync(PluginExecutionService service, string id, PluginApprovalIdentity expected) =>
        service.RunAsync(_env.Root, PluginId.Create(id), runOptions: null, expected, Token);

    private void AssertNotStarted(PluginExecutionResult result, string marker, PluginTrustStatus trustStatus)
    {
        Assert.Equal(PluginExecutionStatus.TrustDenied, result.Status);
        Assert.Equal(trustStatus, result.TrustStatus);
        Assert.Equal(0, _env.ProcessStarts);
        Assert.False(File.Exists(_env.MarkerPath(marker)));
        Assert.Null(result.Approval);
        Assert.Null(result.ValidatedFilePath);
        Assert.Null(result.Output);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == ExecutionDiagnosticCode.TrustDenied);
    }

    // The lease was released: the file can be opened for writing without sharing.
    private static void AssertReleased(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task TheCurrentApprovalAuthorizesTheLaunch()
    {
        PluginApproval approval = _env.Approved("a.ps1", MarkerScript("a"));

        PluginExecutionResult result = await RunExpectingAsync(_env.Service(), "a.ps1", approval.Identity);

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, _env.ProcessStarts);
        Assert.True(File.Exists(_env.MarkerPath("a")));
        Assert.Equal(approval.Identity, result.Approval);
        AssertReleased(_env.Trust.PluginPath("a.ps1"));
    }

    [Fact]
    public async Task AnOlderGenerationOfTheSameBytesStartsNothing()
    {
        PluginApproval first = _env.Approved("a.ps1", MarkerScript("a"));
        _env.TrustService.Revoke(_env.Root, PluginId.Create("a.ps1"), Token);
        PluginApproval second = _env.Trust.Approve(_env.TrustService, "a.ps1");
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        PluginExecutionService service = _env.Service();

        PluginExecutionResult stale = await RunExpectingAsync(service, "a.ps1", first.Identity);
        AssertNotStarted(stale, "a", PluginTrustStatus.StaleApproval);
        Assert.Contains(stale.TrustDiagnostics, diagnostic => diagnostic.Code == TrustDiagnosticCode.StaleApproval);
        AssertReleased(_env.Trust.PluginPath("a.ps1"));

        // The second approval is untouched and authorizes its own launch.
        PluginExecutionResult current = await RunExpectingAsync(service, "a.ps1", second.Identity);
        Assert.Equal(PluginExecutionStatus.Succeeded, current.Status);
        Assert.Equal(second.Identity, current.Approval);
    }

    [Fact]
    public async Task AnExpectedApprovalMustBelongToExactlyThisPlugin()
    {
        PluginApproval a = _env.Approved("a.ps1", MarkerScript("a"));
        PluginApproval b = _env.Approved("b.ps1", MarkerScript("b"));
        PluginExecutionService service = _env.Service();

        PluginExecutionResult other = await RunExpectingAsync(service, "a.ps1", b.Identity);
        AssertNotStarted(other, "a", PluginTrustStatus.StaleApproval);

        // Another root with the same identity is another key as well.
        string otherRoot = Directory.CreateDirectory(Path.Combine(_env.Trust.Base, "other")).FullName;
        var foreign = new PluginApprovalIdentity(new PluginTrustKey(PluginRoot.Create(otherRoot), PluginId.Create("a.ps1")), a.Fingerprint, a.Generation);
        AssertNotStarted(await RunExpectingAsync(service, "a.ps1", foreign), "a", PluginTrustStatus.StaleApproval);

        // Identities are case-insensitive, so the same key spelled differently is still the same approval.
        PluginExecutionResult sameKey = await service.RunAsync(_env.Root.ToUpperInvariant(), PluginId.Create("A.PS1"), runOptions: null, a.Identity, Token);
        Assert.Equal(PluginExecutionStatus.Succeeded, sameKey.Status);
        Assert.Equal(a.Identity, sameKey.Approval);
    }

    [Fact]
    public async Task AChangedFileIsDeniedAndItsApprovalInvalidatedPersistently()
    {
        PluginApproval approval = _env.Approved("a.ps1", MarkerScript("a"));
        _env.Write("a.ps1", [.. MarkerScript("a"), "out changed"]);

        PluginExecutionResult result = await RunExpectingAsync(_env.Service(), "a.ps1", approval.Identity);

        AssertNotStarted(result, "a", PluginTrustStatus.ContentChanged);
        Assert.Contains(result.TrustDiagnostics, diagnostic => diagnostic.Code == TrustDiagnosticCode.ApprovalInvalidated);
        Assert.Equal(PluginTrustStatus.PendingApproval, _env.Trust.Check(_env.Trust.Service(), "a.ps1").Status);
    }

    [Fact]
    public async Task AnUnsavedInvalidationKeepsDenyingTheExpectedApproval()
    {
        string[] original = MarkerScript("a");
        PluginApproval approval = _env.Approved("a.ps1", original);
        _env.Write("a.ps1", [.. original, "out changed"]);
        File.SetAttributes(_env.Trust.StorePath, FileAttributes.ReadOnly);
        _env.Trust.OnDispose(() => File.SetAttributes(_env.Trust.StorePath, FileAttributes.Normal));
        PluginExecutionService service = _env.Service();

        AssertNotStarted(await RunExpectingAsync(service, "a.ps1", approval.Identity), "a", PluginTrustStatus.ContentChanged);
        _env.Write("a.ps1", original);
        AssertNotStarted(await RunExpectingAsync(service, "a.ps1", approval.Identity), "a", PluginTrustStatus.ContentChanged);
    }

    [Fact]
    public async Task ThePublicRunAsyncStillUsesWhicheverApprovalIsCurrent()
    {
        _env.Approved("a.ps1", "out ok");
        _env.TrustService.Revoke(_env.Root, PluginId.Create("a.ps1"), Token);
        PluginApproval second = _env.Trust.Approve(_env.TrustService, "a.ps1");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1");

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal(second.Identity, result.Approval);
    }

    [Fact]
    public void TheOverloadIsInternalAndRequiresAnIdentity()
    {
        Assert.DoesNotContain(
            typeof(PluginExecutionService).GetMethods(),
            method => method.Name == nameof(PluginExecutionService.RunAsync) && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(PluginApprovalIdentity)));
        PluginExecutionService service = _env.Service();
        Assert.Throws<ArgumentNullException>(() => { _ = service.RunAsync(_env.Root, PluginId.Create("a.ps1"), runOptions: null, expectedApproval: null!, Token); });
        Assert.Same(_env.TrustService, service.TrustService);
    }
}
