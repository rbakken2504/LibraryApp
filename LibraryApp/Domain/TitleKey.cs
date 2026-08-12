using System.Collections.Frozen;

namespace LibraryApp.Domain;

/// <summary>How a candidate's title relates to the title the reader asked for.</summary>
public enum TitleMatch
{
    None,

    /// <summary>Same title once folded — "The Hobbit" and "hobbit, the".</summary>
    Exact,

    /// <summary>The asked-for title is contained in a longer one, e.g. a subtitle or collection.</summary>
    Near
}

/// <summary>
/// Compares titles as normalized token sets, so punctuation, casing, accents, leading articles
/// and word order don't defeat a match.
/// </summary>
/// <remarks>
/// <para>
/// <b>Do not swap this for <see cref="QueryNormalizer"/>.</b> That type folds text for cache keys,
/// where collapsing distinct queries onto one entry is the entire point, and its stop-word list
/// contains <c>book</c>, <c>novel</c>, <c>find</c> and <c>some</c> — words that carry meaning inside
/// a title. Running "The Book Thief" through it yields <c>{thief}</c>, which would match any number
/// of unrelated works. Titles need the opposite bias: discard only what is never distinguishing.
/// </para>
/// <para>
/// Hence articles are the only thing removed here.
/// </para>
/// </remarks>
public static class TitleKey
{
    /// <summary>
    /// Articles and connectives only — words that are never what distinguishes one title from
    /// another. Nothing may be added here that could carry meaning in a title.
    /// </summary>
    /// <remarks>
    /// Dropping connectives also sharpens <see cref="SurplusTokens"/>. Counting them leaves
    /// "The Hobbit, or There and Back Again" and "The Hobbit &amp; The Lord of the Rings
    /// [collection/set]" tied at five surplus tokens, so the tiebreak cannot tell a genuine subtitle
    /// from a bundle. Without them it is three against four, and the subtitle wins as it should.
    /// </remarks>
    private static readonly FrozenSet<string> FunctionWords =
        new[] { "a", "an", "the", "and", "or", "of" }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> Tokens(string? title)
    {
        var all = TextFold.Tokenize(title);
        var distinguishing = all.Where(t => !FunctionWords.Contains(t)).ToHashSet(StringComparer.Ordinal);

        // A title made only of these ("The One and Only") keeps them rather than becoming unmatchable.
        return distinguishing.Count > 0 ? distinguishing : all.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Classifies <paramref name="candidateTitle"/> against what the reader asked for.</summary>
    public static TitleComparison Compare(string? wantedTitle, string? candidateTitle)
    {
        var wanted = Tokens(wantedTitle);
        var candidate = Tokens(candidateTitle);

        // With no title asked for there is nothing to be surplus *to*. Reporting the candidate's own
        // token count here would turn the ranker's tiebreak into "shortest title first" for every
        // subject-only and author-only result, displacing the catalogue's relevance order.
        var surplus = wanted.Count == 0 ? 0 : Math.Max(0, candidate.Count - wanted.Count);

        if (wanted.Count == 0 || candidate.Count == 0) return new TitleComparison(TitleMatch.None, surplus);
        if (wanted.SetEquals(candidate)) return new TitleComparison(TitleMatch.Exact, surplus);

        var match = wanted.IsProperSubsetOf(candidate) ? TitleMatch.Near : TitleMatch.None;

        return new TitleComparison(match, surplus);
    }
}

/// <summary>Both facts the ranker needs about one title pair, from a single pass over the tokens.</summary>
/// <param name="Surplus">
/// Tokens the candidate carries beyond what was asked for. Orders near-matches so the tightest one
/// wins — "The Hobbit" ahead of "The Hobbit &amp; The Lord of the Rings [collection/set]". Zero when
/// no title was asked for, so the tiebreak stays neutral instead of ranking by title length.
/// </param>
public readonly record struct TitleComparison(TitleMatch Match, int Surplus);
