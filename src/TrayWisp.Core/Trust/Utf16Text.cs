namespace TrayWisp.Core.Trust;

// Well-formedness of UTF-16 text at the trust-model boundary. Windows file names may contain unpaired
// surrogates, but such names cannot be written to JSON (the encoder silently replaces them with U+FFFD), so
// an approval of one could be read back as a different identity. Trust keys therefore reject them.
internal static class Utf16Text
{
    // True when every high surrogate is immediately followed by a low surrogate and no low surrogate stands
    // alone. Supplementary characters (correct pairs) are well-formed.
    public static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }
}
