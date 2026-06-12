namespace MailSort.Models;

/// <summary>
/// Maps a barcode to a target tray. Seeded from a CSV; managed at runtime via
/// the Blazor Tray Mapping page.
/// </summary>
public class TrayMapEntry
{
    public string Barcode { get; set; } = default!;
    public int Tray { get; set; }
    public string? Description { get; set; }
}
