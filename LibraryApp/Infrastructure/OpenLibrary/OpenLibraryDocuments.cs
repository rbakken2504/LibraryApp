using System.Text.Json.Serialization;

namespace LibraryApp.Infrastructure.OpenLibrary;

internal sealed class OpenLibrarySearchResponse
{
    [JsonPropertyName("numFound")]
    public int NumFound { get; set; }

    [JsonPropertyName("docs")]
    public List<OpenLibraryDoc> Docs { get; set; } = [];
}

internal sealed class OpenLibraryDoc
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("author_name")]
    public List<string>? AuthorName { get; set; }

    /// <summary>Parallel to <see cref="AuthorName"/>, and the same list the work record's authors holds.</summary>
    [JsonPropertyName("author_key")]
    public List<string>? AuthorKey { get; set; }

    /// <summary>Non-author credits, role included: "Scott Brick (Narrator)".</summary>
    [JsonPropertyName("contributor")]
    public List<string>? Contributor { get; set; }

    [JsonPropertyName("first_publish_year")]
    public int? FirstPublishYear { get; set; }

    [JsonPropertyName("cover_i")]
    public int? CoverId { get; set; }

    [JsonPropertyName("edition_count")]
    public int? EditionCount { get; set; }
}

/// <summary>
/// Shape of /authors/{key}/works.json, which differs from the search index: no author names, no
/// publish year, no edition count, and covers arrive as an array rather than a single id.
/// </summary>
internal sealed class OpenLibraryAuthorWorksResponse
{
    [JsonPropertyName("entries")]
    public List<OpenLibraryWorkEntry> Entries { get; set; } = [];
}

internal sealed class OpenLibraryWorkEntry
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>May contain -1 placeholders for missing covers.</summary>
    [JsonPropertyName("covers")]
    public List<int>? Covers { get; set; }
}
