using System.Collections.Immutable;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Runtime.Plugins;

/// <summary>
/// Finds candidate plugin files under a plugin root directory and reads their embedded metadata.
/// Discovery is read-only: it never executes, creates, modifies or deletes anything, and it opens
/// files only for reading a bounded prefix. See docs/plugin-format.md for the rules.
/// </summary>
public static class PluginDiscovery
{
    // Attribute bits that Windows sets on placeholders and on files kept by remote or hierarchical
    // storage. Reading such a file can make Windows fetch it. Only Offline has a .NET name.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes Pinned = (FileAttributes)0x0008_0000;
    private const FileAttributes Unpinned = (FileAttributes)0x0010_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;
    internal const FileAttributes RemoteStorage = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;
    internal const FileAttributes CloudSync = RemoteStorage | Pinned | Unpinned;

    // Hidden and system entries are returned so that the skip rules below are applied explicitly.
    private static readonly EnumerationOptions ListingOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault,
    };

    // How an entry of a directory listing is handled. Only Directory and Candidate entries take part
    // in collision detection, because only they can produce plugin identities.
    internal enum EntryKind
    {
        DotPrefixed,
        ReparsePoint,
        RemoteStorage,
        Hidden,
        InvalidDirectoryName,
        Directory,
        Candidate,
        RejectedFileName,
        NotCandidate,
    }

    /// <summary>Scans <paramref name="rootPath"/> for plugins.</summary>
    /// <param name="rootPath">A fully qualified directory path.</param>
    /// <param name="options">Traversal limits; <see cref="PluginDiscoveryOptions.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the scan by throwing <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is empty, not fully qualified, or contains a NUL character.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    public static PluginDiscoveryResult Discover(string rootPath, PluginDiscoveryOptions? options = null, CancellationToken cancellationToken = default) =>
        Discover(rootPath, options, directoryListed: null, cancellationToken);

    // Test hooks. directoryListed is called with each directory's relative path (null for the root)
    // after its complete listing has been re-checked and before its entries are processed; it allows
    // deterministic tests of cancellation and of entries changing during a scan. entryEnumerated is
    // called with the directory's relative path and the entry name for every entry the file system
    // enumerator returns, including the look-ahead entry, so tests can observe the actual enumeration.
    internal static PluginDiscoveryResult Discover(
        string rootPath,
        PluginDiscoveryOptions? options,
        Action<string?>? directoryListed,
        CancellationToken cancellationToken,
        Action<string?, string>? entryEnumerated = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The plugin root must be a fully qualified path.", nameof(rootPath));
        }

        options ??= PluginDiscoveryOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxDepth, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxDepth, PluginDiscoveryOptions.MaxDepthLimit, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxEntries, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxCandidates, nameof(options));
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var scan = new Scan(options, directoryListed, entryEnumerated, cancellationToken);
        scan.Run(root);
        cancellationToken.ThrowIfCancellationRequested();
        return scan.ToResult(root);
    }

    /// <summary>
    /// Reads at most <paramref name="buffer"/>.Length bytes from the start of a file, sharing it with
    /// writers and deleters. Returns a diagnostic instead of throwing when the file cannot be read.
    /// </summary>
    internal static PluginDiagnostic? ReadPrefix(string fullPath, string relativePath, byte[] buffer, out int length)
    {
        length = 0;
        try
        {
            using var stream = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 0,
                Options = FileOptions.SequentialScan,
            });
            length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Diagnostic(PluginDiagnosticSeverity.Warning, PluginDiagnosticCode.FileDisappeared, "The file was removed before it could be read.", relativePath);
        }
        catch (UnauthorizedAccessException)
        {
            return Diagnostic(PluginDiagnosticSeverity.Error, PluginDiagnosticCode.FileUnreadable, "Access to the file was denied.", relativePath);
        }
        catch (IOException exception)
        {
            return Diagnostic(PluginDiagnosticSeverity.Error, PluginDiagnosticCode.FileUnreadable, $"The file could not be read: {Quote(exception.Message)}", relativePath);
        }
    }

    /// <summary>
    /// Lists one directory without recursing. At most <paramref name="maxEntries"/> entries (which may be
    /// zero) are enumerated and retained in <paramref name="entries"/>. If the directory has more, one
    /// further entry is enumerated as a look-ahead, not retained, and <paramref name="truncated"/> is set.
    /// Returns a diagnostic instead of throwing when the directory cannot be listed; <paramref name="entries"/>
    /// then holds the entries enumerated before the failure, which count against the budget but must not be used.
    /// </summary>
    internal static PluginDiagnostic? ListDirectory(
        string fullPath,
        string? relativePath,
        int maxEntries,
        CancellationToken cancellationToken,
        out List<FileSystemInfo> entries,
        out bool truncated,
        Action<string>? entryEnumerated = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntries);
        string description = relativePath is null ? "The plugin root" : "The directory";
        entries = [];
        truncated = false;
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", ListingOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryEnumerated?.Invoke(entry.Name);

                // Comparing the retained count with the budget, rather than adding one to the budget,
                // cannot overflow when the budget is int.MaxValue.
                if (entries.Count >= maxEntries)
                {
                    truncated = true;
                    break;
                }

                entries.Add(entry);
            }

            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return Unreadable($"{description} was removed before it could be listed.");
        }
        catch (UnauthorizedAccessException)
        {
            return Unreadable($"{description} could not be listed: access was denied.");
        }
        catch (IOException exception)
        {
            return Unreadable($"{description} could not be listed: {Quote(exception.Message)}");
        }

        PluginDiagnostic Unreadable(string message) =>
            Diagnostic(PluginDiagnosticSeverity.Warning, PluginDiagnosticCode.DirectoryUnreadable, message, relativePath);
    }

    /// <summary>
    /// Finds entries of one sorted directory listing whose names are equal ignoring case and that are
    /// of the same participating kind (two directories, or two candidate files). Every identity
    /// collision in a tree shows up this way in the listing where the colliding paths first differ.
    /// </summary>
    /// <returns>For each entry index, the index of the first member of its collision group, or -1.</returns>
    internal static int[] FindCollisions(IReadOnlyList<string> sortedNames, IReadOnlyList<EntryKind> kinds)
    {
        int[] groupStart = new int[sortedNames.Count];
        Array.Fill(groupStart, -1);

        // The listing is sorted case-insensitively first, so names that are equal ignoring case are adjacent.
        int runStart = 0;
        for (int i = 1; i <= sortedNames.Count; i++)
        {
            if (i < sortedNames.Count && string.Equals(sortedNames[i], sortedNames[runStart], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (EntryKind kind in (ReadOnlySpan<EntryKind>)[EntryKind.Directory, EntryKind.Candidate])
            {
                int first = -1;
                int count = 0;
                for (int j = runStart; j < i; j++)
                {
                    if (kinds[j] == kind)
                    {
                        first = first < 0 ? j : first;
                        count++;
                    }
                }

                for (int j = runStart; count > 1 && j < i; j++)
                {
                    if (kinds[j] == kind)
                    {
                        groupStart[j] = first;
                    }
                }
            }

            runStart = i;
        }

        return groupStart;
    }

    internal static EntryKind Classify(FileSystemInfo entry, out PluginFileNameParseResult? fileName)
    {
        fileName = null;
        FileAttributes attributes = entry.Attributes;
        if (entry.Name.StartsWith('.'))
        {
            return EntryKind.DotPrefixed;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return EntryKind.ReparsePoint;
        }

        if ((attributes & RemoteStorage) != 0)
        {
            return EntryKind.RemoteStorage;
        }

        if ((attributes & FileAttributes.Hidden) != 0)
        {
            return EntryKind.Hidden;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return PluginId.IsValidSegment(entry.Name) ? EntryKind.Directory : EntryKind.InvalidDirectoryName;
        }

        fileName = PluginFileNameParser.Parse(entry.Name);
        return fileName.Status switch
        {
            PluginFileNameParseStatus.Parsed => EntryKind.Candidate,
            PluginFileNameParseStatus.Rejected => EntryKind.RejectedFileName,
            _ => EntryKind.NotCandidate,
        };
    }

    private static string Quote(string text) => PluginDiagnosticText.Quote(text, 200);

    private static PluginDiagnostic Diagnostic(PluginDiagnosticSeverity severity, PluginDiagnosticCode code, string message, string? path) =>
        new(severity, code, message) { Path = path };

    private sealed class Scan(
        PluginDiscoveryOptions options,
        Action<string?>? directoryListed,
        Action<string?, string>? entryEnumerated,
        CancellationToken cancellationToken)
    {
        private const int MaxNamesInCollisionMessage = 5;

        private readonly List<PluginDiagnostic> _diagnostics = [];
        private readonly List<PluginDescriptor> _plugins = [];
        private readonly byte[] _buffer = new byte[PluginMetadataParser.MaxScanBytes + 1];
        // Entries enumerated so far, never more than MaxEntries. The look-ahead entry is not counted.
        private int _entriesInspected;
        private int _candidates;

        // Set when the candidate limit is reached: nothing more is processed.
        private bool _stopped;

        // Set when a listing was truncated by the entry budget: no further directory is listed, but
        // entries of listings that were already complete are still processed.
        private bool _entryBudgetExhausted;
        private bool _complete = true;

        public void Run(string root)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(root);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                Fail(PluginDiagnosticCode.RootNotFound, $"The plugin root {Quote(root)} does not exist.");
                return;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Fail(PluginDiagnosticCode.DirectoryUnreadable, $"The plugin root {Quote(root)} could not be examined: {Quote(exception.Message)}");
                return;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                Fail(PluginDiagnosticCode.RootNotDirectory, $"The plugin root {Quote(root)} is not a directory.");
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                string hint = (attributes & CloudSync) != 0
                    ? " It appears to be synchronized by a cloud storage provider such as OneDrive; use a local folder."
                    : string.Empty;
                Fail(PluginDiagnosticCode.RootIsReparsePoint, $"The plugin root {Quote(root)} is a symbolic link, junction or other reparse point and was not scanned.{hint}");
                return;
            }

            if ((attributes & RemoteStorage) != 0)
            {
                Fail(PluginDiagnosticCode.RemoteStorageSkipped, $"The plugin root {Quote(root)} is offline or stored remotely; listing it could trigger a download, so it was not scanned.");
                return;
            }

            // The root's attributes were checked just above, so VisitDirectory does not check them again
            // before listing it.
            VisitDirectory(root, relativePath: null, depth: 0);
        }

        public PluginDiscoveryResult ToResult(string root)
        {
            // Identities are unique here (collisions were excluded per listing), so this order is total.
            _plugins.Sort((left, right) => left.Id.CompareTo(right.Id));
            return new PluginDiscoveryResult(root, [.. _plugins], [.. _diagnostics], _complete, _entriesInspected);
        }

        private void VisitDirectory(string fullPath, string? relativePath, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entryBudgetExhausted)
            {
                // Already reported by the EntryLimitReached diagnostic of the truncated listing.
                return;
            }

            // If the directory was replaced by a link or a file after its parent was listed, do not
            // enumerate it at all.
            if (relativePath is not null && !StillOrdinary(fullPath, relativePath, expectDirectory: true, afterUse: false))
            {
                return;
            }

            // _entriesInspected never exceeds MaxEntries, so the remaining budget is non-negative.
            Action<string>? enumerated = entryEnumerated is null ? null : name => entryEnumerated(relativePath, name);
            PluginDiagnostic? listingError = ListDirectory(
                fullPath,
                relativePath,
                options.MaxEntries - _entriesInspected,
                cancellationToken,
                out List<FileSystemInfo> entries,
                out bool truncated,
                enumerated);
            _entriesInspected += entries.Count;
            if (listingError is not null)
            {
                // Failing to list the root means nothing was scanned, so it is an error.
                Incomplete(relativePath is null ? listingError with { Severity = PluginDiagnosticSeverity.Error } : listingError);
                return;
            }

            if (truncated)
            {
                // An entry that was not seen could be a case variant of one that was, so no identity
                // from this listing can be shown to be unambiguous. Discard the whole listing.
                _entryBudgetExhausted = true;
                Incomplete(Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.EntryLimitReached,
                    $"The limit of {options.MaxEntries} directory entries was reached while listing this directory. "
                    + "Its listing is incomplete, so none of its entries were used, and no further directories were listed.",
                    relativePath));
                return;
            }

            // If the directory was replaced by a link while it was being listed, the listing may
            // describe another location. Discard it.
            if (!StillOrdinary(fullPath, relativePath, expectDirectory: true, afterUse: true))
            {
                return;
            }

            directoryListed?.Invoke(relativePath);
            cancellationToken.ThrowIfCancellationRequested();

            entries.Sort(static (left, right) =>
            {
                int result = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                return result != 0 ? result : string.CompareOrdinal(left.Name, right.Name);
            });

            var kinds = new EntryKind[entries.Count];
            var fileNames = new PluginFileNameParseResult?[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                kinds[i] = Classify(entries[i], out fileNames[i]);
            }

            int[] collisions = FindCollisions(entries.ConvertAll(entry => entry.Name), kinds);

            for (int i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_stopped)
                {
                    return;
                }

                string relativeEntryPath = relativePath is null ? entries[i].Name : relativePath + "/" + entries[i].Name;

                if (collisions[i] >= 0)
                {
                    if (collisions[i] == i)
                    {
                        ReportCollision(entries, kinds, collisions, i, relativePath);
                    }

                    continue;
                }

                VisitEntry(entries[i], kinds[i], fileNames[i], relativeEntryPath, depth);
            }
        }

        private void VisitEntry(FileSystemInfo entry, EntryKind kind, PluginFileNameParseResult? fileName, string relativePath, int depth)
        {
            switch (kind)
            {
                case EntryKind.ReparsePoint:
                    string hint = (entry.Attributes & CloudSync) != 0
                        ? " It appears to be a cloud storage placeholder (for example OneDrive Files On-Demand)."
                        : string.Empty;
                    _diagnostics.Add(Diagnostic(
                        PluginDiagnosticSeverity.Info,
                        PluginDiagnosticCode.ReparsePointSkipped,
                        "Symbolic links, junctions and other reparse points are not followed." + hint,
                        relativePath));
                    return;

                case EntryKind.RemoteStorage:
                    _diagnostics.Add(Diagnostic(
                        PluginDiagnosticSeverity.Info,
                        PluginDiagnosticCode.RemoteStorageSkipped,
                        "The entry is offline or stored remotely; reading it could trigger a download, so it was skipped.",
                        relativePath));
                    return;

                case EntryKind.InvalidDirectoryName:
                    _diagnostics.Add(Diagnostic(
                        PluginDiagnosticSeverity.Warning,
                        PluginDiagnosticCode.InvalidPathSegment,
                        "The directory name cannot be part of a plugin identity, so the directory was not scanned.",
                        relativePath));
                    return;

                case EntryKind.Directory when depth + 1 > options.MaxDepth:
                    Incomplete(Diagnostic(
                        PluginDiagnosticSeverity.Warning,
                        PluginDiagnosticCode.DepthLimitReached,
                        $"The directory is deeper than the maximum depth of {options.MaxDepth} and was not scanned.",
                        relativePath));
                    return;

                case EntryKind.Directory:
                    VisitDirectory(entry.FullName, relativePath, depth + 1);
                    return;

                case EntryKind.RejectedFileName:
                    _diagnostics.Add(fileName!.Diagnostic! with { Path = relativePath });
                    return;

                case EntryKind.Candidate:
                    VisitCandidate(entry.FullName, fileName!.FileName!, fileName.Diagnostic, relativePath);
                    return;

                default:
                    // Dot-prefixed, hidden and unrelated entries are skipped silently.
                    return;
            }
        }

        private void VisitCandidate(string fullPath, PluginFileName fileName, PluginDiagnostic? fileNameNote, string relativePath)
        {
            if (!PluginId.TryCreate(relativePath, out PluginId? id))
            {
                _diagnostics.Add(Diagnostic(PluginDiagnosticSeverity.Warning, PluginDiagnosticCode.InvalidPathSegment, "The path cannot form a plugin identity.", relativePath));
                return;
            }

            if (_candidates >= options.MaxCandidates)
            {
                Stop(PluginDiagnosticCode.CandidateLimitReached, $"Discovery stopped after finding {options.MaxCandidates} candidate plugins.");
                return;
            }

            // If the file was replaced by a link, a directory or a remote-storage placeholder after it
            // was listed, do not open it.
            if (!StillOrdinary(fullPath, relativePath, expectDirectory: false, afterUse: false))
            {
                return;
            }

            _candidates++;
            PluginDiagnostic? readError = ReadPrefix(fullPath, relativePath, _buffer, out int length);
            if (readError is not null)
            {
                // The plugin exists but its state is unknown, so the result cannot be complete.
                Incomplete(readError);
                return;
            }

            // If the file was replaced by a link before it was opened, the bytes may come from
            // another file. Discard them.
            if (!StillOrdinary(fullPath, relativePath, expectDirectory: false, afterUse: true))
            {
                return;
            }

            bool truncated = length > PluginMetadataParser.MaxScanBytes;
            PluginMetadataParseResult metadata = PluginMetadataParser.Parse(
                _buffer.AsSpan(0, Math.Min(length, PluginMetadataParser.MaxScanBytes)),
                fileName.Language,
                truncated);

            IEnumerable<PluginDiagnostic> diagnostics = fileNameNote is null ? metadata.Diagnostics : metadata.Diagnostics.Prepend(fileNameNote);
            _plugins.Add(new PluginDescriptor(id, fileName, metadata.Metadata, [.. diagnostics.Select(diagnostic => diagnostic with { Path = relativePath })]));
        }

        // Re-reads an entry's attributes immediately before it is listed or opened (afterUse false) and
        // again after it was listed or read (afterUse true). Returns false, recording a diagnostic, if it
        // has become a reparse point or a remote-storage entry, changed kind, or disappeared. This
        // narrows, but does not close, the window in which an entry can be swapped.
        private bool StillOrdinary(string fullPath, string? relativePath, bool expectDirectory, bool afterUse)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                Incomplete(expectDirectory
                    ? Diagnostic(PluginDiagnosticSeverity.Warning, PluginDiagnosticCode.DirectoryUnreadable, "The directory was removed or became inaccessible during the scan.", relativePath)
                    : Diagnostic(PluginDiagnosticSeverity.Warning, PluginDiagnosticCode.FileDisappeared, "The file was removed or became inaccessible during the scan.", relativePath));
                return false;
            }

            string consequence = afterUse
                ? "what was read from it was discarded."
                : (expectDirectory ? "it was not listed." : "it was not opened.");
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || isDirectory != expectDirectory)
            {
                Incomplete(Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.ReparsePointSkipped,
                    "The entry changed into a link, reparse point or different kind of entry during the scan; " + consequence,
                    relativePath));
                return false;
            }

            if ((attributes & RemoteStorage) != 0)
            {
                Incomplete(Diagnostic(
                    PluginDiagnosticSeverity.Warning,
                    PluginDiagnosticCode.RemoteStorageSkipped,
                    "The entry became offline or stored remotely during the scan; " + consequence,
                    relativePath));
                return false;
            }

            return true;
        }

        private void ReportCollision(List<FileSystemInfo> entries, EntryKind[] kinds, int[] collisions, int first, string? parentRelativePath)
        {
            string[] names = [.. Enumerable.Range(first, entries.Count - first).Where(i => collisions[i] == first).Select(i => entries[i].Name)];
            string listed = string.Join(", ", names.Take(MaxNamesInCollisionMessage).Select(name => PluginDiagnosticText.Quote(name)));
            if (names.Length > MaxNamesInCollisionMessage)
            {
                listed += $" and {names.Length - MaxNamesInCollisionMessage} more";
            }

            string what = kinds[first] == EntryKind.Directory
                ? $"{names.Length} directories have names that differ only by case ({listed}). None of them is scanned; rename all but one."
                : $"{names.Length} files have the same case-insensitive plugin identity ({listed}). None of them is used; rename all but one.";
            string path = parentRelativePath is null ? names[0] : parentRelativePath + "/" + names[0];
            _diagnostics.Add(Diagnostic(PluginDiagnosticSeverity.Error, PluginDiagnosticCode.IdentityCollision, what, path));
        }

        private void Fail(PluginDiagnosticCode code, string message) =>
            Incomplete(Diagnostic(PluginDiagnosticSeverity.Error, code, message, path: null));

        private void Incomplete(PluginDiagnostic diagnostic)
        {
            _complete = false;
            _diagnostics.Add(diagnostic);
        }

        private void Stop(PluginDiagnosticCode code, string message)
        {
            _stopped = true;
            Incomplete(Diagnostic(PluginDiagnosticSeverity.Warning, code, message, path: null));
        }
    }
}
