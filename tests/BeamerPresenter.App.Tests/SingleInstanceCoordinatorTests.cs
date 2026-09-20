using BeamerPresenter.App;

namespace BeamerPresenter.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void Startup_command_quotes_the_executable_path()
    {
        var registration = new StartupRegistrationService(@"C:\Program Files\Beamer Presenter\BeamerPresenter.App.exe");

        Assert.Equal("\"C:\\Program Files\\Beamer Presenter\\BeamerPresenter.App.exe\" --autostart", registration.StartupCommand);
    }

    [Fact]
    public async Task Second_instance_signals_the_primary_instance()
    {
        var applicationId = $"BeamerPresenter.Tests.{Guid.NewGuid():N}";
        using var primary = SingleInstanceCoordinator.Acquire(applicationId);
        using var secondary = SingleInstanceCoordinator.Acquire(applicationId);
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        primary.StartListening(() => activation.TrySetResult());

        Assert.True(await secondary.SignalPrimaryAsync());
        await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
