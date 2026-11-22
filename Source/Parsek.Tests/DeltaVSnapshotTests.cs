using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Mission Profile P1a (design-mission-profile.md section 8.1): the recorded delta-v
    /// snapshots. Covers the pure trigger / settle / timeout state machine, the cap, the
    /// DELTAV node codec, the optimizer split / merge and quickload-trim carries, and the
    /// Spent / Left arithmetic the Profile window will read.
    /// </summary>
    [Collection("Sequential")]
    public class DeltaVSnapshotTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorSuppress;

        public DeltaVSnapshotTests()
        {
            priorSuppress = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorSuppress;
        }

        private static DeltaVReadiness Readiness(
            bool enabled = true, bool eligible = true, bool ready = true, double delay = 0.5)
        {
            return new DeltaVReadiness
            {
                CalculationsEnabled = enabled,
                VesselEligible = eligible,
                Ready = ready,
                EventDelaySeconds = delay,
            };
        }

        private static DeltaVSnapshot Ok(double ut, DeltaVTrigger trigger, double vacuum, params DeltaVStageReading[] stages)
        {
            return new DeltaVSnapshot(ut, trigger, DeltaVSnapshotStatus.Ok, vacuum, vacuum * 0.9, vacuum * 0.95,
                stages.Length > 0 ? stages : new[] { new DeltaVStageReading(0, vacuum, 60.0) });
        }

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                altitude = 50000.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        private static Recording MakeRecording(string id, double startUT, double midUT, double endUT)
        {
            var rec = new Recording { RecordingId = id };
            rec.Points.Add(PointAt(startUT));
            rec.Points.Add(PointAt(midUT));
            rec.Points.Add(PointAt(endUT));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint> { PointAt(startUT), PointAt(midUT), PointAt(endUT) },
            });
            return rec;
        }

        #region Trigger set and request acceptance

        [Theory]
        [InlineData(DeltaVTrigger.Start, true)]
        [InlineData(DeltaVTrigger.BurnStart, false)]
        [InlineData(DeltaVTrigger.BurnEnd, true)]
        [InlineData(DeltaVTrigger.Staging, true)]
        [InlineData(DeltaVTrigger.Separation, true)]
        [InlineData(DeltaVTrigger.Dock, true)]
        [InlineData(DeltaVTrigger.Undock, true)]
        [InlineData(DeltaVTrigger.Board, true)]
        [InlineData(DeltaVTrigger.Eva, true)]
        [InlineData(DeltaVTrigger.SwitchIn, true)]
        [InlineData(DeltaVTrigger.Resume, true)]
        [InlineData(DeltaVTrigger.End, false)]
        public void RequiresSettle_OnlyBeforeStateReadsSkipTheWait(object trigger, bool expected)
        {
            Assert.Equal(expected, DeltaVSnapshotTracker.RequiresSettle((DeltaVTrigger)trigger));
        }

        [Theory]
        [InlineData(BranchPointType.Undock, DeltaVTrigger.Undock)]
        [InlineData(BranchPointType.Dock, DeltaVTrigger.Dock)]
        [InlineData(BranchPointType.Board, DeltaVTrigger.Board)]
        [InlineData(BranchPointType.EVA, DeltaVTrigger.Eva)]
        [InlineData(BranchPointType.JointBreak, DeltaVTrigger.Separation)]
        [InlineData(BranchPointType.Breakup, DeltaVTrigger.Separation)]
        [InlineData(BranchPointType.VesselSwitchContinuation, DeltaVTrigger.SwitchIn)]
        public void TriggerForBranch_MapsEachSplitAndMerge(BranchPointType branch, object expected)
        {
            Assert.Equal((DeltaVTrigger)expected, FlightRecorder.DeltaVTriggerForBranch(branch));
        }

        [Fact]
        public void ClassifyRequest_RefusesStoppedDisabledAndEva()
        {
            Assert.Equal("not-recording", DeltaVSnapshotTracker.ClassifyRequest(false, true, false));
            Assert.Equal("calculations-disabled", DeltaVSnapshotTracker.ClassifyRequest(true, false, false));
            Assert.Equal("eva-vessel", DeltaVSnapshotTracker.ClassifyRequest(true, true, true));
            Assert.Null(DeltaVSnapshotTracker.ClassifyRequest(true, true, false));
        }

        [Fact]
        public void Request_SameKindWhilePending_KeepsTheFirst()
        {
            var tracker = new DeltaVSnapshotTracker();
            Assert.Null(tracker.Request(DeltaVTrigger.Staging, 10.0));
            Assert.Equal("coalesced", tracker.Request(DeltaVTrigger.Staging, 10.2));
            Assert.Null(tracker.Request(DeltaVTrigger.BurnStart, 10.2));
            Assert.Equal(2, tracker.PendingCount);

            // The kept request is the first one: it settles 0.75 s after 10.0, not after 10.2.
            List<DeltaVDecision> decisions = tracker.Decide(10.0 + 0.5 + DeltaVSnapshotTracker.SettleMarginSeconds,
                Readiness());
            Assert.Equal(2, decisions.Count);
            Assert.Contains(decisions, d => d.Trigger == DeltaVTrigger.Staging && d.Outcome == DeltaVRequestOutcome.Complete);
            Assert.Equal(0, tracker.PendingCount);
        }

        [Fact]
        public void Request_PastThePendingCap_IsRefused()
        {
            var tracker = new DeltaVSnapshotTracker();
            DeltaVTrigger[] kinds = (DeltaVTrigger[])Enum.GetValues(typeof(DeltaVTrigger));
            int filed = 0;
            foreach (DeltaVTrigger kind in kinds)
            {
                string reason = tracker.Request(kind, 0.0);
                if (filed < DeltaVSnapshotTracker.MaxPendingRequests)
                {
                    Assert.Null(reason);
                    filed++;
                }
                else
                {
                    Assert.Equal("pending-full", reason);
                }
            }
            Assert.Equal(DeltaVSnapshotTracker.MaxPendingRequests, tracker.PendingCount);
        }

        [Fact]
        public void EngineActivity_EdgesBecomeBurnStartAndBurnEnd()
        {
            var tracker = new DeltaVSnapshotTracker();
            tracker.InitializeEngineActivity(burning: false);
            Assert.Null(tracker.NoteEngineActivity(false));
            Assert.Equal(DeltaVTrigger.BurnStart, tracker.NoteEngineActivity(true));
            Assert.Null(tracker.NoteEngineActivity(true));
            Assert.Equal(DeltaVTrigger.BurnEnd, tracker.NoteEngineActivity(false));
        }

        [Fact]
        public void EngineActivity_RunningAtStart_IsNoBurnStart()
        {
            var tracker = new DeltaVSnapshotTracker();
            tracker.InitializeEngineActivity(burning: true);
            Assert.Null(tracker.NoteEngineActivity(true));
            Assert.Equal(DeltaVTrigger.BurnEnd, tracker.NoteEngineActivity(false));

            var unseeded = new DeltaVSnapshotTracker();
            Assert.Null(unseeded.NoteEngineActivity(true));
            Assert.Equal(DeltaVTrigger.BurnEnd, unseeded.NoteEngineActivity(false));
        }

        #endregion

        #region Pending, settle and timeout

        [Fact]
        public void DecideOne_SettleKind_WaitsForTheEventDelayEvenWhenReady()
        {
            double delay = 0.5;
            double minWait = delay + DeltaVSnapshotTracker.SettleMarginSeconds;
            Assert.Equal(DeltaVRequestOutcome.Wait,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.Staging, 100.0, 100.0 + minWait - 0.01, Readiness(delay: delay)));
            Assert.Equal(DeltaVRequestOutcome.Complete,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.Staging, 100.0, 100.0 + minWait, Readiness(delay: delay)));
        }

        [Fact]
        public void DecideOne_BurnStart_ReadsOnTheFirstReadyTick()
        {
            Assert.Equal(DeltaVRequestOutcome.Complete,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.BurnStart, 100.0, 100.0, Readiness()));
            Assert.Equal(DeltaVRequestOutcome.Wait,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.BurnStart, 100.0, 100.0, Readiness(ready: false)));
        }

        [Fact]
        public void DecideOne_NotReadyPastTheTimeout_TimesOut()
        {
            double delay = 0.5;
            double timeoutAt = delay + DeltaVSnapshotTracker.SettleMarginSeconds + DeltaVSnapshotTracker.TimeoutSeconds;
            Assert.Equal(DeltaVRequestOutcome.Wait,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.BurnEnd, 0.0, timeoutAt - 0.01, Readiness(ready: false, delay: delay)));
            Assert.Equal(DeltaVRequestOutcome.TimedOut,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.BurnEnd, 0.0, timeoutAt, Readiness(ready: false, delay: delay)));
            // A vessel stock does not compute for (switched away) times out the same way.
            Assert.Equal(DeltaVRequestOutcome.TimedOut,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.BurnEnd, 0.0, timeoutAt, Readiness(eligible: false, delay: delay)));
        }

        [Fact]
        public void DecideOne_CalculationsDisabled_Drops()
        {
            Assert.Equal(DeltaVRequestOutcome.Dropped,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.Start, 0.0, 10.0, Readiness(enabled: false)));
            Assert.Equal(DeltaVRequestOutcome.Dropped,
                DeltaVSnapshotTracker.DecideAtStop(Readiness(enabled: false)));
        }

        [Fact]
        public void DecideOne_BadDelay_ReadsAsZero()
        {
            Assert.Equal(DeltaVRequestOutcome.Complete,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.Start, 0.0, DeltaVSnapshotTracker.SettleMarginSeconds,
                    Readiness(delay: double.NaN)));
            Assert.Equal(DeltaVRequestOutcome.Complete,
                DeltaVSnapshotTracker.DecideOne(DeltaVTrigger.Start, 0.0, DeltaVSnapshotTracker.SettleMarginSeconds,
                    Readiness(delay: -4.0)));
        }

        [Fact]
        public void Decide_KeepsWaitingRequestsAndCountsTheirTicks()
        {
            var tracker = new DeltaVSnapshotTracker();
            tracker.Request(DeltaVTrigger.Start, 0.0);
            Assert.Empty(tracker.Decide(0.1, Readiness()));
            Assert.Empty(tracker.Decide(0.2, Readiness()));
            Assert.Equal(1, tracker.PendingCount);

            List<DeltaVDecision> decisions = tracker.Decide(1.0, Readiness());
            DeltaVDecision decision = Assert.Single(decisions);
            Assert.Equal(DeltaVRequestOutcome.Complete, decision.Outcome);
            Assert.Equal(2, decision.SettleTicks);
            Assert.Equal(1.0, decision.WaitedSeconds, 6);
            Assert.Equal(0, tracker.PendingCount);
        }

        [Fact]
        public void DecideAtStop_ReadsAtOnceOrWritesUnknown()
        {
            Assert.Equal(DeltaVRequestOutcome.Complete, DeltaVSnapshotTracker.DecideAtStop(Readiness()));
            Assert.Equal(DeltaVRequestOutcome.TimedOut, DeltaVSnapshotTracker.DecideAtStop(Readiness(ready: false)));
            Assert.Equal(DeltaVRequestOutcome.TimedOut, DeltaVSnapshotTracker.DecideAtStop(Readiness(eligible: false)));
        }

        [Fact]
        public void IsUsableReading_RequiresFiniteTotals()
        {
            Assert.True(DeltaVSnapshotTracker.IsUsableReading(new DeltaVReading
                { TotalVacuum = 1, TotalSeaLevel = 1, TotalActual = 1 }));
            Assert.False(DeltaVSnapshotTracker.IsUsableReading(new DeltaVReading
                { TotalVacuum = double.NaN, TotalSeaLevel = 1, TotalActual = 1 }));
            Assert.False(DeltaVSnapshotTracker.IsUsableReading(new DeltaVReading
                { TotalVacuum = 1, TotalSeaLevel = 1, TotalActual = double.PositiveInfinity }));
        }

        #endregion

        #region List operations and the cap

        [Fact]
        public void TryInsertByUT_KeepsUtOrderAndInsertionOrderOnTies()
        {
            List<DeltaVSnapshot> list = null;
            var a = Ok(20, DeltaVTrigger.Staging, 100);
            var b = Ok(10, DeltaVTrigger.BurnStart, 200);
            var c = Ok(20, DeltaVTrigger.BurnEnd, 90);
            Assert.True(DeltaVSnapshotList.TryInsertByUT(ref list, a));
            Assert.True(DeltaVSnapshotList.TryInsertByUT(ref list, b));
            Assert.True(DeltaVSnapshotList.TryInsertByUT(ref list, c));
            Assert.Equal(new[] { b, a, c }, list);
        }

        [Fact]
        public void TryInsertByUT_AtTheCap_RefusesAndLeavesTheList()
        {
            List<DeltaVSnapshot> list = null;
            for (int i = 0; i < 3; i++)
                Assert.True(DeltaVSnapshotList.TryInsertByUT(ref list, Ok(i, DeltaVTrigger.BurnEnd, 10), cap: 3));
            Assert.False(DeltaVSnapshotList.TryInsertByUT(ref list, Ok(9, DeltaVTrigger.End, 10), cap: 3));
            Assert.Equal(3, list.Count);
            Assert.Equal(400, DeltaVSnapshotList.MaxSnapshotsPerRecording);
        }

        [Fact]
        public void SplitAtUT_MovesSnapshotsAtOrAfterTheCut()
        {
            var s1 = Ok(10, DeltaVTrigger.Start, 3000);
            var s2 = Ok(20, DeltaVTrigger.BurnStart, 3000);
            var s3 = Ok(30, DeltaVTrigger.BurnEnd, 2000);
            var first = new List<DeltaVSnapshot> { s1, s2, s3 };
            List<DeltaVSnapshot> second = DeltaVSnapshotList.SplitAtUT(ref first, 20);
            Assert.Equal(new[] { s1 }, first);
            Assert.Equal(new[] { s2, s3 }, second);

            var allEarly = new List<DeltaVSnapshot> { s1 };
            Assert.Null(DeltaVSnapshotList.SplitAtUT(ref allEarly, 50));
            List<DeltaVSnapshot> none = null;
            Assert.Null(DeltaVSnapshotList.SplitAtUT(ref none, 50));
            Assert.Null(none);
        }

        [Fact]
        public void Concat_MergesInUtOrder()
        {
            var t1 = Ok(10, DeltaVTrigger.Start, 3000);
            var t2 = Ok(40, DeltaVTrigger.End, 1000);
            var a1 = Ok(20, DeltaVTrigger.BurnEnd, 2000);
            List<DeltaVSnapshot> merged = DeltaVSnapshotList.Concat(
                new List<DeltaVSnapshot> { t1, t2 }, new List<DeltaVSnapshot> { a1 });
            Assert.Equal(new[] { t1, a1, t2 }, merged);
            Assert.Null(DeltaVSnapshotList.Concat(null, new List<DeltaVSnapshot>()));
        }

        [Fact]
        public void TrimPastUT_DropsLaterSnapshots()
        {
            var list = new List<DeltaVSnapshot>
            {
                Ok(10, DeltaVTrigger.Start, 3000), Ok(20, DeltaVTrigger.BurnStart, 3000), Ok(30, DeltaVTrigger.BurnEnd, 2000),
            };
            Assert.True(DeltaVSnapshotList.TrimPastUT(list, 20));
            Assert.Equal(new double[] { 10, 20 }, list.Select(s => s.UT));
            Assert.False(DeltaVSnapshotList.TrimPastUT(list, 20));
            Assert.False(DeltaVSnapshotList.TrimPastUT(null, 20));
        }

        [Fact]
        public void RemoveByReference_RemovesOnlyThatSnapshot()
        {
            var a = Ok(10, DeltaVTrigger.End, 1000);
            var twin = Ok(10, DeltaVTrigger.End, 1000);
            var list = new List<DeltaVSnapshot> { twin, a };
            Assert.True(DeltaVSnapshotList.RemoveByReference(list, a));
            Assert.Same(twin, Assert.Single(list));
            Assert.False(DeltaVSnapshotList.RemoveByReference(list, a));
        }

        #endregion

        #region Spent and Left

        // A Kerbal X-like ascent: 3400 m/s on the pad, a first burn to 2000, the booster
        // staged away (Left drops to 1500 with nothing spent), a second burn to 900.
        private static List<DeltaVSnapshot> AscentProfile()
        {
            return new List<DeltaVSnapshot>
            {
                Ok(100.0, DeltaVTrigger.Start, 3400),
                Ok(100.2, DeltaVTrigger.BurnStart, 3400),
                Ok(160.0, DeltaVTrigger.End, 2000),           // parent recording ends at the split
                Ok(160.8, DeltaVTrigger.Separation, 1500),    // child after the booster dropped
                DeltaVSnapshot.Unknown(161.0, DeltaVTrigger.Staging),
                Ok(170.1, DeltaVTrigger.BurnStart, 1500),
                Ok(240.9, DeltaVTrigger.BurnEnd, 900),
            };
        }

        [Fact]
        public void BurnSpent_IsLeftBeforeMinusLeftAfter()
        {
            List<DeltaVSnapshot> list = AscentProfile();
            Assert.True(DeltaVSnapshotList.TryGetBurnSpent(list, 170.0, 240.0, 1.0, out double spent, out double left));
            Assert.Equal(600.0, spent, 6);
            Assert.Equal(900.0, left, 6);
        }

        [Fact]
        public void StagingDrop_LowersLeftWithoutSpending()
        {
            List<DeltaVSnapshot> list = AscentProfile();
            Assert.True(DeltaVSnapshotList.TryGetStagingDrop(list, 160.0, 5.0, out double drop));
            Assert.Equal(500.0, drop, 6);

            // The first burn spent 1400 and ended at the staging; the drop is not part of it.
            Assert.True(DeltaVSnapshotList.TryGetBurnSpent(list, 100.0, 160.0, 1.0, out double spent, out double left));
            Assert.Equal(1400.0, spent, 6);
            Assert.Equal(2000.0, left, 6);
        }

        [Fact]
        public void LeftAt_SkipsUnknownSnapshots()
        {
            List<DeltaVSnapshot> list = AscentProfile();
            Assert.True(DeltaVSnapshotList.TryGetLeftAt(list, 165.0, out DeltaVSnapshot s));
            Assert.Equal(1500.0, s.TotalVacuum);
            Assert.False(DeltaVSnapshotList.TryGetLeftAt(list, 50.0, out _));
        }

        [Fact]
        public void BurnSpent_WithNoSnapshotAfter_IsFalse()
        {
            var list = new List<DeltaVSnapshot>
            {
                Ok(10, DeltaVTrigger.BurnStart, 3000),
                DeltaVSnapshot.Unknown(40, DeltaVTrigger.BurnEnd),
            };
            Assert.False(DeltaVSnapshotList.TryGetBurnSpent(list, 10, 40, 1.0, out double spent, out _));
            Assert.True(double.IsNaN(spent));
            Assert.False(DeltaVSnapshotList.TryGetStagingDrop(list, 20, 5, out _));
            Assert.False(DeltaVSnapshotList.TryGetBurnSpent(null, 10, 40, 1.0, out _, out _));
        }

        #endregion

        #region Codec

        [Fact]
        public void Codec_RoundTripsThroughTheRecordingNode()
        {
            var rec = new Recording
            {
                RecordingId = "dv-roundtrip",
                DeltaVSnapshots = new List<DeltaVSnapshot>
                {
                    Ok(17000.123456789, DeltaVTrigger.Start, 3412.0625,
                        new DeltaVStageReading(2, 1200.5, 45.25), new DeltaVStageReading(1, 2211.5625, 88.125)),
                    DeltaVSnapshot.Unknown(17010.5, DeltaVTrigger.Staging),
                    new DeltaVSnapshot(17020.0, DeltaVTrigger.End, DeltaVSnapshotStatus.Ok, 0, 0, 0, null),
                },
            };
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);
            Assert.NotNull(node.GetNode(DeltaVSnapshotCodec.NodeName));

            var loaded = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);

            Assert.Equal(3, loaded.DeltaVSnapshots.Count);
            DeltaVSnapshot first = loaded.DeltaVSnapshots[0];
            Assert.Equal(17000.123456789, first.UT);
            Assert.Equal(DeltaVTrigger.Start, first.Trigger);
            Assert.True(first.IsOk);
            Assert.Equal(3412.0625, first.TotalVacuum);
            Assert.Equal(3412.0625 * 0.9, first.TotalSeaLevel);
            Assert.Equal(3412.0625 * 0.95, first.TotalActual);
            Assert.Equal(2, first.Stages.Count);
            Assert.Equal(2, first.Stages[0].Stage);
            Assert.Equal(1200.5, first.Stages[0].VacuumDeltaV);
            Assert.Equal(45.25, first.Stages[0].BurnTimeSeconds);
            Assert.False(loaded.DeltaVSnapshots[1].IsOk);
            Assert.Equal(DeltaVTrigger.Staging, loaded.DeltaVSnapshots[1].Trigger);
            Assert.Empty(loaded.DeltaVSnapshots[2].Stages);
        }

        [Fact]
        public void Codec_IsInvariantUnderACommaCulture()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var snapshot = new DeltaVSnapshot(1234.5, DeltaVTrigger.BurnEnd, DeltaVSnapshotStatus.Ok,
                    987.25, 900.5, 950.75, new[] { new DeltaVStageReading(0, 987.25, 12.5) });
                string line = DeltaVSnapshotCodec.FormatLine(snapshot);
                Assert.Equal("1234.5,BurnEnd,Ok,987.25,900.5,950.75,0:987.25:12.5", line);
                Assert.True(DeltaVSnapshotCodec.TryParseLine(line, out DeltaVSnapshot parsed));
                Assert.Equal(987.25, parsed.TotalVacuum);
                Assert.Equal(12.5, parsed.Stages[0].BurnTimeSeconds);

                string log = FlightRecorder.FormatDeltaVSnapshotLog("rec-1", snapshot, 3, 0.75);
                Assert.Contains("ut=1234.50", log);
                Assert.Contains("vac=987.3", log);
                Assert.Contains("waited=0.75s", log);
                Assert.Contains("trigger=BurnEnd", log);
                Assert.Contains("settleTicks=3", log);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc,Start,Ok,1,1,1,")]
        [InlineData("10,Start")]
        [InlineData("10,Warp,Ok,1,1,1,")]
        [InlineData("10,3,Ok,1,1,1,")]
        [InlineData("10,Start,Maybe")]
        [InlineData("10,Start,Unknown,1")]
        [InlineData("10,Start,Ok,1,1")]
        [InlineData("10,Start,Ok,1,1,NaN,")]
        [InlineData("10,Start,Ok,1,1,1,0:5")]
        [InlineData("10,Start,Ok,1,1,1,x:5:1")]
        public void TryParseLine_RejectsMalformedLines(string line)
        {
            Assert.False(DeltaVSnapshotCodec.TryParseLine(line, out DeltaVSnapshot snapshot));
            Assert.Null(snapshot);
        }

        [Fact]
        public void Deserialize_SkipsMalformedLinesWithOneWarn()
        {
            var recNode = new ConfigNode("RECORDING");
            ConfigNode dv = recNode.AddNode(DeltaVSnapshotCodec.NodeName);
            dv.AddValue(DeltaVSnapshotCodec.ValueName, "10,Start,Ok,3000,2800,2900,0:3000:120");
            dv.AddValue(DeltaVSnapshotCodec.ValueName, "garbage");
            dv.AddValue(DeltaVSnapshotCodec.ValueName, "20,Warp,Ok,1,1,1,");
            dv.AddValue(DeltaVSnapshotCodec.ValueName, "30,End,Unknown");

            List<DeltaVSnapshot> loaded = DeltaVSnapshotCodec.Deserialize(recNode, "rec-bad");

            Assert.Equal(new double[] { 10, 30 }, loaded.Select(s => s.UT));
            List<string> warns = logLines.Where(l => l.Contains("[DeltaV]") && l.Contains("[WARN]")).ToList();
            string warn = Assert.Single(warns);
            Assert.Contains("rec-bad", warn);
            Assert.Contains("skipped 2 malformed", warn);
        }

        [Fact]
        public void Deserialize_AbsentNode_IsNull()
        {
            Assert.Null(DeltaVSnapshotCodec.Deserialize(new ConfigNode("RECORDING"), "rec"));
            var loaded = new Recording();
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, new Recording { RecordingId = "plain" });
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);
            Assert.Null(loaded.DeltaVSnapshots);
        }

        [Fact]
        public void EmptyOrNullList_WritesNoNode_AndTheNodeStaysByteIdentical()
        {
            var withNull = new Recording { RecordingId = "same-id", VesselName = "Kerbal X" };
            var withEmpty = new Recording
            {
                RecordingId = "same-id",
                VesselName = "Kerbal X",
                DeltaVSnapshots = new List<DeltaVSnapshot>(),
            };
            var nullNode = new ConfigNode("RECORDING");
            var emptyNode = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(nullNode, withNull);
            RecordingTreeRecordCodec.SaveRecordingInto(emptyNode, withEmpty);

            Assert.Null(nullNode.GetNode(DeltaVSnapshotCodec.NodeName));
            Assert.Null(emptyNode.GetNode(DeltaVSnapshotCodec.NodeName));
            Assert.Equal(Describe(nullNode), Describe(emptyNode));
        }

        private static string Describe(ConfigNode node)
        {
            var parts = new List<string>();
            for (int i = 0; i < node.values.Count; i++)
                parts.Add(node.values[i].name + "=" + node.values[i].value);
            for (int i = 0; i < node.nodes.Count; i++)
                parts.Add("{" + node.nodes[i].name + ":" + Describe(node.nodes[i]) + "}");
            return string.Join("|", parts);
        }

        [Fact]
        public void RecordingBuilder_WritesSnapshotsThroughTheProductionCodec()
        {
            ConfigNode node = new RecordingBuilder("Kerbal X")
                .AddDeltaVSnapshot(30, DeltaVTrigger.BurnEnd, 2000)
                .AddDeltaVSnapshot(10, DeltaVTrigger.Start, 3400)
                .BuildV3Metadata();
            List<DeltaVSnapshot> loaded = DeltaVSnapshotCodec.Deserialize(node, "builder");
            Assert.Equal(new double[] { 10, 30 }, loaded.Select(s => s.UT));

            ConfigNode bare = new RecordingBuilder("Kerbal X").Build();
            Assert.Null(bare.GetNode(DeltaVSnapshotCodec.NodeName));
        }

        #endregion

        #region Lifecycle carries

        [Fact]
        public void OptimizerSplitAtUT_GivesEachHalfItsSnapshots()
        {
            var rec = MakeRecording("dv-split", 8.0, 34.0, 53.0);
            var before = Ok(10.0, DeltaVTrigger.Start, 3400);
            var atCut = Ok(34.0, DeltaVTrigger.BurnStart, 3400);
            var after = Ok(50.0, DeltaVTrigger.BurnEnd, 2400);
            rec.DeltaVSnapshots = new List<DeltaVSnapshot> { before, atCut, after };

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Equal(new[] { before }, rec.DeltaVSnapshots);
            Assert.Equal(new[] { atCut, after }, tip.DeltaVSnapshots);
        }

        [Fact]
        public void OptimizerMerge_ConcatenatesInUtOrder()
        {
            var target = MakeRecording("dv-merge-a", 10.0, 15.0, 20.0);
            var absorbed = MakeRecording("dv-merge-b", 20.0, 25.0, 30.0);
            var t1 = Ok(10.0, DeltaVTrigger.Start, 3400);
            var a1 = Ok(25.0, DeltaVTrigger.BurnEnd, 2000);
            target.DeltaVSnapshots = new List<DeltaVSnapshot> { t1 };
            absorbed.DeltaVSnapshots = new List<DeltaVSnapshot> { a1 };

            RecordingOptimizer.MergeInto(target, absorbed);

            Assert.Equal(new[] { t1, a1 }, target.DeltaVSnapshots);
        }

        [Fact]
        public void QuickloadTrim_DropsSnapshotsPastTheCutoff()
        {
            var rec = MakeRecording("dv-trim", 10.0, 20.0, 30.0);
            var early = Ok(12.0, DeltaVTrigger.Start, 3400);
            rec.DeltaVSnapshots = new List<DeltaVSnapshot> { early, Ok(25.0, DeltaVTrigger.BurnEnd, 2000) };

            Assert.True(ParsekScenario.TrimRecordingPastUT(rec, 20.0));

            Assert.Equal(new[] { early }, rec.DeltaVSnapshots);
        }

        [Fact]
        public void DeepClone_CopiesTheListIndependently()
        {
            var rec = new Recording
            {
                RecordingId = "dv-clone",
                DeltaVSnapshots = new List<DeltaVSnapshot> { Ok(10.0, DeltaVTrigger.Start, 3400) },
            };
            Recording clone = Recording.DeepClone(rec);
            Assert.NotSame(rec.DeltaVSnapshots, clone.DeltaVSnapshots);
            Assert.Same(rec.DeltaVSnapshots[0], clone.DeltaVSnapshots[0]);
            clone.DeltaVSnapshots.Clear();
            Assert.Single(rec.DeltaVSnapshots);
        }

        #endregion

        #region Log lines

        [Fact]
        public void StopSummary_NamesCountsAndPending()
        {
            string line = FlightRecorder.FormatDeltaVStopSummary("rec-7", "chain boundary stop", 5, 1, 3, 2, true);
            Assert.Equal(
                "Recording rec-7 stopped (chain boundary stop): snapshots=5 unknown=1 writtenByRecorder=3 pendingKept=2",
                line);
            Assert.EndsWith("(EVA kerbal: none recorded)",
                FlightRecorder.FormatDeltaVStopSummary("rec-8", "recording stop", 0, 0, 0, 0, false));
        }

        [Fact]
        public void CountUnknown_CountsOnlyUnknown()
        {
            Assert.Equal(1, DeltaVSnapshotList.CountUnknown(AscentProfile()));
            Assert.Equal(0, DeltaVSnapshotList.CountUnknown(null));
        }

        #endregion
    }
}
