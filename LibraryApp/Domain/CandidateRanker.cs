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
        // Both facts are worked out once here and carried, rather than re-derived by the explainer:
        // two answers to the same question are two chances to disagree.
        var scored = candidates.Select(book =>
        {
            var title = TitleKey.Compare(intent.Title, book.Title);
            var credit = AuthorCredit.Find(book, intent.Author);

            return new
            {
                Book = book,
                Title = title.Match,
                title.Surplus,
                Credit = credit,
                Tier = TierFor(title.Match, credit)
            };
        });

        // OrderBy is stable, so the catalogue's own relevance survives as the final tiebreak within
        // a tier — we are re-banding its results, not re-scoring them from scratch.
        var ordered = scored
            .OrderBy(c => (int)c.Tier)
            .ThenBy(c => c.Surplus)
            .Take(limit)
            .Select(c => new BookMatch(
                c.Book,
                c.Tier,
                MatchExplainer.Explain(c.Book, intent, c.Tier, c.Title, c.Credit)))
            .ToArray();

        var exactPrimary = ordered.Count(m => m.Tier is MatchTier.ExactTitlePrimaryAuthor);

        return new RankedCandidates(ordered, ClearWinner: exactPrimary == 1);
    }

    /// <summary>What a title comparison and an author credit together mean for rank.</summary>
    /// <remarks>
    /// A missing title or author needs no special case: <see cref="TitleKey.Compare"/> reports no
    /// match and <see cref="AuthorCredit.Find"/> returns <c>null</c> when the reader did not ask for
    /// one, which lands on the right tier anyway.
    /// </remarks>
    private static MatchTier TierFor(TitleMatch title, AuthorCredit? credit) =>
        // Arms run strongest to weakest, matching the order MatchTier declares them in.
        (title, credit?.Kind) switch
        {
            (TitleMatch.Exact, CreditKind.Primary)     => MatchTier.ExactTitlePrimaryAuthor,
            (TitleMatch.Exact, CreditKind.Contributor) => MatchTier.ExactTitleContributor,
            (TitleMatch.Near,  not null)               => MatchTier.NearTitleAuthor,

            // Title fits, but nobody corroborates it — a different edition, an adaptation, or a
            // book that happens to share the name.
            (not TitleMatch.None, _)                   => MatchTier.TitleOnly,

            // Wrong book, right person: keep it as one of that author's other works.
            (_, not null)                              => MatchTier.AuthorOnly,

            _                                          => MatchTier.Discovery
        };
}
