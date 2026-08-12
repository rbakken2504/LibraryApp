using LibraryApp.Domain;

namespace LibraryApp.Tests;

public class QueryNormalizerTests
{
    [Theory]
    [InlineData("Books by Stephen King", "king stephen")]
    [InlineData("king stephen BOOKS", "king stephen")]
    [InlineData("KING, Stephen books!", "king stephen")]
    [InlineData("  stephen   king  ", "king stephen")]
    [InlineData("Émile Zola", "emile zola")]
    [InlineData("sci-fi novels", "fi sci")]
    [InlineData("dune dune dune", "dune")]
    public void Canonicalize_produces_the_expected_canonical_form(string raw, string expected)
        => Assert.Equal(expected, QueryNormalizer.Canonicalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ???")]
    public void Canonicalize_returns_empty_for_queries_with_no_content(string? raw)
        => Assert.Equal(string.Empty, QueryNormalizer.Canonicalize(raw));

    [Theory]
    [InlineData("Books by Stephen King", "KING, Stephen books!")]
    [InlineData("cyberpunk dystopia", "dystopia, cyberpunk")]
    [InlineData("The Expanse", "expanse")]
    public void Queries_that_mean_the_same_thing_share_a_fingerprint(string first, string second)
        => Assert.Equal(QueryNormalizer.Fingerprint(first), QueryNormalizer.Fingerprint(second));

    [Theory]
    [InlineData("stephen king", "ursula le guin")]
    [InlineData("cyberpunk novels", "cozy mystery novels")]
    [InlineData("dune", "dune messiah")]
    public void Genuinely_different_queries_do_not_collide(string first, string second)
        => Assert.NotEqual(QueryNormalizer.Fingerprint(first), QueryNormalizer.Fingerprint(second));

    [Fact]
    public void A_query_of_nothing_but_stop_words_keeps_its_tokens()
    {
        // Otherwise every such query would canonicalize to "" and share one cache entry.
        Assert.Equal("book the", QueryNormalizer.Canonicalize("the book"));
        Assert.NotEqual(QueryNormalizer.Fingerprint("the book"), QueryNormalizer.Fingerprint("the novel"));
    }

    [Fact]
    public void Fingerprint_is_url_safe_and_stable()
    {
        var fingerprint = QueryNormalizer.Fingerprint("gritty space opera");

        Assert.Equal(fingerprint, QueryNormalizer.Fingerprint("gritty space opera"));
        Assert.DoesNotContain('+', fingerprint);
        Assert.DoesNotContain('/', fingerprint);
        Assert.DoesNotContain('=', fingerprint);
    }
}
