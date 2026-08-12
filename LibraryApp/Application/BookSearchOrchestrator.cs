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

        var ladder = BroadeningLadder(parsed).ToArray();

        for (var rung = 0; rung < ladder.Length; rung++)
        {
            var attempt = ladder[rung];
            var books = await GatherCandidatesAsync(attempt, limit, cancellationToken);

            if (books.Count > 0)
            {
                // Ranking uses the effective intent, so a broadened search does not claim to have
                // matched constraints it actually dropped.
                var ranked = CandidateRanker.Rank(attempt, books, limit);

                logger.LogInformation(
                    "Ranked {Count} candidates, best tier {Tier}, clear winner {ClearWinner}",
                    ranked.Matches.Count, ranked.Matches.FirstOrDefault()?.Tier, ranked.ClearWinner);

                var matches = ranked.ClearWinner ? ranked.Matches.Take(1).ToArray() : ranked.Matches;

                return new BookSearchResult(attempt, rung > 0, ranked.ClearWinner, matches);
            }

            if (rung < ladder.Length - 1)
            {
                logger.LogInformation("No results at rung {Rung}; broadening the query", rung);
            }
        }

        // Nothing matched the title. If an author was named, fall back to that author's own works
        // rather than giving up — requirement (d).
        var authorFallback = await AuthorFallbackAsync(parsed, limit, cancellationToken);
        if (authorFallback.Count > 0)
        {
            var ranked = CandidateRanker.Rank(parsed with { Title = null }, authorFallback, limit);
            return new BookSearchResult(parsed, Broadened: true, ClearWinner: false, ranked.Matches);
        }

        logger.LogInformation("No results for {Query} after {Attempts} attempt(s)", query, ladder.Length);

        return new BookSearchResult(parsed, ladder.Length > 1, ClearWinner: false, Matches: []);
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

        await Task.WhenAll(fielded, probe);

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
        var byAuthor = await catalog.SearchAsync(
            new SearchIntent(null, intent.Author, null, null, [], intent.Interpretation),
            limit,
            cancellationToken);

        var match = byAuthor.FirstOrDefault(b => b.AuthorKeys.Count > 0
                                                && b.Authors.Any(a => NameKey.Matches(intent.Author, a)));

        if (match is null) return byAuthor;

        var index = match.Authors.ToList().FindIndex(a => NameKey.Matches(intent.Author, a));
        var keyIndex = index >= 0 && index < match.AuthorKeys.Count ? index : 0;

        logger.LogInformation("Author fallback via {AuthorKey}", match.AuthorKeys[keyIndex]);

        var works = await catalog.GetWorksByAuthorAsync(
            match.AuthorKeys[keyIndex], match.Authors[keyIndex], limit, cancellationToken);

        // Keep the search hits if the works endpoint comes back empty — having already paid for
        // them, returning nothing would be worse than returning the looser set.
        return works.Count > 0 ? works : byAuthor;
    }

    /// <summary>
    /// Each attempt is a multi-second catalog round trip, so the ladder is capped rather than
    /// exhaustive. Four rungs bounds a failing search at roughly ten seconds.
    /// </summary>
    private const int MaxAttempts = 4;

    /// <summary>
    /// Progressively looser intents, stopping at the first that returns anything.
    /// </summary>
    private static IEnumerable<SearchIntent> BroadeningLadder(SearchIntent intent)
        => Relaxations(intent).Take(MaxAttempts);

    /// <summary>
    /// Relaxations ordered from least to most likely to matter to the reader. Deterministic —
    /// recovering from an over-narrow parse never costs another AI call.
    /// </summary>
    private static IEnumerable<SearchIntent> Relaxations(SearchIntent intent)
    {
        yield return intent;

        // Year bounds first: the most brittle thing to infer from prose ("from the 90s").
        if (intent.YearFrom is not null || intent.YearTo is not null)
        {
            intent = intent with { YearFrom = null, YearTo = null };
            yield return intent;
        }

        // Dropping the title only helps when subjects remain to search on. When an author was named
        // the author fallback takes over instead — it uses the author's own works endpoint, which
        // answers "top works by this author" directly rather than approximating it with a filter.
        if (!string.IsNullOrWhiteSpace(intent.Title) && intent.Keywords.Count > 0)
        {
            intent = intent with { Title = null };
            yield return intent;
        }

        // Subjects are ANDed, so a single invented token zeroes the entire result set — asking for
        // "gritty space opera about corporate politics" yields science_fiction AND space_opera AND
        // corporate_politics, which matches nothing, while the first two match 2,746 works. The
        // model puts its speculative tokens last, so trim from the tail and keep at least one.
        while (intent.Keywords.Count > 1)
        {
            intent = intent with { Keywords = intent.Keywords.Take(intent.Keywords.Count - 1).ToArray() };
            yield return intent;
        }
    }
}
