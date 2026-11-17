namespace Parsek
{
    /// <summary>
    /// How a stored race format number compares with what this build reads and writes. The
    /// same five classes, in the same order, as <c>SaveFormatClass</c>; RaceCore may not name
    /// the Recording module, so <c>RaceFormatPinTests</c> holds the two equal.
    /// </summary>
    internal enum RaceFormatClass
    {
        /// <summary>The key is absent.</summary>
        Missing,

        /// <summary>Older than <see cref="RaceFormat.Baseline"/>.</summary>
        PreBaseline,

        /// <summary>At or after the baseline, older than current: an upgrade step brings it to current.</summary>
        Upgradable,

        /// <summary>Exactly what this build writes.</summary>
        Current,

        /// <summary>Written by a newer build: never read as if current, never destroyed.</summary>
        Newer,
    }

    /// <summary>
    /// The race format constants and their classifier (design 6.1, 15.7). Two numbers:
    /// <see cref="LinkCurrent"/> versions the race mark (<c>raceLinkFormat</c>, in the save's
    /// <see cref="ScenarioModuleName"/> node) and <see cref="Current"/> versions the exchange
    /// files (<c>raceFormat</c>). Both are registered as rows of <c>SaveFormat</c> by R1-12, and
    /// <c>RaceFormatPinTests</c> holds the rows equal to these constants. No other file declares
    /// a race format constant or classifier.
    /// </summary>
    internal static class RaceFormat
    {
        /// <summary>The first number either race format was written with; never changes.</summary>
        internal const int Baseline = 1;

        /// <summary>The race mark's format (<c>raceLinkFormat</c>) this build writes.</summary>
        internal const int LinkCurrent = 1;

        /// <summary>The exchange format (<c>raceFormat</c>) this build writes.</summary>
        internal const int Current = 1;

        /// <summary>The race mark's scenario module and node name; equals <c>nameof(ParsekRaceScenario)</c>.</summary>
        internal const string ScenarioModuleName = "ParsekRaceScenario";

        /// <summary>The one date unit this format compares game dates in (design 6.6).</summary>
        internal const string DateUnitSecond = "SECOND";

        /// <summary>The site table the first build ships (design 7.3).</summary>
        internal const string SiteTableKerbinEquator1 = "kerbin-equator-1";

        /// <summary>The ladder table the first build ships (design 7.9).</summary>
        internal const string LadderStandard1 = "standard-1";

        /// <summary>
        /// Pure: where <paramref name="stored"/> sits against <see cref="Baseline"/> and
        /// <paramref name="current"/> (<see cref="LinkCurrent"/> or <see cref="Current"/>), by the
        /// rule of <c>SaveFormat.Classify</c>.
        /// </summary>
        internal static RaceFormatClass Classify(int? stored, int current)
        {
            if (!stored.HasValue)
                return RaceFormatClass.Missing;
            int v = stored.Value;
            if (v > current)
                return RaceFormatClass.Newer;
            if (v == current)
                return RaceFormatClass.Current;
            if (v < Baseline)
                return RaceFormatClass.PreBaseline;
            return RaceFormatClass.Upgradable;
        }
    }
}
