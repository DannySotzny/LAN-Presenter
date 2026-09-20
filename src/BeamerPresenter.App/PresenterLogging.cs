using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace BeamerPresenter.App;

internal static class PresenterLogging
{
    public static Serilog.Core.Logger CreateLogger(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Beamer Presenter for LAN-Parties")
            .WriteTo.File(
                new JsonFormatter(renderMessage: true),
                Path.Combine(logsDirectory, "presenter-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }
}
