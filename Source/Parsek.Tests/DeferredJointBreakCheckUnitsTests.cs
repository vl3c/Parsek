using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The decision units extracted from the <c>ParsekFlight.DeferredJointBreakCheck</c>
    /// coroutine. The coroutine itself reads FlightGlobals after its yield and is characterized
    /// in game by the <c>JointBreakCheckCoroutine</c> category.
    /// </summary>
    [Collection("Sequential")]
    public class DeferredJointBreakCheckUnitsTests : IDisposable
    {
        private const uint RecordedPid = 1000u;
        private const uint CandidatePid = 2000u;

        private readonly List<string> logLines = new List<string>();

        public DeferredJointBreakCheckUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private int CountContaining(string text)
        {
            int n = 0;
            foreach (string l in logLines)
                if (l.Contains(text))
                    n++;
            return n;
        }

        private static ParsekFlight.DeferredScanCandidate Classify(
            uint pid,
            HashSet<uint> preBreak = null,
            RecordingTree tree = null,
            List<uint> captured = null,
            Dictionary<uint, uint> origins = null)
        {
            return ParsekFlight.ClassifyDeferredScanCandidate(
                pid, RecordedPid, preBreak, tree, captured ?? new List<uint>(),
                origins ?? new Dictionary<uint, uint>());
        }

        // ---- ClassifyDeferredScanCandidate ----

        [Fact]
        public void Scan_RecordedVessel_Skips()
        {
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Skip, Classify(RecordedPid));
        }

        [Fact]
        public void Scan_GhostMapVessel_Skips()
        {
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(CandidatePid);
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Skip, Classify(CandidatePid));
        }

        [Fact]
        public void Scan_VesselInPreBreakSnapshot_Skips()
        {
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Skip,
                Classify(CandidatePid, preBreak: new HashSet<uint> { CandidatePid }));
        }

        [Fact]
        public void Scan_NoPreBreakSnapshot_Considers()
        {
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Consider, Classify(CandidatePid, preBreak: null));
        }

        [Fact]
        public void Scan_BackgroundMember_Skips()
        {
            var tree = new RecordingTree();
            tree.BackgroundMap[CandidatePid] = "rec-bg";
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Skip, Classify(CandidatePid, tree: tree));
        }

        [Fact]
        public void Scan_NoActiveTree_Considers()
        {
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Consider, Classify(CandidatePid, tree: null));
        }

        [Fact]
        public void Scan_AlreadyCapturedByDecoupleCallback_Skips()
        {
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Skip,
                Classify(CandidatePid, captured: new List<uint> { CandidatePid }));
        }

        [Fact]
        public void Scan_OriginChainNeverReachesRecordedVessel_IsForeignOrigin()
        {
            var origins = new Dictionary<uint, uint> { { CandidatePid, 3000u } };
            Assert.Equal(ParsekFlight.DeferredScanCandidate.ForeignOrigin,
                Classify(CandidatePid, origins: origins));
        }

        [Fact]
        public void Scan_OriginChainReachesRecordedVessel_Considers()
        {
            var origins = new Dictionary<uint, uint> { { CandidatePid, 3000u }, { 3000u, RecordedPid } };
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Consider, Classify(CandidatePid, origins: origins));
        }

        [Fact]
        public void Scan_PreBreakSnapshotIsCheckedBeforeTheForeignOrigin()
        {
            var origins = new Dictionary<uint, uint> { { CandidatePid, 3000u } };
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Skip,
                Classify(CandidatePid, preBreak: new HashSet<uint> { CandidatePid }, origins: origins));
        }

        [Fact]
        public void Scan_CapturedPidIsCheckedBeforeTheForeignOrigin()
        {
            var origins = new Dictionary<uint, uint> { { CandidatePid, 3000u } };
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Skip,
                Classify(CandidatePid, captured: new List<uint> { CandidatePid }, origins: origins));
        }

        [Fact]
        public void Scan_FreshVessel_Considers()
        {
            Assert.Equal(ParsekFlight.DeferredScanCandidate.Consider,
                Classify(CandidatePid, preBreak: new HashSet<uint> { 4000u }, tree: new RecordingTree(),
                    captured: new List<uint> { 5000u }));
        }

        // ---- ReportDeferredScanForeignOriginSkips ----

        [Fact]
        public void ForeignSkips_Zero_LogsNothing()
        {
            ParsekFlight.ReportDeferredScanForeignOriginSkips(0, RecordedPid);
            Assert.Equal(0, CountContaining("came off another vessel"));
        }

        [Fact]
        public void ForeignSkips_Some_LogsOneSummaryLine()
        {
            ParsekFlight.ReportDeferredScanForeignOriginSkips(2, RecordedPid);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("DeferredJointBreakCheck: skipped 2 new vessel(s) that came off another vessel "
                    + "(decouple origin chain never reaches recordedPid=1000)"));
            Assert.Equal(1, CountContaining("came off another vessel"));
        }

        // ---- DescribeDeferredSplitCheckSource ----

        [Theory]
        [InlineData("DecoupleCreatedVessel", "decouple-only")]
        [InlineData("JointBreak", "joint-break")]
        [InlineData("None", "joint-break")]
        public void SplitCheckSource_LabelsTheTrigger(string trigger, string expected)
        {
            var value = (ParsekFlight.DeferredSplitCheckTrigger)Enum.Parse(
                typeof(ParsekFlight.DeferredSplitCheckTrigger), trigger);
            Assert.Equal(expected, ParsekFlight.DescribeDeferredSplitCheckSource(value));
        }

        // ---- EmitWithinSegmentBreakageSegmentEvents ----

        private static PartEvent Ev(PartEventType type, string name, uint pid)
        {
            return new PartEvent { ut = 1.0, partPersistentId = pid, eventType = type, partName = name };
        }

        [Fact]
        public void Breakage_NoRecorder_DoesNothing()
        {
            ParsekFlight.EmitWithinSegmentBreakageSegmentEvents(null, 50.0);
            Assert.Equal(0, CountContaining("Emitted PartDestroyed segment event"));
        }

        [Fact]
        public void Breakage_EmitsOnlyDecoupledEventsAmongTheLastFive_NewestFirst()
        {
            var rec = new FlightRecorder();
            rec.PartEvents.Add(Ev(PartEventType.Decoupled, "old.a", 1u));
            rec.PartEvents.Add(Ev(PartEventType.Decoupled, "old.b", 2u));
            rec.PartEvents.Add(Ev(PartEventType.EngineIgnited, "engine", 3u));
            rec.PartEvents.Add(Ev(PartEventType.Decoupled, "dec.a", 4u));
            rec.PartEvents.Add(Ev(PartEventType.Decoupled, "dec.b", 5u));
            rec.PartEvents.Add(Ev(PartEventType.Decoupled, "dec.c", 6u));
            rec.PartEvents.Add(Ev(PartEventType.Decoupled, "dec.d", 7u));

            ParsekFlight.EmitWithinSegmentBreakageSegmentEvents(rec, 50.0);

            Assert.Equal(4, rec.SegmentEvents.Count);
            Assert.Equal("part=dec.d pid=7", rec.SegmentEvents[0].details);
            Assert.Equal("part=dec.c pid=6", rec.SegmentEvents[1].details);
            Assert.Equal("part=dec.b pid=5", rec.SegmentEvents[2].details);
            Assert.Equal("part=dec.a pid=4", rec.SegmentEvents[3].details);
            foreach (var se in rec.SegmentEvents)
            {
                Assert.Equal(SegmentEventType.PartDestroyed, se.type);
                Assert.Equal(50.0, se.ut);
            }
        }

        [Fact]
        public void Breakage_FewerThanFiveEvents_WalksThemAll()
        {
            var rec = new FlightRecorder();
            rec.PartEvents.Add(Ev(PartEventType.Decoupled, "dec.a", 4u));
            rec.PartEvents.Add(Ev(PartEventType.Decoupled, "dec.b", 5u));

            ParsekFlight.EmitWithinSegmentBreakageSegmentEvents(rec, 10.0);

            Assert.Equal(2, rec.SegmentEvents.Count);
            Assert.Equal("part=dec.b pid=5", rec.SegmentEvents[0].details);
            Assert.Equal("part=dec.a pid=4", rec.SegmentEvents[1].details);
        }

        [Fact]
        public void Breakage_NoDecoupledEvent_EmitsNothing()
        {
            var rec = new FlightRecorder();
            rec.PartEvents.Add(Ev(PartEventType.EngineIgnited, "engine", 3u));

            ParsekFlight.EmitWithinSegmentBreakageSegmentEvents(rec, 10.0);

            Assert.Empty(rec.SegmentEvents);
        }

        [Fact]
        public void Breakage_NoPartEvents_EmitsNothing()
        {
            var rec = new FlightRecorder();
            ParsekFlight.EmitWithinSegmentBreakageSegmentEvents(rec, 10.0);
            Assert.Empty(rec.SegmentEvents);
        }

        // ---- ResolveForegroundSplitChildCause ----

        private static void SeedRecorderSet(FlightRecorder rec, string field, uint pid)
        {
            FieldInfo fi = typeof(FlightRecorder).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(fi);
            ((HashSet<uint>)fi.GetValue(rec)).Add(pid);
        }

        [Fact]
        public void Cause_JointBreakChildNotDecoupleCreated_IsCrash()
        {
            string cause = ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, new Dictionary<uint, bool>(), ParsekFlight.DeferredSplitCheckTrigger.JointBreak,
                new FlightRecorder(), 77u, out bool created, out bool decoupleOnly);
            Assert.Equal("CRASH", cause);
            Assert.False(created);
            Assert.False(decoupleOnly);
        }

        [Fact]
        public void Cause_NullStatusDictionary_IsNotDecoupleCreated()
        {
            string cause = ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, null, ParsekFlight.DeferredSplitCheckTrigger.JointBreak,
                new FlightRecorder(), 77u, out bool created, out bool decoupleOnly);
            Assert.Equal("CRASH", cause);
            Assert.False(created);
            Assert.False(decoupleOnly);
        }

        [Fact]
        public void Cause_DecoupleCreatedChild_IsDecouple()
        {
            string cause = ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, new Dictionary<uint, bool> { { CandidatePid, false } },
                ParsekFlight.DeferredSplitCheckTrigger.JointBreak,
                new FlightRecorder(), 77u, out bool created, out bool decoupleOnly);
            Assert.Equal("DECOUPLE", cause);
            Assert.True(created);
            Assert.False(decoupleOnly);
        }

        [Fact]
        public void Cause_DecoupleOnlyTrigger_IsDecoupleEvenWhenNotCaptured()
        {
            string cause = ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, new Dictionary<uint, bool>(),
                ParsekFlight.DeferredSplitCheckTrigger.DecoupleCreatedVessel,
                new FlightRecorder(), 77u, out bool created, out bool decoupleOnly);
            Assert.Equal("DECOUPLE", cause);
            Assert.False(created);
            Assert.True(decoupleOnly);
        }

        [Fact]
        public void Cause_RootPartBrokeUnderForce_IsBrokeOff()
        {
            var rec = new FlightRecorder();
            SeedRecorderSet(rec, "forceBrokenJointChildPartIds", 77u);
            string cause = ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, new Dictionary<uint, bool> { { CandidatePid, false } },
                ParsekFlight.DeferredSplitCheckTrigger.JointBreak,
                rec, 77u, out bool created, out _);
            Assert.Equal(SegmentBoundaryLogic.BrokeOffSplitCause, cause);
            Assert.True(created);
        }

        [Fact]
        public void Cause_RootPartLeftThroughPreAttachedUndock_IsUndock()
        {
            var rec = new FlightRecorder();
            SeedRecorderSet(rec, "preAttachedUndockPartIds", 77u);
            string cause = ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, new Dictionary<uint, bool> { { CandidatePid, true } },
                ParsekFlight.DeferredSplitCheckTrigger.JointBreak,
                rec, 77u, out _, out _);
            Assert.Equal("UNDOCK", cause);
        }

        [Fact]
        public void Cause_UnknownRootPart_IgnoresTheRecorderSets()
        {
            var rec = new FlightRecorder();
            SeedRecorderSet(rec, "forceBrokenJointChildPartIds", 0u);
            string cause = ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, new Dictionary<uint, bool> { { CandidatePid, false } },
                ParsekFlight.DeferredSplitCheckTrigger.JointBreak,
                rec, 0u, out _, out _);
            Assert.Equal("DECOUPLE", cause);
        }

        [Fact]
        public void Cause_NoRecorder_IsDecoupleForADecoupleCreatedChild()
        {
            string cause = ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, new Dictionary<uint, bool> { { CandidatePid, false } },
                ParsekFlight.DeferredSplitCheckTrigger.JointBreak,
                null, 77u, out bool created, out _);
            Assert.Equal("DECOUPLE", cause);
            Assert.True(created);
        }

        [Fact]
        public void Cause_LogsTheClassification()
        {
            ParsekFlight.ResolveForegroundSplitChildCause(
                CandidatePid, new Dictionary<uint, bool> { { CandidatePid, false } },
                ParsekFlight.DeferredSplitCheckTrigger.DecoupleCreatedVessel,
                new FlightRecorder(), 77u, out _, out _);
            Assert.Contains(logLines, l => l.Contains("[Boundary]")
                && l.Contains("ClassifyForegroundSplitChildCause: rootPartPid=77 decoupleCreated=True "
                    + "triggerDecoupleOnly=True brokeUnderForce=False preAttachedUndock=False => DECOUPLE"));
        }

        // ---- FormatCoalescerSplitFeedLine ----

        [Fact]
        public void FeedLine_WithSeedPoint_NamesEveryField()
        {
            string line = ParsekFlight.FormatCoalescerSplitFeedLine(
                CandidatePid, true, "DECOUPLE", true, false, 77u, new ConfigNode("VESSEL"),
                new TrajectoryPoint { ut = 99.25 }, "decouple-callback", 100.5);
            Assert.Equal(
                "Feeding split to coalescer: childPid=2000, childHasController=True, cause=DECOUPLE, "
                + "decoupleCreated=True, triggerDecoupleOnly=False, decouplerPartId=77, preSnapshot=True, "
                + "preTrajectoryPoint=True pointSource=decouple-callback branchUT=100.50 seedUT=99.25",
                line);
        }

        [Fact]
        public void FeedLine_WithoutSeedPoint_OmitsTheSeedUT()
        {
            string line = ParsekFlight.FormatCoalescerSplitFeedLine(
                CandidatePid, false, "CRASH", false, false, 0u, null,
                null, "destroyed-before-capture", 100.5);
            Assert.Equal(
                "Feeding split to coalescer: childPid=2000, childHasController=False, cause=CRASH, "
                + "decoupleCreated=False, triggerDecoupleOnly=False, decouplerPartId=0, preSnapshot=False, "
                + "preTrajectoryPoint=False pointSource=destroyed-before-capture branchUT=100.50",
                line);
        }

        [Fact]
        public void FeedLine_FormatsTheUTsInvariantly()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = ParsekFlight.FormatCoalescerSplitFeedLine(
                    CandidatePid, false, "CRASH", false, false, 0u, null,
                    new TrajectoryPoint { ut = 2.5 }, "deferred-live-sample", 1.25);
                Assert.EndsWith("branchUT=1.25 seedUT=2.50", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }
    }
}
