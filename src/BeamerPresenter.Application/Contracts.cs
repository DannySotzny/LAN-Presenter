using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public interface IPresenterSettingsService
{
    Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PresenterSettings settings, CancellationToken cancellationToken = default);
    Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default);
    Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default);
    Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default);
}

public interface IMediaLibraryService
{
    Task<IReadOnlyList<VideoAsset>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<VideoAsset> AddUploadAsync(string originalFileName, Stream content, long length, CancellationToken cancellationToken = default);
}

public sealed record PresenterStatus(PresenterState State, string Version, string WebUrl);
