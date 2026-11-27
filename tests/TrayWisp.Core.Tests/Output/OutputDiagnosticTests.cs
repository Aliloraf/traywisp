using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests.Output;

public sealed class OutputDiagnosticTests
{
    [Fact]
    public void ToStringIncludesCodeLineAndColumn()
    {
        Assert.Equal(
            "Warning TW434 3:12: Unknown.",
            new OutputDiagnostic(PluginDiagnosticSeverity.Warning, OutputDiagnosticCode.UnknownParameter, "Unknown.") { Line = 3, Column = 12 }.ToString());
        Assert.Equal(
            "Error TW412 7: Orphan.",
            new OutputDiagnostic(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.OrphanedMenuNode, "Orphan.") { Line = 7 }.ToString());
        Assert.Equal(
            "Error TW400: Too long.",
            new OutputDiagnostic(PluginDiagnosticSeverity.Error, OutputDiagnosticCode.OutputTooLong, "Too long.").ToString());
    }

    [Fact]
    public void ParserDiagnosticsFormatAsSingleSafeLines()
    {
        PluginOutputParseResult result = PluginOutputParser.Parse("---\nItem | \u202Ename=1 x\u001B=1\nA\u2028 | k" + new string('x', 70) + "=1");

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            string text = diagnostic.ToString();
            Assert.StartsWith($"{diagnostic.Severity} TW{(int)diagnostic.Code} ", text, StringComparison.Ordinal);
            Assert.DoesNotContain(text, c => char.IsControl(c) || c is '\u2028' or '\u2029' or '\u202E');
            Assert.InRange(text.Length, 1, 400);
        });
    }

    [Fact]
    public void CodesAreUniqueAndSeparateFromPluginDiagnosticCodes()
    {
        int[] values = [.. Enum.GetValues<OutputDiagnosticCode>().Select(code => (int)code)];

        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.All(values, value => Assert.InRange(value, 400, 499));
        Assert.Empty(values.Intersect(Enum.GetValues<PluginDiagnosticCode>().Select(code => (int)code)));
    }
}
