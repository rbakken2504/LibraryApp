using System.Globalization;
using System.Text;

namespace LibraryApp.Domain;

/// <summary>
/// Shared case/diacritic/punctuation folding used by the comparison keys.
/// </summary>
/// <remarks>
/// Only the mechanical folding is shared. Which words each key then discards is a per-key decision
/// and deliberately not centralised — see the warning on <see cref="TitleKey"/>.
/// </remarks>
internal static class TextFold
{
    /// <summary>Lowercases, strips diacritics, and reduces anything non-alphanumeric to a separator.</summary>
    public static string[] Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
