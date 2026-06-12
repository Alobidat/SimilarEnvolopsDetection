using MailSort.Matching;
using MailSort.Matching.Configuration;
using MailSort.Matching.Engine;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;

namespace MailSort.Api;

/// <summary>
/// Runs the duplicate-scan analysis on a folder of envelope images.
/// This is the same analysis the
/// <c>MailSort.Matching.Tests.DuplicateScanDetectionTests</c> unit
/// tests perform, but exposed as a service so the Blazor UI can run
/// it interactively against any folder.
/// </summary>
public sealed class DuplicateScanAnalyzer
{
    private readonly IMatchEngine _engine;
    private readonly MatchSettings _settings;

    public DuplicateScanAnalyzer(IMatchEngine engine, IOptions<MatchSettings> settings)
    {
        _engine = engine;
        _settings = settings.Value;
    }

    /// <summary>
    /// Analyze a folder of MARS piece images. The folder is read
    /// server-side; the caller (UI) supplies the absolute path.
    /// </summary>
    /// <param name="folder">Absolute path to the folder to analyze.</param>
    /// <param name="imageBaseUrl">
    /// Base URL the report should use when emitting image links, e.g.
    /// <c>http://localhost:5199/api/dup-test/image</c>. The analyzer
    /// appends <c>?path=&lt;filename&gt;</c> to it.
    /// </param>
    /// <param name="useDefaultThresholds">
    /// When true, the engine runs with the configured (default)
    /// thresholds. When false, it runs with the tightest thresholds
    /// the data actually supports.
    /// </param>
    public async Task<DuplicateScanReport> AnalyzeAsync(
        string folder,
        string imageBaseUrl,
        bool useDefaultThresholds,
        CancellationToken ct = default,
        string? folder2 = null)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder does not exist: {folder}");
        if (folder2 is not null && !Directory.Exists(folder2))
            throw new DirectoryNotFoundException($"Second-scan folder does not exist: {folder2}");

        var groups = GroupByEnvelope(folder, folder2);
        var allFiles = groups.SelectMany(g => g.Value).ToList();

        // Hash every scan once.
        var fps = new Dictionary<string, Fingerprint>(StringComparer.Ordinal);
        var fpMs = new Dictionary<string, long>(StringComparer.Ordinal);
        var metas = new Dictionary<string, ImageMeta>(StringComparer.Ordinal);
        foreach (var f in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            await using var s = File.OpenRead(f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            fps[f] = await _engine.ComputeFingerprintAsync(s, ct);
            fpMs[f] = sw.ElapsedMilliseconds;
            metas[f] = ReadMeta(f);
        }

        // Build a candidate set per query, excluding the query itself,
        // exactly as the production caller would.
        var candidateFor = (string queryFile) => allFiles
            .Where(f => f != queryFile)
            .Select(f => new EnvelopeCandidate(
                Id: f, Barcode: null, Tray: 1,
                Fingerprint: fps[f], Source: MatchSource.Automatic))
            .ToList();

        // Pick the engine. The strict engine uses the largest
        // within-pair address distance as its threshold, so every pair
        // is accepted by the address check (the closest cross-envelope
        // candidate is then selected by the combined score).
        var engineSettings = useDefaultThresholds
            ? new MatchSettings()
            : BuildStrictSettings(groups, fps);

        // Build a separate engine with the strict settings when needed.
        var engine = useDefaultThresholds
            ? _engine
            : new MatchEngine(
                Microsoft.Extensions.Options.Options.Create(engineSettings),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchEngine>.Instance);

        var rows = new List<DuplicateScanRow>();
        int recognizedPairs = 0;
        int pairCount = 0;

        // Sort groups: duplicate groups first (most informative), then
        // unique envelopes by ID. This gives the operator a single
        // contiguous block of "the interesting rows" at the top.
        foreach (var (envId, files) in groups
                     .OrderByDescending(g => g.Value.Count >= 2)
                     .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var isPair = files.Count >= 2;
            if (isPair) pairCount++;

            var scans = files
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f =>
                {
                    var fDir = Uri.EscapeDataString(Path.GetDirectoryName(f) ?? folder);
                    return new DuplicateScanViewModel(
                        FileName: Path.GetFileName(f),
                        ImageUrl: $"{imageBaseUrl}?path={Uri.EscapeDataString(f)}&folder={fDir}",
                        AddressRoiUrl: $"{imageBaseUrl}?path={Uri.EscapeDataString(f)}&roi=address&folder={fDir}",
                        BarcodeRoiUrl: $"{imageBaseUrl}?path={Uri.EscapeDataString(f)}&roi=barcode&folder={fDir}",
                        SizeBytes: metas[f].Size,
                        WidthPx: metas[f].Width,
                        HeightPx: metas[f].Height,
                        AddressPHashHex: $"0x{fps[f].AddressPHash:X16}",
                        BarcodePHashHex: $"0x{fps[f].BarcodePHash:X16}",
                        CenterlineHashHex: $"0x{fps[f].CenterlineHash:X16}",
                        SkewDegrees: Math.Round(fps[f].SkewDegrees, 2),
                        FingerprintMs: fpMs[f]);
                })
                .ToList();

            int? addrD = null, barD = null, cenD = null;
            bool? recognized = null;
            string? verdict = null;
            string? closestNonPair = null;
            int? closestAddr = null;

            if (isPair)
            {
                var a = files[0];
                var b = files[1];
                addrD = RegionalFingerprint.HammingDistance(fps[a].AddressPHash, fps[b].AddressPHash);
                barD = RegionalFingerprint.HammingDistance(fps[a].BarcodePHash, fps[b].BarcodePHash);
                cenD = RegionalFingerprint.HammingDistance(fps[a].CenterlineHash, fps[b].CenterlineHash);

                // Query with the last scan (MARS convention: "_1" is
                // the re-scan) and ask the engine to find the first
                // scan in the candidate set.
                var query = files[^1];
                var expected = files[0];
                await using var qStream = File.OpenRead(query);
                var result = await engine.MatchAsync(qStream, candidateFor(query), ct);
                if (result.Match?.EnvelopeId == expected)
                {
                    recognized = true;
                    recognizedPairs++;
                    verdict = $"matched (a={result.MatchedAddressDistance} b={result.MatchedBarcodeDistance} c={result.MatchedCenterlineDistance} score={result.Score:F1})";
                }
                else
                {
                    recognized = false;
                    var actual = result.Match?.EnvelopeId;
                    if (actual is null)
                    {
                        var within = addrD <= engineSettings.MatchEngine.MaxAddressPHashDistance
                                     && barD <= engineSettings.MatchEngine.MaxBarcodePHashDistance * 2;
                        verdict = within
                            ? $"no-match (within aMax/bMax but engine rejected; closestAddr={result.ClosestAddressDistance})"
                            : $"no-match (outside aMax={engineSettings.MatchEngine.MaxAddressPHashDistance} bMax={engineSettings.MatchEngine.MaxBarcodePHashDistance}; closestAddr={result.ClosestAddressDistance})";
                    }
                    else
                    {
                        verdict = $"wrong match -> {Path.GetFileName(actual)} (a={result.MatchedAddressDistance} b={result.MatchedBarcodeDistance})";
                    }
                }

                // For the operator, also surface the closest NON-pair
                // candidate so they can see how close the engine came
                // to confusing the two.
                int closest = int.MaxValue;
                string? closestFile = null;
                foreach (var other in allFiles)
                {
                    if (other == a || other == b) continue;
                    var d = RegionalFingerprint.HammingDistance(fps[query].AddressPHash, fps[other].AddressPHash);
                    if (d < closest) { closest = d; closestFile = Path.GetFileName(other); }
                }
                closestNonPair = closestFile;
                closestAddr = closest == int.MaxValue ? null : closest;
            }
            else
            {
                verdict = "singleton (no re-scan in folder)";
            }

            rows.Add(new DuplicateScanRow(
                EnvelopeId: envId,
                IsDuplicateGroup: isPair,
                Scans: scans,
                AddressHammingBetweenFirstTwo: addrD,
                BarcodeHammingBetweenFirstTwo: barD,
                CenterlineHammingBetweenFirstTwo: cenD,
                EngineRecognizedPair: recognized,
                EngineVerdict: verdict,
                ClosestNonPairFileName: closestNonPair,
                ClosestNonPairAddressHamming: closestAddr));
        }

        // Compute the disposition summary now that we know which
        // pairs were OK / missed (for what reason) / outside threshold.
        var okRows = rows.Where(r => r.EngineRecognizedPair == true).ToList();
        var withinButMissed = rows.Count(r =>
            r.IsDuplicateGroup &&
            r.EngineRecognizedPair == false &&
            r.AddressHammingBetweenFirstTwo is int a &&
            a <= engineSettings.MatchEngine.MaxAddressPHashDistance);
        var outsideThreshold = rows.Count(r =>
            r.IsDuplicateGroup &&
            r.EngineRecognizedPair == false &&
            r.AddressHammingBetweenFirstTwo is int a &&
            a > engineSettings.MatchEngine.MaxAddressPHashDistance);
        var singleton = rows.Count(r => !r.IsDuplicateGroup);
        var overlap = rows.Count(r =>
            r.IsDuplicateGroup &&
            r.ClosestNonPairAddressHamming is int c &&
            r.AddressHammingBetweenFirstTwo is int ap &&
            c <= ap);

        var summary = new DispositionSummary(
            RecognizedPairCount: okRows.Count,
            WithinThresholdButMissedCount: withinButMissed,
            OutsideThresholdCount: outsideThreshold,
            SingletonCount: singleton,
            ClosestNonPairOverlapCount: overlap,
            AvgAddressHammingRecognized: okRows.Count > 0
                ? okRows.Average(r => (double)r.AddressHammingBetweenFirstTwo!)
                : null,
            AvgBarcodeHammingRecognized: okRows.Count > 0
                ? okRows.Average(r => (double)r.BarcodeHammingBetweenFirstTwo!)
                : null,
            MaxAddressHammingRecognized: okRows.Count > 0
                ? (double)okRows.Max(r => r.AddressHammingBetweenFirstTwo!)
                : null,
            MinClosestNonPairAddressHamming: rows
                .Where(r => r.ClosestNonPairAddressHamming is int)
                .Select(r => (double)r.ClosestNonPairAddressHamming!)
                .DefaultIfEmpty(double.MaxValue)
                .Min());

        return new DuplicateScanReport(
            Folder: folder,
            Folder2: folder2,
            EnvelopeCount: groups.Count,
            ScanCount: allFiles.Count,
            PairCount: pairCount,
            RecognizedPairCount: recognizedPairs,
            MaxAddressPHashDistance: engineSettings.MatchEngine.MaxAddressPHashDistance,
            MaxBarcodePHashDistance: engineSettings.MatchEngine.MaxBarcodePHashDistance,
            DefaultThresholds: useDefaultThresholds,
            AddressRoi: new RoiSettings(
                engineSettings.MatchEngine.AddressRoi.X,
                engineSettings.MatchEngine.AddressRoi.Y,
                engineSettings.MatchEngine.AddressRoi.Width,
                engineSettings.MatchEngine.AddressRoi.Height),
            BarcodeRoi: new RoiSettings(
                engineSettings.MatchEngine.BarcodeRoi.X,
                engineSettings.MatchEngine.BarcodeRoi.Y,
                engineSettings.MatchEngine.BarcodeRoi.Width,
                engineSettings.MatchEngine.BarcodeRoi.Height),
            Summary: summary,
            Rows: rows);
    }

    private static MatchSettings BuildStrictSettings(
        Dictionary<string, List<string>> groups,
        Dictionary<string, Fingerprint> fps)
    {
        var withinAddr = new List<int>();
        var withinBar = new List<int>();
        foreach (var (_, files) in groups)
        {
            if (files.Count < 2) continue;
            withinAddr.Add(RegionalFingerprint.HammingDistance(fps[files[0]].AddressPHash, fps[files[1]].AddressPHash));
            withinBar.Add(RegionalFingerprint.HammingDistance(fps[files[0]].BarcodePHash, fps[files[1]].BarcodePHash));
        }
        var s = new MatchSettings();
        s.MatchEngine.MaxAddressPHashDistance = withinAddr.Count > 0 ? withinAddr.Max() : 16;
        s.MatchEngine.MaxBarcodePHashDistance = withinBar.Count > 0 ? Math.Max(0, (withinBar.Max() + 1) / 2) : 18;
        return s;
    }

    private static Dictionary<string, List<string>> GroupByEnvelope(string folder, string? folder2 = null)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (folder2 is null)
        {
            // Single-folder mode: original behaviour — collect all matching files
            // and let the suffix-stripping group copies together.
            foreach (var path in Directory.EnumerateFiles(folder, "*_PieceImage*.tiff", SearchOption.TopDirectoryOnly).OrderBy(p => p))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!name.Contains("_PieceImage")) continue;
                name = NormalizeScanName(name);
                var id = name[..^"_PieceImage".Length].TrimEnd(' ', '-', '_');
                if (id.Length < 2 || !char.IsDigit(id[^1])) continue;
                var envelopeId = id[..^1];
                if (!map.TryGetValue(envelopeId, out var list))
                    map[envelopeId] = list = new List<string>();
                list.Add(path);
            }
        }
        else
        {
            // Two-folder mode: pick exactly ONE file per folder per envelope.
            // The RescanSimulator writes both a bare copy of the original AND
            // an augmented "- Rescan" file into folder2.  Without deduplication
            // each envelope would get 3 files (original in folder1, copy in
            // folder2, rescan in folder2), which breaks the pair logic.
            // Within a folder we prefer files that carry a recognised suffix
            // (e.g. "- Rescan", "- Copy") over bare originals so the augmented
            // image is used rather than the unchanged copy.
            var pick1 = PickOnePerEnvelope(folder);   // originals
            var pick2 = PickOnePerEnvelope(folder2);  // rescans (or augmented copies)

            foreach (var envId in pick1.Keys.Union(pick2.Keys))
            {
                var list = new List<string>();
                if (pick1.TryGetValue(envId, out var f1)) list.Add(f1);
                if (pick2.TryGetValue(envId, out var f2)) list.Add(f2);
                map[envId] = list;
            }
        }

        return map;
    }

    /// <summary>
    /// For a single folder, returns a dictionary of fullBarcode → best path.
    /// Uses the full barcode (everything before "_PieceImage") as the key so
    /// that each physical envelope keeps its own slot — no last-digit stripping.
    /// "Best" = suffixed file (e.g. "- Rescan") wins over a bare original
    /// when both normalise to the same key.
    /// </summary>
    private static Dictionary<string, string> PickOnePerEnvelope(string folder)
    {
        var best = new Dictionary<string, (string path, bool hasSuffix)>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(folder, "*_PieceImage*.tiff", SearchOption.TopDirectoryOnly).OrderBy(p => p))
        {
            var raw = Path.GetFileNameWithoutExtension(path);
            if (!raw.Contains("_PieceImage")) continue;
            var normalized = NormalizeScanName(raw);
            bool hasSuffix = !string.Equals(raw, normalized, StringComparison.Ordinal);

            // Use the full barcode as the key (everything before "_PieceImage").
            // Do NOT strip the last digit here — that trick is only for single-folder
            // mode where MARS names the two scans of the same envelope with a trailing
            // 0/1 digit.  In two-folder mode both folders use identical barcodes so the
            // full string is the correct pairing key.
            var pieceIdx = normalized.LastIndexOf("_PieceImage", StringComparison.Ordinal);
            if (pieceIdx < 0) continue;
            var envelopeId = normalized[..pieceIdx]; // e.g. "0005217811889560"
            if (envelopeId.Length < 2) continue;

            // Replace an existing entry only if the new file is suffixed and
            // the existing one is not (bare copy loses to augmented scan).
            if (!best.TryGetValue(envelopeId, out var existing) || (!existing.hasSuffix && hasSuffix))
                best[envelopeId] = (path, hasSuffix);
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.path, StringComparer.Ordinal);
    }

    /// <summary>
    /// Strip the operator-added suffixes Windows Explorer adds when
    /// you Copy a file: " - Copy", " - Copy (2)", " copy", " (1)".
    /// The base name must still contain "_PieceImage" for the file
    /// to be considered a MARS scan. Suffixes appear <i>after</i>
    /// "_PieceImage" (e.g. "0005217811889560_PieceImage - Copy"),
    /// so we strip from the tail end of the whole name, not from
    /// the prefix.
    /// </summary>
    private static string NormalizeScanName(string name)
    {
        // Only strip suffixes after the LAST "_PieceImage" token, so
        // a name like "0005217811889560_PieceImage" stays untouched.
        var idx = name.LastIndexOf("_PieceImage", StringComparison.Ordinal);
        if (idx < 0) return name;
        var head = name[..(idx + "_PieceImage".Length)];
        var tail = name[(idx + "_PieceImage".Length)..];
        while (true)
        {
            var trimmed = tail.TrimStart();
            // Reject if the head grew, i.e. we accidentally stripped
            // the entire tail (e.g. "_PieceImage - Copy" with no real
            // suffix left). In that case bail out.
            if (trimmed.Length == 0) break;
            // We only strip known Explorer-added suffixes, not digits
            // or letters that could be part of the original name.
            if (trimmed.StartsWith("- Copy", StringComparison.OrdinalIgnoreCase))
                tail = trimmed[("- Copy").Length..];
            else if (trimmed.StartsWith("- Rescan", StringComparison.OrdinalIgnoreCase))
                tail = trimmed[("- Rescan").Length..];
            else if (trimmed.StartsWith("Copy", StringComparison.OrdinalIgnoreCase))
                tail = trimmed["Copy".Length..];
            else if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\(\d+\)"))
                tail = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^\(\d+\)", "");
            else if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\s\(\d+\)"))
                tail = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^\s\(\d+\)", "");
            else
                break;
        }
        return (head + tail).Trim();
    }

    private static ImageMeta ReadMeta(string path)
    {
        var info = new FileInfo(path);
        int? w = null, h = null;
        try
        {
            // We do not need the full decode; just the header. Using
            // IdentifyAsync keeps this fast even on 1-bpp TIFFs.
            var imgInfo = Image.Identify(path);
            w = imgInfo.Width;
            h = imgInfo.Height;
        }
        catch
        {
            // Leave nulls: the UI will show "n/a" rather than crashing.
        }
        return new ImageMeta(info.Length, w, h);
    }

    private readonly record struct ImageMeta(long Size, int? Width, int? Height);
}
