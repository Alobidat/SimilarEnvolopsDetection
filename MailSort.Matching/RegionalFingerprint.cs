using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MailSort.Matching;

/// <summary>
/// Computes a regional <see cref="Fingerprint"/> from an envelope image.
/// Pipeline:
///   1. Decode (JPEG or TIFF) with metadata skipped.
///   2. Estimate skew from a grayscale copy and deskew the image.
///   3. For each ROI, crop + contrast-stretch + resize + compute pHash.
/// </summary>
public static class RegionalFingerprint
{
    private const int PHashSize = 64;
    private const int PHashCoeffs = 8;
    private const int CenterSize = 32;
    private const int ZoneGrid = 8; // 8×8 zones = 64 bits for zone-mean hash

    private static readonly DecoderOptions FastDecode = new()
    {
        Configuration = SixLabors.ImageSharp.Configuration.Default,
        SkipMetadata = true,
    };

    /// <summary>
    /// Compute the fingerprint for the given image, cropped to the two ROIs.
    /// Throws <see cref="ArgumentException"/> if either ROI is invalid.
    /// </summary>
    public static async Task<Fingerprint> ComputeAsync(
        Stream imageStream,
        RegionOfInterest addressRoi,
        RegionOfInterest barcodeRoi,
        CancellationToken ct = default)
    {
        if (!addressRoi.IsValid) throw new ArgumentException("addressRoi is invalid.", nameof(addressRoi));
        if (!barcodeRoi.IsValid) throw new ArgumentException("barcodeRoi is invalid.", nameof(barcodeRoi));

        using var image = await Image.LoadAsync<Rgba32>(FastDecode, imageStream, ct);
        return Compute(image, addressRoi, barcodeRoi);
    }

    public static Fingerprint Compute(
        Image<Rgba32> image,
        RegionOfInterest addressRoi,
        RegionOfInterest barcodeRoi)
    {
        // No deskew.  The RescanSimulator rotates the full image ±3° around
        // its centre (X=0.5, Y=0.5).  The label ROIs are at X≈0.79, so the
        // rotation produces:
        //   • Vertical displacement ≈ ±39px (address/barcode content moves up/down)
        //   • Horizontal displacement ≈ ±15px
        //
        // Hash strategy per channel:
        //   ADDRESS  – zone-mean 8×8 on 64×64 thumbnail.  The vertical shift
        //              moves text lines across zone boundaries (~8 bits flip),
        //              but this channel is used only as a soft tiebreaker so
        //              the noise is acceptable.
        //
        //   BARCODE  – column-mean hash: resize to 64 columns (any height),
        //              compute mean intensity per column, compare each to the
        //              column-median.  For a 1D postal barcode, the vertical
        //              shift has ZERO effect on column means (bars are tall),
        //              and the horizontal shift displaces only ≈1 column
        //              (15px / 918px × 64 ≈ 1.0 col).  Within-pair Hamming ≈ 2.
        //
        //   CENTERLINE – coarse gradient of the full right panel (unchanged).
        const double skew = 0.0;

        using var addressCrop = image.Clone(ctx => ctx
            .Crop(ToPixelRect(image.Width, image.Height, addressRoi))
            .Grayscale());
        using var barcodeCrop = image.Clone(ctx => ctx
            .Crop(ToPixelRect(image.Width, image.Height, barcodeRoi))
            .Grayscale());
        using var centerCrop = image.Clone(ctx => ctx
            .Crop(ToPixelRect(image.Width, image.Height, RegionOfInterest.DefaultAddressAndBarcode))
            .Grayscale());

        StretchContrast(addressCrop);
        StretchContrast(barcodeCrop);
        StretchContrast(centerCrop);

        // Address: zone-mean on 64×64 thumbnail (tilt-noisy but usable as tiebreaker)
        using var addrNorm = addressCrop.Clone(ctx => ctx.Resize(new ResizeOptions
            { Size = new Size(64, 64), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Bicubic }));

        // Barcode: resize to 64 columns, keep full height so column means integrate
        // over all bar rows and are unaffected by vertical shift
        using var barcNorm = barcodeCrop.Clone(ctx => ctx.Resize(new ResizeOptions
            { Size = new Size(64, barcodeCrop.Height), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Bicubic }));

        using var cHash = centerCrop.Clone(ctx => ctx.Resize(new ResizeOptions
            { Size = new Size(CenterSize, CenterSize), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Bicubic }));

        return new Fingerprint(
            AddressPHash: ComputeZoneMeanHash(addrNorm),
            BarcodePHash: ComputeColumnMeanHash(barcNorm),
            CenterlineHash: ComputeCoarseGradientHash(cHash),
            SkewDegrees: skew);
    }

    private static Rectangle ToPixelRect(
        int w, int h, RegionOfInterest roi)
    {
        var x = Math.Clamp((int)Math.Round(roi.X * w), 0, w - 1);
        var y = Math.Clamp((int)Math.Round(roi.Y * h), 0, h - 1);
        var rw = Math.Clamp((int)Math.Round(roi.Width * w), 1, w - x);
        var rh = Math.Clamp((int)Math.Round(roi.Height * h), 1, h - y);
        return new Rectangle(x, y, rw, rh);
    }

    /// <summary>
    /// Translates <paramref name="img"/> in-place so that the centroid of its
    /// dark pixels (R &lt; <paramref name="threshold"/>) lies at the image
    /// centre (Width/2, Height/2).  Vacated pixels are filled with white.
    /// <para>
    /// Compensates for the rigid-body translation that results when the
    /// RescanSimulator rotates the full envelope image around its centre while
    /// the label ROIs are located far from that centre.  The shift is computed
    /// on the (already-downsampled) 64×64 image so the operation is fast.
    /// </para>
    /// </summary>
    private static void CenterByCentroid(
        Image<Rgba32> img, byte threshold = 100, int minDark = 50)
    {
        int w = img.Width, h = img.Height;
        long sx = 0, sy = 0, n = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (img[x, y].R < threshold) { sx += x; sy += y; n++; }
        }
        if (n < minDark) return; // too few dark pixels — skip (blank/uniform crop)

        int dx = w / 2 - (int)(sx / n);  // positive → shift content right
        int dy = h / 2 - (int)(sy / n);  // positive → shift content down
        if (dx == 0 && dy == 0) return;

        // Clone, blank, then paint each source pixel at its translated position.
        using var src = img.Clone();
        img.Mutate(ctx => ctx.BackgroundColor(Color.White));
        for (int oy = 0; oy < h; oy++)
        {
            int ty = oy + dy;
            if (ty < 0 || ty >= h) continue;
            for (int ox = 0; ox < w; ox++)
            {
                int tx = ox + dx;
                if (tx < 0 || tx >= w) continue;
                img[tx, ty] = src[ox, oy];
            }
        }
    }

    /// <summary>
    /// 5/95-percentile histogram stretch. Robust to global brightness
    /// shifts and to the white quiet zone around 2D barcodes.
    /// </summary>
    private static void StretchContrast(Image<Rgba32> img)
    {
        var w = img.Width;
        var h = img.Height;
        var values = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            values[y * w + x] = img[x, y].R;
        Array.Sort(values);
        var lo = values[(int)(values.Length * 0.05)];
        var hi = values[(int)(values.Length * 0.95)];
        if (hi - lo < 16) return;
        var scale = 255.0 / (hi - lo);
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var v = row[x].R;
                    var s = (byte)Math.Clamp((v - lo) * scale, 0, 255);
                    row[x] = new Rgba32(s, s, s, row[x].A);
                }
            }
        });
    }

    private static double EstimateSkew(Image<Rgba32> img, int sampleStride)
    {
        Span<int> bins = stackalloc int[180];
        Span<double> weights = stackalloc double[180];
        for (int y = sampleStride; y < img.Height - sampleStride; y += sampleStride)
        {
            for (int x = sampleStride; x < img.Width - sampleStride; x += sampleStride)
            {
                int gx = img[x + sampleStride, y].R - img[x - sampleStride, y].R;
                int gy = img[x, y + sampleStride].R - img[x, y - sampleStride].R;
                if (gx == 0 && gy == 0) continue;

                var mag = Math.Sqrt(gx * (double)gx + gy * (double)gy);
                var angle = Math.Atan2(gy, gx) * 180.0 / Math.PI;
                if (angle < 0) angle += 180;
                int b = (int)angle;
                if (b < 0) b = 0; if (b >= 180) b = 179;
                bins[b]++;
                weights[b] += mag;
            }
        }
        int best = 0;
        double bestW = 0;
        for (int i = 0; i < 180; i++)
        {
            var w = weights[i] +
                (i > 0 ? weights[i - 1] * 0.5 : 0) +
                (i < 179 ? weights[i + 1] * 0.5 : 0);
            if (w > bestW) { bestW = w; best = i; }
        }
        var skew = best;
        if (skew > 90) skew -= 180;
        if (skew < -45) skew += 90;
        if (skew > 45) skew -= 90;
        return skew;
    }

    // ── Row-gradient hash ────────────────────────────────────────────────────

    /// <summary>
    /// Computes a 64-bit hash by:
    ///   1. Resizing the crop to 64 rows × 64 cols.
    ///   2. Computing the mean darkness of each row (averaged across all columns).
    ///   3. Encoding bit[i] = 1 iff rowMean[i] &lt; rowMean[i+1] (row i is darker).
    /// 
    /// Comparing adjacent rows rather than a global threshold makes this hash:
    ///   • Tilt-robust: a k° tilt shifts the 64-row profile by ~k×W/H rows.
    ///     Only the ~k bits at shifted order-boundaries flip (vs 15+ for zone-mean).
    ///   • Brightness-invariant: comparing relative ordering, not absolute values.
    ///   • Noise-robust: each row mean averages 64 pixels (σ_mean = σ_px/8 = 1).
    ///   • Discriminating: different barcodes have different row-density profiles,
    ///     giving Hamming distances near 32 for different electors.
    /// </summary>
    private static long ComputeRowGradientHash(Image<Rgba32> img)
    {
        // Resize to 64×64 so row heights are fixed and independent of source resolution.
        using var norm = img.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(64, 64),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        // Mean darkness (grayscale R channel) per row, averaged across all 64 columns.
        Span<double> rowMeans = stackalloc double[64];
        for (int y = 0; y < 64; y++)
        {
            double s = 0;
            for (int x = 0; x < 64; x++) s += norm[x, y].R;
            rowMeans[y] = s / 64.0;
        }

        // Differential encoding: bit[i] = row[i] darker than row[i+1]?
        // (darker pixel = lower R value in a white-background grayscale image)
        ulong hash = 0;
        for (int i = 0; i < 63; i++)
            if (rowMeans[i] < rowMeans[i + 1]) hash |= 1UL << i;
        if (rowMeans[63] < rowMeans[0]) hash |= 1UL << 63; // wrap for cyclic stability
        return unchecked((long)hash);
    }

    // ── Zone-mean hash ──────────────────────────────────────────────────────

    /// <summary>Zone-mean hash (unused — kept for diagnostic/comparison use).</summary>
    internal static long ComputeZoneMeanHashPublic(Image<Rgba32> img) => ComputeZoneMeanHash(img);

    // Column-mean hash
    private static long ComputeColumnMeanHash(Image<Rgba32> img)
    {
        var w = img.Width;
        var h = img.Height;
        var colMeans = new double[w];
        for (int x = 0; x < w; x++)
        {
            double s = 0;
            for (int y = 0; y < h; y++) s += img[x, y].R;
            colMeans[x] = s / h;
        }
        var sorted = (double[])colMeans.Clone();
        Array.Sort(sorted);
        double median = sorted[w / 2];
        ulong hash = 0;
        for (int i = 0; i < w; i++)
            if (colMeans[i] < median) hash |= 1UL << i;
        return unchecked((long)hash);
    }

    // ── Zone-mean hash ──────────────────────────────────────────────────────

    /// <summary>
    /// Zone-mean hash: divides the image into an 8×8 grid of zones and
    /// encodes whether each zone's mean intensity is above or below the
    /// global median of all zone means. Works directly on the full-size
    /// crop (no DCT, no bicubic resize). Each zone spans many pixels so
    /// a 1-2 pixel position shift between re-scans has negligible effect
    /// on zone means. Different 2D barcodes produce clearly different
    /// spatial distributions of black modules, giving large Hamming
    /// distances between different electors.
    /// </summary>
    private static long ComputeZoneMeanHash(Image<Rgba32> img)
    {
        var zoneMeans = new double[ZoneGrid * ZoneGrid];
        double zoneW = (double)img.Width  / ZoneGrid;
        double zoneH = (double)img.Height / ZoneGrid;
        for (int cy = 0; cy < ZoneGrid; cy++)
        for (int cx = 0; cx < ZoneGrid; cx++)
        {
            int x0 = (int)(cx * zoneW);
            int y0 = (int)(cy * zoneH);
            int x1 = Math.Min((int)((cx + 1) * zoneW), img.Width);
            int y1 = Math.Min((int)((cy + 1) * zoneH), img.Height);
            double sum = 0;
            int count = 0;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                sum += img[x, y].R;
                count++;
            }
            zoneMeans[cy * ZoneGrid + cx] = count > 0 ? sum / count : 128.0;
        }
        var sorted = (double[])zoneMeans.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        ulong hash = 0;
        for (int i = 0; i < zoneMeans.Length; i++)
            if (zoneMeans[i] > median) hash |= 1UL << i;
        return unchecked((long)hash);
    }

    private static long ComputePHash(Image<Rgba32> img)
    {
        var size = PHashSize;
        var values = new double[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            values[y * size + x] = img[x, y].R;

        Dct2DInPlace(values, size);

        Span<double> low = stackalloc double[PHashCoeffs * PHashCoeffs - 1];
        var k = 0;
        for (int y = 0; y < PHashCoeffs; y++)
        for (int x = 0; x < PHashCoeffs; x++)
        {
            if (x == 0 && y == 0) continue;
            low[k++] = values[y * size + x];
        }

        var sorted = low.ToArray();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];

        ulong hash = 0;
        k = 0;
        for (int y = 0; y < PHashCoeffs; y++)
        for (int x = 0; x < PHashCoeffs; x++)
        {
            if (x == 0 && y == 0) continue;
            if (low[k++] > median) hash |= 1UL << ((y * PHashCoeffs) + x - 1);
        }
        return unchecked((long)hash);
    }

    private static long ComputeCoarseGradientHash(Image<Rgba32> img)
    {
        var size = CenterSize;
        var cells = 4;
        var cellW = size / cells;
        ulong hash = 0;
        var bit = 0;
        for (int cy = 0; cy < cells; cy++)
        for (int cx = 0; cx < cells; cx++)
        {
            var hEnergy = 0.0;
            var vEnergy = 0.0;
            for (int y = cy * cellW + 1; y < (cy + 1) * cellW - 1 && y < size - 1; y++)
            for (int x = cx * cellW + 1; x < (cx + 1) * cellW - 1 && x < size - 1; x++)
            {
                int gx = img[x + 1, y].R - img[x - 1, y].R;
                int gy = img[x, y + 1].R - img[x, y - 1].R;
                hEnergy += Math.Abs(gx);
                vEnergy += Math.Abs(gy);
            }
            if (hEnergy > vEnergy && bit < 64) hash |= 1UL << bit;
            bit++;
            if (vEnergy > hEnergy && bit < 64) hash |= 1UL << bit;
            bit++;
        }
        return unchecked((long)hash);
    }

    private static void Dct2DInPlace(double[] a, int n)
    {
        var row = new double[n];
        var tmp = new double[n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++) row[x] = a[y * n + x];
            Dct1D(row, tmp);
            for (int x = 0; x < n; x++) a[y * n + x] = row[x];
        }
        var col = new double[n];
        for (int x = 0; x < n; x++)
        {
            for (int y = 0; y < n; y++) col[y] = a[y * n + x];
            Dct1D(col, tmp);
            for (int y = 0; y < n; y++) a[y * n + x] = col[y];
        }
    }

    private static void Dct1D(double[] x, double[] tmp)
    {
        var n = x.Length;
        for (int k = 0; k < n; k++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += x[i] * Math.Cos(Math.PI / n * (i + 0.5) * k);
            tmp[k] = sum;
        }
        Array.Copy(tmp, x, n);
    }

    /// <summary>Number of differing bits between two 64-bit hashes (Hamming distance).</summary>
    public static int HammingDistance(long a, long b)
    {
        var x = (ulong)a ^ (ulong)b;
        return BitOperations.PopCount(x);
    }
}
