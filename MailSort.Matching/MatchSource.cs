namespace MailSort.Matching;

/// <summary>
/// Where the candidate's tray information came from. The library uses
/// this to help the caller decide what to do with the match result.
/// </summary>
public enum MatchSource
{
    /// <summary>The candidate was unresolved or undetermined.</summary>
    Unknown = 0,

    /// <summary>The candidate's tray was determined automatically
    /// (e.g. by barcode lookup on the 1st pass).</summary>
    Automatic = 1,

    /// <summary>The candidate's tray was determined by an operator
    /// (e.g. manual entry on a previous pass).</summary>
    Manual = 2,
}
