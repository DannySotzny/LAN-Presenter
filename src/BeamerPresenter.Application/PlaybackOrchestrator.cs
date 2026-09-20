namespace BeamerPresenter.Application;

public sealed class PlaybackOrchestrator(
    PlaybackController playback,
    IBrowserController browser,
    IPresenterGateway presenter,
    IPresenterSettingsService settingsService,
    IPowerManagementService powerManagement)
{
    private readonly SemaphoreSlim commandGate = new(1, 1);

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteSerializedAsync(async () =>
        {
            var settings = await settingsService.GetAsync(cancellationToken);
            try
            {
                await browser.StartAsync(cancellationToken);
                await powerManagement.ApplyAsync(settings.PreventDisplaySleep, settings.PreventSystemSleep, cancellationToken);
                playback.Activate();
            }
            catch
            {
                await powerManagement.ReleaseAsync(CancellationToken.None);
                await browser.StopAsync(CancellationToken.None);
                throw;
            }
        }, cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            await presenter.PauseAsync(cancellationToken);
            await browser.ShowAsync(cancellationToken);
            playback.Pause();
        }, cancellationToken);

    public Task HideAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            await presenter.PauseAsync(cancellationToken);
            await browser.HideAsync(cancellationToken);
            await powerManagement.ReleaseAsync(cancellationToken);
            playback.Hide();
        }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(async () =>
        {
            await presenter.StopAsync(cancellationToken);
            await browser.StopAsync(cancellationToken);
            await powerManagement.ReleaseAsync(cancellationToken);
            playback.Stop();
        }, cancellationToken);

    private async Task ExecuteSerializedAsync(Func<Task> command, CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken);
        try
        {
            await command();
        }
        finally
        {
            commandGate.Release();
        }
    }
}
