namespace LibraryApp.Application.Abstractions;

/// <summary>
/// An upstream dependency (intent parser or catalog) failed after its retry budget was spent.
/// Surfaces to the client as 502 — we do not silently degrade, because a keyword-only fallback
/// returns empty or times out for exactly the vague queries this service exists to handle.
/// </summary>
public sealed class SearchUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
