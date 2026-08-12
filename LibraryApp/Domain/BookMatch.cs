namespace LibraryApp.Domain;

/// <summary>A retrieved book, the rank band it landed in, and why.</summary>
public sealed record BookMatch(Book Book, MatchTier Tier, string Reason);
