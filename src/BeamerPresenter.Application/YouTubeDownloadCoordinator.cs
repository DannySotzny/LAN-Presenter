using System.Collections.Concurrent;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public sealed class YouTubeDownloadCoordinator(
    IYouTubeDownloadTool downloadTool,
    IYouTubeMediaStore mediaStore,
    IMediaFolderService mediaFolders,
    IMediaScanner mediaScanner,
    IFfprobeService ffprobe,
    IPlaybackCommandService playback,
    IPresenterTelemetry telemetry,
    string dataDirectory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DownloadJob> jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim downloadGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string downloadRoot = Path.GetFullPath(Path.Combine(dataDirectory, "Downloads"));

    public Task<YouTubeDownloadSnapshot> StartAsync(string url, CancellationToken cancellationToken = default)
    {
        var reference = YouTubeUrlParser.Parse(url);
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            if (jobs.TryGetValue(reference.VideoId, out var existing))
            {
                if (existing.Snapshot.Phase != YouTubeDownloadPhase.Failed) return Task.FromResult(existing.Snapshot);
                var replacement = new DownloadJob(reference.VideoId);
                if (!jobs.TryUpdate(reference.VideoId, replacement, existing)) continue;
                StartJob(replacement);
                return Task.FromResult(replacement.Snapshot);
            }

            var job = new DownloadJob(reference.VideoId);
            if (!jobs.TryAdd(reference.VideoId, job)) continue;
            StartJob(job);
            return Task.FromResult(job.Snapshot);
        }
    }

    public async Task<YouTubeDownloadSnapshot> GetAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var reference = YouTubeUrlParser.Parse($"https://www.youtube.com/watch?v={videoId}");
        if (jobs.TryGetValue(reference.VideoId, out var job)) return job.Snapshot;
        var existing = await mediaStore.GetBySourceKeyAsync(reference.SourceKey, cancellationToken);
        return existing is { IsAvailable: true, Enabled: true, PlaybackStatus: MediaPlaybackStatus.Supported } && File.Exists(existing.FullPath)
            ? new YouTubeDownloadSnapshot(videoId, YouTubeDownloadPhase.Ready, existing.Id, null, existing.Duration)
            : new YouTubeDownloadSnapshot(videoId, YouTubeDownloadPhase.NotStarted, null, null);
    }

    public async Task<YouTubeDownloadSnapshot> SetIntentAsync(
        string videoId, YouTubeDownloadIntent intent, CancellationToken cancellationToken = default)
    {
        var reference = YouTubeUrlParser.Parse($"https://www.youtube.com/watch?v={videoId}");
        ValidateIntent(intent);
        await StartAsync(reference.CanonicalUrl, cancellationToken);
        var job = jobs[reference.VideoId];
        lock (job.Sync) job.Intent = intent;
        await FlushIntentAsync(job, cancellationToken);
        return job.Snapshot;
    }

    private void StartJob(DownloadJob job) => job.Work = Task.Run(() => RunAsync(job, lifetime.Token), CancellationToken.None);

    private async Task RunAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        string? stagingDirectory = null;
        string? publishedPath = null;
        var published = false;
        var registered = false;
        var gateHeld = false;
        try
        {
            await downloadGate.WaitAsync(cancellationToken);
            gateHeld = true;
            var sourceKey = $"youtube:{job.VideoId}";
            var existing = await mediaStore.GetBySourceKeyAsync(sourceKey, cancellationToken);
            if (existing is { IsAvailable: true, Enabled: true, PlaybackStatus: MediaPlaybackStatus.Supported } && File.Exists(existing.FullPath))
            {
                job.SetSnapshot(YouTubeDownloadPhase.Ready, existing.Id, duration: existing.Duration);
                await FlushIntentAsync(job, cancellationToken);
                return;
            }

            var destination = (await mediaFolders.GetAllAsync(cancellationToken)).FirstOrDefault(folder => folder.Enabled && Directory.Exists(folder.Path))
                ?? throw new InvalidOperationException("Bitte zuerst einen verfügbaren Videoordner in der Desktop-App festlegen.");
            if ((File.GetAttributes(destination.Path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Der Videoordner ist eine Umleitung und kann nicht gescannt werden.");
            stagingDirectory = Path.Combine(downloadRoot, job.VideoId, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            job.SetSnapshot(YouTubeDownloadPhase.Installing);
            var downloadedPath = await downloadTool.DownloadAsync(job.VideoId, stagingDirectory,
                phase => job.SetSnapshot(phase), cancellationToken);
            var safePath = Path.GetFullPath(downloadedPath);
            if (!string.Equals(Path.GetDirectoryName(safePath), Path.GetFullPath(stagingDirectory), StringComparison.OrdinalIgnoreCase) ||
                !safePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(safePath) || (File.GetAttributes(safePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Der Downloader lieferte einen ungültigen Dateipfad.");
            }
            if (!YouTubeDownloadLimits.IsValidSize(new FileInfo(safePath).Length))
                throw new InvalidOperationException("Die Videodatei ist leer oder größer als 5 GB.");

            job.SetSnapshot(YouTubeDownloadPhase.Analyzing);
            var probe = await ffprobe.ProbeAsync(safePath, cancellationToken);
            if (probe.ProbeStatus != MediaProbeStatus.Valid || probe.PlaybackStatus != MediaPlaybackStatus.Supported || probe.Duration is null or { Ticks: <= 0 })
            {
                throw new InvalidOperationException(probe.Error ?? "Das heruntergeladene Video ist im Presenter nicht abspielbar.");
            }

            publishedPath = MakeUniqueDestination(destination.Path, job.VideoId);
            var temporaryPath = Path.Combine(destination.Path, $".YouTube-{job.VideoId}-{Guid.NewGuid():N}.download");
            try
            {
                await using (var source = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }
                if (!YouTubeDownloadLimits.IsValidSize(new FileInfo(temporaryPath).Length))
                {
                    throw new InvalidOperationException("Die Videodatei ist leer oder größer als 5 GB.");
                }
                File.Move(temporaryPath, publishedPath);
                published = true;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            await mediaScanner.ScanAllAsync(cancellationToken);
            var asset = await mediaStore.RegisterDownloadedAsync(sourceKey, publishedPath, cancellationToken);
            registered = true;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                var analyzed = await mediaStore.GetBySourceKeyAsync(sourceKey, wait.Token);
                if (analyzed?.ProbeStatus == MediaProbeStatus.Valid && analyzed.PlaybackStatus == MediaPlaybackStatus.Supported)
                {
                    job.SetSnapshot(YouTubeDownloadPhase.Ready, asset.Id, duration: analyzed.Duration);
                    await FlushIntentAsync(job, cancellationToken);
                    return;
                }
                if (analyzed?.ProbeStatus is MediaProbeStatus.Invalid or MediaProbeStatus.Missing or MediaProbeStatus.Unsupported ||
                    analyzed?.PlaybackStatus == MediaPlaybackStatus.Unsupported)
                {
                    throw new InvalidOperationException(analyzed.ProbeError ?? "Die Medienanalyse hat das Video abgelehnt.");
                }
                await Task.Delay(500, wait.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.SetSnapshot(YouTubeDownloadPhase.Failed, error: "Der Download wurde beim Beenden der App abgebrochen.");
        }
        catch (OperationCanceledException)
        {
            job.SetSnapshot(YouTubeDownloadPhase.Failed, error: "Die Medienanalyse hat das Zeitlimit überschritten.");
        }
        catch (Exception exception)
        {
            job.SetSnapshot(YouTubeDownloadPhase.Failed, error: exception.Message);
        }
        finally
        {
            try
            {
                if (published && !registered && publishedPath is not null && File.Exists(publishedPath)) File.Delete(publishedPath);
                if (stagingDirectory is not null && Path.GetFullPath(stagingDirectory).StartsWith(downloadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (gateHeld) downloadGate.Release();
        }
    }

    private async Task FlushIntentAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        await job.IntentGate.WaitAsync(cancellationToken);
        try
        {
            if (job.Snapshot is not { Phase: YouTubeDownloadPhase.Ready, MediaId: int mediaId }) return;
            YouTubeDownloadIntent? intent;
            lock (job.Sync) { intent = job.Intent; job.Intent = null; }
            if (intent is null) return;
            try
            {
                var asset = await mediaStore.GetBySourceKeyAsync($"youtube:{job.VideoId}", cancellationToken)
                    ?? throw new InvalidOperationException("Das heruntergeladene Video wurde nicht gefunden.");
                var (start, duration) = ResolveSegment(intent, asset);
                if (intent.Action == YouTubeDownloadAction.Now)
                    await playback.PlayNowAsync(mediaId, telemetry.Current.Position, start, duration, cancellationToken);
                else
                    await playback.PlayNextAsync(mediaId, start, duration, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                job.SetSnapshot(YouTubeDownloadPhase.Ready, mediaId, exception.Message, duration: job.Snapshot.Duration);
            }
        }
        finally
        {
            job.IntentGate.Release();
        }
    }

    private static (TimeSpan Start, TimeSpan Duration) ResolveSegment(YouTubeDownloadIntent intent, VideoAsset asset)
    {
        var total = asset.Duration ?? throw new InvalidOperationException("Die Videodauer ist nicht bekannt.");
        return intent.Mode switch
        {
            YouTubeDownloadMode.Full => (TimeSpan.Zero, total),
            YouTubeDownloadMode.Custom when intent.Start is { } start && intent.Duration is { } duration &&
                start >= TimeSpan.Zero && duration > TimeSpan.Zero && start + duration <= total => (start, duration),
            YouTubeDownloadMode.Automatic when intent.MaximumDuration is { } maximum && maximum > TimeSpan.Zero =>
                (TimeSpan.Zero, maximum < total ? maximum : total),
            _ => throw new ArgumentException("Das gewählte Wiedergabesegment ist ungültig.")
        };
    }

    private static void ValidateIntent(YouTubeDownloadIntent intent)
    {
        if (!Enum.IsDefined(intent.Action) || !Enum.IsDefined(intent.Mode))
            throw new ArgumentException("Die Wiedergabeaktion ist ungültig.");
        if (intent.Mode == YouTubeDownloadMode.Custom && (intent.Start is null || intent.Start < TimeSpan.Zero ||
            intent.Duration is null || intent.Duration <= TimeSpan.Zero))
            throw new ArgumentException("Ein eigener Start benötigt eine positive Dauer.");
        if (intent.Mode == YouTubeDownloadMode.Automatic && intent.MaximumDuration is null or { Ticks: <= 0 })
            throw new ArgumentException("Eine positive maximale Laufzeit ist erforderlich.");
    }

    private static string MakeUniqueDestination(string folder, string videoId)
    {
        var path = Path.Combine(folder, $"YouTube-{videoId}.mp4");
        return File.Exists(path) ? Path.Combine(folder, $"YouTube-{videoId}-{Guid.NewGuid():N}.mp4") : path;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try { await Task.WhenAll(jobs.Values.Select(job => job.Work ?? Task.CompletedTask)); }
        catch (OperationCanceledException) { }
        lifetime.Dispose();
        downloadGate.Dispose();
    }

    private sealed class DownloadJob(string videoId)
    {
        private YouTubeDownloadSnapshot snapshot = new(videoId, YouTubeDownloadPhase.Installing, null, null);
        public string VideoId { get; } = videoId;
        public object Sync { get; } = new();
        public SemaphoreSlim IntentGate { get; } = new(1, 1);
        public YouTubeDownloadIntent? Intent { get; set; }
        public Task? Work { get; set; }
        public YouTubeDownloadSnapshot Snapshot => Volatile.Read(ref snapshot);
        public void SetSnapshot(YouTubeDownloadPhase phase, int? mediaId = null, string? error = null, TimeSpan? duration = null) =>
            Volatile.Write(ref snapshot, new YouTubeDownloadSnapshot(VideoId, phase, mediaId, error, duration));
    }
}
