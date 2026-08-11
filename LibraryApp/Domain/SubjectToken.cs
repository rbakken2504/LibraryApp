using System.Text;

namespace BookSearchService.Domain;

/// <summary>
/// Normalizes a phrase into an OpenLibrary subject token: lowercase ASCII alphanumerics
/// separated by single underscores ("Science Fiction" and "sci-fi" become science_fiction and sci_fi).
/// </summary>
/// <remarks>
/// Not cosmetic. Keywords are interpolated into a <c>subject:</c> Solr filter, so a stray space or
/// quote would either break the query or push it onto the free-text path — which OpenLibrary takes
/// upwards of 25 seconds to answer. Sanitizing here keeps a bad model response from becoming a hang.
/// </remarks>
public static class SubjectToken
{
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var builder = new StringBuilder(raw.Length);

        foreach (var ch in raw.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_');
    }
}
