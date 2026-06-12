using MailSort.Matching;
using Xunit;

namespace MailSort.Matching.Tests;

public class RegionalFingerprintTests
{
    [Fact]
    public void HammingDistance_ZeroOnIdentical()
    {
        var x = 0x1234567890ABCDEFL; // high bit clear -> fits in long
        Assert.Equal(0, RegionalFingerprint.HammingDistance(x, x));
    }

    [Fact]
    public void HammingDistance_AllBitsDiffers()
    {
        var x = 0L;
        var y = -1L;
        Assert.Equal(64, RegionalFingerprint.HammingDistance(x, y));
    }

    [Fact]
    public void HammingDistance_IsSymmetric()
    {
        var a = 0x1234567890ABCDEFL; // high nibble 1
        var b = 0x0FEDCBA098765432L; // high nibble 0
        Assert.Equal(
            RegionalFingerprint.HammingDistance(a, b),
            RegionalFingerprint.HammingDistance(b, a));
    }

    [Fact]
    public async Task ComputeAsync_RejectsInvalidRoi()
    {
        await using var stream = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF }); // not a valid JPEG
        var badRoi = new RegionOfInterest(-0.1, 0, 0.5, 0.5);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RegionalFingerprint.ComputeAsync(stream, badRoi, RegionOfInterest.DefaultAddressAndBarcode));
    }
}
