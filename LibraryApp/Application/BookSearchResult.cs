using BookSearchService.Domain;

namespace BookSearchService.Application;

/// <param name="Intent">
/// The intent the catalog actually ran — post-broadening, so it may be looser than what was parsed.
/// </param>
/// <param name="Broadened">True when the original intent returned nothing and had to be relaxed.</param>
public sealed record BookSearchResult(
    SearchIntent Intent,
    bool Broadened,
    IReadOnlyList<BookMatch> Matches);
