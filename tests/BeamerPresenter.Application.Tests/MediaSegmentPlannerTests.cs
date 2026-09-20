using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Application.Tests;

public sealed class MediaSegmentPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Short_video_at_threshold_is_planned_in_full()
    {
        var media = Video(1, TimeSpan.FromMinutes(10));

        var result = Planner().Plan([media], [], Settings(), Now);

        Assert.Equal(new PlannedMediaSegment(1, TimeSpan.Zero, TimeSpan.FromMinutes(10)), result);
    }

    [Fact]
    public void Long_video_segment_stays_inside_configured_duration_range()
    {
        var result = Planner(0, 120, 60).Plan(
            [Video(1, TimeSpan.FromMinutes(30))],
            [],
            Settings(),
            Now);

        Assert.NotNull(result);
        Assert.InRange(result.End - result.Start, TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(10));
        Assert.InRange(result.Start, TimeSpan.Zero, TimeSpan.FromMinutes(23));
        Assert.True(result.End <= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Previously_played_ranges_are_not_selected_again()
    {
        var history = Played(1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), Now.AddHours(-2));

        var result = Planner().Plan(
            [Video(1, TimeSpan.FromMinutes(30))],
            [history],
            Settings(videoCooldownCount: 0, timeCooldownMinutes: 0),
            Now);

        Assert.NotNull(result);
        Assert.True(result.End <= TimeSpan.FromMinutes(5) || result.Start >= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Interrupted_playback_only_reserves_the_actual_range()
    {
        var history = Played(
            1,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(12),
            Now.AddHours(-2),
            plannedEnd: TimeSpan.FromMinutes(20),
            interrupted: true);

        var result = Planner(0, 0, 1, 0).Plan(
            [Video(1, TimeSpan.FromMinutes(20))],
            [history],
            Settings(videoCooldownCount: 0, timeCooldownMinutes: 0),
            Now);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromMinutes(12), result.Start);
        Assert.True(result.End <= TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Recent_video_is_excluded_by_count_cooldown()
    {
        var result = Planner().Plan(
            [Video(1, TimeSpan.FromMinutes(20)), Video(2, TimeSpan.FromMinutes(20))],
            [Played(1, TimeSpan.Zero, TimeSpan.FromMinutes(7), Now.AddHours(-2))],
            Settings(videoCooldownCount: 1, timeCooldownMinutes: 0),
            Now);

        Assert.Equal(2, result?.MediaId);
    }

    [Fact]
    public void Recent_video_is_excluded_by_time_cooldown()
    {
        var result = Planner().Plan(
            [Video(1, TimeSpan.FromMinutes(20)), Video(2, TimeSpan.FromMinutes(20))],
            [Played(1, TimeSpan.Zero, TimeSpan.FromMinutes(7), Now.AddMinutes(-30))],
            Settings(videoCooldownCount: 0, timeCooldownMinutes: 60),
            Now);

        Assert.Equal(2, result?.MediaId);
    }

    [Fact]
    public void Long_video_is_exhausted_when_no_minimum_segment_remains()
    {
        var history = new[]
        {
            Played(1, TimeSpan.Zero, TimeSpan.FromMinutes(8), Now.AddHours(-3)),
            Played(1, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(20), Now.AddHours(-2))
        };

        var result = Planner().Plan(
            [Video(1, TimeSpan.FromMinutes(20))],
            history,
            Settings(videoCooldownCount: 0, timeCooldownMinutes: 0),
            Now);

        Assert.Null(result);
    }

    [Fact]
    public void Video_is_selected_before_its_segment()
    {
        var random = new SequenceRandomSource(1, 0, 0, 0);

        var result = new MediaSegmentPlanner(random).Plan(
            [Video(1, TimeSpan.FromMinutes(20)), Video(2, TimeSpan.FromHours(4))],
            [],
            Settings(),
            Now);

        Assert.Equal(2, result?.MediaId);
        Assert.Equal(2, random.Calls[0].ExclusiveMaximum);
        Assert.True(random.Calls.Count >= 3);
    }

    private static MediaSegmentPlanner Planner(params int[] values) =>
        new(new SequenceRandomSource(values));

    private static PlaybackPlanningOptions Settings(
        int videoCooldownCount = 10,
        int timeCooldownMinutes = 60) =>
        new(VideoCooldownCount: videoCooldownCount, TimeCooldownMinutes: timeCooldownMinutes);

    private static VideoAsset Video(int id, TimeSpan duration) => new()
    {
        Id = id,
        FileName = $"video-{id}.mp4",
        FullPath = $"C:\\media\\video-{id}.mp4",
        Duration = duration,
        IsAvailable = true,
        PlaybackStatus = MediaPlaybackStatus.Supported
    };

    private static PlaybackHistory Played(
        int mediaId,
        TimeSpan actualStart,
        TimeSpan actualEnd,
        DateTimeOffset startedUtc,
        TimeSpan? plannedEnd = null,
        bool interrupted = false) => new()
        {
            MediaId = mediaId,
            SourceType = MediaSourceType.Local,
            PlannedStart = actualStart,
            PlannedEnd = plannedEnd ?? actualEnd,
            ActualStart = actualStart,
            ActualEnd = actualEnd,
            StartedUtc = startedUtc,
            FinishedUtc = startedUtc.Add(actualEnd - actualStart),
            Completed = !interrupted,
            Interrupted = interrupted,
            PlaybackReason = PlaybackReason.Automatic
        };

    private sealed class SequenceRandomSource(params int[] values) : IRandomSource
    {
        private readonly Queue<int> values = new(values);

        public List<RandomCall> Calls { get; } = [];

        public int Next(int exclusiveMaximum)
        {
            Calls.Add(new RandomCall(exclusiveMaximum));
            return values.Count == 0 ? 0 : values.Dequeue() % exclusiveMaximum;
        }
    }

    private sealed record RandomCall(int ExclusiveMaximum);
}
