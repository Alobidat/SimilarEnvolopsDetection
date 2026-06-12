using CsvHelper;
using CsvHelper.Configuration;
using MailSort.Data;
using MailSort.Models;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace MailSort.Services;

public static class TrayMapSeeder
{
    /// <summary>
    /// Loads tray mappings from a CSV at <c>Storage:TrayMapCsv</c> (relative to
    /// content root) on startup. CSV columns: Barcode,Tray,Description.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider sp, IConfiguration cfg, ILogger log, CancellationToken ct = default)
    {
        var csvPath = cfg["Storage:TrayMapCsv"];
        if (string.IsNullOrWhiteSpace(csvPath)) return;
        var full = Path.IsPathRooted(csvPath)
            ? csvPath
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), csvPath));
        if (!File.Exists(full))
        {
            log.LogInformation("Tray map CSV not found at {Path} -- skipping seed", full);
            return;
        }

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MailSortDbContext>();

        var cfgCsv = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };
        using var reader = new StreamReader(full);
        using var csv = new CsvReader(reader, cfgCsv);

        var rows = csv.GetRecords<TrayCsvRow>().ToList();
        var added = 0; var updated = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Barcode) || row.Tray <= 0) continue;
            var existing = await db.TrayMap.FirstOrDefaultAsync(t => t.Barcode == row.Barcode, ct);
            if (existing is null)
            {
                db.TrayMap.Add(new TrayMapEntry
                {
                    Barcode = row.Barcode.Trim(),
                    Tray = row.Tray,
                    Description = row.Description,
                });
                added++;
            }
            else
            {
                existing.Tray = row.Tray;
                existing.Description = row.Description;
                updated++;
            }
        }
        await db.SaveChangesAsync(ct);
        log.LogInformation("Tray map seed complete: +{Added} ~{Updated} (file: {File})", added, updated, full);
    }

    private class TrayCsvRow
    {
        public string Barcode { get; set; } = "";
        public int Tray { get; set; }
        public string? Description { get; set; }
    }
}
