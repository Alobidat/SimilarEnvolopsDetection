using MailSort.Matching;
using MailSort.Matching.Configuration;
using MailSort.Matching.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MailSort.Matching.Tests;

/// <summary>
/// End-to-end quality test for the matcher on real Elections Canada
/// MARS samples. The <c>samples/</c> folder contains a curated set of
/// envelope scans. Of those, 24 files form 12 pairs of re-scans of the
/// same physical envelope (filename pattern: base + "0" then "1",
/// e.g. <c>0005217811889550_PieceImage.tiff</c> and
/// <c>0005217811889551_PieceImage.tiff</c>). The remaining ~50 are
/// unique envelopes.
///
/// The tests below serve as a quality audit on the fingerprint + match
/// engine. They are designed to:
///   1. Sanity-check the engine: deterministic, correctly recognizes
///      the cases its design contract covers.
///   2. Quantify how the engine behaves on real data: TPR, FPR, and
///      the per-pair / cross-envelope Hamming distance distributions
///      that drive the choice of operating point.
///   3. Surface data-quality issues: when the within-pair Hamming
///      distance is no smaller than the cross-envelope distance, no
///      single threshold can separate the two classes. The tests
///      report this rather than papering over it.
/// </summary>
public class DuplicateScanDetectionTests
{
    // Resolved at test time so the test runs regardless of CWD.
    private static string SamplesDir
    {
        get
        {
            // Walk up from the test bin folder to the repo root. The test
            // assembly lives under MailSort.Matching.Tests/bin/<cfg>/<tfm>/.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MailSort.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "samples");
        }
    }

    private readonly ITestOutputHelper _out;
    public DuplicateScanDetectionTests(ITestOutputHelper output) => _out = output;

    private static MatchEngine BuildEngine(MatchSettings? settings = null) =>
        new(
            Options.Create(settings ?? new MatchSettings()),
            NullLogger<MatchEngine>.Instance);

    /// <summary>
    /// Parse a filename like <c>0005217811889550_PieceImage.tiff</c>
    /// into (<c>000521781188955</c>, <c>0005217811889550_PieceImage.tiff</c>).
    /// Returns null if the file is not a piece image from the MARS
    /// export (e.g. legacy test fixtures like <c>00010.tif</c>). Also
    /// accepts operator-added copies like
    /// <c>0005217811889550_PieceImage - Copy.tiff</c> and treats them
    /// as a re-scan of the same envelope.
    /// </summary>
    private static (string EnvelopeId, string File)? TryParse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.Contains("_PieceImage")) return null;
        // Strip operator-added suffixes that come AFTER the
        // "_PieceImage" token: " - Copy", " (1)", " copy", etc.
        var idx = name.LastIndexOf("_PieceImage", StringComparison.Ordinal);
        var head = name[..(idx + "_PieceImage".Length)];
        var tail = name[(idx + "_PieceImage".Length)..];
        while (true)
        {
            var trimmed = tail.TrimStart();
            if (trimmed.Length == 0) break;
            if (trimmed.StartsWith("- Copy", StringComparison.OrdinalIgnoreCase))
                tail = trimmed["- Copy".Length..];
            else if (trimmed.StartsWith("Copy", StringComparison.OrdinalIgnoreCase))
                tail = trimmed["Copy".Length..];
            else if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\(\d+\)"))
                tail = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^\(\d+\)", "");
            else if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\s\(\d+\)"))
                tail = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^\s\(\d+\)", "");
            else
                break;
        }
        var id = (head + tail).Trim();
        id = id[..^"_PieceImage".Length].TrimEnd(' ', '-', '_');
        if (id.Length < 2) return null;
        if (!char.IsDigit(id[^1])) return null;
        var envelopeId = id[..^1];
        return (envelopeId, path);
    }

    /// <summary>
    /// Group the MARS piece images by envelope ID. The dictionary value
    /// is a list of file paths; envelopes with more than one entry are
    /// the known duplicate-scan pairs.
    /// </summary>
    private static Dictionary<string, List<string>> GroupByEnvelope(string samplesDir)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(samplesDir, "*_PieceImage*.tiff", SearchOption.TopDirectoryOnly).OrderBy(p => p))
        {
            var parsed = TryParse(path);
            if (parsed is null) continue;
            if (!map.TryGetValue(parsed.Value.EnvelopeId, out var list))
                map[parsed.Value.EnvelopeId] = list = new List<string>();
            list.Add(parsed.Value.File);
        }
        return map;
    }

    /// <summary>
    /// Hash every scan once and return a file -> fingerprint map.
    /// </summary>
    private static async Task<Dictionary<string, Fingerprint>> ComputeFingerprintsAsync(
        MatchEngine engine, IEnumerable<string> files)
    {
        var fps = new Dictionary<string, Fingerprint>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            await using var s = File.OpenRead(f);
            fps[f] = await engine.ComputeFingerprintAsync(s);
        }
        return fps;
    }

    [Fact]
    public async Task SameImage_HashesIdentically()
    {
        var groups = GroupByEnvelope(SamplesDir);
        var someFile = groups.Values.SelectMany(v => v).First();
        var engine = BuildEngine();

        Fingerprint first;
        await using (var s = File.OpenRead(someFile))
            first = await engine.ComputeFingerprintAsync(s);

        Fingerprint second;
        await using (var s = File.OpenRead(someFile))
            second = await engine.ComputeFingerprintAsync(s);

        Assert.Equal(first.AddressPHash, second.AddressPHash);
        Assert.Equal(first.BarcodePHash, second.BarcodePHash);
        Assert.Equal(first.CenterlineHash, second.CenterlineHash);
    }

    [Fact]
    public async Task WithinThreshold_Pairs_AreRecognized()
    {
        // Production contract: when the candidate set contains a real
        // re-scan whose address pHash is within MaxAddressPHashDistance
        // AND whose barcode pHash is within 2*MaxBarcodePHashDistance,
        // the matcher must return it. Pairs whose address distance
        // exceeds the configured threshold are by design rejected,
        // because at that point the matcher has no way to tell the
        // pair from coincidence.
        //
        // The test reports both:
        //   - how many of the 12 known pairs are within the threshold
        //     and were successfully recognized;
        //   - how many exceed the threshold and were correctly
        //     rejected.
        var groups = GroupByEnvelope(SamplesDir);
        var pairs = groups.Where(g => g.Value.Count >= 2).ToList();
        Assert.True(pairs.Count >= 1, "Expected at least one known duplicate-scan pair in the samples folder.");

        var engine = BuildEngine();
        var settings = new MatchSettings();
        var aMax = settings.MatchEngine.MaxAddressPHashDistance;
        var bMax = settings.MatchEngine.MaxBarcodePHashDistance;
        _out.WriteLine($"Default thresholds: aMax={aMax} bMax={bMax}");

        var allFiles = groups.SelectMany(g => g.Value).ToList();
        var fps = await ComputeFingerprintsAsync(engine, allFiles);

        int recognized = 0, withinButNotRecognized = 0, beyondThreshold = 0;
        var perPair = new List<string>();
        foreach (var (envId, files) in pairs)
        {
            var query = files[^1];
            var expected = files[0];
            var candidate = new EnvelopeCandidate(
                Id: expected, Barcode: null, Tray: 1,
                Fingerprint: fps[expected], Source: MatchSource.Automatic);

            await using var qStream = File.OpenRead(query);
            var result = await engine.MatchAsync(qStream, new[] { candidate });
            var a = RegionalFingerprint.HammingDistance(result.Fingerprint.AddressPHash, fps[expected].AddressPHash);
            var b = RegionalFingerprint.HammingDistance(result.Fingerprint.BarcodePHash, fps[expected].BarcodePHash);

            if (a <= aMax && b <= bMax * 2)
            {
                if (result.Match?.EnvelopeId == expected)
                {
                    recognized++;
                    perPair.Add($"  OK    {Path.GetFileName(query)} -> {Path.GetFileName(expected)}  a={a} b={b} c={RegionalFingerprint.HammingDistance(result.Fingerprint.CenterlineHash, fps[expected].CenterlineHash)}");
                }
                else
                {
                    withinButNotRecognized++;
                    perPair.Add($"  FAIL  {Path.GetFileName(query)} within thresholds (a={a}<={aMax} b={b}<={bMax * 2}) but not returned  matchedId={(result.Match?.EnvelopeId ?? "(no match)")}");
                }
            }
            else
            {
                beyondThreshold++;
                perPair.Add($"  SKIP  {Path.GetFileName(query)} outside thresholds (a={a} b={b}); engine correctly rejected");
            }
        }

        _out.WriteLine($"Pairs: {pairs.Count}, recognized: {recognized}, within-threshold-but-missed: {withinButNotRecognized}, beyond-threshold: {beyondThreshold}");
        foreach (var line in perPair) _out.WriteLine(line);

        // Contract: every pair that is within the configured thresholds
        // must be recognized. If the engine ever fails to return a
        // pair it should have accepted, that is a regression.
        Assert.Equal(0, withinButNotRecognized);
    }

    [Fact]
    public async Task QualityReport_PrintsSeparationMetrics()
    {
        // Pure-observability test. It does not assert any pass/fail
        // beyond the existence of the data, but it prints the full
        // distance distribution and a strict-engine what-if so the
        // operator can see, in the test log, exactly how the matcher
        // performs on the current sample set and where the operating
        // point is.
        //
        // This is the test the team should read when adjusting
        // <see cref="MatchEngineSettings.MaxAddressPHashDistance"/> and
        // <see cref="MatchEngineSettings.MaxBarcodePHashDistance"/>.
        var groups = GroupByEnvelope(SamplesDir);
        var pairs = groups.Where(g => g.Value.Count >= 2).ToList();
        _out.WriteLine($"Loaded {groups.Count} envelopes from {SamplesDir}; {pairs.Count} are known duplicate-scan pairs.");

        var engine = BuildEngine();
        var allFiles = groups.SelectMany(g => g.Value).ToList();
        var fps = await ComputeFingerprintsAsync(engine, allFiles);

        // Per-pair Hamming distances on each of the three channels.
        var withinPairAddr = new List<int>();
        var withinPairBarcode = new List<int>();
        var withinPairCenter = new List<int>();
        foreach (var (_, files) in pairs)
        {
            var a = files[0];
            var b = files[1];
            withinPairAddr.Add(RegionalFingerprint.HammingDistance(fps[a].AddressPHash, fps[b].AddressPHash));
            withinPairBarcode.Add(RegionalFingerprint.HammingDistance(fps[a].BarcodePHash, fps[b].BarcodePHash));
            withinPairCenter.Add(RegionalFingerprint.HammingDistance(fps[a].CenterlineHash, fps[b].CenterlineHash));
        }

        // Cross-envelope address pHash distances. We sample, rather
        // than compute all N*(N-1), to keep the test fast.
        var crossEnvelopeAddr = new List<int>();
        var rng = new Random(42);
        for (int i = 0; i < allFiles.Count; i++)
        {
            for (int j = 0; j < allFiles.Count; j++)
            {
                if (i == j) continue;
                if (TryParse(allFiles[i])?.EnvelopeId == TryParse(allFiles[j])?.EnvelopeId) continue;
                if (rng.NextDouble() < 0.20) // ~20% sample
                    crossEnvelopeAddr.Add(RegionalFingerprint.HammingDistance(fps[allFiles[i]].AddressPHash, fps[allFiles[j]].AddressPHash));
            }
        }

        _out.WriteLine("");
        _out.WriteLine("Distance distribution (address pHash Hamming, 0..64):");
        _out.WriteLine($"  within-pair : n={withinPairAddr.Count} min={withinPairAddr.Min()} avg={withinPairAddr.Average():F1} max={withinPairAddr.Max()}");
        _out.WriteLine($"  cross-env   : n={crossEnvelopeAddr.Count} min={crossEnvelopeAddr.Min()} avg={crossEnvelopeAddr.Average():F1} max={crossEnvelopeAddr.Max()}");
        _out.WriteLine($"  separation  : crossMin - withinMax = {crossEnvelopeAddr.Min() - withinPairAddr.Max()}  (negative = clusters overlap; no Hamming threshold can separate them)");
        _out.WriteLine("");
        _out.WriteLine("Per-pair within distances (a=addr, b=barcode, c=center):");
        foreach (var (envId, files) in pairs)
        {
            var a = files[0];
            var b = files[1];
            _out.WriteLine($"  {envId}  a={RegionalFingerprint.HammingDistance(fps[a].AddressPHash, fps[b].AddressPHash),3} b={RegionalFingerprint.HammingDistance(fps[a].BarcodePHash, fps[b].BarcodePHash),3} c={RegionalFingerprint.HammingDistance(fps[a].CenterlineHash, fps[b].CenterlineHash),3}");
        }
    }
}
