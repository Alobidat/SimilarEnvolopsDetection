namespace MailSort.Models;

public class Envelope
{
    public string Id { get; set; } = default!;
    public DateTime ScanTimeUtc { get; set; }

    /// <summary>Barcode value as received from the machine (may be null/empty).</summary>
    public string? BarcodeRaw { get; set; }

    /// <summary>Final barcode (set by operator when BarcodeRaw is empty).</summary>
    public string? Barcode { get; set; }

    /// <summary>Assigned tray, null until resolved.</summary>
    public int? Tray { get; set; }

    public EnvelopeStatus Status { get; set; }

    public string ImagePath { get; set; } = default!;

    /// <summary>pHash computed on the recipient address block ROI.</summary>
    public long AddressPHash { get; set; }

    /// <summary>pHash computed on the 2D barcode ROI.</summary>
    public long BarcodePHash { get; set; }

    /// <summary>Coarse gradient-direction sketch on the data-varying band.</summary>
    public long CenterlineHash { get; set; }

    /// <summary>Estimated skew angle of the envelope, in degrees, at scan time.</summary>
    public double SkewDegrees { get; set; }

    /// <summary>Operator-provided machine scan id, used as a fallback match key.</summary>
    public string? MachineScanId { get; set; }

    public DateTime? ManualEntryAt { get; set; }
    public string? ManualEntryBy { get; set; }

    /// <summary>On a 2nd-pass envelope, points to the original envelope that was manually entered.</summary>
    public string? MatchedEnvelopeId { get; set; }
    public Envelope? MatchedEnvelope { get; set; }

    /// <summary>True when this row was created by a 2nd pass of the machine.</summary>
    public bool IsSecondPass { get; set; }
}
