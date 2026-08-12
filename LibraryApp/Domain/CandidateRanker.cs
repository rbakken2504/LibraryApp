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

    /// <summary>
    /// How well did the title match, how well did the author match, and what does that pair mean?
    /// </summary>
    /// <remarks>
    /// A missing title or author needs no special case: <see cref="TitleKey.Compare"/> and
    /// <see cref="NameKey.Matches"/> both report no match when the reader did not ask for one, which
    /// lands on the right tier anyway.
    /// </remarks>
    private static MatchTier TierFor(SearchIntent intent, Book book)
    {
        var title = TitleKey.Compare(intent.Title, book.Title);
        var author = AuthorMatch(intent.Author, book);

        // Arms run strongest to weakest, matching the order MatchTier declares them in.
        return (title, author) switch
        {
            (TitleMatch.Exact, AuthorRole.Primary)     => MatchTier.ExactTitlePrimaryAuthor,
            (TitleMatch.Exact, AuthorRole.Contributor) => MatchTier.ExactTitleContributor,
            (TitleMatch.Near,  not AuthorRole.None)    => MatchTier.NearTitleAuthor,

            // Title fits, but nobody corroborates it — a different edition, an adaptation, or a
            // book that happens to share the name.
            (not TitleMatch.None, _)                   => MatchTier.TitleOnly,

            // Wrong book, right person: keep it as one of that author's other works.
            (_, not AuthorRole.None)                   => MatchTier.AuthorOnly,

            _                                          => MatchTier.Discovery
        };
    }

    private enum AuthorRole { None, Primary, Contributor }

    /// <summary>
    /// Primary beats contributor, which is the whole distinction between the top two tiers.
    /// The primary list comes from the search response and matches the work record's own authors.
    /// </summary>
    private static AuthorRole AuthorMatch(string? wantedAuthor, Book book)
    {
        if (book.Authors.Any(author => NameKey.Matches(wantedAuthor, author.Name)))
        {
            return AuthorRole.Primary;
        }

        var contributorMatch = book.Contributors
            .Select(NameKey.SplitRole)
            .Any(c => NameKey.Matches(wantedAuthor, c.Name));

        return contributorMatch ? AuthorRole.Contributor : AuthorRole.None;
    }
}
