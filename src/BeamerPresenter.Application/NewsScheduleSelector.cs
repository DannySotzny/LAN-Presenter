using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public static class NewsScheduleSelector
{
    public static ScheduledNewsSelection Select(IReadOnlyCollection<NewsItem> items, DateTimeOffset now)
    {
        var eligible = items
            .Where(item => item.ValidFrom.HasValue || item.ValidUntil.HasValue)
            .Where(item => !item.ValidFrom.HasValue || item.ValidFrom.Value <= now)
            .Where(item => !item.ValidUntil.HasValue || item.ValidUntil.Value > now)
            .ToArray();

        var ticker = eligible
            .Where(item => item.Mode == NewsMode.Ticker)
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.CreatedUtc)
            .FirstOrDefault();
        var main = eligible
            .Where(item => item.Mode != NewsMode.Ticker)
            .OrderByDescending(item => item.Mode == NewsMode.Fullscreen)
            .ThenByDescending(item => item.Priority)
            .ThenByDescending(item => item.CreatedUtc)
            .FirstOrDefault();

        return new ScheduledNewsSelection(ticker, main);
    }
}

public sealed record ScheduledNewsSelection(NewsItem? Ticker, NewsItem? Main);
