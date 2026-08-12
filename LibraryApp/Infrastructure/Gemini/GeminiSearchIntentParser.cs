using LibraryApp.Application.Abstractions;
using LibraryApp.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LibraryApp.Infrastructure.Gemini;

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
          title    - the work the reader means, whenever they have pointed at one. Usually that is
                     a title they typed, but a character or a half-remembered fragment identifies a
                     work just as precisely: "huckleberry" is Adventures of Huckleberry Finn. Null
                     when no particular work is indicated.
          author   - any real person the user named, whatever their credit: wrote it, narrated it,
                     illustrated it, edited it, translated it. Extract the name either way and let
                     ranking work out how they were credited. Otherwise null. Surname alone is fine,
                     and a bare name pointing at one obvious writer resolves to them: "austen" is
                     Jane Austen. Never put a fictional character here — they identify the work,
                     not the person who wrote it.
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

        Ambiguous fragments matter as much as keywords. Readers often type two half-remembered
        words rather than a title — a character, a surname, or one of each. Resolve them when they
        point somewhere definite, and say in interpretation what you resolved:
          - "mark huckleberry" is Mark Twain and Huckleberry Finn, not a man named Mark Huckleberry.
          - "austen bennet" is Jane Austen and Elizabeth Bennet, so the work is Pride and Prejudice.
        Treating a pair of fragments as one person's full name is almost always the wrong reading.

        Resolving a reference is not the same as inventing one. Fill a field when the request points
        at something recognisable, however partially, and leave it null when you would be guessing:
        "gritty space opera" names no particular work, so its title stays null.

        Examples:
          "dune by frank herbert"
            -> title "Dune", author "Frank Herbert", keywords [science_fiction]
          "dune narrated by scott brick"
            -> title "Dune", author "Scott Brick", keywords [science_fiction]
          "mark huckleberry"
            -> title "Adventures of Huckleberry Finn", author "Mark Twain",
               interpretation reads "mark" as Mark Twain and "huckleberry" as the character
          "austen bennet"
            -> title "Pride and Prejudice", author "Jane Austen",
               interpretation reads "bennet" as Elizabeth Bennet
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
