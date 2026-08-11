namespace BookSearchService.Infrastructure.OpenLibrary;

public sealed class OpenLibraryOptions
{
    public const string SectionName = "OpenLibrary";

    public string BaseAddress { get; set; } = "https://openlibrary.org";

    /// <summary>
    /// Per-attempt ceiling. OpenLibrary answers a well-formed fielded query in roughly 3.5s but has
    /// been measured at 18s on awkward ones, so the cap is what keeps a slow upstream from becoming
    /// an unbounded request.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
