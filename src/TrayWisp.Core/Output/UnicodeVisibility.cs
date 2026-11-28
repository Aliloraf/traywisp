namespace TrayWisp.Core.Output;

/// <summary>
/// The character classes TrayWisp uses to decide whether text renders as nothing, for hosts that must make such
/// characters visible when they display identities and paths (see docs/cli.md, "Terminal safety").
/// </summary>
public static class UnicodeVisibility
{
    /// <summary>
    /// True if <paramref name="codePoint"/> is a Default_Ignorable_Code_Point (Unicode 16.0 DerivedCoreProperties.txt:
    /// format characters, Hangul fillers, variation selectors, the combining grapheme joiner, the Mongolian free variation
    /// selectors, their supplementary-plane counterparts and others), which renders as nothing, or U+2800 BRAILLE PATTERN
    /// BLANK, which is not default-ignorable but renders as an empty cell. The same list decides whether a label is
    /// invisible (docs/output-protocol.md).
    /// </summary>
    /// <param name="codePoint">A Unicode scalar value, or a UTF-16 code unit.</param>
    public static bool IsDefaultIgnorableOrBlank(int codePoint) => OutputText.IsDefaultIgnorableOrBlank(codePoint);
}
