using System.Text;

namespace BookSearchService.Domain;

/// <summary>
/// Derives the "why did this match?" text from the intent that retrieved the book.
/// </summary>
/// <remarks>
/// This is deliberately not an AI call. Every clause below is a fact about the query
/// constraints and the returned document, so the explanation cannot hallucinate — and it
/// costs nothing per result. Callers must pass the <em>effective</em> intent (the one the
/// catalog actually ran, post-broadening), not the originally parsed one.
/// </remarks>
public static class MatchExplainer
{
    public static string Explain(Book book, SearchIntent intent)
    {
        var clauses = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(intent.Author))
        {
            // Prefer the catalog's spelling of the author over the user's fragment.
            var matched = book.Authors.FirstOrDefault(
                a => a.Contains(intent.Author, StringComparison.OrdinalIgnoreCase));

            clauses.Add(matched is not null
                ? $"matches author {matched}"
                : $"matches author {intent.Author}");
        }

        if (!string.IsNullOrWhiteSpace(intent.Title)
            && book.Title.Contains(intent.Title, StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add($"title contains \"{intent.Title}\"");
        }

        if (book.FirstPublishYear is { } year && (intent.YearFrom is not null || intent.YearTo is not null))
        {
            clauses.Add((intent.YearFrom, intent.YearTo) switch
            {
                (int from, int to) => $"published {year}, within {from}–{to}",
                (int from, null)   => $"published {year}, on or after {from}",
                (null, int to)     => $"published {year}, on or before {to}",
                _                  => $"published {year}"
            });
        }

        if (intent.Keywords.Count > 0)
        {
            // The catalog applied these as subject: filters, so every returned doc carries them.
            var subjects = string.Join(", ", intent.Keywords.Select(Humanize));
            clauses.Add($"tagged {subjects}");
        }

        return clauses.Count == 0
            ? "Matched your search."
            : Capitalize(string.Join("; ", clauses)) + ".";
    }

    private static string Humanize(string subjectToken) => subjectToken.Replace('_', ' ');

    private static string Capitalize(string value)
    {
        if (value.Length == 0) return value;
        return string.Create(value.Length, value, static (span, source) =>
        {
            source.AsSpan().CopyTo(span);
            span[0] = char.ToUpperInvariant(span[0]);
        });
    }
}
