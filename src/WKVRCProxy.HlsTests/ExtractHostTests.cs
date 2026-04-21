using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

public class ExtractHostTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", "youtube.com")]
    [InlineData("https://youtube.com/watch?v=abc",     "youtube.com")]
    [InlineData("https://youtu.be/abc",                "youtu.be")]
    [InlineData("https://WWW.YouTube.com/watch?v=abc", "youtube.com")]
    [InlineData("https://m.youtube.com/watch?v=abc",   "m.youtube.com")]
    [InlineData("https://www.twitch.tv/someone",       "twitch.tv")]
    public void NormalizesHostConsistently(string url, string expectedHost)
    {
        Assert.Equal(expectedHost, ResolutionEngine.ExtractHost(url));
    }

    [Fact]
    public void ReturnsEmptyForMalformedUrl()
    {
        Assert.Equal("", ResolutionEngine.ExtractHost("not a url"));
    }

    [Fact]
    public void ReturnsEmptyForEmpty()
    {
        Assert.Equal("", ResolutionEngine.ExtractHost(""));
    }

    [Fact]
    public void AllYouTubeWatchUrlsMapToSameDomainKey()
    {
        // The domain-flag cache must key on the normalized host so that a bot-check
        // triggered by www.youtube.com also applies to youtube.com (no www) etc.
        string a = ResolutionEngine.ExtractHost("https://www.youtube.com/watch?v=abc");
        string b = ResolutionEngine.ExtractHost("https://youtube.com/watch?v=xyz");
        Assert.Equal(a, b);
    }

    [Fact]
    public void YouTubeAndYoutuBeAreDistinct()
    {
        // Intentional: youtu.be is a different host so it gets its own flag. In practice
        // a bot-check on youtube.com will almost always also affect youtu.be but we can't
        // assume that — each host tracks its own TTL.
        string yt = ResolutionEngine.ExtractHost("https://www.youtube.com/watch?v=abc");
        string ytbe = ResolutionEngine.ExtractHost("https://youtu.be/abc");
        Assert.NotEqual(yt, ytbe);
    }
}
