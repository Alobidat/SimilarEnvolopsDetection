using MailSort.Matching;
using Xunit;

namespace MailSort.Matching.Tests;

public class RegionOfInterestTests
{
    [Fact]
    public void IsValid_AcceptsFullImage()
    {
        var roi = new RegionOfInterest(0, 0, 1, 1);
        Assert.True(roi.IsValid);
    }

    [Fact]
    public void IsValid_AcceptsCenteredBox()
    {
        var roi = new RegionOfInterest(0.25, 0.25, 0.5, 0.5);
        Assert.True(roi.IsValid);
    }

    [Fact]
    public void IsValid_RejectsNegativeOrigin()
    {
        var roi = new RegionOfInterest(-0.1, 0, 0.5, 0.5);
        Assert.False(roi.IsValid);
    }

    [Fact]
    public void IsValid_RejectsZeroSize()
    {
        var roi = new RegionOfInterest(0, 0, 0, 0.5);
        Assert.False(roi.IsValid);
    }

    [Fact]
    public void IsValid_RejectsExtendingPastImage()
    {
        var roi = new RegionOfInterest(0.5, 0.5, 0.6, 0.6);
        Assert.False(roi.IsValid);
    }

    [Fact]
    public void DefaultAddressAndBarcode_IsValid()
    {
        Assert.True(RegionOfInterest.DefaultAddressAndBarcode.IsValid);
    }
}
