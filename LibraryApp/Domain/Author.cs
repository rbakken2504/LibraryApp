namespace LibraryApp.Domain;

/// <summary>
/// A credited author and the catalogue's identifier for them, kept together.
/// </summary>
/// <remarks>
/// These arrive from OpenLibrary as two parallel arrays, and were once stored that way. Pairing them
/// here is not tidiness: de-duplicating the names without de-duplicating the keys silently shifted
/// them out of step, so looking up "Christopher Tolkien" could return the key of a duplicate
/// J.R.R. Tolkien record and list the wrong author's works. One type makes that unrepresentable.
/// </remarks>
public sealed record Author(string Name, string Key);
