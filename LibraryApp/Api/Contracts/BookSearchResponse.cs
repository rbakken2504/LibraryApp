using BookSearchService.Application;

namespace BookSearchService.Api.Contracts;

/// <param name="Query">
/// The query that produced this result set — which on a cache hit is the wording that first
/// populated the entry, not necessarily what the caller just sent. "HERBERT, books!" is served
/// from the entry seeded by "books by herbert" and echoes the latter. That is the intended
/// consequence of collapsing equivalent queries onto one cache line, not a bug, but treat this
/// field as provenance rather than an echo of the request.
/// </param>
/// <param name="Interpretation">How the query was read, straight from the intent parser.</param>
/// <param name="Broadened">
/// True when the parsed query returned nothing and had to be relaxed — the results are real but
/// looser than what was asked for.
/// </param>
public sealed record BookSearchResponse(
    string Query,
    string Interpretation,
    bool Broadened,
    int Count,
    IReadOnlyList<BookResource> Results)
{
    public static BookSearchResponse From(string query, BookSearchResult result)
    {
        var results = result.Matches.Select(BookResource.From).ToArray();

        return new BookSearchResponse(
            Query: query,
            Interpretation: result.Intent.Interpretation,
            Broadened: result.Broadened,
            Count: results.Length,
            Results: results);
    }
}
