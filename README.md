# BookSearchService

Natural-language book search. You ask for *"gritty space opera about corporate politics"*; Gemini
resolves that into structured search fields, OpenLibrary retrieves against them, and each result
comes back with an explanation of why it matched.

```
GET /api/books/search?q=cyberpunk novels from the 90s

{
  "query": "cyberpunk novels from the 90s",
  "interpretation": "I searched for science fiction novels with the cyberpunk subject published in the 1990s.",
  "broadened": false,
  "count": 17,
  "results": [
    {
      "key": "/works/OL38501W",
      "title": "Snow Crash",
      "authors": ["Neal Stephenson"],
      "firstPublishYear": 1992,
      "coverUrl": "https://covers.openlibrary.org/b/id/392508-M.jpg",
      "editionCount": 41,
      "reason": "Published 1992, within 1990–1999; tagged cyberpunk, science fiction."
    }
  ]
}
```

---

## How it works

```
Client ──> BooksController ──> BookSearchOrchestrator
                                    │
                                    ├─1─> ISearchIntentParser  (Gemini, one call)
                                    │       "cyberpunk novels from the 90s"
                                    │         -> { yearFrom: 1990, yearTo: 1999,
                                    │              keywords: [cyberpunk, science_fiction] }
                                    │
                                    └─2─> IBookCatalog  (OpenLibrary)
                                            fielded query -> top 20 works
                                                  │
                                          MatchExplainer (pure, no AI)
                                                  │
                                            BookSearchResponse ──> Client
```

Ordering is the central decision: **the AI runs before retrieval, not as a re-ranker after it.**

The intuitive design is to search first and have the model re-rank the results. Measured against the
live OpenLibrary API, that doesn't work — its `q=` parameter is a lexical Solr match, not a semantic
one:

| Query strategy | Results | Latency |
|---|---|---|
| `q=gritty sci-fi like the expanse` (raw natural language) | **0** | — |
| `q=cyberpunk dystopia` (plain multi-term) | — | **timeout >25s** |
| `q=title:dune AND author:herbert` (hand-built Solr) | 165 | 18.8s |
| `title=dune&author=herbert` (dedicated params) | 148 | **3.5s** |
| `q=subject:cyberpunk AND subject:dystopia` | 14 | 1.8s |

Three rules fall out of that, and they drive the implementation:

1. **Retrieval has to be shaped by the AI, not corrected by it.** A vague query returns zero rows and
   a plain multi-term query times out. Re-ranking can reorder what you retrieved; it cannot recover
   documents that were never retrieved at all.
2. **Prefer dedicated params over composed Solr strings** — 3.5s versus 18.8s for a near-identical
   result set. `OpenLibraryBookCatalog.BuildUrl` never composes a boolean when a param exists.
3. **Keywords must become `subject:` filters, never free text.** The same words as bare terms time out.

Parsing first is also the cheaper direction: ~450 tokens in and ~80 out per query, versus shipping
50 books of metadata into a ranking prompt on every request.

### Match explanations are derived, not generated

There is no second AI call to explain the results. `MatchExplainer` reports which intent fields each
book satisfied — *"Matches author Frank Herbert; published 1965, within 1960–1980."* Every clause is
a fact about the query constraints and the returned document, so it costs nothing per result and
cannot hallucinate. Gemini's own `interpretation` string covers how the query was read.

### Recovering from an over-narrow parse

Subjects are ANDed, so one speculative token zeroes the entire result set:

| Query | Results |
|---|---|
| `science_fiction AND space_opera AND corporate_politics` | **0** |
| `science_fiction AND space_opera` | 2,746 |

`BookSearchOrchestrator` walks a deterministic broadening ladder — drop the year range, then the
title, then trim keywords from the tail where the model puts its guesses — stopping at the first rung
that returns anything. No extra AI call. Capped at four rungs, since each is a real HTTP round trip.
The `broadened` flag in the response tells the client the results are looser than what they asked for.

---

## Setup

**Requirements:** .NET 10 SDK, and a Gemini API key from [aistudio.google.com/apikey](https://aistudio.google.com/apikey).

```bash
dotnet user-secrets set "Gemini:ApiKey" "<your-key>" --project LibraryApp
dotnet run --project LibraryApp
```

The app refuses to start without a key rather than failing at the first request:

```
OptionsValidationException: Gemini:ApiKey is not configured.
Set it with: dotnet user-secrets set "Gemini:ApiKey" "<key>"
```

> **On a fresh clone**, `launchSettings.json` is git-ignored (ports and environment are per-machine),
> and **user-secrets only load in the Development environment**. Without a launch profile you must
> set it explicitly, or the key won't be picked up despite being stored correctly:
>
> ```bash
> ASPNETCORE_ENVIRONMENT=Development dotnet run --project LibraryApp
> ```

Then send the requests in `LibraryApp/LibraryApp.http`, or:

```bash
curl "http://localhost:5014/api/books/search?q=dune by frank herbert"
```

### Configuration

| Key | Default | Notes |
|---|---|---|
| `Gemini:ApiKey` | — | Required. User-secrets or `Gemini__ApiKey` env var. Never `appsettings.json`. |
| `Gemini:Model` | `gemini-flash-lite-latest` | ~0.8s vs ~1.9s for full flash, with identical extractions on the sample queries. |
| `Gemini:Endpoint` | `.../v1beta/openai/` | Gemini's OpenAI-compatible surface. |
| `Gemini:UseStrictJsonSchema` | `false` | Gemini's compat layer only partially supports strict `json_schema`; the default is JSON mode with the schema in the prompt. |
| `OpenLibrary:BaseAddress` | `https://openlibrary.org` | |
| `OpenLibrary:Timeout` | `10s` | Per-attempt ceiling. |

Environment variables use `__` instead of `:` (`Gemini__ApiKey`) and **override user-secrets** — worth
knowing if a key ever appears not to take effect.

> Pin the floating `-latest` aliases rather than a version. `gemini-2.5-flash` still appears in the
> model listing but returns 404 for keys issued after it was retired.

---

## API

**`GET /api/books/search?q={query}`**

| Status | Meaning |
|---|---|
| `200` | `BookSearchResponse` — up to 20 results |
| `400` | `q` missing or blank |
| `502` | Gemini or OpenLibrary unreachable after one retry |

There is deliberately no `limit` parameter. The result count is a constant so that no caller-supplied
value can escape the cache key (see below).

On failure the service returns 502 rather than degrading to a keyword search. A "degraded" fallback
would send the raw query to OpenLibrary's free-text search — which, per the table above, returns
empty or times out for exactly the vague queries this service exists to serve. An empty list dressed
up as a success is worse than an honest error.

---

## Caching

Responses are output-cached for 7 days on a **fingerprint of the normalized query**, so rewordings
share a single entry:

```
"The BOOKS by Émile Zola!"
  → lowercase, strip diacritics, drop punctuation
  → tokenize, remove stop words          [emile, zola]
  → Distinct(), sort ordinal             [emile, zola]
  → SHA-256 → base64url                  cache key
```

Measured: `books by stephen king` 1.0s cold, `KING, Stephen books!` **8ms** warm, with no upstream
call for the second. Since `AllowLocking` is on, concurrent requests for one key wait for the first
to populate instead of stampeding — and because the lock is keyed on the *storage key*, ten requests
spelling the same query ten ways collapse to one upstream call.

**The non-obvious part:** the framework's built-in `DefaultPolicy` sets `CacheVaryByRules.QueryKeys = "*"`,
which puts the verbatim query string into the storage key. That must be actively cleared —
`NormalizedQueryCachePolicy` does it — or every rewording produces a distinct key and the
normalization silently does nothing. This was caught by testing, not by reading the code.

Failures are never stored: `ServeResponseAsync` blocks cache storage for any non-200, so a transient
502 isn't served back for seven days.

---

## Testing

50 tests, no mocking framework, no network, no API key required. The full suite runs in ~33ms.

```bash
dotnet test
```

The strategy is to test the code that holds decisions and to skip the code that only holds wiring.

**Pure logic, exhaustively** — `QueryNormalizerTests`, `SubjectTokenTests`. These are table-driven
`[Theory]` cases over the functions with real branching: term reordering, casing, diacritics,
punctuation, stop words, duplicates, empty input. Two assertions matter most and pull in opposite
directions — that equivalent queries *do* collapse to one fingerprint, and that genuinely different
ones *don't* collide. A cache key that over-collapses serves the wrong results, which is far worse
than a missed cache hit.

**The orchestrator, against hand-written fakes** — `BookSearchOrchestratorTests`. `ISearchIntentParser`
and `IBookCatalog` are small enough that stub classes are clearer than mock setup, and they make the
test read as a scenario rather than a script. Covers the broadening ladder rung by rung: year range
dropped first, then title, then keywords trimmed from the tail, always leaving at least one subject,
capped at four attempts. Also that explanations describe the *effective* intent — a search broadened
past a year filter must not claim the book matched that year — plus cancellation and error propagation.

**The query builder, directly** — `OpenLibraryQueryTests`, reached via `InternalsVisibleTo`. This
guards the measured findings above: that title and author use dedicated params rather than a Solr
string, that keywords become ANDed `subject:` filters, and that open-ended year bounds become
wildcards. These are performance characteristics encoded as structure, and a well-meaning refactor
toward "cleaner" Solr composition would be a 5x regression that no functional test would catch.

**What isn't covered, and why.** There are no integration tests and nothing calls a live upstream.
Gemini and OpenLibrary sit behind interfaces; testing the adapters against the real services would
mostly test their SDKs, and would make the suite slow, flaky, and dependent on a key.

The honest gap is that **the Gemini prompt and its response binding have no automated coverage** —
prompt quality isn't unit-testable, and the binding only fails against a real response shape. That
was verified manually instead: a local stub of Gemini's OpenAI-compatible endpoint exercised the full
pipeline against live OpenLibrary, then the real key confirmed the extractions end to end. If this
were going further, that's the first place I'd add coverage — recorded-response contract tests over
the adapter, not live calls.

---

## Layout

Layered by folder in a single project. Domain depends on nothing, Application depends on Domain,
Infrastructure implements Application's abstractions, Api wires it together.

```
LibraryApp/
├─ Domain/            Book, SearchIntent, BookMatch
│                     MatchExplainer, QueryNormalizer, SubjectToken   (pure, tested)
├─ Application/       ISearchIntentParser, IBookCatalog
│                     BookSearchOrchestrator                          (the pipeline)
├─ Infrastructure/
│    Gemini/          GeminiSearchIntentParser  (IChatClient)
│    OpenLibrary/     OpenLibraryBookCatalog    (typed HttpClient)
└─ Api/               Controller, contracts, cache policy, 502 handler
```

The boundary is enforced by convention rather than by the compiler — a deliberate trade for a project
this size. Splitting into four assemblies buys compiler-enforced dependency rules at the cost of real
ceremony; at this scale the folder convention is legible enough that the enforcement isn't earning
its keep. It would be a mechanical change if the project grew.

`Microsoft.Extensions.AI`'s `IChatClient` is the seam to the model, reached through Gemini's
OpenAI-compatible endpoint, so swapping providers doesn't touch the core.

---

## Known limitations

**The cache is in-memory and per-process.** Lost on restart, not shared across instances, bounded at
100 MB — so the 7-day expiry really means "7 days or until the process recycles". Fine single-instance.
In production this would be a Redis-backed `IOutputCacheStore`, which is a DI registration only, since
`QueryNormalizer` produces a key and knows nothing about storage. Note that a distributed store does
*not* give you a distributed lock: N instances can still each make one upstream call for the same cold
key, and closing that needs an explicit lock rather than just Redis.

**The cache key is a set of tokens.** Sorting discards word order, and `Distinct()` discards frequency.
`man bites dog` and `dog bites man` share a key. There's no stemmer either, so `cozy mystery` and
`cozy mysteries` do *not* share one. Both are acceptable for book search; neither is invisible.

**On a cache hit, the `query` field echoes the wording that seeded the entry**, not what the caller
sent — an inherent consequence of collapsing equivalent queries onto one entry. Treat it as provenance.

**No paging.** Top 20, by OpenLibrary's own relevance order.

**Edition and format are not supported.** They aren't in OpenLibrary's search index — that data lives
at `/works/{id}/editions.json`, and honoring them would mean N extra round trips per search. The
intent schema omits them rather than accepting fields it can't act on.

---

## Design notes

A few decisions worth calling out, since the reasoning isn't visible in the diff:

- **Gemini's token caching is not used.** The system prompt is 440 tokens, below the 1,024-token
  minimum for implicit caching, so it never engages — confirmed by inspecting `usage` across repeated
  identical calls. It also wouldn't help much: the output cache already eliminates the *entire* call
  for repeat queries, which strictly dominates a discount on a call you're not making.
- **One retry, then fail.** Both upstreams get a single retry with backoff and a bounded per-attempt
  timeout. OpenLibrary answers in ~3.5s on a good query and ~18.8s on an awkward one, so the timeout
  is what keeps worst-case latency finite rather than unbounded.
- **The result limit is a constant, not a parameter.** Because the cache keys only on the normalized
  query, a caller-supplied `limit` would let two differently-sized requests collide on one entry.
