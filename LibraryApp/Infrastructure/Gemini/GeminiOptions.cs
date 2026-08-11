namespace BookSearchService.Infrastructure.Gemini;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Set via user-secrets or environment, never appsettings.json.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Flash-lite is cheap and fast, and intent extraction is a structured-output task it handles
    /// well — measured at ~0.8s against ~1.9s for full flash, with identical extractions on the
    /// sample queries. Note that <c>gemini-2.5-flash</c> still appears in the model listing but is
    /// rejected at call time for keys issued after it was retired, so prefer the floating aliases.
    /// </summary>
    public string Model { get; set; } = "gemini-flash-lite-latest";

    /// <summary>Gemini's OpenAI-compatible surface, which lets us use the standard IChatClient plumbing.</summary>
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/";

    /// <summary>
    /// When true, requests a strict <c>json_schema</c> response format. Gemini's OpenAI-compat
    /// layer only partially supports it, so the default is the portable path: plain JSON mode with
    /// the schema described in the prompt. Flip this if strict binding proves reliable for your model.
    /// </summary>
    public bool UseStrictJsonSchema { get; set; }
}
