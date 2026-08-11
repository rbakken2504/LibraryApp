using BookSearchService.Api.Contracts;
using BookSearchService.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace BookSearchService.Api.Controllers;

[ApiController]
[Route("api/books")]
public class BooksController(BookSearchOrchestrator orchestrator) : ControllerBase
{
    /// <summary>
    /// Fixed result count. Deliberately not a query parameter: the cache policy keys only on the
    /// normalized query, so a caller-supplied limit would let two different-sized requests collide
    /// on one cache entry.
    /// </summary>
    private const int ResultLimit = 20;

    [HttpGet("search")]
    [OutputCache(PolicyName = "BookSearchCache")]
    [ProducesResponseType<BookSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<BookSearchResponse>> Search(
        [FromQuery] string? q,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return ValidationProblem("Provide a search query in the 'q' parameter.");
        }

        var result = await orchestrator.SearchAsync(q, ResultLimit, cancellationToken);

        return Ok(BookSearchResponse.From(q, result));
    }
}
