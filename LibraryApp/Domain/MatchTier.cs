namespace LibraryApp.Domain;

/// <summary>
/// Rank bands, strongest first. The numeric values are the sort order, so declaration order matters.
/// </summary>
public enum MatchTier
{
    /// <summary>Title matches exactly and the reader's author is a primary author of the work.</summary>
    ExactTitlePrimaryAuthor = 0,

    /// <summary>
    /// Title matches exactly, but the named person only contributed — narrator, illustrator, editor.
    /// Ranked below a primary-author match on purpose.
    /// </summary>
    ExactTitleContributor = 1,

    /// <summary>Title is contained in a longer one (subtitle, collection) and the author matches.</summary>
    NearTitleAuthor = 2,

    /// <summary>Title matched but no author was asked for, so there is nothing to corroborate it.</summary>
    TitleOnly = 3,

    /// <summary>No usable title: these are the author's works.</summary>
    AuthorOnly = 4,

    /// <summary>Subject-led browsing. Ordering is the catalogue's relevance, untouched.</summary>
    Discovery = 5
}
