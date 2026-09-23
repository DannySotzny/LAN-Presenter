using System.Collections.Concurrent;
using System.Text.Json;
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
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> JavaScriptLineHits = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim CoverageFileGate = new(1, 1);
    private readonly RecordingPlaybackCommands _playbackCommands = new();
    private readonly RecordingNewsDisplayState _newsDisplayState = new();
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
        builder.Services.AddSingleton<INewsDisplayState>(_newsDisplayState);
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
        await using var coverage = new BrowserCoverageCapture(context);
        await context.RouteAsync("**/api/status", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = """{"presenterState":"coverage-state","browserConnected":true,"currentTitle":"coverage-title","position":"00:01:02","duration":"00:05:00","ffprobeAvailable":true,"mediaScannerRunning":false,"mediaScannerError":null}"""
        }));
        var page = await coverage.NewPageAsync();

        await page.GotoAsync($"{_baseAddress}/login");
        await page.FillAsync("#password", TestPassword);
        await page.Locator("form[action='/account/login'] button[type='submit']").ClickAsync();
        await page.WaitForURLAsync($"{_baseAddress}/");
        await WaitForTextAsync(page, "#dashboard-presenter-state", "coverage-state");
        Assert.Equal("Browser: Verbunden", await page.TextContentAsync("#dashboard-browser-state"));
        Assert.Equal("coverage-title", await page.TextContentAsync("#dashboard-current-title"));
        Assert.Equal("00:01:02 / 00:05:00", await page.TextContentAsync("#dashboard-position"));
        Assert.Equal("OK", await page.TextContentAsync("#dashboard-ffprobe"));
        Assert.Equal("Bereit", await page.TextContentAsync("#dashboard-scanner"));

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

        await page.GotoAsync($"{_baseAddress}/");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Url = $"{_baseAddress}/_content/BeamerPresenter.Web/js/dashboard.js"
        });
        await WaitForTextAsync(page, "#dashboard-presenter-state", "coverage-state");
    }

    [Fact]
    public async Task Ticker_started_before_presenter_connects_is_visible_over_idle_screen()
    {
        _newsDisplayState.Snapshot = new NewsDisplaySnapshot(null, new NewsItem
        {
            Id = 99,
            Title = "Turnier",
            Text = "Start in fünf Minuten",
            Mode = NewsMode.Ticker,
            Permanent = true
        });
        await using var context = await _browser!.NewContextAsync();
        await using var coverage = new BrowserCoverageCapture(context);
        var page = await coverage.NewPageAsync();

        await page.GotoAsync($"{_baseAddress}/presenter");
        await page.WaitForFunctionAsync("document.querySelector('#presenter-ticker').dataset.newsId === '99'");

        Assert.True(await page.Locator("#presenter-ticker").IsVisibleAsync());
        Assert.Equal("Turnier", await page.TextContentAsync("#presenter-ticker-title"));
        Assert.False(await page.Locator("#presenter-news").IsVisibleAsync());
        Assert.True(await page.EvaluateAsync<bool>("""
            () => {
                const ticker = document.querySelector('#presenter-ticker');
                const rect = ticker.getBoundingClientRect();
                return rect.height > 0 && rect.bottom === window.innerHeight &&
                    ticker.contains(document.elementFromPoint(window.innerWidth / 2, window.innerHeight - 20));
            }
            """));
    }

    [Fact]
    public async Task Presenter_handles_playback_news_reconnect_and_terminal_reports()
    {
        await using var context = await _browser!.NewContextAsync();
        await context.AddInitScriptAsync(MediaAndSocketTestDoubles);
        await using var coverage = new BrowserCoverageCapture(context);
        var page = await coverage.NewPageAsync();

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
    public async Task YouTube_metadata_always_downloads_single_video_and_queues_local_next()
    {
        await using var context = await _browser!.NewContextAsync();
        await using var coverage = new BrowserCoverageCapture(context);
        var phase = "downloading";
        string? intentBody = null;
        string? startBody = null;
        var downloadRequests = new List<string>();
        await context.RouteAsync("**/api/youtube/download**", async route =>
        {
            downloadRequests.Add(route.Request.Url + " " + route.Request.Method + " " + route.Request.PostData);
            if (route.Request.Url.EndsWith("/api/youtube/download", StringComparison.Ordinal) && route.Request.Method == "POST")
            {
                startBody = route.Request.PostData;
            }
            if (route.Request.Url.EndsWith("/intent", StringComparison.Ordinal))
            {
                intentBody = route.Request.PostData;
            }
            var mediaId = phase == "ready" ? "43" : "null";
            var body = $$"""{"videoId":"Es7F0h1DKGs","phase":"{{phase}}","mediaId":{{mediaId}},"error":null,"durationSeconds":319}""";
            await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = body });
        });
        var page = await coverage.NewPageAsync();
        await page.GotoAsync($"{_baseAddress}/login");
        await page.FillAsync("#password", TestPassword);
        await page.Locator("form[action='/account/login'] button[type='submit']").ClickAsync();
        await page.GotoAsync($"{_baseAddress}/playback");
        await page.FillAsync("#youtube-url", "https://www.youtube.com/watch?v=Es7F0h1DKGs&list=RDEs7F0h1DKGs&start_radio=1");
        await page.ClickAsync("#youtube-load-metadata");
        await WaitForTextAsync(page, "#youtube-metadata-status", "Video wird lokal geladen");
        Assert.Contains("url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DEs7F0h1DKGs", startBody);
        Assert.Null(await page.QuerySelectorAsync("#youtube-iframe-api"));
        await page.Locator(".youtube-actions button").First.ClickAsync();
        await page.WaitForFunctionAsync("document.querySelector('#youtube-metadata-status').textContent.includes('vorgemerkt')");
        Assert.True(intentBody?.Contains("action=next", StringComparison.Ordinal) == true,
            string.Join(" | ", downloadRequests));
        phase = "ready";
        await WaitForTextAsync(page, "#youtube-metadata-status", "Mediathek bereit");
        Assert.Equal("Dauer: 00:05:19", await page.Locator("#youtube-duration").InnerTextAsync());
        Assert.EndsWith("/media/43", await page.Locator("#youtube-preview video").GetAttributeAsync("src"));
    }

    [Fact]
    public async Task YouTube_play_now_without_metadata_click_still_starts_local_download()
    {
        await using var context = await _browser!.NewContextAsync();
        await using var coverage = new BrowserCoverageCapture(context);
        var requests = new List<string>();
        await context.RouteAsync("**/api/youtube/download**", async route =>
        {
            requests.Add(route.Request.Url + " " + route.Request.Method + " " + route.Request.PostData);
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = """{"videoId":"Es7F0h1DKGs","phase":"downloading","mediaId":null,"error":null,"durationSeconds":null}"""
            });
        });
        var page = await coverage.NewPageAsync();
        await page.GotoAsync($"{_baseAddress}/login");
        await page.FillAsync("#password", TestPassword);
        await page.Locator("form[action='/account/login'] button[type='submit']").ClickAsync();
        await page.GotoAsync($"{_baseAddress}/playback");
        await page.FillAsync("#youtube-url", "https://www.youtube.com/watch?v=Es7F0h1DKGs&list=RDEs7F0h1DKGs&start_radio=1");
        await page.Locator(".youtube-actions button").Last.ClickAsync();
        await page.WaitForFunctionAsync("document.querySelector('#youtube-metadata-status').textContent.includes('vorgemerkt')");

        Assert.Contains(requests, item => item.Contains("/api/youtube/download POST", StringComparison.Ordinal) &&
            item.Contains("Es7F0h1DKGs", StringComparison.Ordinal));
        Assert.Contains(requests, item => item.Contains("/intent POST", StringComparison.Ordinal) &&
            item.Contains("action=now", StringComparison.Ordinal));
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
                    FileName = "recent-video.mp4",
                    FullPath = Path.Combine(_dataDirectory, "recent-video.mp4"),
                    AddedAtUtc = DateTimeOffset.UtcNow.AddDays(-6),
                    IsAvailable = true,
                    ProbeStatus = MediaProbeStatus.Valid,
                    PlaybackStatus = MediaPlaybackStatus.Supported
                },
                new VideoAsset
                {
                    FileName = "old-video.mp4",
                    FullPath = Path.Combine(_dataDirectory, "old-video.mp4"),
                    AddedAtUtc = DateTimeOffset.UtcNow.AddDays(-8),
                    IsAvailable = true,
                    ProbeStatus = MediaProbeStatus.Valid,
                    PlaybackStatus = MediaPlaybackStatus.Supported
                });
            await database.SaveChangesAsync();
        }

        await using var context = await _browser!.NewContextAsync();
        await using var coverage = new BrowserCoverageCapture(context);
        var page = await coverage.NewPageAsync();
        await page.SetViewportSizeAsync(1920, 1080);
        await page.GotoAsync($"{_baseAddress}/login");
        await page.FillAsync("#password", TestPassword);
        await page.Locator("form[action='/account/login'] button[type='submit']").ClickAsync();
        await page.GotoAsync($"{_baseAddress}/media");

        Assert.Equal(1, await page.GetByText("Kürzlich geladen").CountAsync());
        var recentCard = page.Locator(".media-card").Filter(new LocatorFilterOptions { HasText = "recent-video.mp4" });
        Assert.Equal(1, await recentCard.GetByText("Kürzlich geladen").CountAsync());
        var preview = await recentCard.Locator(".media-preview").BoundingBoxAsync();
        Assert.NotNull(preview);
        Assert.InRange(preview.Width, 350, 385);
        Assert.InRange(preview.Height, 195, 220);
    }

    public async Task DisposeAsync()
    {
        await WriteJavaScriptCoverageAsync();

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

    private static async Task CollectJavaScriptCoverageAsync(ICDPSession cdp)
    {
        var response = await cdp.SendAsync("Profiler.takePreciseCoverage");
        if (response is not { } coverage || !coverage.TryGetProperty("result", out var scripts)) return;

        foreach (var script in scripts.EnumerateArray())
        {
            var scriptUrl = script.GetProperty("url").GetString() ?? string.Empty;
            var sourcePath = GetTrackedScriptPath(scriptUrl);
            if (sourcePath is null) continue;

            var source = await File.ReadAllTextAsync(sourcePath);
            var hits = JavaScriptLineHits.GetOrAdd(GetRelativeScriptPath(sourcePath),
                _ => new ConcurrentDictionary<int, byte>());
            var coverageRanges = new List<(int Start, int End, int Count)>();
            foreach (var function in script.GetProperty("functions").EnumerateArray())
            {
                foreach (var range in function.GetProperty("ranges").EnumerateArray())
                {
                    coverageRanges.Add((range.GetProperty("startOffset").GetInt32(),
                        range.GetProperty("endOffset").GetInt32(), range.GetProperty("count").GetInt32()));
                }
            }

            AddCoveredLines(source, coverageRanges, hits);
        }
    }

    private static string? GetTrackedScriptPath(string? scriptUrl)
    {
        if (string.IsNullOrWhiteSpace(scriptUrl) || !Uri.TryCreate(scriptUrl, UriKind.Absolute, out var uri)) return null;
        var relativePath = uri.AbsolutePath.TrimStart('/');
        if (relativePath.Contains('/'))
        {
            relativePath = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        }

        var projectPath = relativePath switch
        {
            "dashboard.js" => "src/BeamerPresenter.Web/wwwroot/js/dashboard.js",
            "media-previews.js" => "src/BeamerPresenter.Web/wwwroot/js/media-previews.js",
            "presenter.js" => "src/BeamerPresenter.Web/wwwroot/js/presenter.js",
            "youtube-management.js" => "src/BeamerPresenter.Web/wwwroot/js/youtube-management.js",
            _ => null
        };
        if (projectPath is null) return null;

        var fullPath = Path.GetFullPath(Path.Combine(RepositoryRoot, projectPath));
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static string GetRelativeScriptPath(string fullPath) =>
        Path.GetRelativePath(RepositoryRoot, fullPath).Replace('\\', '/');

    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                    (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git"))))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the repository root for JavaScript coverage.");
        }
    }

    private static void AddCoveredLines(string source, IReadOnlyList<(int Start, int End, int Count)> ranges,
        ConcurrentDictionary<int, byte> hits)
    {
        var lines = source.Split('\n');
        var lineStartOffset = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var lineEndOffset = lineStartOffset + lines[index].Length + (index < lines.Length - 1 ? 1 : 0);
            for (var offset = lineStartOffset; offset < lineEndOffset && offset < source.Length; offset++)
            {
                if (char.IsWhiteSpace(source[offset])) continue;
                var mostSpecific = ranges
                    .Where(range => offset >= range.Start && offset < range.End)
                    .OrderBy(range => range.End - range.Start)
                    .FirstOrDefault();
                if (mostSpecific.End > mostSpecific.Start && mostSpecific.Count > 0)
                {
                    hits.TryAdd(index + 1, 0);
                    break;
                }
            }

            lineStartOffset = lineEndOffset;
            if (lineStartOffset >= source.Length) break;
        }
    }

    private static async Task WriteJavaScriptCoverageAsync()
    {
        var outputPath = Environment.GetEnvironmentVariable("SONAR_JAVASCRIPT_LCOV");
        if (string.IsNullOrWhiteSpace(outputPath)) return;

        await CoverageFileGate.WaitAsync();
        try
        {
            var resolvedPath = Path.GetFullPath(outputPath, RepositoryRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);
            var report = new List<string>();
            foreach (var (relativePath, coveredLines) in JavaScriptLineHits.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var sourcePath = Path.GetFullPath(Path.Combine(RepositoryRoot, relativePath));
                var totalLines = await File.ReadAllLinesAsync(sourcePath);
                report.Add($"SF:{relativePath}");
                var coveredCount = 0;
                for (var line = 1; line <= totalLines.Length; line++)
                {
                    var hits = coveredLines.ContainsKey(line) ? 1 : 0;
                    coveredCount += hits;
                    report.Add($"DA:{line},{hits}");
                }

                report.Add($"LF:{totalLines.Length}");
                report.Add($"LH:{coveredCount}");
                report.Add("end_of_record");
            }

            await File.WriteAllLinesAsync(resolvedPath, report);
            Console.WriteLine($"Wrote browser JavaScript coverage for {JavaScriptLineHits.Count} scripts to {resolvedPath}.");
        }
        finally
        {
            CoverageFileGate.Release();
        }
    }

    private sealed class BrowserCoverageCapture(IBrowserContext context) : IAsyncDisposable
    {
        private readonly List<ICDPSession> sessions = [];
        private readonly List<Task> collectors = [];
        private readonly CancellationTokenSource stopping = new();

        public async Task<IPage> NewPageAsync()
        {
            var page = await context.NewPageAsync();
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Profiler.enable");
            await cdp.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object>
            {
                ["callCount"] = false,
                ["detailed"] = true
            });
            sessions.Add(cdp);
            collectors.Add(PollJavaScriptCoverageAsync(cdp, stopping.Token));
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            try
            {
                await Task.WhenAll(collectors);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Each page collector is expected to stop with its test context.
            }

            foreach (var session in sessions)
            {
                try
                {
                    await CollectJavaScriptCoverageAsync(session);
                }
                catch (PlaywrightException exception) when (IsTargetClosed(exception))
                {
                    // The periodic collector already captured this page before its context closed.
                }
                finally
                {
                    try
                    {
                        await session.DetachAsync();
                    }
                    catch (PlaywrightException exception) when (IsTargetClosed(exception))
                    {
                        // Chromium detaches CDP sessions when a page closes.
                    }
                }
            }

            await WriteJavaScriptCoverageAsync();
            stopping.Dispose();
        }

        private static async Task PollJavaScriptCoverageAsync(ICDPSession session, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                try
                {
                    await CollectJavaScriptCoverageAsync(session);
                }
                catch (PlaywrightException exception) when (IsTargetClosed(exception))
                {
                    return;
                }
            }
        }

        private static bool IsTargetClosed(PlaywrightException exception) =>
            exception.Message.Contains("Target page, context or browser has been closed", StringComparison.Ordinal);
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

    private sealed class RecordingNewsDisplayState : INewsDisplayState
    {
        public NewsDisplaySnapshot Snapshot { get; set; } = new(null, null);

        public Task<NewsDisplaySnapshot> GetNewsDisplayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);
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
