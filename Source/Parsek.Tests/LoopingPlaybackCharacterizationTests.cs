using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security;
using System.Text;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <see cref="GhostPlaybackEngine"/>'s single-ghost loop path
    /// (UpdateLoopingPlayback with a period at least as long as the recording): the schedule and loop-UT
    /// failures, the warp hide, the cycle boundary (pending and loaded ghost, anchor retired or not), the
    /// first spawn, the zone hide, the visual rehydrate, the pause window and the primary positioning.
    ///
    /// Each case runs one call on a fresh engine and records every log line, every lifecycle / camera event,
    /// every positioner call, the frame skip counters and the ghost state left behind. A loaded ghost is a
    /// GameObject that Unity's managed null check reads as alive (its native pointer is set, nothing native
    /// backs it), so the loaded branches run until their first native call; the headless runtime refuses that
    /// call (MissingMethodException on mono) and the transcript records the refusal, which pins everything
    /// the branch did before it. The distance resolvers and the mesh-trace clock read live Unity / KSP state,
    /// so the engine's test overrides fix them.
    ///
    /// Every cell enters a method whose body calls a Unity native method, so each is a
    /// <see cref="UnityEntryFactAttribute"/>: it runs on mono, which CI runs, and is skipped on a host that
    /// refuses such a body on entry (the Windows .NET Framework test host).
    /// </summary>
    [Collection("Sequential")]
    public class LoopingPlaybackCharacterizationTests : IDisposable
    {
        private readonly List<string> transcript = new List<string>();

        public LoopingPlaybackCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line =>
            {
                if (!line.Contains("[Settings]"))
                    transcript.Add("LOG " + line);
            };
            ParsekLog.VerboseOverrideForTesting = true;
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = true;
            GhostRenderTrace.FrameCounterOverrideForTesting = () => 1;
            GhostPlaybackLogic.ResetForTesting();
            // The mesh lifecycle and loop cycle traces stamp Planetarium's clock, which is not there headless.
            GhostPlaybackEngine.MeshTraceUTOverrideForTesting = () => 12345.0;
        }

        public void Dispose()
        {
            GhostPlaybackEngine.MeshTraceUTOverrideForTesting = null;
            GhostRenderTrace.ForceEnabledForTesting = false;
            GhostRenderTrace.FrameCounterOverrideForTesting = null;
            GhostRenderTrace.Reset();
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class ScriptedPositioner : IGhostPositioner
        {
            private readonly List<string> sink;
            internal ZoneRenderingResult Zone;
            internal bool RetireOnPositionLoop;

            internal ScriptedPositioner(List<string> sink) { this.sink = sink; }

            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx)
                => sink.Add("POS InterpolateAndPosition #" + index + " ut=" + F(ut) + " fx=" + suppressFx);
            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx, RelativeSectionPlaybackTarget target)
                => sink.Add("POS InterpolateAndPositionRelative #" + index + " ut=" + F(ut));
            public bool TryPositionFromBodyFixedPrimary(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT, RelativeSectionPlaybackTarget target, out double bracketBeforeUT, out double bracketAfterUT)
            {
                sink.Add("POS TryPositionFromBodyFixedPrimary #" + index + " ut=" + F(playbackUT));
                bracketBeforeUT = double.NaN;
                bracketAfterUT = double.NaN;
                return false;
            }
            public void PositionAtPoint(int index, IPlaybackTrajectory traj, GhostPlaybackState state, TrajectoryPoint point)
                => sink.Add("POS PositionAtPoint #" + index + " ut=" + F(point.ut));
            public void PositionAtSurface(int index, IPlaybackTrajectory traj, GhostPlaybackState state)
                => sink.Add("POS PositionAtSurface #" + index);
            public void PositionFromOrbit(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut)
                => sink.Add("POS PositionFromOrbit #" + index + " ut=" + F(ut));
            public void PositionLoop(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx)
            {
                sink.Add("POS PositionLoop #" + index + " ut=" + F(ut) + " fx=" + suppressFx);
                if (RetireOnPositionLoop && state != null)
                    state.anchorRetiredThisFrame = true;
            }
            public bool TryResolveExplosionAnchorPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
            {
                sink.Add("POS TryResolveExplosionAnchorPosition #" + index);
                worldPosition = default(Vector3);
                return false;
            }
            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state, IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex)
            {
                sink.Add("POS ApplyZoneRendering #" + index + " ut=" + F(playbackUT) + " distance=" + F(distance)
                    + " protected=" + protectedIndex.ToString(CultureInfo.InvariantCulture));
                return Zone;
            }
        }

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private ScriptedPositioner positioner;

        private GhostPlaybackEngine NewEngine(bool spendSpawnBudget)
        {
            positioner = new ScriptedPositioner(transcript);
            var engine = new GhostPlaybackEngine(positioner);
            // A loaded ghost's distance reads its transform; the fixed distances keep the frame on managed code.
            engine.ResolvePlaybackDistanceOverride = (i, t, s, ut) => 1500.0;
            engine.ResolvePlaybackActiveVesselDistanceOverride = (i, t, s, ut) => 1200.0;
            engine.DestroyGhostResourcesOverrideForTesting = s => transcript.Add(
                "RES destroyed cycle=" + s.loopCycleIndex.ToString(CultureInfo.InvariantCulture));
            engine.OnPlaybackCompleted += e => transcript.Add("EVT Completed #" + e.Index);
            engine.OnGhostDestroyed += e => transcript.Add("EVT Destroyed #" + e.Index);
            engine.OnGhostCreated += e => transcript.Add("EVT Created #" + e.Index);
            engine.OnGhostSpawnPending += e => transcript.Add("EVT SpawnPending #" + e.Index);
            engine.OnLoopCameraAction += e => transcript.Add("EVT LoopCamera #" + e.Index + " " + e.Action
                + " holdUntil=" + F(e.HoldUntilUT));
            engine.OnOverlapCameraAction += e => transcript.Add("EVT OverlapCamera #" + e.Index + " " + e.Action);
            engine.OnLoopRestarted += e => transcript.Add("EVT LoopRestarted #" + e.Index
                + " prev=" + e.PreviousCycleIndex.ToString(CultureInfo.InvariantCulture)
                + " new=" + e.NewCycleIndex.ToString(CultureInfo.InvariantCulture)
                + " explosion=" + e.ExplosionFired);
            engine.OnOverlapExpired += e => transcript.Add("EVT OverlapExpired #" + e.Index);
            if (spendSpawnBudget)
            {
                while (engine.FrameSpawnCountForTesting < GhostPlayback.MaxSpawnsPerFrame)
                    engine.IncrementFrameSpawnCountForTesting();
            }
            return engine;
        }

        private static GameObject FakeLoadedGhost()
        {
            var ghost = (GameObject)FormatterServices.GetUninitializedObject(typeof(GameObject));
            FieldInfo cachedPtr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            cachedPtr?.SetValue(ghost, new IntPtr(1));
            return ghost;
        }

        // A 100 s recording from UT 100 looping every 500 s: cycle N plays [100 + 500N, 200 + 500N] and
        // pauses until the next launch.
        private static MockTrajectory LoopTraj(string id)
        {
            var t = new MockTrajectory().WithTimeRange(100, 200).WithLoop(500);
            t.RecordingId = id;
            t.VesselName = "V-" + id;
            return t;
        }

        private static TrajectoryPlaybackFlags Flags(string id)
            => new TrajectoryPlaybackFlags { recordingId = id, chainEndUT = 200 };

        private static FrameContext Ctx(double ut, float warp)
            => new FrameContext
            {
                currentUT = ut,
                warpRate = warp,
                protectedIndex = -1,
                protectedLoopCycleIndex = -1,
                autoLoopIntervalSeconds = 500,
            };

        private static bool IsHeadlessUnityRefusal(Exception ex)
            => ex is MissingMethodException || ex is SecurityException || ex is MethodAccessException;

        private void Run(string label, GhostPlaybackEngine engine, int index, IPlaybackTrajectory traj,
            FrameContext ctx, bool suppressGhosts, bool suppressVisualFx)
        {
            ParsekLog.ResetRateLimitsForTesting();
            transcript.Add("CASE " + label + " ut=" + F(ctx.currentUT) + " warp=" + F(ctx.warpRate)
                + " suppressGhosts=" + suppressGhosts + " suppressFx=" + suppressVisualFx);
            try
            {
                engine.UpdateLoopingPlaybackForTesting(
                    index, traj, Flags(traj.RecordingId), ctx, suppressGhosts, suppressVisualFx);
                transcript.Add("RET");
            }
            catch (Exception ex) when (IsHeadlessUnityRefusal(ex))
            {
                transcript.Add("RET unity-native-refused");
            }
            transcript.Add(DescribeAfter(engine, index));
        }

        private static string DescribeAfter(GhostPlaybackEngine engine, int index)
        {
            var sb = new StringBuilder("AFTER states=");
            sb.Append(engine.ghostStates.Count.ToString(CultureInfo.InvariantCulture));
            if (engine.ghostStates.TryGetValue(index, out GhostPlaybackState s) && s != null)
            {
                sb.Append(" cycle=").Append(s.loopCycleIndex.ToString(CultureInfo.InvariantCulture));
                sb.Append(" ghost=").Append(s.ghost != null ? "loaded" : "none");
                sb.Append(" pending=").Append(s.pendingSpawnLifecycle);
                sb.Append(" anchorRetired=").Append(s.anchorRetiredThisFrame);
                sb.Append(" explosionFired=").Append(s.explosionFired);
            }
            sb.Append(" spawnCount=").Append(engine.FrameSpawnCountForTesting.ToString(CultureInfo.InvariantCulture));
            sb.Append(" spawnDeferred=").Append(engine.FrameSpawnDeferredForTesting.ToString(CultureInfo.InvariantCulture));
            sb.Append(" reserveAttempts=").Append(engine.SpawnReserveAttemptCountForTesting.ToString(CultureInfo.InvariantCulture));
            sb.Append(" loadAttempts=").Append(engine.VisualLoadAttemptCountForTesting.ToString(CultureInfo.InvariantCulture));
            var fields = typeof(GhostPlaybackEngine).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            var names = new List<string>();
            foreach (FieldInfo f in fields)
            {
                if (f.FieldType == typeof(int) && f.Name.StartsWith("frameSkip", StringComparison.Ordinal))
                    names.Add(f.Name);
            }
            names.Sort(StringComparer.Ordinal);
            foreach (string n in names)
            {
                int v = (int)typeof(GhostPlaybackEngine).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(engine);
                if (v != 0)
                    sb.Append(' ').Append(n).Append('=').Append(v.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private string Transcript() => string.Join("\n", transcript);

        private static string Fnv1a64(string s)
        {
            ulong h = 14695981039346656037UL;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 1099511628211UL;
            }
            return h.ToString("X16", CultureInfo.InvariantCulture);
        }

        private void AssertGolden(string expectedDigest, int expectedLines)
        {
            string text = Transcript();
            string actual = "digest=" + Fnv1a64(text) + " lines=" + transcript.Count;
            string expected = "digest=" + expectedDigest + " lines=" + expectedLines;
            Assert.True(expected == actual, "golden mismatch\nexpected " + expected + "\nactual   " + actual
                + "\n--- transcript ---\n" + text);
        }

        private static GhostPlaybackState LoadedState(long cycle)
            => new GhostPlaybackState { ghost = FakeLoadedGhost(), loopCycleIndex = cycle, vesselName = "V" };

        private static GhostPlaybackState PendingState(long cycle)
            => new GhostPlaybackState { loopCycleIndex = cycle, vesselName = "V" };

        [UnityEntryFact]
        public void Golden_GuardsAndWarpHide()
        {
            // Schedule resolution fails (no points) with a loaded ghost: destroy and count.
            var e = NewEngine(true);
            var noPoints = new MockTrajectory { RecordingId = "np", VesselName = "V-np", LoopPlayback = true };
            e.ghostStates[0] = LoadedState(0);
            Run("schedule-fail-loaded", e, 0, noPoints, Ctx(450, 1f), false, false);

            // Before the schedule start: the loop UT computation fails.
            e = NewEngine(true);
            e.ghostStates[0] = LoadedState(0);
            Run("loop-ut-fail-loaded", e, 0, LoopTraj("u"), Ctx(50, 1f), false, false);
            e = NewEngine(true);
            Run("loop-ut-fail-none", e, 0, LoopTraj("u"), Ctx(50, 1f), false, false);

            // High warp hides the moving loop ghost.
            e = NewEngine(true);
            Run("warp-hide-none", e, 0, LoopTraj("w"), Ctx(150, 1000f), true, false);
            e = NewEngine(true);
            e.ghostStates[0] = PendingState(0);
            Run("warp-hide-pending", e, 0, LoopTraj("w"), Ctx(150, 1000f), true, false);
            e = NewEngine(true);
            e.ghostStates[0] = LoadedState(0);
            Run("warp-hide-loaded", e, 0, LoopTraj("w"), Ctx(150, 1000f), true, false);
            // suppressGhosts false keeps the ghost at the same warp.
            e = NewEngine(true);
            e.ghostStates[0] = PendingState(0);
            Run("warp-not-suppressed-pending", e, 0, LoopTraj("w"), Ctx(150, 1000f), false, false);

            AssertGolden("2237A9A00CCC98B7", 40);
        }

        [UnityEntryFact]
        public void Golden_CycleBoundary()
        {
            // A pending (never built) ghost crosses a cycle boundary: the cycle index advances quietly.
            var e = NewEngine(true);
            e.ghostStates[0] = PendingState(0);
            Run("cycle-pending", e, 0, LoopTraj("c"), Ctx(650, 1f), false, false);

            // A loaded ghost crosses it: endpoint positioning, then the camera / restart side effects.
            e = NewEngine(true);
            e.ghostStates[0] = LoadedState(0);
            Run("cycle-loaded", e, 0, LoopTraj("c"), Ctx(650, 1f), false, false);

            // The endpoint positioner retires the anchor: no explosion, camera or restart events.
            e = NewEngine(true);
            positioner.RetireOnPositionLoop = true;
            e.ghostStates[0] = LoadedState(0);
            Run("cycle-loaded-retired", e, 0, LoopTraj("c"), Ctx(650, 1f), false, false);

            // A destroyed vessel: the endpoint explosion check.
            e = NewEngine(true);
            var destroyed = LoopTraj("x");
            destroyed.TerminalStateValue = TerminalState.Destroyed;
            e.ghostStates[0] = LoadedState(0);
            Run("cycle-loaded-destroyed", e, 0, destroyed, Ctx(650, 1f), false, false);

            AssertGolden("8B1A159181A113B6", 24);
        }

        [UnityEntryFact]
        public void Golden_SpawnZoneAndRehydrate()
        {
            // First spawn with the frame's spawn budget spent: deferred.
            var e = NewEngine(true);
            Run("first-spawn-budget-spent", e, 0, LoopTraj("s"), Ctx(150, 1f), false, false);

            // First spawn of a debris trajectory with no snapshot: the build fails and the state is dropped.
            e = NewEngine(false);
            var debris = LoopTraj("d");
            debris.IsDebris = true;
            Run("first-spawn-load-failed", e, 0, debris, Ctx(150, 1f), false, false);

            // First spawn that reaches the mesh build.
            e = NewEngine(false);
            Run("first-spawn-build", e, 0, LoopTraj("b"), Ctx(150, 1f), false, false);

            // Zone hide, pending and loaded.
            e = NewEngine(true);
            positioner.Zone = new ZoneRenderingResult { hiddenByZone = true };
            e.ghostStates[0] = PendingState(0);
            Run("zone-hidden-pending", e, 0, LoopTraj("z"), Ctx(150, 1f), false, false);
            e = NewEngine(true);
            positioner.Zone = new ZoneRenderingResult { hiddenByZone = true };
            e.ghostStates[0] = LoadedState(0);
            Run("zone-hidden-loaded", e, 0, LoopTraj("z"), Ctx(150, 1f), false, false);

            // Visible again with no visuals: the rehydrate is throttled, or fails for a snapshot-less debris.
            e = NewEngine(true);
            e.ghostStates[0] = PendingState(0);
            Run("rehydrate-budget-spent", e, 0, LoopTraj("r"), Ctx(150, 1f), false, false);
            e = NewEngine(true);
            var firstSpawnContinue = PendingState(0);
            firstSpawnContinue.pendingSpawnLifecycle = PendingSpawnLifecycle.LoopEnter;
            e.ghostStates[0] = firstSpawnContinue;
            Run("rehydrate-first-spawn-continue-budget-spent", e, 0, LoopTraj("r"), Ctx(150, 1f), false, false);
            e = NewEngine(false);
            var debrisR = LoopTraj("rd");
            debrisR.IsDebris = true;
            e.ghostStates[0] = PendingState(0);
            Run("rehydrate-load-failed", e, 0, debrisR, Ctx(150, 1f), false, false);

            AssertGolden("5DABA917DC23F6AD", 48);
        }

        [UnityEntryFact]
        public void Golden_PauseWindowAndPrimary()
        {
            // In the pause window after the recording ends.
            var e = NewEngine(true);
            e.ghostStates[0] = LoadedState(0);
            Run("pause-window-loaded", e, 0, LoopTraj("p"), Ctx(450, 1f), false, false);

            // Mid-cycle with a loaded ghost: position, then the visuals.
            e = NewEngine(true);
            e.ghostStates[0] = LoadedState(0);
            Run("primary-loaded", e, 0, LoopTraj("q"), Ctx(150, 1f), false, false);
            e = NewEngine(true);
            e.ghostStates[0] = LoadedState(0);
            Run("primary-loaded-suppress-fx", e, 0, LoopTraj("q"), Ctx(150, 1f), false, true);
            e = NewEngine(true);
            positioner.Zone = new ZoneRenderingResult { skipPartEvents = true, suppressVisualFx = true, reduceFidelity = true };
            e.ghostStates[0] = LoadedState(0);
            Run("primary-loaded-zone-reduced", e, 0, LoopTraj("q"), Ctx(150, 1f), false, false);

            // The positioner retires the anchor mid-cycle.
            e = NewEngine(true);
            positioner.RetireOnPositionLoop = true;
            e.ghostStates[0] = LoadedState(0);
            Run("primary-loaded-retired", e, 0, LoopTraj("q"), Ctx(150, 1f), false, false);

            AssertGolden("B41CE793CD6EC6B7", 33);
        }

        // ------------------------------------------------------------------
        // The extracted pieces, called directly.
        // ------------------------------------------------------------------

        private static int SkipCount(GhostPlaybackEngine engine, string field)
            => (int)typeof(GhostPlaybackEngine).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(engine);

        [UnityEntryFact]
        public void HideLoopGhostAtWarp_NoLoadedGhost_TracesAndCountsWithoutACameraEvent()
        {
            var e = NewEngine(true);
            e.HideLoopGhostAtWarp(0, LoopTraj("w"), Flags("w"), Ctx(150, 1000f), PendingState(0), ghostActive: false);

            Assert.Contains(transcript, l => l.Contains("reason=loop-warp-hidden"));
            Assert.DoesNotContain(transcript, l => l.StartsWith("EVT LoopCamera", StringComparison.Ordinal));
            Assert.Equal(1, SkipCount(e, "frameSkipWarpHidden"));
        }

        [UnityEntryFact]
        public void HandleLoopCycleBoundary_PendingGhost_AdvancesTheCycleWithoutRestartEvents()
        {
            var e = NewEngine(true);
            var state = PendingState(0);
            e.ghostStates[0] = state;

            e.HandleLoopCycleBoundary(0, LoopTraj("c"), Flags("c"), Ctx(650, 1f), state, 150.0, 1);

            Assert.Equal(1, state.loopCycleIndex);
            Assert.DoesNotContain(transcript, l => l.StartsWith("EVT LoopRestarted", StringComparison.Ordinal));
            Assert.DoesNotContain(transcript, l => l.StartsWith("EVT LoopCamera", StringComparison.Ordinal));
            Assert.DoesNotContain(transcript, l => l.StartsWith("POS ", StringComparison.Ordinal));
        }

        [UnityEntryFact]
        public void TrySpawnLoopFirstGhost_SpawnBudgetSpent_DefersWithoutAState()
        {
            var e = NewEngine(true);
            GhostPlaybackState state = null;
            bool ghostActive = false;

            Assert.False(e.TrySpawnLoopFirstGhost(0, LoopTraj("s"), Flags("s"), 150.0, 0, ref state, ref ghostActive));

            Assert.Null(state);
            Assert.False(ghostActive);
            Assert.Empty(e.ghostStates);
            Assert.Equal(1, e.FrameSpawnDeferredForTesting);
            Assert.Contains(transcript, l => l.Contains("Spawn throttled (loop-first-spawn): #0 deferred to next frame"));
        }

        [UnityEntryFact]
        public void TrySpawnLoopFirstGhost_BuildFails_DropsTheRegisteredState()
        {
            var e = NewEngine(false);
            var debris = LoopTraj("d");
            debris.IsDebris = true;
            GhostPlaybackState state = null;
            bool ghostActive = true;

            Assert.False(e.TrySpawnLoopFirstGhost(0, debris, Flags("d"), 150.0, 3, ref state, ref ghostActive));

            Assert.NotNull(state);
            Assert.Equal(3, state.loopCycleIndex);
            Assert.Equal(PendingSpawnLifecycle.LoopEnter, state.pendingSpawnLifecycle);
            Assert.True(ghostActive);
            Assert.Empty(e.ghostStates);
            Assert.Equal(1, SkipCount(e, "frameSkipVisualLoadFailed"));
            Assert.DoesNotContain(transcript, l => l.StartsWith("EVT SpawnPending", StringComparison.Ordinal));
        }

        [UnityEntryFact]
        public void TryRehydrateLoopGhostVisuals_ThrottledOrFailed_ReturnsFalse()
        {
            var e = NewEngine(true);
            var state = PendingState(0);
            e.ghostStates[0] = state;
            Assert.False(e.TryRehydrateLoopGhostVisuals(0, LoopTraj("r"), state, 150.0));
            Assert.Contains(transcript, l => l.Contains("Spawn throttled (loop-distance-tier-rehydrate)"));

            state.pendingSpawnLifecycle = PendingSpawnLifecycle.LoopEnter;
            ParsekLog.ResetRateLimitsForTesting();
            Assert.False(e.TryRehydrateLoopGhostVisuals(0, LoopTraj("r"), state, 150.0));
            Assert.Contains(transcript, l => l.Contains("Spawn throttled (loop-first-spawn-continue)"));

            var f = NewEngine(false);
            var debris = LoopTraj("rd");
            debris.IsDebris = true;
            var failing = PendingState(0);
            f.ghostStates[0] = failing;
            Assert.False(f.TryRehydrateLoopGhostVisuals(0, debris, failing, 150.0));
            Assert.Equal(1, SkipCount(f, "frameSkipVisualLoadFailed"));
            Assert.Single(f.ghostStates);
        }

        [UnityEntryFact]
        public void RenderLoopPrimaryFrame_ClearsTheRetireSignalBeforePositioning()
        {
            var e = NewEngine(true);
            var state = LoadedState(0);
            state.anchorRetiredThisFrame = true;
            e.ghostStates[0] = state;
            try
            {
                e.RenderLoopPrimaryFrame(0, LoopTraj("q"), Ctx(150, 1f), state, 150.0,
                    skipLoopPartEvents: false, effectiveSuppressVisualFx: false);
            }
            catch (Exception ex) when (IsHeadlessUnityRefusal(ex))
            {
            }

            Assert.False(state.anchorRetiredThisFrame);
            Assert.Contains(transcript, l => l.StartsWith("POS PositionLoop #0 ut=150", StringComparison.Ordinal)
                || l.StartsWith("POS InterpolateAndPosition #0 ut=150", StringComparison.Ordinal)
                || l.StartsWith("POS TryPositionFromBodyFixedPrimary #0 ut=150", StringComparison.Ordinal));
        }
    }
}
