# LibraryApp

Natural-language book search. You ask for *"gritty space opera about corporate politics"* or
*"dune by frank herbert"*; Gemini resolves that into structured search fields, OpenLibrary retrieves
against them, and the results come back ranked into tiers with an explanation of why each matched.

Name a book and its author and there is one unambiguous answer, so that is all you get:

```
GET /api/books/search?q=dune by frank herbert

{
  "query": "dune by frank herbert",
  "interpretation": "I searched for the book Dune authored by Frank Herbert.",
  "broadened": false,
  "clearWinner": true,
  "count": 1,
  "results": [
    {
      "key": "/works/OL893414W",
      "title": "Dune",
      "authors": ["Frank Herbert", "Френк Герберт"],
      "firstPublishYear": 1965,
      "coverUrl": "https://covers.openlibrary.org/b/id/11481354-M.jpg",
      "editionCount": 160,
      "tier": "ExactTitlePrimaryAuthor",
      "tierLabel": "Exact match",
      "reason": "Exact title match, by the author you named; by Frank Herbert; tagged science fiction."
    }
  ]
}
```

Where the answer is genuinely ambiguous you get up to five, ranked. `the hobbit by tolkien` returns
the novel and the graphic-novel adaptation both as **Exact match**, then two **Close match** variants,
then a **Same author** work — rather than silently picking one.

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
                                            fielded query  ─┐
                                            free-text probe ┘ concurrent
                                                  │
                                          CandidateRanker (pure, no AI)
                                            tier + explanation
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

### Ranking

Retrieval order is the catalogue's; the rank order is ours. `CandidateRanker` bands every candidate,
strongest first:

| Tier | Meaning | Badge |
|---|---|---|
| `ExactTitlePrimaryAuthor` | title matches and the named person wrote it | Exact match |
| `ExactTitleContributor` | title matches, but they only narrated / illustrated / edited it | Exact title, contributor |
| `NearTitleAuthor` | the asked-for title sits inside a longer one, author confirmed | Close match |
| `TitleOnly` | title matches, author unconfirmed or not asked for | Title match |
| `AuthorOnly` | no title match — these are that author's works | Same author |
| `Discovery` | subject-led browsing; catalogue order untouched | Suggested |

Exactly one candidate at the top tier sets `clearWinner` and returns that book alone. A tie there is
ambiguity, not a winner, so the full list of five comes back — `the hobbit by tolkien` returns both
the novel and the graphic-novel adaptation rather than silently picking one.

Within a tier the catalogue's relevance survives as the tiebreak, with one adjustment: near-matches
prefer fewer surplus title tokens, so *The Hobbit, or There and Back Again* outranks
*The Hobbit & The Lord of the Rings [collection/set]*.

**Two findings made this cheap.** The search response's `author_key` proved identical to the work
record's `authors` on every case checked, so the primary-author list is already in hand and no
`/works/{id}.json` fetch is needed — worth about 2.5s each, since the detail endpoints are as slow as
search. And `contributor` is a separate field carrying roles (`Scott Brick (Narrator)`), which is
precisely the top-two-tier discriminator.

**One extra call is unavoidable.** `author=` matches only primary authors, so a contributor-only
match cannot come back from the fielded query — `title=dune&author=scott+brick` finds none, while
free-text `q=dune scott brick` finds three. The two queries therefore run **concurrently** and merge
on work key; both are ~2s, so a title-plus-author search still completes in 1.2–1.4s.

The probe is best-effort. It can only *add* lower-tier candidates, so if it fails the search returns
the fielded results rather than 502. That is a deliberate exception to the no-silent-degradation rule
below — a failure of the *primary* query still surfaces as 502.

### Explanations are derived, not generated

There is no second AI call. `MatchExplainer` states the tier, then which intent fields the book
satisfied — *"Exact title match, but the person you named only contributed; credits Scott Brick as
narrator."* Every clause is a fact about the query constraints and the returned document, so it costs
nothing per result and cannot hallucinate. Gemini's `interpretation` covers how the query was read.

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

The ladder only drops a title when *subjects* remain. When an author was named, the author fallback
takes over instead and calls `/authors/{key}/works.json`, which answers "top works by this author"
directly rather than approximating it with a filter.

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

Then open <http://localhost:5014> for the search page, send the requests in
`LibraryApp/LibraryApp.http`, or:

```bash
curl "http://localhost:5014/api/books/search?q=dune by frank herbert"
```

### Front-end

`wwwroot/index.html` is the entire UI — a search box and a result list, using Vue 3 and Tailwind 4
from CDN. There is no npm, no build step and no CORS configuration: the page is served from the same
origin as the API by `UseStaticFiles`, so `dotnet run` is the only command needed.

It surfaces `interpretation`, `broadened` and the ranking tier alongside each result, since how the
query was read and how strongly a book matched are the interesting parts of the response.

This is a demo UI rather than a production asset pipeline — see
[Not built](#2-a-real-front-end-build).

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
| `200` | `BookSearchResponse` — up to 5 ranked results, or exactly one when `clearWinner` is true |
| `400` | `q` missing or blank |
| `502` | Gemini or OpenLibrary unreachable after one retry |

Each result carries `tier` (the enum name) and `tierLabel` (display text); the response carries
`clearWinner`. See [Ranking](#ranking) for what the tiers mean.

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

102 tests, no mocking framework, no network, no API key required. The full suite runs in ~40ms.

```bash
dotnet test
```

The strategy is to test the code that holds decisions and to skip the code that only holds wiring.

**Pure logic, exhaustively** — `QueryNormalizerTests`, `SubjectTokenTests`, `TitleKeyTests`,
`NameKeyTests`. Table-driven `[Theory]` cases over the functions with real branching: term
reordering, casing, diacritics, punctuation, stop words, duplicates, empty input. Two assertions
matter most and pull in opposite directions — that equivalent queries *do* collapse to one
fingerprint, and that genuinely different ones *don't* collide. A cache key that over-collapses
serves the wrong results, which is far worse than a missed cache hit.

Two of these guard traps rather than behaviour. `TitleKeyTests` asserts that "The Book Thief" keeps
its `book` token, because `QueryNormalizer` would strip it — the two normalizers look
interchangeable and are not, so the test states the contrast side by side. `NameKeyTests` asserts
that a bracketed aside which *isn't* a role — life dates, an organisation, expanded initials —
is stripped rather than reported; treating every parenthetical as a role produced
*"credits Tolkien, J. R. R. as john ronald reuel"* in a real search.

**The ranking tiers** — `CandidateRankerTests`. This is where the requirements live, so it gets the
most cases: primary author above contributor for the same title, exact above near, the tightest near
match first, a tie at the top tier refusing to declare a winner, and subject-only intents leaving the
catalogue's order alone. The fixtures are shaped from live responses — including Dune's two author
records for one person and the Hobbit graphic novel's `["Charles Dixon", "Sean Deming",
"J.R.R. Tolkien"]` — so they exercise the awkward data rather than an idealised version of it.

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
pipeline against live OpenLibrary, then the real key confirmed the extractions end to end. See
[Not built](#3-an-eval-suite-for-the-gemini-prompt) for what would close it.

That gap is not hypothetical. Of the three bugs found by running real queries rather than tests, two
were in deterministic code and are now covered — reintroducing either fails the suite. The third was
the prompt returning a null author for *"dune narrated by scott brick"*, and nothing in the suite
would catch it coming back. The bugs in deterministic code were caught the moment tests were written
for them; the one at the probabilistic boundary needed a live query.

---

## Layout

Layered by folder in a single project. Domain depends on nothing, Application depends on Domain,
Infrastructure implements Application's abstractions, Api wires it together.

```
LibraryApp/
├─ Domain/            Book, SearchIntent, BookMatch, MatchTier
│                     CandidateRanker                                 (the tier rules)
│                     TitleKey, NameKey, QueryNormalizer, SubjectToken
│                     MatchExplainer                                  (all pure, all tested)
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

Limits of what *is* built. Things deliberately left out are in [Not built](#not-built-and-why) below.

**The cache key is a set of tokens.** Sorting discards word order, and `Distinct()` discards frequency.
`man bites dog` and `dog bites man` share a key. There's no stemmer either, so `cozy mystery` and
`cozy mysteries` do *not* share one. Both are acceptable for book search; neither is invisible.

**On a cache hit, the `query` field echoes the wording that seeded the entry**, not what the caller
sent — an inherent consequence of collapsing equivalent queries onto one entry. Treat it as provenance.

**No paging.** Top 5, tier-ranked.

**Transliterated author records aren't folded.** Dune carries both `Frank Herbert` and
`Френк Герберт` as separate author records for the same person. `NameKey.Distinct` collapses
punctuation and casing variants, but connecting these would mean fetching `/authors/{id}.json` for
its `alternate_names` — a ~2.2s call per author on every result. The duplicate is left visible rather
than paid for.

**Edition and format are not supported.** They aren't in OpenLibrary's search index — that data lives
at `/works/{id}/editions.json`, and honoring them would mean N extra round trips per search. The
intent schema omits them rather than accepting fields it can't act on.

---

## Not built, and why

This is an interview exercise, not a production service. The following are deliberate omissions with
a known cost rather than things that were missed.

### 1. A distributed cache, and a distributed lock with it

Today the output cache is in-memory and per-process: lost on restart, bounded at 100 MB, and not
shared between instances, so the 7-day expiry really means "7 days or until the process recycles".
Single-instance that is fine, and it is what makes a reworded repeat search return in 8ms.

In production this becomes a Redis-backed `IOutputCacheStore`. That part is a DI registration and
nothing else, because `QueryNormalizer` produces a key and knows nothing about where it is stored.

The part worth stating explicitly is that **Redis alone does not finish the job**. `AllowLocking` is
per-process, so making the *store* distributed does not make the *lock* distributed: N instances can
still each fire a Gemini call and an OpenLibrary round trip for the same cold key. Closing that needs
an explicit distributed lock so the first request populates the entry and the rest wait on it. Given
a cold search costs an AI call plus a multi-second catalogue call, that is worth doing at any real
traffic level.

### 2. A real front-end build

`wwwroot/index.html` is the whole UI, with Vue and Tailwind from CDN and no npm. Tailwind's browser
build compiles CSS at runtime and is officially a development tool, so this is a demo page rather
than an asset pipeline.

Production would be Vite with single-file components, tree-shaken CSS, and the page split into
components instead of one template with one `setup()`. The swap is that one file plus deleting two
script tags — the API contract does not move.

### 3. An eval suite for the Gemini prompt

Prompt behaviour has no automated coverage, and unit tests structurally cannot provide it. The
concrete instance: *"dune narrated by scott brick"* returned a null author, making the contributor
tier unreachable through natural phrasing, until the prompt was changed. No unit test would catch
that regression — deleting the example from the prompt fails nothing.

What would work is an eval: a fixed set of query → expected-intent pairs run against the live model,
asserting on extracted fields. It belongs outside `dotnet test`, since it needs an API key, costs
money per run, and is non-deterministic.

### 4. A fallback catalogue

**OpenLibrary is a single point of failure.** If it is down, every search returns 502 — there is no
second source. It is also the slowest part of the pipeline and not consistently fast: a well-formed
fielded query answers in ~2s, but awkward shapes were measured at 18.8s and free text over common
words past 25s. Availability and tail latency both rest on one third party.

A second provider behind the existing `IBookCatalog`, with a circuit breaker choosing between them,
would fix that. The interface already makes the substitution clean — that boundary was drawn for
testability and pays off here.

Two things make this more than swapping an adapter, and they are the reason it is called out rather
than quietly assumed easy:

- **The ranking tiers are coupled to OpenLibrary's data model.** They depend on `author_key` matching
  the work record's authors and on `contributor` being a separate role-carrying field. A different
  provider will not expose the same shape, so either the tiers degrade on the fallback path or the
  ranker needs a provider-neutral notion of "primary author" versus "contributor".
- **Results would need reconciling.** Work identifiers differ per provider, so a cached response and
  a live one could disagree about the same book, and `key`-based de-duplication stops working across
  sources.

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
