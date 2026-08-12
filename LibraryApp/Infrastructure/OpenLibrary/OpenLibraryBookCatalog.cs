using System.Net.Http.Json;
using LibraryApp.Application.Abstractions;
using LibraryApp.Domain;
using Microsoft.AspNetCore.WebUtilities;

namespace LibraryApp.Infrastructure.OpenLibrary;

/// <summary>
/// Retrieves books from openlibrary.org's search endpoint, in its own relevance order.
/// </summary>
public sealed class OpenLibraryBookCatalog(
    HttpClient httpClient,
    ILogger<OpenLibraryBookCatalog> logger) : IBookCatalog
{
    /// <summary>
    /// Trimmed response payload — everything <see cref="Book"/> needs and nothing else.
    /// <c>author_key</c> and <c>contributor</c> earn their place by making the ranking tiers
    /// decidable from this one response, with no per-work detail fetch.
    /// </summary>
    private const string Fields =
        "key,title,author_name,author_key,contributor,first_publish_year,cover_i";

    public Task<IReadOnlyList<Book>> SearchAsync(
        SearchIntent intent,
        int limit,
        CancellationToken cancellationToken) =>
        GetBooksAsync(BuildUrl(intent, limit), cancellationToken);

    public Task<IReadOnlyList<Book>> SearchFreeTextAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = QueryHelpers.AddQueryString("/search.json", new Dictionary<string, string?>
        {
            ["q"] = query,
            ["fields"] = Fields,
            ["limit"] = limit.ToString()
        });

        return GetBooksAsync(url, cancellationToken);
    }

    public async Task<IReadOnlyList<Book>> GetWorksByAuthorAsync(
        string authorKey,
        string authorName,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = QueryHelpers.AddQueryString(
            $"/authors/{authorKey}/works.json",
            new Dictionary<string, string?> { ["limit"] = limit.ToString() });

        logger.LogInformation("Querying OpenLibrary author works: {Url}", url);

        var payload = await GetAsync<OpenLibraryAuthorWorksResponse>(url, cancellationToken);

        // This endpoint is thinner than the search index: no publish year and no author names. The
        // name is supplied by the caller; the year is genuinely absent, so it stays null rather than
        // being invented.
        return payload.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry => new Book(
                Key: entry.Key!,
                Title: entry.Title ?? "Untitled",
                Authors: [new Author(authorName, authorKey)],
                Contributors: [],
                FirstPublishYear: null,
                CoverId: FirstUsableCover(entry.Covers)))
            .ToArray();
    }

    /// <summary>The first real cover id, or <c>null</c> when the entry has none.</summary>
    /// <remarks>
    /// The cast is what makes this correct. <c>FirstOrDefault</c> over a sequence of <c>int</c>
    /// reports "nothing matched" as <c>0</c>, and this endpoint really does send <c>-1</c>
    /// placeholders — so the obvious spelling turns a missing cover into cover id 0, which
    /// <see cref="Api.Contracts.BookResource"/> then renders as a URL that 404s. Widening to
    /// <c>int?</c> first makes absence come back as absence.
    /// </remarks>
    internal static int? FirstUsableCover(IReadOnlyList<int>? covers) =>
        covers?.Where(id => id > 0).Cast<int?>().FirstOrDefault();

    private async Task<IReadOnlyList<Book>> GetBooksAsync(string url, CancellationToken cancellationToken)
    {
        logger.LogInformation("Querying OpenLibrary: {Url}", url);

        var payload = await GetAsync<OpenLibrarySearchResponse>(url, cancellationToken);

        logger.LogInformation("OpenLibrary returned {Returned} of {Total}", payload.Docs.Count, payload.NumFound);

        return payload.Docs.Select(ToBook).ToArray();
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        T? payload;
        try
        {
            payload = await httpClient.GetFromJsonAsync<T>(url, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SearchUnavailableException("Could not reach the book catalog.", ex);
        }

        return payload ?? throw new SearchUnavailableException("The book catalog returned an unreadable response.");
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

    internal static Book ToBook(OpenLibraryDoc doc) => new(
        Key: doc.Key ?? string.Empty,
        Title: doc.Title ?? "Untitled",
        // Zip before folding: names and keys arrive as two arrays, and de-duplicating one without
        // the other is exactly the misalignment the Author type exists to prevent.
        Authors: NameKey.Distinct(PairAuthors(doc)),
        Contributors: doc.Contributor ?? [],
        FirstPublishYear: doc.FirstPublishYear,
        CoverId: doc.CoverId);

    /// <summary>
    /// Pairs each author name with its key. The two arrays are positional and, in practice, equal
    /// length — but a name without a key is useless for the author lookup, so any tail beyond the
    /// shorter array is dropped rather than paired with a placeholder.
    /// </summary>
    private static IReadOnlyList<Author> PairAuthors(OpenLibraryDoc doc)
    {
        var names = doc.AuthorName ?? [];
        var keys = doc.AuthorKey ?? [];

        return names.Zip(keys, (name, key) => new Author(name, key)).ToArray();
    }
}
