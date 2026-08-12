namespace LibraryApp.Domain;

/// <param name="ClearWinner">
/// Exactly one candidate reached <see cref="MatchTier.ExactTitlePrimaryAuthor"/>, so the reader
/// almost certainly meant that book. A tie at the top tier is not a winner — it is ambiguity, and
/// collapsing it would hide a real choice.
/// </param>
public sealed record RankedCandidates(IReadOnlyList<BookMatch> Matches, bool ClearWinner);

/// <summary>
/// Assigns each candidate a <see cref="MatchTier"/> and orders them. Pure — the catalogue has
/// already been queried by the time this runs.
/// </summary>
public static class CandidateRanker
{
    public static RankedCandidates Rank(SearchIntent intent, IReadOnlyList<Book> candidates, int limit)
    {
        var scored = candidates.Select(book => new
        {
            Book = book,
            Tier = TierFor(intent, book),
            Surplus = TitleKey.SurplusTokens(intent.Title, book.Title)
        });

        // OrderBy is stable, so the catalogue's own relevance survives as the final tiebreak within
        // a tier — we are re-banding its results, not re-scoring them from scratch.
        var ordered = scored
            .OrderBy(c => (int)c.Tier)
            .ThenBy(c => c.Surplus)
            .Take(limit)
            .Select(c => new BookMatch(c.Book, c.Tier, MatchExplainer.Explain(c.Book, intent, c.Tier)))
            .ToArray();

        var exactPrimary = ordered.Count(m => m.Tier is MatchTier.ExactTitlePrimaryAuthor);

        return new RankedCandidates(ordered, ClearWinner: exactPrimary == 1);
    }

    private static MatchTier TierFor(SearchIntent intent, Book book)
    {
        var wantsTitle = !string.IsNullOrWhiteSpace(intent.Title);
        var wantsAuthor = !string.IsNullOrWhiteSpace(intent.Author);

        if (!wantsTitle && !wantsAuthor) return MatchTier.Discovery;

        var titleMatch = wantsTitle ? TitleKey.Compare(intent.Title, book.Title) : TitleMatch.None;

        // Nothing was asked about the title, so these are simply the author's works.
        if (!wantsTitle) return AuthorMatch(intent.Author, book) is not AuthorRole.None
            ? MatchTier.AuthorOnly
            : MatchTier.Discovery;

        // The reader named a title and this is not it. If the author still matches, it belongs with
        // that author's other works rather than being discarded.
        if (titleMatch is TitleMatch.None) return wantsAuthor && AuthorMatch(intent.Author, book) is not AuthorRole.None
            ? MatchTier.AuthorOnly
            : MatchTier.Discovery;

        if (!wantsAuthor) return MatchTier.TitleOnly;

        return (titleMatch, AuthorMatch(intent.Author, book)) switch
        {
            (TitleMatch.Exact, AuthorRole.Primary)     => MatchTier.ExactTitlePrimaryAuthor,
            (TitleMatch.Exact, AuthorRole.Contributor) => MatchTier.ExactTitleContributor,
            (TitleMatch.Near, not AuthorRole.None)     => MatchTier.NearTitleAuthor,

            // Title lines up but the named person is nowhere on this work — a different edition,
            // adaptation or same-titled book. Below any corroborated match.
            _ => MatchTier.TitleOnly
        };
    }

    private enum AuthorRole { None, Primary, Contributor }

    /// <summary>
    /// Primary beats contributor, which is the whole distinction between the top two tiers.
    /// The primary list comes from the search response and matches the work record's own authors.
    /// </summary>
    private static AuthorRole AuthorMatch(string? wantedAuthor, Book book)
    {
        if (book.Authors.Any(author => NameKey.Matches(wantedAuthor, author)))
        {
            return AuthorRole.Primary;
        }

        var contributorMatch = book.Contributors
            .Select(NameKey.SplitRole)
            .Any(c => NameKey.Matches(wantedAuthor, c.Name));

        return contributorMatch ? AuthorRole.Contributor : AuthorRole.None;
    }
}
