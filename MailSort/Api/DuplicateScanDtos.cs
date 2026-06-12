namespace MailSort.Api;

/// <summary>
/// ROI settings (normalized 0..1 image coordinates) that the
/// analyzer used to compute the fingerprints. Surfaced in the
/// report so the UI can label the ROI crops with the actual
/// coordinates that were used.
/// </summary>
public sealed record RoiSettings(double X, double Y, double Width, double Height)
{
    public override string ToString() =>
        $"({X:F3}, {Y:F3})  {Width:F3}×{Height:F3}";
}

/// <summary>
/// One scan of an envelope, as it appears in a single column of the
/// duplicate-scan report.
/// </summary>
/// <param name="FileName">Just the filename (no path) for display.</param>
/// <param name="ImageUrl">URL the browser can load to render the full image.</param>
/// <param name="AddressRoiUrl">URL the browser can load to render the address-ROI crop
/// (the exact region of the image that was hashed for the address pHash).</param>
/// <param name="BarcodeRoiUrl">URL the browser can load to render the barcode-ROI crop.</param>
/// <param name="SizeBytes">On-disk size of the file.</param>
/// <param name="WidthPx">Image width in pixels (null if it could not be decoded).</param>
/// <param name="HeightPx">Image height in pixels (null if it could not be decoded).</param>
/// <param name="AddressPHashHex">64-bit address pHash in hex.</param>
/// <param name="BarcodePHashHex">64-bit barcode pHash in hex.</param>
/// <param name="CenterlineHashHex">64-bit centerline gradient hash in hex.</param>
/// <param name="SkewDegrees">Estimated deskew angle, in degrees.</param>
/// <param name="FingerprintMs">Wall-clock time in milliseconds to compute the fingerprint for this scan.</param>
public sealed record DuplicateScanViewModel(
    string FileName,
    string ImageUrl,
    string? AddressRoiUrl,
    string? BarcodeRoiUrl,
    long SizeBytes,
    int? WidthPx,
    int? HeightPx,
    string AddressPHashHex,
    string BarcodePHashHex,
    string CenterlineHashHex,
    double SkewDegrees,
    long FingerprintMs);

/// <summary>
/// Per-envelope row in the duplicate-scan report. Each row holds
/// every scan of the same envelope (one column per scan) plus the
/// summary statistics the matcher computed across those scans.
/// </summary>
public sealed record DuplicateScanRow(
    string EnvelopeId,
    bool IsDuplicateGroup,
    IReadOnlyList<DuplicateScanViewModel> Scans,
    int? AddressHammingBetweenFirstTwo,
    int? BarcodeHammingBetweenFirstTwo,
    int? CenterlineHammingBetweenFirstTwo,
    bool? EngineRecognizedPair,
    string? EngineVerdict,
    string? ClosestNonPairFileName,
    int? ClosestNonPairAddressHamming);

/// <summary>
/// Aggregate stats over the OK-recognized pairs. Surfaced so the
/// UI can show "avg address Hamming across the N pairs we got
/// right" without having to recompute it on the client.
/// </summary>
public sealed record DispositionSummary(
    int RecognizedPairCount,
    int WithinThresholdButMissedCount,
    int OutsideThresholdCount,
    int SingletonCount,
    int ClosestNonPairOverlapCount,
    double? AvgAddressHammingRecognized,
    double? AvgBarcodeHammingRecognized,
    double? MaxAddressHammingRecognized,
    double? MinClosestNonPairAddressHamming);

/// <summary>
/// The full report for a folder. Includes the input parameters and
/// stats so the UI can show what was actually analyzed and how the
/// matcher performed on it.
/// </summary>
public sealed record DuplicateScanReport(
    string Folder,
    string? Folder2,
    int EnvelopeCount,
    int ScanCount,
    int PairCount,
    int RecognizedPairCount,
    int MaxAddressPHashDistance,
    int MaxBarcodePHashDistance,
    bool DefaultThresholds,
    RoiSettings AddressRoi,
    RoiSettings BarcodeRoi,
    DispositionSummary Summary,
    IReadOnlyList<DuplicateScanRow> Rows);
