namespace MailSort.Components;

public record NeedsEntryItem(string Id, DateTime ScanTimeUtc, string? MachineScanId, string ImageUrl);

public record TrayMapRow(string Barcode, int Tray, string? Description);

public record ManualEntryResponse(string Id, int? Tray, string? Barcode);
