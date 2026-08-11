using BookSearchService.Domain;

namespace BookSearchService.Api.Contracts;

/// <param name="Reason">Why this book satisfied the query, derived from the matched fields.</param>
public sealed record BookResource(
    string Key,
    string Title,
    IReadOnlyList<string> Authors,
    int? FirstPublishYear,
    string? CoverUrl,
    int EditionCount,
    string Reason)
{
    public static BookResource From(BookMatch match) => new(
        Key: match.Book.Key,
        Title: match.Book.Title,
        Authors: match.Book.Authors,
        FirstPublishYear: match.Book.FirstPublishYear,
        CoverUrl: match.Book.CoverId is { } id
            ? $"https://covers.openlibrary.org/b/id/{id}-M.jpg"
            : null,
        EditionCount: match.Book.EditionCount,
        Reason: match.Reason);
}
