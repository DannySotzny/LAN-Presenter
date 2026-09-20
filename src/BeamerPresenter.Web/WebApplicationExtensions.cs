using System.Security.Claims;
using BeamerPresenter.Application;
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
        app.MapPost("/api/videos/upload", (Delegate)UploadAsync).RequireAuthorization(); app.MapRazorComponents<PresenterWebApp>(); return app;
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
        try { await using var stream = video.OpenReadStream(); var asset = await mediaLibraryService.AddUploadAsync(video.FileName, stream, video.Length, cancellationToken); return Results.Ok(new { asset.Id, asset.FileName, asset.FileSize }); }
        catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
    }
    private static bool IsLocalUrl(string value) => value.StartsWith("/", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal);
}
