using System.Collections.Generic;
using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

public class YtDlpOutputParserTests
{
    [Fact]
    public void ParsesWellFormedPrintOutput()
    {
        var lines = new List<string>
        {
            "url:https://cdn.example.com/video.m3u8",
            "meta:1080|1920|avc1.640028|137|m3u8_native"
        };

        var result = ResolutionEngine.ParseYtDlpOutput(lines);

        Assert.NotNull(result);
        Assert.Equal("https://cdn.example.com/video.m3u8", result!.Url);
        Assert.Equal(1080, result.Height);
        Assert.Equal(1920, result.Width);
        Assert.Equal("avc1.640028", result.Vcodec);
        Assert.Equal("137", result.FormatId);
        Assert.Equal("m3u8_native", result.Protocol);
    }

    [Fact]
    public void TreatsNaFieldsAsNull()
    {
        var lines = new List<string>
        {
            "url:https://cdn.example.com/a.mp4",
            "meta:NA|NA|NA|NA|NA"
        };

        var result = ResolutionEngine.ParseYtDlpOutput(lines);

        Assert.NotNull(result);
        Assert.Null(result!.Height);
        Assert.Null(result.Width);
        Assert.Null(result.Vcodec);
        Assert.Null(result.FormatId);
        Assert.Null(result.Protocol);
    }

    [Fact]
    public void TreatsNoneAndEmptyFieldsAsNull()
    {
        var lines = new List<string>
        {
            "url:https://cdn.example.com/a.mp4",
            "meta:None||None|None|None"
        };

        var result = ResolutionEngine.ParseYtDlpOutput(lines);

        Assert.NotNull(result);
        Assert.Null(result!.Height);
        Assert.Null(result.Width);
    }

    [Fact]
    public void AcceptsPlainHttpUrlFromLegacyOutput()
    {
        // yt-dlp-og and streamlink emit the URL as a bare line, no `url:` prefix
        var lines = new List<string>
        {
            "https://legacy-cdn.example.com/stream.m3u8"
        };

        var result = ResolutionEngine.ParseYtDlpOutput(lines);

        Assert.NotNull(result);
        Assert.Equal("https://legacy-cdn.example.com/stream.m3u8", result!.Url);
        Assert.Null(result.Height);
        Assert.Null(result.Width);
    }

    [Fact]
    public void ReturnsNullWhenNoUrlPresent()
    {
        var lines = new List<string> { "meta:1080|1920|avc1|137|m3u8_native" };
        Assert.Null(ResolutionEngine.ParseYtDlpOutput(lines));
    }

    [Fact]
    public void ReturnsNullForEmptyInput()
    {
        Assert.Null(ResolutionEngine.ParseYtDlpOutput(new List<string>()));
    }

    [Fact]
    public void IgnoresStderrNoiseLines()
    {
        // yt-dlp's stderr sometimes leaks into stdout in weird terminal setups — skip anything not matching
        var lines = new List<string>
        {
            "[youtube] Extracting URL: https://youtube.com/watch?v=abc",
            "WARNING: some warning here",
            "url:https://cdn.example.com/video.mp4",
            "[debug] ignored",
            "meta:720|1280|avc1|22|https"
        };

        var result = ResolutionEngine.ParseYtDlpOutput(lines);

        Assert.NotNull(result);
        Assert.Equal("https://cdn.example.com/video.mp4", result!.Url);
        Assert.Equal(720, result.Height);
        Assert.Equal(1280, result.Width);
    }

    [Fact]
    public void FirstUrlWinsWhenMultiplePresent()
    {
        // Some playlist extractors emit multiple URLs. We expect the first one.
        var lines = new List<string>
        {
            "url:https://cdn.example.com/first.mp4",
            "url:https://cdn.example.com/second.mp4"
        };

        var result = ResolutionEngine.ParseYtDlpOutput(lines);

        Assert.NotNull(result);
        Assert.Equal("https://cdn.example.com/first.mp4", result!.Url);
    }

    [Fact]
    public void HandlesPartialMetaLine()
    {
        // yt-dlp returned fewer fields than requested — safely parse what's there
        var lines = new List<string>
        {
            "url:https://cdn.example.com/a.mp4",
            "meta:480|854"
        };

        var result = ResolutionEngine.ParseYtDlpOutput(lines);

        Assert.NotNull(result);
        Assert.Equal(480, result!.Height);
        Assert.Equal(854, result.Width);
        Assert.Null(result.Vcodec);
    }

    [Fact]
    public void RejectsUrlPrefixWithNonHttpValue()
    {
        // Protects against malformed output like `url:ERROR` or `url:<something garbled>`
        var lines = new List<string>
        {
            "url:ERROR",
            "meta:1080|1920|avc1|137|https"
        };

        var result = ResolutionEngine.ParseYtDlpOutput(lines);
        Assert.Null(result);
    }
}
