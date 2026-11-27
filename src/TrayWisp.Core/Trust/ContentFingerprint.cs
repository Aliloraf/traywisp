using System.Diagnostics.CodeAnalysis;

namespace TrayWisp.Core.Trust;

/// <summary>
/// A SHA-256 hash of the complete raw bytes of a plugin file, in its canonical form: exactly
/// 64 lowercase hexadecimal characters.
/// </summary>
/// <remarks>
/// A fingerprint is only a value. Holding one proves nothing about the current content of any file:
/// Runtime recomputes the fingerprint from the file whenever it approves or checks a plugin.
/// Equality is ordinal on the canonical text.
/// </remarks>
public sealed class ContentFingerprint : IEquatable<ContentFingerprint>
{
    /// <summary>Size of a SHA-256 hash in bytes.</summary>
    public const int HashSizeInBytes = 32;

    /// <summary>Length of the canonical text: 64 characters.</summary>
    public const int TextLength = HashSizeInBytes * 2;

    private ContentFingerprint(string value) => Value = value;

    /// <summary>The canonical text: 64 lowercase hexadecimal characters.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses the canonical text. Upper-case digits, prefixes such as <c>sha256:</c>, white space and any
    /// other length are rejected rather than normalized.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out ContentFingerprint? fingerprint)
    {
        fingerprint = null;
        if (text is null || text.Length != TextLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        fingerprint = new ContentFingerprint(text);
        return true;
    }

    /// <summary>Parses the canonical text, throwing <see cref="FormatException"/> if it is not canonical.</summary>
    public static ContentFingerprint Parse(string text) =>
        TryParse(text, out ContentFingerprint? fingerprint)
            ? fingerprint
            : throw new FormatException("A content fingerprint must be exactly 64 lowercase hexadecimal characters.");

    /// <summary>Creates a fingerprint from the 32 bytes of a SHA-256 hash.</summary>
    /// <exception cref="ArgumentException"><paramref name="sha256"/> is not 32 bytes long.</exception>
    public static ContentFingerprint FromHash(ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != HashSizeInBytes)
        {
            throw new ArgumentException("A SHA-256 hash is exactly 32 bytes long.", nameof(sha256));
        }

        return new ContentFingerprint(Convert.ToHexStringLower(sha256));
    }

    /// <inheritdoc />
    public bool Equals(ContentFingerprint? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ContentFingerprint);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Value equality.</summary>
    public static bool operator ==(ContentFingerprint? left, ContentFingerprint? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ContentFingerprint? left, ContentFingerprint? right) => !(left == right);
}
