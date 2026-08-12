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

        // Title lengths deliberately differ. Every fixture folding to one token would let this pass
        // whether or not the tiebreak stayed neutral — the long title has to lead for it to mean
        // anything, since surplus would otherwise sort it last.
        var result = Rank(intent, HobbitCollection, Dune, Foundation);

        Assert.All(result.Matches, m => Assert.Equal(MatchTier.Discovery, m.Tier));
        Assert.Equal(
            ["The Hobbit & The Lord of the Rings [collection/set]", "Dune", "Foundation"],
            result.Matches.Select(m => m.Book.Title));
        Assert.False(result.ClearWinner);
    }

    [Fact]
    public void Works_that_match_the_author_but_not_the_title_are_kept_as_that_authors_other_works()
    {
        var result = Rank(Intent("Nonexistent", "Herbert"), Dune);

        Assert.Equal(MatchTier.AuthorOnly, Assert.Single(result.Matches).Tier);
    }

    [Fact]
    public void An_author_only_reason_names_the_person_asked_about_exactly_once()
    {
        // The catalogue lists Frank first, but the reader asked about Brian. Tier and reason come
        // from one lookup, so the banner cannot name a different person than the match found — and
        // since the banner already names them, nothing repeats it.
        var coAuthored = new Book(
            "/works/house-atreides", "House Atreides",
            Authors: [new Author("Frank Herbert", "OL79034A"), new Author("Brian Herbert", "OL79035A")],
            Contributors: [], FirstPublishYear: 1999, CoverId: null);

        var match = Assert.Single(Rank(Intent("Nonexistent", "Brian Herbert"), coAuthored).Matches);

        Assert.Equal(MatchTier.AuthorOnly, match.Tier);
        Assert.Equal("Another work by Brian Herbert.", match.Reason);
    }

    [Fact]
    public void A_title_with_no_author_named_cannot_reach_the_corroborated_tiers()
    {
        var result = Rank(Intent("Dune", author: null), Dune);

        Assert.Equal(MatchTier.TitleOnly, Assert.Single(result.Matches).Tier);
        Assert.False(result.ClearWinner);
    }

    [Fact]
    public void A_candidate_matching_neither_the_title_nor_the_author_falls_to_the_bottom()
    {
        // The catalogue returned it, but nothing about it answers what was asked. It is kept rather
        // than dropped, so a thin result set can still be filled out.
        var result = Rank(Intent("Nonexistent", "Nobody"), Dune);

        Assert.Equal(MatchTier.Discovery, Assert.Single(result.Matches).Tier);
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
        Authors: [new Author("Frank Herbert", "OL79034A"), new Author("Френк Герберт", "OL7388009A")],
        Contributors: ["Scott Brick (Narrator)", "John Schoenherr (Illustrator)"],
        FirstPublishYear: 1965, CoverId: 392508);

    private static readonly Book DuneNarratedEdition = new(
        "/works/dune-audio", "Dune",
        Authors: [new Author("Some Publisher", "OL1X")],
        Contributors: ["Scott Brick (Narrator)", "Frank Herbert (Author)"],
        FirstPublishYear: 2007, CoverId: null);

    private static readonly Book Hobbit = Work("/works/OL27482W", "The Hobbit", "J.R.R. Tolkien");

    private static readonly Book HobbitThereAndBack =
        Work("/works/hobbit-full", "The Hobbit, or There and Back Again", "J.R.R. Tolkien");

    private static readonly Book HobbitCollection =
        Work("/works/OL14926019W", "The Hobbit & The Lord of the Rings [collection/set]", "J.R.R. Tolkien");

    /// <summary>The graphic novel: exact title, Tolkien nowhere in its author list.</summary>
    private static readonly Book HobbitAdaptation = new(
        "/works/OL219602W", "The Hobbit",
        Authors: [new Author("Charles Dixon", "OL2X"), new Author("Sean Deming", "OL3X")],
        Contributors: [], FirstPublishYear: 1989, CoverId: null);

    private static readonly Book Foundation = Work("/works/foundation", "Foundation", "Isaac Asimov");

    private static Book Work(string key, string title, string author) =>
        new(key, title, [new Author(author, $"OL{key.Length}A")], [], 1950, null);

    private static SearchIntent Intent(string? title, string? author) =>
        new(title, author, null, null, [], "test");

    private static RankedCandidates Rank(SearchIntent intent, params Book[] candidates) =>
        CandidateRanker.Rank(intent, candidates, 5);
}
