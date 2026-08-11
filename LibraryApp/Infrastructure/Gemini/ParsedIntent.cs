namespace BookSearchService.Infrastructure.Gemini;

/// <summary>
/// The wire shape Gemini fills in. Kept separate from <see cref="Domain.SearchIntent"/> so the
/// AI response schema stays an infrastructure concern — every property is nullable because the
/// model omits what the query did not mention.
/// </summary>
internal sealed class ParsedIntent
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public List<string>? Keywords { get; set; }
    public string? Interpretation { get; set; }
}
