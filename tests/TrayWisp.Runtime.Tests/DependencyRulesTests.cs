using System.Reflection;
using Xunit;

namespace TrayWisp.Runtime.Tests;

// Guards the layering rule in docs/architecture.md at the compiled-assembly level.
// Directory.Build.targets enforces the same rule for project references at build time.
public sealed class DependencyRulesTests
{
    [Fact]
    public void RuntimeReferencesNoTrayWispAssemblyOtherThanCore()
    {
        Assembly runtime = Assembly.Load("TrayWisp.Runtime");

        string[] disallowedReferences = runtime.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("TrayWisp", StringComparison.OrdinalIgnoreCase))
            .Where(name => !name.Equals("TrayWisp.Core", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(disallowedReferences);
    }
}
