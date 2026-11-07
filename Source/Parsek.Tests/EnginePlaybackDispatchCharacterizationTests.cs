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
    /// Characterization of the headless-reachable dispatch in <see cref="GhostPlaybackEngine.UpdatePlayback"/>
    /// and <see cref="GhostPlaybackEngine"/>'s UpdateLoopingPlayback: a frame of trajectories, each routed to a
    /// different guard (skip flag with the hidden past-end completion, no renderable data, session-suppressed
    /// subtree, Re-Fly unstable anchor, before activation, unloaded loop anchor, past-end completion, warp hide for
    /// a plain / looping / overlap-looping trajectory, chain-seam shadow). No ghost mesh is built on any of these
    /// paths. The transcript (every log line, every lifecycle / completion / camera event, in order) is pinned.
    ///
    /// UpdatePlayback's frame tail reads Unity's <c>Time.realtimeSinceStartup</c> to record the frame history; the
    /// headless runtime refuses that native call (MissingMethodException on mono, SecurityException on the Windows
    /// test host), which ends the frame after everything else in it has run. The transcript records only that the
    /// call was refused.
    /// </summary>
    [Collection("Sequential")]
    public class EnginePlaybackDispatchCharacterizationTests : IDisposable
    {
        private readonly List<string> transcript = new List<string>();

        public EnginePlaybackDispatchCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            // The settings type logs once per process when first touched; which test touches it
            // first depends on test order, so that line is left out of the transcript.
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
        }

        public void Dispose()
        {
            GhostRenderTrace.ForceEnabledForTesting = false;
            GhostRenderTrace.FrameCounterOverrideForTesting = null;
            GhostRenderTrace.Reset();
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class InertPositioner : IGhostPositioner
        {
            private readonly List<string> sink;
            internal InertPositioner(List<string> sink) { this.sink = sink; }

            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx)
                => sink.Add("POS InterpolateAndPosition #" + index);
            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx, RelativeSectionPlaybackTarget target)
                => sink.Add("POS InterpolateAndPositionRelative #" + index);
            public bool TryPositionFromBodyFixedPrimary(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT, RelativeSectionPlaybackTarget target, out double bracketBeforeUT, out double bracketAfterUT)
            {
                sink.Add("POS TryPositionFromBodyFixedPrimary #" + index);
                bracketBeforeUT = double.NaN;
                bracketAfterUT = double.NaN;
                return false;
            }
            public void PositionAtPoint(int index, IPlaybackTrajectory traj, GhostPlaybackState state, TrajectoryPoint point)
                => sink.Add("POS PositionAtPoint #" + index);
            public void PositionAtSurface(int index, IPlaybackTrajectory traj, GhostPlaybackState state)
                => sink.Add("POS PositionAtSurface #" + index);
            public void PositionFromOrbit(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut)
                => sink.Add("POS PositionFromOrbit #" + index);
            public void PositionLoop(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx)
                => sink.Add("POS PositionLoop #" + index);
            public bool TryResolveExplosionAnchorPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
            {
                sink.Add("POS TryResolveExplosionAnchorPosition #" + index);
                worldPosition = default(Vector3);
                return false;
            }
            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state, IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex)
            {
                sink.Add("POS ApplyZoneRendering #" + index);
                return default(ZoneRenderingResult);
            }
            public void ClearOrbitCache() => sink.Add("POS ClearOrbitCache");
        }

        private GhostPlaybackEngine NewEngine()
        {
            var engine = new GhostPlaybackEngine(new InertPositioner(transcript));
            engine.OnPlaybackCompleted += e => transcript.Add(string.Format(CultureInfo.InvariantCulture,
                "EVT Completed #{0} active={1} pastEffEnd={2} loopFirstRun={3} ut={4}",
                e.Index, e.GhostWasActive, e.PastEffectiveEnd, e.LoopFirstRun, e.CurrentUT));
            engine.OnGhostDestroyed += e => transcript.Add("EVT Destroyed #" + e.Index);
            engine.OnGhostCreated += e => transcript.Add("EVT Created #" + e.Index);
            engine.OnGhostSpawnPending += e => transcript.Add("EVT SpawnPending #" + e.Index);
            engine.OnLoopCameraAction += e => transcript.Add("EVT LoopCamera #" + e.Index + " " + e.Action);
            engine.OnOverlapCameraAction += e => transcript.Add("EVT OverlapCamera #" + e.Index + " " + e.Action);
            engine.OnLoopRestarted += e => transcript.Add("EVT LoopRestarted #" + e.Index);
            engine.OnOverlapExpired += e => transcript.Add("EVT OverlapExpired #" + e.Index);
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

        private static MockTrajectory Traj(string id, double start, double end)
        {
            var t = new MockTrajectory().WithTimeRange(start, end);
            t.RecordingId = id;
            t.VesselName = "V-" + id;
            return t;
        }

        private static TrajectoryPlaybackFlags Flags(string id, double chainEndUT)
            => new TrajectoryPlaybackFlags { recordingId = id, chainEndUT = chainEndUT };

        private void RunFrame(GhostPlaybackEngine engine, List<IPlaybackTrajectory> trajs,
            TrajectoryPlaybackFlags[] flags, FrameContext ctx)
        {
            ParsekLog.ResetRateLimitsForTesting();
            transcript.Add("FRAME ut=" + ctx.currentUT.ToString("R", CultureInfo.InvariantCulture)
                + " warp=" + ctx.warpRate.ToString("R", CultureInfo.InvariantCulture));
            try
            {
                engine.UpdatePlayback(trajs, flags, ctx);
                transcript.Add("END completed");
            }
            catch (Exception ex) when (IsHeadlessUnityRefusal(ex))
            {
                transcript.Add("END unity-native-refused");
            }
        }

        private static bool IsHeadlessUnityRefusal(Exception ex)
            => ex is MissingMethodException || ex is SecurityException || ex is MethodAccessException;

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

        [Fact]
        public void UpdatePlayback_GuardMatrix_AtNormalWarp()
        {
            var engine = NewEngine();
            var trajs = new List<IPlaybackTrajectory>();
            var flags = new List<TrajectoryPlaybackFlags>();

            // 0: skip flag (PlaybackDisabled) past its end -> hidden past-end completion.
            var t0 = Traj("r0", 100, 200);
            t0.PlaybackEnabled = false;
            var f0 = Flags("r0", 200);
            f0.skipGhost = true;
            f0.skipReason = GhostPlaybackSkipReason.PlaybackDisabled;
            trajs.Add(t0); flags.Add(f0);

            // 1: skip flag (external vessel) -> silent skip.
            var t1 = Traj("r1", 100, 200);
            var f1 = Flags("r1", 200);
            f1.skipGhost = true;
            f1.skipReason = GhostPlaybackSkipReason.ExternalVesselSuppressed;
            trajs.Add(t1); flags.Add(f1);

            // 2: no renderable data.
            var t2 = new MockTrajectory { RecordingId = "r2", VesselName = "V-r2" };
            trajs.Add(t2); flags.Add(Flags("r2", 0));

            // 3: Re-Fly unstable anchor.
            var t3 = Traj("r3", 100, 1000);
            var f3 = Flags("r3", 1000);
            f3.anchorReFlyUnstable = true;
            trajs.Add(t3); flags.Add(f3);

            // 4: before activation.
            trajs.Add(Traj("r4", 1000, 2000)); flags.Add(Flags("r4", 2000));

            // 5: loop with an unloaded anchor.
            var t5 = Traj("r5", 100, 200).WithLoop(50);
            t5.LoopAnchorVesselId = 77u;
            trajs.Add(t5); flags.Add(Flags("r5", 200));

            // 6: plain recording past its end -> completion, then stale cleanup (no ghost).
            trajs.Add(Traj("r6", 100, 200)); flags.Add(Flags("r6", 200));

            // 7: past its own end but before the chain's effective end.
            trajs.Add(Traj("r7", 100, 200)); flags.Add(Flags("r7", 900));

            var ctx = new FrameContext { currentUT = 500, warpRate = 1f, protectedIndex = -1, protectedLoopCycleIndex = -1 };
            RunFrame(engine, trajs, flags.ToArray(), ctx);
            ctx.currentUT = 501;
            RunFrame(engine, trajs, flags.ToArray(), ctx);

            AssertGolden("F8429E3149AF0367", 23);
        }

        [Fact]
        public void UpdatePlayback_WarpHides_PlainLoopAndOverlapLoop()
        {
            var engine = NewEngine();
            var trajs = new List<IPlaybackTrajectory>
            {
                Traj("w0", 100, 1000),                    // plain, in range
                Traj("w1", 100, 200).WithLoop(500),       // single loop (period >= duration)
                Traj("w2", 100, 300).WithLoop(20),        // overlap loop (period < duration)
            };
            var flags = new[] { Flags("w0", 1000), Flags("w1", 200), Flags("w2", 300) };
            var ctx = new FrameContext { currentUT = 450, warpRate = 1000f, protectedIndex = -1, protectedLoopCycleIndex = -1 };
            RunFrame(engine, trajs, flags, ctx);
            ctx.currentUT = 1450;
            RunFrame(engine, trajs, flags, ctx);

            AssertGolden("93FE56AE1BF50B7E", 13);
        }

        [Fact]
        public void UpdatePlayback_SessionSuppressedSubtree()
        {
            var engine = NewEngine();
            var trajs = new List<IPlaybackTrajectory> { Traj("s0", 100, 1000), Traj("s1", 100, 1000) };
            var f0 = Flags("s0", 1000);
            f0.sessionSuppressed = true;
            var f1 = Flags("s1", 1000);
            f1.anchorReFlyUnstable = true;
            var ctx = new FrameContext
            {
                currentUT = 500, warpRate = 1f, protectedIndex = -1, protectedLoopCycleIndex = -1,
                activeReFlyMarker = new ReFlySessionMarker { SessionId = "sess-char", ActiveReFlyRecordingId = "s9" },
            };
            RunFrame(engine, trajs, new[] { f0, f1 }, ctx);

            AssertGolden("87B3523FBAC53004", 7);
        }

        [Fact]
        public void UpdatePlayback_ChainSeamShadow()
        {
            var engine = NewEngine();
            engine.ResolveChainNextIndex = i => i == 0 ? 1 : -1;
            // The continuation slot holds a loaded ghost; its own trajectory has no renderable
            // data, so the frame never touches that ghost.
            engine.ghostStates[1] = new GhostPlaybackState { ghost = FakeLoadedGhost() };
            var trajs = new List<IPlaybackTrajectory>
            {
                Traj("c0", 100, 1000),
                new MockTrajectory { RecordingId = "c1", VesselName = "V-c1" },
            };
            var flags = new[] { Flags("c0", 2000), Flags("c1", 2000) };
            var ctx = new FrameContext { currentUT = 500, warpRate = 1f, protectedIndex = -1, protectedLoopCycleIndex = -1 };
            RunFrame(engine, trajs, flags, ctx);

            AssertGolden("3138B1B10D0B23FE", 7);
        }

        private void Loop(GhostPlaybackEngine engine, int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx, bool suppressGhosts)
        {
            transcript.Add("LOOP #" + index.ToString(CultureInfo.InvariantCulture));
            try
            {
                engine.UpdateLoopingPlaybackForTesting(index, traj, flags, ctx, suppressGhosts, true);
                transcript.Add("RET");
            }
            catch (Exception ex) when (IsHeadlessUnityRefusal(ex))
            {
                transcript.Add("RET unity-native-refused");
            }
        }

        [Fact]
        public void UpdateLoopingPlayback_HeadlessBranches()
        {
            var engine = NewEngine();
            var ctx = new FrameContext { currentUT = 450, warpRate = 1000f, protectedIndex = -1, protectedLoopCycleIndex = -1 };

            var single = Traj("l0", 100, 200).WithLoop(500);
            var overlap = Traj("l1", 100, 300).WithLoop(20);
            var unresolvable = new MockTrajectory { RecordingId = "l2", VesselName = "V-l2", LoopPlayback = true };

            // suppressGhosts=false reaches the ghost build, which needs live Unity objects; only the
            // warp-hidden and schedule-failure exits are driven here.
            foreach (bool suppressGhosts in new[] { true })
            {
                ParsekLog.ResetRateLimitsForTesting();
                transcript.Add("CASE suppressGhosts=" + suppressGhosts);
                Loop(engine, 0, single, Flags("l0", 200), ctx, suppressGhosts);
                Loop(engine, 1, overlap, Flags("l1", 300), ctx, suppressGhosts);
                Loop(engine, 2, unresolvable, Flags("l2", 0), ctx, suppressGhosts);
                transcript.Add("STATES " + engine.ghostStates.Count.ToString(CultureInfo.InvariantCulture));
            }

            AssertGolden("96076C87BD0E3A5F", 13);
        }
    }
}
