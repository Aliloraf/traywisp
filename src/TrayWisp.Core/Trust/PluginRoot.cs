using System.Diagnostics.CodeAnalysis;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Core.Trust;

/// <summary>
/// The normalized, fully qualified path of a plugin root on a local Windows drive, as used by approvals.
/// </summary>
/// <remarks>
/// <para>This type only checks and normalizes the <em>syntax</em> of a path. It never touches the file
/// system. Runtime additionally verifies, every time the root is used, that the path names an ordinary
/// local directory reached without links, substituted drives or other aliases.</para>
/// <para>Accepted form: a drive letter, <c>:</c>, a separator, then zero or more segments, for example
/// <c>C:\Users\me\plugins</c>. <c>/</c> is accepted as a separator and normalized to <c>\</c>. A single
/// trailing separator is removed, except for a drive root, which is always <c>X:\</c>.</para>
/// <para>Rejected: relative paths, drive-relative paths (<c>C:plugins</c>), rooted paths without a drive
/// (<c>\plugins</c>), UNC paths (<c>\\server\share</c>), device and extended-length namespaces
/// (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>), empty segments (doubled separators), <c>.</c> and
/// <c>..</c> segments, alternate data streams and any other <c>:</c> after the drive, and every segment
/// that <see cref="PluginId.IsValidSegment"/> rejects (control and reserved characters, bidirectional
/// controls, reserved device names, trailing spaces or dots). Paths longer than
/// <see cref="MaxLength"/> are rejected, and so are paths that are not well-formed UTF-16 (an unpaired
/// surrogate), because they cannot be stored as JSON without being silently replaced. Supplementary
/// characters (correctly paired surrogates) are accepted. No Unicode normalization is applied.</para>
/// <para>The original spelling, including case, is preserved in <see cref="Value"/>. Equality and hashing are
/// ordinal case-insensitive, consistent with <see cref="PluginId"/>.</para>
/// </remarks>
public sealed class PluginRoot : IEquatable<PluginRoot>, IComparable<PluginRoot>
{
    /// <summary>Longest accepted path, in UTF-16 code units, before and after normalization.</summary>
    public const int MaxLength = 4096;

    private readonly string[] _segments;

    private PluginRoot(string value, string[] segments)
    {
        Value = value;
        _segments = segments;
    }

    /// <summary>The normalized path, for example <c>C:\Users\me\plugins</c> or <c>D:\</c>.</summary>
    public string Value { get; }

    /// <summary><see langword="true"/> for a drive root such as <c>C:\</c>.</summary>
    public bool IsDriveRoot => _segments.Length == 0;

    /// <summary>Normalizes and validates <paramref name="path"/> as described in the type remarks.</summary>
    public static bool TryCreate([NotNullWhen(true)] string? path, [NotNullWhen(true)] out PluginRoot? root)
    {
        root = null;
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength || !Utf16Text.IsWellFormed(path))
        {
            return false;
        }

        string normalized = path.Replace('/', '\\');
        if (normalized.Length < 3 || !char.IsAsciiLetter(normalized[0]) || normalized[1] != ':' || normalized[2] != '\\')
        {
            return false;
        }

        string drive = normalized[..3];
        string rest = normalized[3..];
        if (rest.Length == 0)
        {
            root = new PluginRoot(drive, []);
            return true;
        }

        if (rest[^1] == '\\')
        {
            rest = rest[..^1];
        }

        string[] segments = rest.Split('\\');
        foreach (string segment in segments)
        {
            if (!PluginId.IsValidSegment(segment))
            {
                return false;
            }
        }

        root = new PluginRoot(drive + rest, segments);
        return true;
    }

    /// <summary>Normalizes and validates a path, throwing <see cref="ArgumentException"/> if it is not acceptable.</summary>
    public static PluginRoot Create(string path) =>
        TryCreate(path, out PluginRoot? root)
            ? root
            : throw new ArgumentException("The path is not a supported fully qualified local Windows directory path.", nameof(path));

    /// <summary>
    /// Returns whether <paramref name="other"/> is this directory or lies below it, comparing whole segments
    /// case-insensitively (so <c>C:\plugins</c> does not contain <c>C:\plugins2</c>). This is a comparison of
    /// normalized paths, which is only meaningful after Runtime has excluded aliases.
    /// </summary>
    public bool Contains(PluginRoot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (char.ToUpperInvariant(Value[0]) != char.ToUpperInvariant(other.Value[0]) || other._segments.Length < _segments.Length)
        {
            return false;
        }

        for (int i = 0; i < _segments.Length; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether either path contains the other.</summary>
    public bool Overlaps(PluginRoot other) => Contains(other) || other.Contains(this);

    /// <inheritdoc />
    public bool Equals(PluginRoot? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PluginRoot);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <summary>Ordinal case-insensitive ordering, consistent with equality.</summary>
    public int CompareTo(PluginRoot? other) =>
        other is null ? 1 : string.Compare(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Case-insensitive equality.</summary>
    public static bool operator ==(PluginRoot? left, PluginRoot? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Case-insensitive inequality.</summary>
    public static bool operator !=(PluginRoot? left, PluginRoot? right) => !(left == right);

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator <(PluginRoot? left, PluginRoot? right) => Compare(left, right) < 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator <=(PluginRoot? left, PluginRoot? right) => Compare(left, right) <= 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator >(PluginRoot? left, PluginRoot? right) => Compare(left, right) > 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator >=(PluginRoot? left, PluginRoot? right) => Compare(left, right) >= 0;

    private static int Compare(PluginRoot? left, PluginRoot? right) => left is null ? (right is null ? 0 : -1) : left.CompareTo(right);
}
