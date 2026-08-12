using System.Collections.Frozen;

namespace LibraryApp.Domain;

/// <summary>
/// Compares people's names tolerantly, since a reader types "herbert" and the catalogue holds
/// "Frank Herbert".
/// </summary>
public static class NameKey
{
    private static readonly FrozenSet<string> DroppedTokens =
        new[] { "jr", "sr", "dr", "prof" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Credited roles the catalogue actually uses, sampled from live contributor data.
    /// </summary>
    /// <remarks>
    /// A whitelist rather than "whatever is in the brackets", because that field carries plenty of
    /// parentheticals that are not roles at all: life dates ("1912-1978"), organisations
    /// ("Library of Congress"), and expanded initials — "Tolkien, J. R. R. (John Ronald Reuel)"
    /// was rendering as <em>credits Tolkien, J. R. R. as john ronald reuel</em> before this existed.
    /// </remarks>
    private static readonly FrozenSet<string> KnownRoles = new[]
    {
        "narrator", "illustrator", "editor", "translator", "adapter", "contributor", "reader",
        "introduction", "foreword", "afterword", "preface", "author", "actor", "compiler",
        "photographer", "composer", "director", "producer", "cover design", "cover art"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Splits a contributor entry into the person and, when the catalogue really gave one, the role
    /// they were credited in: "John Schoenherr (Illustrator)".
    /// </summary>
    public static (string Name, string? Role) SplitRole(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return (string.Empty, null);

        var open = entry.LastIndexOf('(');
        var close = entry.LastIndexOf(')');

        if (open < 0 || close < open) return (entry.Trim(), null);

        // The bracketed part is dropped from the name either way — whether it holds a role, life
        // dates or expanded initials, it is not part of what the reader typed.
        var name = entry[..open].Trim();
        var bracketed = entry[(open + 1)..close].Trim();

        if (name.Length == 0) return (entry.Trim(), null);

        // "Editor, Introduction" is a real combination, so every part must be a known role.
        var parts = bracketed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var isRole = parts.Length > 0
                     && parts.All(part => KnownRoles.Contains(part.ToLowerInvariant()));

        return (name, isRole ? bracketed : null);
    }

    public static IReadOnlySet<string> Tokens(string? name) =>
        TextFold.Tokenize(name).Where(t => !DroppedTokens.Contains(t)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when everything the reader typed appears in the catalogue's name — so "herbert" and
    /// "frank herbert" both match "Frank Herbert", but "brian herbert" does not.
    /// </summary>
    public static bool Matches(string? wanted, string? candidate)
    {
        var wantedTokens = Tokens(wanted);
        if (wantedTokens.Count == 0) return false;

        return wantedTokens.IsSubsetOf(Tokens(candidate));
    }

    /// <summary>Stable comparison key, for folding duplicate records of the same person.</summary>
    public static string Canonical(string? name) =>
        string.Join(' ', Tokens(name).Order(StringComparer.Ordinal));

    /// <summary>
    /// Collapses author records that normalize identically, preserving first-seen order and each
    /// author's own key.
    /// </summary>
    /// <remarks>
    /// This catches punctuation and casing variants ("J.R.R. Tolkien" / "J R R Tolkien") but not
    /// transliterations: Dune carries both "Frank Herbert" and "Френк Герберт" as separate records
    /// for the same person, and nothing short of fetching /authors/{id}.json for its alternate_names
    /// would connect them — a ~2.5s call per author, which is not worth paying on every result.
    /// </remarks>
    public static IReadOnlyList<Author> Distinct(IReadOnlyList<Author> authors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<Author>(authors.Count);

        foreach (var author in authors)
        {
            if (string.IsNullOrWhiteSpace(author.Name)) continue;
            if (seen.Add(Canonical(author.Name))) kept.Add(author with { Name = author.Name.Trim() });
        }

        return kept;
    }
}
