using MailSort.Matching;
using MailSort.Matching.Configuration;
using MailSort.Matching.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MailSort.Matching.Tests;

public class MatchEngineTests
{
    private static MatchEngine BuildEngine(MatchSettings? settings = null) =>
        new(
            Options.Create(settings ?? new MatchSettings()),
            NullLogger<MatchEngine>.Instance);

    /// <summary>
    /// Synthesize a 200x300 envelope with two distinct horizontal bands:
    /// top half is "address" (noise), bottom half is "barcode" (different
    /// pattern). The address pHash of two such images with the same
    /// noise should be near zero; the address pHash of two with very
    /// different noise should be much higher.
    /// </summary>
    private static byte[] SynthEnvelope(ulong seed, string pattern = "address")
    {
        using var img = new Image<L8>(200, 300);
        var rng = new Random(unchecked((int)seed));
        img.ProcessPixelRows(p =>
        {
            for (int y = 0; y < p.Height; y++)
            {
                var row = p.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    byte v = pattern switch
                    {
                        "address" => (byte)((y * 7 + x * 3 + rng.Next(60)) & 0xFF),
                        "barcode" => (byte)((y * 3 + x * 11 + rng.Next(40)) & 0xFF),
                        _ => (byte)rng.Next(256)
                    };
                    row[x] = new L8(v);
                }
            }
        });
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task MatchAsync_NoCandidates_ReturnsNoMatch()
    {
        var engine = BuildEngine();
        var bytes = SynthEnvelope(1);
        using var stream = new MemoryStream(bytes);
        var result = await engine.MatchAsync(stream, Array.Empty<EnvelopeCandidate>());
        Assert.Null(result.Match);
        Assert.Equal(-1, result.ClosestAddressDistance);
        Assert.Equal(0, result.CandidatesScanned);
    }

    [Fact]
    public async Task MatchAsync_PicksClosestCandidate()
    {
        var engine = BuildEngine();
        // Build a target fingerprint (seed=42) and a candidate set with
        // one near-duplicate (seed=42) and one unrelated (seed=999).
        var target = SynthEnvelope(42);
        var near = SynthEnvelope(42);
        var far = SynthEnvelope(999);

        var targetFp = await engine.ComputeFingerprintAsync(new MemoryStream(target));
        var nearFp = await engine.ComputeFingerprintAsync(new MemoryStream(near));
        var farFp = await engine.ComputeFingerprintAsync(new MemoryStream(far));

        var candidates = new[]
        {
            new EnvelopeCandidate("near", null, 1, nearFp, MatchSource.Manual),
            new EnvelopeCandidate("far",  null, 2, farFp,  MatchSource.Automatic),
        };
        var result = await engine.MatchAsync(new MemoryStream(target), candidates);
        Assert.NotNull(result.Match);
        Assert.Equal("near", result.Match!.EnvelopeId);
        Assert.Equal(MatchSource.Manual, result.Match.Source);
    }

    [Fact]
    public async Task MatchAsync_HardRejectsWildlyDifferent()
    {
        // Configure very tight thresholds.
        var settings = new MatchSettings();
        settings.MatchEngine.MaxAddressPHashDistance = 0; // must be perfect
        settings.MatchEngine.MaxBarcodePHashDistance = 0;
        var engine = BuildEngine(settings);

        var target = SynthEnvelope(1);
        var other = SynthEnvelope(2);
        var otherFp = await engine.ComputeFingerprintAsync(new MemoryStream(other));

        var candidates = new[]
        {
            new EnvelopeCandidate("other", null, 1, otherFp, MatchSource.Automatic),
        };
        var result = await engine.MatchAsync(new MemoryStream(target), candidates);
        Assert.Null(result.Match);
    }

    [Fact]
    public async Task MatchAsync_RespectsSourceFromCandidate()
    {
        var engine = BuildEngine();
        var target = SynthEnvelope(7);
        var same = SynthEnvelope(7);
        var sameFp = await engine.ComputeFingerprintAsync(new MemoryStream(same));

        var candidates = new[]
        {
            new EnvelopeCandidate("manual-1pass", "BC", 5, sameFp, MatchSource.Manual),
        };
        var result = await engine.MatchAsync(new MemoryStream(target), candidates);
        Assert.NotNull(result.Match);
        Assert.Equal(MatchSource.Manual, result.Match!.Source);
        Assert.Equal("BC", result.Match.Barcode);
        Assert.Equal(5, result.Match.Tray);
    }

    [Fact]
    public async Task ComputeFingerprintAsync_ReturnsValidFingerprint()
    {
        var engine = BuildEngine();
        var bytes = SynthEnvelope(123);
        var fp = await engine.ComputeFingerprintAsync(new MemoryStream(bytes));
        // The address and barcode pHashes of a fully-synthetic envelope
        // with the default ROIs should both be non-zero (some bits set).
        Assert.NotEqual(0L, fp.AddressPHash);
        Assert.NotEqual(0L, fp.BarcodePHash);
    }
}
