using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public sealed class PlaybackController
{
    private PresenterState _state = PresenterState.Stopped;

    public PresenterState State => _state;

    public void Activate() => _state = PresenterState.Active;
    public void Pause() => _state = PresenterState.Paused;
    public void Stop() => _state = PresenterState.Stopped;
    public void Hide() => _state = PresenterState.Hidden;
}
