namespace MailSort.Models;

public enum EnvelopeStatus
{
    /// <summary>Machine sent a valid barcode; tray assigned and returned.</summary>
    Processed = 0,

    /// <summary>Barcode was missing/empty; awaiting operator manual entry.</summary>
    NeedsManualEntry = 1,

    /// <summary>Operator entered barcode + tray; awaiting second machine pass.</summary>
    Resolved = 2,

    /// <summary>Operator dismissed an envelope (e.g. junk).</summary>
    Dismissed = 3,
}
