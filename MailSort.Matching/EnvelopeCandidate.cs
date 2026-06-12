namespace MailSort.Matching;

/// <summary>
/// A candidate envelope, supplied by the caller, that the matcher will
/// score against the query image. The library stores only the fields it
/// needs to compare; the caller owns the rest (image path, status, etc.).
/// </summary>
/// <param name="Id">Stable identifier (e.g. DB primary key).</param>
/// <param name="Barcode">Barcode value if known (e.g. from the machine's
/// 1st-pass scan, or from operator manual entry).</param>
/// <param name="Tray">Tray number this candidate was previously routed
/// to. Null if the candidate has no determined tray.</param>
/// <param name="Fingerprint">The precomputed fingerprint stored at scan
/// time, used for fast comparison without re-reading the image.</param>
public sealed record EnvelopeCandidate(
    string Id,
    string? Barcode,
    int? Tray,
    Fingerprint Fingerprint,
    MatchSource Source = MatchSource.Unknown);
