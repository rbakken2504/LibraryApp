using LibraryApp.Application;
using LibraryApp.Application.Abstractions;
using LibraryApp.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibraryApp.Tests;

public class BookSearchOrchestratorTests
{
    [Fact]
    public async Task Returns_matches_with_reasons_on_the_happy_path()
    {
        var intent = IntentOf(title: "Dune", author: "Herbert");
        var catalog = CatalogReturning([Dune]);

        var result = await Orchestrate(intent, catalog).SearchAsync("dune by herbert", 20, default);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Dune", match.Book.Title);
        Assert.False(result.Broadened);
        Assert.Contains("Frank Herbert", match.Reason);
    }

    [Fact]
    public async Task Skips_the_catalog_entirely_when_the_intent_has_nothing_to_search_on()
    {
        var catalog = CatalogReturning([Dune]);

        var result = await Orchestrate(SearchIntent.Empty, catalog).SearchAsync("???", 20, default);

        Assert.Empty(result.Matches);
        Assert.False(result.Broadened);
        Assert.Empty(catalog.Received);
    }

    [Fact]
    public async Task Drops_the_year_range_first_when_the_parsed_intent_returns_nothing()
    {
        var intent = IntentOf(title: "Dune", author: "Herbert", yearFrom: 2000, yearTo: 2010);
        var catalog = CatalogReturning([], [Dune]);

        var result = await Orchestrate(intent, catalog).SearchAsync("dune by herbert from the 2000s", 20, default);

        Assert.Equal(2, catalog.Received.Count);
        Assert.Null(catalog.Received[1].YearFrom);
        Assert.Null(catalog.Received[1].YearTo);
        Assert.Equal("Dune", catalog.Received[1].Title);   // title survives the first broadening
        Assert.True(result.Broadened);
        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task Drops_the_title_next_when_another_signal_remains()
    {
        var intent = IntentOf(title: "Dune", author: "Asimov");
        var catalog = CatalogReturning([], [Foundation]);

        var result = await Orchestrate(intent, catalog).SearchAsync("dune by asimov", 20, default);

        Assert.Equal(2, catalog.Received.Count);
        Assert.Null(catalog.Received[1].Title);
        Assert.Equal("Asimov", catalog.Received[1].Author);
        Assert.True(result.Broadened);
        Assert.Equal("Foundation", Assert.Single(result.Matches).Book.Title);
    }

    [Fact]
    public async Task Does_not_broaden_when_there_is_nothing_left_to_drop()
    {
        var intent = IntentOf(keywords: ["cyberpunk"]);
        var catalog = CatalogReturning([]);

        var result = await Orchestrate(intent, catalog).SearchAsync("cyberpunk", 20, default);

        Assert.Single(catalog.Received);
        Assert.Empty(result.Matches);
        Assert.False(result.Broadened);
    }

    [Fact]
    public async Task Trims_speculative_keywords_from_the_tail()
    {
        // Subjects are ANDed, so one invented token zeroes the result set. The model puts its
        // speculative guesses last, so the tail is what gets dropped first.
        var intent = IntentOf(keywords: ["science_fiction", "space_opera", "corporate_politics"]);
        var catalog = CatalogReturning([], [Dune]);

        var result = await Orchestrate(intent, catalog)
            .SearchAsync("gritty space opera about corporate politics", 20, default);

        Assert.Equal(2, catalog.Received.Count);
        Assert.Equal(["science_fiction", "space_opera"], catalog.Received[1].Keywords);
        Assert.True(result.Broadened);
        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task Keyword_trimming_always_leaves_at_least_one_subject()
    {
        var intent = IntentOf(keywords: ["a", "b", "c"]);
        var catalog = CatalogReturning([]);

        await Orchestrate(intent, catalog).SearchAsync("nothing matches", 20, default);

        Assert.All(catalog.Received, received => Assert.NotEmpty(received.Keywords));
        Assert.Equal(["a"], catalog.Received[^1].Keywords);
    }

    [Fact]
    public async Task Ladder_is_capped_so_a_failing_search_stays_bounded()
    {
        // Every constraint present and nothing ever matches: without a cap this would walk every
        // combination, at a multi-second catalog round trip each.
        var intent = IntentOf(
            title: "T", author: "A", yearFrom: 1990, yearTo: 1999,
            keywords: ["a", "b", "c", "d", "e"]);
        var catalog = CatalogReturning([]);

        await Orchestrate(intent, catalog).SearchAsync("everything at once", 20, default);

        Assert.Equal(4, catalog.Received.Count);
    }

    [Fact]
    public async Task Reasons_describe_the_effective_intent_not_the_dropped_constraints()
    {
        var intent = IntentOf(author: "Herbert", yearFrom: 2000, yearTo: 2010);
        var catalog = CatalogReturning([], [Dune]);

        var result = await Orchestrate(intent, catalog).SearchAsync("herbert from the 2000s", 20, default);

        // Dune is from 1965; claiming it matched a 2000-2010 range would be a lie.
        var reason = Assert.Single(result.Matches).Reason;
        Assert.DoesNotContain("2000", reason);
        Assert.Contains("Frank Herbert", reason);
    }

    [Fact]
    public async Task Never_returns_more_than_the_requested_limit()
    {
        var many = Enumerable.Range(0, 50).Select(i => BookOf($"Book {i}", "Someone")).ToArray();
        var catalog = CatalogReturning(many);

        var result = await Orchestrate(IntentOf(author: "Someone"), catalog).SearchAsync("someone", 5, default);

        Assert.Equal(5, result.Matches.Count);
    }

    [Fact]
    public async Task Parser_failures_surface_rather_than_being_swallowed()
    {
        var orchestrator = new BookSearchOrchestrator(
            new ThrowingIntentParser(),
            CatalogReturning([Dune]),
            NullLogger<BookSearchOrchestrator>.Instance);

        await Assert.ThrowsAsync<SearchUnavailableException>(
            () => orchestrator.SearchAsync("anything", 20, default));
    }

    [Fact]
    public async Task Cancellation_propagates_to_the_catalog()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var orchestrator = Orchestrate(IntentOf(author: "Herbert"), new CancellationAwareCatalog());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.SearchAsync("herbert", 20, cts.Token));
    }

    // --- helpers ------------------------------------------------------------------------

    private static readonly Book Dune = BookOf("Dune", "Frank Herbert", 1965);
    private static readonly Book Foundation = BookOf("Foundation", "Isaac Asimov", 1951);

    private static Book BookOf(string title, string author, int? year = null) =>
        new($"/works/{title}", title, [author], year, null, 1);

    private static SearchIntent IntentOf(
        string? title = null,
        string? author = null,
        int? yearFrom = null,
        int? yearTo = null,
        string[]? keywords = null) =>
        new(title, author, yearFrom, yearTo, keywords ?? [], "test interpretation");

    private static BookSearchOrchestrator Orchestrate(SearchIntent intent, IBookCatalog catalog) =>
        new(new StubIntentParser(intent), catalog, NullLogger<BookSearchOrchestrator>.Instance);

    private static StubCatalog CatalogReturning(params IReadOnlyList<Book>[] responses) => new(responses);

    private sealed class StubIntentParser(SearchIntent intent) : ISearchIntentParser
    {
        public Task<SearchIntent> ParseAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult(intent);
    }

    private sealed class ThrowingIntentParser : ISearchIntentParser
    {
        public Task<SearchIntent> ParseAsync(string query, CancellationToken cancellationToken)
            => throw new SearchUnavailableException("upstream is down");
    }

    /// <summary>Replays one canned response per call, repeating the last once exhausted.</summary>
    private sealed class StubCatalog(IReadOnlyList<Book>[] responses) : IBookCatalog
    {
        public List<SearchIntent> Received { get; } = [];

        public Task<IReadOnlyList<Book>> SearchAsync(
            SearchIntent intent, int limit, CancellationToken cancellationToken)
        {
            Received.Add(intent);

            if (responses.Length == 0) return Task.FromResult<IReadOnlyList<Book>>([]);

            return Task.FromResult(responses[Math.Min(Received.Count - 1, responses.Length - 1)]);
        }
    }

    private sealed class CancellationAwareCatalog : IBookCatalog
    {
        public Task<IReadOnlyList<Book>> SearchAsync(
            SearchIntent intent, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<Book>>([]);
        }
    }
}
