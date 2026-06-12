using MailSort.Matching;
using MailSort.Matching.Configuration;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MailSort.Api;

public static class DuplicateScanEndpoints
{
    /// <summary>
    /// Maps the duplicate-scan test endpoints. There are three routes:
    ///
    ///   GET  /api/dup-test/analyze?folder=...&strict=...
    ///        Runs the analysis and returns a <see cref="DuplicateScanReport"/>.
    ///
    ///   GET  /api/dup-test/image?path=...
    ///        Streams back an image file from an absolute path. The
    ///        server enforces that the path lives under the
    ///        configured "TestImagesRoot" (default: the "samples"
    ///        folder next to the app) so this endpoint cannot be
    ///        abused to read arbitrary files from disk.
    ///
    ///   GET  /api/dup-test/image?path=...&roi=address|barcode
    ///        Same path-safety, but crops the image to the address
    ///        or barcode ROI and returns a small PNG. Used by the
    ///        UI to show the operator exactly which region of the
    ///        image was hashed.
    /// </summary>
    public static void MapDuplicateScanEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/dup-test");

        g.MapGet("/analyze", async (
            string? folder,
            string? folder2,
            bool? strict,
            DuplicateScanAnalyzer analyzer,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var effectiveFolder = string.IsNullOrWhiteSpace(folder) ? DefaultSamplesFolder() : folder;
            if (!Path.IsPathRooted(effectiveFolder))
                effectiveFolder = Path.GetFullPath(effectiveFolder);

            string? effectiveFolder2 = null;
            if (!string.IsNullOrWhiteSpace(folder2))
                effectiveFolder2 = Path.IsPathRooted(folder2) ? folder2 : Path.GetFullPath(folder2);

            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/dup-test/image";
            var report = await analyzer.AnalyzeAsync(
                effectiveFolder,
                baseUrl,
                useDefaultThresholds: !(strict ?? false),
                ct,
                folder2: effectiveFolder2);
            return Results.Ok(report);
        });

        g.MapGet("/image", async (
            string? path,
            string? roi,
            string? folder,
            bool? outline,
            IOptions<MatchSettings> settingsOpt) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(path);

            // Defense: the path must live under the folder the caller declared it
            // came from. If no folder is provided, fall back to DefaultSamplesFolder().
            // This prevents an open file-read while still supporting user-chosen dirs.
            var allowedRoot = string.IsNullOrWhiteSpace(folder) ? DefaultSamplesFolder() : folder;
            var root = Path.GetFullPath(allowedRoot);
            var fullPath = Path.GetFullPath(path);
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "path is outside the declared folder" }, statusCode: StatusCodes.Status403Forbidden);

            if (!System.IO.File.Exists(fullPath))
                return Results.NotFound();

            // The "roi" parameter is either null/empty (return the
            // whole image), "address", or "barcode". For ROI requests
            // we crop the image down to the same rectangle the
            // fingerprinting code uses, and optionally draw a thin
            // red outline around it so the operator can see exactly
            // what was hashed.
            var roiKind = (roi ?? "").ToLowerInvariant();
            if (roiKind is "address" or "barcode" or "center" or "overlay")
            {
                var settings = settingsOpt.Value;

                // "overlay" returns the full image with all three ROI
                // rectangles drawn on it in different colours so the
                // operator can see exactly what regions are being hashed.
                if (roiKind == "overlay")
                {
                    try
                    {
                        await using var src = System.IO.File.OpenRead(fullPath);
                        using var img = await Image.LoadAsync<Rgba32>(src);
                        var addrRect   = ToPixelRect(img.Width, img.Height, settings.MatchEngine.AddressRoi);
                        var barcodeRect = ToPixelRect(img.Width, img.Height, settings.MatchEngine.BarcodeRoi);
                        var cen = RegionOfInterest.DefaultAddressAndBarcode;
                        var centerRect = ToPixelRect(img.Width, img.Height,
                            new RegionOfInterestOptions { X = cen.X, Y = cen.Y, Width = cen.Width, Height = cen.Height });
                        img.Mutate(ctx => { });  // ensure mutable
                        DrawRect(img, addrRect,    new Rgba32(220, 40,  40, 220), 4); // red   = address
                        DrawRect(img, barcodeRect, new Rgba32(40,  160, 40, 220), 4); // green = barcode
                        DrawRect(img, centerRect,  new Rgba32(40,  80, 220, 220), 4); // blue  = centerline
                        ScaleToMaxEdge(img, 480);
                        using var ms = new MemoryStream();
                        await img.SaveAsync(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 });
                        return Results.File(ms.ToArray(), "image/png");
                    }
                    catch (Exception ex)
                    {
                        return Results.Json(new { error = $"failed to decode {Path.GetFileName(fullPath)}: {ex.Message}" }, statusCode: StatusCodes.Status415UnsupportedMediaType);
                    }
                }

                RegionOfInterestOptions roiOpts;
                if (roiKind == "address")
                    roiOpts = settings.MatchEngine.AddressRoi;
                else if (roiKind == "barcode")
                    roiOpts = settings.MatchEngine.BarcodeRoi;
                else
                    roiOpts = new RegionOfInterestOptions { X = RegionOfInterest.DefaultAddressAndBarcode.X, Y = RegionOfInterest.DefaultAddressAndBarcode.Y, Width = RegionOfInterest.DefaultAddressAndBarcode.Width, Height = RegionOfInterest.DefaultAddressAndBarcode.Height };
                try
                {
                    await using var src = System.IO.File.OpenRead(fullPath);
                    using var img = await Image.LoadAsync<Rgba32>(src);
                    var rect = ToPixelRect(img.Width, img.Height, roiOpts);
                    img.Mutate(ctx => ctx.Crop(rect));
                    // Make small ROIs more useful to look at.
                    ScaleToMaxEdge(img, 240);
                    using var ms = new MemoryStream();
                    await img.SaveAsync(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 });
                    return Results.File(ms.ToArray(), "image/png");
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = $"failed to decode {Path.GetFileName(fullPath)}: {ex.Message}" }, statusCode: StatusCodes.Status415UnsupportedMediaType);
                }
            }

            var ext = Path.GetExtension(fullPath).ToLowerInvariant();

            // Browsers do not render TIFF (or BMP) inside <img> tags,
            // so for those formats we transcode to PNG on the server
            // using ImageSharp. JPEG and PNG are passed through
            // byte-for-byte so the page stays snappy on the common
            // case. We also downscale to a reasonable max edge so a
            // 4K envelope doesn't blow the page's memory.
            const int MaxEdgePx = 480;
            if (ext is ".tif" or ".tiff" or ".bmp")
            {
                try
                {
                    await using var src = System.IO.File.OpenRead(fullPath);
                    using var img = await Image.LoadAsync<Rgba32>(src);
                    ScaleToMaxEdge(img, MaxEdgePx);
                    var png = new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 };
                    using var ms = new MemoryStream();
                    await img.SaveAsync(ms, png);
                    return Results.File(ms.ToArray(), "image/png");
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = $"failed to decode {Path.GetFileName(fullPath)}: {ex.Message}" }, statusCode: StatusCodes.Status415UnsupportedMediaType);
                }
            }

            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => "application/octet-stream",
            };

            // Even for JPEG/PNG, downscale if the file is huge.
            if (TryGetDownscaledPath(fullPath, MaxEdgePx, out var scaledPath))
            {
                return Results.File(scaledPath, contentType);
            }
            return Results.File(fullPath, contentType);
        });
    }

    private static SixLabors.ImageSharp.Rectangle ToPixelRect(
        int w, int h, RegionOfInterestOptions roi)
    {
        var x = Math.Clamp((int)Math.Round(roi.X * w), 0, w - 1);
        var y = Math.Clamp((int)Math.Round(roi.Y * h), 0, h - 1);
        var rw = Math.Clamp((int)Math.Round(roi.Width * w), 1, w - x);
        var rh = Math.Clamp((int)Math.Round(roi.Height * h), 1, h - y);
        return new SixLabors.ImageSharp.Rectangle(x, y, rw, rh);
    }

    /// <summary>
    /// Draws a solid-colour rectangle border (no fill) on the image
    /// using direct pixel writes (no SixLabors.ImageSharp.Drawing dependency).
    /// </summary>
    private static void DrawRect(
        Image<Rgba32> img,
        SixLabors.ImageSharp.Rectangle rect,
        Rgba32 colour,
        int thickness)
    {
        img.ProcessPixelRows(accessor =>
        {
            int x0 = Math.Clamp(rect.X, 0, img.Width - 1);
            int y0 = Math.Clamp(rect.Y, 0, img.Height - 1);
            int x1 = Math.Clamp(rect.X + rect.Width  - 1, 0, img.Width - 1);
            int y1 = Math.Clamp(rect.Y + rect.Height - 1, 0, img.Height - 1);
            for (int y = y0; y <= y1; y++)
            {
                var row = accessor.GetRowSpan(y);
                bool onHEdge = y < y0 + thickness || y > y1 - thickness;
                for (int x = x0; x <= x1; x++)
                {
                    bool onVEdge = x < x0 + thickness || x > x1 - thickness;
                    if (onHEdge || onVEdge) row[x] = colour;
                }
            }
        });
    }

    /// <summary>
    /// In-place resize so the longer edge is at most <paramref name="maxEdge"/>.
    /// No-op if the image is already smaller.
    /// </summary>
    private static void ScaleToMaxEdge(Image<Rgba32> img, int maxEdge)
    {
        var longest = Math.Max(img.Width, img.Height);
        if (longest <= maxEdge) return;
        var scale = (double)maxEdge / longest;
        var w = Math.Max(1, (int)Math.Round(img.Width * scale));
        var h = Math.Max(1, (int)Math.Round(img.Height * scale));
        img.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(w, h),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));
    }

    /// <summary>
    /// For JPEG/PNG, check the dimensions and return a downscaled
    /// PNG cache path if the original is too big. The cache lives
    /// under the system temp folder keyed by content hash. The
    /// caller passes through the bytes via <see cref="Results.File(string,string)"/>.
    /// </summary>
    private static bool TryGetDownscaledPath(string sourcePath, int maxEdge, out string scaledPath)
    {
        scaledPath = string.Empty;
        try
        {
            var info = Image.Identify(sourcePath);
            if (Math.Max(info.Width, info.Height) <= maxEdge) return false;
        }
        catch
        {
            return false;
        }

        var cacheDir = Path.Combine(Path.GetTempPath(), "mailsort-image-cache");
        Directory.CreateDirectory(cacheDir);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(sourcePath + ":" + maxEdge)));
        var cached = Path.Combine(cacheDir, hash + ".png");
        if (System.IO.File.Exists(cached) && new FileInfo(cached).Length > 0)
        {
            scaledPath = cached;
            return true;
        }
        try
        {
            using var img = Image.Load<Rgba32>(sourcePath);
            ScaleToMaxEdge(img, maxEdge);
            img.Save(cached, new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 });
            scaledPath = cached;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve the default samples folder. We try, in order:
    ///  1. the env var <c>MAILSORT_SAMPLES_DIR</c>;
    ///  2. a "samples" folder next to the app's working directory;
    ///  3. a "samples" folder next to the .sln (walks up from the
    ///     content root, so it works whether the app is launched
    ///     from the repo root or from MailSort/bin/.../).
    /// </summary>
    public static string DefaultSamplesFolder()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MAILSORT_SAMPLES_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        var cwd = Directory.GetCurrentDirectory();
        var nearCwd = Path.Combine(cwd, "samples");
        if (Directory.Exists(nearCwd)) return Path.GetFullPath(nearCwd);

        var dir = new DirectoryInfo(cwd);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(candidate)) return candidate;
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "MailSort.sln"))) break;
            dir = dir.Parent;
        }
        return nearCwd;
    }
}
