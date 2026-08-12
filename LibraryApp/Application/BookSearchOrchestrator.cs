using LibraryApp.Application.Abstractions;
using LibraryApp.Domain;

namespace LibraryApp.Application;

/// <summary>
/// The search pipeline: parse the query with AI, retrieve from the catalog, explain the matches.
/// </summary>
/// <remarks>
/// Ordering matters. The AI runs <em>before</em> retrieval rather than re-ranking after it, because
/// a natural-language string sent straight to OpenLibrary's free-text search returns nothing for
/// vague queries and times out for plain multi-term ones. Ranking cannot recover documents that
/// were never retrieved, so intent parsing has to shape the query itself.
/// </remarks>
public sealed class BookSearchOrchestrator(
    ISearchIntentParser intentParser,
    IBookCatalog catalog,
    ILogger<BookSearchOrchestrator> logger)
{
    public async Task<BookSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var parsed = await intentParser.ParseAsync(query, cancellationToken);

        logger.LogInformation(
            "Parsed {Query} to title={Title} author={Author} years={From}-{To} keywords={Keywords}",
            query, parsed.Title, parsed.Author, parsed.YearFrom, parsed.YearTo, parsed.Keywords);

        if (parsed.IsEmpty)
        {
            logger.LogWarning("Intent for {Query} had no searchable fields; returning no matches", query);
            return new BookSearchResult(parsed, Broadened: false, ClearWinner: false, Matches: []);
        }

        // Search with the query as parsed. If the catalogue finds nothing, drop one constraint and
        // search again, until it finds something or there is nothing left to drop.
        var attempt = parsed;
        var attempts = 0;

        while (attempts < MaxAttempts)
        {
            attempts++;

            var books = await GatherCandidatesAsync(attempt, limit, cancellationToken);

            if (books.Count > 0)
            {
                // Rank against the query that actually ran, so a loosened search does not claim to
                // have matched constraints it dropped.
                var ranked = CandidateRanker.Rank(attempt, books, limit);

                logger.LogInformation(
                    "Ranked {Count} candidates, best tier {Tier}, clear winner {ClearWinner}",
                    ranked.Matches.Count, ranked.Matches.FirstOrDefault()?.Tier, ranked.ClearWinner);

                var matches = ranked.ClearWinner ? ranked.Matches.Take(1).ToArray() : ranked.Matches;

                return new BookSearchResult(attempt, attempts > 1, ranked.ClearWinner, matches);
            }

            if (Loosen(attempt) is not { } looser) break;

            logger.LogInformation("Attempt {Attempt} found nothing; loosening the query", attempts);
            attempt = looser;
        }

        // Still nothing. If an author was named, return that author's own works instead of an empty
        // list — requirement (d).
        var authorFallback = await AuthorFallbackAsync(parsed, limit, cancellationToken);
        if (authorFallback.Count > 0)
        {
            // Rank against what the fallback actually ran. Carrying the parsed subjects or years in
            // here would have every result explain itself as "tagged cyberpunk" for a filter the
            // works endpoint cannot even take.
            var ranked = CandidateRanker.Rank(AuthorAlone(parsed), authorFallback, limit);
            return new BookSearchResult(parsed, Broadened: true, ClearWinner: false, ranked.Matches);
        }

        logger.LogInformation("No results for {Query} after {Attempts} attempt(s)", query, attempts);

        return new BookSearchResult(parsed, attempts > 1, ClearWinner: false, Matches: []);
    }

    /// <summary>
    /// Retrieves candidates for one intent, adding a free-text probe when both a title and an author
    /// were named.
    /// </summary>
    /// <remarks>
    /// The probe exists because <c>author=</c> only matches primary authors, so a work the named
    /// person merely narrated or illustrated cannot come back from the fielded query — that is the
    /// entire contributor tier. The two run concurrently: each is roughly two seconds, so together
    /// they cost about what the fielded query alone used to.
    /// <para>
    /// The probe is best-effort by design. It can only <em>add</em> lower-tier candidates, so losing
    /// it degrades ranking rather than correctness, and a slow free-text query should not fail a
    /// search the fielded path already answered. A failure of the fielded query still propagates.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Book>> GatherCandidatesAsync(
        SearchIntent intent, int limit, CancellationToken cancellationToken)
    {
        var fielded = catalog.SearchAsync(intent, limit, cancellationToken);

        if (string.IsNullOrWhiteSpace(intent.Title) || string.IsNullOrWhiteSpace(intent.Author))
        {
            return await fielded;
        }

        var probe = ProbeAsync($"{intent.Title} {intent.Author}", limit, cancellationToken);

        // Both are already in flight, so this is the one place that waits — and it observes both,
        // rather than leaving a faulted probe unobserved.
        await Task.WhenAll(fielded, probe);

        // .Result rather than await, deliberately. WhenAll above has already thrown if either task
        // faulted, so both are complete and successful by the time we get here: reading them is a
        // fetch, not a wait. An await would tell the reader to expect a suspension point that
        // cannot occur — and there is no AggregateException to unwrap, because a fault never
        // reaches this line.
        //
        // Fielded results first so their relevance order survives as the within-tier tiebreak.
        return fielded.Result
                      .Concat(probe.Result)
                      .DistinctBy(book => book.Key, StringComparer.Ordinal)
                      .ToArray();
    }

    private async Task<IReadOnlyList<Book>> ProbeAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.SearchFreeTextAsync(query, limit, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Contributor probe failed for {Query}; ranking on fielded results only", query);
            return [];
        }
    }

    /// <summary>Requirement (d): with no title match left, return the named author's own works.</summary>
    private async Task<IReadOnlyList<Book>> AuthorFallbackAsync(
        SearchIntent intent, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(intent.Author)) return [];

        // The author key only comes from a search hit, so find the author before listing their works.
        var byAuthor = await catalog.SearchAsync(AuthorAlone(intent), limit, cancellationToken);

        var match = byAuthor
            .SelectMany(book => book.Authors)
            .FirstOrDefault(author => NameKey.Matches(intent.Author, author.Name));

        if (match is null) return byAuthor;

        logger.LogInformation("Author fallback via {AuthorKey}", match.Key);

        var works = await catalog.GetWorksByAuthorAsync(match.Key, match.Name, limit, cancellationToken);

        // Keep the search hits if the works endpoint comes back empty — having already paid for
        // them, returning nothing would be worse than returning the looser set.
        return works.Count > 0 ? works : byAuthor;
    }

    /// <summary>
    /// The intent stripped to the author — what the fallback searches on, and therefore the only
    /// thing its results may claim to have matched. Both facts come from one place so the query and
    /// the explanation of it cannot drift apart.
    /// </summary>
    private static SearchIntent AuthorAlone(SearchIntent intent) =>
        intent with { Title = null, YearFrom = null, YearTo = null, Keywords = [] };

    /// <summary>Retries cost a multi-second catalog call each, so a failing search stops after four.</summary>
    private const int MaxAttempts = 4;

    /// <summary>
    /// Returns the same query with one constraint removed, or <c>null</c> when nothing is left to
    /// remove. Dropping the least important constraint first means the narrowest query that still
    /// finds something is the one that wins.
    /// </summary>
    private static SearchIntent? Loosen(SearchIntent intent)
    {
        // Years go first: the most brittle thing to infer from prose ("from the 90s").
        if (intent.YearFrom is not null || intent.YearTo is not null)
        {
            return intent with { YearFrom = null, YearTo = null };
        }

        // Then the title, but only if subjects remain to search on. When an author was named the
        // author fallback handles it instead, via that author's own works endpoint.
        if (!string.IsNullOrWhiteSpace(intent.Title) && intent.Keywords.Count > 0)
        {
            return intent with { Title = null };
        }

        // Then the last subject. Subjects are ANDed, so one invented token zeroes the whole result
        // set — "gritty space opera about corporate politics" becomes science_fiction AND
        // space_opera AND corporate_politics, matching nothing, where the first two match 2,746
        // works. The model puts its guesses last, so the tail is what goes.
        if (intent.Keywords.Count > 1)
        {
            return intent with { Keywords = intent.Keywords.SkipLast(1).ToArray() };
        }

        return null;
    }
}
