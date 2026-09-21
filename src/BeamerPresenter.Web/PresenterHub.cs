using BeamerPresenter.Application;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Web;

public interface IPresenterClient
{
    Task LoadLocalVideo(int mediaId, double? startSeconds, double? endSeconds, bool autoPlay);
    Task Play();
    Task Pause();
    Task Stop();
    Task Seek(double positionSeconds);
    Task SetVolume(double volume);
}

public sealed class PresenterConnectionState
{
    private readonly object reportLock = new();
    private int connectionCount;
    private PresenterClientReport latestReport = new("Disconnected", null, null, null, DateTimeOffset.UtcNow);

    public int ConnectionCount => Volatile.Read(ref connectionCount);
    public bool IsConnected => ConnectionCount > 0;
    public PresenterClientReport LatestReport => latestReport;

    internal void Connected()
    {
        Interlocked.Increment(ref connectionCount);
        Volatile.Write(ref latestReport, new PresenterClientReport("Connected", null, null, null, DateTimeOffset.UtcNow));
    }

    internal void Disconnected()
    {
        Interlocked.Decrement(ref connectionCount);
        Volatile.Write(ref latestReport, new PresenterClientReport("Disconnected", null, null, null, DateTimeOffset.UtcNow));
    }

    internal bool Report(string status, double? positionSeconds, double? durationSeconds, string? message)
    {
        lock (reportLock)
        {
            var previousStatus = latestReport.Status;
            Volatile.Write(ref latestReport, new PresenterClientReport(status, positionSeconds, durationSeconds, message, DateTimeOffset.UtcNow));
            return IsTerminal(status) && !IsTerminal(previousStatus);
        }
    }

    private static bool IsTerminal(string status) =>
        status.Equals("Ended", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Error", StringComparison.OrdinalIgnoreCase);
}

public sealed record PresenterClientReport(
    string Status,
    double? PositionSeconds,
    double? DurationSeconds,
    string? Message,
    DateTimeOffset ReceivedUtc);

public sealed class PresenterHub(
    PresenterConnectionState connectionState,
    IServiceProvider services) : Hub<IPresenterClient>
{
    public override async Task OnConnectedAsync()
    {
        connectionState.Connected();
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connectionState.Disconnected();
        await base.OnDisconnectedAsync(exception);
    }

    public async Task ReportStatus(string status, double? positionSeconds, double? durationSeconds, string? message)
    {
        if (!connectionState.Report(status, positionSeconds, durationSeconds, message))
        {
            return;
        }

        var playback = services.GetService<IPlaybackCommandService>();
        if (playback is not null)
        {
            TimeSpan? position = positionSeconds is >= 0 ? TimeSpan.FromSeconds(positionSeconds.Value) : null;
            await playback.AdvanceAsync(position, status.Equals("Ended", StringComparison.OrdinalIgnoreCase), Context.ConnectionAborted);
        }
    }
}

internal sealed class SignalRPresenterGateway(IHubContext<PresenterHub, IPresenterClient> hubContext) : IPresenterGateway
{
    public Task LoadLocalVideoAsync(int mediaId, TimeSpan? start, TimeSpan? end, bool autoPlay, CancellationToken cancellationToken = default) =>
        hubContext.Clients.All.LoadLocalVideo(mediaId, start?.TotalSeconds, end?.TotalSeconds, autoPlay).WaitAsync(cancellationToken);

    public Task PlayAsync(CancellationToken cancellationToken = default) =>
        hubContext.Clients.All.Play().WaitAsync(cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        hubContext.Clients.All.Pause().WaitAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        hubContext.Clients.All.Stop().WaitAsync(cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        hubContext.Clients.All.Seek(position.TotalSeconds).WaitAsync(cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) =>
        hubContext.Clients.All.SetVolume(Math.Clamp(volume, 0, 1)).WaitAsync(cancellationToken);
}
