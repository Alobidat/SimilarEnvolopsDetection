using Microsoft.EntityFrameworkCore;
using MailSort.Models;

namespace MailSort.Data;

public class MailSortDbContext : DbContext
{
    public MailSortDbContext(DbContextOptions<MailSortDbContext> options) : base(options) { }

    public DbSet<Envelope> Envelopes => Set<Envelope>();
    public DbSet<TrayMapEntry> TrayMap => Set<TrayMapEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Envelope>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.BarcodeRaw).HasMaxLength(256);
            e.Property(x => x.Barcode).HasMaxLength(256);
            e.Property(x => x.ImagePath).HasMaxLength(512);
            e.Property(x => x.MachineScanId).HasMaxLength(128);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.MachineScanId);
            e.HasIndex(x => x.AddressPHash);
            e.HasIndex(x => x.BarcodePHash);
            // Composite index used by 2nd-pass matching: find Resolved envelopes
            // in the recent window without scanning the whole table.
            e.HasIndex(x => new { x.Status, x.ScanTimeUtc });
        });

        modelBuilder.Entity<TrayMapEntry>(e =>
        {
            e.HasKey(x => x.Barcode);
            e.Property(x => x.Barcode).HasMaxLength(256);
            e.Property(x => x.Description).HasMaxLength(256);
        });
    }
}
