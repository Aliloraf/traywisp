using TrayWisp.Core.Plugins;

namespace TrayWisp.Core.Trust;

/// <summary>
/// What an approval is bound to, apart from content: a normalized plugin root and a root-relative
/// <see cref="PluginId"/>. The same identity under two different roots is two different keys.
/// </summary>
/// <remarks>
/// <para>Equality and ordering are ordinal case-insensitive on both parts, using the semantics of
/// <see cref="PluginRoot"/> and <see cref="PluginId"/>.</para>
/// <para>Only identities that discovery could produce with its default limits are accepted (see
/// <see cref="IsSupportedPluginId"/>), so an approval can never refer to a file that discovery would have
/// skipped because of its name or location.</para>
/// </remarks>
public sealed class PluginTrustKey : IEquatable<PluginTrustKey>, IComparable<PluginTrustKey>
{
    /// <summary>Longest accepted <see cref="PluginId.Value"/>, in UTF-16 code units.</summary>
    public const int MaxPluginIdLength = 4096;

    /// <summary>
    /// Deepest directory level a plugin file may be in, where files directly in the root are at level 0.
    /// Equal to the default <c>PluginDiscoveryOptions.MaxDepth</c>.
    /// </summary>
    public const int MaxDirectoryDepth = 8;

    /// <summary>Creates a key.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a supported plugin identity (see <see cref="IsSupportedPluginId"/>).</exception>
    public PluginTrustKey(PluginRoot root, PluginId id)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(id);
        if (!IsSupportedPluginId(id))
        {
            throw new ArgumentException("The identity is not a plugin path that discovery can produce.", nameof(id));
        }

        Root = root;
        Id = id;
    }

    /// <summary>The normalized plugin root.</summary>
    public PluginRoot Root { get; }

    /// <summary>The root-relative plugin identity.</summary>
    public PluginId Id { get; }

    /// <summary>
    /// Returns whether <paramref name="id"/> is a path discovery can produce with its default limits: at most
    /// <see cref="MaxPluginIdLength"/> characters, at most <see cref="MaxDirectoryDepth"/> directories deep, no
    /// segment beginning with <c>.</c> (discovery skips such entries), and a file name that
    /// <see cref="PluginFileNameParser"/> accepts as a plugin. In addition, the identity must be well-formed
    /// UTF-16 (no unpaired surrogate), so that it can be stored and read back exactly; such names are
    /// possible on Windows but cannot be approved.
    /// </summary>
    public static bool IsSupportedPluginId(PluginId? id)
    {
        if (id is null || id.Value.Length > MaxPluginIdLength || !Utf16Text.IsWellFormed(id.Value) || !PluginFileNameParser.Parse(id.FileName).IsParsed)
        {
            return false;
        }

        int directories = 0;
        foreach (Range range in id.Value.AsSpan().Split('/'))
        {
            if (id.Value.AsSpan()[range].StartsWith('.'))
            {
                return false;
            }

            directories++;
        }

        return directories - 1 <= MaxDirectoryDepth;
    }

    /// <inheritdoc />
    public bool Equals(PluginTrustKey? other) => other is not null && Root.Equals(other.Root) && Id.Equals(other.Id);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PluginTrustKey);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Root, Id);

    /// <summary>Orders by root, then by identity, both ordinal case-insensitive.</summary>
    public int CompareTo(PluginTrustKey? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Root.CompareTo(other.Root);
        return result != 0 ? result : Id.CompareTo(other.Id);
    }

    /// <summary>The root and identity, for example <c>C:\plugins | work/clock.5s.ps1</c>. Not escaped.</summary>
    public override string ToString() => $"{Root.Value} | {Id.Value}";

    /// <summary>Case-insensitive equality.</summary>
    public static bool operator ==(PluginTrustKey? left, PluginTrustKey? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Case-insensitive inequality.</summary>
    public static bool operator !=(PluginTrustKey? left, PluginTrustKey? right) => !(left == right);

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator <(PluginTrustKey? left, PluginTrustKey? right) => Compare(left, right) < 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator <=(PluginTrustKey? left, PluginTrustKey? right) => Compare(left, right) <= 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator >(PluginTrustKey? left, PluginTrustKey? right) => Compare(left, right) > 0;

    /// <summary>Ordering as defined by <see cref="CompareTo"/>.</summary>
    public static bool operator >=(PluginTrustKey? left, PluginTrustKey? right) => Compare(left, right) >= 0;

    private static int Compare(PluginTrustKey? left, PluginTrustKey? right) => left is null ? (right is null ? 0 : -1) : left.CompareTo(right);
}
