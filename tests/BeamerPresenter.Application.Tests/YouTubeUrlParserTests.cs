using BeamerPresenter.Application;

namespace BeamerPresenter.Application.Tests;

public sealed class YouTubeUrlParserTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42")]
    [InlineData("https://youtube.com/shorts/dQw4w9WgXcQ")]
    public void Supported_urls_are_normalized(string value)
    {
        var parsed = YouTubeUrlParser.Parse(value);

        Assert.Equal("dQw4w9WgXcQ", parsed.VideoId);
        Assert.Equal("youtube:dQw4w9WgXcQ", parsed.SourceKey);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", parsed.CanonicalUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=too-short")]
    [InlineData("javascript://youtube.com/watch?v=dQw4w9WgXcQ")]
    public void Unsupported_or_unsafe_values_are_rejected(string? value)
    {
        Assert.False(YouTubeUrlParser.TryParse(value, out var parsed));
        Assert.Null(parsed);
    }
}
