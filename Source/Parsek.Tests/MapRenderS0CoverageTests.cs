using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8e S0 coverage-closure instrument unit tests. PURELY ADDITIVE diagnostics that prove, before
    /// any legacy deletion, that the Director's accounted set is a superset of what the autonomous render
    /// path draws.
    /// <list type="bullet">
    /// <item><b>Instrument 1</b> - the polyline accounted-vs-drawn assertion (RecordingId domain): the pure
    /// <see cref="GhostMapPresence.IsDrawnRecordingAccounted"/> predicate + the end-to-end
    /// <see cref="GhostMapPresence.AssertDrawnRecordingsAccounted"/> seam. The assertion must FIRE for a
    /// proto-less drawn recording NOT in the coverage set, and PASS for one that is + for a proto-bearing
    /// one (non-vacuity).</item>
    /// <item><b>Instrument 2</b> - the icon-floor gap counter: the pure
    /// <see cref="IconFloorGapCounter.Classify"/> reason mapping + the per-frame accumulator + a
    /// warp-stable-key guard on the rate-limited summary (vary UT, hold the clock fixed -&gt; one emit;
    /// mirrors <see cref="GhostPlaybackLogHygieneTests"/> / <see cref="MapRenderTraceTests"/>).</item>
    /// </list>
    /// Touches shared static state (GhostMapPresence coverage sets + pid bridge, IconFloorGapCounter
    /// accumulator, ParsekLog sink), so it runs in the Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class MapRenderS0CoverageTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private double clock = 1000.0;

        public MapRenderS0CoverageTests()
        {
            GhostMapPresence.ResetCoverageSetsForTesting();
            GhostMapPresence.ResetForTesting();
            IconFloorGapCounter.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ClockOverrideForTesting = () => clock;
        }

        public void Dispose()
        {
            GhostMapPresence.ResetCoverageSetsForTesting();
            GhostMapPresence.ResetForTesting();
            IconFloorGapCounter.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
        }

        // =====================================================================
        // Instrument 1 - pure accounted predicate (RecordingId domain)
        // =====================================================================

        [Fact]
        public void IsDrawnRecordingAccounted_ProtoBearing_Accounted()
        {
            // A drawn recording whose RecordingId is in the proto-bearing set (the Director's enumerated
            // pid set bridged to RecordingIds) is accounted via the pid path, NOT via the coverage set.
            var protoBearing = new HashSet<string>(StringComparer.Ordinal) { "rec-proto" };
            var coverage = new HashSet<string>(StringComparer.Ordinal);

            Assert.True(GhostMapPresence.IsDrawnRecordingAccounted("rec-proto", protoBearing, coverage));
        }

        [Fact]
        public void IsDrawnRecordingAccounted_InProtoLessCoverage_Accounted()
        {
            // A proto-less (pid-0) drawn recording in the coverage set is accounted via the non-proto path.
            var protoBearing = new HashSet<string>(StringComparer.Ordinal);
            var coverage = new HashSet<string>(StringComparer.Ordinal) { "rec-atmo" };

            Assert.True(GhostMapPresence.IsDrawnRecordingAccounted("rec-atmo", protoBearing, coverage));
        }

        [Fact]
        public void IsDrawnRecordingAccounted_DrawnButInNeither_FIRES()
        {
            // NON-VACUITY: a recording drawn by the autonomous walk but NEITHER proto-bearing NOR in the
            // proto-less coverage set is UNACCOUNTED - the deletion-blocker the assertion exists to surface.
            var protoBearing = new HashSet<string>(StringComparer.Ordinal) { "rec-other-proto" };
            var coverage = new HashSet<string>(StringComparer.Ordinal) { "rec-other-atmo" };

            Assert.False(GhostMapPresence.IsDrawnRecordingAccounted("rec-orphan", protoBearing, coverage));
        }

        [Fact]
        public void IsDrawnRecordingAccounted_NullOrEmpty_NeverFlagged()
        {
            // A null/empty id is not a real drawn recording; never flag it (would be a false anomaly).
            Assert.True(GhostMapPresence.IsDrawnRecordingAccounted(null, null, null));
            Assert.True(GhostMapPresence.IsDrawnRecordingAccounted("", null, null));
        }

        // =====================================================================
        // Instrument 1 - end-to-end assertion seam (the pid-bridge into the RecordingId domain)
        // =====================================================================

        [Fact]
        public void AssertDrawnRecordingsAccounted_ProtoLessDrawnNotInCoverage_FIRES()
        {
            // A proto-LESS recording (pid 0) is drawn but NOT folded into the coverage set: the assertion
            // must fire. This models the deletion-blocker case where the non-proto walk drew something the
            // Director never accounted for. (We stamp drawn=true, protoLess=false so it goes only into the
            // drawn set, simulating a draw path that bypassed NoteDrawnRecordingCoverage's pid-0 fold.)
            GhostMapPresence.SetFrameCoverageForTesting("rec-orphan", drawn: true, protoLess: false);
            // No proto-bearing pid maps to rec-orphan, so the pid bridge cannot account for it either.

            var unaccounted = new List<string>();
            GhostMapPresence.AssertDrawnRecordingsAccounted(
                (recId, pb, plc, drawn) => unaccounted.Add(recId));

            Assert.Contains("rec-orphan", unaccounted);
        }

        [Fact]
        public void AssertDrawnRecordingsAccounted_ProtoLessInCoverage_DoesNotFire()
        {
            // A proto-less recording drawn AND folded into the coverage set is accounted - no fire.
            GhostMapPresence.SetFrameCoverageForTesting("rec-atmo", drawn: true, protoLess: true);

            var unaccounted = new List<string>();
            GhostMapPresence.AssertDrawnRecordingsAccounted(
                (recId, pb, plc, drawn) => unaccounted.Add(recId));

            Assert.Empty(unaccounted);
        }

        [Fact]
        public void AssertDrawnRecordingsAccounted_ProtoBearingDrawn_AccountedViaPidBridge()
        {
            // A proto-BEARING recording drawn via the polyline (pid != 0, so deliberately NOT in the
            // coverage set) is accounted by the RecordingId-DOMAIN bridge from the live pid map. This is
            // the critical non-vacuity guard: the bridge must translate the proto-bearing pid back into
            // the RecordingId domain (the draw domain) so the pid-0-vs-pid-set false "absent" trap is
            // avoided. Drawn but NOT in coverage; only the pid bridge can account for it.
            GhostMapPresence.SetProtoBearingPidForTesting(pid: 12345, recordingId: "rec-proto");
            GhostMapPresence.SetFrameCoverageForTesting("rec-proto", drawn: true, protoLess: false);

            var unaccounted = new List<string>();
            GhostMapPresence.AssertDrawnRecordingsAccounted(
                (recId, pb, plc, drawn) => unaccounted.Add(recId));

            Assert.Empty(unaccounted); // accounted via the pid bridge, not the coverage set
        }

        [Fact]
        public void AssertDrawnRecordingsAccounted_MixedFrame_FiresOnlyForTheOrphan()
        {
            // A realistic frame: a proto-bearing recording, a proto-less covered recording, and one orphan.
            // Only the orphan fires.
            GhostMapPresence.SetProtoBearingPidForTesting(pid: 1, recordingId: "rec-proto");
            GhostMapPresence.SetFrameCoverageForTesting("rec-proto", drawn: true, protoLess: false);
            GhostMapPresence.SetFrameCoverageForTesting("rec-atmo", drawn: true, protoLess: true);
            GhostMapPresence.SetFrameCoverageForTesting("rec-orphan", drawn: true, protoLess: false);

            var unaccounted = new List<string>();
            GhostMapPresence.AssertDrawnRecordingsAccounted(
                (recId, pb, plc, drawn) => unaccounted.Add(recId));

            Assert.Single(unaccounted);
            Assert.Equal("rec-orphan", unaccounted[0]);
        }

        [Fact]
        public void AssertDrawnRecordingsAccounted_NothingDrawn_NoOp()
        {
            int fires = 0;
            GhostMapPresence.AssertDrawnRecordingsAccounted((recId, pb, plc, drawn) => fires++);
            Assert.Equal(0, fires);
        }

        [Fact]
        public void NoteDrawnRecordingCoverage_PidZero_AddsToBothSets()
        {
            // The live producer semantics: a pid-0 draw goes into BOTH the drawn set and the proto-less
            // coverage set (so the assertion accounts for it). End-to-end: nothing fires.
            GhostMapPresence.NoteDrawnRecordingCoverage("rec-atmo", ghostPid: 0);

            var unaccounted = new List<string>();
            GhostMapPresence.AssertDrawnRecordingsAccounted(
                (recId, pb, plc, drawn) => unaccounted.Add(recId));
            Assert.Empty(unaccounted);
        }

        [Fact]
        public void NoteDrawnRecordingCoverage_NonZeroPid_DrawnButNotCoverage_NeedsPidBridge()
        {
            // The live producer semantics: a proto-bearing (pid != 0) draw goes into the drawn set ONLY,
            // NOT the coverage set. Without a matching pid bridge it would be UNACCOUNTED - proving the
            // proto-less fold is genuinely gated on pid 0 (not "echo every draw").
            GhostMapPresence.NoteDrawnRecordingCoverage("rec-proto", ghostPid: 999);
            // No SetProtoBearingPidForTesting => the pid bridge is empty => unaccounted.

            var unaccounted = new List<string>();
            GhostMapPresence.AssertDrawnRecordingsAccounted(
                (recId, pb, plc, drawn) => unaccounted.Add(recId));
            Assert.Contains("rec-proto", unaccounted);
        }

        [Fact]
        public void ClearFrameCoverageSets_EmptiesTheDrawnSet()
        {
            GhostMapPresence.NoteDrawnRecordingCoverage("rec-atmo", ghostPid: 0);
            GhostMapPresence.ClearFrameCoverageSets();

            int fires = 0;
            GhostMapPresence.AssertDrawnRecordingsAccounted((recId, pb, plc, drawn) => fires++);
            Assert.Equal(0, fires); // drawn set empty after the per-frame clear
        }

        // =====================================================================
        // Instrument 2 - pure icon-floor reason classifier
        // =====================================================================

        [Fact]
        public void Classify_GateOff_ReturnsGateOff_NotCountable()
        {
            var r = IconFloorGapCounter.Classify(
                gateOn: false, hasBounds: true, freshSeed: true, seedBodyResolved: true);
            Assert.Equal(IconFloorGapCounter.FloorReason.GateOff, r);
            Assert.False(IconFloorGapCounter.IsCountableFloor(r));
        }

        [Fact]
        public void Classify_NoBounds_ReturnsNoBounds_Countable()
        {
            var r = IconFloorGapCounter.Classify(
                gateOn: true, hasBounds: false, freshSeed: false, seedBodyResolved: false);
            Assert.Equal(IconFloorGapCounter.FloorReason.NoBounds, r);
            Assert.True(IconFloorGapCounter.IsCountableFloor(r));
        }

        [Fact]
        public void Classify_FreshSeedBodyUnresolved_ReturnsUnresolvableSeedBody_Countable()
        {
            var r = IconFloorGapCounter.Classify(
                gateOn: true, hasBounds: true, freshSeed: true, seedBodyResolved: false);
            Assert.Equal(IconFloorGapCounter.FloorReason.UnresolvableSeedBody, r);
            Assert.True(IconFloorGapCounter.IsCountableFloor(r));
        }

        [Fact]
        public void Classify_NoFreshSeed_ReturnsNoFreshSeed_Countable()
        {
            var r = IconFloorGapCounter.Classify(
                gateOn: true, hasBounds: true, freshSeed: false, seedBodyResolved: false);
            Assert.Equal(IconFloorGapCounter.FloorReason.NoFreshSeed, r);
            Assert.True(IconFloorGapCounter.IsCountableFloor(r));
        }

        [Fact]
        public void Classify_DirectorDrove_ReturnsNone_NotCountable()
        {
            // Gate on, bounds present, fresh seed, body resolves => the Director DROVE the icon. Not a
            // legacy-floor frame. Mirrors ShadowRenderDriver.IsDirectorDriveActive exactly.
            var r = IconFloorGapCounter.Classify(
                gateOn: true, hasBounds: true, freshSeed: true, seedBodyResolved: true);
            Assert.Equal(IconFloorGapCounter.FloorReason.None, r);
            Assert.False(IconFloorGapCounter.IsCountableFloor(r));
        }

        // =====================================================================
        // Instrument 2 - accumulator + flush (no-op for non-countable reasons)
        // =====================================================================

        [Fact]
        public void NoteLegacyFloor_CountableReason_Accumulates()
        {
            IconFloorGapCounter.NoteLegacyFloor(7, IconFloorGapCounter.FloorReason.NoFreshSeed);
            IconFloorGapCounter.NoteLegacyFloor(7, IconFloorGapCounter.FloorReason.NoFreshSeed);
            Assert.Equal(2, IconFloorGapCounter.CountForTesting(7, IconFloorGapCounter.FloorReason.NoFreshSeed));
        }

        [Fact]
        public void NoteLegacyFloor_NonCountableReason_NoOp()
        {
            IconFloorGapCounter.NoteLegacyFloor(7, IconFloorGapCounter.FloorReason.None);
            IconFloorGapCounter.NoteLegacyFloor(7, IconFloorGapCounter.FloorReason.GateOff);
            Assert.Equal(0, IconFloorGapCounter.BucketCountForTesting);
        }

        [Fact]
        public void FlushFrameSummary_EmitsAggregate_ThenResets()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            IconFloorGapCounter.NoteLegacyFloor(11, IconFloorGapCounter.FloorReason.NoFreshSeed);
            IconFloorGapCounter.NoteLegacyFloor(22, IconFloorGapCounter.FloorReason.NoBounds);
            IconFloorGapCounter.FlushFrameSummary();

            Assert.Contains(logLines, l =>
                l.Contains("[MapRender]") && l.Contains("icon-floor:")
                && l.Contains("pids=[11,22]") && l.Contains("no-bounds") && l.Contains("no-fresh-seed"));
            // Accumulator reset after the flush.
            Assert.Equal(0, IconFloorGapCounter.BucketCountForTesting);

            MapRenderTrace.ForceEnabledForTesting = false;
        }

        [Fact]
        public void FlushFrameSummary_NothingAccumulated_NoOp()
        {
            MapRenderTrace.ForceEnabledForTesting = true;
            IconFloorGapCounter.FlushFrameSummary();
            Assert.DoesNotContain(logLines, l => l.Contains("icon-floor:"));
            MapRenderTrace.ForceEnabledForTesting = false;
        }

        // =====================================================================
        // Instrument 2 - WARP-STABLE rate-limit key guard (the #1063 / #1064 lesson)
        // =====================================================================

        [Fact]
        public void FlushFrameSummary_WarpAdvancingPidsAndCounts_SameKeyCollapsesToOneLine()
        {
            // The summary's rate-limit KEY is the constant "icon-floor-fallback" - WARP-STABLE. Across N
            // frames the per-frame pids / reasons / counts ADVANCE (they live in the BODY), but the key
            // does not, so within one wall-clock window exactly ONE line emits. Clock held fixed; vary the
            // accumulated content every "frame" to mimic warp churn.
            MapRenderTrace.ForceEnabledForTesting = true;

            for (int frame = 0; frame < 50; frame++)
            {
                // A different pid + reason mix each frame (warp-advancing content), clock unchanged.
                IconFloorGapCounter.NoteLegacyFloor(
                    (uint)(100 + frame),
                    (frame % 2 == 0)
                        ? IconFloorGapCounter.FloorReason.NoFreshSeed
                        : IconFloorGapCounter.FloorReason.NoBounds);
                IconFloorGapCounter.FlushFrameSummary();
            }

            int emitted = logLines.Count(l => l.Contains("[MapRender]") && l.Contains("icon-floor:"));
            // Exactly one line proves the warp-advancing pids/counts are in the BODY, not the KEY.
            Assert.Equal(1, emitted);

            MapRenderTrace.ForceEnabledForTesting = false;
        }

        [Fact]
        public void FlushFrameSummary_AfterIntervalElapses_EmitsAgain()
        {
            // Advancing the wall clock past the 5 s rate-limit window re-opens the gate for the same key.
            MapRenderTrace.ForceEnabledForTesting = true;

            IconFloorGapCounter.NoteLegacyFloor(1, IconFloorGapCounter.FloorReason.NoFreshSeed);
            IconFloorGapCounter.FlushFrameSummary();
            clock += 6.0; // past the 5 s interval
            IconFloorGapCounter.NoteLegacyFloor(2, IconFloorGapCounter.FloorReason.NoBounds);
            IconFloorGapCounter.FlushFrameSummary();

            int emitted = logLines.Count(l => l.Contains("[MapRender]") && l.Contains("icon-floor:"));
            Assert.Equal(2, emitted);

            MapRenderTrace.ForceEnabledForTesting = false;
        }
    }
}
