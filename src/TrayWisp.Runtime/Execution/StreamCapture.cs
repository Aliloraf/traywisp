using System.Runtime.InteropServices;

namespace TrayWisp.Runtime.Execution;

// Reads one redirected output stream as raw bytes, with a bounded buffer. See docs/execution-engine.md ("Output capture").
//
// Head mode (standard output): keeps the first `limit` bytes and asks for at most one byte more (the look-ahead that
// tells "exactly the limit" from "more than the limit"). On overflow it reports it, then keeps reading and
// discarding until the stream ends or capture is stopped, so the end of the stream can still be confirmed.
//
// Tail mode (standard error): keeps the last `limit` bytes in a ring buffer and reads until the stream ends.
//
// Limits are enforced on bytes actually read. Nothing is decoded here. The read loop runs on its own background
// thread with synchronous reads, because redirected pipes are synchronous handles: a blocked read occupies that thread,
// never a thread-pool thread. RequestStop interrupts a blocked read on Windows (CancelSynchronousIo). After Freeze,
// nothing more is kept, whatever the loop still receives. The loop never throws: failures are recorded.
internal sealed partial class StreamCapture(int limit, bool keepTail)
{
    internal const int ChunkSize = 16 * 1024;

    private const uint ThreadTerminate = 0x0001;

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] _buffer = [];
    private int _retained;
    private int _ringStart;
    private long _total;
    private bool _frozen;
    private bool _ended;
    private bool _overflowed;
    private bool _stopRequested;
    private bool _started;
    private string? _failure;
    private nint _threadHandle;

    // Completes when the read loop has ended, for whatever reason. Never faults. Never completes if Start was not
    // called or failed (see Started).
    public Task Completion => _completion.Task;

    // The read loop's thread was started.
    public bool Started
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    // The stream reached its end while capture was running.
    public bool ReachedEnd
    {
        get
        {
            lock (_gate)
            {
                return _ended;
            }
        }
    }

    // More bytes were read than the limit (head mode), or than are retained (tail mode).
    public bool Overflowed
    {
        get
        {
            lock (_gate)
            {
                return _overflowed;
            }
        }
    }

    // The type and HResult of a read failure, never its message (which can contain untrusted text).
    public string? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    // Starts the read loop on a new background thread. Calls onOverflow once when the head limit is first exceeded
    // (before onRead for that chunk), onFailure when a read fails, and onEnd when the stream reaches its end; all from
    // the loop's thread, as soon as the event is observed.
    public void Start(Stream stream, Action<long>? onRead, Action onOverflow, Action onFailure, Action? onEnd = null)
    {
        var thread = new Thread(() => Run(stream, onRead, onOverflow, onFailure, onEnd))
        {
            IsBackground = true,
            Name = "TrayWisp output capture",
        };
        thread.Start();
        lock (_gate)
        {
            _started = true;
        }
    }

    // Asks the loop to stop, and on Windows cancels a read it is blocked in. A read that starts just after this call is
    // not cancelled, so callers repeat it until Completion has finished or they give up.
    public void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            if (_threadHandle != 0 && OperatingSystem.IsWindows())
            {
                // Fails harmlessly (ERROR_NOT_FOUND) when no read is pending.
                CancelSynchronousIo(_threadHandle);
            }
        }
    }

    // Stops keeping data and returns what was kept, in stream order.
    public byte[] Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
            return Snapshot();
        }
    }

    private void Run(Stream stream, Action<long>? onRead, Action onOverflow, Action onFailure, Action? onEnd)
    {
        if (OperatingSystem.IsWindows())
        {
            lock (_gate)
            {
                _threadHandle = OpenThread(ThreadTerminate, inheritHandle: false, GetCurrentThreadId());
            }
        }

        try
        {
            if (ReadLoop(stream, onRead, onOverflow))
            {
                onEnd?.Invoke();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or OperationCanceledException)
        {
            bool report;
            lock (_gate)
            {
                // A read cancelled by RequestStop, or failing after Freeze, is a stop, not a failure.
                report = !_frozen && !_stopRequested;
                if (report)
                {
                    _failure = $"{exception.GetType().Name} (HRESULT 0x{exception.HResult:X8})";
                }
            }

            if (report)
            {
                onFailure();
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_threadHandle != 0 && OperatingSystem.IsWindows())
                {
                    CloseHandle(_threadHandle);
                }

                _threadHandle = 0;
            }

            _completion.TrySetResult();
        }
    }

    // Returns true when the stream reached its end while capture was running.
    private bool ReadLoop(Stream stream, Action<long>? onRead, Action onOverflow)
    {
        byte[] chunk = new byte[ChunkSize];
        while (true)
        {
            int wanted;
            lock (_gate)
            {
                if (_frozen || _stopRequested)
                {
                    return false;
                }

                // Never ask for more than one byte past the limit while the output can still be complete.
                wanted = keepTail || _overflowed ? chunk.Length : (int)Math.Min(chunk.Length, limit + 1L - _total);
            }

            int read = stream.Read(chunk, 0, wanted);
            bool overflowNow;
            long total;
            lock (_gate)
            {
                if (_frozen)
                {
                    return false;
                }

                if (read == 0)
                {
                    _ended = true;
                    return true;
                }

                bool wasOverflowed = _overflowed;
                Append(chunk.AsSpan(0, read));
                overflowNow = _overflowed && !wasOverflowed;
                total = _total;
            }

            // The overflow is reported first, so it is recorded at the moment it is detected.
            if (overflowNow && !keepTail)
            {
                onOverflow();
            }

            onRead?.Invoke(total);
        }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        _total += data.Length;
        if (!keepTail)
        {
            int room = limit - _retained;
            if (data.Length > room)
            {
                _overflowed = true;
            }

            int take = Math.Min(room, data.Length);
            if (take > 0)
            {
                EnsureCapacity(_retained + take);
                data[..take].CopyTo(_buffer.AsSpan(_retained));
                _retained += take;
            }

            return;
        }

        if (_total > limit)
        {
            _overflowed = true;
        }

        if (limit == 0)
        {
            return;
        }

        if (_buffer.Length < limit)
        {
            // The ring is allocated once, at its full size, before anything is written to it.
            Array.Resize(ref _buffer, limit);
        }

        if (data.Length >= limit)
        {
            data[^limit..].CopyTo(_buffer);
            _ringStart = 0;
            _retained = limit;
            return;
        }

        // Write at the logical end of the ring, wrapping around, then advance the start past overwritten bytes.
        int end = (_ringStart + _retained) % limit;
        int first = Math.Min(data.Length, limit - end);
        data[..first].CopyTo(_buffer.AsSpan(end));
        data[first..].CopyTo(_buffer);
        int overwritten = Math.Max(0, _retained + data.Length - limit);
        _retained = Math.Min(limit, _retained + data.Length);
        _ringStart = (_ringStart + overwritten) % limit;
    }

    private void EnsureCapacity(int needed)
    {
        if (_buffer.Length < needed)
        {
            Array.Resize(ref _buffer, Math.Max(needed, Math.Min(limit, Math.Max(ChunkSize, _buffer.Length * 2))));
        }
    }

    private byte[] Snapshot()
    {
        if (!keepTail || _ringStart == 0)
        {
            return _buffer.AsSpan(0, _retained).ToArray();
        }

        byte[] ordered = new byte[_retained];
        int first = Math.Min(_retained, limit - _ringStart);
        _buffer.AsSpan(_ringStart, first).CopyTo(ordered);
        _buffer.AsSpan(0, _retained - first).CopyTo(ordered.AsSpan(first));
        return ordered;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelSynchronousIo(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
