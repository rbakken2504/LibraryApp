using LibraryApp.Domain;

namespace LibraryApp.Application.Abstractions;

/// <summary>Retrieves books matching a structured intent, in the catalog's own relevance order.</summary>
public interface IBookCatalog
{
    /// <exception cref="SearchUnavailableException">The upstream catalog could not be reached or understood.</exception>
    Task<IReadOnlyList<Book>> SearchAsync(SearchIntent intent, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Free-text search over the whole record rather than specific fields.
    /// </summary>
    /// <remarks>
    /// This exists for one reason: the catalogue's <c>author=</c> parameter only matches primary
    /// authors, so a work where the named person merely narrated or illustrated it is unreachable
    /// through it. Free text does reach them — "dune scott brick" surfaces Dune itself, whose
    /// contributor list holds Brick while its author list does not.
    /// <para>
    /// Only for title-plus-author queries. Free text over common words is pathologically slow on
    /// this API — "cyberpunk dystopia" was measured past 25 seconds, where distinctive proper nouns
    /// return in about two.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Book>> SearchFreeTextAsync(string query, int limit, CancellationToken cancellationToken);

    /// <summary>Top works by a specific author, for the author-only fallback.</summary>
    /// <param name="authorName">
    /// Supplied by the caller because this endpoint returns author <em>keys</em> only. The caller
    /// already holds the name from the search hit that produced the key, so passing it avoids a
    /// ~2.2s /authors/{key}.json lookup purely to re-learn something we know.
    /// </param>
    Task<IReadOnlyList<Book>> GetWorksByAuthorAsync(
        string authorKey, string authorName, int limit, CancellationToken cancellationToken);
}
