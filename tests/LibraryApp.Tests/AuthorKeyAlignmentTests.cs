using LibraryApp.Domain;
using LibraryApp.Infrastructure.OpenLibrary;

namespace LibraryApp.Tests;

public class AuthorKeyAlignmentTests
{
    [Fact]
    public void Deduplicating_author_names_must_not_desynchronise_them_from_their_keys()
    {
        // A work whose first two author records are the same person spelled differently — exactly
        // what NameKey.Distinct exists to collapse.
        var doc = new OpenLibraryDoc
        {
            Key = "/works/OL1W",
            Title = "The Silmarillion",
            AuthorName = ["J.R.R. Tolkien", "J R R Tolkien", "Christopher Tolkien"],
            AuthorKey = ["OL_JRR_A", "OL_JRR_B", "OL_CHRISTOPHER"]
        };

        var book = OpenLibraryBookCatalog.ToBook(doc);

        // The duplicate spelling is folded away, and every surviving author keeps their own key.
        Assert.Equal(["J.R.R. Tolkien", "Christopher Tolkien"], book.Authors.Select(a => a.Name));

        var christopher = book.Authors.Single(a => NameKey.Matches("christopher tolkien", a.Name));
        Assert.Equal("OL_CHRISTOPHER", christopher.Key);

        var tolkien = book.Authors.First(a => NameKey.Matches("j.r.r. tolkien", a.Name));
        Assert.Equal("OL_JRR_A", tolkien.Key);
    }
}
