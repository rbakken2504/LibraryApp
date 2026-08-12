using LibraryApp.Domain;

namespace LibraryApp.Tests;

public class TitleKeyTests
{
    [Theory]
    [InlineData("The Hobbit", "the hobbit", TitleMatch.Exact)]
    [InlineData("the hobbit", "The Hobbit!", TitleMatch.Exact)]
    [InlineData("The Hobbit", "Hobbit, The", TitleMatch.Exact)]
    [InlineData("Les Misérables", "Les Miserables", TitleMatch.Exact)]
    [InlineData("The Hobbit", "The Hobbit, or There and Back Again", TitleMatch.Near)]
    [InlineData("The Hobbit", "The Hobbit & The Lord of the Rings [collection/set]", TitleMatch.Near)]
    [InlineData("The Hobbit", "Dune", TitleMatch.None)]
    [InlineData("The Hobbit, or There and Back Again", "The Hobbit", TitleMatch.None)]
    [InlineData(null, "The Hobbit", TitleMatch.None)]
    [InlineData("The Hobbit", null, TitleMatch.None)]
    public void Compare_classifies_the_candidate_against_what_was_asked_for(
        string? wanted, string? candidate, TitleMatch expected)
        => Assert.Equal(expected, TitleKey.Compare(wanted, candidate).Match);

    [Fact]
    public void Words_that_only_matter_in_a_query_are_never_stripped_from_a_title()
    {
        // QueryNormalizer drops "book", "novel", "find" and "some" because collapsing queries is
        // the point of a cache key. Reusing it here would strip the word that distinguishes
        // "The Book Thief" and match a pile of unrelated works. This test keeps the two apart.
        Assert.Equal(["book", "thief"], TitleKey.Tokens("The Book Thief").Order());
        Assert.Equal(TitleMatch.Exact, TitleKey.Compare("The Book Thief", "the book thief").Match);
        Assert.Equal(TitleMatch.None, TitleKey.Compare("The Book Thief", "The Thief").Match);

        // The contrast, made explicit: the cache key loses "book" where the title key keeps it.
        // It retains "the", which is what stops the key colliding with a search for "thief".
        Assert.Equal("the thief", QueryNormalizer.Canonicalize("The Book Thief"));
        Assert.NotEqual(QueryNormalizer.Fingerprint("The Book Thief"), QueryNormalizer.Fingerprint("thief"));
    }

    [Fact]
    public void Articles_are_dropped_but_a_title_made_only_of_articles_survives()
    {
        Assert.Equal(["hobbit"], TitleKey.Tokens("The Hobbit").Order());
        Assert.NotEmpty(TitleKey.Tokens("The A"));
    }

    [Theory]
    [InlineData("The Hobbit", "The Hobbit", 0)]
    [InlineData("The Hobbit", "The Hobbit, or There and Back Again", 3)]
    [InlineData("The Hobbit", "The Hobbit & The Lord of the Rings [collection/set]", 4)]
    public void Surplus_measures_how_much_wider_the_candidate_is(
        string wanted, string candidate, int expected)
        => Assert.Equal(expected, TitleKey.Compare(wanted, candidate).Surplus);

    [Fact]
    public void Dropping_connectives_lets_a_real_subtitle_outrank_a_bundle()
    {
        // Counting "or"/"and"/"of" leaves these tied, and the tiebreak stops discriminating.
        var subtitle = TitleKey.Compare("The Hobbit", "The Hobbit, or There and Back Again").Surplus;
        var bundle = TitleKey.Compare("The Hobbit", "The Hobbit & The Lord of the Rings [collection/set]").Surplus;

        Assert.True(subtitle < bundle, $"subtitle surplus {subtitle} should be below bundle surplus {bundle}");
    }

    [Fact]
    public void Function_words_never_swallow_a_meaningful_title()
    {
        Assert.Equal(["men", "mice"], TitleKey.Tokens("Of Mice and Men").Order());
        Assert.Equal(["lord", "rings"], TitleKey.Tokens("The Lord of the Rings").Order());
        Assert.NotEmpty(TitleKey.Tokens("The One and Only"));
    }
}
