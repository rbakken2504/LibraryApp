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
    /// <param name="title">
    /// How the title compared, as the ranker already measured it. A <see cref="MatchTier"/> alone
    /// cannot say: <see cref="MatchTier.TitleOnly"/> covers both an exact and a near title.
    /// </param>
    /// <param name="credit">Who matched and how, or <c>null</c> when nobody did.</param>
    public static string Explain(
        Book book, SearchIntent intent, MatchTier tier, TitleMatch title, AuthorCredit? credit)
    {
        var clauses = new List<string>(5);

        // Lead with the rank band — it is the first thing a reader scanning a list of five wants.
        if (TierClause(intent, tier, credit) is { } banner) clauses.Add(banner);

        // AuthorOnly's banner already names the person, so a second "by X" would just repeat it.
        if (credit is not null && tier is not MatchTier.AuthorOnly) clauses.Add(CreditClause(credit));

        // Near implies the reader named a title: an empty ask compares as None.
        if (title is TitleMatch.Near) clauses.Add($"broader than \"{intent.Title}\"");

        // One switch over all three facts, so "was a year asked for?" is stated once rather than as
        // a guard and again as arms. The final arm is now reachable: no bounds, or no year on record.
        var published = (book.FirstPublishYear, intent.YearFrom, intent.YearTo) switch
        {
            (int year, int from, int to) => $"published {year}, within {from}–{to}",
            (int year, int from, null)   => $"published {year}, on or after {from}",
            (int year, null, int to)     => $"published {year}, on or before {to}",
            _                            => null
        };

        if (published is not null) clauses.Add(published);

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

    private static string? TierClause(SearchIntent intent, MatchTier tier, AuthorCredit? credit) => tier switch
    {
        MatchTier.ExactTitlePrimaryAuthor => "exact title match, by the author you named",
        MatchTier.ExactTitleContributor   => "exact title match, but the person you named only contributed",
        MatchTier.NearTitleAuthor         => "close title match with the right author",
        MatchTier.TitleOnly when !string.IsNullOrWhiteSpace(intent.Author)
                                          => "title matches, though not by the author you named",
        MatchTier.TitleOnly               => "title match",

        // The person the reader asked about, who on a co-authored work is not necessarily the one
        // the catalogue happens to list first.
        MatchTier.AuthorOnly              => $"another work by {credit?.Name ?? intent.Author}",

        _                                 => null
    };

    /// <summary>
    /// Names the person using the catalogue's spelling rather than the reader's fragment.
    /// </summary>
    /// <remarks>
    /// Naming the role is the point of tier b: the reader should see immediately that the person
    /// they named narrated or illustrated the book rather than wrote it. The catalogue does not
    /// always give one, and a contributor without a stated role still beats saying nothing.
    /// </remarks>
    private static string CreditClause(AuthorCredit credit)
    {
        if (credit.Kind is CreditKind.Primary) return $"by {credit.Name}";

        return credit.Role is null
            ? $"credits {credit.Name}"
            : $"credits {credit.Name} as {credit.Role.ToLowerInvariant()}";
    }

    private static string Humanize(string subjectToken) => subjectToken.Replace('_', ' ');

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
