namespace MailSort.Matching.Configuration;

/// <summary>
/// All configuration owned by the MailSort.Matching library. Bound from
/// the "Match" section of <c>appsettings.json</c> via
/// <see cref="Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.AddOptions{TOptions}"/>.
/// </summary>
public sealed class MatchSettings
{
    public const string SectionName = "Match";

    /// <summary>
    /// Top-level matcher options (window, top-K, ROI geometry).
    /// </summary>
    public MatchEngineSettings MatchEngine { get; set; } = new();

    /// <summary>
    /// Lookback window (hours) when scanning candidates from the caller.
    /// The library does not enforce this -- it is informational and
    /// surfaced in diagnostics so the caller can size its candidate list.
    /// </summary>
    public int WindowHours { get; set; } = 24;
}

public sealed class MatchEngineSettings
{
    /// <summary>Max Hamming distance on the address zone-mean hash (0..64).</summary>
    public int MaxAddressPHashDistance { get; set; } = 8;

    /// <summary>Max Hamming distance on the barcode zone-mean hash (0..64).</summary>
    public int MaxBarcodePHashDistance { get; set; } = 10;

    /// <summary>Top-K candidates kept per ingest (1..32).</summary>
    public int TopK { get; set; } = 7;

    /// <summary>ROI for the recipient address block (normalized 0..1).</summary>
    public RegionOfInterestOptions AddressRoi { get; set; } = RegionOfInterestOptions.DefaultAddressBlock();

    /// <summary>ROI for the 2D barcode (normalized 0..1).</summary>
    public RegionOfInterestOptions BarcodeRoi { get; set; } = RegionOfInterestOptions.DefaultBarcode();
}

/// <summary>
/// Mirror of <see cref="RegionOfInterest"/> for configuration binding.
/// Kept separate so configuration POCOs do not depend on the runtime types.
/// </summary>
public sealed class RegionOfInterestOptions
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public void Validate(string name)
    {
        if (X < 0 || X > 1) throw new ArgumentOutOfRangeException($"{name}.X", X, "Must be in [0, 1].");
        if (Y < 0 || Y > 1) throw new ArgumentOutOfRangeException($"{name}.Y", Y, "Must be in [0, 1].");
        if (Width <= 0 || Width > 1) throw new ArgumentOutOfRangeException($"{name}.Width", Width, "Must be in (0, 1].");
        if (Height <= 0 || Height > 1) throw new ArgumentOutOfRangeException($"{name}.Height", Height, "Must be in (0, 1].");
        if (X + Width > 1.0001) throw new ArgumentException($"{name} extends past the right edge of the image.");
        if (Y + Height > 1.0001) throw new ArgumentException($"{name} extends past the bottom edge of the image.");
    }

    public static RegionOfInterestOptions DefaultAddressBlock() => new()
    {
        // The full "bar code label" block in the upper-right of the
        // MARS Elections Canada envelope. Covers the elector name,
        // riding, and the 2D barcode matrix — the entire labelled
        // rectangle from the left edge of the label to the right edge
        // of the envelope, from the top of the label to below the
        // barcode number. This is the canonical fingerprint ROI:
        // the printed content is identical between re-scans of the
        // same physical envelope and unique across electors.
        // The barcode label block on the right side of the envelope,
        // starting just below the static "Place bar code label here"
        // instruction text (which is identical on every envelope) and
        // covering the elector name + riding + 2D barcode matrix +
        // NAT number. This is the most discriminative region.
        X = 0.61, Y = 0.21, Width = 0.36, Height = 0.10
    };

    public static RegionOfInterestOptions DefaultBarcode() => new()
    {
        // The 2D barcode matrix + NAT number only.
        X = 0.61, Y = 0.31, Width = 0.36, Height = 0.16
    };
}
