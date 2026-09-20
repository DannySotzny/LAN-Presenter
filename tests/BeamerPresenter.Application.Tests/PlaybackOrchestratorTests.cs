using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application.Tests;

public sealed class PlaybackOrchestratorTests
{
    [Fact]
    public async Task Presenter_states_coordinate_browser_realtime_and_power_services()
    {
        var calls = new List<string>();
        var state = new PlaybackController();
        var orchestrator = new PlaybackOrchestrator(
            state,
            new RecordingBrowser(calls),
            new RecordingPresenter(calls),
            new StubSettingsService(),
            new RecordingPowerManagement(calls));

        await orchestrator.ActivateAsync();
        Assert.Equal(PresenterState.Active, state.State);
        await orchestrator.PauseAsync();
        Assert.Equal(PresenterState.Paused, state.State);
        await orchestrator.HideAsync();
        Assert.Equal(PresenterState.Hidden, state.State);
        await orchestrator.StopAsync();
        Assert.Equal(PresenterState.Stopped, state.State);

        Assert.Equal(
            [
                "browser:start",
                "power:apply:display=True:system=True",
                "presenter:pause",
                "browser:show",
                "presenter:pause",
                "browser:hide",
                "power:release",
                "presenter:stop",
                "browser:stop",
                "power:release"
            ],
            calls);
    }

    private sealed class StubSettingsService : IPresenterSettingsService
    {
        private readonly PresenterSettings settings = new() { PreventDisplaySleep = true, PreventSystemSleep = true };
        public Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(PresenterSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RecordingBrowser(List<string> calls) : IBrowserController
    {
        public Task StartAsync(CancellationToken cancellationToken = default) { calls.Add("browser:start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { calls.Add("browser:stop"); return Task.CompletedTask; }
        public Task ShowAsync(CancellationToken cancellationToken = default) { calls.Add("browser:show"); return Task.CompletedTask; }
        public Task HideAsync(CancellationToken cancellationToken = default) { calls.Add("browser:hide"); return Task.CompletedTask; }
        public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingPresenter(List<string> calls) : IPresenterGateway
    {
        public Task LoadLocalVideoAsync(int mediaId, TimeSpan? start, TimeSpan? end, bool autoPlay, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:play"); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:pause"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { calls.Add("presenter:stop"); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingPowerManagement(List<string> calls) : IPowerManagementService
    {
        public Task ApplyAsync(bool preventDisplaySleep, bool preventSystemSleep, CancellationToken cancellationToken = default)
        {
            calls.Add($"power:apply:display={preventDisplaySleep}:system={preventSystemSleep}");
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("power:release");
            return Task.CompletedTask;
        }
    }
}
