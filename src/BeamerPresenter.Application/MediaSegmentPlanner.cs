using BeamerPresenter.Domain;

namespace BeamerPresenter.Application;

public interface IRandomSource
{
    int Next(int exclusiveMaximum);
}

public sealed class SystemRandomSource : IRandomSource
{
    public int Next(int exclusiveMaximum) => Random.Shared.Next(exclusiveMaximum);
}

public sealed record PlannedMediaSegment(int MediaId, TimeSpan Start, TimeSpan End);

public sealed record PlaybackPlanningOptions(
    int ShortVideoThresholdSeconds = 10 * 60,
    int ClipLengthMinSeconds = 7 * 60,
    int ClipLengthMaxSeconds = 10 * 60,
    int VideoCooldownCount = 10,
    int TimeCooldownMinutes = 60,
    int QueueTargetLength = 10)
{
    public static PlaybackPlanningOptions From(PresenterSettings settings) => new(
        settings.ShortVideoThresholdSeconds,
        settings.ClipLengthMinSeconds,
        settings.ClipLengthMaxSeconds,
        settings.VideoCooldownCount,
        settings.TimeCooldownMinutes,
        settings.QueueTargetLength);
}

public sealed class MediaSegmentPlanner
{
    private static readonly TimeSpan BoundaryTolerance = TimeSpan.FromSeconds(2);

    private readonly IRandomSource random;

    public MediaSegmentPlanner(IRandomSource random)
    {
        this.random = random;
    }

    public PlannedMediaSegment? Plan(
        IReadOnlyCollection<VideoAsset> media,
        IReadOnlyCollection<PlaybackHistory> history,
        PlaybackPlanningOptions settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        var cooledDownMediaIds = GetCooledDownMediaIds(history, settings, now);
        var candidates = media
            .Where(asset => IsPlayable(asset) && !cooledDownMediaIds.Contains(asset.Id))
            .Select(asset => new Candidate(asset, GetFreeRanges(asset, history)))
            .Where(candidate => HasPlayableRange(candidate, settings))
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        var selected = candidates[random.Next(candidates.Length)];
        return PlanSegment(selected, settings);
    }

    private PlannedMediaSegment PlanSegment(Candidate candidate, PlaybackPlanningOptions settings)
    {
        var duration = candidate.Asset.Duration!.Value;
        if (duration <= TimeSpan.FromSeconds(settings.ShortVideoThresholdSeconds))
        {
            return new PlannedMediaSegment(candidate.Asset.Id, TimeSpan.Zero, duration);
        }

        var minimum = TimeSpan.FromSeconds(settings.ClipLengthMinSeconds);
        var maximum = TimeSpan.FromSeconds(settings.ClipLengthMaxSeconds);
        var longestRange = candidate.FreeRanges.Max(range => range.Duration);
        var maximumClip = maximum < longestRange ? maximum : longestRange;
        var clipLength = RandomDuration(minimum, maximumClip);
        var ranges = candidate.FreeRanges.Where(range => range.Duration >= clipLength).ToArray();
        var range = ranges[random.Next(ranges.Length)];
        var availableOffset = range.Duration - clipLength;
        var start = range.Start + RandomDuration(TimeSpan.Zero, availableOffset);

        return new PlannedMediaSegment(candidate.Asset.Id, start, start + clipLength);
    }

    private TimeSpan RandomDuration(TimeSpan minimum, TimeSpan maximum)
    {
        var rangeInSeconds = (int)Math.Floor((maximum - minimum).TotalSeconds);
        return minimum + TimeSpan.FromSeconds(rangeInSeconds == 0 ? 0 : random.Next(rangeInSeconds + 1));
    }

    private static IReadOnlyList<TimeRange> GetFreeRanges(
        VideoAsset asset,
        IReadOnlyCollection<PlaybackHistory> history)
    {
        var duration = asset.Duration!.Value;
        var used = history
            .Where(entry => entry.MediaId == asset.Id && entry.ActualStart.HasValue && entry.ActualEnd.HasValue)
            .Select(entry => new TimeRange(
                Clamp(entry.ActualStart!.Value, TimeSpan.Zero, duration),
                Clamp(entry.ActualEnd!.Value, TimeSpan.Zero, duration)))
            .Where(range => range.End > range.Start)
            .OrderBy(range => range.Start)
            .ToArray();

        if (used.Length == 0)
        {
            return [new TimeRange(TimeSpan.Zero, duration)];
        }

        var merged = new List<TimeRange>();
        foreach (var range in used)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End + BoundaryTolerance)
            {
                merged.Add(range);
                continue;
            }

            var current = merged[^1];
            merged[^1] = current with { End = range.End > current.End ? range.End : current.End };
        }

        var free = new List<TimeRange>();
        var cursor = TimeSpan.Zero;
        foreach (var range in merged)
        {
            if (range.Start - cursor > BoundaryTolerance)
            {
                free.Add(new TimeRange(cursor, range.Start));
            }

            cursor = range.End > cursor ? range.End : cursor;
        }

        if (duration - cursor > BoundaryTolerance)
        {
            free.Add(new TimeRange(cursor, duration));
        }

        return free;
    }

    private static HashSet<int> GetCooledDownMediaIds(
        IReadOnlyCollection<PlaybackHistory> history,
        PlaybackPlanningOptions settings,
        DateTimeOffset now)
    {
        var recentByCount = settings.VideoCooldownCount == 0
            ? []
            : history
                .OrderByDescending(entry => entry.StartedUtc)
                .Take(settings.VideoCooldownCount)
                .Where(entry => entry.MediaId.HasValue)
                .Select(entry => entry.MediaId!.Value);
        var recentByTime = settings.TimeCooldownMinutes == 0
            ? []
            : history
                .Where(entry => entry.MediaId.HasValue && entry.StartedUtc >= now.AddMinutes(-settings.TimeCooldownMinutes))
                .Select(entry => entry.MediaId!.Value);

        return recentByCount.Concat(recentByTime).ToHashSet();
    }

    private static bool IsPlayable(VideoAsset asset) =>
        asset.IsAvailable &&
        asset.Duration > TimeSpan.Zero &&
        asset.PlaybackStatus is not MediaPlaybackStatus.Unsupported and not MediaPlaybackStatus.Failed;

    private static bool HasPlayableRange(Candidate candidate, PlaybackPlanningOptions settings)
    {
        if (candidate.Asset.Duration <= TimeSpan.FromSeconds(settings.ShortVideoThresholdSeconds))
        {
            return candidate.FreeRanges.Count == 1 &&
                   candidate.FreeRanges[0].Start == TimeSpan.Zero &&
                   candidate.FreeRanges[0].End == candidate.Asset.Duration;
        }

        var minimum = TimeSpan.FromSeconds(settings.ClipLengthMinSeconds);
        return candidate.FreeRanges.Any(range => range.Duration >= minimum);
    }

    private static void Validate(PlaybackPlanningOptions settings)
    {
        if (settings.ShortVideoThresholdSeconds <= 0 ||
            settings.ClipLengthMinSeconds <= 0 ||
            settings.ClipLengthMaxSeconds < settings.ClipLengthMinSeconds ||
            settings.VideoCooldownCount < 0 ||
            settings.TimeCooldownMinutes < 0 ||
            settings.QueueTargetLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The playback planning settings are invalid.");
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private sealed record Candidate(VideoAsset Asset, IReadOnlyList<TimeRange> FreeRanges);

    private sealed record TimeRange(TimeSpan Start, TimeSpan End)
    {
        public TimeSpan Duration => End - Start;
    }
}
