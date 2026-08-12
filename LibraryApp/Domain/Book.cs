namespace LibraryApp.Domain;

/// <summary>
/// A work as the catalog knows it. Deliberately narrower than what OpenLibrary returns —
/// only the fields we actually surface or match on.
/// </summary>
/// <param name="Authors">
/// The work's <em>primary</em> authors, each with the catalogue's key for them. These come straight
/// from the search response, which was verified to carry the same list as the work record's
/// <c>authors</c> — so ranking never has to pay for a per-work detail fetch.
/// </param>
/// <param name="Contributors">
/// People credited without being authors, each carrying its role: "Scott Brick (Narrator)".
/// A name here but not in <paramref name="Authors"/> is what separates a contributor match from a
/// primary-author one.
/// </param>
public sealed record Book(
    string Key,
    string Title,
    IReadOnlyList<Author> Authors,
    IReadOnlyList<string> Contributors,
    int? FirstPublishYear,
    int? CoverId);
