using BookSearchService.Application.Abstractions;
using BookSearchService.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace BookSearchService.Infrastructure.Gemini;

/// <summary>
/// Resolves a natural-language query into a <see cref="SearchIntent"/> using a single Gemini call.
/// </summary>
public sealed class GeminiSearchIntentParser(
    IChatClient chatClient,
    IOptions<GeminiOptions> options,
    ILogger<GeminiSearchIntentParser> logger) : ISearchIntentParser
{
    private readonly GeminiOptions _options = options.Value;

    private const string SystemPrompt =
        """
        You convert a reader's natural-language book request into structured search fields
        for the OpenLibrary catalog. Respond with JSON only.

        Fields:
          title    - only when the user named a specific work. Otherwise null.
          author   - only when the user named a person. Otherwise null. Surname alone is fine.
          yearFrom - inclusive lower bound on first publication year, else null.
          yearTo   - inclusive upper bound on first publication year, else null.
          keywords - OpenLibrary subject tokens describing theme, genre, setting or mood.
          interpretation - one short sentence, addressed to the reader, saying how you read
                           the request. Mention any word you had to disambiguate.

        Rules for keywords, which matter most:
          - Emit them as OpenLibrary subject tokens: lowercase, underscores between words.
            Good: science_fiction, space_opera, dystopia, detective_and_mystery_stories
            Bad:  "Science Fiction", sci-fi, "gritty space opera"
          - Prefer established subject headings over words lifted from the query. A request for
            "gritty scifi like the expanse" becomes science_fiction, space_opera - not gritty.
          - Give 2 to 5 keywords. They are combined with AND, so more means fewer results.
          - Drop mood words that are not real subjects (gritty, underrated, good, best).

        Never invent a title or author that the request did not mention. Leave a field null
        rather than guessing at it.

        Examples:
          "dune by frank herbert"
            -> title "Dune", author "Frank Herbert", keywords [science_fiction]
          "gritty space opera about corporate politics"
            -> title null, author null, keywords [science_fiction, space_opera]
          "cyberpunk dystopian novels from the 90s"
            -> yearFrom 1990, yearTo 1999, keywords [cyberpunk, dystopia, science_fiction]
        """;

    public async Task<SearchIntent> ParseAsync(string query, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, query)
        ];

        ChatResponse<ParsedIntent> response;
        try
        {
            response = await chatClient.GetResponseAsync<ParsedIntent>(
                messages,
                options: null,
                useJsonSchemaResponseFormat: _options.UseStrictJsonSchema,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SearchUnavailableException("Could not reach the search intent parser.", ex);
        }

        if (!response.TryGetResult(out var parsed))
        {
            logger.LogWarning("Gemini returned unparseable intent for {Query}: {Raw}", query, response.Text);
            throw new SearchUnavailableException("The search intent parser returned an unreadable response.");
        }

        return ToDomain(parsed);
    }

    private static SearchIntent ToDomain(ParsedIntent parsed)
    {
        var keywords = (parsed.Keywords ?? [])
            .Select(SubjectToken.Sanitize)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var (from, to) = OrderYears(parsed.YearFrom, parsed.YearTo);

        return new SearchIntent(
            Title: NullIfBlank(parsed.Title),
            Author: NullIfBlank(parsed.Author),
            YearFrom: from,
            YearTo: to,
            Keywords: keywords,
            Interpretation: parsed.Interpretation?.Trim() ?? string.Empty);
    }

    /// <summary>Guards against a transposed range, which OpenLibrary would silently return nothing for.</summary>
    private static (int? From, int? To) OrderYears(int? from, int? to) =>
        from is { } f && to is { } t && f > t ? (t, f) : (from, to);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
