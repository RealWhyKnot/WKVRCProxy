using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

public class ResolveCacheKeyTests
{
    [Fact]
    public void SameUrlAndPlayerProduceSameKey()
    {
        string a = ResolutionEngine.ResolveCacheKey("https://youtu.be/abc", "AVPro");
        string b = ResolutionEngine.ResolveCacheKey("https://youtu.be/abc", "AVPro");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentPlayerProducesDifferentKey()
    {
        // AVPro and Unity get different yt-dlp format strings, so cached results must not cross-pollinate
        string avpro = ResolutionEngine.ResolveCacheKey("https://youtu.be/abc", "AVPro");
        string unity = ResolutionEngine.ResolveCacheKey("https://youtu.be/abc", "Unity");
        Assert.NotEqual(avpro, unity);
    }

    [Fact]
    public void DifferentUrlProducesDifferentKey()
    {
        string a = ResolutionEngine.ResolveCacheKey("https://youtu.be/abc", "AVPro");
        string b = ResolutionEngine.ResolveCacheKey("https://youtu.be/xyz", "AVPro");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void QueryStringVariationsAreDistinct()
    {
        // Different query strings (e.g. ?si=tracking vs not) produce distinct keys — conservative
        // behavior, since yt-dlp may interpret query parameters as part of the video identity.
        string a = ResolutionEngine.ResolveCacheKey("https://youtu.be/abc", "AVPro");
        string b = ResolutionEngine.ResolveCacheKey("https://youtu.be/abc?si=tracking", "AVPro");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void KeyContainsBothPlayerAndUrl()
    {
        string key = ResolutionEngine.ResolveCacheKey("https://youtu.be/abc", "AVPro");
        Assert.Contains("AVPro", key);
        Assert.Contains("https://youtu.be/abc", key);
    }
}
