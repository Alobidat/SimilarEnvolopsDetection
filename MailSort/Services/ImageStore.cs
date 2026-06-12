namespace MailSort.Services;

public class ImageStore
{
    private readonly string _root;
    private readonly ILogger<ImageStore> _log;

    public ImageStore(IConfiguration cfg, ILogger<ImageStore> log)
    {
        _root = Path.GetFullPath(cfg["Storage:ImageRoot"] ?? "data/images");
        _log = log;
        Directory.CreateDirectory(_root);
        _log.LogInformation("ImageStore root: {Root}", _root);
    }

    public string Root => _root;

    public async Task<string> SaveAsync(string envelopeId, Stream imageStream, CancellationToken ct = default)
    {
        var path = Path.Combine(_root, envelopeId + ".jpg");
        await using var fs = File.Create(path);
        await imageStream.CopyToAsync(fs, ct);
        return path;
    }

    public Stream OpenRead(string fileName)
    {
        // Defend against path traversal.
        var safe = Path.GetFileName(fileName);
        return File.OpenRead(Path.Combine(_root, safe));
    }
}
