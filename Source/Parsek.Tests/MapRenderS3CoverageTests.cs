using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Display;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8e S3a deletion-safety gate unit tests. PURELY ADDITIVE diagnostics that PROVE, before S3b
    /// deletes the legacy polyline ownership publish (<c>activeLegRecordings</c>), that the deletion drops
    /// nothing: every recording the autonomous walk publishes into the LEGACY ownership set this frame is
    /// EITHER director-owned (so it keeps its proto-line/icon SUPPRESSION) OR pid-0 / proto-less (nothing to
    /// suppress; the kept walk draws it regardless of ownership). A legacy-owned leg in NEITHER set is
    /// proto-BEARING and not director-owned: deleting the legacy ownership would lose its suppression and
    /// produce a double-draw artifact - the deletion BLOCKER the gate surfaces.
    /// <list type="bullet">
    /// <item>the pure <see cref="GhostMapPresence.IsLegacyOwnedLegCoveredByDeletion"/> predicate: FIRES for
    /// a proto-bearing legacy-owned recId in NEITHER set (the blocker), PASSES for a drew-set one,
    /// PASSES for a pid-0 one in the proto-less set, never flags null/empty.</item>
    /// <item>the end-to-end <see cref="GhostMapPresence.AssertLegacyOwnedLegsCovered"/> seam, driving the
    /// legacy-owned set (GhostMapPresence) + the drew (actual-draw) view
    /// (<see cref="GhostTrajectoryPolylineRenderer.DrewNonOrbitalLegRecordingsThisFrame"/> via
    /// <c>SetOwnershipPublishForTesting</c>).</item>
    /// <item>the 8e S3a.1 BRIDGE-LEG regression: a recording that is legacy-owned AND drew-set-owned (via
    /// the any-draw publish, even when the Director classified the span StockConic) AND proto-bearing must
    /// NOT fire - the direct lock for the re-aim StockConic bridge-leg coverage gap S3a.1 closes.</item>
    /// <item>a WARP-STABLE-key guard on the new anomaly's per-recId rate-limit
    /// (<see cref="MapRenderProbe.PassesPerRecIdRateLimit"/>): vary the UT/leg content every frame, hold the
    /// wall clock -&gt; ONE pass per recId per window (the recId is the KEY; UT lives in the body). Mirrors
    /// the S0 instrument warp-stable test (<see cref="MapRenderS0CoverageTests"/>).</item>
    /// </list>
    /// Touches shared static state (GhostMapPresence coverage sets + the polyline renderer ownership sets),
    /// so it runs in the Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class MapRenderS3CoverageTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private double clock = 2000.0;

        public MapRenderS3CoverageTests()
        {
            GhostMapPresence.ResetCoverageSetsForTesting();
            GhostMapPresence.ResetForTesting();
            GhostTrajectoryPolylineRenderer.Clear();
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
            GhostTrajectoryPolylineRenderer.Clear();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
        }

        // =====================================================================
        // Pure deletion-safety predicate (RecordingId domain)
        // =====================================================================

        [Fact]
        public void IsLegacyOwnedLegCoveredByDeletion_DrewSet_Covered()
        {
            // A legacy-owned recording also in the drew (actual-draw) set keeps its proto-line/icon
            // suppression after the legacy publish is deleted (the drew set still grants it). Covered.
            var drew = new HashSet<string>(StringComparer.Ordinal) { "rec-director" };
            var protoLess = new HashSet<string>(StringComparer.Ordinal);

            Assert.True(GhostMapPresence.IsLegacyOwnedLegCoveredByDeletion(
                "rec-director", drew, protoLess));
        }

        [Fact]
        public void IsLegacyOwnedLegCoveredByDeletion_ProtoLess_Covered()
        {
            // A pid-0 / proto-less legacy-owned recording has NO proto to suppress, and the kept walk draws
            // it independent of ownership, so dropping the legacy ownership is invisible. Covered.
            var drew = new HashSet<string>(StringComparer.Ordinal);
            var protoLess = new HashSet<string>(StringComparer.Ordinal) { "rec-atmo" };

            Assert.True(GhostMapPresence.IsLegacyOwnedLegCoveredByDeletion(
                "rec-atmo", drew, protoLess));
        }

        [Fact]
        public void IsLegacyOwnedLegCoveredByDeletion_ProtoBearingNotDrawn_FIRES()
        {
            // NON-VACUITY: a legacy-owned recording in NEITHER set is proto-BEARING (not pid-0) AND did
            // NOT draw -> deleting the legacy ownership loses its suppression (double-draw). The deletion
            // BLOCKER the gate exists to surface.
            var drew = new HashSet<string>(StringComparer.Ordinal) { "rec-other-director" };
            var protoLess = new HashSet<string>(StringComparer.Ordinal) { "rec-other-atmo" };

            Assert.False(GhostMapPresence.IsLegacyOwnedLegCoveredByDeletion(
                "rec-blocker", drew, protoLess));
        }

        [Fact]
        public void IsLegacyOwnedLegCoveredByDeletion_NullOrEmpty_NeverFlagged()
        {
            // A null/empty id is not a real legacy-owned leg; never flag it (would be a false anomaly).
            Assert.True(GhostMapPresence.IsLegacyOwnedLegCoveredByDeletion(null, null, null));
            Assert.True(GhostMapPresence.IsLegacyOwnedLegCoveredByDeletion("", null, null));
        }

        // =====================================================================
        // End-to-end assertion seam (legacy-owned set + director-owned view)
        // =====================================================================

        [Fact]
        public void AssertLegacyOwnedLegsCovered_ProtoBearingNotDrawn_FIRES()
        {
            // A legacy-owned recording that is NOT in the drew set and NOT proto-less must fire: the
            // deletion-blocker case where the legacy publish granted suppression nothing else covers.
            GhostMapPresence.SetLegacyOwnedForTesting("rec-blocker", legacyOwned: true);
            // No drew-set publish, no proto-less coverage for rec-blocker.

            var uncovered = new List<string>();
            GhostMapPresence.AssertLegacyOwnedLegsCovered(
                (recId, doCount, plCount, loCount) => uncovered.Add(recId));

            Assert.Contains("rec-blocker", uncovered);
        }

        [Fact]
        public void AssertLegacyOwnedLegsCovered_DrewSet_DoesNotFire()
        {
            // A legacy-owned recording ALSO published into the drew (actual-draw) set (the same-frame view
            // from GhostTrajectoryPolylineRenderer) is covered - no fire.
            GhostMapPresence.SetLegacyOwnedForTesting("rec-director", legacyOwned: true);
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                "rec-director", inDrewSet: true, inLegacySet: true);

            var uncovered = new List<string>();
            GhostMapPresence.AssertLegacyOwnedLegsCovered(
                (recId, doCount, plCount, loCount) => uncovered.Add(recId));

            Assert.Empty(uncovered);
        }

        [Fact]
        public void AssertLegacyOwnedLegsCovered_BridgeLeg_ProtoBearingDrewSetOwned_DoesNotFire()
        {
            // 8e S3a.1 BRIDGE-LEG REGRESSION (the direct lock for the re-aim StockConic bridge gap):
            // the re-aim "bridge" leg DRAWS a brief body-fixed segment that lies on the conic, but the
            // Director classified that UT span StockConic (NOT TracedPath), so before S3a.1 the drew-set
            // publish (gated on ownedByTreatment) was SKIPPED - the leg was legacy-owned + proto-BEARING
            // but NOT drew-set-owned -> the gate FIRED a false uncovered-legacy-owned-leg anomaly. After
            // S3a.1 the publish is DECOUPLED: the leg drew, so it lands in the drew set on the any-draw
            // path regardless of the StockConic classification. Model it: legacy-owned + drew-set-owned
            // (inDrewSet:true) + proto-BEARING (NO proto-less coverage stamped). The gate must NOT fire.
            const string recBridge = "rec-stockconic-bridge";
            GhostMapPresence.SetLegacyOwnedForTesting(recBridge, legacyOwned: true);
            // The any-draw publish (StockConic bridge leg drew) -> drew set, NOT proto-less.
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                recBridge, inDrewSet: true, inLegacySet: true);
            // Intentionally NO SetFrameCoverageForTesting(protoLess:true): this leg is proto-BEARING, so the
            // ONLY thing that can cover it is the drew set - exactly what S3a.1 fixes.

            var uncovered = new List<string>();
            GhostMapPresence.AssertLegacyOwnedLegsCovered(
                (recId, doCount, plCount, loCount) => uncovered.Add(recId));

            Assert.DoesNotContain(recBridge, uncovered);
            Assert.Empty(uncovered);
        }

        [Fact]
        public void AssertLegacyOwnedLegsCovered_ProtoLess_DoesNotFire()
        {
            // A pid-0 legacy-owned recording folded into the proto-less coverage set is covered - no fire.
            GhostMapPresence.SetLegacyOwnedForTesting("rec-atmo", legacyOwned: true);
            GhostMapPresence.SetFrameCoverageForTesting("rec-atmo", drawn: true, protoLess: true);

            var uncovered = new List<string>();
            GhostMapPresence.AssertLegacyOwnedLegsCovered(
                (recId, doCount, plCount, loCount) => uncovered.Add(recId));

            Assert.Empty(uncovered);
        }

        [Fact]
        public void AssertLegacyOwnedLegsCovered_MixedFrame_FiresOnlyForTheBlocker()
        {
            // A realistic frame: a drew-set leg, a proto-less leg, and one proto-bearing-not-drawn
            // blocker - all three legacy-owned. Only the blocker fires.
            GhostMapPresence.SetLegacyOwnedForTesting("rec-director", legacyOwned: true);
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                "rec-director", inDrewSet: true, inLegacySet: true);

            GhostMapPresence.SetLegacyOwnedForTesting("rec-atmo", legacyOwned: true);
            GhostMapPresence.SetFrameCoverageForTesting("rec-atmo", drawn: true, protoLess: true);

            GhostMapPresence.SetLegacyOwnedForTesting("rec-blocker", legacyOwned: true);

            var uncovered = new List<string>();
            GhostMapPresence.AssertLegacyOwnedLegsCovered(
                (recId, doCount, plCount, loCount) => uncovered.Add(recId));

            Assert.Single(uncovered);
            Assert.Equal("rec-blocker", uncovered[0]);
        }

        [Fact]
        public void AssertLegacyOwnedLegsCovered_CountsReportSetSizes()
        {
            // The callback's counts feed the anomaly body. drew=1, protoLess=1, legacyOwned=3
            // (the three legacy-owned recordings stamped below).
            GhostMapPresence.SetLegacyOwnedForTesting("rec-director", legacyOwned: true);
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                "rec-director", inDrewSet: true, inLegacySet: true);
            GhostMapPresence.SetLegacyOwnedForTesting("rec-atmo", legacyOwned: true);
            GhostMapPresence.SetFrameCoverageForTesting("rec-atmo", drawn: true, protoLess: true);
            GhostMapPresence.SetLegacyOwnedForTesting("rec-blocker", legacyOwned: true);

            int doCount = -1, plCount = -1, loCount = -1;
            GhostMapPresence.AssertLegacyOwnedLegsCovered(
                (recId, d, p, l) => { doCount = d; plCount = p; loCount = l; });

            Assert.Equal(1, doCount);   // drew (actual-draw) set
            Assert.Equal(1, plCount);   // proto-less coverage set
            Assert.Equal(3, loCount);   // legacy-owned set
        }

        [Fact]
        public void AssertLegacyOwnedLegsCovered_NothingLegacyOwned_NoOp()
        {
            int fires = 0;
            GhostMapPresence.AssertLegacyOwnedLegsCovered((recId, d, p, l) => fires++);
            Assert.Equal(0, fires);
        }

        [Fact]
        public void NoteLegacyOwnedLeg_PopulatesTheSet_ProtoBearingFires()
        {
            // The live producer semantics: NoteLegacyOwnedLeg adds the recId to the legacy-owned set. With
            // no director-owned publish and no proto-less coverage, the proto-bearing leg is the blocker.
            GhostMapPresence.NoteLegacyOwnedLeg("rec-blocker");

            var uncovered = new List<string>();
            GhostMapPresence.AssertLegacyOwnedLegsCovered(
                (recId, d, p, l) => uncovered.Add(recId));
            Assert.Contains("rec-blocker", uncovered);
        }

        [Fact]
        public void NoteLegacyOwnedLeg_NullOrEmpty_NoOp()
        {
            GhostMapPresence.NoteLegacyOwnedLeg(null);
            GhostMapPresence.NoteLegacyOwnedLeg("");
            int fires = 0;
            GhostMapPresence.AssertLegacyOwnedLegsCovered((recId, d, p, l) => fires++);
            Assert.Equal(0, fires);
        }

        [Fact]
        public void ClearFrameCoverageSets_EmptiesTheLegacyOwnedSet()
        {
            GhostMapPresence.NoteLegacyOwnedLeg("rec-blocker");
            GhostMapPresence.ClearFrameCoverageSets();

            int fires = 0;
            GhostMapPresence.AssertLegacyOwnedLegsCovered((recId, d, p, l) => fires++);
            Assert.Equal(0, fires); // legacy-owned set empty after the per-frame clear
        }

        // =====================================================================
        // WARP-STABLE rate-limit key guard (the #1063 lesson)
        // =====================================================================

        [Fact]
        public void PassesPerRecIdRateLimit_WarpAdvancingUtSameRecId_OnePassPerWindow()
        {
            // The anomaly's rate-limit KEY is the RecordingId - WARP-STABLE. Across N frames the UT / leg
            // detail ADVANCE (they live in the anomaly BODY, fed to EmitAnomaly), but the held wall clock
            // keeps a persistent blocker (same recId) to exactly ONE pass per window. Clock held fixed;
            // vary the simulated UT every "frame" to mimic warp churn.
            var lastEmit = new Dictionary<string, double>(StringComparer.Ordinal);
            double realtime = 5000.0; // wall clock held fixed across the burst

            int passes = 0;
            for (int frame = 0; frame < 50; frame++)
            {
                // The UT advances each frame (warp), but it is NOT the key - only the recId is.
                double simulatedUT = 1000.0 + frame * 10.0;
                if (MapRenderProbe.PassesPerRecIdRateLimit(
                        lastEmit, "rec-persistent-blocker", realtime, minIntervalSeconds: 1.0))
                {
                    passes++;
                }
                // (simulatedUT is the body content the real callback would format; referenced so the intent
                // is explicit that it varies while the key does not.)
                Assert.True(simulatedUT >= 1000.0);
            }

            // Exactly one pass proves the warp-advancing UT is in the BODY, not the KEY.
            Assert.Equal(1, passes);
        }

        [Fact]
        public void PassesPerRecIdRateLimit_AfterIntervalElapses_PassesAgain()
        {
            // Advancing the wall clock past the interval re-opens the gate for the same recId.
            var lastEmit = new Dictionary<string, double>(StringComparer.Ordinal);

            Assert.True(MapRenderProbe.PassesPerRecIdRateLimit(
                lastEmit, "rec-blocker", realtime: 100.0, minIntervalSeconds: 1.0));
            // Still inside the window -> blocked.
            Assert.False(MapRenderProbe.PassesPerRecIdRateLimit(
                lastEmit, "rec-blocker", realtime: 100.5, minIntervalSeconds: 1.0));
            // Past the window -> passes again.
            Assert.True(MapRenderProbe.PassesPerRecIdRateLimit(
                lastEmit, "rec-blocker", realtime: 101.5, minIntervalSeconds: 1.0));
        }

        [Fact]
        public void PassesPerRecIdRateLimit_DistinctRecIds_EachPassesOncePerWindow()
        {
            // Two distinct blockers in the same window each emit once (the key is per-recId, not global).
            var lastEmit = new Dictionary<string, double>(StringComparer.Ordinal);
            double realtime = 7000.0;

            Assert.True(MapRenderProbe.PassesPerRecIdRateLimit(lastEmit, "rec-a", realtime, 1.0));
            Assert.True(MapRenderProbe.PassesPerRecIdRateLimit(lastEmit, "rec-b", realtime, 1.0));
            // Both re-blocked within the window.
            Assert.False(MapRenderProbe.PassesPerRecIdRateLimit(lastEmit, "rec-a", realtime, 1.0));
            Assert.False(MapRenderProbe.PassesPerRecIdRateLimit(lastEmit, "rec-b", realtime, 1.0));
        }

        [Fact]
        public void PassesPerRecIdRateLimit_NullDictOrEmptyRecId_NeverPasses()
        {
            var lastEmit = new Dictionary<string, double>(StringComparer.Ordinal);
            Assert.False(MapRenderProbe.PassesPerRecIdRateLimit(null, "rec", 1.0, 1.0));
            Assert.False(MapRenderProbe.PassesPerRecIdRateLimit(lastEmit, null, 1.0, 1.0));
            Assert.False(MapRenderProbe.PassesPerRecIdRateLimit(lastEmit, "", 1.0, 1.0));
        }
    }
}
