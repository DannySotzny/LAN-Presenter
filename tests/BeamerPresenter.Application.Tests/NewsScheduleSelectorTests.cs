using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application.Tests;

public sealed class NewsScheduleSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fullscreen_wins_over_higher_numeric_overlay_priority()
    {
        var result = NewsScheduleSelector.Select(
            [
                News(1, NewsMode.Ticker, 100, Now.AddMinutes(-1), Now.AddMinutes(10)),
                News(2, NewsMode.Fullscreen, 1, Now.AddMinutes(-1), Now.AddMinutes(10))
            ],
            Now);

        Assert.Equal(2, result?.Id);
    }

    [Fact]
    public void Selector_ignores_manual_future_and_expired_items()
    {
        var result = NewsScheduleSelector.Select(
            [
                News(1, NewsMode.Ticker, 10, null, null),
                News(2, NewsMode.Ticker, 20, Now.AddMinutes(1), Now.AddMinutes(10)),
                News(3, NewsMode.Fullscreen, 30, Now.AddMinutes(-10), Now),
                News(4, NewsMode.SplitScreen, 5, Now.AddMinutes(-1), Now.AddMinutes(10))
            ],
            Now);

        Assert.Equal(4, result?.Id);
    }

    private static NewsItem News(
        long id,
        NewsMode mode,
        int priority,
        DateTimeOffset? validFrom,
        DateTimeOffset? validUntil) => new()
        {
            Id = id,
            Title = $"News {id}",
            Text = "Text",
            Mode = mode,
            Permanent = true,
            Priority = priority,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            CreatedUtc = Now.AddMinutes(-id)
        };
}
