using System.Diagnostics;
using System.Reflection;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// The execution boundary: one public entry point, no public way to launch without the trust check, and results that
// only Runtime can create.
public sealed class ExecutionApiTests
{
    private static readonly Assembly Runtime = typeof(PluginExecutionService).Assembly;

    [Fact]
    public void TheExecutionNamespaceExposesOnlyTheDocumentedTypes()
    {
        string[] expected =
        [
            nameof(PluginExecutionOptions),
            nameof(PluginExecutionResult),
            nameof(PluginExecutionService),
            nameof(PluginInterpreter),
            nameof(PluginProcessCleanup),
            nameof(PluginRunOptions),
        ];
        string[] actual = [.. Runtime.GetExportedTypes().Where(type => type.Namespace == "TrayWisp.Runtime.Execution").Select(type => type.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RunAsyncIsTheOnlyPublicWayToRunAPlugin()
    {
        MethodInfo[] methods = [.. typeof(PluginExecutionService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(method => !method.IsSpecialName)];
        MethodInfo run = Assert.Single(methods);
        Assert.Equal(nameof(PluginExecutionService.RunAsync), run.Name);
        Assert.Equal([typeof(string), typeof(PluginId), typeof(PluginRunOptions), typeof(CancellationToken)], run.GetParameters().Select(parameter => parameter.ParameterType));

        // The public constructor requires the host's trust service; nothing accepts a path, command line or interpreter
        // arguments for a run.
        ConstructorInfo constructor = Assert.Single(typeof(PluginExecutionService).GetConstructors());
        Assert.Equal(typeof(PluginTrustService), constructor.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void NoPublicRuntimeMemberExposesProcessesOrStartInformation()
    {
        Type[] forbidden = [typeof(Process), typeof(ProcessStartInfo), typeof(Microsoft.Win32.SafeHandles.SafeProcessHandle), typeof(Microsoft.Win32.SafeHandles.SafeFileHandle)];
        foreach (Type type in Runtime.GetExportedTypes())
        {
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                IEnumerable<Type> used = member switch
                {
                    MethodBase method => method.GetParameters().Select(parameter => parameter.ParameterType).Append((method as MethodInfo)?.ReturnType ?? typeof(void)),
                    PropertyInfo property => [property.PropertyType],
                    FieldInfo field => [field.FieldType],
                    _ => [],
                };
                Assert.DoesNotContain(used, forbidden.Contains);
            }
        }
    }

    [Theory]
    [InlineData(typeof(PluginExecutionResult))]
    [InlineData(typeof(PluginInterpreter))]
    [InlineData(typeof(PluginProcessCleanup))]
    public void ResultTypesCannotBeCreatedOutsideRuntime(Type type)
    {
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod is { IsPublic: true } ? property.SetMethod : null));
    }

    [Fact]
    public void TheExecutionLeaseIsInternal()
    {
        Assert.False(typeof(PluginExecutionLease).IsPublic);
        Assert.DoesNotContain(typeof(PluginTrustService).GetMethods(BindingFlags.Public | BindingFlags.Instance), method => method.Name == nameof(PluginTrustService.CheckTrustForExecution));
    }

    [Fact]
    public void RunOptionsAreValidatedBeforeAnythingHappens()
    {
        var trust = new PluginTrustService(Path.Combine(Path.GetTempPath(), "traywisp-unused-" + Guid.NewGuid().ToString("N"), "approvals.json"));
        var service = new PluginExecutionService(trust);
        PluginId id = PluginId.Create("a.ps1");

        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = service.RunAsync(@"C:\plugins", id, new PluginRunOptions { Timeout = TimeSpan.FromMilliseconds(999) }, token); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = service.RunAsync(@"C:\plugins", id, new PluginRunOptions { Timeout = TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1) }, token); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = service.RunAsync(@"C:\plugins", id, new PluginRunOptions { Timeout = TimeSpan.Zero }, token); });
        Assert.Throws<ArgumentNullException>(() => { _ = service.RunAsync(null!, id, cancellationToken: token); });
        Assert.Throws<ArgumentNullException>(() => { _ = service.RunAsync(@"C:\plugins", null!, cancellationToken: token); });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginExecutionService(trust, new PluginExecutionOptions { DefaultTimeout = TimeSpan.FromHours(1) }));
        Assert.Throws<ArgumentNullException>(() => new PluginExecutionService(null!));
    }

    [Fact]
    public void TheDocumentedLimitsHold()
    {
        Assert.Equal(1024 * 1024, PluginExecutionService.MaxStandardOutputBytes);
        Assert.Equal(64 * 1024, PluginExecutionService.MaxStandardErrorBytes);
        Assert.Equal(64, PluginExecutionService.MaxDiagnostics);
        Assert.Equal(TimeSpan.FromSeconds(5), PluginExecutionService.CleanupGracePeriod);
        Assert.Equal(TimeSpan.FromSeconds(30), PluginExecutionOptions.Default.DefaultTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), PluginExecutionOptions.MinTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), PluginExecutionOptions.MaxTimeout);
    }

    [Fact]
    public void ExecutionDiagnosticCodesStayInTheirRange()
    {
        Assert.All(Enum.GetValues<ExecutionDiagnosticCode>(), code => Assert.InRange((int)code, 600, 699));
        Assert.Equal("Error TW631: x", new ExecutionDiagnostic(PluginDiagnosticSeverity.Error, ExecutionDiagnosticCode.ProcessStartFailed, "x").ToString());
    }

    [Fact]
    public async Task OnOtherPlatformsNothingRuns()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Only meaningful on platforms other than Windows.");
        var trust = new PluginTrustService(@"C:\traywisp-unused\approvals.json");
        PluginExecutionResult result = await new PluginExecutionService(trust).RunAsync(@"C:\plugins", PluginId.Create("a.ps1"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(PluginExecutionStatus.UnsupportedPlatform, result.Status);
    }
}
