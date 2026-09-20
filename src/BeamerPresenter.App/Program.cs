using BeamerPresenter.Infrastructure;
using BeamerPresenter.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeamerPresenter.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var startMinimized = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        using var singleInstance = SingleInstanceCoordinator.Acquire();
        if (!singleInstance.IsPrimary)
        {
            if (!startMinimized)
            {
                singleInstance.SignalPrimaryAsync().GetAwaiter().GetResult();
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        using var presenterHost = BuildPresenterHost();
        presenterHost.StartAsync().GetAwaiter().GetResult();
        using var presenterForm = new PresenterForm(presenterHost, startMinimized);
        singleInstance.StartListening(() =>
        {
            if (!presenterForm.IsDisposed && presenterForm.IsHandleCreated)
            {
                presenterForm.BeginInvoke(presenterForm.ShowFromExternalLaunch);
            }
        });
        System.Windows.Forms.Application.Run(presenterForm);
        presenterHost.StopAsync().GetAwaiter().GetResult();
    }

    private static WebApplication BuildPresenterHost()
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HouseOfLAN", "Presenter", "Data");
        var webPort = PresenterDatabase.GetConfiguredWebPort(dataDirectory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");
        builder.Services.AddPresenterInfrastructure(dataDirectory);
        builder.Services.AddPresenterWebUi();
        builder.Services.AddSingleton(new StartupRegistrationService(Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath));
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = 5L * 1024 * 1024 * 1024);
        var application = builder.Build();
        using (var scope = application.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<PresenterDbContext>>().CreateDbContext().Database.EnsureCreated();
        }
        application.UseStaticFiles();
        application.UseAuthentication();
        application.UseAuthorization();
        application.UseAntiforgery();
        application.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        application.MapPresenterWebUi();
        return application;
    }
}
