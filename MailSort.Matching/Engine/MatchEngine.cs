using MailSort.Matching.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailSort.Matching.Engine;

/// <summary>
/// Reference <see cref="IMatchEngine"/>. Stateless and thread-safe;
/// registered as a singleton.
/// </summary>
public sealed class MatchEngine : IMatchEngine
{
    private readonly MatchSettings _settings;
    private readonly ILogger<MatchEngine> _log;

    public MatchEngine(IOptions<MatchSettings> settings, ILogger<MatchEngine> log)
    {
        _settings = settings.Value;
        _log = log;
    }

    public async Task<MatchResult> MatchAsync(
        Stream imageStream,
        IReadOnlyList<EnvelopeCandidate> candidates,
        CancellationToken ct = default)
    {
        var query = await ComputeFingerprintAsync(imageStream, ct);

        if (candidates.Count == 0)
        {
            return new MatchResult
            {
                Match = null,
                ClosestAddressDistance = -1,
                Score = double.MaxValue,
                SkewDegrees = query.SkewDegrees,
                CandidatesScanned = 0,
                Fingerprint = query,
            };
        }

        var top = TopKByBarcode(query, candidates);
        var best = PickBestByCombinedScore(query, top, candidates);

        if (best is null)
        {
            _log.LogInformation(
                "Match: no candidate accepted. ClosestBarcode={Closest}; candidates={Count}; skew={Skew}deg",
                top[0].P, candidates.Count, query.SkewDegrees);

            return new MatchResult
            {
                Match = null,
                ClosestAddressDistance = top[0].P == int.MaxValue ? -1 : top[0].P,
                Score = double.MaxValue,
                SkewDegrees = query.SkewDegrees,
                CandidatesScanned = candidates.Count,
                Fingerprint = query,
            };
        }

        _log.LogInformation(
            "Match: picked {Id} barcode={Barcode} tray={Tray} addr={A} barc={B} center={C} score={Score} skew={Skew}deg",
            best.Id, best.Barcode, best.Tray, best.AddressDistance, best.BarcodeDistance,
            best.CenterlineDistance, best.Score, query.SkewDegrees);

        return new MatchResult
        {
            Match = new MatchedEnvelope(
                EnvelopeId: best.Id,
                Barcode: best.Barcode,
                Tray: best.Tray,
                Source: best.Source,
                Fingerprint: best.Fingerprint),
            ClosestAddressDistance = top[0].P == int.MaxValue ? -1 : top[0].P,
            MatchedAddressDistance = best.AddressDistance,
            MatchedBarcodeDistance = best.BarcodeDistance,
            MatchedCenterlineDistance = best.CenterlineDistance,
            Score = best.Score,
            SkewDegrees = query.SkewDegrees,
            CandidatesScanned = candidates.Count,
            Fingerprint = query,
        };
    }

    public async Task<Fingerprint> ComputeFingerprintAsync(
        Stream imageStream,
        CancellationToken ct = default)
    {
        var addressRoi = ToRegionOfInterest(_settings.MatchEngine.AddressRoi);
        var barcodeRoi = ToRegionOfInterest(_settings.MatchEngine.BarcodeRoi);
        return await RegionalFingerprint.ComputeAsync(imageStream, addressRoi, barcodeRoi, ct);
    }

    private static RegionOfInterest ToRegionOfInterest(RegionOfInterestOptions o) =>
        new(o.X, o.Y, o.Width, o.Height);

    /// <summary>
    /// Top-K candidates by barcode pHash distance. After centroid alignment
    /// the barcode zone-mean hash has within-pair distance 0–14, making it
    /// the most reliable primary pre-filter.
    /// </summary>
    private (string Id, int P)[] TopKByAddress(
        Fingerprint query, IReadOnlyList<EnvelopeCandidate> candidates)
    {
        var k = Math.Min(_settings.MatchEngine.TopK, candidates.Count);
        var top = new (string Id, int P)[k];
        for (int i = 0; i < k; i++) top[i] = (string.Empty, int.MaxValue);

        foreach (var c in candidates)
        {
            var d = RegionalFingerprint.HammingDistance(query.AddressPHash, c.Fingerprint.AddressPHash);
            if (d >= top[k - 1].P) continue;
            int j = k - 1;
            while (j > 0 && top[j - 1].P > d) j--;
            for (int s = k - 1; s > j; s--) top[s] = top[s - 1];
            top[j] = (c.Id, d);
        }
        return top;
    }

    /// <summary>
    /// Top-K candidates by barcode pHash distance. Used for secondary pre-filter.
    /// </summary>
    private (string Id, int P)[] TopKByBarcode(
        Fingerprint query, IReadOnlyList<EnvelopeCandidate> candidates)
    {
        var k = Math.Min(_settings.MatchEngine.TopK, candidates.Count);
        var top = new (string Id, int P)[k];
        for (int i = 0; i < k; i++) top[i] = (string.Empty, int.MaxValue);

        foreach (var c in candidates)
        {
            var d = RegionalFingerprint.HammingDistance(query.BarcodePHash, c.Fingerprint.BarcodePHash);
            if (d >= top[k - 1].P) continue;
            int j = k - 1;
            while (j > 0 && top[j - 1].P > d) j--;
            for (int s = k - 1; s > j; s--) top[s] = top[s - 1];
            top[j] = (c.Id, d);
        }
        return top;
    }

    private sealed record ScoredCandidate(
        string Id,
        string? Barcode,
        int? Tray,
        MatchSource Source,
        Fingerprint Fingerprint,
        int AddressDistance,
        int BarcodeDistance,
        int CenterlineDistance,
        double Score);

    /// <summary>
    /// For each top-K barcode candidate, apply the barcode hard gate and
    /// pick the lowest combined score.
    /// Primary channel: barcode (2x). Tiebreaker: centerline (1x).
    /// Hard-reject on barcode &gt; bMax. Address hash not used: some envelopes
    /// have a blank address ROI which produces an unstable all-zero hash.
    /// </summary>
    private ScoredCandidate? PickBestByCombinedScore(
        Fingerprint query,
        (string Id, int P)[] top,
        IReadOnlyList<EnvelopeCandidate> candidates)
    {
        var bMax = _settings.MatchEngine.MaxBarcodePHashDistance;

        ScoredCandidate? best = null;
        foreach (var c in top)
        {
            // c.P is the barcode distance from TopKByBarcode pre-filter.
            if (c.P == int.MaxValue) break;
            if (c.P > bMax) continue;
            var full = candidates.First(x => x.Id == c.Id);
            var aD = RegionalFingerprint.HammingDistance(query.AddressPHash, full.Fingerprint.AddressPHash);
            var cD = RegionalFingerprint.HammingDistance(query.CenterlineHash, full.Fingerprint.CenterlineHash);
            // BarcodePHash is the combined label hash (address + barcode region).
            // Primary weight 3x. Address is a tiebreaker (1x) when both hashes
            // are non-zero (envelopes without printed address text have addrHash=0).
            // Centerline (bottom strip) is only a last-resort fractional tiebreaker —
            // it is far from the pivot at high tilts so its contribution is kept tiny.
            var addrWeight = (query.AddressPHash == 0 || full.Fingerprint.AddressPHash == 0) ? 0.0 : 1.0;
            var score = 3.0 * c.P + addrWeight * aD + 0.01 * cD;
            if (best is null || score < best.Score)
            {
                best = new ScoredCandidate(
                    Id: full.Id,
                    Barcode: full.Barcode,
                    Tray: full.Tray,
                    Source: full.Source,
                    Fingerprint: full.Fingerprint,
                    AddressDistance: aD,
                    BarcodeDistance: c.P,
                    CenterlineDistance: cD,
                    Score: score);
            }
        }
        return best;
    }
}
