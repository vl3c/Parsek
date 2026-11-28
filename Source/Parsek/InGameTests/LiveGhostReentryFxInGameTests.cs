using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live half of <see cref="LiveGhostReentryFx"/>: samples every live flight-scene ghost on
    /// consecutive rendered frames for up to <see cref="MaxWaitSeconds"/> of real time,
    /// re-derives the reentry intensity the engine aims at for it, and checks that a hot ghost
    /// is lit. Passes on the first lit hot sample with no defect run; skips when no live ghost
    /// was hot in the window (a lane that pins <c>passed=1</c> reds then).
    /// </summary>
    public class LiveGhostReentryFxInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "LiveGhostReentryFx";

        internal const float MaxWaitSeconds = 8f;
        internal const int NoGhostGiveUpFrames = 60;

        private sealed class Run
        {
            public int Frames;
            public float StartedAt;
        }

        [InGameTest(Category = "LiveGhostReentryFx", Scene = GameScenes.FLIGHT,
            Description = "A live flight ghost of a real flown recording that is hot enough for reentry glow (raw intensity re-derived from its own body, altitude and surface velocity) has its reentry FX built and lit within the smoothing lag (test-gap list, reentry glow on a real recording)")]
        public IEnumerator HotGhostsOfARealRecordingGlow()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || flight.Engine == null)
            {
                InGameAssert.Skip("needs a ParsekFlight instance with a playback engine (FLIGHT scene)");
                yield break;
            }

            var runs = new Dictionary<int, Run>();
            var vessels = new SortedSet<string>(System.StringComparer.Ordinal);
            var failures = new List<string>();
            int frames = 0, maxGhosts = 0, inAtmosphere = 0, hot = 0, hotLit = 0, hotUnlit = 0;
            int longestUnlit = 0, framesWithoutGhost = 0;
            float maxRaw = 0f;
            bool defect = false;
            float started = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - started < MaxWaitSeconds)
            {
                yield return new WaitForEndOfFrame();
                if (ParsekFlight.Instance != flight || flight.Engine == null)
                    break;
                frames++;
                GhostPlaybackEngine engine = flight.Engine;
                Dictionary<int, GameObject> ghosts = engine.GetGhostGameObjects();
                int live = 0;
                bool suppressed = GhostPlaybackLogic.ShouldSuppressVisualFx(TimeWarp.CurrentRate);
                float now = Time.realtimeSinceStartup;
                var hotThisFrame = new HashSet<int>();
                if (ghosts != null)
                {
                    foreach (int idx in ghosts.Keys)
                    {
                        if (!engine.TryGetGhostState(idx, out GhostPlaybackState state) || state == null
                            || state.ghost == null || !state.ghost.activeInHierarchy || !state.positionedThisFrame)
                            continue;
                        live++;
                        if (!TryRawIntensity(state, out float raw)) continue;
                        inAtmosphere++;
                        if (raw > maxRaw) maxRaw = raw;
                        if (!LiveGhostReentryFx.IsHot(raw, suppressed)) continue;
                        hot++;
                        vessels.Add(state.vesselName ?? "?");
                        ReentryFxInfo info = state.reentryFxInfo;
                        if (LiveGhostReentryFx.IsLit(info != null, info != null ? info.lastIntensity : 0f))
                        {
                            hotLit++;
                            runs.Remove(idx);
                            continue;
                        }
                        hotUnlit++;
                        hotThisFrame.Add(idx);
                        if (!runs.TryGetValue(idx, out Run run))
                        {
                            run = new Run { StartedAt = now };
                            runs[idx] = run;
                        }
                        run.Frames++;
                        if (run.Frames > longestUnlit) longestUnlit = run.Frames;
                        if (!defect && LiveGhostReentryFx.IsDefectRun(run.Frames, now - run.StartedAt))
                        {
                            defect = true;
                            failures.Add("ghost #" + idx.ToString(IC) + " '" + (state.vesselName ?? "?")
                                + "' hot raw=" + raw.ToString("F3", IC)
                                + " alt=" + state.lastInterpolatedAltitude.ToString("F0", IC)
                                + " built=" + (info != null ? "true" : "false")
                                + " pending=" + (state.reentryFxPendingBuild ? "true" : "false")
                                + " intensity=" + (info != null ? info.lastIntensity.ToString("F3", IC) : "-")
                                + " unlitFrames=" + run.Frames.ToString(IC));
                        }
                    }
                }
                // A ghost that is no longer hot and unlit ends its run.
                var ended = new List<int>();
                foreach (int k in runs.Keys)
                    if (!hotThisFrame.Contains(k)) ended.Add(k);
                for (int i = 0; i < ended.Count; i++) runs.Remove(ended[i]);

                if (live > maxGhosts) maxGhosts = live;
                framesWithoutGhost = live == 0 ? framesWithoutGhost + 1 : 0;
                if (maxGhosts == 0 && framesWithoutGhost >= NoGhostGiveUpFrames)
                    break;
                if (hotLit > 0 && !defect && runs.Count == 0 && frames >= LiveGhostReentryFx.MinUnlitRunFrames)
                    break;
            }

            ParsekLog.Info(Tag, LiveGhostReentryFx.FormatSummary(HighLogic.LoadedScene.ToString(), frames,
                maxGhosts, inAtmosphere, hot, hotLit, hotUnlit, longestUnlit, maxRaw,
                string.Join(",", new List<string>(vessels).ToArray())));

            switch (LiveGhostReentryFx.DecideOutcome(hotLit, defect))
            {
                case LiveGhostReentryFx.CellOutcome.Fail:
                    InGameAssert.Fail("a hot ghost stayed without reentry glow past the smoothing lag: "
                        + string.Join(" | ", failures.ToArray()));
                    yield break;
                case LiveGhostReentryFx.CellOutcome.Skip:
                    InGameAssert.Skip("no live flight ghost was hot enough for reentry glow in the sampling window"
                        + " (frames=" + frames.ToString(IC) + " ghosts=" + maxGhosts.ToString(IC)
                        + " inAtmosphere=" + inAtmosphere.ToString(IC)
                        + " maxRaw=" + maxRaw.ToString("F3", IC) + ")");
                    yield break;
            }
        }

        // The engine's own intensity inputs (UpdateReentryFx): the ghost's interpolated body,
        // altitude and usable surface velocity through stock's atmosphere model. False when the
        // ghost is not inside an atmosphere.
        private static bool TryRawIntensity(GhostPlaybackState state, out float raw)
        {
            raw = 0f;
            string bodyName = state.lastInterpolatedBodyName;
            if (string.IsNullOrEmpty(bodyName)) return false;
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body == null || !body.atmosphere) return false;
            double altitude = state.lastInterpolatedAltitude;
            if (double.IsNaN(altitude) || altitude >= body.atmosphereDepth) return false;
            double pressure = body.GetPressure(altitude);
            double temperature = body.GetTemperature(altitude);
            if (double.IsNaN(pressure) || pressure < 0 || double.IsNaN(temperature) || temperature < 0) return true;
            double density = body.GetDensity(pressure, temperature);
            if (double.IsNaN(density) || density < 0) return true;
            float speed = GhostSurfaceVelocity.Usable(
                state.lastInterpolatedSurfaceVelocity, state.lastInterpolatedSurfaceVelocitySource).magnitude;
            double speedOfSound = body.GetSpeedOfSound(pressure, density);
            float mach = speedOfSound > 0 ? (float)(speed / speedOfSound) : 0f;
            raw = GhostVisualBuilder.ComputeReentryIntensity(speed, (float)density, mach,
                GhostAeroFxRenderer.Constants, out _);
            return true;
        }
    }
}
