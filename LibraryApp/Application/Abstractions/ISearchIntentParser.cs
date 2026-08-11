using BookSearchService.Domain;

namespace BookSearchService.Application.Abstractions;

/// <summary>Turns a natural-language query into fields the catalog can search on.</summary>
public interface ISearchIntentParser
{
    /// <exception cref="SearchUnavailableException">The upstream parser could not be reached or understood.</exception>
    Task<SearchIntent> ParseAsync(string query, CancellationToken cancellationToken);
}
