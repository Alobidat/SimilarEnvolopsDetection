using MailSort.Data;
using MailSort.Models;
using MailSort.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MailSort.Api;

public static class Endpoints
{
    public static void MapMailSortEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // Machine -> app. The machine calls this for every envelope.
        api.MapPost("/ingest", IngestAsync)
            .DisableAntiforgery();

        // UI -> app. Lists envelopes that still need operator input.
        api.MapGet("/envelopes/needs-entry", ListNeedsEntryAsync);

        // UI -> app. Operator saves barcode + tray for a pending envelope.
        api.MapPost("/envelopes/{id}/manual-entry", ManualEntryAsync)
            .DisableAntiforgery();

        // UI -> app. Operator dismisses an envelope (e.g. it's junk).
        api.MapPost("/envelopes/{id}/dismiss", DismissAsync)
            .DisableAntiforgery();

        // UI -> app. List every tray mapping (for the Tray Map page).
        api.MapGet("/tray-map", ListTrayMapAsync);

        // UI -> app. Add or update a tray mapping.
        api.MapPost("/tray-map", UpsertTrayMapAsync)
            .DisableAntiforgery();

        // UI -> app. Remove a tray mapping.
        api.MapDelete("/tray-map/{barcode}", DeleteTrayMapAsync);
    }

    public record IngestRequest(IFormFile? Image, string? Barcode, string? MachineScanId);

    private static async Task<IResult> IngestAsync(
        HttpRequest request,
        IngestService ingest,
        ILoggerFactory logf,
        CancellationToken ct)
    {
        var log = logf.CreateLogger("Ingest");

        // Accept either multipart/form-data or JSON+base64. Multipart is what the
        // machine will use; the JSON shape is handy for testing with curl.
        Stream imageStream;
        string? fileName;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "image file is required (field 'image')" });
            imageStream = file.OpenReadStream();
            fileName = file.FileName;

            var barcode = form["barcode"].ToString();
            var scanId = form["scanId"].ToString();
            log.LogInformation("Ingest multipart file={File} barcode='{Barcode}' scanId='{ScanId}'",
                fileName, barcode, scanId);

            var result = await ingest.IngestAsync(imageStream, barcode, scanId, ct);
            return Results.Ok(new
            {
                status = result.Status.ToString(),
                tray = result.Tray,
                envelopeId = result.EnvelopeId,
                matchedEnvelopeId = result.MatchedEnvelopeId,
                addressPHashDistance = result.MatchAddressPHashDistance,
                barcodePHashDistance = result.MatchBarcodePHashDistance,
                centerlineDistance = result.MatchCenterlineDistance,
                score = result.MatchScore,
                skewDegrees = result.SkewDegrees,
            });
        }
        else
        {
            var body = await request.ReadFromJsonAsync<JsonIngest>(cancellationToken: ct);
            if (body is null || string.IsNullOrWhiteSpace(body.ImageBase64))
                return Results.BadRequest(new { error = "imageBase64 is required" });

            imageStream = new MemoryStream(Convert.FromBase64String(body.ImageBase64));
            fileName = "image.jpg";
            log.LogInformation("Ingest json barcode='{Barcode}' scanId='{ScanId}'", body.Barcode, body.MachineScanId);

            var result = await ingest.IngestAsync(imageStream, body.Barcode, body.MachineScanId, ct);
            return Results.Ok(new
            {
                status = result.Status.ToString(),
                tray = result.Tray,
                envelopeId = result.EnvelopeId,
                matchedEnvelopeId = result.MatchedEnvelopeId,
                addressPHashDistance = result.MatchAddressPHashDistance,
                barcodePHashDistance = result.MatchBarcodePHashDistance,
                centerlineDistance = result.MatchCenterlineDistance,
                score = result.MatchScore,
                skewDegrees = result.SkewDegrees,
            });
        }
    }

    public record JsonIngest(string? ImageBase64, string? Barcode, string? MachineScanId);

    private static async Task<IResult> ListNeedsEntryAsync(MailSortDbContext db, CancellationToken ct)
    {
        var items = await db.Envelopes.AsNoTracking()
            .Where(e => e.Status == EnvelopeStatus.NeedsManualEntry)
            .OrderBy(e => e.ScanTimeUtc)
            .Select(e => new
            {
                e.Id,
                e.ScanTimeUtc,
                e.MachineScanId,
                ImageUrl = $"/api/envelopes/{e.Id}/image",
            })
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    public record ManualEntryRequest(string Barcode, int Tray, string? EnteredBy);

    private static async Task<IResult> ManualEntryAsync(
        string id,
        [FromBody] ManualEntryRequest body,
        MailSortDbContext db,
        CancellationToken ct)
    {
        var env = await db.Envelopes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (env is null) return Results.NotFound();
        if (env.Status != EnvelopeStatus.NeedsManualEntry)
            return Results.Conflict(new { error = $"envelope is in status {env.Status}, cannot manually enter" });

        // If the operator entered a barcode, also resolve the tray from the map
        // (the operator can override it though).
        if (!string.IsNullOrWhiteSpace(body.Barcode))
        {
            env.Barcode = body.Barcode.Trim();
            if (body.Tray > 0) env.Tray = body.Tray;
        }
        env.Tray = body.Tray > 0 ? body.Tray : env.Tray;
        env.ManualEntryAt = DateTime.UtcNow;
        env.ManualEntryBy = body.EnteredBy;
        env.Status = EnvelopeStatus.Resolved;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = env.Id, tray = env.Tray, barcode = env.Barcode });
    }

    private static async Task<IResult> DismissAsync(string id, MailSortDbContext db, CancellationToken ct)
    {
        var env = await db.Envelopes.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (env is null) return Results.NotFound();
        env.Status = EnvelopeStatus.Dismissed;
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    private static async Task<IResult> ListTrayMapAsync(MailSortDbContext db, CancellationToken ct)
    {
        var rows = await db.TrayMap.AsNoTracking()
            .OrderBy(t => t.Tray).ThenBy(t => t.Barcode)
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    public record TrayMapRequest(string Barcode, int Tray, string? Description);

    private static async Task<IResult> UpsertTrayMapAsync(
        [FromBody] TrayMapRequest body,
        MailSortDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Barcode) || body.Tray <= 0)
            return Results.BadRequest(new { error = "barcode and tray (>0) are required" });

        var existing = await db.TrayMap.FirstOrDefaultAsync(t => t.Barcode == body.Barcode, ct);
        if (existing is null)
        {
            db.TrayMap.Add(new TrayMapEntry
            {
                Barcode = body.Barcode.Trim(),
                Tray = body.Tray,
                Description = body.Description,
            });
        }
        else
        {
            existing.Tray = body.Tray;
            existing.Description = body.Description;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    private static async Task<IResult> DeleteTrayMapAsync(string barcode, MailSortDbContext db, CancellationToken ct)
    {
        var existing = await db.TrayMap.FirstOrDefaultAsync(t => t.Barcode == barcode, ct);
        if (existing is null) return Results.NotFound();
        db.TrayMap.Remove(existing);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
