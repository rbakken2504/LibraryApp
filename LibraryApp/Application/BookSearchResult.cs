using LibraryApp.Domain;

namespace LibraryApp.Application;

/// <param name="Intent">
/// The intent the catalog actually ran — post-broadening, so it may be looser than what was parsed.
/// </param>
/// <param name="Broadened">True when the original intent returned nothing and had to be relaxed.</param>
/// <param name="ClearWinner">
/// Exactly one candidate was an exact title match by the author the reader named, so it is returned
/// on its own. A tie at that tier is ambiguity rather than a winner, and gets the full list.
/// </param>
public sealed record BookSearchResult(
    SearchIntent Intent,
    bool Broadened,
    bool ClearWinner,
    IReadOnlyList<BookMatch> Matches);
