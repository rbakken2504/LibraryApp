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

    [JsonPropertyName("first_publish_year")]
    public int? FirstPublishYear { get; set; }

    [JsonPropertyName("cover_i")]
    public int? CoverId { get; set; }

    [JsonPropertyName("edition_count")]
    public int? EditionCount { get; set; }
}
