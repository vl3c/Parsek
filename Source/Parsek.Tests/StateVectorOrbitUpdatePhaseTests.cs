using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the units <c>GhostMapPresence.UpdateGhostOrbitFromStateVectors</c> calls.
    /// The fakes are uninitialized Unity objects (an alive one has its native pointer set), so
    /// every fact is a <see cref="UnityEntryFactAttribute"/>.
    /// </summary>
    [Collection("Sequential")]
    public class StateVectorOrbitUpdatePhaseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes priorScene;

        public StateVectorOrbitUpdatePhaseTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            OrbitSeedResolver.ResetForTesting();
            ResetCounters();
            priorScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.TRACKSTATION;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = priorScene;
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            OrbitSeedResolver.ResetForTesting();
            ResetCounters();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetCounters()
        {
            GhostMapPresence.gapGlideInertialSeedCount = 0;
            GhostMapPresence.gapGlideBodyFixedFallbackCount = 0;
            GhostMapPresence.seamComdRefreshCount = 0;
            GhostMapPresence.seamComdSteadyCount = 0;
            GhostMapPresence.seamComdMaxOffOrbitDegPostRefresh = 0.0;
        }

        private static Vessel Ghost(uint pid, bool withDriver = false)
        {
            var vessel = StateVectorOrbitUpdateCharacterizationTests.Alive(
                (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel)));
            vessel.persistentId = pid;
            if (withDriver)
            {
                var driver = StateVectorOrbitUpdateCharacterizationTests.Alive(
                    (OrbitDriver)FormatterServices.GetUninitializedObject(typeof(OrbitDriver)));
                driver.orbit = new Orbit { semiMajorAxis = 700000.0, eccentricity = 0.01, epoch = 500.0 };
                vessel.orbitDriver = driver;
            }
            return vessel;
        }

        private static TrajectoryPoint Pt(double ut) =>
            StateVectorOrbitUpdateCharacterizationTests.Pt(ut);

        private static Recording Rec(string id) =>
            StateVectorOrbitUpdateCharacterizationTests.Rec(id);

        private static GhostMapPresence.StateVectorWorldFrame Frame(string branch, uint anchorPid = 0u)
        {
            return new GhostMapPresence.StateVectorWorldFrame
            {
                Resolved = true,
                WorldPos = new Vector3d(1.0, 2.0, 3.0),
                Branch = branch,
                AnchorPid = anchorPid,
            };
        }

        private static CelestialBody Kerbin() => Body("Kerbin");

        private static CelestialBody Body(string name) =>
            StateVectorOrbitUpdateCharacterizationTests.Alive(
                TestBodyRegistry.CreateBody(name, 600000.0, 3.5316e12));

        // ---------------------------------------------------------------- miss / skip

        [UnityEntryFact]
        public void Miss_LogsTheReasonAsAnError()
        {
            GhostMapPresence.LogStateVectorUpdateMiss(5, Rec("m0"), Pt(500), Ghost(9100), 500.0, "body-not-found");

            Assert.Contains(logLines, l => l.Contains("[ERROR][GhostMap] update-state-vector-miss: rec=m0 idx=5")
                && l.Contains("ghostPid=9100") && l.Contains("reason=body-not-found"));
        }

        [UnityEntryFact]
        public void Miss_NullTrajectory_NamesNoRecording()
        {
            GhostMapPresence.LogStateVectorUpdateMiss(1, null, Pt(500), Ghost(9101), 500.0, "no-orbit-driver");

            Assert.Contains(logLines, l => l.Contains("update-state-vector-miss: rec=(null) idx=1 vessel=\"(null)\"")
                && l.Contains("reason=no-orbit-driver"));
        }

        [UnityEntryFact]
        public void Skip_LogsTheBranchAndFailureAsAWarning()
        {
            var frame = new GhostMapPresence.StateVectorWorldFrame
            {
                Resolved = false, Branch = "relative", FailureReason = "anchor-not-found", AnchorPid = 42u,
            };

            GhostMapPresence.LogStateVectorUpdateSkip(2, Rec("s0"), Pt(500), 500.0, Ghost(9102), frame);

            Assert.Contains(logLines, l => l.Contains("[WARN][GhostMap] update-state-vector-skip: rec=s0 idx=2")
                && l.Contains("branch=Relative") && l.Contains("reason=anchor-not-found"));
        }

        [UnityEntryFact]
        public void Skip_NullFailureReason_PrintsNull()
        {
            var frame = new GhostMapPresence.StateVectorWorldFrame { Resolved = false, Branch = "no-section" };

            GhostMapPresence.LogStateVectorUpdateSkip(2, Rec("s1"), Pt(500), 500.0, Ghost(9103), frame);

            Assert.Contains(logLines, l => l.Contains("update-state-vector-skip: rec=s1")
                && l.Contains("reason=(null)"));
        }

        // ---------------------------------------------------------------- active Re-Fly suppression

        [UnityEntryFact]
        public void Suppress_NoMarker_ReturnsFalseAndLeavesNoTrace()
        {
            bool suppressed = GhostMapPresence.TrySuppressStateVectorUpdateForActiveReFly(
                3, Rec("p0"), Pt(500), 500.0, Ghost(9104), Frame("relative", 7770u), null, null);

            Assert.False(suppressed);
            Assert.DoesNotContain(logLines, l => l.Contains("update-state-vector-suppressed"));
        }

        [UnityEntryFact]
        public void Suppress_InPlaceMarkerAnchoredToTheActiveVessel_DefersAndReturnsTrue()
        {
            Recording debris = StateVectorOrbitUpdateCharacterizationTests.SeedAnchorTree();
            StateVectorOrbitUpdateCharacterizationTests.ArmInPlaceMarker();

            bool suppressed = GhostMapPresence.TrySuppressStateVectorUpdateForActiveReFly(
                3, debris, Pt(500), 500.0, Ghost(9105), Frame("relative", 7770u),
                null, new Vector3d(1.0, 2.0, 3.0));

            Assert.True(suppressed);
            Assert.Contains(logLines, l => l.Contains("[INFO][GhostMap] update-state-vector-suppressed: rec=sv-debris idx=3")
                && l.Contains("sess=sess-sv removeReason="));
            Assert.Contains(logLines, l => l.Contains("update-state-vector-suppressed") && l.Contains("localOffset="));
        }

        [UnityEntryFact]
        public void Suppress_AbsoluteBranch_IsNotSuppressed()
        {
            StateVectorOrbitUpdateCharacterizationTests.SeedAnchorTree();
            StateVectorOrbitUpdateCharacterizationTests.ArmInPlaceMarker();

            bool suppressed = GhostMapPresence.TrySuppressStateVectorUpdateForActiveReFly(
                3, Rec("sv-debris"), Pt(500), 500.0, Ghost(9106), Frame("absolute"), null, null);

            Assert.False(suppressed);
        }

        // ---------------------------------------------------------------- SOI change

        [UnityEntryFact]
        public void SoiChange_NothingAppliedYet_IsAChangeAndLogs()
        {
            bool changed = GhostMapPresence.ResolveStateVectorSoiChange(
                4, Rec("o0"), 500.0, Ghost(9107), Kerbin(), Frame("absolute"), "gap-reason");

            Assert.True(changed);
            Assert.Contains(logLines, l => l.Contains("update-state-vector-soi-change: rec=o0 idx=4")
                && l.Contains("branch=Absolute") && l.Contains("body=Kerbin") && l.Contains("reason=gap-reason"));
        }

        [UnityEntryFact]
        public void SoiChange_SameBodyAsLastApplied_IsQuiet()
        {
            GhostMapPresence.ghostLastAppliedOrbitBody[9108u] = "Kerbin";

            bool changed = GhostMapPresence.ResolveStateVectorSoiChange(
                4, Rec("o1"), 500.0, Ghost(9108), Kerbin(), Frame("absolute"), null);

            Assert.False(changed);
            Assert.DoesNotContain(logLines, l => l.Contains("update-state-vector-soi-change"));
        }

        [UnityEntryFact]
        public void SoiChange_OtherBodyThanLastApplied_IsAChange()
        {
            GhostMapPresence.ghostLastAppliedOrbitBody[9109u] = "Kerbin";

            bool changed = GhostMapPresence.ResolveStateVectorSoiChange(
                4, Rec("o2"), 500.0, Ghost(9109), Body("Mun"), Frame("relative"), null);

            Assert.True(changed);
            Assert.Contains(logLines, l => l.Contains("update-state-vector-soi-change: rec=o2") && l.Contains("body=Mun"));
        }

        // ---------------------------------------------------------------- gap glide

        [UnityEntryFact]
        public void GapGlide_NoRotationPeriod_DeclinesAndCountsTheFallback()
        {
            OrbitSeedResolver.RotationPeriodForTesting = b => 0.0;
            bool fired = false;
            string decline = null;
            double lon = double.NaN;

            GhostMapPresence.SeedStateVectorGapGlideInertial(
                6, Rec("g0"), Pt(500), 2000.0, Ghost(9110, withDriver: true), Kerbin(),
                ref fired, ref decline, ref lon);

            Assert.False(fired);
            Assert.Equal("historical-rotation-unavailable", decline);
            Assert.True(double.IsNaN(lon));
            Assert.Equal(1, GhostMapPresence.gapGlideBodyFixedFallbackCount);
            Assert.Equal(0, GhostMapPresence.gapGlideInertialSeedCount);
            Assert.Contains(logLines, l => l.Contains("Gap-glide inertial seed declined for ghost pid=9110 recIndex=6 body=Kerbin")
                && l.Contains("shift=2000.0") && l.Contains("reason=historical-rotation-unavailable"));
            Assert.Contains(logLines, l => l.Contains("Gap-glide seed summary: inertial=0 bodyFixed=1"));
        }

        [UnityEntryFact]
        public void GapGlide_PointNotARecordedSample_DeclinesWithThePairReason()
        {
            OrbitSeedResolver.RotationPeriodForTesting = b => 21549.425;
            bool fired = false;
            string decline = null;
            double lon = double.NaN;

            // Rec's samples sit at UT 100 and 900; 500 is not one of them.
            GhostMapPresence.SeedStateVectorGapGlideInertial(
                6, Rec("g1"), Pt(500), 0.0, Ghost(9111, withDriver: true), Kerbin(),
                ref fired, ref decline, ref lon);

            Assert.False(fired);
            Assert.Equal(OrbitReseed.GapGlidePairPointNotFound, decline);
            Assert.Contains(logLines, l => l.Contains("Gap-glide inertial seed declined for ghost pid=9111")
                && l.Contains("reason=" + OrbitReseed.GapGlidePairPointNotFound));
        }

        [UnityEntryFact]
        public void GapGlide_NullTrajectory_DeclinesWithThePairReason()
        {
            OrbitSeedResolver.RotationPeriodForTesting = b => 21549.425;
            bool fired = false;
            string decline = null;
            double lon = double.NaN;

            GhostMapPresence.SeedStateVectorGapGlideInertial(
                6, null, Pt(500), 0.0, Ghost(9112, withDriver: true), Kerbin(),
                ref fired, ref decline, ref lon);

            Assert.False(fired);
            Assert.Equal(OrbitReseed.GapGlidePairPointNotFound, decline);
        }

        // ---------------------------------------------------------------- stale segment drive

        [UnityEntryFact]
        public void ClearStale_SegmentDriveState_IsDroppedAndLogged()
        {
            const uint pid = 9113u;
            GhostMapPresence.ghostOrbitBounds[pid] = (100.0, 200.0);
            GhostMapPresence.ghostBodyFrameOrbitBounds[pid] = (100.0, 200.0);
            GhostMapPresence.ghostOrbitLoopShiftedPids.Add(pid);
            GhostMapPresence.ghostOrbitEpochShift[pid] = 50.0;
            GhostMapPresence.ghostIconDrivePropagation[pid] =
                new GhostMapPresence.IconDrivePropagation { PropagateUT = 1.0, Frame = 2 };

            GhostMapPresence.ClearStaleSegmentDriveForStateVectorReseed(Ghost(pid), 7, 2000.0);

            Assert.False(GhostMapPresence.ghostOrbitBounds.ContainsKey(pid));
            Assert.False(GhostMapPresence.ghostBodyFrameOrbitBounds.ContainsKey(pid));
            Assert.DoesNotContain(pid, GhostMapPresence.ghostOrbitLoopShiftedPids);
            Assert.False(GhostMapPresence.ghostOrbitEpochShift.ContainsKey(pid));
            Assert.False(GhostMapPresence.ghostIconDrivePropagation.ContainsKey(pid));
            Assert.Contains(logLines, l => l.Contains("State-vector reseed cleared stale segment-drive state for ghost pid=9113 recIndex=7")
                && l.Contains("shift=2000.0") && l.Contains("scene=TRACKSTATION"));
        }

        [UnityEntryFact]
        public void ClearStale_OnlyAnIconDriveRecord_IsDroppedWithoutTheLine()
        {
            const uint pid = 9114u;
            GhostMapPresence.ghostIconDrivePropagation[pid] =
                new GhostMapPresence.IconDrivePropagation { PropagateUT = 1.0, Frame = 2 };

            GhostMapPresence.ClearStaleSegmentDriveForStateVectorReseed(Ghost(pid), 7, 0.0);

            Assert.False(GhostMapPresence.ghostIconDrivePropagation.ContainsKey(pid));
            Assert.DoesNotContain(logLines, l => l.Contains("cleared stale segment-drive state"));
        }

        [UnityEntryFact]
        public void ClearStale_OtherGhostsState_IsKept()
        {
            GhostMapPresence.ghostOrbitBounds[1u] = (100.0, 200.0);

            GhostMapPresence.ClearStaleSegmentDriveForStateVectorReseed(Ghost(9115), 7, 0.0);

            Assert.True(GhostMapPresence.ghostOrbitBounds.ContainsKey(1u));
            Assert.DoesNotContain(logLines, l => l.Contains("cleared stale segment-drive state"));
        }

        // ---------------------------------------------------------------- body-fixed reseed

        [UnityEntryFact]
        public void Reseed_SeedsTheOrbitAtTheLoopShiftedEpoch()
        {
            var vessel = Ghost(9130, withDriver: true);
            var kerbin = Kerbin();

            GhostMapPresence.ReseedStateVectorOrbitBodyFixed(
                3, Rec("r0"), Pt(500), 500.0, vessel, kerbin, Frame("absolute"),
                new Vector3d(700000.0, 0.0, 0.0), 250.0);

            Assert.Same(kerbin, vessel.orbitDriver.orbit.referenceBody);
            Assert.Equal(750.0, vessel.orbitDriver.orbit.epoch);
            Assert.Contains(logLines, l => l.Contains("[OrbitReseed]")
                && l.Contains("FromWorldPosAndRecordedVelocity: body=Kerbin ut=750.00"));
        }

        [UnityEntryFact]
        public void Reseed_NoLoopShift_SeedsAtTheSampleUT()
        {
            var vessel = Ghost(9131, withDriver: true);

            GhostMapPresence.ReseedStateVectorOrbitBodyFixed(
                3, null, Pt(500), 500.0, vessel, Kerbin(), Frame("absolute"),
                new Vector3d(700000.0, 0.0, 0.0), 0.0);

            Assert.Equal(500.0, vessel.orbitDriver.orbit.epoch);
            Assert.Contains(logLines, l => l.Contains("FromWorldPosAndRecordedVelocity: body=Kerbin ut=500.00"));
        }

        // ---------------------------------------------------------------- renderer + applied body

        [UnityEntryFact]
        public void RendererAndBody_RecordsTheAppliedBody()
        {
            var vessel = Ghost(9132);
            GhostMapPresence.ghostLastAppliedOrbitBody[9132u] = "Kerbin";

            GhostMapPresence.RebuildRendererAndRecordStateVectorBody(vessel, Body("Mun"), soiChanged: true);

            Assert.Equal("Mun", GhostMapPresence.ghostLastAppliedOrbitBody[9132u]);
        }

        [UnityEntryFact]
        public void RendererAndBody_SameBody_KeepsTheRecord()
        {
            var vessel = Ghost(9133);
            GhostMapPresence.ghostLastAppliedOrbitBody[9133u] = "Kerbin";
            GhostMapPresence.ghostLastAppliedOrbitBody[9134u] = "Duna";

            GhostMapPresence.RebuildRendererAndRecordStateVectorBody(vessel, Kerbin(), soiChanged: false);

            Assert.Equal("Kerbin", GhostMapPresence.ghostLastAppliedOrbitBody[9133u]);
            Assert.Equal("Duna", GhostMapPresence.ghostLastAppliedOrbitBody[9134u]);
            Assert.Equal(2, GhostMapPresence.ghostLastAppliedOrbitBody.Count);
        }

        // ---------------------------------------------------------------- seam CoMD

        [UnityEntryFact]
        public void Seam_FirstConic_IsRecordedWithoutARefreshWhenNoPrecalc()
        {
            Vessel ghost = Ghost(9116, withDriver: true);

            GhostMapPresence.RefreshStateVectorSeamComd(ghost);

            Assert.Equal((700000.0, 0.01, 500.0), GhostMapPresence.ghostLastAppliedOrbitElements[9116u]);
            Assert.Equal(0, GhostMapPresence.seamComdRefreshCount);
            Assert.Equal(0, GhostMapPresence.seamComdSteadyCount);
            Assert.Contains(logLines, l => l.Contains("Seam CoMD re-snap summary: refreshed=0 steady=0"));
        }

        [UnityEntryFact]
        public void Seam_UnchangedConic_CountsSteady()
        {
            Vessel ghost = Ghost(9117, withDriver: true);
            GhostMapPresence.ghostLastAppliedOrbitElements[9117u] = (700000.0, 0.01, 500.0);

            GhostMapPresence.RefreshStateVectorSeamComd(ghost);

            Assert.Equal(1, GhostMapPresence.seamComdSteadyCount);
            Assert.Equal(0, GhostMapPresence.seamComdRefreshCount);
        }

        [UnityEntryFact]
        public void Seam_ChangedEpoch_RecordsTheNewElements()
        {
            Vessel ghost = Ghost(9118, withDriver: true);
            GhostMapPresence.ghostLastAppliedOrbitElements[9118u] = (700000.0, 0.01, 400.0);

            GhostMapPresence.RefreshStateVectorSeamComd(ghost);

            Assert.Equal((700000.0, 0.01, 500.0), GhostMapPresence.ghostLastAppliedOrbitElements[9118u]);
            Assert.Equal(0, GhostMapPresence.seamComdSteadyCount);
        }

        [UnityEntryFact]
        public void Seam_NoOrbit_DoesNothing()
        {
            Vessel ghost = Ghost(9119, withDriver: true);
            ghost.orbitDriver.orbit = null;

            GhostMapPresence.RefreshStateVectorSeamComd(ghost);

            Assert.False(GhostMapPresence.ghostLastAppliedOrbitElements.ContainsKey(9119u));
            Assert.DoesNotContain(logLines, l => l.Contains("Seam CoMD re-snap summary"));
        }

        // ---------------------------------------------------------------- update line + stash

        [UnityEntryFact]
        public void Done_PlainReason_LogsItAndStashesTheFrame()
        {
            GhostMapPresence.LogStateVectorUpdateAndStash(
                8, Rec("u0"), Pt(500), 500.0, Ghost(9120), Kerbin(), Frame("absolute"),
                new Vector3d(1.0, 2.0, 3.0), null, null, "tracking-station-state-vector",
                false, double.NaN, null);

            Assert.Contains(logLines, l => l.Contains("[VERBOSE][GhostMap] update-state-vector: rec=u0 idx=8")
                && l.Contains("branch=Absolute") && l.Contains("body=Kerbin")
                && l.Contains("reason=tracking-station-state-vector"));
            GhostMapPresence.LastKnownGhostFrame frame = LastKnown(8);
            Assert.Equal("u0", frame.RecordingId);
            Assert.Equal("StateVector", frame.Source);
            Assert.Equal("Absolute", frame.Branch);
            Assert.Equal("Kerbin", frame.Body);
            Assert.Equal(9120u, frame.GhostPid);
            Assert.Equal(500.0, frame.LastUT);
        }

        [UnityEntryFact]
        public void Done_GapGlideInertial_AnnotatesTheSeedFrame()
        {
            GhostMapPresence.LogStateVectorUpdateAndStash(
                8, Rec("u1"), Pt(500), 500.0, Ghost(9121), Kerbin(), Frame("absolute"),
                new Vector3d(1.0, 2.0, 3.0), null, null, "orbit-raise-gap-points",
                true, 12.5, null);

            Assert.Contains(logLines, l => l.Contains("update-state-vector: rec=u1")
                && l.Contains("reason=orbit-raise-gap-points seedFrame=inertial inertialLon=12.5000"));
        }

        [UnityEntryFact]
        public void Done_GapGlideDeclined_NamesTheBodyFixedFallback()
        {
            GhostMapPresence.LogStateVectorUpdateAndStash(
                8, Rec("u2"), Pt(500), 500.0, Ghost(9122), Kerbin(), Frame("absolute"),
                new Vector3d(1.0, 2.0, 3.0), null, null, "orbit-raise-gap-points",
                false, double.NaN, "historical-rotation-unavailable");

            Assert.Contains(logLines, l => l.Contains("update-state-vector: rec=u2")
                && l.Contains("seedFrame=body-fixed") && l.Contains("decline=historical-rotation-unavailable"));
        }

        [UnityEntryFact]
        public void Done_GapReasonOnARelativeBranch_IsNotAnnotated()
        {
            GhostMapPresence.LogStateVectorUpdateAndStash(
                8, Rec("u3"), Pt(500), 500.0, Ghost(9123), Kerbin(), Frame("relative", 5u),
                new Vector3d(1.0, 2.0, 3.0), null, new Vector3d(4.0, 5.0, 6.0), "orbit-raise-gap-points",
                false, double.NaN, null);

            Assert.Contains(logLines, l => l.Contains("update-state-vector: rec=u3")
                && l.Contains("reason=orbit-raise-gap-points"));
            Assert.DoesNotContain(logLines, l => l.Contains("seedFrame="));
        }

        private static GhostMapPresence.LastKnownGhostFrame LastKnown(int idx)
        {
            var store = (Dictionary<int, GhostMapPresence.LastKnownGhostFrame>)typeof(GhostMapPresence)
                .GetField("lastKnownByRecordingIndex",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .GetValue(null);
            Assert.True(store.TryGetValue(idx, out GhostMapPresence.LastKnownGhostFrame frame));
            return frame;
        }
    }
}
