using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application.Tests;

public sealed class PlaybackControllerTests
{
    [Fact]
    public void State_changes_follow_the_requested_command()
    {
        var controller = new PlaybackController();

        controller.Activate();
        Assert.Equal(PresenterState.Active, controller.State);
        controller.Pause();
        Assert.Equal(PresenterState.Paused, controller.State);
        controller.Hide();
        Assert.Equal(PresenterState.Hidden, controller.State);
        controller.Stop();
        Assert.Equal(PresenterState.Stopped, controller.State);
    }
}
