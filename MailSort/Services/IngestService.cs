using System.Diagnostics;
using MailSort.Data;
using MailSort.Matching;
using MailSort.Matching.Configuration;
using MailSort.Matching.Engine;
using MailSort.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailSort.Services;

public record IngestResult(
    EnvelopeStatus Status,
    int? Tray,
    string EnvelopeId,
    string? MatchedEnvelopeId = null,
    int? MatchAddressPHashDistance = null,
    int? MatchBarcodePHashDistance = null,
    int? MatchCenterlineDistance = null,
    double? MatchScore = null,
    double? SkewDegrees = null);

/// <summary>
/// Orchestrates the 1st/2nd-pass ingest path. The image-matching
/// algorithm itself lives in <see cref="IMatchEngine"/> (MailSort.Matching);
/// this class is the orchestrator: it loads the candidate set from the
/// DB, calls the matcher, and persists the resulting Envelope row.
/// </summary>
public class IngestService
{
    private readonly MailSortDbContext _db;
    private readonly ImageStore _images;
    private readonly IMatchEngine _matcher;
    private readonly MatchSettings _matchSettings;
    private readonly ILogger<IngestService> _log;

    public IngestService(
        MailSortDbContext db,
        ImageStore images,
        IMatchEngine matcher,
        IOptions<MatchSettings> matchSettings,
        ILogger<IngestService> log)
    {
        _db = db;
        _images = images;
        _matcher = matcher;
        _matchSettings = matchSettings.Value;
        _log = log;
        _log.LogInformation(
            "IngestService: windowHours={W} aMax={A} bMax={B} topK={K} addressRoi=({X},{Y},{W},{H}) barcodeRoi=({X2},{Y2},{W2},{H2})",
            _matchSettings.WindowHours,
            _matchSettings.MatchEngine.MaxAddressPHashDistance,
            _matchSettings.MatchEngine.MaxBarcodePHashDistance,
            _matchSettings.MatchEngine.TopK,
            _matchSettings.MatchEngine.AddressRoi.X, _matchSettings.MatchEngine.AddressRoi.Y,
            _matchSettings.MatchEngine.AddressRoi.Width, _matchSettings.MatchEngine.AddressRoi.Height,
            _matchSettings.MatchEngine.BarcodeRoi.X, _matchSettings.MatchEngine.BarcodeRoi.Y,
            _matchSettings.MatchEngine.BarcodeRoi.Width, _matchSettings.MatchEngine.BarcodeRoi.Height);
    }

    /// <summary>
    /// Process a scanned envelope. If a barcode is present, look up the
    /// tray and persist with that tray. Otherwise, hash the image and
    /// search the recent-window candidate set:
    ///   - on match, route to the matched envelope's tray (2nd pass).
    ///   - on no match, flag for manual entry.
    ///
    /// The full no-barcode path is budgeted at 400ms; we log timings so
    /// a regression surfaces in the log.
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        Stream imageStream,
        string? barcodeRaw,
        string? machineScanId,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var id = Guid.NewGuid().ToString("N");
        var hasBarcode = !string.IsNullOrWhiteSpace(barcodeRaw);

        await using var buf = new MemoryStream();
        await imageStream.CopyToAsync(buf, ct);
        buf.Position = 0;

        var tSaveStart = sw.ElapsedMilliseconds;
        var imagePath = await _images.SaveAsync(id, buf, ct);
        var tSaved = sw.ElapsedMilliseconds;

        if (hasBarcode)
        {
            // For barcoded envelopes, use the tray-map lookup. We still
            // compute the fingerprint so a future 2nd-pass scan can
            // match us.
            buf.Position = 0;
            var fp = await _matcher.ComputeFingerprintAsync(buf, ct);
            var tHashed = sw.ElapsedMilliseconds;
            var tray = await ResolveTrayAsync(barcodeRaw!, ct);
            var env = new Envelope
            {
                Id = id,
                ScanTimeUtc = DateTime.UtcNow,
                BarcodeRaw = barcodeRaw,
                Barcode = barcodeRaw,
                Tray = tray,
                Status = tray.HasValue ? EnvelopeStatus.Processed : EnvelopeStatus.NeedsManualEntry,
                ImagePath = imagePath,
                AddressPHash = fp.AddressPHash,
                BarcodePHash = fp.BarcodePHash,
                CenterlineHash = fp.CenterlineHash,
                SkewDegrees = fp.SkewDegrees,
                MachineScanId = machineScanId,
            };
            _db.Envelopes.Add(env);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "Ingest total={Total}ms save={Save}ms hash={Hash}ms path=barcode status={Status}",
                sw.ElapsedMilliseconds, tSaved - tSaveStart, tHashed - tSaved, env.Status);
            return new IngestResult(env.Status, env.Tray, env.Id, SkewDegrees: fp.SkewDegrees);
        }

        // No-barcode path: hash + match in one call.
        var candidates = await LoadCandidatesAsync(machineScanId, ct);
        var tLoaded = sw.ElapsedMilliseconds;
        var match = await _matcher.MatchAsync(buf, candidates, ct);
        var tMatched = sw.ElapsedMilliseconds;

        if (match.Match is not null)
        {
            var matched = match.Match;
            var env = new Envelope
            {
                Id = id,
                ScanTimeUtc = DateTime.UtcNow,
                BarcodeRaw = barcodeRaw,
                Barcode = matched.Barcode,
                Tray = matched.Tray,
                Status = EnvelopeStatus.Processed,
                ImagePath = imagePath,
                AddressPHash = match.Fingerprint.AddressPHash,
                BarcodePHash = match.Fingerprint.BarcodePHash,
                CenterlineHash = match.Fingerprint.CenterlineHash,
                SkewDegrees = match.SkewDegrees,
                MachineScanId = machineScanId,
                MatchedEnvelopeId = matched.EnvelopeId,
                IsSecondPass = true,
            };
            _db.Envelopes.Add(env);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "Ingest total={Total}ms save={Save}ms load={Load}ms match={Match}ms path=2nd-pass-{Source} status=Processed " +
                "addr={A} barcode={B} center={C} score={Score} candidates={N} matched={M}",
                sw.ElapsedMilliseconds,
                tSaved - tSaveStart,
                tLoaded - tSaved,
                tMatched - tLoaded,
                matched.Source, match.MatchedAddressDistance, match.MatchedBarcodeDistance,
                match.MatchedCenterlineDistance, match.Score, match.CandidatesScanned,
                matched.EnvelopeId);
            return new IngestResult(
                EnvelopeStatus.Processed, env.Tray, env.Id,
                matched.EnvelopeId,
                match.MatchedAddressDistance,
                match.MatchedBarcodeDistance,
                match.MatchedCenterlineDistance,
                match.Score,
                match.SkewDegrees);
        }

        // No match: record the fingerprint on a NeedsManualEntry row so
        // a future scan of the same envelope can match it.
        var fpPending = match.Fingerprint;
        var pending = new Envelope
        {
            Id = id,
            ScanTimeUtc = DateTime.UtcNow,
            BarcodeRaw = barcodeRaw,
            Status = EnvelopeStatus.NeedsManualEntry,
            ImagePath = imagePath,
            AddressPHash = fpPending.AddressPHash,
            BarcodePHash = fpPending.BarcodePHash,
            CenterlineHash = fpPending.CenterlineHash,
            SkewDegrees = fpPending.SkewDegrees,
            MachineScanId = machineScanId,
        };
        _db.Envelopes.Add(pending);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            "Ingest total={Total}ms save={Save}ms load={Load}ms match={Match}ms path=no-match status=NeedsManualEntry " +
            "closestAddr={A} candidates={N}",
            sw.ElapsedMilliseconds,
            tSaved - tSaveStart,
            tLoaded - tSaved,
            tMatched - tLoaded,
            match.ClosestAddressDistance,
            match.CandidatesScanned);
        return new IngestResult(
            EnvelopeStatus.NeedsManualEntry, null, pending.Id,
            MatchAddressPHashDistance: match.ClosestAddressDistance < 0 ? null : match.ClosestAddressDistance,
            SkewDegrees: fpPending.SkewDegrees);
    }

    private async Task<int?> ResolveTrayAsync(string barcode, CancellationToken ct)
    {
        var entry = await _db.TrayMap.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Barcode == barcode, ct);
        return entry?.Tray;
    }

    /// <summary>
    /// Build the candidate set the matcher should search. The set is:
    ///   - all envelopes from the recent window with a tray assigned
    ///     (Resolved or Processed, not IsSecondPass).
    ///   - plus, when a machineScanId is supplied, the row with that
    ///     scan id (even if outside the window) is appended so the
    ///     matcher can find the exact-scan match without depending on
    ///     hash stability.
    /// </summary>
    private async Task<IReadOnlyList<EnvelopeCandidate>> LoadCandidatesAsync(
        string? machineScanId, CancellationToken ct)
    {
        var windowHours = _matchSettings.WindowHours;
        var cutoff = DateTime.UtcNow.AddHours(-windowHours);

        var rows = await _db.Envelopes.AsNoTracking()
            .Where(e => (e.Status == EnvelopeStatus.Resolved || e.Status == EnvelopeStatus.Processed)
                     && !e.IsSecondPass
                     && e.Tray != null
                     && e.ScanTimeUtc >= cutoff)
            .Select(e => new
            {
                e.Id,
                e.Barcode,
                e.Tray,
                e.AddressPHash,
                e.BarcodePHash,
                e.CenterlineHash,
                e.SkewDegrees,
                e.Status,
            })
            .ToListAsync(ct);

        var candidates = new List<EnvelopeCandidate>(rows.Count);
        var seen = new HashSet<string>(rows.Count, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            // Manual source = operator entered it (Resolved).
            // Automatic source = barcode resolved it (Processed).
            var source = r.Status == EnvelopeStatus.Resolved
                ? MatchSource.Manual
                : MatchSource.Automatic;
            candidates.Add(new EnvelopeCandidate(
                Id: r.Id,
                Barcode: r.Barcode,
                Tray: r.Tray,
                Fingerprint: new Fingerprint(
                    AddressPHash: r.AddressPHash,
                    BarcodePHash: r.BarcodePHash,
                    CenterlineHash: r.CenterlineHash,
                    SkewDegrees: r.SkewDegrees),
                Source: source));
            seen.Add(r.Id);
        }

        if (!string.IsNullOrWhiteSpace(machineScanId))
        {
            // Append the exact-scan row even if outside the window.
            var byId = await _db.Envelopes.AsNoTracking()
                .Where(e => e.MachineScanId == machineScanId
                         && (e.Status == EnvelopeStatus.Resolved || e.Status == EnvelopeStatus.Processed)
                         && !e.IsSecondPass)
                .Select(e => new
                {
                    e.Id,
                    e.Barcode,
                    e.Tray,
                    e.AddressPHash,
                    e.BarcodePHash,
                    e.CenterlineHash,
                    e.SkewDegrees,
                    e.Status,
                })
                .FirstOrDefaultAsync(ct);
            if (byId is not null && seen.Add(byId.Id))
            {
                var src = byId.Status == EnvelopeStatus.Resolved
                    ? MatchSource.Manual
                    : MatchSource.Automatic;
                candidates.Add(new EnvelopeCandidate(
                    Id: byId.Id,
                    Barcode: byId.Barcode,
                    Tray: byId.Tray,
                    Fingerprint: new Fingerprint(
                        AddressPHash: byId.AddressPHash,
                        BarcodePHash: byId.BarcodePHash,
                        CenterlineHash: byId.CenterlineHash,
                        SkewDegrees: byId.SkewDegrees),
                    Source: src));
            }
        }
        return candidates;
    }
}
