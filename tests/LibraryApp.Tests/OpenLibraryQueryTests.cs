using LibraryApp.Domain;
using LibraryApp.Infrastructure.OpenLibrary;

namespace LibraryApp.Tests;

/// <summary>
/// Guards the query-shape rules that came out of measuring the live OpenLibrary API:
/// dedicated params answer in ~3.5s where the equivalent Solr boolean took ~18.8s, and subjects
/// must be <c>subject:</c> filters because the same words as free text time out past 25s.
/// </summary>
public class OpenLibraryQueryTests
{
    [Fact]
    public void Title_and_author_use_dedicated_params_not_a_solr_string()
    {
        var url = OpenLibraryBookCatalog.BuildUrl(IntentOf(title: "Dune", author: "Herbert"), 20);

        Assert.Contains("title=Dune", url);
        Assert.Contains("author=Herbert", url);
        Assert.DoesNotContain("title%3A", url);   // no "title:" inside q=
        Assert.DoesNotContain("q=", url);
    }

    [Fact]
    public void Keywords_become_anded_subject_filters()
    {
        var url = OpenLibraryBookCatalog.BuildUrl(IntentOf(keywords: ["cyberpunk", "dystopia"]), 20);

        Assert.Contains("q=subject%3Acyberpunk%20AND%20subject%3Adystopia", url);
    }

    [Theory]
    [InlineData(1980, 2000, "first_publish_year%3A%5B1980%20TO%202000%5D")]
    [InlineData(1980, null, "first_publish_year%3A%5B1980%20TO%20*%5D")]
    [InlineData(null, 2000, "first_publish_year%3A%5B*%20TO%202000%5D")]
    public void Year_bounds_become_a_solr_range_with_wildcards_for_open_ends(
        int? from, int? to, string expected)
    {
        var url = OpenLibraryBookCatalog.BuildUrl(IntentOf(yearFrom: from, yearTo: to), 20);

        Assert.Contains(expected, url);
    }

    [Fact]
    public void Subjects_and_year_range_combine_into_one_q_parameter()
    {
        var url = OpenLibraryBookCatalog.BuildUrl(
            IntentOf(keywords: ["cyberpunk"], yearFrom: 1990, yearTo: 1999), 20);

        Assert.Single(url.Split("q=") [1..]);   // exactly one q parameter
        Assert.Contains("subject%3Acyberpunk%20AND%20first_publish_year", url);
    }

    [Fact]
    public void Payload_is_always_trimmed_by_fields_and_limit()
    {
        var url = OpenLibraryBookCatalog.BuildUrl(IntentOf(author: "Herbert"), 7);

        Assert.Contains("fields=", url);
        Assert.Contains("first_publish_year", url);
        Assert.Contains("limit=7", url);
    }

    [Fact]
    public void Values_needing_escaping_are_url_encoded()
    {
        var url = OpenLibraryBookCatalog.BuildUrl(IntentOf(author: "Ursula K. Le Guin"), 20);

        Assert.DoesNotContain("Le Guin", url);
        Assert.Contains("Ursula%20K.%20Le%20Guin", url);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(new int[0], null)]
    [InlineData(new[] { -1 }, null)]              // the endpoint's documented "no cover" placeholder
    [InlineData(new[] { -1, 392508 }, 392508)]
    [InlineData(new[] { 392508, 11481354 }, 392508)]
    public void A_missing_cover_stays_missing_rather_than_becoming_cover_zero(int[]? covers, int? expected)
    {
        // FirstOrDefault over int reports absence as 0, and cover id 0 is a URL that 404s — the
        // placeholder would reach the browser as a broken image rather than the "No cover" tile.
        Assert.Equal(expected, OpenLibraryBookCatalog.FirstUsableCover(covers));
    }

    private static SearchIntent IntentOf(
        string? title = null,
        string? author = null,
        int? yearFrom = null,
        int? yearTo = null,
        string[]? keywords = null) =>
        new(title, author, yearFrom, yearTo, keywords ?? [], "test");
}
