namespace TrayWisp.Cli;

// Turns Ctrl+C into a cancellation request without doing any work on the console's signal thread: the handler marks the
// process as staying alive (e.Cancel) and starts CancelAsync, which runs the token's callbacks on the thread pool. The
// command then finishes normally, so a running plugin's cleanup is awaited, never abandoned. Further Ctrl+C presses are
// ignored. Ctrl+Break is not intercepted: it ends the process at once, and the run's job object (kill on close) then
// terminates the plugin's tracked processes.
//
// CloseAsync ends cancellation before the report is written: no further delivery can start, a started delivery is awaited,
// and a failure of a cancellation callback is observed and returned, never thrown, so it can be reported without hiding
// the command's result. The source is disposed only after the handler has been removed, no further delivery can start,
// and a started delivery has finished, also when it failed. This process ends right afterwards, so the Phase 5
// scheduler's extra WaitHandle disposal is not needed.
internal sealed class ConsoleCancellation : IAsyncDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly Lock _gate = new();
    private Task? _delivery;
    private bool _closed;
    private bool _disposed;

    public CancellationToken Token => _source.Token;

    public void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (e.SpecialKey != ConsoleSpecialKey.ControlC)
        {
            return;
        }

        e.Cancel = true;
        RequestCancellation();
    }

    // Returns whether this call started the cancellation.
    public bool RequestCancellation()
    {
        lock (_gate)
        {
            if (_closed || _delivery is not null)
            {
                return false;
            }

            _delivery = _source.CancelAsync();
            return true;
        }
    }

    // Stops accepting cancellation requests and waits for a started delivery. Returns the failure of a cancellation
    // callback (the exception CancellationTokenSource reports), or null. Never throws; may be called more than once.
    public async Task<Exception?> CloseAsync()
    {
        Task? delivery;
        lock (_gate)
        {
            _closed = true;
            delivery = _delivery;
        }

        if (delivery is null)
        {
            return null;
        }

        try
        {
            await delivery.ConfigureAwait(false);
            return null;
        }
#pragma warning disable CA1031 // A callback's failure is observed and reported by the caller; it must not end finalization.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return exception;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _source.Dispose();
                }
            }
        }
    }
}

// The last steps of the process: release the cancellation source, then flush both streams. Each step runs even if an
// earlier one failed, and none of them replaces the command's exit code (a completed run's status, or 9 for unconfirmed
// cleanup) except a failed flush, which means the report itself was not delivered.
internal static class ProcessFinalization
{
    public static async Task<int> CompleteAsync(ConsoleCancellation cancellation, TextWriter output, TextWriter error, int exitCode)
    {
        try
        {
            await cancellation.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Both streams are flushed even if the first one fails.
            bool outputDelivered = TryFlush(output);
            bool errorDelivered = TryFlush(error);
            if (!outputDelivered || !errorDelivered)
            {
                // A closed pipe: the report could not be delivered.
                exitCode = ExitCodes.Failure;
            }
        }

        return exitCode;
    }

    private static bool TryFlush(TextWriter writer)
    {
        try
        {
            writer.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
