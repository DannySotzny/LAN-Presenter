using System.Net;
using System.Net.Http.Headers;
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
