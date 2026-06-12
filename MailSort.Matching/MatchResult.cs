namespace MailSort.Matching;

/// <summary>
/// A single matched envelope returned by the matcher. The matcher only
/// returns this when the candidate was within both the address and
/// barcode pHash thresholds (configurable). The caller uses
/// <see cref="EnvelopeId"/> to look up the full record in its store.
/// </summary>
public sealed record MatchedEnvelope(
    string EnvelopeId,
    string? Barcode,
    int? Tray,
    MatchSource Source,
    Fingerprint Fingerprint);

/// <summary>
/// Result of a single match operation. <see cref="Match"/> is null when
/// no candidate was close enough; in that case <see cref="ClosestAddressDistance"/>
/// still surfaces the top-1 pHash distance so the caller can log
/// "almost matched" diagnostics.
/// </summary>
public sealed record MatchResult
{
    public MatchedEnvelope? Match { get; init; }
    public int ClosestAddressDistance { get; init; } = -1;
    public int MatchedAddressDistance { get; init; }
    public int MatchedBarcodeDistance { get; init; }
    public int MatchedCenterlineDistance { get; init; }
    public double Score { get; init; } = double.MaxValue;
    public double SkewDegrees { get; init; }
    public int CandidatesScanned { get; init; }

    /// <summary>
    /// The fingerprint of the query image. The caller persists this on
    /// the new Envelope row regardless of whether a match was found, so
    /// the next scan of the same envelope can be matched against it.
    /// </summary>
    public Fingerprint Fingerprint { get; init; } = Fingerprint.Zero;
}
