using System.Collections.Concurrent;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using BeamerPresenter.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace BeamerPresenter.Browser.Tests;

public sealed class PresenterBrowserTests : IAsyncLifetime
{
    private const string TestPassword = "browser-test-password";
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "BeamerPresenter.BrowserTests", Guid.NewGuid().ToString("N"));
    private readonly RecordingPlaybackCommands _playbackCommands = new();
    private WebApplication? _application;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string? _baseAddress;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddPresenterInfrastructure(_dataDirectory);
        builder.Services.AddPresenterWebUi();
        builder.Services.AddSingleton<IPlaybackCommandService>(_playbackCommands);
        builder.Services.AddSingleton<INewsCommandService>(_playbackCommands);
        builder.Services.AddSingleton<IPresenterControlService, NoOpPresenterControls>();

        _application = builder.Build();
        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
            await using var database = await factory.CreateDbContextAsync();
            await database.Database.MigrateAsync();
        }

        _application.UseStaticFiles();
        _application.UsePresenterLoopbackProtection();
        _application.UseAuthentication();
        _application.UseAuthorization();
        _application.UseAntiforgery();
        _application.MapPresenterWebUi();
        await _application.StartAsync();
        await _application.Services.GetRequiredService<IPresenterSettingsService>().SetWebPasswordAsync(TestPassword);

        var addresses = _application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        _baseAddress = Assert.Single(addresses!);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    [Fact]
    public async Task Management_uses_clickable_menu_routes_instead_of_one_long_page()
    {
        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseAddress}/login");
        await page.FillAsync("#password", TestPassword);
        await page.Locator("form[action='/account/login'] button[type='submit']").ClickAsync();
        await page.WaitForURLAsync($"{_baseAddress}/");

        Assert.Equal(4, await page.Locator(".management-nav a").CountAsync());
        Assert.True(await page.GetByText("AKTUELLE WIEDERGABE", new() { Exact = true }).IsVisibleAsync());
        Assert.False(await page.GetByText("Wiedergabe-Queue", new() { Exact = true }).IsVisibleAsync());

        await page.GetByRole(AriaRole.Link, new() { Name = "Wiedergabe", Exact = true }).ClickAsync();
        await page.WaitForURLAsync($"{_baseAddress}/playback");
        Assert.True(await page.GetByText("Wiedergabe-Queue", new() { Exact = true }).IsVisibleAsync());

        await page.GetByRole(AriaRole.Link, new() { Name = "Mediathek", Exact = true }).ClickAsync();
        await page.WaitForURLAsync($"{_baseAddress}/media");
        Assert.True(await page.GetByText("Videobibliothek", new() { Exact = true }).IsVisibleAsync());

        await page.GetByRole(AriaRole.Link, new() { Name = "News", Exact = true }).ClickAsync();
        await page.WaitForURLAsync($"{_baseAddress}/news");
        Assert.True(await page.GetByText("News & Einblendungen", new() { Exact = true }).IsVisibleAsync());
    }

    [Fact]
    public async Task Presenter_handles_playback_news_reconnect_and_terminal_reports()
    {
        await using var context = await _browser!.NewContextAsync();
        await context.AddInitScriptAsync(MediaAndSocketTestDoubles);
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_baseAddress}/presenter");
        await WaitForTextAsync(page, "#presenter-status", "Verbunden");

        var gateway = _application!.Services.GetRequiredService<IPresenterGateway>();
        await gateway.LoadLocalVideoAsync(42, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(12), autoPlay: true);

        await page.WaitForFunctionAsync("document.querySelector('#presenter-video').dataset.playCalls === '1'");
        Assert.EndsWith("/media/42", await page.GetAttributeAsync("#presenter-video", "src"), StringComparison.Ordinal);
        Assert.Equal(3, await page.EvalOnSelectorAsync<double>("#presenter-video", "video => video.currentTime"));
        Assert.False(await page.Locator("#presenter-video").EvaluateAsync<bool>("video => video.classList.contains('presenter-media-hidden')"));
        Assert.True(await page.Locator("#presenter-idle").EvaluateAsync<bool>("idle => idle.classList.contains('presenter-idle-hidden')"));

        await gateway.PauseAsync();
        await WaitForTelemetryAsync("Paused");
        await gateway.PlayAsync();
        await WaitForTelemetryAsync("Playing");
        await gateway.SeekAsync(TimeSpan.FromSeconds(7));
        await gateway.SetVolumeAsync(0.35);
        await page.WaitForFunctionAsync("document.querySelector('#presenter-video').currentTime === 7");
        Assert.Equal(0.35, await page.EvalOnSelectorAsync<double>("#presenter-video", "video => video.volume"), 2);

        await gateway.ShowTickerAsync(new NewsItem
        {
            Id = 1,
            Title = "Ticker title",
            Text = "Ticker text",
            Mode = NewsMode.Ticker,
            Permanent = true
        });
        await page.WaitForFunctionAsync("document.querySelector('#presenter-ticker').dataset.newsId === '1'");
        Assert.False(await page.Locator("#presenter-ticker").EvaluateAsync<bool>("ticker => ticker.classList.contains('presenter-media-hidden')"));
        Assert.False(await page.Locator("#presenter-video").EvaluateAsync<bool>("video => video.classList.contains('presenter-media-hidden')"));
        await AssertNewsModeAsync(page, gateway, NewsMode.SplitScreen, "presenter-news-splitscreen", splitActive: true);
        Assert.Equal("Ticker title", await page.TextContentAsync("#presenter-ticker-title"));
        Assert.True(await page.EvaluateAsync<bool>("""
            () => {
                const ticker = document.querySelector('#presenter-ticker');
                const news = document.querySelector('#presenter-news');
                return ticker.getBoundingClientRect().bottom === window.innerHeight &&
                    Number(getComputedStyle(ticker).zIndex) > Number(getComputedStyle(news).zIndex) &&
                    parseFloat(getComputedStyle(news).paddingBottom) > parseFloat(getComputedStyle(news).paddingTop);
            }
            """));
        await AssertNewsModeAsync(page, gateway, NewsMode.Fullscreen, "presenter-news-fullscreen", splitActive: false);
        Assert.Equal("Ticker title", await page.TextContentAsync("#presenter-ticker-title"));
        Assert.True(await page.EvaluateAsync<bool>("""
            () => parseFloat(getComputedStyle(document.querySelector('#presenter-news')).paddingBottom) >
                parseFloat(getComputedStyle(document.querySelector('#presenter-news')).paddingTop)
            """));
        await gateway.HideNewsAsync();
        await page.WaitForFunctionAsync("document.querySelector('#presenter-news').classList.contains('presenter-media-hidden')");
        Assert.False(await page.Locator("#presenter-ticker").EvaluateAsync<bool>("ticker => ticker.classList.contains('presenter-media-hidden')"));
        await AssertNewsModeAsync(page, gateway, NewsMode.SplitScreen, "presenter-news-splitscreen", splitActive: true);
        await gateway.HideTickerAsync();
        await page.WaitForFunctionAsync("document.querySelector('#presenter-ticker').classList.contains('presenter-media-hidden')");
        Assert.False(await page.Locator("#presenter-news").EvaluateAsync<bool>("news => news.classList.contains('presenter-media-hidden')"));
        await gateway.HideNewsAsync();

        await page.EvaluateAsync("window.__presenterSockets.at(-1).close()");
        await page.WaitForFunctionAsync("window.__presenterSockets.length >= 2", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        await WaitForTextAsync(page, "#presenter-status", "Verbunden");

        await page.DispatchEventAsync("#presenter-video", "ended");
        await WaitForAdvanceCountAsync(1);
        Assert.True(_playbackCommands.AdvanceCalls.TryPeek(out var ended));
        Assert.True(ended.Successful);
        Assert.Equal(TimeSpan.FromSeconds(7), ended.Position);

        await gateway.LoadLocalVideoAsync(43, null, null, autoPlay: false);
        await page.WaitForFunctionAsync("document.querySelector('#presenter-video').getAttribute('src').endsWith('/media/43')");
        await page.Locator("#presenter-video").EvaluateAsync("video => video.dataset.allowTestError = 'true'");
        await page.DispatchEventAsync("#presenter-video", "error");
        await WaitForAdvanceCountAsync(2);
        Assert.Contains(_playbackCommands.AdvanceCalls, call => !call.Successful);
    }

    [Fact]
    public async Task YouTube_embedding_error_starts_download_and_remembers_play_now()
    {
        await using var context = await _browser!.NewContextAsync();
        var phase = "downloading";
        string? intentBody = null;
        var downloadRequests = new List<string>();
        await context.RouteAsync("**/api/youtube/download**", async route =>
        {
            downloadRequests.Add(route.Request.Url + " " + route.Request.Method + " " + route.Request.PostData);
            if (route.Request.Url.EndsWith("/intent", StringComparison.Ordinal))
            {
                intentBody = route.Request.PostData;
            }
            var body = $$"""{"videoId":"M7lc1UVf-VE","phase":"{{phase}}","mediaId":null,"error":null}""";
            await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = body });
        });
        await context.AddInitScriptAsync("""
            window.YT = {
                Player: class {
                    constructor(element, options) {
                        setTimeout(() => options.events.onReady({ target: this }), 0);
                        setTimeout(() => options.events.onError({ data: 150 }), 400);
                    }
                    getDuration() { return 90; }
                    destroy() {}
                }
            };
            """);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_baseAddress}/login");
        await page.FillAsync("#password", TestPassword);
        await page.Locator("form[action='/account/login'] button[type='submit']").ClickAsync();
        await page.GotoAsync($"{_baseAddress}/playback");
        await page.FillAsync("#youtube-url", "https://youtu.be/M7lc1UVf-VE");
        await page.ClickAsync("#youtube-load-metadata");
        await WaitForTextAsync(page, "#youtube-metadata-status", "Video erkannt");
        await WaitForTextAsync(page, "#youtube-metadata-status", "Video wird lokal geladen");
        Assert.False(await page.Locator(".youtube-actions button").Last.IsDisabledAsync());
        await page.Locator(".youtube-actions button").Last.ClickAsync();
        await page.WaitForFunctionAsync("document.querySelector('#youtube-metadata-status').textContent.includes('vorgemerkt')");
        Assert.True(intentBody?.Contains("action=now", StringComparison.Ordinal) == true,
            string.Join(" | ", downloadRequests));
        phase = "ready";
        await WaitForTextAsync(page, "#youtube-metadata-status", "Mediathek bereit");
        await page.FillAsync("#youtube-url", "https://youtu.be/dQw4w9WgXcQ");
        Assert.False(await page.Locator(".youtube-actions button").Last.IsDisabledAsync());
    }

    [Fact]
    public async Task Media_library_shows_recently_loaded_badge_for_seven_days()
    {
        var factory = _application!.Services.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
        await using (var database = await factory.CreateDbContextAsync())
        {
            database.Videos.AddRange(
                new VideoAsset
                {
                    FileName = "recent-video.mp4", FullPath = Path.Combine(_dataDirectory, "recent-video.mp4"),
                    AddedAtUtc = DateTimeOffset.UtcNow.AddDays(-6), IsAvailable = true,
                    ProbeStatus = MediaProbeStatus.Valid, PlaybackStatus = MediaPlaybackStatus.Supported
                },
                new VideoAsset
                {
                    FileName = "old-video.mp4", FullPath = Path.Combine(_dataDirectory, "old-video.mp4"),
                    AddedAtUtc = DateTimeOffset.UtcNow.AddDays(-8), IsAvailable = true,
                    ProbeStatus = MediaProbeStatus.Valid, PlaybackStatus = MediaPlaybackStatus.Supported
                });
            await database.SaveChangesAsync();
        }

        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_baseAddress}/login");
        await page.FillAsync("#password", TestPassword);
        await page.Locator("form[action='/account/login'] button[type='submit']").ClickAsync();
        await page.GotoAsync($"{_baseAddress}/media");

        Assert.Equal(1, await page.GetByText("Kürzlich geladen").CountAsync());
        Assert.Equal(1, await page.Locator("tr").Filter(new LocatorFilterOptions { HasText = "recent-video.mp4" })
            .GetByText("Kürzlich geladen").CountAsync());
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_application is not null)
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }

        if (Directory.Exists(_dataDirectory))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private async Task WaitForTelemetryAsync(string expectedStatus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var telemetry = _application!.Services.GetRequiredService<PresenterConnectionState>();
        while (!string.Equals(telemetry.LatestReport.Status, expectedStatus, StringComparison.Ordinal))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task WaitForAdvanceCountAsync(int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_playbackCommands.AdvanceCalls.Count < expectedCount)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task WaitForTextAsync(IPage page, string selector, string expectedText) =>
        await page.WaitForFunctionAsync(
            "([selector, expected]) => document.querySelector(selector)?.textContent.includes(expected)",
            new[] { selector, expectedText },
            new PageWaitForFunctionOptions { Timeout = 10_000 });

    private static async Task AssertNewsModeAsync(
        IPage page,
        IPresenterGateway gateway,
        NewsMode mode,
        string expectedClass,
        bool splitActive)
    {
        await gateway.ShowNewsAsync(new NewsItem
        {
            Id = (long)mode + 1,
            Title = $"{mode} title",
            Text = $"{mode} text",
            Mode = mode,
            Permanent = true
        });
        await page.WaitForFunctionAsync(
            "expected => document.querySelector('#presenter-news').classList.contains(expected)",
            expectedClass);
        Assert.Equal($"{mode} title", await page.TextContentAsync("#presenter-news-title"));
        Assert.Equal(splitActive, await page.Locator("#presenter-root").EvaluateAsync<bool>("root => root.classList.contains('news-split-active')"));
    }

    private const string MediaAndSocketTestDoubles = """
        (() => {
            const NativeWebSocket = window.WebSocket;
            window.__presenterSockets = [];
            window.WebSocket = class extends NativeWebSocket {
                constructor(...args) {
                    super(...args);
                    window.__presenterSockets.push(this);
                }
            };

            const state = new WeakMap();
            const mediaState = element => {
                if (!state.has(element)) state.set(element, { currentTime: 0, duration: 90, volume: 1 });
                return state.get(element);
            };
            Object.defineProperty(HTMLMediaElement.prototype, 'currentTime', {
                configurable: true,
                get() { return mediaState(this).currentTime; },
                set(value) { mediaState(this).currentTime = Number(value); }
            });
            Object.defineProperty(HTMLMediaElement.prototype, 'duration', {
                configurable: true,
                get() { return mediaState(this).duration; }
            });
            Object.defineProperty(HTMLMediaElement.prototype, 'volume', {
                configurable: true,
                get() { return mediaState(this).volume; },
                set(value) { mediaState(this).volume = Number(value); }
            });
            Object.defineProperty(HTMLMediaElement.prototype, 'currentSrc', {
                configurable: true,
                get() { return this.src || ''; }
            });
            const nativeAddEventListener = HTMLMediaElement.prototype.addEventListener;
            HTMLMediaElement.prototype.addEventListener = function(type, listener, options) {
                if (type !== 'error') return nativeAddEventListener.call(this, type, listener, options);
                return nativeAddEventListener.call(this, type, event => {
                    if (this.dataset.allowTestError === 'true') listener.call(this, event);
                }, options);
            };
            HTMLMediaElement.prototype.load = function() {
                if (this.getAttribute('src')) queueMicrotask(() => this.dispatchEvent(new Event('loadedmetadata')));
            };
            HTMLMediaElement.prototype.play = function() {
                this.dataset.playCalls = String(Number(this.dataset.playCalls || 0) + 1);
                this.dispatchEvent(new Event('playing'));
                return Promise.resolve();
            };
            HTMLMediaElement.prototype.pause = function() {
                this.dispatchEvent(new Event('pause'));
            };
        })();
        """;

    private sealed class RecordingPlaybackCommands : IPlaybackCommandService, INewsCommandService
    {
        public ConcurrentQueue<AdvanceCall> AdvanceCalls { get; } = new();

        public Task<QueueEntry?> AdvanceAsync(TimeSpan? actualPosition, bool successful = true, CancellationToken cancellationToken = default)
        {
            AdvanceCalls.Enqueue(new AdvanceCall(actualPosition, successful));
            return Task.FromResult<QueueEntry?>(null);
        }

        public Task<QueueEntry> PlayNextAsync(int mediaId, TimeSpan? start = null, TimeSpan? duration = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<QueueEntry> PlayNowAsync(int mediaId, TimeSpan? currentPosition, TimeSpan? start = null, TimeSpan? duration = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<QueueEntry> PlayYouTubeNextAsync(string url, TimeSpan? start = null, TimeSpan? duration = null, TimeSpan? maximumDuration = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<QueueEntry> PlayYouTubeNowAsync(string url, TimeSpan? currentPosition, TimeSpan? start = null, TimeSpan? duration = null, TimeSpan? maximumDuration = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PrioritizeQueuedAsync(long queueEntryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<QueueEntry> PlayQueuedNowAsync(long queueEntryId, TimeSpan? currentPosition, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ShowNewsAsync(NewsItem item, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopNewsAsync(long? newsId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopTickerAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpPresenterControls : IPresenterControlService
    {
        public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HideAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record AdvanceCall(TimeSpan? Position, bool Successful);
}
