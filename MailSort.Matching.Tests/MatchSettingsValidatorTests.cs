using MailSort.Matching.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailSort.Matching.Tests;

public class MatchSettingsValidatorTests
{
    private static ValidateOptionsResult Validate(MatchSettings s) =>
        new MatchSettingsValidator().Validate(null, s);

    [Fact]
    public void Validate_AcceptsDefaults()
    {
        var s = new MatchSettings();
        Assert.True(Validate(s).Succeeded);
    }

    [Fact]
    public void Validate_RejectsWindowHoursOutOfRange()
    {
        var s = new MatchSettings { WindowHours = 0 };
        Assert.False(Validate(s).Succeeded);
        s.WindowHours = 1000;
        Assert.False(Validate(s).Succeeded);
    }

    [Fact]
    public void Validate_RejectsMaxAddressOutOfRange()
    {
        var s = new MatchSettings();
        s.MatchEngine.MaxAddressPHashDistance = -1;
        Assert.False(Validate(s).Succeeded);
        s.MatchEngine.MaxAddressPHashDistance = 65;
        Assert.False(Validate(s).Succeeded);
    }

    [Fact]
    public void Validate_RejectsInvalidRoi()
    {
        var s = new MatchSettings();
        s.MatchEngine.AddressRoi = new RegionOfInterestOptions
        {
            X = 0.7, Y = 0.0, Width = 0.5, Height = 0.5 // extends past right edge
        };
        var result = Validate(s);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failures);
        Assert.Contains(result.Failures!, f => f.Contains("AddressRoi"));
    }

    [Fact]
    public void Validate_AcceptsCustomValidRoi()
    {
        var s = new MatchSettings();
        s.MatchEngine.AddressRoi = new RegionOfInterestOptions
        {
            X = 0.1, Y = 0.1, Width = 0.4, Height = 0.4
        };
        s.MatchEngine.BarcodeRoi = new RegionOfInterestOptions
        {
            X = 0.55, Y = 0.55, Width = 0.4, Height = 0.4
        };
        Assert.True(Validate(s).Succeeded);
    }
}
