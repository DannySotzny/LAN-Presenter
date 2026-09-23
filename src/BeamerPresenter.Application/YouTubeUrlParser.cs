using System.Text.RegularExpressions;

namespace BeamerPresenter.Application;

public sealed record YouTubeReference(string VideoId)
{
    public string SourceKey => $"youtube:{VideoId}";
    public string CanonicalUrl => $"https://www.youtube.com/watch?v={VideoId}";
}

public static partial class YouTubeUrlParser
{
    public static bool TryParse(string? value, out YouTubeReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var videoId = ExtractVideoId(uri);

        if (videoId is null || !VideoIdPattern().IsMatch(videoId))
        {
            return false;
        }

        reference = new YouTubeReference(videoId);
        return true;
    }

    public static YouTubeReference Parse(string value)
    {
        if (!TryParse(value, out var reference) || reference is null)
        {
            throw new FormatException("Die angegebene URL ist kein unterstützter YouTube-Link.");
        }

        return reference;
    }

    private static bool IsYouTubeHost(string host) =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractVideoId(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 1 ? parts[0] : null;
        }

        if (!IsYouTubeHost(host))
        {
            return null;
        }

        if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            return ParseQueryValue(uri.Query, "v");
        }

        var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return pathParts.Length == 2 && pathParts[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)
            ? pathParts[1]
            : null;
    }

    private static string? ParseQueryValue(string query, string requestedName)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = Uri.UnescapeDataString(separator >= 0 ? part[..separator] : part);
            if (!name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Uri.UnescapeDataString(separator >= 0 ? part[(separator + 1)..] : string.Empty);
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdPattern();
}
