using MailSort.Matching;
using MailSort.Matching.Configuration;
using MailSort.Matching.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Build a host with the library's services wired in. Configuration
// comes from in-memory defaults + environment overrides. The smoke
// program is a thin driver: it hands the engine image bytes and a
// candidate list, then prints results.

if (args.Length < 1)
{
    Console.WriteLine("Usage: MailSort.Matching.Smoke <image> [image ...]");
    return 1;
}

var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Match:WindowHours"] = "24",
        ["Match:MatchEngine:MaxAddressPHashDistance"] = "16",
        ["Match:MatchEngine:MaxBarcodePHashDistance"] = "18",
        ["Match:MatchEngine:TopK"] = "5",
    })
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
services.AddMailSortMatching(config);
await using var sp = services.BuildServiceProvider();
var engine = sp.GetRequiredService<IMatchEngine>();
var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MatchSettings>>().Value;
Console.WriteLine($"Settings: window={opts.WindowHours}h aMax={opts.MatchEngine.MaxAddressPHashDistance} bMax={opts.MatchEngine.MaxBarcodePHashDistance} topK={opts.MatchEngine.TopK}");

// Hash every input image, build a candidate set, and try to match.
var fingerprints = new List<(string File, Fingerprint Fp)>();
foreach (var f in args)
{
    var fp = await engine.ComputeFingerprintAsync(File.OpenRead(f));
    fingerprints.Add((f, fp));
    Console.WriteLine($"  {Path.GetFileName(f),-30}  A=0x{fp.AddressPHash:X16}  B=0x{fp.BarcodePHash:X16}  C=0x{fp.CenterlineHash:X16}  skew={fp.SkewDegrees:F2}deg");
}
Console.WriteLine();

var cands = new List<EnvelopeCandidate>();
for (int i = 1; i < fingerprints.Count; i++)
{
    cands.Add(new EnvelopeCandidate(
        Id: Path.GetFileName(fingerprints[i].File),
        Barcode: null,
        Tray: 1,
        Fingerprint: fingerprints[i].Fp,
        Source: MatchSource.Automatic));
}

int matches = 0, total = 0;
foreach (var (file, fp) in fingerprints)
{
    total++;
    var candidates = cands.Where(c => c.Id != Path.GetFileName(file)).ToList();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await engine.MatchAsync(File.OpenRead(file), candidates);
    sw.Stop();
    var name = Path.GetFileName(file);
    if (result.Match is not null)
    {
        matches++;
        Console.WriteLine($"  {name,-30} -> {result.Match.EnvelopeId,-30}  addr={result.MatchedAddressDistance} barcode={result.MatchedBarcodeDistance} center={result.MatchedCenterlineDistance} score={result.Score:F1} ({sw.ElapsedMilliseconds}ms)");
    }
    else
    {
        Console.WriteLine($"  {name,-30} -> (no match)  closestAddr={result.ClosestAddressDistance} ({sw.ElapsedMilliseconds}ms)");
    }
}
Console.WriteLine($"\nMatched {matches}/{total} in {total} scans.");
return 0;
