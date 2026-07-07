using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;
using Parsek;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // S4 arrival re-stitch WIRING (docs/dev/plans/reaim-s4-arrival-restitch.md): the trigger offset
    // and the rendered rotation must come from ONE resolver cache entry (never disagree), the offset
    // must reach EVERY production DescentTrigger head/timing call site, and the eligibility stamp
    // must live only in the descent-trigger engage block. Touches ReaimPlaybackResolver.Shared +
    // ParsekLog -> Sequential.
    [Collection("Sequential")]
    public class ArrivalRestitchWiringTests : IDisposable
    {
        private const double KerbinPeriod = 9203545.0, DunaPeriod = 17315400.0;
        private const double SpanStart = 1000.0, SpanEnd = 5000.0;
        private const double RecordedDeparture = 2000.0, RecordedArrival = 5000.0, RecordedTof = 3000.0;
        private const string CommonAncestor = "Sun";
        private const string XferId = "xfer-restitch-test";
        private const double DunaTrot = 65517.859375;
        private const double Tpark = 4000.0;
        private const double CapShift = -2000.0;

        public ArrivalRestitchWiringTests()
        {
            ReaimPlaybackResolver.Shared.Clear();
            ReaimPlaybackResolver.Shared.BuilderOverrideForTesting = null;
        }

        public void Dispose()
        {
            ReaimPlaybackResolver.Shared.Clear();
            ReaimPlaybackResolver.Shared.BuilderOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        private static ReaimWindowPlanner.ReaimWindowSchedule PlanSchedule()
            => ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, RecordedDeparture, RecordedTof, SpanStart, SpanEnd, 100_000.0);

        private static ReaimMissionPlan SupportedPlan() => new ReaimMissionPlan
        {
            Supported = true,
            LaunchBody = "Kerbin",
            TargetBody = "Duna",
            CommonAncestor = CommonAncestor,
            RecordedDepartureUT = RecordedDeparture,
            RecordedArrivalUT = RecordedArrival,
            RecordedTransferTofSeconds = RecordedTof,
        };

        private static List<OrbitSegment> HeliocentricMemberSegments() => new List<OrbitSegment>
        {
            new OrbitSegment
            {
                startUT = RecordedDeparture - 100.0,
                endUT = RecordedArrival + 100.0,
                bodyName = CommonAncestor,
                isPredicted = false,
                semiMajorAxis = 1.5e10,
            },
        };

        private static List<OrbitSegment> BuildOk(
            string memberId, IReadOnlyList<OrbitSegment> memberSegments, ReaimMissionPlan plan,
            ReaimWindowPlanner.ReaimWindowSchedule schedule, long window, out double captureShiftSeconds)
        {
            captureShiftSeconds = CapShift;
            return new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = RecordedDeparture, endUT = RecordedArrival,
                    bodyName = CommonAncestor, isPredicted = false, semiMajorAxis = 1.0e10,
                },
            };
        }

        private static List<OrbitSegment> BuildMiss(
            string memberId, IReadOnlyList<OrbitSegment> memberSegments, ReaimMissionPlan plan,
            ReaimWindowPlanner.ReaimWindowSchedule schedule, long window, out double captureShiftSeconds)
        {
            captureShiftSeconds = 0.0;
            return null; // window miss => faithful
        }

        // Resolves one window on Shared (creating the cache entry) and returns its index.
        private static long ResolveOneWindowOnShared(
            ReaimPlaybackResolver.WindowSegmentsBuilderForTesting builder,
            ReaimWindowPlanner.ReaimWindowSchedule schedule, out bool resolved)
        {
            ReaimPlaybackResolver.Shared.BuilderOverrideForTesting = builder;
            resolved = ReaimPlaybackResolver.Shared.TryResolveWindowSegments(
                XferId, HeliocentricMemberSegments(), SupportedPlan(), schedule,
                schedule.PhaseAnchorUT, SpanStart, SpanEnd, schedule.CadenceSeconds,
                schedule.FirstDepartureUT + 10.0,
                out _, out long windowIndex);
            return windowIndex;
        }

        // A minimal descent-trigger LoopUnit whose transfer member id is XferId (or null).
        private static GhostPlaybackLogic.LoopUnit DescentUnit(string transferId, bool engage = true)
        {
            var windows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
            {
                { 0, new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, SpanEnd) },
            };
            var plan = SupportedPlan();
            var sched = PlanSchedule();
            return new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, 49 }, SpanStart, SpanEnd, sched.CadenceSeconds, sched.PhaseAnchorUT,
                sched.CadenceSeconds, windows, null, plan, sched,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: double.NaN, launchHoldEngaged: false,
                recordedSoiExitUT: double.NaN,
                descentMemberIndices: engage ? new[] { 49 } : null,
                recordedDeorbitUT: engage ? RecordedArrival + 500.0 : double.NaN,
                descentEndUT: engage ? RecordedArrival + 900.0 : double.NaN,
                destinationBodyRotationPeriodSeconds: engage ? DunaTrot : double.NaN,
                loiterPeriodSeconds: engage ? Tpark : double.NaN,
                captureShiftSeconds: engage ? CapShift : double.NaN,
                parkingConicEndUT: double.NaN,
                transferMemberIndex: engage ? 0 : -1,
                firstDeorbitLegStartUT: double.NaN,
                transferMemberRecordingId: transferId);
        }

        // --- Cache coherence: the offset and the rotation come from ONE entry ---

        [Fact]
        public void Offset_FromResolvedRotatedWindow_MatchesFormula()
        {
            var schedule = PlanSchedule();
            Assert.True(schedule.Valid, schedule.Reason);
            long w = ResolveOneWindowOnShared(BuildOk, schedule, out bool resolved);
            Assert.True(resolved);
            ReaimPlaybackResolver.Shared.SetArrivalRestitchRotationForTesting(XferId, w, 90.0);

            double offset = GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(DescentUnit(XferId), w);
            Assert.Equal(DunaTrot / 4.0, offset, 6);
        }

        [Fact]
        public void Offset_UnrotatedResolvedWindow_IsZero()
        {
            var schedule = PlanSchedule();
            long w = ResolveOneWindowOnShared(BuildOk, schedule, out bool resolved);
            Assert.True(resolved);
            // The production builder path stamps 0 on the direct/unrotated path; no stamp here.
            Assert.Equal(0.0,
                GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(DescentUnit(XferId), w), 9);
        }

        [Fact]
        public void Offset_DeclinedWindow_FallsBackToZero_WithUnrotatedRender()
        {
            // A window that declined to faithful renders UNROTATED; the offset must be 0 with it.
            var schedule = PlanSchedule();
            long w = ResolveOneWindowOnShared(BuildMiss, schedule, out bool resolved);
            Assert.False(resolved); // faithful
            Assert.False(ReaimPlaybackResolver.Shared.TryGetArrivalRestitchRotationDeg(XferId, w, out _));
            Assert.Equal(0.0,
                GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(DescentUnit(XferId), w), 9);
        }

        [Fact]
        public void Offset_UncachedCycle_IsZero()
        {
            var schedule = PlanSchedule();
            long w = ResolveOneWindowOnShared(BuildOk, schedule, out _);
            ReaimPlaybackResolver.Shared.SetArrivalRestitchRotationForTesting(XferId, w, 90.0);
            Assert.Equal(0.0,
                GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(DescentUnit(XferId), w + 500), 9);
        }

        [Fact]
        public void Offset_ClearedCache_IsZero()
        {
            var schedule = PlanSchedule();
            long w = ResolveOneWindowOnShared(BuildOk, schedule, out _);
            ReaimPlaybackResolver.Shared.SetArrivalRestitchRotationForTesting(XferId, w, 90.0);
            ReaimPlaybackResolver.Shared.Clear();
            Assert.Equal(0.0,
                GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(DescentUnit(XferId), w), 9);
        }

        [Fact]
        public void Offset_NonDescentUnit_IsZero()
        {
            Assert.Equal(0.0,
                GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(DescentUnit(XferId, engage: false), 0), 9);
        }

        [Fact]
        public void Offset_NullTransferMemberId_IsZero()
        {
            var schedule = PlanSchedule();
            long w = ResolveOneWindowOnShared(BuildOk, schedule, out _);
            ReaimPlaybackResolver.Shared.SetArrivalRestitchRotationForTesting(XferId, w, 90.0);
            Assert.Equal(0.0,
                GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(DescentUnit(null), w), 9);
        }

        [Fact]
        public void Offset_NegativeCycle_IsZero()
        {
            Assert.Equal(0.0,
                GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(DescentUnit(XferId), -1), 9);
        }

        [Fact]
        public void Accessor_RotationRoundTrips_OnResolvedEntryOnly()
        {
            var schedule = PlanSchedule();
            long w = ResolveOneWindowOnShared(BuildOk, schedule, out _);
            Assert.True(ReaimPlaybackResolver.Shared.TryGetArrivalRestitchRotationDeg(XferId, w, out double d0));
            Assert.Equal(0.0, d0, 9);
            ReaimPlaybackResolver.Shared.SetArrivalRestitchRotationForTesting(XferId, w, -33.5);
            Assert.True(ReaimPlaybackResolver.Shared.TryGetArrivalRestitchRotationDeg(XferId, w, out double d1));
            Assert.Equal(-33.5, d1, 9);
            Assert.False(ReaimPlaybackResolver.Shared.TryGetArrivalRestitchRotationDeg("other", w, out _));
        }

        // --- Source-text gates: the offset reaches EVERY production call site ---

        [Fact]
        public void SpanClock_EveryDescentTriggerCallSite_PassesTheResolvedOffset()
        {
            AssertAllDescentTriggerCallsCarryOffset(
                ReadParsekSource("GhostPlaybackLogic.SpanClock.cs"),
                "ResolveDescentSiteAlignOffsetSeconds(unit, unitCycle)");
        }

        [Fact]
        public void SeamStitcher_DescentTriggerCallSite_PassesTheResolvedOffset()
        {
            AssertAllDescentTriggerCallsCarryOffset(
                ReadParsekSource("MapRender/CrossMemberSeamStitcher.cs"),
                "GhostPlaybackLogic.ResolveDescentSiteAlignOffsetSeconds(unit, unitCycle)");
        }

        [Fact]
        public void Builder_StampsEligibility_OnlyInTheDescentEngageBlock()
        {
            string src = ReadParsekSource("MissionLoopUnitBuilder.cs");
            // Exactly one assignment site, and it re-stores the stamped plan into the unit's slot.
            int first = src.IndexOf("plan.ArrivalRestitchEligible = true;", StringComparison.Ordinal);
            Assert.True(first >= 0, "the builder must stamp ArrivalRestitchEligible in the engage block");
            Assert.Equal(first, src.LastIndexOf("plan.ArrivalRestitchEligible = true;", StringComparison.Ordinal));
            // The stamp lives inside the descentEngage success block: between the engage decision and
            // the engage log line.
            int engageIdx = src.IndexOf("if (descentEngage)", StringComparison.Ordinal);
            int engageLog = src.IndexOf("DESCENT TRIGGER engaged", StringComparison.Ordinal);
            Assert.True(engageIdx >= 0 && engageLog > engageIdx);
            Assert.InRange(first, engageIdx, engageLog);
            // The stamped plan is what the unit stores (the resolver reads unit.ReaimPlan).
            int restore = src.IndexOf("reaimPlan = plan;", first, StringComparison.Ordinal);
            Assert.InRange(restore, first, engageLog);
        }

        [Fact]
        public void Resolver_GatesRotation_OnEligibilityAndParkingBundle()
        {
            string src = ReadParsekSource("Reaim/ReaimPlaybackResolver.cs");
            Assert.Contains("plan.ArrivalRestitchEligible && hasDepartureOverride", src);
        }

        private static void AssertAllDescentTriggerCallsCarryOffset(string src, string expectedArg)
        {
            string[] calls =
            {
                "Parsek.Reaim.DescentTrigger.TryResolveDescentMemberHead(",
                "Parsek.Reaim.DescentTrigger.ComputeDescentMemberHead(",
                "Parsek.Reaim.DescentTrigger.ComputeDescentTiming(",
                "Parsek.Reaim.DescentTrigger.TryComputeTransferDeorbitHead(",
            };
            int found = 0;
            foreach (string call in calls)
            {
                int i = 0;
                while ((i = src.IndexOf(call, i, StringComparison.Ordinal)) >= 0)
                {
                    int k = i + call.Length;
                    int depth = 1;
                    while (depth > 0 && k < src.Length)
                    {
                        char c = src[k];
                        if (c == '(') depth++;
                        else if (c == ')') depth--;
                        k++;
                    }
                    string callText = src.Substring(i, k - i);
                    Assert.True(callText.Contains(expectedArg),
                        $"a {call.TrimEnd('(')} call site does not pass the S4 site-align offset:\n{callText}");
                    found++;
                    i = k;
                }
            }
            Assert.True(found > 0, "expected at least one DescentTrigger call site in the file");
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
