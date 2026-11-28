using System.Reflection;
using Xunit;

namespace TrayWisp.Cli.Tests;

// The Ctrl+C handler's resource ownership, and the CLI's layering.
public sealed class ConsoleCancellationAndRulesTests
{
    [Fact]
    public async Task CtrlCRequestsCancellationWithoutRunningCallbacksOnTheSignalThread()
    {
        var cancellation = new ConsoleCancellation();
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        cancellation.Token.Register(() =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        });

        ConsoleCancelEventArgs args = CreateArgs(ConsoleSpecialKey.ControlC);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        cancellation.OnCancelKeyPress(null, args);

        // The handler returned while the callback is still blocked: it ran on another thread.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(args.Cancel);
        Assert.True(cancellation.Token.IsCancellationRequested);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(cancellation.RequestCancellation());

        // Disposal waits for the delivery, and later requests are ignored without touching the disposed source.
        Task disposed = cancellation.DisposeAsync().AsTask();
        Assert.False(disposed.IsCompleted);
        gate.Set();
        await disposed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(cancellation.RequestCancellation());
        cancellation.OnCancelKeyPress(null, CreateArgs(ConsoleSpecialKey.ControlC));
    }

    [Fact]
    public async Task CtrlBreakIsNotIntercepted()
    {
        await using var cancellation = new ConsoleCancellation();
        ConsoleCancelEventArgs args = CreateArgs(ConsoleSpecialKey.ControlBreak);
        cancellation.OnCancelKeyPress(null, args);
        Assert.False(args.Cancel);
        Assert.False(cancellation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposalWithoutCancellationIsImmediate()
    {
        var cancellation = new ConsoleCancellation();
        await cancellation.DisposeAsync();
        Assert.False(cancellation.RequestCancellation());
    }

    [Fact]
    public void TheCliReferencesOnlyCoreAndRuntime()
    {
        Assembly cli = typeof(CliApplication).Assembly;
        Assert.Equal("traywisp", cli.GetName().Name);
        string[] references = [.. cli.GetReferencedAssemblies().Select(r => r.Name ?? string.Empty).Where(n => n.StartsWith("TrayWisp", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
        Assert.Equal(["TrayWisp.Core", "TrayWisp.Runtime"], references);
    }

    [Fact]
    public void TheCliHasNoPublicTypes() => Assert.Empty(typeof(CliApplication).Assembly.GetExportedTypes());

    private static ConsoleCancelEventArgs CreateArgs(ConsoleSpecialKey key) =>
        (ConsoleCancelEventArgs)Activator.CreateInstance(typeof(ConsoleCancelEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, [key], null)!;
}
