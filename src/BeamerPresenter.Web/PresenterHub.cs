using BeamerPresenter.Application;
using Microsoft.AspNetCore.SignalR;

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

    internal void Report(string status, double? positionSeconds, double? durationSeconds, string? message) =>
        Volatile.Write(ref latestReport, new PresenterClientReport(status, positionSeconds, durationSeconds, message, DateTimeOffset.UtcNow));
}

public sealed record PresenterClientReport(
    string Status,
    double? PositionSeconds,
    double? DurationSeconds,
    string? Message,
    DateTimeOffset ReceivedUtc);

public sealed class PresenterHub(PresenterConnectionState connectionState) : Hub<IPresenterClient>
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

    public Task ReportStatus(string status, double? positionSeconds, double? durationSeconds, string? message)
    {
        connectionState.Report(status, positionSeconds, durationSeconds, message);
        return Task.CompletedTask;
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
