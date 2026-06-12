using MailSort.Matching.Configuration;
using MailSort.Matching.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MailSort.Matching;

/// <summary>
/// DI extensions for the MailSort.Matching library. Call
/// <see cref="AddMailSortMatching"/> once at startup.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="MatchSettings"/> from the "Match" config section
    /// and register the matcher as a singleton. Validation runs at
    /// startup so a misconfigured ROI or threshold fails fast.
    /// </summary>
    public static IServiceCollection AddMailSortMatching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        services.AddOptions<MatchSettings>()
            .Bind(configuration.GetSection(MatchSettings.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<MatchSettings>, MatchSettingsValidator>();
        services.TryAddSingleton<IMatchEngine, MatchEngine>();
        return services;
    }
}
