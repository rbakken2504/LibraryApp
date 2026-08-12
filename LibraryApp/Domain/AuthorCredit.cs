namespace LibraryApp.Domain;

/// <summary>How the catalogue credits a person on a work.</summary>
public enum CreditKind
{
    /// <summary>Listed among the work's authors.</summary>
    Primary,

    /// <summary>Credited without having authored it — narrator, illustrator, editor.</summary>
    Contributor
}

/// <summary>
/// The person the reader named, as the catalogue records them: its spelling of their name, how they
/// were credited, and the role when it named one.
/// </summary>
/// <remarks>
/// Found once per candidate and then used twice — <see cref="CandidateRanker"/> turns
/// <see cref="Kind"/> into a tier, and <see cref="MatchExplainer"/> names the person. Deriving it
/// separately in each is how a result ends up badged "contributor" while its reason reads
/// "by Frank Herbert".
/// </remarks>
public sealed record AuthorCredit(string Name, CreditKind Kind, string? Role);

/// <summary>
/// Who is credited on one book, worked out in a single pass over its author and contributor lists.
/// </summary>
/// <param name="Match">How the person the reader named is credited here, or <c>null</c> if absent.</param>
/// <param name="Adapters">
/// This work's own authors who are also credited with a derivative role. OpenLibrary pads
/// <c>author_name</c> with illustrators, editors and adaptors, so a work carrying any of these is a
/// retelling of something rather than the thing itself.
/// </param>
public sealed record BookCredits(AuthorCredit? Match, IReadOnlyList<AuthorCredit> Adapters)
{
    public bool IsAdaptation => Adapters.Count > 0;

    public static BookCredits For(Book book, string? wantedAuthor)
    {
        var adapters = book.Authors
            .Select(author => new
            {
                author.Name,
                Role = NameKey.DerivativeRole(book.Contributors, author.Name)
            })
            .Where(credited => credited.Role is not null)
            .Select(credited => new AuthorCredit(credited.Name, CreditKind.Contributor, credited.Role))
            .ToArray();

        return new BookCredits(Match: FindMatch(book, wantedAuthor, adapters), adapters);
    }

    /// <summary>
    /// How <paramref name="wanted"/> is credited, or <c>null</c> when they appear nowhere on the
    /// book — which includes the reader not having named anyone.
    /// </summary>
    private static AuthorCredit? FindMatch(Book book, string? wanted, IReadOnlyList<AuthorCredit> adapters)
    {
        var author = book.Authors.FirstOrDefault(a => NameKey.Matches(wanted, a.Name));

        if (author is not null)
        {
            // Appearing in author_name is not enough. Someone the same work credits as its adaptor
            // or illustrator contributed to it rather than writing it, whichever field lists them —
            // which is what keeps a search for the adapter out of the primary-author tier.
            var credited = adapters.FirstOrDefault(a =>
                string.Equals(NameKey.Canonical(a.Name), NameKey.Canonical(author.Name), StringComparison.Ordinal));

            return credited ?? new AuthorCredit(author.Name, CreditKind.Primary, null);
        }

        foreach (var entry in book.Contributors)
        {
            var (name, role) = NameKey.SplitRole(entry);

            if (NameKey.Matches(wanted, name)) return new AuthorCredit(name, CreditKind.Contributor, role);
        }

        return null;
    }
}
