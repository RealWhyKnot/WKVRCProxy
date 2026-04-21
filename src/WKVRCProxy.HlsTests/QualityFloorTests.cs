using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

public class QualityFloorTests
{
    [Theory]
    [InlineData(1080, 720)]  // Pref 1080p → floor 720p. 1080*2/3=720.
    [InlineData(720,  480)]  // Pref 720p  → floor 480p. 720*2/3=480.
    [InlineData(480,  320)]  // Pref 480p  → floor 320p. 480*2/3=320.
    [InlineData(360,  240)]  // Pref 360p  → floor 240p. 360*2/3=240.
    [InlineData(2160, 1440)] // Pref 4K    → floor 1440p. 2160*2/3=1440.
    public void ComputesQualityFloorAsTwoThirdsOfPreferred(int preferredHeight, int expectedFloor)
    {
        Assert.Equal(expectedFloor, ResolutionEngine.ComputeQualityFloor(preferredHeight));
    }

    [Fact]
    public void AcceptsResultExactlyAtFloor()
    {
        // Pref=1080, floor=720. A 720p result should be accepted, not cascaded.
        Assert.True(ResolutionEngine.IsAcceptableQuality(720, 720));
    }

    [Fact]
    public void AcceptsResultAboveFloor()
    {
        Assert.True(ResolutionEngine.IsAcceptableQuality(1080, 720));
    }

    [Fact]
    public void RejectsResultBelowFloor()
    {
        // Pref=1080, floor=720. 480p < 720p → cascade.
        Assert.False(ResolutionEngine.IsAcceptableQuality(480, 720));
    }

    [Fact]
    public void RejectsResultOneBelowFloor()
    {
        // Boundary test — 719p should NOT be accepted when floor is 720p
        Assert.False(ResolutionEngine.IsAcceptableQuality(719, 720));
    }

    [Fact]
    public void AcceptsNullHeightByDefault()
    {
        // Tiers without height metadata (Tier 2 cloud, Tier 3, Streamlink) must be trusted
        Assert.True(ResolutionEngine.IsAcceptableQuality(null, 720));
    }

    [Theory]
    [InlineData(null, 720,  true)]  // trust-by-default
    [InlineData(1080, 720,  true)]
    [InlineData(720,  720,  true)]  // at floor
    [InlineData(719,  720,  false)] // just below
    [InlineData(480,  720,  false)] // well below
    [InlineData(360,  720,  false)]
    public void CascadeDecisionMatrix(int? resolvedHeight, int floor, bool shouldAccept)
    {
        Assert.Equal(shouldAccept, ResolutionEngine.IsAcceptableQuality(resolvedHeight, floor));
    }

    [Fact]
    public void Pref1080UserScenario_Tier1Returns480_CascadesAsExpected()
    {
        // User's actual reported scenario: Pref=1080, Tier 1 returns 480p, should cascade.
        int floor = ResolutionEngine.ComputeQualityFloor(1080);
        Assert.False(ResolutionEngine.IsAcceptableQuality(480, floor));
    }

    [Fact]
    public void Pref1080UserScenario_Tier2Returns1080_Accepts()
    {
        int floor = ResolutionEngine.ComputeQualityFloor(1080);
        Assert.True(ResolutionEngine.IsAcceptableQuality(1080, floor));
    }

    [Fact]
    public void Pref1080UserScenario_Tier2ReturnsNullHeight_Accepts()
    {
        // Tier 2 (whyknot.dev cloud) doesn't report height yet → trusted by default
        int floor = ResolutionEngine.ComputeQualityFloor(1080);
        Assert.True(ResolutionEngine.IsAcceptableQuality(null, floor));
    }
}
