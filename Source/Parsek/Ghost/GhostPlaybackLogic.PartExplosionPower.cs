using System.Globalization;

namespace Parsek
{
    // The power of a destroyed ghost part's explosion: stock's Part.explode rule, speed term included
    // (design-part-ghost-parity.md G.8 item 2, fix Y2).
    internal static partial class GhostPlaybackLogic
    {
        /// <summary>
        /// Surface speed from which stock's <c>Part.explode</c> adds
        /// <see cref="StockPartExplosionFastSpeedOffset"/> to the part's explosion potential.
        /// </summary>
        internal const double StockPartExplosionFastSurfaceSpeedMps = 2000.0;

        /// <summary>What stock adds to the potential at or above <see cref="StockPartExplosionFastSurfaceSpeedMps"/>.</summary>
        internal const float StockPartExplosionFastSpeedOffset = 0.25f;

        /// <summary>
        /// The speed term stock's <c>Part.explode(float)</c> adds to a part's
        /// <c>explosionPotential</c> (decompiled IL, KSP 1.12.5):
        /// <code>
        /// num = 0f;
        /// if (ship_srfSpeed &gt; 1000.0) { if (ship_srfSpeed &lt; 200.0) { num = 0.12f; goto done; } }
        /// if (ship_srfSpeed &gt;= 2000.0) num = 0.25f;
        /// done: FXMonger.Explode(this, partTransform.position, explosionPotential + num);
        /// </code>
        /// No speed is above 1000 and below 200, so the 0.12 branch never runs: stock adds 0.25 from
        /// 2000 m/s on and nothing below it (a NaN speed takes neither branch). Stock reads the ACTIVE
        /// vessel's surface speed; a ghost passes its own.
        /// </summary>
        internal static float StockPartExplosionSpeedOffset(double surfaceSpeedMps)
        {
            return surfaceSpeedMps >= StockPartExplosionFastSurfaceSpeedMps
                ? StockPartExplosionFastSpeedOffset
                : 0f;
        }

        /// <summary>
        /// The power a destroyed part's explosion is queued with: <paramref name="explosionPotential"/>
        /// plus stock's speed term, summed in float as stock does, then clamped to [0, 1]. The clamp
        /// keeps stock's prefab and sound index for every sum (<c>FXMonger</c> raises a power below
        /// <c>minPower</c> = 0.001 to it and indexes with <c>Clamp01(power)</c>, so a sum at or below 0
        /// lands on index 0 either way) and keeps the fallbacks' [0, 1] input.
        /// </summary>
        internal static double StockPartExplosionPower(float explosionPotential, double surfaceSpeedMps)
        {
            float sum = explosionPotential + StockPartExplosionSpeedOffset(surfaceSpeedMps);
            if (float.IsNaN(sum)) return GhostAudioPresets.DefaultPartExplosionPotential;
            return sum < 0f ? 0.0 : (sum > 1f ? 1.0 : (double)sum);
        }

        /// <summary>
        /// The ghost's surface speed this frame, the input stock's speed term reads: the speed its
        /// reentry fire reads (<see cref="GhostSurfaceVelocity.SpeedForAeroFx"/>, from the recorded
        /// trajectory at this frame's UT; the part-event walk runs after positioning, so the frame is
        /// at most one frame past the event). 0 when no surface velocity is known: no speed is
        /// invented. <paramref name="source"/> is <c>recorded</c> when the bracket's recorded speeds
        /// gave it, else the surface velocity source token.
        /// </summary>
        internal static double PartExplosionSurfaceSpeed(GhostPlaybackState state, out string source)
        {
            if (state == null)
            {
                source = GhostSurfaceVelocity.SourceToken(GhostSurfaceVelocitySource.None);
                return 0.0;
            }
            float speed = GhostSurfaceVelocity.SpeedForAeroFx(state.lastInterpolatedSurfaceVelocity,
                state.lastInterpolatedSurfaceVelocitySource, state.lastInterpolatedStockSurfaceSpeed,
                state.lastInterpolatedStockSpeedChord, out bool usedStockSpeed);
            source = usedStockSpeed
                ? "recorded"
                : GhostSurfaceVelocity.SourceToken(state.lastInterpolatedSurfaceVelocitySource);
            return float.IsNaN(speed) || float.IsInfinity(speed) ? 0.0 : speed;
        }

        /// <summary>
        /// The grep-stable inputs and result of a destroyed part's explosion power, for its log lines:
        /// <c>potential= srfSpeed= speedSource= speedOffset= power= prefabIndex=</c> (the index of
        /// stock's seven FXMonger explosions the power draws).
        /// </summary>
        internal static string FormatPartExplosionPower(
            float explosionPotential, double surfaceSpeedMps, string speedSource, double power)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            int index = StockExplosionPrefabCache.ResolvePrefabIndex(power, FallbackExplosionLook.StockPrefabCount);
            return "potential=" + explosionPotential.ToString("0.###", ic)
                + " srfSpeed=" + surfaceSpeedMps.ToString("F1", ic)
                + " speedSource=" + (speedSource ?? "none")
                + " speedOffset=" + StockPartExplosionSpeedOffset(surfaceSpeedMps).ToString("0.##", ic)
                + " power=" + power.ToString("0.###", ic)
                + " prefabIndex=" + index.ToString(ic);
        }
    }
}
