using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using BeamerPresenter.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Web.Tests;

public sealed class AuthenticatedWebRouteTests : IAsyncLifetime
{
    private const string TestPassword = "integration-test-password";
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingPlaybackCommands _playbackCommands = new();
    private WebApplication? _application;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddPresenterInfrastructure(_dataDirectory);
        builder.Services.AddPresenterWebUi();
        builder.Services.AddSingleton<IPlaybackCommandService>(_playbackCommands);
        builder.Services.AddSingleton<INewsCommandService>(_playbackCommands);

        _application = builder.Build();
        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            await context.Database.MigrateAsync();
        }

        _application.UseAuthentication();
        _application.UseAuthorization();
        _application.UseAntiforgery();
        _application.MapPresenterWebUi();
        await _application.StartAsync();

        var settings = _application.Services.GetRequiredService<IPresenterSettingsService>();
        await settings.SetWebPasswordAsync(TestPassword);
    }

    [Fact]
    public async Task Valid_login_renders_management_page()
    {
        using var client = _application!.GetTestClient();
        var cookie = await LoginAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", cookie);
        using var pageResponse = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Contains("Videobibliothek", await pageResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Wiedergabe-Queue", await pageResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("YouTube einreihen", await pageResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("News &amp; Einblendungen", await pageResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Media_search_filters_library_and_marks_playback_status()
    {
        await using (var scope = _application!.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            context.Videos.AddRange(
                CreateVideo("arena-final.mp4", "h264", MediaPlaybackStatus.Supported),
                CreateVideo("retro-demo.mkv", "hevc", MediaPlaybackStatus.Unsupported));
            await context.SaveChangesAsync();
        }

        using var client = _application.GetTestClient();
        var cookie = await LoginAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/?q=h264");
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("arena-final.mp4", html, StringComparison.Ordinal);
        Assert.Contains("Bereit", html, StringComparison.Ordinal);
        Assert.Contains("Als Nächstes", html, StringComparison.Ordinal);
        Assert.DoesNotContain("retro-demo.mkv", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_form_upload_redirects_to_success_message()
    {
        var uploadDirectory = Path.Combine(_dataDirectory, "Uploads");
        Directory.CreateDirectory(uploadDirectory);
        await _application!.Services.GetRequiredService<IMediaFolderService>().AddAsync(uploadDirectory, includeSubdirectories: false);
        using var client = _application.GetTestClient();
        var cookie = await LoginAsync(client);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("/"), "returnUrl");
        var videoContent = new ByteArrayContent([0, 1, 2, 3]);
        videoContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(videoContent, "video", "uploaded-clip.mp4");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/videos/upload") { Content = form };
        request.Headers.Add("Cookie", cookie);

        using var uploadResponse = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, uploadResponse.StatusCode);
        Assert.Equal("/?upload=success", uploadResponse.Headers.Location?.OriginalString);
        Assert.True(File.Exists(Path.Combine(uploadDirectory, "uploaded-clip.mp4")));
    }

    [Fact]
    public async Task Authenticated_queue_action_accepts_manual_segment()
    {
        using var client = _application!.GetTestClient();
        var cookie = await LoginAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/queue/next")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["mediaId"] = "42",
                ["start"] = "01:20:00",
                ["duration"] = "00:08:00"
            })
        };
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?queue=success", response.Headers.Location?.OriginalString);
        var command = Assert.Single(_playbackCommands.NextCalls);
        Assert.Equal(42, command.MediaId);
        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(20), command.Start);
        Assert.Equal(TimeSpan.FromMinutes(8), command.Duration);
    }

    [Theory]
    [InlineData("/api/queue/now")]
    [InlineData("/api/youtube/now")]
    [InlineData("/api/news/show")]
    [InlineData("/api/news/delete")]
    public async Task Management_actions_reject_unauthenticated_requests(string endpoint)
    {
        using var client = _application!.GetTestClient();
        using var response = await client.PostAsync(
            endpoint,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["mediaId"] = "42" }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
        Assert.Empty(_playbackCommands.NextCalls);
        Assert.Empty(_playbackCommands.NowCalls);
        Assert.Empty(_playbackCommands.YouTubeNowCalls);
        Assert.Empty(_playbackCommands.ShownNews);
        Assert.Equal(0, _playbackCommands.StopNewsCalls);
    }

    [Fact]
    public async Task Authenticated_youtube_action_passes_bounded_playback_request()
    {
        using var client = _application!.GetTestClient();
        var cookie = await LoginAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/youtube/next")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["url"] = "https://youtu.be/dQw4w9WgXcQ",
                ["maximumDuration"] = "00:10:00"
            })
        };
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?youtube=success", response.Headers.Location?.OriginalString);
        var command = Assert.Single(_playbackCommands.YouTubeNextCalls);
        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", command.Url);
        Assert.Equal(TimeSpan.FromMinutes(10), command.MaximumDuration);
    }

    [Fact]
    public async Task Authenticated_news_can_be_created_and_shown()
    {
        using var client = _application!.GetTestClient();
        var cookie = await LoginAsync(client);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/news/create")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["title"] = "CS2 5on5",
                ["text"] = "Start um 20 Uhr",
                ["mode"] = "Ticker",
                ["duration"] = "00:05:00",
                ["priority"] = "3"
            })
        };
        createRequest.Headers.Add("Cookie", cookie);
        using var createResponse = await client.SendAsync(createRequest);
        Assert.Equal("/?news=created", createResponse.Headers.Location?.OriginalString);
        var item = Assert.Single(await _application!.Services.GetRequiredService<INewsService>().GetAllAsync());

        using var showRequest = new HttpRequestMessage(HttpMethod.Post, "/api/news/show")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        };
        showRequest.Headers.Add("Cookie", cookie);
        using var showResponse = await client.SendAsync(showRequest);

        Assert.Equal("/?news=shown", showResponse.Headers.Location?.OriginalString);
        Assert.Equal(item.Id, Assert.Single(_playbackCommands.ShownNews).Id);
    }

    [Fact]
    public async Task Media_endpoint_supports_ranges_and_rejects_paths_outside_configured_folders()
    {
        var mediaDirectory = Path.Combine(_dataDirectory, "Media");
        var outsideDirectory = Path.Combine(_dataDirectory, "Outside");
        Directory.CreateDirectory(mediaDirectory);
        Directory.CreateDirectory(outsideDirectory);
        var mediaPath = Path.Combine(mediaDirectory, "range-test.mp4");
        var outsidePath = Path.Combine(outsideDirectory, "outside.mp4");
        await File.WriteAllBytesAsync(mediaPath, Enumerable.Range(0, 16).Select(value => (byte)value).ToArray());
        await File.WriteAllBytesAsync(outsidePath, [10, 11, 12]);
        await _application!.Services.GetRequiredService<IMediaFolderService>().AddAsync(mediaDirectory, includeSubdirectories: false);

        int mediaId;
        int outsideId;
        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PresenterDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            var media = CreateVideo("range-test.mp4", "h264", MediaPlaybackStatus.Supported);
            media.FullPath = mediaPath;
            media.FileSize = 16;
            var outside = CreateVideo("outside.mp4", "h264", MediaPlaybackStatus.Supported);
            outside.FullPath = outsidePath;
            outside.FileSize = 3;
            context.Videos.AddRange(media, outside);
            await context.SaveChangesAsync();
            mediaId = media.Id;
            outsideId = outside.Id;
        }

        using var client = _application.GetTestClient();
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/media/{mediaId}");
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
        using var rangeResponse = await client.SendAsync(rangeRequest);

        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal("video/mp4", rangeResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes 2-5/16", rangeResponse.Content.Headers.ContentRange?.ToString());
        Assert.Equal([2, 3, 4, 5], await rangeResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("bytes", rangeResponse.Headers.AcceptRanges);

        using var outsideResponse = await client.GetAsync($"/media/{outsideId}");
        Assert.Equal(HttpStatusCode.NotFound, outsideResponse.StatusCode);
    }

    [Fact]
    public async Task Presenter_page_connects_to_dedicated_signalr_hub_and_reports_status()
    {
        var application = _application!;
        using var client = application.GetTestClient();
        using var pageResponse = await client.GetAsync("/presenter");
        var pageHtml = await pageResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Contains("presenter-video", pageHtml, StringComparison.Ordinal);
        Assert.Contains("presenter-youtube-host", pageHtml, StringComparison.Ordinal);
        Assert.Contains("presenter-news", pageHtml, StringComparison.Ordinal);
        Assert.Contains("js/presenter.js", pageHtml, StringComparison.Ordinal);

        using var negotiateResponse = await client.PostAsync("/hubs/presenter/negotiate?negotiateVersion=1", content: null);
        Assert.Equal(HttpStatusCode.OK, negotiateResponse.StatusCode);
        using var negotiation = JsonDocument.Parse(await negotiateResponse.Content.ReadAsStringAsync());
        var connectionToken = negotiation.RootElement.GetProperty("connectionToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionToken));
        Assert.Contains(
            negotiation.RootElement.GetProperty("availableTransports").EnumerateArray(),
            transport => transport.GetProperty("transport").GetString() == "WebSockets");

        var webSocketClient = application.GetTestServer().CreateWebSocketClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = await webSocketClient.ConnectAsync(
            new Uri($"ws://localhost/hubs/presenter?id={Uri.EscapeDataString(connectionToken!)}"),
            cancellation.Token);
        await SendSignalRMessageAsync(socket, "{\"protocol\":\"json\",\"version\":1}", cancellation.Token);
        var handshake = await ReceiveSignalRMessageAsync(socket, cancellation.Token);
        Assert.Equal("{}", handshake);

        await SendSignalRMessageAsync(
            socket,
            "{\"type\":1,\"target\":\"ReportStatus\",\"arguments\":[\"Ready\",12.5,90.0,null]}",
            cancellation.Token);
        var connectionState = application.Services.GetRequiredService<PresenterConnectionState>();
        await WaitForPresenterStatusAsync(connectionState, "Ready", cancellation.Token);
        Assert.True(connectionState.IsConnected);
        Assert.Equal(12.5, connectionState.LatestReport.PositionSeconds);

        var gateway = application.Services.GetRequiredService<IPresenterGateway>();
        await gateway.LoadYouTubeVideoAsync(
            "dQw4w9WgXcQ",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            autoPlay: true,
            cancellation.Token);
        var youtubeInvocation = await ReceiveSignalRMessageAsync(socket, cancellation.Token);
        Assert.Contains("\"target\":\"LoadYouTubeVideo\"", youtubeInvocation, StringComparison.Ordinal);
        Assert.Contains("dQw4w9WgXcQ", youtubeInvocation, StringComparison.Ordinal);

        await gateway.ShowNewsAsync(new NewsItem
        {
            Id = 7,
            Title = "Turnierstart",
            Text = "CS2 5on5 beginnt jetzt",
            Mode = NewsMode.Fullscreen,
            Duration = TimeSpan.FromMinutes(5),
            Priority = 10
        }, cancellation.Token);
        var newsInvocation = await ReceiveSignalRMessageAsync(socket, cancellation.Token);
        Assert.Contains("\"target\":\"ShowNews\"", newsInvocation, StringComparison.Ordinal);
        Assert.Contains("Fullscreen", newsInvocation, StringComparison.Ordinal);
        await gateway.HideNewsAsync(cancellation.Token);
        Assert.Contains("\"target\":\"HideNews\"", await ReceiveSignalRMessageAsync(socket, cancellation.Token), StringComparison.Ordinal);

        await SendSignalRMessageAsync(
            socket,
            "{\"type\":1,\"invocationId\":\"ended-1\",\"target\":\"ReportStatus\",\"arguments\":[\"Ended\",89.5,90.0,null]}",
            cancellation.Token);
        Assert.Contains("\"invocationId\":\"ended-1\"", await ReceiveSignalRMessageAsync(socket, cancellation.Token), StringComparison.Ordinal);
        await SendSignalRMessageAsync(
            socket,
            "{\"type\":1,\"invocationId\":\"ended-2\",\"target\":\"ReportStatus\",\"arguments\":[\"Ended\",89.5,90.0,null]}",
            cancellation.Token);
        Assert.Contains("\"invocationId\":\"ended-2\"", await ReceiveSignalRMessageAsync(socket, cancellation.Token), StringComparison.Ordinal);
        await _playbackCommands.Advanced.Task.WaitAsync(cancellation.Token);
        var advance = Assert.Single(_playbackCommands.AdvanceCalls);
        Assert.Equal(TimeSpan.FromSeconds(89.5), advance.Position);
        Assert.True(advance.Successful);

        await SendSignalRMessageAsync(
            socket,
            "{\"type\":1,\"target\":\"ReportStatus\",\"arguments\":[\"Ready\",0.0,600.0,null]}",
            cancellation.Token);
        await WaitForPresenterStatusAsync(connectionState, "Ready", cancellation.Token);
        await SendSignalRMessageAsync(
            socket,
            "{\"type\":1,\"invocationId\":\"error-1\",\"target\":\"ReportStatus\",\"arguments\":[\"Error\",4.0,600.0,\"offline\"]}",
            cancellation.Token);
        Assert.Contains("\"invocationId\":\"error-1\"", await ReceiveSignalRMessageAsync(socket, cancellation.Token), StringComparison.Ordinal);
        Assert.Equal(2, _playbackCommands.AdvanceCalls.Count);
        Assert.False(_playbackCommands.AdvanceCalls[1].Successful);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test completed", cancellation.Token);
    }

    private static VideoAsset CreateVideo(string fileName, string videoCodec, MediaPlaybackStatus playbackStatus) => new()
    {
        FileName = fileName,
        FullPath = $"D:\\Videos\\{fileName}",
        FileSize = 42_000_000,
        AddedAtUtc = DateTimeOffset.UtcNow,
        LastWriteUtc = DateTimeOffset.UtcNow,
        LastScannedUtc = DateTimeOffset.UtcNow,
        IsAvailable = true,
        Duration = TimeSpan.FromMinutes(3),
        Container = Path.GetExtension(fileName).TrimStart('.'),
        VideoCodec = videoCodec,
        VideoWidth = 1920,
        VideoHeight = 1080,
        AudioCodec = "aac",
        AudioChannels = 2,
        ProbeStatus = MediaProbeStatus.Valid,
        PlaybackStatus = playbackStatus
    };

    private static async Task<string> LoginAsync(HttpClient client)
    {
        using var loginResponse = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = TestPassword,
            ["returnUrl"] = "/"
        }));

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("/", loginResponse.Headers.Location?.OriginalString);
        return Assert.Single(loginResponse.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
    }

    private static async Task SendSignalRMessageAsync(WebSocket socket, string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message + '\u001e');
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<string> ReceiveSignalRMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, result.Count).TrimEnd('\u001e');
    }

    private static async Task WaitForPresenterStatusAsync(
        PresenterConnectionState connectionState,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        while (!string.Equals(connectionState.LatestReport.Status, expectedStatus, StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private sealed class RecordingPlaybackCommands : IPlaybackCommandService, INewsCommandService
    {
        public List<QueueCommand> NextCalls { get; } = [];
        public List<QueueCommand> NowCalls { get; } = [];
        public List<YouTubeCommand> YouTubeNextCalls { get; } = [];
        public List<YouTubeCommand> YouTubeNowCalls { get; } = [];
        public List<AdvanceCommand> AdvanceCalls { get; } = [];
        public TaskCompletionSource Advanced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<NewsItem> ShownNews { get; } = [];
        public int StopNewsCalls { get; private set; }

        public Task<QueueEntry> PlayNextAsync(int mediaId, TimeSpan? start = null, TimeSpan? duration = null, CancellationToken cancellationToken = default)
        {
            NextCalls.Add(new QueueCommand(mediaId, start, duration));
            return Task.FromResult(new QueueEntry { MediaId = mediaId, SourceType = MediaSourceType.Local });
        }

        public Task<QueueEntry> PlayNowAsync(int mediaId, TimeSpan? currentPosition, TimeSpan? start = null, TimeSpan? duration = null, CancellationToken cancellationToken = default)
        {
            NowCalls.Add(new QueueCommand(mediaId, start, duration));
            return Task.FromResult(new QueueEntry { MediaId = mediaId, SourceType = MediaSourceType.Local });
        }

        public Task<QueueEntry> PlayYouTubeNextAsync(string url, TimeSpan? start = null, TimeSpan? duration = null, TimeSpan? maximumDuration = null, CancellationToken cancellationToken = default)
        {
            YouTubeNextCalls.Add(new YouTubeCommand(url, start, duration, maximumDuration));
            return Task.FromResult(new QueueEntry { ExternalSourceKey = "youtube:dQw4w9WgXcQ", SourceType = MediaSourceType.YouTube });
        }

        public Task<QueueEntry> PlayYouTubeNowAsync(string url, TimeSpan? currentPosition, TimeSpan? start = null, TimeSpan? duration = null, TimeSpan? maximumDuration = null, CancellationToken cancellationToken = default)
        {
            YouTubeNowCalls.Add(new YouTubeCommand(url, start, duration, maximumDuration));
            return Task.FromResult(new QueueEntry { ExternalSourceKey = "youtube:dQw4w9WgXcQ", SourceType = MediaSourceType.YouTube });
        }

        public Task<QueueEntry?> AdvanceAsync(TimeSpan? actualPosition, bool successful = true, CancellationToken cancellationToken = default)
        {
            AdvanceCalls.Add(new AdvanceCommand(actualPosition, successful));
            Advanced.TrySetResult();
            return Task.FromResult<QueueEntry?>(null);
        }

        public Task ShowNewsAsync(NewsItem item, CancellationToken cancellationToken = default)
        {
            ShownNews.Add(item);
            return Task.CompletedTask;
        }

        public Task StopNewsAsync(long? newsId = null, CancellationToken cancellationToken = default)
        {
            StopNewsCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed record QueueCommand(int MediaId, TimeSpan? Start, TimeSpan? Duration);
    private sealed record YouTubeCommand(string Url, TimeSpan? Start, TimeSpan? Duration, TimeSpan? MaximumDuration);
    private sealed record AdvanceCommand(TimeSpan? Position, bool Successful);

    public async Task DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BeamerPresenter.Tests"));
        var dataDirectory = Path.GetFullPath(_dataDirectory);
        if (dataDirectory.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
