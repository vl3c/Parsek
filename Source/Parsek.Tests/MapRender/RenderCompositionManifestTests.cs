using System.Globalization;
using System.Threading;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests.MapRender
{
    /// <summary>
    /// M-A7 Phase 1: the pure accumulation core (<see cref="RenderCompositionManifest"/>). Every cell
    /// drives the core directly - no Unity, no addon, no env var - which is exactly why the core was
    /// split out of the recorder.
    /// </summary>
    public class RenderCompositionManifestTests
    {
        private static RenderCompositionManifest.DwellSample Sample(
            uint pid, string treatment, string coverage, string body, int segmentIndex,
            double ut, double headUT, double warpRate = 1.0, bool physicsWarp = false)
        {
            var s = default(RenderCompositionManifest.DwellSample);
            s.Pid = pid;
            s.RecId = "rec-" + pid.ToString(CultureInfo.InvariantCulture);
            s.CommittedIndex = 3;
            s.ChainSignature = "sig-A";
            s.SegmentIndex = segmentIndex;
            s.PhaseKind = "ascent";
            s.Treatment = treatment;
            s.Visible = treatment != "None";
            s.Coverage = coverage;
            s.FrameBody = body;
            s.CurrentUT = ut;
            s.HeadUT = headUT;
            s.WarpRate = warpRate;
            s.PhysicsWarp = physicsWarp;
            return s;
        }

        private static RenderCompositionManifest.ManifestHeader Header(double ut = 1000.0)
            => new RenderCompositionManifest.ManifestHeader(
                ut, "verb", "FLIGHT", "test-save", true, false, true);

        // ---- dwell open / close ordering ----

        [Fact]
        public void Dwell_OpensOnFirstFrame_ClosesOnKeyChange_AndEmitsOneTransition()
        {
            var m = new RenderCompositionManifest();

            m.ObserveDwellFrame(Sample(7u, "StockConic", "InSegment", "Kerbin", 0, 100.0, 100.0));
            m.ObserveDwellFrame(Sample(7u, "StockConic", "InSegment", "Kerbin", 0, 101.0, 101.0));
            Assert.Equal(0, m.ClosedDwellCount);
            Assert.Equal(1, m.OpenDwellCount);
            Assert.Equal(0, m.TransitionCount);

            // Segment index change -> new dwell + one transition.
            m.ObserveDwellFrame(Sample(7u, "TracedPath", "InSegment", "Kerbin", 1, 102.0, 102.0));
            Assert.Equal(1, m.ClosedDwellCount);
            Assert.Equal(1, m.OpenDwellCount);
            Assert.Equal(1, m.TransitionCount);

            m.CloseAllOpenDwells(110.0);
            Assert.Equal(2, m.ClosedDwellCount);
            Assert.Equal(0, m.OpenDwellCount);

            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode obs = root.GetNode("OBSERVED");
            ConfigNode[] dwells = obs.GetNodes("DWELL");
            Assert.Equal(2, dwells.Length);

            // Ordering: closed dwells appear in CLOSE order, so the first-opened one is first.
            Assert.Equal("StockConic", dwells[0].GetValue("treatment"));
            Assert.Equal("100", dwells[0].GetValue("openUT"));
            Assert.Equal("102", dwells[0].GetValue("closeUT"));
            Assert.Equal("2", dwells[0].GetValue("frames"));
            Assert.Equal("TracedPath", dwells[1].GetValue("treatment"));
            Assert.Equal("102", dwells[1].GetValue("openUT"));
            Assert.Equal("110", dwells[1].GetValue("closeUT"));
            Assert.Equal("True", dwells[1].GetValue("openAtExport"));

            ConfigNode transition = Assert.Single(obs.GetNodes("TRANSITION"));
            Assert.Equal("StockConic", transition.GetValue("fromTreatment"));
            Assert.Equal("TracedPath", transition.GetValue("toTreatment"));
            Assert.Equal("0", transition.GetValue("fromSegmentIndex"));
            Assert.Equal("1", transition.GetValue("toSegmentIndex"));
            Assert.Equal("102", transition.GetValue("ut"));
        }

        [Fact]
        public void Build_SnapshotsStillOpenDwells_WithoutClosingThem()
        {
            // The EXPORT path no longer closes open dwells - it snapshots them. A build that mutated
            // first would destroy accumulation state before the write could fail, and the eventual
            // real close would land on a dwell the failed export had already retired.
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(7u, "TracedPath", "InSegment", "Kerbin", 0, 100.0, 100.0));
            m.ObserveDwellFrame(Sample(7u, "TracedPath", "InSegment", "Kerbin", 0, 101.0, 101.0));

            ConfigNode obs = m.BuildFileNode(Header(120.0))
                .GetNode(RenderCompositionManifest.RootNodeName).GetNode("OBSERVED");
            ConfigNode snapshot = Assert.Single(obs.GetNodes("DWELL"));
            Assert.Equal("True", snapshot.GetValue("openAtExport"));
            Assert.Equal("100", snapshot.GetValue("openUT"));
            Assert.Equal("120", snapshot.GetValue("closeUT"));   // advanced to the export instant
            Assert.Equal("2", snapshot.GetValue("frames"));

            // NOTHING moved: the dwell is still open and still accumulating.
            Assert.Equal(0, m.ClosedDwellCount);
            Assert.Equal(1, m.OpenDwellCount);

            m.ObserveDwellFrame(Sample(7u, "TracedPath", "InSegment", "Kerbin", 0, 130.0, 130.0));
            m.CloseOpenDwell(7u, 140.0);
            Assert.Equal(1, m.ClosedDwellCount);
            Assert.Equal(0, m.OpenDwellCount);

            // The REAL close wins on the second build - one record, no openAtExport, three frames.
            ConfigNode closed = Assert.Single(
                m.BuildFileNode(Header(200.0))
                    .GetNode(RenderCompositionManifest.RootNodeName).GetNode("OBSERVED")
                    .GetNodes("DWELL"));
            Assert.False(closed.HasValue("openAtExport"));
            Assert.Equal("140", closed.GetValue("closeUT"));
            Assert.Equal("3", closed.GetValue("frames"));
        }

        [Fact]
        public void Dwell_AnomalyEchoes_AggregateByReasonInsideTheOpenDwell()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(7u, "StockConic", "InSegment", "Kerbin", 0, 100.0, 100.0));
            m.NoteAnomalyEcho(7u, "icon-jump");
            m.NoteAnomalyEcho(7u, "icon-jump");
            m.NoteAnomalyEcho(7u, "line-blink");
            // An echo with no open dwell is dropped by design (the tracer owns the raise itself).
            m.NoteAnomalyEcho(99u, "icon-jump");
            m.CloseAllOpenDwells(105.0);

            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode dwell = Assert.Single(root.GetNode("OBSERVED").GetNodes("DWELL"));
            ConfigNode[] echoes = dwell.GetNodes("ANOMALY_ECHO");
            Assert.Equal(2, echoes.Length);
            Assert.Equal("icon-jump", echoes[0].GetValue("reason"));
            Assert.Equal("2", echoes[0].GetValue("count"));
            Assert.Equal("line-blink", echoes[1].GetValue("reason"));
            Assert.Equal("1", echoes[1].GetValue("count"));
        }

        // ---- cap + truncate marker ----

        [Fact]
        public void Dwell_CapHit_DropsTheNewRecord_AndEmitsATruncatedMarker()
        {
            var m = new RenderCompositionManifest();
            // Every observation flips the segment index, so each one opens a fresh dwell.
            int frames = RenderCompositionManifest.MaxDwellsPerPid + 25;
            for (int i = 0; i < frames; i++)
                m.ObserveDwellFrame(Sample(4u, "StockConic", "InSegment", "Kerbin", i, 100.0 + i, 100.0 + i));

            Assert.True(m.ClosedDwellCount <= RenderCompositionManifest.MaxDwellsPerPid);
            Assert.True(m.TruncationCount > 0);

            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode truncated = Assert.Single(root.GetNode("OBSERVED").GetNodes("TRUNCATED"));
            Assert.Equal("DWELL", truncated.GetValue("section"));
            Assert.Equal("4", truncated.GetValue("pid"));
            Assert.Equal("dwell", truncated.GetValue("kind"));
            Assert.Equal(
                (frames - RenderCompositionManifest.MaxDwellsPerPid).ToString(CultureInfo.InvariantCulture),
                truncated.GetValue("droppedCount"));
        }

        [Fact]
        public void ClockEvent_CapHit_CountsATruncation()
        {
            var m = new RenderCompositionManifest();
            int total = RenderCompositionManifest.MaxClockEvents + 7;
            for (int i = 0; i < total; i++)
            {
                m.AppendClockEventIfChanged(
                    RenderCompositionManifest.ClockCycleRollover, 1, i, 100.0 + i, 0.0, 0.0, 0.0, null);
            }
            Assert.Equal(RenderCompositionManifest.MaxClockEvents, m.ClockEventCount);

            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode truncated = Assert.Single(root.GetNode("OBSERVED").GetNodes("TRUNCATED"));
            // CLOCK_EVENT has no per-pid dimension, so MaxClockEvents IS its whole-export bound and
            // the marker carries the ":global" suffix + pid 0 like every other family cap. (Under the
            // wave-1 wiring the family caps disagreed on this: RATIFIED_SKIP suffixed, the rest did
            // not.)
            Assert.Equal("CLOCK_EVENT" + RenderCompositionManifest.GlobalSectionSuffix,
                truncated.GetValue("section"));
            Assert.Equal("0", truncated.GetValue("pid"));
            Assert.Equal("7", truncated.GetValue("droppedCount"));
        }

        [Fact]
        public void ClockEvent_DedupeTableExhausted_FailsClosed_AndNeverWipesTheTable()
        {
            var m = new RenderCompositionManifest();

            // Fill the dedupe table with distinct keys. MaxClockEvents is smaller than the dedupe cap,
            // so the family cap trips first; both drop paths are counted, and neither may clear the
            // table - a wipe would let an ALREADY-recorded key be appended a second time, and a
            // consumer counting one engage per run would read that duplicate as a second run.
            int distinct = RenderCompositionManifest.MaxClockEventDedupeKeys + 5;
            for (int i = 0; i < distinct; i++)
            {
                m.AppendClockEventIfChanged(
                    RenderCompositionManifest.ClockCycleRollover, 1, i, 100.0 + i, 0.0, 0.0, 0.0, null);
            }
            Assert.Equal(RenderCompositionManifest.MaxClockEvents, m.ClockEventCount);

            // The very first key is STILL known: re-offering it is debounced, not re-appended.
            Assert.False(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 1, 0, 9999.0, 0.0, 0.0, 0.0, null));
            Assert.Equal(RenderCompositionManifest.MaxClockEvents, m.ClockEventCount);

            ConfigNode obs = m.BuildFileNode(Header())
                .GetNode(RenderCompositionManifest.RootNodeName).GetNode("OBSERVED");
            ConfigNode[] rows = obs.GetNodes("TRUNCATED");
            ConfigNode exhausted = Assert.Single(System.Array.FindAll(rows,
                r => r.GetValue("section") == RenderCompositionManifest.ClockEventDedupeExhaustedSection));
            Assert.Equal(RenderCompositionManifest.ClockEventDedupeExhaustedKind,
                exhausted.GetValue("kind"));
            Assert.Equal("0", exhausted.GetValue("pid"));
            Assert.Equal(
                (distinct - RenderCompositionManifest.MaxClockEventDedupeKeys)
                    .ToString(CultureInfo.InvariantCulture),
                exhausted.GetValue("droppedCount"));
        }

        // ---- GLOBAL caps (the per-pid bounds only bound ONE ghost) ----

        [Fact]
        public void Dwell_GlobalCapHit_DropsTheRecord_AndMarksTheGlobalSection()
        {
            var m = new RenderCompositionManifest();

            // MaxDwellsPerPid saturates each pid, so the global bound is reached exactly
            // MaxDwellsTotal / MaxDwellsPerPid pids in. One pid beyond that can open nothing at
            // all even though its OWN per-pid budget is untouched - which is the whole point of
            // the global bound, and what a per-pid-only cap misses.
            int pidsToSaturate = RenderCompositionManifest.MaxDwellsTotal
                / RenderCompositionManifest.MaxDwellsPerPid;
            int overflowFrames = 9;
            for (uint pid = 0; pid < (uint)pidsToSaturate; pid++)
                for (int i = 0; i < RenderCompositionManifest.MaxDwellsPerPid; i++)
                    m.ObserveDwellFrame(
                        Sample(pid, "StockConic", "InSegment", "Kerbin", i, 100.0 + i, 100.0 + i));

            Assert.Equal(RenderCompositionManifest.MaxDwellsTotal,
                m.ClosedDwellCount + m.OpenDwellCount);
            Assert.Equal(0, m.TruncationCount);

            uint overflowPid = (uint)pidsToSaturate;
            for (int i = 0; i < overflowFrames; i++)
                m.ObserveDwellFrame(
                    Sample(overflowPid, "StockConic", "InSegment", "Kerbin", i, 500.0 + i, 500.0 + i));

            // Nothing was accepted, and nothing was dropped SILENTLY.
            Assert.Equal(RenderCompositionManifest.MaxDwellsTotal,
                m.ClosedDwellCount + m.OpenDwellCount);
            Assert.Equal(1, m.TruncationCount);

            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode truncated = Assert.Single(root.GetNode("OBSERVED").GetNodes("TRUNCATED"));
            // The section token is what tells a consumer this was the GLOBAL bound, not the
            // per-pid one ("DWELL"), and the pid is the whole-export 0 rather than a real ghost.
            Assert.Equal("DWELL" + RenderCompositionManifest.GlobalSectionSuffix,
                truncated.GetValue("section"));
            Assert.Equal("0", truncated.GetValue("pid"));
            Assert.Equal("dwell", truncated.GetValue("kind"));
            Assert.Equal(overflowFrames.ToString(CultureInfo.InvariantCulture),
                truncated.GetValue("droppedCount"));
        }

        [Fact]
        public void TotalCeiling_ExceedsTheSumOfEveryFamilyCap()
        {
            // The whole-export ceiling is a BACKSTOP: every family already has its own bound, and
            // a family's own TRUNCATED marker is strictly more informative than "ALL:global". So
            // the ceiling must sit ABOVE the sum of the family budgets - if it did not, it would
            // preempt them and every truncation would read as the anonymous one. This cell is the
            // arithmetic guard on that: raising a family cap past the budget reds here instead of
            // silently degrading every marker on the next crowded scene.
            long familySum =
                (long)RenderCompositionManifest.MaxDwellsTotal
                + RenderCompositionManifest.MaxTransitionsTotal
                + RenderCompositionManifest.MaxLineBranchesTotal
                + RenderCompositionManifest.MaxSeamRecordsTotal
                + RenderCompositionManifest.MaxRatifiedSkipRecords
                + RenderCompositionManifest.MaxClockEvents
                + RenderCompositionManifest.MaxOwnershipChanges
                + RenderCompositionManifest.MaxPlanUnits
                + RenderCompositionManifest.MaxChainBuilds
                + RenderCompositionManifest.MaxAnomalyEchoRecords
                + (3L * RenderCompositionManifest.MaxRouteRecords);   // build + leg-defer + co-draw

            Assert.True(familySum <= RenderCompositionManifest.MaxTotalRecords,
                "the family caps now sum to " + familySum.ToString(CultureInfo.InvariantCulture)
                + ", above the " + RenderCompositionManifest.MaxTotalRecords.ToString(CultureInfo.InvariantCulture)
                + " whole-export ceiling: the ceiling would fire before the family markers");
        }

        [Fact]
        public void TotalRecordCount_CountsEveryFamily_AndResetsWithTheCore()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(1u, "StockConic", "InSegment", "Kerbin", 0, 100.0, 100.0));
            m.ObserveDwellFrame(Sample(1u, "StockConic", "InSegment", "Kerbin", 1, 101.0, 101.0));
            // Two dwells plus the transition between them: the ceiling counts them all, because
            // every one of them is a node in the exported file.
            Assert.Equal(3, m.TotalRecordCount);

            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 1, 0, 102.0, 0.0, 0.0, 0.0, null);
            Assert.Equal(4, m.TotalRecordCount);

            // The debounced duplicate is not a record.
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 1, 0, 103.0, 0.0, 0.0, 0.0, null);
            Assert.Equal(4, m.TotalRecordCount);

            m.Reset();
            Assert.Equal(0, m.TotalRecordCount);
        }

        [Fact]
        public void TruncationRows_AreThemselvesBounded_ByOneOverflowRow()
        {
            var m = new RenderCompositionManifest();
            int distinctKeys = RenderCompositionManifest.MaxTruncationRecords + 40;
            for (int i = 0; i < distinctKeys; i++)
                m.NoteTruncationForTesting("DWELL", (uint)i, "dwell");

            // Bounded at the cap plus the ONE reserved overflow row.
            Assert.Equal(RenderCompositionManifest.MaxTruncationRecords + 1, m.TruncationCount);

            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode[] rows = root.GetNode("OBSERVED").GetNodes("TRUNCATED");
            Assert.Equal(RenderCompositionManifest.MaxTruncationRecords + 1, rows.Length);

            ConfigNode overflow = Assert.Single(
                System.Array.FindAll(rows, r => r.GetValue("kind")
                    == RenderCompositionManifest.TruncationOverflowKind));
            Assert.Equal(RenderCompositionManifest.TruncationOverflowSection,
                overflow.GetValue("section"));
            // Every key past the cap is still COUNTED, just no longer named individually.
            Assert.Equal(
                (distinctKeys - RenderCompositionManifest.MaxTruncationRecords)
                    .ToString(CultureInfo.InvariantCulture),
                overflow.GetValue("droppedCount"));
        }

        // ---- warp bucket aggregates ----

        [Theory]
        [InlineData(1.0, false, 0)]
        [InlineData(0.5, false, 0)]
        [InlineData(1.0, true, 0)]
        [InlineData(4.0, true, 1)]
        [InlineData(10.0, false, 2)]
        [InlineData(100.0, false, 2)]
        [InlineData(1000.0, false, 3)]
        [InlineData(100000.0, false, 4)]
        public void ClassifyWarpBucket_IsThePureFiveWayBucketing(double rate, bool phys, int expected)
        {
            Assert.Equal(expected, RenderCompositionManifest.ClassifyWarpBucket(rate, phys));
        }

        [Fact]
        public void Dwell_WarpHistogram_CountsFramesPerBucket()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(2u, "StockConic", "InSegment", "Mun", 0, 100.0, 100.0, 1.0));
            m.ObserveDwellFrame(Sample(2u, "StockConic", "InSegment", "Mun", 0, 101.0, 101.0, 1.0));
            m.ObserveDwellFrame(Sample(2u, "StockConic", "InSegment", "Mun", 0, 102.0, 102.0, 4.0, true));
            m.ObserveDwellFrame(Sample(2u, "StockConic", "InSegment", "Mun", 0, 110.0, 110.0, 50.0));
            m.ObserveDwellFrame(Sample(2u, "StockConic", "InSegment", "Mun", 0, 210.0, 210.0, 1000.0));
            m.ObserveDwellFrame(Sample(2u, "StockConic", "InSegment", "Mun", 0, 100210.0, 100210.0, 100000.0));
            m.CloseAllOpenDwells(100211.0);

            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode dwell = Assert.Single(root.GetNode("OBSERVED").GetNodes("DWELL"));
            Assert.Equal("6", dwell.GetValue("frames"));
            Assert.Equal("2", dwell.GetValue("warp1x"));
            Assert.Equal("1", dwell.GetValue("warpPhys"));
            Assert.Equal("1", dwell.GetValue("warp100"));
            Assert.Equal("1", dwell.GetValue("warp1000"));
            Assert.Equal("1", dwell.GetValue("warpHigh"));
            Assert.Equal("100", dwell.GetValue("minHeadUT"));
            Assert.Equal("100210", dwell.GetValue("maxHeadUT"));
        }

        // ---- maxUtStep ----

        [Fact]
        public void Dwell_MaxUtStep_IsTheLargestGapBetweenConsecutiveArmedFramesInThatDwell()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(Sample(5u, "StockConic", "InSegment", "Kerbin", 0, 100.0, 100.0));
            m.ObserveDwellFrame(Sample(5u, "StockConic", "InSegment", "Kerbin", 0, 101.0, 101.0));
            m.ObserveDwellFrame(Sample(5u, "StockConic", "InSegment", "Kerbin", 0, 5101.0, 5101.0));
            m.ObserveDwellFrame(Sample(5u, "StockConic", "InSegment", "Kerbin", 0, 5103.0, 5103.0));
            // A new dwell must START at 0 - the step that carried us across the boundary belongs to
            // neither dwell's interior resolution.
            m.ObserveDwellFrame(Sample(5u, "TracedPath", "InSegment", "Kerbin", 1, 9000.0, 9000.0));
            m.ObserveDwellFrame(Sample(5u, "TracedPath", "InSegment", "Kerbin", 1, 9002.0, 9002.0));
            m.CloseAllOpenDwells(9003.0);

            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            ConfigNode[] dwells = root.GetNode("OBSERVED").GetNodes("DWELL");
            Assert.Equal(2, dwells.Length);
            Assert.Equal("5000", dwells[0].GetValue("maxUtStep"));
            Assert.Equal("2", dwells[1].GetValue("maxUtStep"));
        }

        // ---- clock-event debounce ----

        [Fact]
        public void ClockEvent_Debounce_CollapsesRepeatsAndAdmitsEachDistinctKey()
        {
            var m = new RenderCompositionManifest();

            Assert.True(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 1, 0, 100.0, 0.0, 0.0, 0.0, null));
            Assert.False(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 1, 0, 101.0, 0.0, 0.0, 0.0, null));
            Assert.True(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 1, 1, 200.0, 0.0, 0.0, 0.0, null));
            // A different owner on the same cycle is a distinct unit's rollover.
            Assert.True(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 2, 1, 200.0, 0.0, 0.0, 0.0, null));
            // descent-phase keys on the phase token, so a phase change re-admits at the same cycle.
            Assert.True(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockDescentPhase, 1, 1, 210.0, 5.0, 6.0, 0.0, "Loiter"));
            Assert.False(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockDescentPhase, 1, 1, 211.0, 5.0, 6.0, 0.0, "Loiter"));
            Assert.True(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockDescentPhase, 1, 1, 212.0, 5.0, 6.0, 0.0, "Descent"));

            Assert.Equal(5, m.ClockEventCount);
        }

        [Fact]
        public void LineBranch_DebouncesOnReasonLineActiveCoverage()
        {
            var m = new RenderCompositionManifest();
            Assert.True(m.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
            { Pid = 9u, Reason = "visible-body-frame", LineActive = true, Coverage = "Inside", UT = 10.0 }));
            Assert.False(m.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
            { Pid = 9u, Reason = "visible-body-frame", LineActive = true, Coverage = "Inside", UT = 11.0 }));
            Assert.True(m.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
            { Pid = 9u, Reason = "past-body-frame-end", LineActive = false, Coverage = "Outside", UT = 12.0 }));
            // A different pid keeps its own state.
            Assert.True(m.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
            { Pid = 10u, Reason = "visible-body-frame", LineActive = true, Coverage = "Inside", UT = 12.0 }));
            Assert.Equal(3, m.LineBranchCount);
        }

        // ---- InvariantCulture round-trip ----

        [Fact]
        public void Serialization_UsesInvariantCulture_UnderACommaDecimalCulture()
        {
            CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
            CultureInfo originalUiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Thread.CurrentThread.CurrentUICulture = new CultureInfo("de-DE");

                var m = new RenderCompositionManifest();
                m.ObserveDwellFrame(Sample(1u, "StockConic", "InSegment", "Kerbin", 0, 1234.5, 6789.25));
                m.CloseAllOpenDwells(1236.75);
                m.AppendSeamTangent(new RenderCompositionManifest.SeamTangentRecord
                {
                    Pid = 1u,
                    RecId = "rec-1",
                    LegIndex = 2,
                    UT = 1234.5,
                    Continuous = false,
                    AngleRadians = 0.125,
                    ToleranceRadians = 0.1,
                });

                ConfigNode root = m.BuildFileNode(
                    new RenderCompositionManifest.ManifestHeader(
                        9876.5, "verb", "TRACKSTATION", "save", true, false, false))
                    .GetNode(RenderCompositionManifest.RootNodeName);

                Assert.Equal("9876.5", root.GetValue("exportUT"));
                Assert.Equal("0.1", root.GetNode("CONSTANTS")
                    .GetValue("PhaseSeamClassifier.DefaultTangentToleranceRadians"));

                ConfigNode obs = root.GetNode("OBSERVED");
                ConfigNode dwell = Assert.Single(obs.GetNodes("DWELL"));
                Assert.Equal("1234.5", dwell.GetValue("openUT"));
                Assert.Equal("1236.75", dwell.GetValue("closeUT"));
                Assert.Equal("6789.25", dwell.GetValue("minHeadUT"));

                ConfigNode tangent = Assert.Single(obs.GetNodes("SEAM_TANGENT"));
                Assert.Equal("0.125", tangent.GetValue("angleRad"));
                Assert.Equal("0.1", tangent.GetValue("toleranceRadians"));

                // Round-trip: reparse with the comma culture still installed.
                Assert.Equal(0.125, double.Parse(
                    tangent.GetValue("angleRad"), NumberStyles.Float, CultureInfo.InvariantCulture), 9);
                Assert.Equal(9876.5, double.Parse(
                    root.GetValue("exportUT"), NumberStyles.Float, CultureInfo.InvariantCulture), 9);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                Thread.CurrentThread.CurrentUICulture = originalUiCulture;
            }
        }

        // ---- structural: outer node is dropped, RENDER_MANIFEST is the file root ----

        [Fact]
        public void BuildFileNode_WrapsExactlyOneRenderManifestChild()
        {
            var m = new RenderCompositionManifest();
            ConfigNode file = m.BuildFileNode(Header());
            Assert.Equal(0, file.values.Count);
            Assert.Equal(1, file.nodes.Count);
            ConfigNode root = file.GetNode(RenderCompositionManifest.RootNodeName);
            Assert.NotNull(root);
            Assert.Equal("1", root.GetValue("schemaVersion"));
            Assert.NotNull(root.GetNode("CONSTANTS"));
            Assert.NotNull(root.GetNode("PLAN"));
            Assert.NotNull(root.GetNode("CHAIN"));
            Assert.NotNull(root.GetNode("OBSERVED"));
        }

        [Fact]
        public void StableHash_IsDeterministicAndNonNegative()
        {
            Assert.Equal(
                RenderCompositionManifest.StableHash("abc|def"),
                RenderCompositionManifest.StableHash("abc|def"));
            Assert.NotEqual(
                RenderCompositionManifest.StableHash("abc|def"),
                RenderCompositionManifest.StableHash("abc|deg"));
            Assert.True(RenderCompositionManifest.StableHash("anything") >= 0);
            Assert.Equal(RenderCompositionManifest.StableHash(null),
                RenderCompositionManifest.StableHash(""));
        }

        /// <summary>
        /// Reset must clear EVERY family, not the four a first pass happened to name: a section left
        /// populated survives the cross-save partition the recorder resets for, and mixes two saves'
        /// recording-id namespaces into one manifest. So this cell populates one record of every kind
        /// the schema has and asserts the whole export goes back to empty - counters, total ceiling
        /// count, and the serialized file alike.
        /// </summary>
        [Fact]
        public void Reset_ClearsEverySection()
        {
            var m = new RenderCompositionManifest();

            // PLAN + CHAIN.
            m.AppendPlanUnit(new RenderCompositionManifest.PlanUnitRecord { Host = "TrackingStation" });
            var chain = new RenderCompositionManifest.ChainBuildRecord { Pid = 1u, Signature = "sig-A" };
            chain.Phases.Add(new RenderCompositionManifest.ChainPhaseRecord { Kind = "ascent" });
            chain.Seams.Add(new RenderCompositionManifest.ChainSeamRecord { BoundaryIndex = 1, Kind = "rigid" });
            m.AppendChainBuild(chain);

            // DWELL (+ its nested ANOMALY_ECHO) + TRANSITION.
            m.ObserveDwellFrame(Sample(1u, "StockConic", "InSegment", "Kerbin", 0, 10.0, 10.0));
            m.NoteAnomalyEcho(1u, "icon-jump");
            m.ObserveDwellFrame(Sample(1u, "TracedPath", "InSegment", "Kerbin", 1, 11.0, 11.0));

            // SEAM_TANGENT + SEAM_ENDPOINT.
            m.AppendSeamTangent(new RenderCompositionManifest.SeamTangentRecord { Pid = 1u, UT = 12.0 });
            m.AppendSeamEndpoint(new RenderCompositionManifest.SeamEndpointRecord { Pid = 1u, UT = 12.5 });

            // CLOCK_EVENT + LINE_BRANCH + OWNERSHIP_CHANGE + RATIFIED_SKIP + CLOCK_DEFER.
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 1, 0, 13.0, 0.0, 13.0, 0.0, null);
            m.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
            { Pid = 1u, Reason = "visible-body-frame", LineActive = true, Coverage = "Unknown", UT = 13.5 });
            m.AppendOwnershipChange("rec-1", 14.0, appeared: true);
            m.NoteRatifiedSkip(1u, 14.5, "reaim-segment-skip");
            m.NoteClockDefer(15.0);

            // Standalone ANOMALY_ECHO + the three ROUTE families + a TRUNCATED marker.
            m.AppendAnomalyEchoRecord("route-0001", "rec-1", "line-blink", 15.5);
            m.AppendRouteLineBuild(new RenderCompositionManifest.RouteLineBuildRecord
            { RouteId = "route-0001", UT = 16.0 });
            m.NoteRouteLegDeferred("route-0001", "rec-1");
            m.AppendRouteCoDrawViolation("route-0001", "rec-1", 16.5, 42);
            m.NoteTruncationForTesting("DWELL", 99u, "dwell");

            // Everything is really there before the Reset, so the cell cannot pass vacuously.
            Assert.True(m.TotalRecordCount > 0);
            ConfigNode before = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            foreach (string kind in new[]
            {
                "DWELL", "TRANSITION", "SEAM_TANGENT", "SEAM_ENDPOINT", "CLOCK_EVENT", "LINE_BRANCH",
                "OWNERSHIP_CHANGE", "RATIFIED_SKIP", "CLOCK_DEFER", "ANOMALY_ECHO", "ROUTE_LINE_BUILD",
                "ROUTE_LEG_DEFER", "ROUTE_CODRAW_VIOLATION", "TRUNCATED",
            })
            {
                Assert.True(before.GetNode("OBSERVED").GetNodes(kind).Length > 0,
                    "the pre-Reset accumulation must carry a " + kind + " node, or the Reset "
                    + "assertion below proves nothing about it");
            }
            Assert.NotEmpty(before.GetNode("PLAN").GetNodes("UNIT"));
            Assert.NotEmpty(before.GetNode("CHAIN").GetNodes("CHAIN_BUILD"));

            m.Reset();

            Assert.Equal(0, m.PlanUnitCount);
            Assert.Equal(0, m.ChainBuildCount);
            Assert.Equal(0, m.ClosedDwellCount);
            Assert.Equal(0, m.OpenDwellCount);
            Assert.Equal(0, m.TransitionCount);
            Assert.Equal(0, m.ClockEventCount);
            Assert.Equal(0, m.OwnershipChangeCount);
            Assert.Equal(0, m.LineBranchCount);
            Assert.Equal(0, m.SeamTangentCount);
            Assert.Equal(0, m.SeamEndpointCount);
            Assert.Equal(0, m.RouteLineBuildCount);
            Assert.Equal(0, m.RouteCoDrawViolationCount);
            Assert.Equal(0, m.AnomalyEchoRecordCount);
            Assert.Equal(0, m.TruncationCount);
            Assert.Equal(0, m.TotalRecordCount);

            // And the SERIALIZED manifest carries no record node of any kind.
            ConfigNode root = m.BuildFileNode(Header()).GetNode(RenderCompositionManifest.RootNodeName);
            Assert.Empty(root.GetNode("PLAN").GetNodes("UNIT"));
            Assert.Empty(root.GetNode("CHAIN").GetNodes("CHAIN_BUILD"));
            ConfigNode obs = root.GetNode("OBSERVED");
            Assert.Equal(0, obs.nodes.Count);
            Assert.Equal(0, obs.values.Count);
        }

        // ---- standalone anomaly echoes ----

        [Fact]
        public void AnomalyEchoRecord_IsKeptForEveryRaise_IncludingNonNumericAndDwellLessOnes()
        {
            var m = new RenderCompositionManifest();
            // A raise whose key is not a uint at all, and one that arrived with no dwell open: BOTH
            // used to vanish, leaving a verifier unable to tell "no anomaly" from "nowhere to land".
            m.AppendAnomalyEchoRecord("route-0001", "", "polyline-orbit-overlap", 100.0);
            m.AppendAnomalyEchoRecord("4242", "rec-a", "icon-jump", 101.0);
            // Reason-less raises are not records.
            m.AppendAnomalyEchoRecord("4242", "rec-a", "", 102.0);

            Assert.Equal(2, m.AnomalyEchoRecordCount);
            ConfigNode obs = m.BuildFileNode(Header())
                .GetNode(RenderCompositionManifest.RootNodeName).GetNode("OBSERVED");
            ConfigNode[] echoes = obs.GetNodes("ANOMALY_ECHO");
            Assert.Equal(2, echoes.Length);
            Assert.Equal("route-0001", echoes[0].GetValue("pidKey"));
            Assert.Equal("", echoes[0].GetValue("recId"));
            Assert.Equal("polyline-orbit-overlap", echoes[0].GetValue("reason"));
            Assert.Equal("100", echoes[0].GetValue("ut"));
            Assert.Equal("4242", echoes[1].GetValue("pidKey"));
            Assert.Equal("rec-a", echoes[1].GetValue("recId"));
        }

        [Fact]
        public void AnomalyEchoRecord_CapHit_DropsAndMarksItsOwnGlobalSection()
        {
            var m = new RenderCompositionManifest();
            int over = 6;
            for (int i = 0; i < RenderCompositionManifest.MaxAnomalyEchoRecords + over; i++)
                m.AppendAnomalyEchoRecord("4242", "rec-a", "icon-jump", 100.0 + i);

            Assert.Equal(RenderCompositionManifest.MaxAnomalyEchoRecords, m.AnomalyEchoRecordCount);
            ConfigNode obs = m.BuildFileNode(Header())
                .GetNode(RenderCompositionManifest.RootNodeName).GetNode("OBSERVED");
            ConfigNode truncated = Assert.Single(obs.GetNodes("TRUNCATED"));
            Assert.Equal("ANOMALY_ECHO" + RenderCompositionManifest.GlobalSectionSuffix,
                truncated.GetValue("section"));
            Assert.Equal("anomaly-echo", truncated.GetValue("kind"));
            Assert.Equal(over.ToString(CultureInfo.InvariantCulture), truncated.GetValue("droppedCount"));
        }

        // ---- the field-wise dwell compare must agree with the key it replaces ----

        [Fact]
        public void Dwell_FieldCompare_AgreesWithTheKeyOnEveryComponent()
        {
            // The open/close decision is now a field compare rather than a key-string compare, so a
            // component present in ONE of the two is a silent behavioural drift: either dwells stop
            // splitting where they used to, or they split where the key says they should not. This
            // walks every key component, changes exactly that one, and asserts BOTH the key moves and
            // a new dwell opens.
            var baseline = Sample(3u, "StockConic", "InSegment", "Kerbin", 0, 100.0, 100.0);
            baseline.PhaseKind = "ascent";
            baseline.ChainSignature = "sig-A";

            var mutations = new System.Collections.Generic.List<RenderCompositionManifest.DwellSample>();
            RenderCompositionManifest.DwellSample s;
            s = baseline; s.Visible = !baseline.Visible; mutations.Add(s);
            s = baseline; s.Treatment = "TracedPath"; mutations.Add(s);
            s = baseline; s.Coverage = "InInteriorGap"; mutations.Add(s);
            s = baseline; s.FrameBody = "Mun"; mutations.Add(s);
            s = baseline; s.SegmentIndex = 1; mutations.Add(s);
            s = baseline; s.PhaseKind = "hold"; mutations.Add(s);
            s = baseline; s.ChainSignature = "sig-B"; mutations.Add(s);

            string baseKey = RenderCompositionManifest.BuildDwellKey(baseline);
            for (int i = 0; i < mutations.Count; i++)
            {
                RenderCompositionManifest.DwellSample mutated = mutations[i];
                mutated.CurrentUT = 101.0;
                Assert.NotEqual(baseKey, RenderCompositionManifest.BuildDwellKey(mutated));

                var m = new RenderCompositionManifest();
                m.ObserveDwellFrame(baseline);
                m.ObserveDwellFrame(mutated);
                Assert.Equal(1, m.ClosedDwellCount);
                Assert.Equal(1, m.TransitionCount);
            }

            // ... and a frame that changes NOTHING in the key folds into the open dwell.
            var same = baseline;
            same.CurrentUT = 101.0;
            same.HeadUT = 101.0;
            var stable = new RenderCompositionManifest();
            stable.ObserveDwellFrame(baseline);
            stable.ObserveDwellFrame(same);
            Assert.Equal(0, stable.ClosedDwellCount);
            Assert.Equal(0, stable.TransitionCount);
            Assert.Equal(1, stable.OpenDwellCount);
        }

        // ---- the close-time marker re-sample ----

        [Fact]
        public void Dwell_MarkerTriple_IsReSampledAtCloseOnlyWhenTheStampWasFalse()
        {
            var m = new RenderCompositionManifest();
            int calls = 0;
            m.MarkerResampler = (uint pid, out bool decision, out bool tracedPath,
                out bool polyline, out bool iconSuppressed) =>
            {
                calls++;
                decision = true;
                tracedPath = true;
                polyline = false;
                iconSuppressed = false;
                return true;
            };

            // pid 1 stamped FALSE by the Director -> re-read at close, and the whole triple moves with
            // the decision so the four values always describe ONE sample.
            var stampedFalse = Sample(1u, "StockConic", "InSegment", "Kerbin", 0, 100.0, 100.0);
            stampedFalse.MarkerDecision = false;
            m.ObserveDwellFrame(stampedFalse);

            // pid 2 stamped TRUE -> already positive evidence, so no second read.
            var stampedTrue = Sample(2u, "StockConic", "InSegment", "Kerbin", 0, 100.0, 100.0);
            stampedTrue.MarkerDecision = true;
            stampedTrue.MarkerTracedPath = false;
            stampedTrue.MarkerPolyline = true;
            m.ObserveDwellFrame(stampedTrue);

            m.CloseAllOpenDwells(110.0);
            Assert.Equal(1, calls);

            ConfigNode obs = m.BuildFileNode(Header())
                .GetNode(RenderCompositionManifest.RootNodeName).GetNode("OBSERVED");
            ConfigNode[] dwells = obs.GetNodes("DWELL");
            Assert.Equal(2, dwells.Length);
            Assert.Equal("True", dwells[0].GetValue("markerDecision"));
            Assert.Equal("True", dwells[0].GetValue("markerTracedPath"));
            Assert.Equal("False", dwells[0].GetValue("markerPolyline"));
            Assert.Equal("True", dwells[1].GetValue("markerDecision"));
            Assert.Equal("True", dwells[1].GetValue("markerPolyline"));
        }

        [Fact]
        public void Dwell_MarkerReSample_LeavesTheStampAloneWhenNoSampleCouldBeTaken()
        {
            var m = new RenderCompositionManifest();
            m.MarkerResampler = (uint pid, out bool decision, out bool tracedPath,
                out bool polyline, out bool iconSuppressed) =>
            {
                decision = true;
                tracedPath = true;
                polyline = true;
                iconSuppressed = true;
                return false;   // "could not sample" - nothing may be overwritten
            };

            var s = Sample(1u, "StockConic", "InSegment", "Kerbin", 0, 100.0, 100.0);
            s.MarkerDecision = false;
            s.MarkerIconSuppressed = false;
            m.ObserveDwellFrame(s);
            m.CloseAllOpenDwells(110.0);

            ConfigNode dwell = Assert.Single(m.BuildFileNode(Header())
                .GetNode(RenderCompositionManifest.RootNodeName).GetNode("OBSERVED").GetNodes("DWELL"));
            Assert.Equal("False", dwell.GetValue("markerDecision"));
            Assert.Equal("False", dwell.GetValue("markerIconSuppressed"));
        }
    }
}
