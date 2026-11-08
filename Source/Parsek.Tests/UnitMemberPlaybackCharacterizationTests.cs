using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Parsek.Reaim;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <see cref="GhostPlaybackEngine"/>'s UpdateUnitMemberPlayback (the loop-unit
    /// follower dispatch), driven one member at a time through
    /// <see cref="GhostPlaybackEngine.UpdateUnitMemberPlaybackForTesting"/>. Covers every headless-reachable
    /// exit: unloaded anchor, unresolved span clock, out of window (destroy, watched keep-alive), inter-cycle
    /// tail, before activation, warp hide, the cycle-change rebuild, the self-overlap branch (zero duration,
    /// first-run handover, warp hide, watched-instance pin), the descent trigger, the descent handoff hide,
    /// the loiter wrap and the launch-hold boundary secondary. The frame's spawn budget is spent before the first call, so a
    /// render path defers its ghost build instead of reaching Unity; the self-overlap and boundary-secondary
    /// builds take no budget, so those calls end on mono's refusal of the first Unity native call, which
    /// the transcript records.
    ///
    /// UpdateUnitMemberPlayback calls <c>SetActive</c> / <c>activeSelf</c> in its own body, so every cell is
    /// a <see cref="UnityEntryFactAttribute"/>: mono (CI) runs it, a host that refuses such a body on entry
    /// (the Windows .NET Framework test host) skips it.
    ///
    /// The transcript holds every log line, lifecycle / camera event and destroy, the rate-limit keys, the
    /// frame-skip counters and the returned state after each call. Numbers in it are canonicalized to their double bits before
    /// hashing, so the digest does not depend on how a runtime formats "R".
    /// </summary>
    [Collection("Sequential")]
    public class UnitMemberPlaybackCharacterizationTests : IDisposable
    {
        private readonly List<string> transcript = new List<string>();

        public UnitMemberPlaybackCharacterizationTests()
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
            GhostPlaybackEngine.MeshTraceUTOverrideForTesting = () => 7.0;
            GhostPlaybackLogic.ResetForTesting();
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

        private GhostPlaybackEngine NewEngine(GhostPlaybackLogic.LoopUnitSet units)
        {
            var engine = new GhostPlaybackEngine(new InertPositioner(transcript));
            engine.SetLoopUnits(units);
            engine.DestroyGhostResourcesOverrideForTesting = s => transcript.Add(
                "RES destroyed cycle=" + s.loopCycleIndex.ToString(CultureInfo.InvariantCulture));
            engine.OnPlaybackCompleted += e => transcript.Add("EVT Completed #" + e.Index);
            engine.OnGhostDestroyed += e => transcript.Add("EVT Destroyed #" + e.Index);
            engine.OnGhostCreated += e => transcript.Add("EVT Created #" + e.Index);
            engine.OnGhostSpawnPending += e => transcript.Add("EVT SpawnPending #" + e.Index);
            engine.OnLoopCameraAction += e => transcript.Add("EVT LoopCamera #" + e.Index + " " + e.Action);
            engine.OnOverlapCameraAction += e => transcript.Add("EVT OverlapCamera #" + e.Index + " " + e.Action);
            engine.OnLoopRestarted += e => transcript.Add("EVT LoopRestarted #" + e.Index);
            engine.OnOverlapExpired += e => transcript.Add("EVT OverlapExpired #" + e.Index);
            engine.OnSeamMarker += e => transcript.Add("EVT SeamMarker #" + e.Index);
            // The frame's spawn budget is spent up front, so a render path defers its ghost build
            // (logged) instead of reaching Unity: every call stays on managed code.
            while (engine.FrameSpawnCountForTesting < GhostPlayback.MaxSpawnsPerFrame)
                engine.IncrementFrameSpawnCountForTesting();
            return engine;
        }

        private static GhostPlaybackLogic.LoopUnitSet Set(GhostPlaybackLogic.LoopUnit unit)
        {
            var owners = new Dictionary<int, int>();
            for (int k = 0; k < unit.MemberIndices.Length; k++)
                owners[unit.MemberIndices[k]] = unit.OwnerIndex;
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { unit.OwnerIndex, unit } }, owners);
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

        private static FrameContext Ctx(double ut, float warp, int protectedIndex = -1, long protectedCycle = -1)
            => new FrameContext
            {
                currentUT = ut, warpRate = warp,
                protectedIndex = protectedIndex, protectedLoopCycleIndex = protectedCycle,
            };

        private static readonly string[] SkipCounterNames =
        {
            "frameSkipAnchorMissing", "frameSkipMissionLoopUnitInactive", "frameSkipWarpHidden",
            "frameSkipBeforeActivation",
        };

        private string SkipCounters(GhostPlaybackEngine engine)
        {
            var sb = new StringBuilder("SKIPS");
            for (int k = 0; k < SkipCounterNames.Length; k++)
            {
                FieldInfo field = typeof(GhostPlaybackEngine).GetField(
                    SkipCounterNames[k], BindingFlags.Instance | BindingFlags.NonPublic);
                sb.Append(' ').Append(SkipCounterNames[k].Substring("frameSkip".Length)).Append('=')
                    .Append(field == null ? "?" : Convert.ToString(field.GetValue(engine), CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string Describe(GhostPlaybackState state)
            => state == null
                ? "null"
                : "cycle=" + state.loopCycleIndex.ToString(CultureInfo.InvariantCulture)
                    + " shift=" + state.bodyFixedShiftSeconds.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>One member call. A non-null <paramref name="state"/> with <paramref name="ghostActive"/>
        /// is installed in the engine's slot first, the way UpdatePlayback finds it.</summary>
        private void Member(
            GhostPlaybackEngine engine, int i, IPlaybackTrajectory traj, FrameContext ctx,
            IReadOnlyList<IPlaybackTrajectory> trajs, bool suppressGhosts,
            GhostPlaybackState state, bool ghostActive, string label)
        {
            ParsekLog.ResetRateLimitsForTesting();
            if (state != null)
                engine.ghostStates[i] = state;
            transcript.Add("CALL " + label + " #" + i.ToString(CultureInfo.InvariantCulture)
                + " ut=" + ctx.currentUT.ToString("R", CultureInfo.InvariantCulture)
                + " warp=" + ctx.warpRate.ToString("R", CultureInfo.InvariantCulture)
                + " sg=" + suppressGhosts + " active=" + ghostActive + " state=" + Describe(state));
            bool hasPoints = traj.Points != null && traj.Points.Count > 0;
            try
            {
                bool member = engine.UpdateUnitMemberPlaybackForTesting(
                    i, traj, Flags(traj.RecordingId, traj.EndUT), ctx, trajs, suppressGhosts, true,
                    hasPoints, hasPoints && traj.Points.Count > 1, false, false,
                    ref state, ref ghostActive);
                transcript.Add("RET member=" + member + " active=" + ghostActive + " state=" + Describe(state));
            }
            catch (Exception ex) when (IsHeadlessUnityRefusal(ex))
            {
                transcript.Add("RET unity-native-refused");
            }
            transcript.Add("KEYS " + string.Join(" | ", RateLimitKeys()));
            transcript.Add(SkipCounters(engine)
                + " slots=" + engine.ghostStates.Count.ToString(CultureInfo.InvariantCulture)
                + " overlapSlots=" + engine.overlapGhosts.Count.ToString(CultureInfo.InvariantCulture));
        }

        // The rate-limit keys this call touched (the store is reset before each call), sorted.
        private static List<string> RateLimitKeys()
        {
            PropertyInfo prop = typeof(ParsekLog).GetProperty(
                "rateLimitStateByKey", BindingFlags.Static | BindingFlags.NonPublic);
            var dict = (System.Collections.IDictionary)prop.GetValue(null);
            var keys = new List<string>();
            foreach (object k in dict.Keys)
                keys.Add((string)k);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static bool IsHeadlessUnityRefusal(Exception ex)
            => ex is MissingMethodException || ex is SecurityException || ex is MethodAccessException;

        private static readonly Regex NumberPattern = new Regex(
            @"-?\d+(\.\d+)?(E[+-]?\d+)?", RegexOptions.CultureInvariant);

        // Every number becomes the hex of its double bits: formatting differences between runtimes
        // ("R" in particular) cannot move the digest, a changed value still does.
        private static string Canonical(string text)
            => NumberPattern.Replace(text, m =>
            {
                double v = double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                return BitConverter.DoubleToInt64Bits(v).ToString("X16", CultureInfo.InvariantCulture);
            });

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
            string text = string.Join("\n", transcript);
            string actual = "digest=" + Fnv1a64(Canonical(text)) + " lines=" + transcript.Count;
            string expected = "digest=" + expectedDigest + " lines=" + expectedLines;
            Assert.True(expected == actual, "golden mismatch\nexpected " + expected + "\nactual   " + actual
                + "\n--- transcript ---\n" + text);
        }

        // ------------------------------------------------------------------
        // Unit A: span clock, no self-overlap. Span [1000, 1100], cadence 300 (inter-cycle wait
        // 200 s), anchor 1000. Member 0 [1000, 1050], member 1 [1050, 1100], member 2 [1000, 1100]
        // whose payload starts at 1040 (ExplicitStartUT-style widening).
        // ------------------------------------------------------------------

        private static GhostPlaybackLogic.LoopUnit SpanUnit()
            => new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0, 1, 2 },
                spanStartUT: 1000, spanEndUT: 1100, cadenceSeconds: 300, phaseAnchorUT: 1000);

        private static List<IPlaybackTrajectory> SpanTrajs()
        {
            var t2 = Traj("a2", 1040, 1100);
            t2.StartUTOverride = 1000;
            return new List<IPlaybackTrajectory> { Traj("a0", 1000, 1050), Traj("a1", 1050, 1100), t2 };
        }

        [UnityEntryFact]
        public void UnitMember_SpanClock_HideAndDestroyExits()
        {
            var engine = NewEngine(Set(SpanUnit()));
            var trajs = SpanTrajs();

            var anchored = Traj("a0", 1000, 1050);
            anchored.LoopAnchorVesselId = 77u;
            Member(engine, 0, anchored, Ctx(1320, 1f), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 1 }, true, "anchor-unloaded");
            Member(engine, 0, trajs[0], Ctx(900, 1f), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 1 }, true, "clock-unresolved");
            Member(engine, 1, trajs[1], Ctx(1320, 1f), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 1 }, true, "outside-window");
            Member(engine, 1, trajs[1], Ctx(1320, 1f, protectedIndex: 1), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 1 }, true, "outside-window-watched");
            Member(engine, 1, trajs[1], Ctx(1320, 1f), trajs, false, null, false, "outside-window-no-ghost");
            Member(engine, 0, trajs[0], Ctx(1450, 1f), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 1 }, true, "inter-cycle-tail");
            Member(engine, 2, trajs[2], Ctx(1320, 1f), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 1 }, true, "before-activation");
            Member(engine, 2, trajs[2], Ctx(1320, 1f, protectedIndex: 2), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 1 }, true, "before-activation-watched");
            Member(engine, 2, trajs[2], Ctx(1320, 1f), trajs, false, null, false, "before-activation-no-ghost");

            AssertGolden("614C68A36732C552", 75);
        }

        [UnityEntryFact]
        public void UnitMember_SpanClock_WarpHideAndRender()
        {
            var engine = NewEngine(Set(SpanUnit()));
            var trajs = SpanTrajs();

            engine.overlapGhosts[0] = new List<GhostPlaybackState>();
            Member(engine, 0, trajs[0], Ctx(1320, 1000f), trajs, true,
                new GhostPlaybackState { loopCycleIndex = 1 }, false, "warp-hidden");
            // A stale self-overlap list is cleared, the cycle change rebuilds, then the render
            // path reaches the ghost build.
            engine.overlapGhosts[0] = new List<GhostPlaybackState>();
            Member(engine, 0, trajs[0], Ctx(1320, 1f), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 0 }, true, "cycle-change-render");
            Member(engine, 0, trajs[0], Ctx(1325, 1f), trajs, false, null, false, "render-fresh");

            AssertGolden("0D5935A960A92D21", 21);
        }

        // ------------------------------------------------------------------
        // Unit B: mission self-overlap. Span [1000, 1300], overlap cadence 60 < span. Member 0 is
        // the full span; member 1 is trimmed to a zero-length window.
        // ------------------------------------------------------------------

        private static GhostPlaybackLogic.LoopUnit OverlapUnit()
            => new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0, 1 },
                spanStartUT: 1000, spanEndUT: 1300, cadenceSeconds: 300, phaseAnchorUT: 1000,
                overlapCadenceSeconds: 60,
                memberWindows: new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                {
                    { 1, new GhostPlaybackLogic.LoopUnit.MemberWindow(1100, 1100) },
                });

        [UnityEntryFact]
        public void UnitMember_SelfOverlap_Branches()
        {
            var engine = NewEngine(Set(OverlapUnit()));
            var trajs = new List<IPlaybackTrajectory> { Traj("b0", 1000, 1300), Traj("b1", 1000, 1300) };

            Member(engine, 1, trajs[1], Ctx(1250, 1f), trajs, false,
                new GhostPlaybackState { loopCycleIndex = 2 }, true, "zero-duration");
            Member(engine, 0, trajs[0], Ctx(1250, 1000f), trajs, true,
                new GhostPlaybackState { loopCycleIndex = -1 }, true, "handover-then-warp");
            Member(engine, 0, trajs[0], Ctx(1250, 1000f), trajs, true, null, false, "warp-no-ghost");
            Member(engine, 0, trajs[0], Ctx(1250, 1f), trajs, false, null, false, "newest-instance");
            Member(engine, 0, trajs[0], Ctx(1250, 1f, protectedIndex: 0, protectedCycle: 2), trajs, false,
                null, false, "watched-instance-pin");
            Member(engine, 0, trajs[0], Ctx(1400, 1f, protectedIndex: 0, protectedCycle: 0), trajs, false,
                null, false, "watched-instance-ended");

            AssertGolden("C4DBE98094C4A7A9", 51);
        }

        // ------------------------------------------------------------------
        // Unit C: the re-aim descent trigger fixture shared with DescentTriggerTests (members 0, 5, 7,
        // 49, 50, 51; 49-51 are the descent set, 5 the destination transfer member).
        // ------------------------------------------------------------------

        private const double DescentPa = 1_000_000.0;
        private const double DescentDeorbit = 30_000_000.0;
        private const double DescentCapShift = -2_860_000.0;
        private const double DescentDunaTrot = 65517.859375;
        private const double DescentEntryUT = DescentPa + DescentDeorbit + DescentCapShift;

        private static List<IPlaybackTrajectory> DescentTrajs()
        {
            var trajs = new List<IPlaybackTrajectory>();
            for (int k = 0; k < 52; k++)
                trajs.Add(Traj("c" + k.ToString(CultureInfo.InvariantCulture), 0, 30_002_000));
            return trajs;
        }

        [UnityEntryFact]
        public void UnitMember_DescentTrigger_HandoffAndLoiter()
        {
            var engine = NewEngine(DescentTriggerTests.BuildDescentUnit(engage: true));
            var trajs = DescentTrajs();
            double trigger = DescentTrigger.ComputeRotationAlignedTriggerUT(
                DescentEntryUT, DescentDeorbit, DescentDunaTrot);

            // At warp so a rendering member stops at the warp gate instead of the ghost build.
            Member(engine, 49, trajs[49], Ctx(trigger + 400, 1000f), trajs, true, null, false, "descent-render");
            Member(engine, 50, trajs[50], Ctx(trigger + 400, 1000f), trajs, true,
                new GhostPlaybackState { loopCycleIndex = 0 }, true, "descent-outside-slice");
            Member(engine, 5, trajs[5], Ctx(trigger + 400, 1000f), trajs, true,
                new GhostPlaybackState { loopCycleIndex = 0 }, true, "transfer-handoff-hide");
            Member(engine, 5, trajs[5], Ctx(DescentEntryUT + 10, 1000f), trajs, true, null, false, "transfer-loiter");
            Member(engine, 49, trajs[49], Ctx(DescentEntryUT - 1, 1000f), trajs, true, null, false, "descent-inert");
            Member(engine, 7, trajs[7], Ctx(trigger + 400, 1000f), trajs, true, null, false, "ride-along");
            // At normal rate the render path shows the clock each member is driven at (the re-anchored
            // descent head, the wrapped loiter head) before the ghost build is refused.
            Member(engine, 49, trajs[49], Ctx(trigger + 400, 1f), trajs, false, null, false, "descent-render-live");
            Member(engine, 5, trajs[5], Ctx(DescentEntryUT + 10, 1f), trajs, false, null, false, "transfer-loiter-live");

            AssertGolden("7803BBB6DAF9CB5A", 64);
        }

        // ------------------------------------------------------------------
        // Unit D: launch-hold boundary-overlap secondary on the zero-slack fixture of
        // BoundaryOverlapClockTests (span [0, 1000], cadence 1000, anchor 300, T_sid 700, SOI exit
        // 600: the overlap always engages). Member 0 holds the ascent [0, 500], member 1 the rest.
        // ------------------------------------------------------------------

        private static GhostPlaybackLogic.LoopUnit LaunchHoldUnit()
            => new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, 1 }, 0, 1000, 1000, 300, 1000, null, null, null, null,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: 700, launchHoldEngaged: true, recordedSoiExitUT: 600);

        [UnityEntryFact]
        public void UnitMember_LaunchHold_BoundarySecondary()
        {
            var engine = NewEngine(Set(LaunchHoldUnit()));
            var trajs = new List<IPlaybackTrajectory> { Traj("d0", 0, 500), Traj("d1", 500, 1000) };

            // 2150 is inside cycle 1's borrow window (instance 2 launching early); 1500 is not.
            foreach (double ut in new[] { 1500.0, 2150.0, 2200.0 })
            {
                Member(engine, 0, trajs[0], Ctx(ut, 1000f), trajs, true, null, false, "launch-hold");
                Member(engine, 1, trajs[1], Ctx(ut, 1000f), trajs, true, null, false, "launch-hold");
            }
            // At normal rate: the secondary carrier drives its secondary while the primary is hidden,
            // and the in-window member renders its primary.
            Member(engine, 0, trajs[0], Ctx(2150, 1f), trajs, false, null, false, "launch-hold-live");
            Member(engine, 1, trajs[1], Ctx(2150, 1f), trajs, false, null, false, "launch-hold-live");

            AssertGolden("3247C4568CEA9CAF", 78);
        }
    }
}
