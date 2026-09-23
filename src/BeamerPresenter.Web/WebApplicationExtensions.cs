using System.Security.Claims;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BeamerPresenter.Web;

public static class WebApplicationExtensions
{
    private const string DashboardPath = "/";
    private const string MediaPath = "/media";
    private const string NewsPath = "/news";
    private const string PlaybackPath = "/playback";
    private const int ClientClosedRequestStatusCode = 499;

    public static IServiceCollection AddPresenterWebUi(this IServiceCollection services)
    {
        services.AddRazorComponents();
        services.AddMudServices();
        services.AddSignalR();
        services.AddSingleton<PresenterConnectionState>();
        services.AddSingleton<IPresenterTelemetry>(provider => provider.GetRequiredService<PresenterConnectionState>());
        services.AddSingleton<IPresenterGateway, SignalRPresenterGateway>();
        services.AddSingleton<PresenterDashboardService>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.LoginPath = "/login"; options.Cookie.Name = "BeamerPresenter.Auth"; options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax; options.SlidingExpiration = true; options.ExpireTimeSpan = TimeSpan.FromHours(12);
        });
        services.AddAuthorization(); return services;
    }

    public static IEndpointRouteBuilder MapPresenterWebUi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/login", (Delegate)LoginAsync).AllowAnonymous(); app.MapPost("/account/logout", (Delegate)LogoutAsync).RequireAuthorization();
        app.MapPost("/api/videos/upload", (Delegate)UploadAsync).RequireAuthorization();
        app.MapPost("/api/videos/set-enabled", (Delegate)SetVideoEnabledAsync).RequireAuthorization();
        app.MapPost("/api/videos/reanalyze", (Delegate)ReanalyzeVideoAsync).RequireAuthorization();
        app.MapGet("/api/videos/{mediaId:int}/preview", (Delegate)GetMediaPreviewAsync).RequireAuthorization();
        app.MapPost("/api/queue/next", (Delegate)PlayNextAsync).RequireAuthorization();
        app.MapPost("/api/queue/now", (Delegate)PlayNowAsync).RequireAuthorization();
        app.MapPost("/api/queue/move-up", (Delegate)MoveQueueUpAsync).RequireAuthorization();
        app.MapPost("/api/queue/move-down", (Delegate)MoveQueueDownAsync).RequireAuthorization();
        app.MapPost("/api/queue/remove", (Delegate)RemoveQueueEntryAsync).RequireAuthorization();
        app.MapPost("/api/queue/play-next", (Delegate)PlayQueuedNextAsync).RequireAuthorization();
        app.MapPost("/api/queue/play-now", (Delegate)PlayQueuedNowAsync).RequireAuthorization();
        app.MapPost("/api/queue/regenerate", (Delegate)RegenerateQueueAsync).RequireAuthorization();
        app.MapPost("/api/history/clear", (Delegate)ClearHistoryAsync).RequireAuthorization();
        app.MapGet("/api/youtube/reference", (Delegate)GetYouTubeReference).RequireAuthorization();
        app.MapPost("/api/youtube/download", (Delegate)StartYouTubeDownloadAsync).RequireAuthorization();
        app.MapGet("/api/youtube/download/{videoId}", (Delegate)GetYouTubeDownloadAsync).RequireAuthorization();
        app.MapPost("/api/youtube/download/{videoId}/intent", (Delegate)SetYouTubeDownloadIntentAsync).RequireAuthorization();
        app.MapPost("/api/youtube/next", (Delegate)PlayYouTubeNextAsync).RequireAuthorization();
        app.MapPost("/api/youtube/now", (Delegate)PlayYouTubeNowAsync).RequireAuthorization();
        app.MapPost("/api/news/create", (Delegate)CreateNewsAsync).RequireAuthorization();
        app.MapPost("/api/news/show", (Delegate)ShowNewsAsync).RequireAuthorization();
        app.MapPost("/api/news/stop", (Delegate)StopNewsAsync).RequireAuthorization();
        app.MapPost("/api/news/stop-ticker", (Delegate)StopTickerAsync).RequireAuthorization();
        app.MapPost("/api/news/delete", (Delegate)DeleteNewsAsync).RequireAuthorization();
        app.MapPost("/api/presenter/activate", (Delegate)ActivatePresenterAsync).RequireAuthorization();
        app.MapPost("/api/presenter/pause", (Delegate)PausePresenterAsync).RequireAuthorization();
        app.MapPost("/api/presenter/hide", (Delegate)HidePresenterAsync).RequireAuthorization();
        app.MapPost("/api/presenter/stop", (Delegate)StopPresenterAsync).RequireAuthorization();
        app.MapGet("/api/status", (Delegate)GetStatusAsync).RequireAuthorization();
        app.MapGet("/health/details", (Delegate)GetStatusAsync).RequireAuthorization();
        app.MapGet("/health", (Delegate)GetHealthAsync).AllowAnonymous();
        app.MapGet("/media/{mediaId:int}", (Delegate)StreamMediaAsync).AllowAnonymous();
        app.MapHub<PresenterHub>("/hubs/presenter");
        app.MapRazorComponents<PresenterWebApp>(); return app;
    }

    private static async Task<IResult> GetStatusAsync(
        PresenterDashboardService dashboard,
        CancellationToken cancellationToken) =>
        Results.Ok(await dashboard.GetAsync(cancellationToken));

    private static async Task<IResult> GetHealthAsync(
        PresenterDashboardService dashboard,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await dashboard.GetAsync(cancellationToken);
            return Results.Ok(new
            {
                status = "ok",
                database = "ok",
                presenter = status.PresenterState,
                generatedUtc = status.GeneratedUtc
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Json(new { status = "unhealthy", database = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<IResult> ActivatePresenterAsync(
        IPresenterControlService presenter,
        CancellationToken cancellationToken) =>
        ExecutePresenterCommandAsync(presenter.ActivateAsync, "active", cancellationToken);

    private static Task<IResult> PausePresenterAsync(
        IPresenterControlService presenter,
        CancellationToken cancellationToken) =>
        ExecutePresenterCommandAsync(presenter.PauseAsync, "paused", cancellationToken);

    private static Task<IResult> HidePresenterAsync(
        IPresenterControlService presenter,
        CancellationToken cancellationToken) =>
        ExecutePresenterCommandAsync(presenter.HideAsync, "hidden", cancellationToken);

    private static Task<IResult> StopPresenterAsync(
        IPresenterControlService presenter,
        CancellationToken cancellationToken) =>
        ExecutePresenterCommandAsync(presenter.StopAsync, "stopped", cancellationToken);

    private static async Task<IResult> ExecutePresenterCommandAsync(
        Func<CancellationToken, Task> command,
        string result,
        CancellationToken cancellationToken)
    {
        try
        {
            await command(cancellationToken);
            return Results.Redirect($"{DashboardPath}?presenter={result}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException)
        {
            return Results.Redirect($"{DashboardPath}?presenter=error&message={Uri.EscapeDataString(exception.Message)}");
        }
    }

    private static async Task<IResult> LoginAsync(HttpContext context, IPresenterSettingsService settingsService, CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!await settingsService.VerifyWebPasswordAsync(form["password"].ToString(), cancellationToken)) return Results.Redirect("/login?error=1");
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Presenter Admin")], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        var returnUrl = form["returnUrl"].ToString(); return Results.Redirect(IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
    private static async Task<IResult> LogoutAsync(HttpContext context) { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.Redirect("/login"); }
    private static async Task<IResult> UploadAsync(HttpContext context, IMediaLibraryService mediaLibraryService, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "Bitte eine multipart/form-data-Anfrage senden." });
        var form = await context.Request.ReadFormAsync(cancellationToken); var video = form.Files.GetFile("video");
        if (video is null || video.Length == 0) return Results.BadRequest(new { error = "Es wurde keine Videodatei ausgewählt." });
        var returnUrl = form["returnUrl"].ToString();
        try
        {
            await using var stream = video.OpenReadStream();
            var asset = await mediaLibraryService.AddUploadAsync(video.FileName, stream, video.Length, cancellationToken);
            return IsLocalUrl(returnUrl)
                ? Results.Redirect($"{returnUrl}?upload=success")
                : Results.Ok(new { asset.Id, asset.FileName, asset.FileSize });
        }
        catch (InvalidOperationException exception)
        {
            return IsLocalUrl(returnUrl)
                ? Results.Redirect($"{returnUrl}?upload=error&message={Uri.EscapeDataString(exception.Message)}")
                : Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> SetVideoEnabledAsync(
        HttpContext context,
        IMediaLibraryService mediaLibrary,
        CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!int.TryParse(form["id"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ||
            !bool.TryParse(form["enabled"], out var enabled))
        {
            return Results.Redirect($"{MediaPath}?media=error&message=Ungültige%20Videoaktion");
        }

        return await ExecuteMediaCommandAsync(
            () => mediaLibrary.SetEnabledAsync(id, enabled, cancellationToken),
            enabled ? "enabled" : "disabled");
    }

    private static async Task<IResult> ReanalyzeVideoAsync(
        HttpContext context,
        IMediaLibraryService mediaLibrary,
        CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!int.TryParse(form["id"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return Results.Redirect($"{MediaPath}?media=error&message=Ungültiges%20Video");
        }

        return await ExecuteMediaCommandAsync(
            () => mediaLibrary.ReanalyzeAsync(id, cancellationToken),
            "reanalyzing");
    }

    private static async Task<IResult> ExecuteMediaCommandAsync(Func<Task> command, string result)
    {
        try
        {
            await command();
            return Results.Redirect($"{MediaPath}?media={result}");
        }
        catch (InvalidOperationException exception)
        {
            return Results.Redirect($"{MediaPath}?media=error&message={Uri.EscapeDataString(exception.Message)}");
        }
    }

    private static async Task<IResult> PlayNextAsync(
        HttpContext context,
        IPlaybackCommandService playback,
        CancellationToken cancellationToken) =>
        await ExecuteQueueCommandAsync(context, (mediaId, start, duration) =>
            playback.PlayNextAsync(mediaId, start, duration, cancellationToken));

    private static Task<IResult> MoveQueueUpAsync(
        HttpContext context,
        PlaybackQueueService queue,
        CancellationToken cancellationToken) =>
        ExecuteQueueManagementAsync(context, id => queue.MoveAsync(id, -1, cancellationToken));

    private static Task<IResult> MoveQueueDownAsync(
        HttpContext context,
        PlaybackQueueService queue,
        CancellationToken cancellationToken) =>
        ExecuteQueueManagementAsync(context, id => queue.MoveAsync(id, 1, cancellationToken));

    private static Task<IResult> RemoveQueueEntryAsync(
        HttpContext context,
        PlaybackQueueService queue,
        CancellationToken cancellationToken) =>
        ExecuteQueueManagementAsync(context, id => queue.RemoveAsync(id, cancellationToken));

    private static Task<IResult> PlayQueuedNextAsync(
        HttpContext context,
        IPlaybackCommandService playback,
        CancellationToken cancellationToken) =>
        ExecuteQueueManagementAsync(context, id => playback.PrioritizeQueuedAsync(id, cancellationToken));

    private static Task<IResult> PlayQueuedNowAsync(
        HttpContext context,
        IPlaybackCommandService playback,
        PresenterConnectionState presenterState,
        CancellationToken cancellationToken)
    {
        TimeSpan? currentPosition = presenterState.LatestReport.PositionSeconds is >= 0
            ? TimeSpan.FromSeconds(presenterState.LatestReport.PositionSeconds.Value)
            : null;
        return ExecuteQueueManagementAsync(
            context,
            async id => { await playback.PlayQueuedNowAsync(id, currentPosition, cancellationToken); });
    }

    private static async Task<IResult> RegenerateQueueAsync(
        PlaybackQueueService queue,
        CancellationToken cancellationToken)
    {
        await queue.RegenerateAsync(cancellationToken);
        return Results.Redirect($"{PlaybackPath}?queue=success");
    }

    private static async Task<IResult> ClearHistoryAsync(
        PlaybackQueueService queue,
        CancellationToken cancellationToken)
    {
        await queue.ClearHistoryAsync(cancellationToken);
        return Results.Redirect($"{PlaybackPath}?history=cleared");
    }

    private static async Task<IResult> ExecuteQueueManagementAsync(
        HttpContext context,
        Func<long, Task> command)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!long.TryParse(form["id"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return Results.Redirect($"{PlaybackPath}?queue=error&message=Ungültiger%20Queue-Eintrag");
        }

        try
        {
            await command(id);
            return Results.Redirect($"{PlaybackPath}?queue=success");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return Results.Redirect($"{PlaybackPath}?queue=error&message={Uri.EscapeDataString(exception.Message)}");
        }
    }

    private static async Task<IResult> PlayNowAsync(
        HttpContext context,
        IPlaybackCommandService playback,
        PresenterConnectionState presenterState,
        CancellationToken cancellationToken)
    {
        TimeSpan? currentPosition = presenterState.LatestReport.PositionSeconds is >= 0
            ? TimeSpan.FromSeconds(presenterState.LatestReport.PositionSeconds.Value)
            : null;
        return await ExecuteQueueCommandAsync(context, (mediaId, start, duration) =>
            playback.PlayNowAsync(mediaId, currentPosition, start, duration, cancellationToken));
    }

    private static async Task<IResult> PlayYouTubeNextAsync(
        HttpContext context,
        YouTubeDownloadCoordinator downloads,
        CancellationToken cancellationToken) =>
        await ExecuteYouTubeCommandAsync(context, downloads, YouTubeDownloadAction.Next, cancellationToken);

    private static IResult GetYouTubeReference(string? url)
    {
        if (!YouTubeUrlParser.TryParse(url, out var reference) || reference is null)
        {
            return Results.BadRequest(new { error = "Die angegebene URL ist kein unterstützter YouTube-Link." });
        }

        return Results.Ok(new
        {
            reference.VideoId,
            reference.SourceKey,
            reference.CanonicalUrl
        });
    }

    private static async Task<IResult> PlayYouTubeNowAsync(
        HttpContext context,
        YouTubeDownloadCoordinator downloads,
        CancellationToken cancellationToken) =>
        await ExecuteYouTubeCommandAsync(context, downloads, YouTubeDownloadAction.Now, cancellationToken);

    private static async Task<IResult> ExecuteQueueCommandAsync(
        HttpContext context,
        Func<int, TimeSpan?, TimeSpan?, Task<QueueEntry>> command)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!int.TryParse(form["mediaId"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mediaId))
        {
            return Results.Redirect($"{PlaybackPath}?queue=error&message=Ungültiges%20Video");
        }

        try
        {
            var start = ParseOptionalTime(form["start"].ToString());
            var duration = ParseOptionalTime(form["duration"].ToString());
            await command(mediaId, start, duration);
            return Results.Redirect($"{PlaybackPath}?queue=success");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            return Results.Redirect($"{PlaybackPath}?queue=error&message={Uri.EscapeDataString(exception.Message)}");
        }
    }

    private static async Task<IResult> ExecuteYouTubeCommandAsync(
        HttpContext context,
        YouTubeDownloadCoordinator downloads,
        YouTubeDownloadAction action,
        CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        try
        {
            var reference = YouTubeUrlParser.Parse(form["url"].ToString());
            var start = ParseOptionalTime(form["start"].ToString());
            var duration = ParseOptionalTime(form["duration"].ToString());
            var maximumDuration = ParseOptionalTime(form["maximumDuration"].ToString());
            var modeValue = form["playbackMode"].ToString();
            var mode = ParseYouTubePlaybackMode(modeValue, start, duration, maximumDuration);
            var snapshot = await downloads.SetIntentAsync(reference.VideoId,
                new YouTubeDownloadIntent(action, mode, start, duration, maximumDuration), cancellationToken);
            if (snapshot.Error is not null)
                throw new InvalidOperationException(snapshot.Error);
            var result = snapshot.Phase == YouTubeDownloadPhase.Ready ? "success" : "downloading";
            return Results.Redirect($"{PlaybackPath}?youtube={result}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            return Results.Redirect($"{PlaybackPath}?youtube=error&message={Uri.EscapeDataString(exception.Message)}");
        }
    }

    private static YouTubeDownloadMode ParseYouTubePlaybackMode(
        string modeValue,
        TimeSpan? start,
        TimeSpan? duration,
        TimeSpan? maximumDuration)
    {
        if (modeValue.Length != 0)
        {
            if (Enum.TryParse<YouTubeDownloadMode>(modeValue, true, out var selected) && Enum.IsDefined(selected))
            {
                return selected;
            }

            throw new FormatException("Der Wiedergabemodus ist ungültig.");
        }

        if (start.HasValue || duration.HasValue) return YouTubeDownloadMode.Custom;
        return maximumDuration.HasValue ? YouTubeDownloadMode.Automatic : YouTubeDownloadMode.Full;
    }

    private static async Task<IResult> CreateNewsAsync(
        HttpContext context,
        INewsService newsService,
        INewsCommandService commands,
        CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        try
        {
            if (!Enum.TryParse<NewsMode>(form["mode"].ToString(), ignoreCase: true, out var mode))
            {
                throw new FormatException("Der News-Modus ist ungültig.");
            }

            _ = int.TryParse(form["priority"], out var priority);
            var item = await newsService.AddAsync(new NewsItem
            {
                Title = form["title"].ToString().Trim(),
                Text = form["text"].ToString().Trim(),
                Mode = mode,
                Duration = ParseOptionalTime(form["duration"].ToString()),
                Permanent = form.ContainsKey("permanent"),
                ValidFrom = ParseOptionalDateTime(form["validFrom"].ToString()),
                ValidUntil = ParseOptionalDateTime(form["validUntil"].ToString()),
                Priority = priority
            }, cancellationToken);

            if (form.ContainsKey("showNow"))
            {
                await commands.ShowNewsAsync(item, cancellationToken);
                return Results.Redirect($"{NewsPath}?news=created-shown");
            }

            return Results.Redirect($"{NewsPath}?news=created");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            return Results.Redirect($"{NewsPath}?news=error&message={Uri.EscapeDataString(exception.Message)}");
        }
    }

    private static async Task<IResult> ShowNewsAsync(
        HttpContext context,
        INewsService newsService,
        INewsCommandService commands,
        CancellationToken cancellationToken)
    {
        var id = await ReadNewsIdAsync(context, cancellationToken);
        var item = (await newsService.GetAllAsync(cancellationToken)).SingleOrDefault(news => news.Id == id);
        if (item is null)
        {
            return Results.Redirect($"{NewsPath}?news=error&message=News%20nicht%20gefunden");
        }

        await commands.ShowNewsAsync(item, cancellationToken);
        return Results.Redirect($"{NewsPath}?news=shown");
    }

    private static async Task<IResult> StopNewsAsync(
        INewsCommandService commands,
        CancellationToken cancellationToken)
    {
        await commands.StopNewsAsync(cancellationToken: cancellationToken);
        return Results.Redirect($"{NewsPath}?news=stopped");
    }

    private static async Task<IResult> StopTickerAsync(
        INewsCommandService commands,
        CancellationToken cancellationToken)
    {
        await commands.StopTickerAsync(cancellationToken);
        return Results.Redirect($"{NewsPath}?news=ticker-stopped");
    }

    private static async Task<IResult> DeleteNewsAsync(
        HttpContext context,
        INewsService newsService,
        CancellationToken cancellationToken)
    {
        try
        {
            await newsService.DeleteAsync(await ReadNewsIdAsync(context, cancellationToken), cancellationToken);
            return Results.Redirect($"{NewsPath}?news=deleted");
        }
        catch (InvalidOperationException exception)
        {
            return Results.Redirect($"{NewsPath}?news=error&message={Uri.EscapeDataString(exception.Message)}");
        }
    }

    private static async Task<long> ReadNewsIdAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        return long.TryParse(form["id"], out var id) ? id : 0;
    }

    private static TimeSpan? ParseOptionalTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException("Start und Dauer müssen als HH:MM:SS angegeben werden.");
    }

    private static DateTimeOffset? ParseOptionalDateTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.CurrentCulture, out var result))
        {
            return result;
        }

        throw new FormatException("Das News-Gültigkeitsfenster ist ungültig.");
    }

    internal static async Task<IResult> StreamMediaAsync(
        int mediaId,
        HttpContext context,
        IMediaLibraryService mediaLibraryService,
        IMediaFolderService mediaFolderService,
        CancellationToken cancellationToken)
    {
        try
        {
            var asset = await mediaLibraryService.GetByIdAsync(mediaId, cancellationToken);
            if (asset is null || !asset.Enabled || !asset.IsAvailable || !File.Exists(asset.FullPath))
            {
                return Results.NotFound();
            }

            var folders = await mediaFolderService.GetAllAsync(cancellationToken);
            if (!folders.Any(folder => folder.Enabled && IsPathInside(asset.FullPath, folder.Path)))
            {
                return Results.NotFound();
            }

            return Results.File(asset.FullPath, GetVideoContentType(asset.FullPath), enableRangeProcessing: true);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Video elements routinely abort stale range requests while seeking or switching sources.
            return Results.StatusCode(ClientClosedRequestStatusCode);
        }
    }

    private static async Task<IResult> StartYouTubeDownloadAsync(
        HttpContext context, YouTubeDownloadCoordinator downloads, CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        try
        {
            return Results.Ok(ToDownloadResponse(await downloads.StartAsync(form["url"].ToString(), cancellationToken)));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> GetYouTubeDownloadAsync(
        string videoId, YouTubeDownloadCoordinator downloads, CancellationToken cancellationToken)
    {
        try { return Results.Ok(ToDownloadResponse(await downloads.GetAsync(videoId, cancellationToken))); }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { return Results.BadRequest(new { error = exception.Message }); }
    }

    private static async Task<IResult> SetYouTubeDownloadIntentAsync(
        string videoId, HttpContext context, YouTubeDownloadCoordinator downloads, CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        try
        {
            if (!Enum.TryParse<YouTubeDownloadAction>(form["action"], true, out var action) ||
                !Enum.IsDefined(action) ||
                !Enum.TryParse<YouTubeDownloadMode>(form["mode"], true, out var mode) ||
                !Enum.IsDefined(mode))
                throw new ArgumentException("Die Wiedergabeaktion ist ungültig.");
            var intent = new YouTubeDownloadIntent(action, mode,
                ParseOptionalTime(form["start"].ToString()), ParseOptionalTime(form["duration"].ToString()),
                ParseOptionalTime(form["maximumDuration"].ToString()));
            return Results.Ok(ToDownloadResponse(await downloads.SetIntentAsync(videoId, intent, cancellationToken)));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static object ToDownloadResponse(YouTubeDownloadSnapshot snapshot) => new
    {
        snapshot.VideoId,
        Phase = snapshot.Phase.ToString().ToLowerInvariant(),
        snapshot.MediaId,
        snapshot.Error,
        DurationSeconds = snapshot.Duration?.TotalSeconds
    };

    private static async Task<IResult> GetMediaPreviewAsync(
        int mediaId,
        HttpContext context,
        IMediaLibraryService mediaLibraryService,
        IMediaFolderService mediaFolderService,
        IMediaPreviewService mediaPreviewService,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        var asset = await mediaLibraryService.GetByIdAsync(mediaId, cancellationToken);
        if (asset is null || !asset.IsAvailable)
        {
            return Results.NotFound();
        }

        var folders = await mediaFolderService.GetAllAsync(cancellationToken);
        if (!folders.Any(folder => folder.Enabled && IsPathInside(asset.FullPath, folder.Path)))
        {
            return Results.NotFound();
        }

        var preview = mediaPreviewService.GetReadyPreviewPath(asset);
        return preview is null ? Results.NotFound() : Results.File(preview, "image/jpeg");
    }

    private static bool IsPathInside(string filePath, string folderPath)
    {
        try
        {
            var fullFilePath = Path.GetFullPath(filePath);
            var fullFolderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            return fullFilePath.StartsWith(fullFolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string GetVideoContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        _ => "application/octet-stream"
    };

    private static bool IsLocalUrl(string value) => value.StartsWith("/", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal);
}
