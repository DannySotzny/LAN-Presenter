using BeamerPresenter.App;

namespace BeamerPresenter.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
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
