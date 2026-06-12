using MailSort.Matching;

if (args.Length < 1)
{
    Console.WriteLine("Usage: HashProbe <image-file> [image-file ...]");
    return;
}

var addressRoi = new RegionOfInterest(0.04, Y: 0.50, Width: 0.55, Height: 0.20);
var barcodeRoi = new RegionOfInterest(0.55, Y: 0.78, Width: 0.40, Height: 0.18);
Console.WriteLine($"Address ROI: X={addressRoi.X} Y={addressRoi.Y} W={addressRoi.Width} H={addressRoi.Height}");
Console.WriteLine($"Barcode ROI: X={barcodeRoi.X} Y={barcodeRoi.Y} W={barcodeRoi.Width} H={barcodeRoi.Height}");
Console.WriteLine();

var results = new List<(string File, Fingerprint Fp)>();
foreach (var file in args)
{
    var fp = await RegionalFingerprint.ComputeAsync(File.OpenRead(file), addressRoi, barcodeRoi);
    results.Add((file, fp));
    var name = System.IO.Path.GetFileName(file);
    Console.WriteLine($"{name,-40}  A=0x{fp.AddressPHash:X16}  B=0x{fp.BarcodePHash:X16}  C=0x{fp.CenterlineHash:X16}  skew={fp.SkewDegrees:F2}deg");
}

if (results.Count < 2) return;

Console.WriteLine();
Console.WriteLine("Pairwise Hamming distances (Address | Barcode | Centerline):");
var shortNames = results.Select(r => System.IO.Path.GetFileName(r.File).Substring(0, Math.Min(10, System.IO.Path.GetFileName(r.File).Length))).ToList();
Console.WriteLine($"{"",-40}  {string.Join("  ", shortNames.Select(n => n.PadRight(15)))}");
for (int i = 0; i < results.Count; i++)
{
    Console.Write($"{System.IO.Path.GetFileName(results[i].File),-40}");
    for (int j = 0; j < results.Count; j++)
    {
        var aD = RegionalFingerprint.HammingDistance(results[i].Fp.AddressPHash, results[j].Fp.AddressPHash);
        var bD = RegionalFingerprint.HammingDistance(results[i].Fp.BarcodePHash, results[j].Fp.BarcodePHash);
        var cD = RegionalFingerprint.HammingDistance(results[i].Fp.CenterlineHash, results[j].Fp.CenterlineHash);
        Console.Write($"  {aD,2}|{bD,2}|{cD,2}  ");
    }
    Console.WriteLine();
}
