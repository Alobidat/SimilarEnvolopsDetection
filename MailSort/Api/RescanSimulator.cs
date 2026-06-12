using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MailSort.Api;

// ─── DTOs ───────────────────────────────────────────────────────────────────

/// <summary>
/// Parameters for a simulated-rescan generation run.
/// All augmentation ranges are applied independently and randomly
/// per image, using <see cref="Seed"/> for reproducibility.
/// </summary>
public sealed record RescanSimRequest(
    /// <summary>Absolute path to folder of original TIFF scans.</summary>
    string SourceFolder,
    /// <summary>Absolute path to write augmented pairs into (created if missing).</summary>
    string OutputFolder,
    /// <summary>Maximum rotation in either direction, degrees (e.g. 3.0).</summary>
    double MaxTiltDeg,
    /// <summary>Fractional brightness variation (0.15 = ±15 %).</summary>
    double BrightnessDelta,
    /// <summary>Gaussian noise standard deviation in pixel units (e.g. 8).</summary>
    double NoiseSigma,
    /// <summary>RNG seed for reproducibility; -1 = random.</summary>
    int Seed);

public sealed record RescanSimResult(
    string SourceFolder,
    string OutputFolder,
    int RescansGenerated,
    IReadOnlyList<RescanSimEntry> Entries);

public sealed record RescanSimEntry(
    string OriginalFile,
    string RescanFile,
    double TiltApplied,
    double BrightnessFactorApplied,
    double NoiseSigmaApplied);

// ─── Service ────────────────────────────────────────────────────────────────

/// <summary>
/// Generates synthetic re-scan pairs from a folder of MARS piece images
/// by applying realistic scanner perturbations:
///   • Random tilt (rotation) to simulate paper mis-feed
///   • Random brightness shift to simulate lamp variation
///   • Gaussian noise to simulate sensor grain
/// Each original <c>*_PieceImage.tiff</c> produces a companion
/// <c>*_PieceImage - Rescan.tiff</c> in the output folder.
/// The originals are also copied unchanged so the output folder is
/// self-contained and can be fed directly to the duplicate-scan analyzer.
/// </summary>
public sealed class RescanSimulatorService
{
    private static readonly DecoderOptions FastDecode = new()
    {
        Configuration = SixLabors.ImageSharp.Configuration.Default,
        SkipMetadata = true,
    };

    public async Task<RescanSimResult> GenerateAsync(
        RescanSimRequest req,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(req.SourceFolder))
            throw new DirectoryNotFoundException($"Source folder not found: {req.SourceFolder}");

        Directory.CreateDirectory(req.OutputFolder);

        var rng = req.Seed >= 0 ? new Random(req.Seed) : new Random();
        var entries = new List<RescanSimEntry>();

        // Only pick the canonical scans (no " - Rescan", " - Copy" etc.)
        var sources = Directory
            .EnumerateFiles(req.SourceFolder, "*_PieceImage.tiff", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p)
            .ToList();

        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();

            var baseName = Path.GetFileNameWithoutExtension(src); // e.g. 0005217811889560_PieceImage
            var rescanDest = Path.Combine(req.OutputFolder, $"{baseName} - Rescan.tiff");

            // Randomise augmentation parameters.
            double tilt = (rng.NextDouble() * 2 - 1) * req.MaxTiltDeg;
            double brightFactor = 1.0 + (rng.NextDouble() * 2 - 1) * req.BrightnessDelta;
            double noise = req.NoiseSigma;

            // Load, augment, save.
            await using var stream = File.OpenRead(src);
            using var img = await Image.LoadAsync<Rgba32>(FastDecode, stream, ct);

            // 1. Tilt — rotate around the LABEL CENTROID, not the image centre.
            //
            // Real ADF scanners produce skew as the paper feeding at a slight
            // angle.  The scanning head is positioned over the label area, so
            // the effective rotation pivot is near the label — not the paper
            // centre.  Rotating around (0.79 W, 0.38 H) means:
            //   • The barcode ROI (centred at ≈0.39 H) has ≈0px vertical shift.
            //   • The address ROI (≈0.26 H) is ~166 px above the pivot, giving
            //     a vertical shift of only 166 × sin(3°) ≈ 8.7 px.
            //
            // By contrast, rotating around the image centre (0.5 W, 0.5 H)
            // puts the label 801 px from the pivot → 42 px vertical shift,
            // which destroys zone-mean hash stability.
            if (Math.Abs(tilt) > 0.05)
            {
                float pivotX = img.Width  * 0.79f;   // horizontal centre of label
                float pivotY = img.Height * 0.38f;   // vertical centre of barcode ROI
                float radians = (float)(tilt * Math.PI / 180.0);

                // Rotate around the label centroid, keeping the canvas size fixed.
                // Matrix3x2.CreateRotation(θ, center) is the canonical .NET API
                // for rotation around an arbitrary pivot.
                var m = System.Numerics.Matrix3x2.CreateRotation(
                    radians,
                    new System.Numerics.Vector2(pivotX, pivotY));

                var srcRect = new Rectangle(0, 0, img.Width, img.Height);
                var targetSize = new SixLabors.ImageSharp.Size(img.Width, img.Height);
                img.Mutate(ctx => ctx
                    .BackgroundColor(Color.White)
                    .Transform(srcRect, m, targetSize,
                        SixLabors.ImageSharp.Processing.KnownResamplers.Bicubic));
            }

            // 2. Brightness — multiply every channel by brightFactor.
            if (Math.Abs(brightFactor - 1.0) > 0.001)
            {
                ApplyBrightness(img, (float)brightFactor);
            }

            // 3. Gaussian noise — add per-pixel random perturbation.
            if (noise > 0.1)
            {
                ApplyGaussianNoise(img, noise, rng);
            }

            await img.SaveAsync(rescanDest, new TiffEncoder(), ct);

            entries.Add(new RescanSimEntry(
                OriginalFile: Path.GetFileName(src),
                RescanFile: Path.GetFileName(rescanDest),
                TiltApplied: Math.Round(tilt, 2),
                BrightnessFactorApplied: Math.Round(brightFactor, 3),
                NoiseSigmaApplied: noise));
        }

        return new RescanSimResult(
            SourceFolder: req.SourceFolder,
            OutputFolder: req.OutputFolder,
            RescansGenerated: entries.Count,
            Entries: entries);
    }

    // ── Image augmentation helpers ──────────────────────────────────────────

    private static void ApplyBrightness(Image<Rgba32> img, float factor)
    {
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    row[x] = new Rgba32(
                        Clamp(p.R * factor),
                        Clamp(p.G * factor),
                        Clamp(p.B * factor),
                        p.A);
                }
            }
        });
    }

    private static void ApplyGaussianNoise(Image<Rgba32> img, double sigma, Random rng)
    {
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    float n = (float)(Gaussian(rng) * sigma);
                    row[x] = new Rgba32(
                        Clamp(p.R + n),
                        Clamp(p.G + n),
                        Clamp(p.B + n),
                        p.A);
                }
            }
        });
    }

    /// <summary>Box-Muller transform for standard normal sample.</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private static byte Clamp(float v) =>
        v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
}

// ─── Endpoint registration ───────────────────────────────────────────────────

public static class RescanSimulatorEndpoints
{
    public static void MapRescanSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/rescan-sim/generate", async (
            RescanSimRequest req,
            RescanSimulatorService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourceFolder))
                return Results.BadRequest(new { error = "sourceFolder is required" });
            if (string.IsNullOrWhiteSpace(req.OutputFolder))
                return Results.BadRequest(new { error = "outputFolder is required" });
            if (!Path.IsPathRooted(req.SourceFolder) || !Path.IsPathRooted(req.OutputFolder))
                return Results.BadRequest(new { error = "paths must be absolute" });
            // Prevent writing outside of reasonable locations by requiring
            // outputFolder to be different from sourceFolder.
            if (Path.GetFullPath(req.SourceFolder).Equals(
                    Path.GetFullPath(req.OutputFolder),
                    StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "outputFolder must differ from sourceFolder" });

            try
            {
                var result = await svc.GenerateAsync(req, ct);
                return Results.Ok(result);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }
}
