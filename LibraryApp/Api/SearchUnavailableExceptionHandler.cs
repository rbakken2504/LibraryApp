using BookSearchService.Application.Abstractions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BookSearchService.Api;

/// <summary>
/// Maps an exhausted upstream to 502. There is no degraded fallback on purpose — sending the raw
/// query to the catalog's free-text search returns nothing for vague queries and times out for
/// multi-term ones, so a "degraded" result would be an empty list dressed up as a success.
/// </summary>
public sealed class SearchUnavailableExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<SearchUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not SearchUnavailableException) return false;

        logger.LogError(exception, "Search failed: upstream dependency unavailable");

        httpContext.Response.StatusCode = StatusCodes.Status502BadGateway;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Book search is temporarily unavailable",
                Detail = exception.Message
            }
        });
    }
}
