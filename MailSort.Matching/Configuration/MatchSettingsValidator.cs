using Microsoft.Extensions.Options;

namespace MailSort.Matching.Configuration;

/// <summary>
/// Fail-fast validation for <see cref="MatchSettings"/>. Registered as
/// <c>IValidateOptions&lt;MatchSettings&gt;</c> so misconfiguration throws
/// at startup, not on the first ingest.
/// </summary>
public sealed class MatchSettingsValidator : IValidateOptions<MatchSettings>
{
    public ValidateOptionsResult Validate(string? name, MatchSettings options)
    {
        var errors = new List<string>();

        if (options.WindowHours < 1 || options.WindowHours > 24 * 30)
            errors.Add($"Match:WindowHours must be between 1 and 720, got {options.WindowHours}.");

        if (options.MatchEngine.MaxAddressPHashDistance < 0 || options.MatchEngine.MaxAddressPHashDistance > 64)
            errors.Add($"Match:MatchEngine:MaxAddressPHashDistance must be in [0, 64], got {options.MatchEngine.MaxAddressPHashDistance}.");
        if (options.MatchEngine.MaxBarcodePHashDistance < 0 || options.MatchEngine.MaxBarcodePHashDistance > 64)
            errors.Add($"Match:MatchEngine:MaxBarcodePHashDistance must be in [0, 64], got {options.MatchEngine.MaxBarcodePHashDistance}.");
        if (options.MatchEngine.TopK < 1 || options.MatchEngine.TopK > 64)
            errors.Add($"Match:MatchEngine:TopK must be in [1, 64], got {options.MatchEngine.TopK}.");

        try { options.MatchEngine.AddressRoi.Validate("Match:MatchEngine:AddressRoi"); }
        catch (Exception ex) { errors.Add(ex.Message); }
        try { options.MatchEngine.BarcodeRoi.Validate("Match:MatchEngine:BarcodeRoi"); }
        catch (Exception ex) { errors.Add(ex.Message); }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
