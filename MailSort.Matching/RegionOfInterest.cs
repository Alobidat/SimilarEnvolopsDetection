namespace MailSort.Matching;

/// <summary>
/// A rectangular region expressed in normalized image coordinates
/// (0..1 of the image's width/height). Used to crop the image to a
/// data-varying area before hashing, so the hash is not dominated by
/// static envelope paper, borders, or background noise.
/// </summary>
public readonly record struct RegionOfInterest(double X, double Y, double Width, double Height)
{
    /// <summary>True if every field is within [0, 1] and the region has non-zero area.</summary>
    public bool IsValid =>
        X >= 0 && X <= 1
        && Y >= 0 && Y <= 1
        && Width > 0 && Width <= 1
        && Height > 0 && Height <= 1
        && X + Width <= 1.0001
        && Y + Height <= 1.0001;

    /// <summary>
    /// Default ROI used internally for the third (centerline) channel.
    /// Tuned for the 1440x832 / 1472x832 portrait MARS Elections
    /// Canada envelopes: the centerline channel hashes the elector's
    /// signature + date strip, which is the strongest discriminator
    /// between two different envelopes and the most stable signal
    /// across re-scans of the same envelope.
    /// </summary>
    public static RegionOfInterest DefaultAddressAndBarcode =>
        new(0.02, Y: 0.71, Width: 0.96, Height: 0.13);
}
