using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TrayWisp.Core.Trust;

/// <summary>
/// Identifies one act of approval. Runtime creates a new random generation for every newly created
/// approval, so an approval that was revoked (or invalidated) and then granted again, even for the same
/// bytes, has a different generation than before. Output produced under an earlier generation can
/// therefore be recognized as stale.
/// </summary>
/// <remarks>
/// The canonical text is 32 lowercase hexadecimal characters (a GUID in <c>N</c> format). The all-zero
/// value is rejected. Equality is ordinal on the canonical text. Core never creates generations.
/// </remarks>
public sealed class ApprovalGeneration : IEquatable<ApprovalGeneration>
{
    /// <summary>Length of the canonical text: 32 characters.</summary>
    public const int TextLength = 32;

    private ApprovalGeneration(string value) => Value = value;

    /// <summary>The canonical text: 32 lowercase hexadecimal characters.</summary>
    public string Value { get; }

    /// <summary>Parses the canonical text. Any other form, including braces, hyphens and upper case, is rejected.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out ApprovalGeneration? generation)
    {
        generation = null;
        if (text is null || text.Length != TextLength)
        {
            return false;
        }

        bool allZero = true;
        foreach (char c in text)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }

            allZero &= c == '0';
        }

        if (allZero)
        {
            return false;
        }

        generation = new ApprovalGeneration(text);
        return true;
    }

    /// <summary>Parses the canonical text, throwing <see cref="FormatException"/> if it is not canonical.</summary>
    public static ApprovalGeneration Parse(string text) =>
        TryParse(text, out ApprovalGeneration? generation)
            ? generation
            : throw new FormatException("An approval generation must be 32 lowercase hexadecimal characters and not all zero.");

    /// <summary>Creates a generation from a GUID.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is <see cref="Guid.Empty"/>.</exception>
    public static ApprovalGeneration FromGuid(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An approval generation cannot be the empty GUID.", nameof(value));
        }

        return new ApprovalGeneration(value.ToString("N", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public bool Equals(ApprovalGeneration? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ApprovalGeneration);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Value equality.</summary>
    public static bool operator ==(ApprovalGeneration? left, ApprovalGeneration? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ApprovalGeneration? left, ApprovalGeneration? right) => !(left == right);
}
