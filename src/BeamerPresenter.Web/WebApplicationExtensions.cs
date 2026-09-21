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
    public static IServiceCollection AddPresenterWebUi(this IServiceCollection services)
    {
        services.AddRazorComponents();
        services.AddMudServices();
        services.AddSignalR();
        services.AddSingleton<PresenterConnectionState>();
        services.AddSingleton<IPresenterGateway, SignalRPresenterGateway>();
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
        app.MapPost("/api/queue/next", (Delegate)PlayNextAsync).RequireAuthorization();
        app.MapPost("/api/queue/now", (Delegate)PlayNowAsync).RequireAuthorization();
        app.MapPost("/api/youtube/next", (Delegate)PlayYouTubeNextAsync).RequireAuthorization();
        app.MapPost("/api/youtube/now", (Delegate)PlayYouTubeNowAsync).RequireAuthorization();
        app.MapGet("/media/{mediaId:int}", (Delegate)StreamMediaAsync).AllowAnonymous();
        app.MapHub<PresenterHub>("/hubs/presenter");
        app.MapRazorComponents<PresenterWebApp>(); return app;
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

    private static async Task<IResult> PlayNextAsync(
        HttpContext context,
        IPlaybackCommandService playback,
        CancellationToken cancellationToken) =>
        await ExecuteQueueCommandAsync(context, (mediaId, start, duration) =>
            playback.PlayNextAsync(mediaId, start, duration, cancellationToken));

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
        IPlaybackCommandService playback,
        CancellationToken cancellationToken) =>
        await ExecuteYouTubeCommandAsync(context, (url, start, duration, maximumDuration) =>
            playback.PlayYouTubeNextAsync(url, start, duration, maximumDuration, cancellationToken));

    private static async Task<IResult> PlayYouTubeNowAsync(
        HttpContext context,
        IPlaybackCommandService playback,
        PresenterConnectionState presenterState,
        CancellationToken cancellationToken)
    {
        TimeSpan? currentPosition = presenterState.LatestReport.PositionSeconds is >= 0
            ? TimeSpan.FromSeconds(presenterState.LatestReport.PositionSeconds.Value)
            : null;
        return await ExecuteYouTubeCommandAsync(context, (url, start, duration, maximumDuration) =>
            playback.PlayYouTubeNowAsync(url, currentPosition, start, duration, maximumDuration, cancellationToken));
    }

    private static async Task<IResult> ExecuteQueueCommandAsync(
        HttpContext context,
        Func<int, TimeSpan?, TimeSpan?, Task<QueueEntry>> command)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!int.TryParse(form["mediaId"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mediaId))
        {
            return Results.Redirect("/?queue=error&message=Ungültiges%20Video");
        }

        try
        {
            var start = ParseOptionalTime(form["start"].ToString());
            var duration = ParseOptionalTime(form["duration"].ToString());
            await command(mediaId, start, duration);
            return Results.Redirect("/?queue=success");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            return Results.Redirect($"/?queue=error&message={Uri.EscapeDataString(exception.Message)}");
        }
    }

    private static async Task<IResult> ExecuteYouTubeCommandAsync(
        HttpContext context,
        Func<string, TimeSpan?, TimeSpan?, TimeSpan?, Task<QueueEntry>> command)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        try
        {
            var start = ParseOptionalTime(form["start"].ToString());
            var duration = ParseOptionalTime(form["duration"].ToString());
            var maximumDuration = ParseOptionalTime(form["maximumDuration"].ToString());
            await command(form["url"].ToString(), start, duration, maximumDuration);
            return Results.Redirect("/?youtube=success");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            return Results.Redirect($"/?youtube=error&message={Uri.EscapeDataString(exception.Message)}");
        }
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

    private static async Task<IResult> StreamMediaAsync(
        int mediaId,
        IMediaLibraryService mediaLibraryService,
        IMediaFolderService mediaFolderService,
        CancellationToken cancellationToken)
    {
        var asset = await mediaLibraryService.GetByIdAsync(mediaId, cancellationToken);
        if (asset is null || !asset.IsAvailable || !File.Exists(asset.FullPath))
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
