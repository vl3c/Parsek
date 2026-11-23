using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure core of the <c>LiveGhostReentryFx</c> in-game category
    /// (<see cref="LiveGhostReentryFxInGameTests"/>): on a REAL flown recording replayed in the
    /// flight scene, does a ghost that is hot enough for reentry glow actually glow? (test-gap
    /// list 2026-11-18, "Reentry glow and ghost sound on a real flown recording": the
    /// <c>ReentryFx</c> category's three cells drive synthetic inputs only.)
    ///
    /// <para>The cell re-derives the intensity the engine aims at from the ghost's own
    /// interpolated body, altitude and surface velocity through the same stock atmosphere calls
    /// and <see cref="GhostVisualBuilder.ComputeReentryIntensity"/> the engine uses. A sample is
    /// HOT when that raw intensity is at least <see cref="HotRawIntensity"/> and FX are not
    /// warp-suppressed; a hot sample is LIT when the ghost's reentry FX exist and their smoothed
    /// intensity is above zero. The engine smooths at
    /// <see cref="GhostVisualBuilder.ReentrySmoothingRate"/> per second, so a ghost that has
    /// just turned hot is not yet lit: a defect is a ghost hot and unlit for
    /// <see cref="MinUnlitRunFrames"/> consecutive sampled frames AND at least
    /// <see cref="MinUnlitRunSeconds"/> of real time.</para>
    /// </summary>
    internal static class LiveGhostReentryFx
    {
        internal const string Category = "LiveGhostReentryFx";

        /// <summary>The raw intensity at or above which a ghost is expected to glow.</summary>
        internal const float HotRawIntensity = 0.1f;

        internal const int MinUnlitRunFrames = 30;
        internal const float MinUnlitRunSeconds = 1.0f;

        internal enum CellOutcome
        {
            Pass = 0,
            Fail = 1,
            Skip = 2,
        }

        /// <summary>A hot sample: the engine should be drawing reentry FX for it.</summary>
        internal static bool IsHot(float rawIntensity, bool fxSuppressedByWarp)
            => !fxSuppressedByWarp && !float.IsNaN(rawIntensity) && rawIntensity >= HotRawIntensity;

        /// <summary>A lit sample: the FX exist and the engine drove them above zero.</summary>
        internal static bool IsLit(bool fxBuilt, float lastIntensity)
            => fxBuilt && lastIntensity > 0f;

        /// <summary>A run of hot-unlit samples long enough to be a defect, not the smoothing lag.</summary>
        internal static bool IsDefectRun(int frames, float seconds)
            => frames >= MinUnlitRunFrames && seconds >= MinUnlitRunSeconds;

        internal static CellOutcome DecideOutcome(int hotLit, bool anyDefectRun)
        {
            if (anyDefectRun) return CellOutcome.Fail;
            return hotLit > 0 ? CellOutcome.Pass : CellOutcome.Skip;
        }

        internal static string FormatSummary(string scene, int frames, int ghosts, int inAtmosphere, int hot,
            int hotLit, int hotUnlit, int longestUnlitFrames, float maxRaw, string vessels)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "LiveGhostReentryFx summary scene=" + (scene ?? "?")
                + " frames=" + frames.ToString(ic)
                + " ghosts=" + ghosts.ToString(ic)
                + " inAtmosphere=" + inAtmosphere.ToString(ic)
                + " hot=" + hot.ToString(ic)
                + " hotLit=" + hotLit.ToString(ic)
                + " hotUnlit=" + hotUnlit.ToString(ic)
                + " longestUnlitFrames=" + longestUnlitFrames.ToString(ic)
                + " maxRaw=" + maxRaw.ToString("F3", ic)
                + " vessels=" + (string.IsNullOrEmpty(vessels) ? "none" : vessels);
        }
    }
}
