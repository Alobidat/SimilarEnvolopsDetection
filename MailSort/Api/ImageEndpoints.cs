using MailSort.Data;
using Microsoft.EntityFrameworkCore;

namespace MailSort.Api;

public static class ImageEndpoints
{
    public static void MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/envelopes/{id}/image", async (
            string id,
            MailSortDbContext db,
            CancellationToken ct) =>
        {
            var env = await db.Envelopes.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id, ct);
            if (env is null || !System.IO.File.Exists(env.ImagePath))
                return Results.NotFound();

            var contentType = env.ImagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg";
            return Results.File(env.ImagePath, contentType);
        });
    }
}
