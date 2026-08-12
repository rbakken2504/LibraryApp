using LibraryApp.Domain;

namespace LibraryApp.Tests;

public class NameKeyTests
{
    [Theory]
    [InlineData("herbert", "Frank Herbert", true)]
    [InlineData("frank herbert", "Frank Herbert", true)]
    [InlineData("HERBERT, Frank", "Frank Herbert", true)]
    [InlineData("j.r.r. tolkien", "J R R Tolkien", true)]
    [InlineData("brian herbert", "Frank Herbert", false)]
    [InlineData("asimov", "Frank Herbert", false)]
    [InlineData("", "Frank Herbert", false)]
    [InlineData(null, "Frank Herbert", false)]
    public void Matches_accepts_what_the_reader_typed_as_a_subset_of_the_catalogue_name(
        string? wanted, string candidate, bool expected)
        => Assert.Equal(expected, NameKey.Matches(wanted, candidate));

    [Theory]
    [InlineData("Scott Brick (Narrator)", "Scott Brick", "Narrator")]
    [InlineData("John Schoenherr (Illustrator)", "John Schoenherr", "Illustrator")]
    [InlineData("Random House Mondadori (Editor)", "Random House Mondadori", "Editor")]
    [InlineData("Someone (Editor, Introduction)", "Someone", "Editor, Introduction")]
    [InlineData("Frank Herbert", "Frank Herbert", null)]
    [InlineData("", "", null)]
    public void SplitRole_separates_the_person_from_their_credited_role(
        string entry, string expectedName, string? expectedRole)
    {
        var (name, role) = NameKey.SplitRole(entry);

        Assert.Equal(expectedName, name);
        Assert.Equal(expectedRole, role);
    }

    [Theory]
    // Everything the live contributor field puts in brackets that is not a role. Treating these as
    // roles produced "credits Tolkien, J. R. R. as john ronald reuel" in a real search.
    [InlineData("Tolkien, J. R. R. (John Ronald Reuel)", "Tolkien, J. R. R.")]
    [InlineData("Herbert, Frank (1920-1986)", "Herbert, Frank")]
    [InlineData("Some Body (Library of Congress)", "Some Body")]
    [InlineData("Another Person (Firm)", "Another Person")]
    public void A_bracketed_aside_that_is_not_a_role_is_stripped_rather_than_reported_as_one(
        string entry, string expectedName)
    {
        var (name, role) = NameKey.SplitRole(entry);

        Assert.Equal(expectedName, name);
        Assert.Null(role);
    }

    [Fact]
    public void A_stripped_aside_still_leaves_the_name_matchable()
    {
        var (name, _) = NameKey.SplitRole("Tolkien, J. R. R. (John Ronald Reuel)");

        Assert.True(NameKey.Matches("tolkien", name));
    }

    [Fact]
    public void Distinct_folds_records_that_differ_only_in_punctuation_or_casing()
    {
        var folded = NameKey.Distinct([
            new Author("J.R.R. Tolkien", "OL_A"),
            new Author("J R R Tolkien", "OL_B"),
            new Author("j.r.r. tolkien", "OL_C"),
            new Author("Christopher Tolkien", "OL_D")
        ]);

        Assert.Equal(["J.R.R. Tolkien", "Christopher Tolkien"], folded.Select(a => a.Name));

        // Each survivor keeps its own key — folding names must never shuffle keys between people.
        Assert.Equal(["OL_A", "OL_D"], folded.Select(a => a.Key));
    }

    [Fact]
    public void Distinct_does_not_fold_transliterations_and_that_is_a_known_limit()
    {
        // Dune carries two author records for Frank Herbert, one Cyrillic. Connecting them would
        // mean fetching /authors/{id}.json for its alternate_names — a ~2.2s call per author, on
        // every result. The duplicate is left visible rather than paid for.
        var folded = NameKey.Distinct([
            new Author("Frank Herbert", "OL79034A"),
            new Author("Френк Герберт", "OL7388009A")
        ]);

        Assert.Equal(2, folded.Count);
    }
}
