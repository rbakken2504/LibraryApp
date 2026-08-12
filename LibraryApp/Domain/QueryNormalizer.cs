using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LibraryApp.Domain;

/// <summary>
/// Collapses natural-language queries that mean the same thing onto one cache key, so
/// "Books by Stephen King" and "king stephen BOOKS" share a cached response.
/// </summary>
/// <remarks>
/// An uncached search costs a Gemini call plus a ~3.5s OpenLibrary round trip, so widening
/// what counts as a cache hit is the single highest-leverage thing here.
/// <para>
/// Known limitation: sorting tokens discards word order, which means negation and phrase
/// structure are lost — "not fantasy" and "fantasy not" canonicalize identically. That is
/// acceptable for the queries this serves, but it is a real edge, not an oversight.
/// </para>
/// </remarks>
public static class QueryNormalizer
{
    /// <summary>
    /// Scaffolding a reader wraps around a request — never the words that distinguish one book from
    /// another.
    /// </summary>
    /// <remarks>
    /// Articles, prepositions and conjunctions are deliberately absent, and the omission is the
    /// whole point. Dropping them merges titles that differ only by one: <c>the road</c> and
    /// <c>on the road</c> both reduced to <c>{road}</c>, so a search for McCarthy returned Kerouac
    /// from the cache in 75ms — with whichever query arrived first owning the entry for a week.
    /// The same collapse put <c>the book thief</c> and <c>thief</c> on one key.
    /// <para>
    /// This is the hazard <see cref="TitleKey"/> already refuses to inherit; the cache had it too.
    /// The cost of keeping them is a narrower notion of "same query" — <c>The Expanse</c> and
    /// <c>expanse</c> no longer share an entry — which buys a slower miss, where the collision
    /// bought a wrong book.
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> StopWords = new[]
    {
        "book", "books", "novel", "novels", "find", "me", "some", "any", "please",
        "i", "want", "looking", "by", "about", "like", "that", "is", "are"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Human-readable canonical form: lowercased, de-accented, punctuation-free,
    /// stop-words dropped, de-duplicated, ordinally sorted.
    /// </summary>
    public static string Canonicalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var decomposed = raw.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            // Combining marks are dropped so "Émile" and "Emile" converge.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        var tokens = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return string.Empty;

        var meaningful = tokens.Where(t => !StopWords.Contains(t)).ToArray();

        // A query that is nothing but stop words ("the book") keeps its original tokens
        // rather than canonicalizing to "" and colliding with every other such query.
        var kept = meaningful.Length > 0 ? meaningful : tokens;

        return string.Join(' ', kept.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>Stable, fixed-length, URL-safe cache key for the canonical form.</summary>
    public static string Fingerprint(string? raw)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(raw)));

        // Base64url by hand — Domain stays free of ASP.NET dependencies.
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
