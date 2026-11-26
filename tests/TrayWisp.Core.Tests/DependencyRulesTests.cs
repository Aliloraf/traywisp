using System.Reflection;
using Xunit;

namespace TrayWisp.Core.Tests;

// Guards the layering rule in docs/architecture.md at the compiled-assembly level.
// Directory.Build.targets enforces the same rule for project references at build time.
public sealed class DependencyRulesTests
{
    [Fact]
    public void CoreDoesNotReferenceOtherTrayWispAssemblies()
    {
        Assembly core = Assembly.Load("TrayWisp.Core");

        string[] trayWispReferences = core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("TrayWisp", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(trayWispReferences);
    }
}
