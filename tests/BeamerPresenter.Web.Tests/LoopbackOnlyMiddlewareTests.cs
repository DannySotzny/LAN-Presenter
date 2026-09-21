using System.Net;
using BeamerPresenter.Web;
using Microsoft.AspNetCore.Http;

namespace BeamerPresenter.Web.Tests;

public sealed class LoopbackOnlyMiddlewareTests
{
    [Theory]
    [InlineData("/presenter")]
    [InlineData("/presenter/subpath")]
    [InlineData("/media/42")]
    [InlineData("/hubs/presenter/negotiate")]
    public async Task Presenter_resources_reject_non_loopback_clients(string path)
    {
        var nextCalled = false;
        var middleware = new LoopbackOnlyMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.10.42");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Presenter_resources_allow_loopback_clients(string remoteAddress)
    {
        var nextCalled = false;
        var middleware = new LoopbackOnlyMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/presenter";
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Management_routes_remain_available_to_lan_clients()
    {
        var nextCalled = false;
        var middleware = new LoopbackOnlyMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/login";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.10.42");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }
}
