using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BeamerPresenter.Web;

public static class LoopbackOnlyMiddlewareExtensions
{
    public static IApplicationBuilder UsePresenterLoopbackProtection(this IApplicationBuilder app) =>
        app.UseMiddleware<LoopbackOnlyMiddleware>();
}

internal sealed class LoopbackOnlyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPresenterResource(context.Request.Path) && !IsLoopback(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool IsPresenterResource(PathString path) =>
        path.StartsWithSegments("/presenter", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/media", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/hubs/presenter", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopback(IPAddress? address)
    {
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }

        return address is not null && IPAddress.IsLoopback(address);
    }
}
