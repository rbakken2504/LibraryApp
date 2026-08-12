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
public sealed record AuthorCredit(string Name, CreditKind Kind, string? Role)
{
    /// <summary>
    /// How <paramref name="wanted"/> is credited on <paramref name="book"/>, or <c>null</c> when
    /// they appear nowhere on it — which includes the reader not having named anyone.
    /// </summary>
    public static AuthorCredit? Find(Book book, string? wanted)
    {
        var author = book.Authors.FirstOrDefault(a => NameKey.Matches(wanted, a.Name));

        // Primary beats contributor, so the author list is checked first and wins outright.
        if (author is not null) return new AuthorCredit(author.Name, CreditKind.Primary, null);

        foreach (var entry in book.Contributors)
        {
            var (name, role) = NameKey.SplitRole(entry);

            if (NameKey.Matches(wanted, name)) return new AuthorCredit(name, CreditKind.Contributor, role);
        }

        return null;
    }
}
