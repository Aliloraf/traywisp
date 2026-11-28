using System.Reflection;
using TrayWisp.Core.Output;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Scheduling;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// The scheduling boundary: a small public API, no public seam, no unchecked launch path, and snapshots that expose
// publishable output in exactly one place.
public sealed class SchedulerApiTests
{
    private static readonly Assembly Runtime = typeof(PluginScheduler).Assembly;

    private static readonly Type[] SnapshotTypes =
    [
        typeof(PluginSchedulerSnapshot),
        typeof(ScheduledPlugin),
        typeof(PluginRunSummary),
        typeof(PublishedPluginOutput),
        typeof(SchedulerDiscoveryState),
        typeof(PluginCleanupBlock),
        typeof(SchedulerStopResult),
    ];

    [Fact]
    public void TheSchedulingNamespaceExposesOnlyTheDocumentedTypes()
    {
        string[] expected =
        [
            nameof(PluginCleanupBlock),
            nameof(PluginRunSummary),
            nameof(PluginScheduler),
            nameof(PluginSchedulerOptions),
            nameof(PluginSchedulerSnapshot),
            nameof(PublishedPluginOutput),
            nameof(ScheduledPlugin),
            nameof(SchedulerDiscoveryState),
            nameof(SchedulerStopResult),
        ];
        string[] actual = [.. Runtime.GetExportedTypes().Where(type => type.Namespace == "TrayWisp.Runtime.Scheduling").Select(type => type.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TheSchedulerIsBuiltOnlyFromAnExecutionServiceAndARoot()
    {
        ConstructorInfo constructor = Assert.Single(typeof(PluginScheduler).GetConstructors());
        Assert.Equal([typeof(PluginExecutionService), typeof(string), typeof(PluginSchedulerOptions)], constructor.GetParameters().Select(parameter => parameter.ParameterType));

        // No public member accepts a trust service, operations, delegates, paths to launch or processes.
        foreach (MethodBase method in typeof(PluginScheduler).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OfType<MethodBase>())
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.False(typeof(Delegate).IsAssignableFrom(parameter.ParameterType), $"{method.Name}({parameter.Name})");
                Assert.NotEqual(typeof(PluginTrustService), parameter.ParameterType);
                Assert.NotEqual(typeof(PluginExecutionResult), parameter.ParameterType);
            }
        }

        string[] methods = [.. typeof(PluginScheduler).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(method => !method.IsSpecialName).Select(method => method.Name).Distinct().Order(StringComparer.Ordinal)];
        Assert.Equal(["DisposeAsync", "NotifyTrustChanged", "RequestReconciliation", "RequestRefresh", "RequestRescan", "SetEnabled", "StartAsync", "StopAsync", "WaitForChangeAsync"], methods);
        Assert.False(typeof(SchedulerOperations).IsPublic);
        Assert.False(typeof(SchedulerTestHooks).IsPublic);
    }

    [Fact]
    public void SnapshotTypesCannotBeCreatedOrChangedOutsideRuntime()
    {
        foreach (Type type in SnapshotTypes)
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod is { IsPublic: true } ? property.SetMethod : null));
        }
    }

    [Fact]
    public void PublishableOutputIsReachableOnlyThroughScheduledPluginOutput()
    {
        // Run summaries carry no output, no execution result and no approval identity; nothing else carries output.
        Type[] forbidden = [typeof(PluginOutput), typeof(PluginExecutionResult), typeof(PluginApprovalIdentity), typeof(OutputAction), typeof(PluginDiscoveryResult)];
        foreach (Type type in SnapshotTypes.Where(type => type != typeof(PublishedPluginOutput)))
        {
            foreach (PropertyInfo property in type.GetProperties())
            {
                Assert.DoesNotContain(property.PropertyType, forbidden);
                if (property.PropertyType == typeof(PublishedPluginOutput))
                {
                    Assert.Equal((typeof(ScheduledPlugin), nameof(ScheduledPlugin.Output)), (type, property.Name));
                }
            }
        }

        Assert.Equal(
            [nameof(PublishedPluginOutput.Approval), nameof(PublishedPluginOutput.Output), nameof(PublishedPluginOutput.PublishedAtUtc), nameof(PublishedPluginOutput.RunId)],
            typeof(PublishedPluginOutput).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ConstructionValidatesItsArgumentsWithoutTouchingTheFileSystem()
    {
        var trust = new PluginTrustService(@"C:\traywisp-unused\store\approvals.json");
        var execution = new PluginExecutionService(trust);
        Assert.Throws<ArgumentNullException>(() => new PluginScheduler(null!, @"C:\plugins"));
        Assert.Throws<ArgumentNullException>(() => new PluginScheduler(execution, null!));
        Assert.Throws<ArgumentException>(() => new PluginScheduler(execution, "plugins"));
        Assert.Throws<ArgumentException>(() => new PluginScheduler(execution, @"\\server\share\plugins"));
        Assert.Throws<ArgumentException>(() => new PluginScheduler(execution, @"C:\traywisp-unused"));
        Assert.Throws<ArgumentException>(() => new PluginScheduler(execution, @"C:\traywisp-unused\store\plugins"));

        PluginSchedulerOptions[] invalid =
        [
            new() { MaxConcurrency = 0 },
            new() { MaxConcurrency = 17 },
            new() { WatcherDebounce = TimeSpan.FromTicks(-1) },
            new() { WatcherDebounce = TimeSpan.FromSeconds(11), WatcherMaxDelay = TimeSpan.FromSeconds(20) },
            new() { WatcherMaxDelay = TimeSpan.FromMilliseconds(100) },
            new() { WatcherMaxDelay = TimeSpan.FromSeconds(61) },
            new() { ReconciliationInterval = TimeSpan.FromMilliseconds(999) },
            new() { ReconciliationInterval = TimeSpan.FromHours(2) },
            new() { ShutdownTimeout = TimeSpan.FromTicks(-1) },
            new() { ShutdownTimeout = TimeSpan.FromMinutes(6) },
            new() { RunOptions = new PluginRunOptions { Timeout = TimeSpan.FromMilliseconds(500) } },
            new() { RunOptions = new PluginRunOptions { Timeout = TimeSpan.FromMinutes(6) } },
        ];
        foreach (PluginSchedulerOptions options in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PluginScheduler(execution, @"C:\plugins", options));
        }

        PluginScheduler valid = new(execution, @"c:/Plugins/", new PluginSchedulerOptions { MaxConcurrency = 16, WatcherDebounce = TimeSpan.Zero, WatcherMaxDelay = TimeSpan.Zero, ShutdownTimeout = TimeSpan.Zero });
        Assert.Equal(PluginRoot.Create(@"C:\plugins"), valid.Root);
        Assert.Equal(Core.Scheduling.SchedulerLifecycle.NotStarted, valid.Snapshot.Lifecycle);
        Assert.False(Directory.Exists(@"C:\traywisp-unused"));
    }

    [Fact]
    public async Task WaitingRequiresARevisionOfThisScheduler()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.ps1");
        long current = harness.Snapshot.Revision;

        // Older or equal: at once, or the next change. Larger: it cannot come from this scheduler, and waiting for the next
        // change would return a snapshot that is not larger than it. Rejected synchronously, as an argument error.
        Assert.Equal(current, (await harness.Scheduler.WaitForChangeAsync(current - 1, SchedulerHarness.Token)).Revision);
        Assert.Equal(current, (await harness.Scheduler.WaitForChangeAsync(long.MinValue, SchedulerHarness.Token)).Revision);
        Assert.False(harness.Scheduler.WaitForChangeAsync(current, SchedulerHarness.Token).IsCompleted);
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = harness.Scheduler.WaitForChangeAsync(current + 1, SchedulerHarness.Token); });
        Assert.Equal("afterRevision", error.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = harness.Scheduler.WaitForChangeAsync(long.MaxValue, SchedulerHarness.Token); });
    }

    [Fact]
    public async Task SnapshotsAfterShutdownDoNotImplyThatExecutionContinues()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { MaxConcurrency = 1, ShutdownTimeout = TimeSpan.FromSeconds(5) });
        await harness.StartAsync("a.1s.ps1", "b.1s.ps1");
        Assert.Equal(PluginActivity.Queued, harness.Plugin("b.1s.ps1").Activity);
        Assert.NotNull(harness.Plugin("b.1s.ps1").NextRefreshUtc);

        Task<SchedulerStopResult> stop = harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.All(harness.Snapshot.Plugins, plugin =>
        {
            Assert.NotEqual(PluginActivity.Queued, plugin.Activity);
            Assert.Null(plugin.NextRefreshUtc);
            Assert.False(plugin.ManualRefreshPending);
        });
        Assert.Null(harness.Snapshot.Discovery.NextReconciliationUtc);
        Assert.True((await stop).IsComplete);
    }

    [Fact]
    public void TheDocumentedDefaultsAndLimitsHold()
    {
        PluginSchedulerOptions defaults = PluginSchedulerOptions.Default;
        Assert.Equal(4, defaults.MaxConcurrency);
        Assert.Equal(TimeSpan.FromMilliseconds(250), defaults.WatcherDebounce);
        Assert.Equal(TimeSpan.FromSeconds(1), defaults.WatcherMaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.ReconciliationInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), defaults.ShutdownTimeout);
        Assert.Null(defaults.RunOptions);
        Assert.Equal(1, PluginSchedulerOptions.MinConcurrency);
        Assert.Equal(16, PluginSchedulerOptions.MaxConcurrencyLimit);
        Assert.Equal(500, PluginScheduler.MaxTrackedPlugins);
        Assert.Equal(PluginScheduler.MaxTrackedPlugins, PluginDiscoveryOptions.Default.MaxCandidates);
        Assert.Equal(16, PluginScheduler.MaxCleanupIncidents);
        Assert.Equal(256, PluginScheduler.MaxWatcherHints);
        Assert.Equal(64, PluginScheduler.MaxDiagnostics);
        Assert.Equal(8, PluginScheduler.MaxRunDiagnostics);
        Assert.Equal(64, PluginScheduler.MaxDiscoveryDiagnostics);
        Assert.Equal(32L * 1024 * 1024, PluginScheduler.MaxRetainedOutputCost);
        Assert.Equal(TimeSpan.FromSeconds(5), PluginScheduler.IncompleteScanRetryDelay);
        Assert.True(defaults.ShutdownTimeout > PluginExecutionService.CleanupGracePeriod);
    }
}
