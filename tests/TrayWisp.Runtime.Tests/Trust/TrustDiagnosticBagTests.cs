using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Trust;

public sealed class TrustDiagnosticBagTests
{
    [Fact]
    public void KeepsAtMostTheLimitAndReportsTheOverflow()
    {
        var bag = new TrustDiagnosticBag();
        for (int i = 0; i < PluginTrustService.MaxDiagnostics * 3; i++)
        {
            bag.Add(PluginDiagnosticSeverity.Error, TrustDiagnosticCode.FileUnreadable, "x");
        }

        var diagnostics = bag.ToImmutable();

        Assert.Equal(PluginTrustService.MaxDiagnostics, diagnostics.Length);
        Assert.All(diagnostics[..^1], d => Assert.Equal(TrustDiagnosticCode.FileUnreadable, d.Code));
        Assert.Equal(TrustDiagnosticCode.DiagnosticLimitReached, diagnostics[^1].Code);
    }

    [Fact]
    public void KeepsEverythingBelowTheLimit()
    {
        var bag = new TrustDiagnosticBag();
        for (int i = 0; i < PluginTrustService.MaxDiagnostics - 1; i++)
        {
            bag.Add(PluginDiagnosticSeverity.Info, TrustDiagnosticCode.ApprovalPending, "x");
        }

        Assert.All(bag.ToImmutable(), d => Assert.Equal(TrustDiagnosticCode.ApprovalPending, d.Code));
        Assert.Equal(PluginTrustService.MaxDiagnostics - 1, bag.ToImmutable().Length);
    }
}
