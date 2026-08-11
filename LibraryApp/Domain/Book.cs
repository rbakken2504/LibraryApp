namespace BookSearchService.Domain;

/// <summary>
/// A work as the catalog knows it. Deliberately narrower than what OpenLibrary returns —
/// only the fields we actually surface or match on.
/// </summary>
public sealed record Book(
    string Key,
    string Title,
    IReadOnlyList<string> Authors,
    int? FirstPublishYear,
    int? CoverId,
    int EditionCount);
