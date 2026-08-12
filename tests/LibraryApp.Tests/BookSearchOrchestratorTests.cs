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

        var result = await Orchestrate(IntentOf(), catalog).SearchAsync("???", 20, default);

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
    public async Task Drops_the_title_only_when_subjects_remain_to_search_on()
    {
        // With an author named, Loosen must NOT drop the title and re-filter by author — that
        // would pre-empt the author fallback, which answers "top works by this author" properly.
        var intent = IntentOf(title: "Dune", author: "Asimov", keywords: ["science_fiction"]);
        var catalog = CatalogReturning([], [Foundation]);

        var result = await Orchestrate(intent, catalog).SearchAsync("dune by asimov", 20, default);

        Assert.Equal(2, catalog.Received.Count);
        Assert.Null(catalog.Received[1].Title);
        Assert.Equal(["science_fiction"], catalog.Received[1].Keywords);
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
    public async Task Retries_are_capped_so_a_failing_search_stays_bounded()
    {
        // Every constraint present and nothing ever matches: without a cap this would walk every
        // combination, at a multi-second catalog round trip each.
        var intent = IntentOf(
            title: "T", author: "A", yearFrom: 1990, yearTo: 1999,
            keywords: ["a", "b", "c", "d", "e"]);
        var catalog = CatalogReturning([]);

        await Orchestrate(intent, catalog).SearchAsync("everything at once", 20, default);

        // Four search attempts, then one author lookup for the fallback — the retry loop is what is
        // capped, and the fallback is a single bounded step after it, not another retry. The fallback's
        // lookup is the only attempt carrying neither a title nor any subject.
        var fallbackLookups = catalog.Received.Count(i => i.Title is null && i.Keywords.Count == 0);

        Assert.Equal(1, fallbackLookups);
        Assert.Equal(4, catalog.Received.Count - fallbackLookups);
    }

    [Fact]
    public async Task Author_fallback_looks_up_the_authors_own_works_when_the_title_never_matches()
    {
        var intent = IntentOf(title: "Nonexistent", author: "Herbert");

        // Every retry finds nothing; the author lookup then finds a book carrying the author's key.
        var catalog = new StubCatalog
        {
            // Every retry still carries a title; the fallback's author lookup does not.
            Search = intent => string.IsNullOrWhiteSpace(intent.Title) ? [Dune] : [],
            WorksByAuthor = [BookOf("Dune Messiah", "Frank Herbert")]
        };

        var result = await Orchestrate(intent, catalog).SearchAsync("nonexistent by herbert", 5, default);

        Assert.Equal("Dune Messiah", Assert.Single(result.Matches).Book.Title);
        Assert.Equal(MatchTier.AuthorOnly, result.Matches[0].Tier);
        Assert.True(result.Broadened);
        Assert.Equal(Dune.Authors[0].Key, catalog.RequestedAuthorKey);
    }

    [Fact]
    public async Task Author_fallback_reasons_do_not_claim_filters_the_fallback_never_ran()
    {
        // The sibling test above covers the retry loop's effective intent. The fallback lives
        // outside that loop and had its own copy of the rule, which is how it came to keep the
        // subjects: /authors/{key}/works.json cannot take a subject filter at all.
        var intent = IntentOf(title: "Nonexistent", author: "Herbert", keywords: ["cyberpunk"]);

        var catalog = new StubCatalog
        {
            // Only the fallback's own lookup drops both the title and the subjects.
            Search = i => string.IsNullOrWhiteSpace(i.Title) && i.Keywords.Count == 0 ? [Dune] : [],
            WorksByAuthor = [BookOf("Dune Messiah", "Frank Herbert")]
        };

        var result = await Orchestrate(intent, catalog)
            .SearchAsync("nonexistent cyberpunk by herbert", 5, default);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Dune Messiah", match.Book.Title);
        Assert.DoesNotContain("tagged", match.Reason);
    }

    [Fact]
    public async Task Contributor_probe_runs_only_when_both_title_and_author_are_known()
    {
        var withBoth = CatalogReturning([Dune]);
        await Orchestrate(IntentOf(title: "Dune", author: "Herbert"), withBoth)
            .SearchAsync("dune by herbert", 5, default);
        Assert.Equal(["Dune Herbert"], withBoth.FreeTextQueries);

        // Free text over common subject words is the shape that was measured past 25s, so it must
        // not fire for a subject-only query.
        var subjectsOnly = CatalogReturning([Dune]);
        await Orchestrate(IntentOf(keywords: ["cyberpunk"]), subjectsOnly)
            .SearchAsync("cyberpunk", 5, default);
        Assert.Empty(subjectsOnly.FreeTextQueries);
    }

    [Fact]
    public async Task A_failing_probe_degrades_to_fielded_results_instead_of_failing_the_search()
    {
        // The probe can only add lower-tier candidates, so losing it must not cost the caller the
        // results the fielded query already returned.
        var catalog = new StubCatalog { Search = Replaying([Dune]), FreeTextThrows = true };

        var result = await Orchestrate(IntentOf(title: "Dune", author: "Herbert"), catalog)
            .SearchAsync("dune by herbert", 5, default);

        Assert.Equal("Dune", Assert.Single(result.Matches).Book.Title);
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

        var orchestrator = Orchestrate(IntentOf(author: "Herbert"), new StubCatalog());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.SearchAsync("herbert", 20, cts.Token));
    }

    // --- helpers ------------------------------------------------------------------------

    private static readonly Book Dune = BookOf("Dune", "Frank Herbert", 1965);
    private static readonly Book Foundation = BookOf("Foundation", "Isaac Asimov", 1951);

    private static Book BookOf(string title, string author, int? year = null) =>
        new($"/works/{title}", title, [new Author(author, $"OL{title.Length}A")], [], year, null);

    private static SearchIntent IntentOf(
        string? title = null,
        string? author = null,
        int? yearFrom = null,
        int? yearTo = null,
        string[]? keywords = null) =>
        new(title, author, yearFrom, yearTo, keywords ?? [], "test interpretation");

    private static BookSearchOrchestrator Orchestrate(SearchIntent intent, IBookCatalog catalog) =>
        new(new StubIntentParser(intent), catalog, NullLogger<BookSearchOrchestrator>.Instance);

    private static StubCatalog CatalogReturning(params IReadOnlyList<Book>[] responses) =>
        new() { Search = Replaying(responses) };

    /// <summary>Answers each call with the next canned response, repeating the last once exhausted.</summary>
    private static Func<SearchIntent, IReadOnlyList<Book>> Replaying(params IReadOnlyList<Book>[] responses)
    {
        var remaining = new Queue<IReadOnlyList<Book>>(responses);
        IReadOnlyList<Book> last = [];

        return _ =>
        {
            if (remaining.Count > 0) last = remaining.Dequeue();
            return last;
        };
    }

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

    /// <summary>
    /// The catalogue every test here runs against. Search answers are a rule over the intent rather
    /// than a fixed sequence, because the fallback path depends on what the intent still carries
    /// (a title or not) rather than on how many calls preceded it — <see cref="Replaying"/> adapts a
    /// plain sequence to that shape. Cancellation is honoured on every method, so a cancelled token
    /// behaves as the real catalogue would without needing a separate stub.
    /// </summary>
    private sealed class StubCatalog : IBookCatalog
    {
        public Func<SearchIntent, IReadOnlyList<Book>> Search { get; init; } = _ => [];
        public IReadOnlyList<Book> WorksByAuthor { get; init; } = [];
        public bool FreeTextThrows { get; init; }

        public List<SearchIntent> Received { get; } = [];
        public List<string> FreeTextQueries { get; } = [];
        public string? RequestedAuthorKey { get; private set; }

        public Task<IReadOnlyList<Book>> SearchAsync(
            SearchIntent intent, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Received.Add(intent);

            return Task.FromResult(Search(intent));
        }

        public Task<IReadOnlyList<Book>> SearchFreeTextAsync(
            string query, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FreeTextQueries.Add(query);

            if (FreeTextThrows) throw new SearchUnavailableException("probe is down");

            return Task.FromResult<IReadOnlyList<Book>>([]);
        }

        public Task<IReadOnlyList<Book>> GetWorksByAuthorAsync(
            string authorKey, string authorName, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedAuthorKey = authorKey;

            return Task.FromResult(WorksByAuthor);
        }
    }
}
