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
            return new BookSearchResult(parsed, Broadened: false, Matches: []);
        }

        var ladder = BroadeningLadder(parsed).ToArray();

        for (var rung = 0; rung < ladder.Length; rung++)
        {
            var attempt = ladder[rung];
            var books = await catalog.SearchAsync(attempt, limit, cancellationToken);

            if (books.Count > 0)
            {
                // Explanations use the effective intent, so a broadened search does not claim
                // to have matched constraints it actually dropped.
                var matches = books
                    .Take(limit)
                    .Select(book => new BookMatch(book, MatchExplainer.Explain(book, attempt)))
                    .ToArray();

                return new BookSearchResult(attempt, Broadened: rung > 0, matches);
            }

            if (rung < ladder.Length - 1)
            {
                logger.LogInformation("No results at rung {Rung}; broadening the query", rung);
            }
        }

        logger.LogInformation("No results for {Query} after {Attempts} attempt(s)", query, ladder.Length);

        return new BookSearchResult(parsed, Broadened: ladder.Length > 1, Matches: []);
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

        // Only worth dropping the title if something else remains to search on.
        var hasOtherSignal = !string.IsNullOrWhiteSpace(intent.Author) || intent.Keywords.Count > 0;

        if (!string.IsNullOrWhiteSpace(intent.Title) && hasOtherSignal)
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
