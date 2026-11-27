namespace TrayWisp.Core.Trust;

/// <summary>
/// The result of inspecting a plugin file: its validated key, the SHA-256 fingerprint of its entire
/// content and the number of bytes hashed.
/// </summary>
/// <remarks>
/// An inspection is a point-in-time observation. It is what a user interface shows when it asks the user
/// to review a plugin, and what the user then approves by passing <see cref="Fingerprint"/> back as the
/// expected fingerprint. It does not authorize anything by itself.
/// </remarks>
public sealed record PluginFingerprint
{
    /// <summary>Creates an inspection result.</summary>
    public PluginFingerprint(PluginTrustKey key, ContentFingerprint fingerprint, long contentLength)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        Key = key;
        Fingerprint = fingerprint;
        ContentLength = contentLength;
    }

    /// <summary>The validated plugin root and identity.</summary>
    public PluginTrustKey Key { get; }

    /// <summary>SHA-256 of the complete raw file content.</summary>
    public ContentFingerprint Fingerprint { get; }

    /// <summary>Number of bytes hashed, which is the file length.</summary>
    public long ContentLength { get; }
}
