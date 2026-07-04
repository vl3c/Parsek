using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Cutover-hardening (B4/D2, design §11.2) - the in-game gate that drives the REAL wired
    // ShadowRenderDriver.RunFrame (the unconditional typed spine) over a live faithful ghost and proves
    // the cold-load clock-readiness guard:
    //
    //  - liveUT <= 0 (the Planetarium UT=0 cold-load trap): RunFrame DEFERS the whole spine frame (renders
    //    nothing), so NO StockConic seed is stamped, and the once-per-event clock-not-ready anomaly fires.
    //  - liveUT > 0: the SAME ghost + scene stamps the seed normally (the guard only defers at UT<=0; a
    //    ready clock samples as before). This is the "the guard does not over-defer" arm.
    //
    // This is the Unity-coupled half of the cold-load guard (the pure predicate IsLiveClockReady + the
    // dedup + emit helpers are unit-tested headless in CutoverHardeningTests). RunFrame reads Time.frameCount
    // and drives a live MapViewScene, so it cannot run headless.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); FLIGHT only; career-independent. The
    // spine is unconditional since the Phase-5b flag removal, so no seam forcing is needed.
    public class ColdLoadClockGuardInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double Sma = 850000.0;
        private const double Ecc = 0.0;
        private const double Inc = 0.0;
        private const double KerbinRadiusFallback = 600000.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Cold-load clock guard: RunFrame (unconditional spine) DEFERS at liveUT<=0 (no seed "
                + "stamped, clock-not-ready anomaly fired) and samples normally at liveUT>0 (the guard does "
                + "not over-defer a ready clock)")]
        public void ColdLoadGuard_DefersAtUtZero_SamplesWhenReady()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double liveUT = Planetarium.GetUniversalTime();
            double startUT = liveUT - 1800.0;
            OrbitSegment seg = BuildSegment(startUT);

            bool prevForceTrace = MapRenderTrace.ForceEnabledForTesting;
            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            Recording rec = BuildRecording(startUT, seg);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("coldload-guard-start");
            ShadowRenderDriver.Reset();

            // Capture the trace sink so we can assert the clock-not-ready anomaly fired on the defer frame
            // WITHOUT depending on global log scraping (a robust in-game capture).
            var traceLines = new List<string>();
            System.Action<string> prevSink = ParsekLog.TestSinkForTesting;

            uint pid = 0u;
            try
            {
                // Reset BEFORE the first arm (and again in finally, mirroring FailClosedFaithfulInGameTest):
                // the spine:clock-not-ready anomaly dedups through a constant (key,signature) in
                // MapRenderTrace's cutover-anomaly dict, cleared only on scene switch - without this a
                // SECOND run of this test in the same scene session would have the anomaly suppressed and
                // false-fail the clockNotReadyFired assertion.
                MapRenderTrace.Reset();
                MapRenderTrace.ForceEnabledForTesting = true; // so the anomaly raise is live

                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex, rec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg, default(TrajectoryPoint), startUT, loopEpochShiftSeconds: 0.0);

                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null)
                {
                    InGameAssert.Skip("Faithful ghost did not create in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;

                var scene = new MapViewScene();
                if (!scene.IsActive)
                {
                    InGameAssert.Skip("MapViewScene not active (not in FLIGHT)");
                    return;
                }

                // --- Arm 1: liveUT = 0 (cold-load trap) -> the spine must DEFER. ---
                ParsekLog.TestSinkForTesting = line => traceLines.Add(line);
                scene.SetFrameInputs(GhostPlaybackLogic.LoopUnitSet.Empty, 0.0);
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                bool stampedAtZero = ShadowRenderDriver.TryGetFreshStockConicSeed(
                    pid, Time.frameCount, out OrbitSegment _, out string _);
                ParsekLog.TestSinkForTesting = prevSink;

                bool clockNotReadyFired = false;
                foreach (string l in traceLines)
                    if (l.Contains("[MapRenderTrace]")
                        && l.Contains("reason=" + MapRenderTrace.AnomalyClockNotReady))
                        clockNotReadyFired = true;

                InGameAssert.IsFalse(stampedAtZero,
                    "the cold-load guard must DEFER at liveUT=0: no StockConic seed may be stamped (a "
                    + "degenerate UT=0 ghost would otherwise place on the first cold-load frames)");
                InGameAssert.IsTrue(clockNotReadyFired,
                    "the cold-load defer must raise the clock-not-ready anomaly (reason="
                    + MapRenderTrace.AnomalyClockNotReady + ") so the defer is observable");

                // --- Arm 2: liveUT > 0 (ready clock) -> the SAME ghost/scene samples normally. ---
                scene.SetFrameInputs(GhostPlaybackLogic.LoopUnitSet.Empty, liveUT);
                ShadowRenderDriver.Reset();
                ShadowRenderDriver.RunFrame(scene);
                bool stampedWhenReady = ShadowRenderDriver.TryGetFreshStockConicSeed(
                    pid, Time.frameCount, out OrbitSegment readySeed, out string readyBody);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "ColdLoadGuard: stampedAtZero={0} clockNotReadyFired={1} stampedWhenReady={2} "
                    + "readyBody={3} readySma={4:F0}",
                    stampedAtZero, clockNotReadyFired, stampedWhenReady, readyBody ?? "?",
                    readySeed.semiMajorAxis));

                InGameAssert.IsTrue(stampedWhenReady,
                    "the guard must NOT over-defer: at a ready (positive) liveUT the SAME ghost must stamp "
                    + "its StockConic seed normally");
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("coldload-guard-cleanup");
                ShadowRenderDriver.Reset();
                MapRenderTrace.ForceEnabledForTesting = prevForceTrace;
                // Clear the once-per-scene anomaly dedup this run consumed (see the Reset before arm 1).
                MapRenderTrace.Reset();
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
            }
        }

        private static OrbitSegment BuildSegment(double startUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = startUT + 3600.0,
                inclination = Inc,
                eccentricity = Ecc,
                semiMajorAxis = Sma,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                bodyName = KerbinBodyName,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            };
        }

        private static Recording BuildRecording(double startUT, OrbitSegment seg)
        {
            double endUT = startUT + 3600.0;
            var rec = new Recording
            {
                RecordingId = "coldload-guard-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Cold-Load Guard",
                TerminalStateValue = null,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = KerbinBodyName,
                TerminalOrbitBody = KerbinBodyName,
                TerminalOrbitSemiMajorAxis = Sma,
                TerminalOrbitEccentricity = Ecc,
                TerminalOrbitInclination = Inc,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.0,
                TerminalOrbitEpoch = startUT,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                PlaybackEnabled = true,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0.0, longitude = 0.0, altitude = Sma - KerbinRadiusFallback,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 2200f, 0f), bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0.0, longitude = 5.0, altitude = Sma - KerbinRadiusFallback,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 2200f, 0f), bodyName = KerbinBodyName,
            });
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }
    }
}
