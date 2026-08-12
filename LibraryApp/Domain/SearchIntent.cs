namespace LibraryApp.Domain;

/// <summary>
/// A natural-language query resolved into fields OpenLibrary can actually search on.
/// </summary>
/// <param name="Keywords">
/// OpenLibrary subject tokens (lowercase, underscore-separated: <c>science_fiction</c>).
/// These become <c>subject:</c> filters — OpenLibrary times out when the same words are
/// sent as bare search terms, so the token form is required, not cosmetic.
/// </param>
/// <param name="Interpretation">
/// Plain-language account of how the query was read, e.g. "read 'herbert' as an author,
/// not a title". Returned to the client so the parse is inspectable.
/// </param>
public sealed record SearchIntent(
    string? Title,
    string? Author,
    int? YearFrom,
    int? YearTo,
    IReadOnlyList<string> Keywords,
    string Interpretation)
{
    /// <summary>True when there is nothing for the catalog to search on.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title)
        && string.IsNullOrWhiteSpace(Author)
        && YearFrom is null
        && YearTo is null
        && Keywords.Count == 0;
}
