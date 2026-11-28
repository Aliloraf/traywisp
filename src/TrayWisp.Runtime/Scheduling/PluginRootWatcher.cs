using TrayWisp.Core.Plugins;

namespace TrayWisp.Runtime.Scheduling;

internal enum WatcherChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,

    // Notifications were lost: the watcher's buffer overflowed.
    Overflow,

    // The watcher failed for another reason and must be recreated.
    Failed,
}

// One notification, exactly as the watcher reported it. Names are relative to the root and are hints only: they are never
// opened, launched or trusted.
internal readonly record struct WatcherNotification(WatcherChangeKind Kind, string? Name, string? OldName);

// What a notification means for the scheduler.
internal readonly record struct WatcherRelevance(bool Rescan, PluginId? Plugin, PluginId? OldPlugin);

// A recursive FileSystemWatcher on the plugin root. Its callbacks only forward the notification, which the scheduler records
// with bounded work; nothing is scanned, hashed or launched in a callback. Notifications are hints: the watcher can drop
// them (buffer overflow), report them late, twice or out of order, and does not report changes behind reparse points,
// which discovery does not follow either.
internal sealed class PluginRootWatcher : IDisposable
{
    // The largest buffer Windows accepts for a directory watch.
    private const int BufferSize = 64 * 1024;

    private readonly FileSystemWatcher _watcher;
    private readonly Action<WatcherNotification> _sink;
    private int _disposed;

    private PluginRootWatcher(FileSystemWatcher watcher, Action<WatcherNotification> sink)
    {
        _watcher = watcher;
        _sink = sink;
    }

    public static PluginRootWatcher? TryCreate(string root, Action<WatcherNotification> sink, out string? problem)
    {
        problem = null;
        FileSystemWatcher? watcher = null;
        try
        {
            // Names, directories, content, size and attributes (hidden, reparse, offline). Not last access or security,
            // so reading files, including the scheduler's own trust checks and discovery, causes no notifications.
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = BufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime,
            };
            var result = new PluginRootWatcher(watcher, sink);
            watcher.Created += result.OnCreated;
            watcher.Changed += result.OnChanged;
            watcher.Deleted += result.OnDeleted;
            watcher.Renamed += result.OnRenamed;
            watcher.Error += result.OnError;
            watcher.EnableRaisingEvents = true;
            watcher = null;
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            problem = exception.GetType().Name;
            return null;
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    // Classifies a notification. Pure: no file system access.
    //   - Names in or below a dot-prefixed entry are ignored, as discovery ignores them.
    //   - A content change matters only for a plugin file (a recognized extension); auxiliary files a plugin writes, and
    //     a directory's own timestamp, cause no rescan.
    //   - Creations, deletions and renames always cause a rescan, because the name alone cannot tell a file from a
    //     directory whose renaming changes plugin identities.
    //   - A plugin-file name additionally yields a hint for a targeted trust check of a tracked plugin with that identity.
    public static WatcherRelevance Classify(WatcherNotification notification)
    {
        if (notification.Kind is WatcherChangeKind.Overflow or WatcherChangeKind.Failed)
        {
            return new WatcherRelevance(Rescan: true, Plugin: null, OldPlugin: null);
        }

        bool rescan = false;
        PluginId? plugin = null;
        PluginId? oldPlugin = null;
        Examine(notification.Name, ref rescan, ref plugin);
        if (notification.Kind == WatcherChangeKind.Renamed)
        {
            Examine(notification.OldName, ref rescan, ref oldPlugin);
        }

        return new WatcherRelevance(rescan, plugin, oldPlugin);

        void Examine(string? name, ref bool needsRescan, ref PluginId? hinted)
        {
            if (string.IsNullOrEmpty(name))
            {
                // Unknown name: be conservative.
                needsRescan = true;
                return;
            }

            foreach (Range range in name.AsSpan().SplitAny('\\', '/'))
            {
                if (name.AsSpan()[range].StartsWith('.'))
                {
                    return;
                }
            }

            bool pluginFile = IsPluginFileName(name);
            if (pluginFile && PluginId.TryCreate(name, out PluginId? id))
            {
                hinted = id;
            }

            if (pluginFile || notification.Kind != WatcherChangeKind.Changed)
            {
                needsRescan = true;
            }
        }
    }

    private static bool IsPluginFileName(string name)
    {
        int separator = name.LastIndexOfAny(['\\', '/']);
        string fileName = separator < 0 ? name : name[(separator + 1)..];
        int dot = fileName.LastIndexOf('.');
        return dot > 0 && PluginLanguages.TryClassifyExtension(fileName[(dot + 1)..], out _);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Changed -= OnChanged;
        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Forward(new WatcherNotification(WatcherChangeKind.Created, e.Name, null));

    private void OnChanged(object sender, FileSystemEventArgs e) => Forward(new WatcherNotification(WatcherChangeKind.Changed, e.Name, null));

    private void OnDeleted(object sender, FileSystemEventArgs e) => Forward(new WatcherNotification(WatcherChangeKind.Deleted, e.Name, null));

    private void OnRenamed(object sender, RenamedEventArgs e) => Forward(new WatcherNotification(WatcherChangeKind.Renamed, e.Name, e.OldName));

    private void OnError(object sender, ErrorEventArgs e) =>
        Forward(new WatcherNotification(e.GetException() is InternalBufferOverflowException ? WatcherChangeKind.Overflow : WatcherChangeKind.Failed, null, null));

    private void Forward(WatcherNotification notification)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _sink(notification);
        }
    }
}
