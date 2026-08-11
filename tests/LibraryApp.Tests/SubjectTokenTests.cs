using BookSearchService.Domain;

namespace LibraryApp.Tests;

public class SubjectTokenTests
{
    [Theory]
    [InlineData("Science Fiction", "science_fiction")]
    [InlineData("sci-fi", "sci_fi")]
    [InlineData("science_fiction", "science_fiction")]
    [InlineData("  space  opera  ", "space_opera")]
    [InlineData("detective & mystery stories", "detective_mystery_stories")]
    [InlineData("\"quoted subject\"", "quoted_subject")]
    public void Sanitize_produces_a_safe_subject_token(string raw, string expected)
        => Assert.Equal(expected, SubjectToken.Sanitize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Sanitize_returns_empty_when_nothing_usable_remains(string? raw)
        => Assert.Equal(string.Empty, SubjectToken.Sanitize(raw));

    [Fact]
    public void Sanitize_strips_characters_that_would_break_a_solr_filter()
    {
        // A raw space or quote reaching subject: would either break the query or push it onto
        // OpenLibrary's free-text path, which has been measured at >25s.
        var token = SubjectToken.Sanitize("space opera: \"the good stuff\"");

        Assert.DoesNotContain(' ', token);
        Assert.DoesNotContain('"', token);
        Assert.DoesNotContain(':', token);
    }
}
