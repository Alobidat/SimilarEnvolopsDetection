namespace MailSort.Matching;

/// <summary>
/// A 192-bit regional fingerprint split into three 64-bit channels, each
/// computed on a different <see cref="RegionOfInterest"/>:
///   - <see cref="AddressPHash"/>: zone-mean hash on the full label block
///     (elector name + 2D barcode + NAT number). Divides the crop into
///     an 8×8 grid and hashes whether each zone is lighter or darker
///     than the median. Stable across re-scans; highly discriminative
///     between different electors.
///   - <see cref="BarcodePHash"/>: zone-mean hash on the 2D barcode matrix
///     only (same algorithm, tighter crop).
///   - <see cref="CenterlineHash"/>: a coarse gradient-direction sketch
///     of the elector signature/date strip.
/// All three are computed from the same image in a single pass.
/// </summary>
public readonly record struct Fingerprint(
    long AddressPHash,
    long BarcodePHash,
    long CenterlineHash,
    double SkewDegrees)
{
    public static Fingerprint Zero => default;
}
