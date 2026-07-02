namespace SpreadTrader.Engine;

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>How often spreads are recalculated from the tick store and, if changed,
    /// pushed to the UI / evaluated against trigger rules. Lower = snappier UI, more CPU and
    /// more SignalR traffic. 300ms (the default in appsettings.json) is a reasonable default
    /// for ~200 instruments; this class-level default of 250 is only used if the Engine section
    /// is missing from configuration entirely.</summary>
    public int SpreadRecalculationIntervalMs { get; set; } = 250;

    /// <summary>Minimum time a re-armable (non one-shot) rule must wait between fires, even if
    /// the spread keeps crossing the threshold every recalculation tick. Guards against firing
    /// a burst of duplicate orders off a single noisy price move.</summary>
    public int MinFireIntervalPerRuleMs { get; set; } = 2000;

    /// <summary>How often (in minutes) the engine re-ranks every enabled spread by the absolute
    /// value of its spread percentage (biggest carry first) and pushes the ranked list to the
    /// dashboard as "rankingUpdate". Independent of SpreadRecalculationIntervalMs - this is a
    /// much slower, "what's most interesting right now" summary rather than a live tick feed.</summary>
    public int RankingIntervalMinutes { get; set; } = 3;
}
