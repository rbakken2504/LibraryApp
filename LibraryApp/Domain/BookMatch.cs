namespace LibraryApp.Domain;

/// <summary>A retrieved book paired with why it satisfied the query.</summary>
public sealed record BookMatch(Book Book, string Reason);
