using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public static class NewsScheduleSelector
{
    public static NewsItem? Select(IReadOnlyCollection<NewsItem> items, DateTimeOffset now) =>
        items
            .Where(item => item.ValidFrom.HasValue || item.ValidUntil.HasValue)
            .Where(item => !item.ValidFrom.HasValue || item.ValidFrom.Value <= now)
            .Where(item => !item.ValidUntil.HasValue || item.ValidUntil.Value > now)
            .OrderByDescending(item => item.Mode == NewsMode.Fullscreen)
            .ThenByDescending(item => item.Priority)
            .ThenByDescending(item => item.CreatedUtc)
            .FirstOrDefault();
}
