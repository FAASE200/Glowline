using System;
using Glowline.Models;

namespace Glowline.Services;

public sealed class SessionSnapshot
{
    public TrackInfo? Track { get; init; }
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public bool IsPlaying { get; init; }
    public string PlaybackStatus { get; init; } = "";
    public string SourceAppId { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan RawTimelinePosition { get; init; }
    public DateTimeOffset TimelineLastUpdated { get; init; }
}
