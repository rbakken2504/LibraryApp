using BookSearchService.Domain;

namespace BookSearchService.Application.Abstractions;

/// <summary>Retrieves books matching a structured intent, in the catalog's own relevance order.</summary>
public interface IBookCatalog
{
    /// <exception cref="SearchUnavailableException">The upstream catalog could not be reached or understood.</exception>
    Task<IReadOnlyList<Book>> SearchAsync(SearchIntent intent, int limit, CancellationToken cancellationToken);
}
