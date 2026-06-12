using MailSort.Matching.Configuration;

namespace MailSort.Matching.Engine;

/// <summary>
/// Compares a scanned envelope image against a caller-supplied list of
/// known envelopes and returns the best match, or null if no candidate
/// is close enough. Pure compute -- no I/O, no database.
/// </summary>
public interface IMatchEngine
{
    /// <summary>
    /// Hash the image and find the closest candidate. The candidates
    /// list is filtered by the caller's own criteria (e.g. recent scan
    /// time); the matcher does not look up anything itself.
    /// </summary>
    Task<MatchResult> MatchAsync(
        Stream imageStream,
        IReadOnlyList<EnvelopeCandidate> candidates,
        CancellationToken ct = default);

    /// <summary>
    /// Hash the image and return the fingerprint only, without doing
    /// any matching. Useful for the 1st pass where you just want to
    /// store the fingerprint on a new envelope row.
    /// </summary>
    Task<Fingerprint> ComputeFingerprintAsync(
        Stream imageStream,
        CancellationToken ct = default);
}
