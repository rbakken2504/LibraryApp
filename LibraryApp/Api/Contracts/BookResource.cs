using LibraryApp.Domain;

namespace LibraryApp.Api.Contracts;

/// <param name="Tier">Which rank band this landed in — serialized by name, not ordinal.</param>
/// <param name="TierLabel">Short human-readable form of <paramref name="Tier"/>, for display.</param>
/// <param name="Reason">Why this book satisfied the query, derived from the matched fields.</param>
public sealed record BookResource(
    string Key,
    string Title,
    IReadOnlyList<string> Authors,
    int? FirstPublishYear,
    string? CoverUrl,
    string Tier,
    string TierLabel,
    string Reason)
{
    public static BookResource From(BookMatch match) => new(
        Key: match.Book.Key,
        Title: match.Book.Title,
        // Keys are an internal lookup detail; callers only need the names.
        Authors: match.Book.Authors.Select(a => a.Name).ToArray(),
        FirstPublishYear: match.Book.FirstPublishYear,
        CoverUrl: match.Book.CoverId is { } id
            ? $"https://covers.openlibrary.org/b/id/{id}-M.jpg"
            : null,
        Tier: match.Tier.ToString(),
        TierLabel: Label(match.Tier),
        Reason: match.Reason);

    private static string Label(MatchTier tier) => tier switch
    {
        MatchTier.ExactTitlePrimaryAuthor => "Exact match",
        MatchTier.ExactTitleContributor   => "Exact title, contributor",
        MatchTier.NearTitleAuthor         => "Close match",
        MatchTier.TitleOnly               => "Title match",
        MatchTier.AuthorOnly              => "Same author",
        _                                 => "Suggested"
    };
}
