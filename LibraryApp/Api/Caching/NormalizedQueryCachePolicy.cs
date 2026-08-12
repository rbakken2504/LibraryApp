using LibraryApp.Domain;
using Microsoft.AspNetCore.OutputCaching;

namespace LibraryApp.Api.Caching;

/// <summary>
/// Keys cached responses on the <em>normalized</em> query, so requests that mean the same thing
/// share one entry: "Books by Stephen King" and "KING, Stephen books!" hit the same cache line.
/// </summary>
/// <remarks>
/// Clearing <see cref="CacheVaryByRules.QueryKeys"/> is what makes this work, and it is not
/// optional. The framework's <c>DefaultPolicy</c> — which every policy builder includes — assigns
/// <c>QueryKeys = "*"</c>, putting the verbatim query string into the storage key. Left in place it
/// silently defeats the whole normalization: every rewording produces a distinct key and the
/// fingerprint below never gets a chance to match. This policy must therefore run <em>after</em>
/// the default one (it is added later in the builder chain) so that this override wins.
/// </remarks>
public sealed class NormalizedQueryCachePolicy : IOutputCachePolicy
{
    ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var rawQuery = context.HttpContext.Request.Query["q"].ToString();

        // Drop the verbatim query string from the key, then reintroduce the query as a fingerprint
        // of its normalized form. Safe because 'q' is the endpoint's only meaningful input — the
        // result count is a constant, precisely so no caller-supplied value can escape this key.
        context.CacheVaryByRules.QueryKeys = default;
        context.CacheVaryByRules.VaryByValues["q-normalized"] = QueryNormalizer.Fingerprint(rawQuery);

        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;

        // An uncached search costs an AI call plus a multi-second catalog round trip, so collapsing
        // concurrent identical queries into one upstream request is worth the lock.
        context.AllowLocking = true;

        return ValueTask.CompletedTask;
    }

    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        // Never persist a failure for the entry's full lifetime — a 502 from a flaky upstream would
        // otherwise be served back for seven days.
        if (context.HttpContext.Response.StatusCode is not StatusCodes.Status200OK)
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
