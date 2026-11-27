using System.Diagnostics.CodeAnalysis;

namespace TrayWisp.Core.Plugins;

/// <summary>
/// The identity of a plugin: its path relative to the plugin root, including the file name and
/// extension, with <c>/</c> as the separator (for example <c>work/github.notifications.5m.py</c>).
/// </summary>
/// <remarks>
/// <para>The original casing is preserved in <see cref="Value"/>, but equality and hashing use
/// ordinal case-insensitive comparison to approximate Windows file-name semantics. No Unicode
/// normalization is applied, matching NTFS, which stores names as given.</para>
/// <para>Ordering (<see cref="CompareTo"/>) is ordinal case-insensitive and culture-independent, so it is
/// consistent with equality: two identities compare as 0 exactly when they are equal.</para>
/// </remarks>
public sealed class PluginId : IEquatable<PluginId>, IComparable<PluginId>
{
    private PluginId(string value)
    {
        Value = value;
        int slash = value.LastIndexOf('/');
        FileName = slash < 0 ? value : value[(slash + 1)..];
    }

    /// <summary>The normalized root-relative path.</summary>
    public string Value { get; }

    /// <summary>The last path segment, for example <c>clock.5s.ps1</c>.</summary>
    public string FileName { get; }

    /// <summary>
    /// Creates an identity from a root-relative path. Both <c>/</c> and <c>\</c> are accepted as
    /// separators and normalized to <c>/</c>. Fails for empty, rooted or drive-qualified paths and for
    /// any segment rejected by <see cref="IsValidSegment"/>.
    /// </summary>
    public static bool TryCreate(string? relativePath, [NotNullWhen(true)] out PluginId? id)
    {
        id = null;
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        string[] segments = relativePath.Split('/', '\\');
        foreach (string segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                return false;
            }
        }

        id = new PluginId(string.Join('/', segments));
        return true;
    }

    /// <summary>Creates an identity, throwing <see cref="ArgumentException"/> if the path is invalid.</summary>
    public static PluginId Create(string relativePath) =>
        TryCreate(relativePath, out PluginId? id)
            ? id
            : throw new ArgumentException("The path is not a valid root-relative plugin path.", nameof(relativePath));

    /// <summary>
    /// Returns whether <paramref name="segment"/> can be one component of a plugin identity: not empty,
    /// not <c>.</c> or <c>..</c>, no control characters, no <c>&lt; &gt; : " / \ | ? *</c>, no Unicode
    /// bidirectional controls, not a reserved Windows device name such as <c>CON</c>, and not ending with a
    /// space or a dot (Windows cannot normally address such names).
    /// </summary>
    public static bool IsValidSegment([NotNullWhen(true)] string? segment) =>
        !string.IsNullOrEmpty(segment)
        && segment is not "." and not ".."
        && !FileNameRules.ContainsInvalidCharacter(segment)
        && !FileNameRules.ContainsBidiControl(segment)
        && !FileNameRules.IsReservedDeviceName(segment)
        && segment[^1] is not ' ' and not '.';

    /// <inheritdoc />
    public bool Equals(PluginId? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PluginId);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <inheritdoc />
    public int CompareTo(PluginId? other) =>
        other is null ? 1 : string.Compare(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Case-insensitive equality.</summary>
    public static bool operator ==(PluginId? left, PluginId? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Case-insensitive inequality.</summary>
    public static bool operator !=(PluginId? left, PluginId? right) => !(left == right);

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator <(PluginId? left, PluginId? right) => Compare(left, right) < 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator <=(PluginId? left, PluginId? right) => Compare(left, right) <= 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator >(PluginId? left, PluginId? right) => Compare(left, right) > 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator >=(PluginId? left, PluginId? right) => Compare(left, right) >= 0;

    private static int Compare(PluginId? left, PluginId? right) => left is null ? (right is null ? 0 : -1) : left.CompareTo(right);
}
