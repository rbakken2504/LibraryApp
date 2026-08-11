using System.Net.Http.Json;
using BookSearchService.Application.Abstractions;
using BookSearchService.Domain;
using Microsoft.AspNetCore.WebUtilities;

namespace BookSearchService.Infrastructure.OpenLibrary;

/// <summary>
/// Retrieves books from openlibrary.org's search endpoint, in its own relevance order.
/// </summary>
public sealed class OpenLibraryBookCatalog(
    HttpClient httpClient,
    ILogger<OpenLibraryBookCatalog> logger) : IBookCatalog
{
    /// <summary>Trimmed response payload — everything <see cref="Book"/> needs and nothing else.</summary>
    private const string Fields = "key,title,author_name,first_publish_year,cover_i,edition_count";

    public async Task<IReadOnlyList<Book>> SearchAsync(
        SearchIntent intent,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(intent, limit);

        logger.LogInformation("Querying OpenLibrary: {Url}", url);

        OpenLibrarySearchResponse? payload;
        try
        {
            payload = await httpClient.GetFromJsonAsync<OpenLibrarySearchResponse>(url, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SearchUnavailableException("Could not reach the book catalog.", ex);
        }

        if (payload is null)
        {
            throw new SearchUnavailableException("The book catalog returned an unreadable response.");
        }

        logger.LogInformation("OpenLibrary returned {Returned} of {Total}", payload.Docs.Count, payload.NumFound);

        return payload.Docs.Select(ToBook).ToArray();
    }

    /// <summary>
    /// Builds the query, preferring OpenLibrary's dedicated parameters over hand-composed Solr.
    /// </summary>
    /// <remarks>
    /// Measured: <c>title=dune&amp;author=herbert</c> answers in ~3.5s, while the equivalent
    /// <c>q=title:dune AND author:herbert</c> took ~18.8s for a near-identical result set. Only the
    /// constraints with no dedicated parameter — subjects and the year range — go into <c>q</c>.
    /// </remarks>
    internal static string BuildUrl(SearchIntent intent, int limit)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["fields"] = Fields,
            ["limit"] = limit.ToString()
        };

        if (!string.IsNullOrWhiteSpace(intent.Title)) parameters["title"] = intent.Title;
        if (!string.IsNullOrWhiteSpace(intent.Author)) parameters["author"] = intent.Author;

        var filters = new List<string>();

        // Subjects, never bare terms: OpenLibrary answers `subject:cyberpunk AND subject:dystopia`
        // promptly but times out on the same words as free text.
        filters.AddRange(intent.Keywords.Select(keyword => $"subject:{keyword}"));

        if (intent.YearFrom is not null || intent.YearTo is not null)
        {
            var from = intent.YearFrom?.ToString() ?? "*";
            var to = intent.YearTo?.ToString() ?? "*";
            filters.Add($"first_publish_year:[{from} TO {to}]");
        }

        if (filters.Count > 0) parameters["q"] = string.Join(" AND ", filters);

        return QueryHelpers.AddQueryString("/search.json", parameters);
    }

    private static Book ToBook(OpenLibraryDoc doc) => new(
        Key: doc.Key ?? string.Empty,
        Title: doc.Title ?? "Untitled",
        Authors: doc.AuthorName ?? [],
        FirstPublishYear: doc.FirstPublishYear,
        CoverId: doc.CoverId,
        EditionCount: doc.EditionCount ?? 0);
}
