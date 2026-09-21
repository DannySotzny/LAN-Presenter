using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeamerPresenter.App.Tests;

public sealed class PresenterWatchdogTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Missing_chrome_is_restarted_immediately()
    {
        var fixture = new WatchdogFixture(browserRunning: false);

        await fixture.Watchdog.CheckAsync();

        Assert.Equal(1, fixture.Browser.StartCalls);
        Assert.Equal(0, fixture.Recovery.ReloadCalls);
    }

    [Fact]
    public async Task Fresh_reconnect_restores_current_entry_and_aggressive_topmost()
    {
        var fixture = new WatchdogFixture(browserRunning: true, aggressiveTopmost: true);
        fixture.Telemetry.Snapshot = Connected("Ready", null, fixture.Clock.GetUtcNow());

        await fixture.Watchdog.CheckAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        fixture.Telemetry.Snapshot = Connected("Playing", TimeSpan.FromSeconds(1), fixture.Clock.GetUtcNow());
        await fixture.Watchdog.CheckAsync();

        Assert.Equal(1, fixture.Recovery.ReloadCalls);
        Assert.Equal(2, fixture.Browser.ShowCalls);
    }

    [Fact]
    public async Task Stalled_playback_is_reloaded_once_then_failed_and_advanced()
    {
        var fixture = new WatchdogFixture(browserRunning: true);
        fixture.Telemetry.Snapshot = Connected("Playing", TimeSpan.FromSeconds(10), fixture.Clock.GetUtcNow());
        await fixture.Watchdog.CheckAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(16));
        fixture.Telemetry.Snapshot = Connected("Playing", TimeSpan.FromSeconds(10), fixture.Clock.GetUtcNow());
        await fixture.Watchdog.CheckAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(16));
        fixture.Telemetry.Snapshot = Connected("Playing", TimeSpan.FromSeconds(10), fixture.Clock.GetUtcNow());
        await fixture.Watchdog.CheckAsync();

        Assert.Equal(2, fixture.Recovery.ReloadCalls);
        Assert.Equal(1, fixture.Recovery.FailAndAdvanceCalls);
        Assert.Equal(TimeSpan.FromSeconds(10), fixture.Recovery.LastFailedPosition);
    }

    [Fact]
    public async Task Stale_connection_restarts_chrome_after_timeout()
    {
        var fixture = new WatchdogFixture(browserRunning: true);
        fixture.Telemetry.Snapshot = new PresenterTelemetrySnapshot(false, "Disconnected", null, null, null, fixture.Clock.GetUtcNow());
        await fixture.Watchdog.CheckAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(16));
        await fixture.Watchdog.CheckAsync();

        Assert.Equal(1, fixture.Browser.StopCalls);
        Assert.Equal(1, fixture.Browser.StartCalls);
    }

    [Fact]
    public async Task Inactive_presenter_does_not_touch_chrome_or_recovery()
    {
        var fixture = new WatchdogFixture(browserRunning: false, active: false);

        await fixture.Watchdog.CheckAsync();

        Assert.Equal(0, fixture.Browser.StartCalls);
        Assert.Equal(0, fixture.Browser.ShowCalls);
        Assert.Equal(0, fixture.Recovery.ReloadCalls);
    }

    [Fact]
    public async Task Advancing_playback_never_triggers_stall_recovery()
    {
        var fixture = new WatchdogFixture(browserRunning: true);
        fixture.Telemetry.Snapshot = Connected("Playing", TimeSpan.FromSeconds(10), fixture.Clock.GetUtcNow());
        await fixture.Watchdog.CheckAsync();

        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Telemetry.Snapshot = Connected("Playing", TimeSpan.FromSeconds(11), fixture.Clock.GetUtcNow());
        await fixture.Watchdog.CheckAsync();

        Assert.Equal(1, fixture.Recovery.ReloadCalls);
        Assert.Equal(0, fixture.Recovery.FailAndAdvanceCalls);
    }

    private static PresenterTelemetrySnapshot Connected(string status, TimeSpan? position, DateTimeOffset now) =>
        new(true, status, position, TimeSpan.FromMinutes(30), null, now);

    private sealed class WatchdogFixture
    {
        public WatchdogFixture(bool browserRunning, bool aggressiveTopmost = false, bool active = true)
        {
            Browser = new RecordingBrowser(browserRunning);
            var playback = new PlaybackController();
            if (active)
            {
                playback.Activate();
            }
            Watchdog = new PresenterWatchdog(
                playback,
                Browser,
                Telemetry,
                Recovery,
                new StubSettingsService(aggressiveTopmost),
                Clock,
                NullLogger<PresenterWatchdog>.Instance);
        }

        public MutableTimeProvider Clock { get; } = new(Start);
        public RecordingBrowser Browser { get; }
        public StubTelemetry Telemetry { get; } = new();
        public RecordingRecovery Recovery { get; } = new();
        public PresenterWatchdog Watchdog { get; }
    }

    private sealed class RecordingBrowser(bool running) : IBrowserController
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int ShowCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) { running = true; StartCalls++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { running = false; StopCalls++; return Task.CompletedTask; }
        public Task ShowAsync(CancellationToken cancellationToken = default) { ShowCalls++; return Task.CompletedTask; }
        public Task HideAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(running);
    }

    private sealed class StubTelemetry : IPresenterTelemetry
    {
        public PresenterTelemetrySnapshot Snapshot { get; set; } = new(false, "Disconnected", null, null, null, Start);
        public PresenterTelemetrySnapshot Current => Snapshot;
    }

    private sealed class RecordingRecovery : IPresenterRecoveryService
    {
        public int ReloadCalls { get; private set; }
        public int FailAndAdvanceCalls { get; private set; }
        public TimeSpan? LastFailedPosition { get; private set; }

        public Task ReloadCurrentAsync(CancellationToken cancellationToken = default) { ReloadCalls++; return Task.CompletedTask; }
        public Task FailCurrentAndAdvanceAsync(TimeSpan? actualPosition, CancellationToken cancellationToken = default)
        {
            FailAndAdvanceCalls++;
            LastFailedPosition = actualPosition;
            return Task.CompletedTask;
        }
    }

    private sealed class StubSettingsService(bool aggressiveTopmost) : IPresenterSettingsService
    {
        private readonly PresenterSettings settings = new() { AlwaysOnTop = true, AggressiveTopmost = aggressiveTopmost };
        public Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(PresenterSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
