using LibraryApp.Domain;

namespace LibraryApp.Tests;

/// <summary>
/// The tier rules from the requirements. Every case here mirrors real catalogue data observed
/// against the live API, so the fixtures are not invented shapes.
/// </summary>
public class CandidateRankerTests
{
    [Fact]
    public void Exact_title_by_a_primary_author_is_the_strongest_tier()
    {
        var result = Rank(Intent("Dune", "Frank Herbert"), Dune);

        Assert.Equal(MatchTier.ExactTitlePrimaryAuthor, Assert.Single(result.Matches).Tier);
        Assert.True(result.ClearWinner);
    }

    [Fact]
    public void Exact_title_where_the_named_person_only_contributed_ranks_below_a_primary_author()
    {
        // Dune's author list holds Frank Herbert; Scott Brick appears only as its narrator.
        var result = Rank(Intent("Dune", "Scott Brick"), Dune);

        var match = Assert.Single(result.Matches);
        Assert.Equal(MatchTier.ExactTitleContributor, match.Tier);
        Assert.False(result.ClearWinner);
        Assert.Contains("narrator", match.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Primary_author_outranks_contributor_for_the_same_title()
    {
        var result = Rank(Intent("Dune", "Herbert"), DuneNarratedEdition, Dune);

        Assert.Equal(MatchTier.ExactTitlePrimaryAuthor, result.Matches[0].Tier);
        Assert.Equal("/works/dune", result.Matches[0].Book.Key);
    }

    [Fact]
    public void A_longer_title_containing_the_query_is_a_near_match()
    {
        var result = Rank(Intent("The Hobbit", "Tolkien"), HobbitCollection);

        Assert.Equal(MatchTier.NearTitleAuthor, Assert.Single(result.Matches).Tier);
    }

    [Fact]
    public void Exact_beats_near_and_the_tightest_near_match_comes_first()
    {
        var result = Rank(Intent("The Hobbit", "Tolkien"), HobbitCollection, HobbitThereAndBack, Hobbit);

        Assert.Equal(
            ["The Hobbit", "The Hobbit, or There and Back Again", "The Hobbit & The Lord of the Rings [collection/set]"],
            result.Matches.Select(m => m.Book.Title));
    }

    [Fact]
    public void A_title_match_by_the_wrong_author_ranks_below_any_corroborated_match()
    {
        // The graphic-novel adaptation: exact title, but Tolkien is absent from its author list.
        var result = Rank(Intent("The Hobbit", "Tolkien"), HobbitAdaptation, Hobbit);

        Assert.Equal(MatchTier.ExactTitlePrimaryAuthor, result.Matches[0].Tier);
        Assert.Equal(MatchTier.TitleOnly, result.Matches[1].Tier);
    }

    [Fact]
    public void A_tie_at_the_top_tier_is_ambiguity_rather_than_a_winner()
    {
        var reissue = Dune with { Key = "/works/dune-reissue" };

        var result = Rank(Intent("Dune", "Herbert"), Dune, reissue);

        Assert.Equal(2, result.Matches.Count(m => m.Tier is MatchTier.ExactTitlePrimaryAuthor));
        Assert.False(result.ClearWinner);
    }

    [Fact]
    public void A_subject_only_intent_leaves_the_catalogues_order_untouched()
    {
        var intent = new SearchIntent(null, null, null, null, ["cyberpunk"], "test");

        var result = Rank(intent, Hobbit, Dune, Foundation);

        Assert.All(result.Matches, m => Assert.Equal(MatchTier.Discovery, m.Tier));
        Assert.Equal(["The Hobbit", "Dune", "Foundation"], result.Matches.Select(m => m.Book.Title));
        Assert.False(result.ClearWinner);
    }

    [Fact]
    public void Works_that_match_the_author_but_not_the_title_are_kept_as_that_authors_other_works()
    {
        var result = Rank(Intent("Nonexistent", "Herbert"), Dune);

        Assert.Equal(MatchTier.AuthorOnly, Assert.Single(result.Matches).Tier);
    }

    [Fact]
    public void A_title_with_no_author_named_cannot_reach_the_corroborated_tiers()
    {
        var result = Rank(Intent("Dune", author: null), Dune);

        Assert.Equal(MatchTier.TitleOnly, Assert.Single(result.Matches).Tier);
        Assert.False(result.ClearWinner);
    }

    [Fact]
    public void Ranking_never_returns_more_than_the_limit()
    {
        var many = Enumerable.Range(0, 20).Select(i => Dune with { Key = $"/works/{i}" }).ToArray();

        Assert.Equal(5, CandidateRanker.Rank(Intent("Dune", "Herbert"), many, 5).Matches.Count);
    }

    // --- fixtures, shaped like real OpenLibrary responses --------------------------------

    /// <summary>Dune as the catalogue actually returns it — two author records for one person.</summary>
    private static readonly Book Dune = new(
        "/works/dune", "Dune",
        Authors: ["Frank Herbert", "Френк Герберт"],
        AuthorKeys: ["OL79034A", "OL7388009A"],
        Contributors: ["Scott Brick (Narrator)", "John Schoenherr (Illustrator)"],
        FirstPublishYear: 1965, CoverId: 392508, EditionCount: 160);

    private static readonly Book DuneNarratedEdition = new(
        "/works/dune-audio", "Dune",
        Authors: ["Some Publisher"], AuthorKeys: ["OL1X"],
        Contributors: ["Scott Brick (Narrator)", "Frank Herbert (Author)"],
        FirstPublishYear: 2007, CoverId: null, EditionCount: 3);

    private static readonly Book Hobbit = Work("/works/OL27482W", "The Hobbit", "J.R.R. Tolkien");

    private static readonly Book HobbitThereAndBack =
        Work("/works/hobbit-full", "The Hobbit, or There and Back Again", "J.R.R. Tolkien");

    private static readonly Book HobbitCollection =
        Work("/works/OL14926019W", "The Hobbit & The Lord of the Rings [collection/set]", "J.R.R. Tolkien");

    /// <summary>The graphic novel: exact title, Tolkien nowhere in its author list.</summary>
    private static readonly Book HobbitAdaptation = new(
        "/works/OL219602W", "The Hobbit",
        Authors: ["Charles Dixon", "Sean Deming"], AuthorKeys: ["OL2X", "OL3X"],
        Contributors: [], FirstPublishYear: 1989, CoverId: null, EditionCount: 4);

    private static readonly Book Foundation = Work("/works/foundation", "Foundation", "Isaac Asimov");

    private static Book Work(string key, string title, string author) =>
        new(key, title, [author], [$"OL{key.Length}A"], [], 1950, null, 1);

    private static SearchIntent Intent(string? title, string? author) =>
        new(title, author, null, null, [], "test");

    private static RankedCandidates Rank(SearchIntent intent, params Book[] candidates) =>
        CandidateRanker.Rank(intent, candidates, 5);
}
