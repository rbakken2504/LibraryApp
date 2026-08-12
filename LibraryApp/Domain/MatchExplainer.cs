using System.Text;

namespace LibraryApp.Domain;

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
    public static string Explain(Book book, SearchIntent intent, MatchTier tier)
    {
        var clauses = new List<string>(5);

        // Lead with the rank band — it is the first thing a reader scanning a list of five wants.
        if (TierClause(book, intent, tier) is { } banner) clauses.Add(banner);

        if (!string.IsNullOrWhiteSpace(intent.Author))
        {
            // Prefer the catalog's spelling of the author over the user's fragment.
            var matched = book.Authors.FirstOrDefault(a => NameKey.Matches(intent.Author, a.Name));

            if (matched is not null)
            {
                clauses.Add($"by {matched.Name}");
            }
            else if (MatchingContributor(book, intent.Author) is { } contributor)
            {
                // Naming the role is the point of tier b: the reader should see immediately that
                // the person they named narrated or illustrated it rather than wrote it.
                clauses.Add(contributor.Role is null
                    ? $"credits {contributor.Name}"
                    : $"credits {contributor.Name} as {contributor.Role.ToLowerInvariant()}");
            }
        }

        if (!string.IsNullOrWhiteSpace(intent.Title)
            && TitleKey.Compare(intent.Title, book.Title) is TitleMatch.Near)
        {
            clauses.Add($"broader than \"{intent.Title}\"");
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

    private static string? TierClause(Book book, SearchIntent intent, MatchTier tier) => tier switch
    {
        MatchTier.ExactTitlePrimaryAuthor => "exact title match, by the author you named",
        MatchTier.ExactTitleContributor   => "exact title match, but the person you named only contributed",
        MatchTier.NearTitleAuthor         => "close title match with the right author",
        MatchTier.TitleOnly when !string.IsNullOrWhiteSpace(intent.Author)
                                          => "title matches, though not by the author you named",
        MatchTier.TitleOnly               => "title match",
        MatchTier.AuthorOnly              => $"another work by {book.Authors.FirstOrDefault()?.Name ?? intent.Author}",
        _                                 => null
    };

    private static (string Name, string? Role)? MatchingContributor(Book book, string? wantedAuthor)
    {
        foreach (var entry in book.Contributors)
        {
            var split = NameKey.SplitRole(entry);
            if (NameKey.Matches(wantedAuthor, split.Name)) return split;
        }

        return null;
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
