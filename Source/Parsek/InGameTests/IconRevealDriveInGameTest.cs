using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Parsek.MapRender;
using Parsek.Patches;
using UnityEngine;

namespace Parsek.InGameTests
{
    // A looped ghost's map icon is never shown where stock left it (todo
    // MAP-ICON-SHOWN-BEFORE-DRIVEN-2026-11-24).
    //
    // The whole handoff runs inside ONE synchronous test body, so no FixedUpdate can fall between its
    // steps: exactly the render frame without a physics step (or the paused game) the defect needs, with
    // nothing left to the frame rate. The real icon-drive Prefix, stock updateFromParameters and the real
    // line Postfix run over a real ghost ProtoVessel:
    //  1. the Director owns the leg as a TracedPath: the Prefix hides the icon and lets stock propagate
    //     the raw recorded epoch at the live clock (a third of an orbit away from the recorded phase);
    //  2. the Director hands the ghost back to StockConic (fresh seed) and the Postfix shows the icon;
    //  3. the icon must sit on the seed's baked conic at the live clock.
    //
    // The renderer's VectorLine is made by hand: OrbitRendererBase.Start would make it on the next frame,
    // and a test that waits a frame no longer controls whether a FixedUpdate runs.
    public class IconRevealDriveInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const double Sma = 800000.0;
        private const double Ecc = 0.05;

        // Whole periods plus this fraction: the raw epoch at the live clock is far from the recorded phase.
        private const double ShiftPeriods = 100.37;
        private const double MinStaleMeters = 500000.0;
        private const double MaxPlacedMeters = 10.0;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "A looped ghost handed back from the polyline on a frame with no physics step (or while paused) is placed on its orbit before its map icon is shown, never drawn where stock propagated the raw epoch")]
        public void PolylineHandback_WithoutAPhysicsStep_PlacesTheIconBeforeShowingIt()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double period = 2.0 * System.Math.PI
                * System.Math.Sqrt(Sma * Sma * Sma / kerbin.gravParameter);
            double shift = ShiftPeriods * period;
            double liveUT = Planetarium.GetUniversalTime();
            double effUT = liveUT - shift;
            OrbitSegment seg = BuildSegment(effUT);

            System.Func<double> prevUTNow = GhostMapPresence.CurrentUTNow;
            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();

            Recording rec = BuildRecording(seg);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();
            RecordingStore.AddCommittedInternal(rec);
            int recordingIndex = RecordingStore.CommittedRecordings.Count - 1;
            GhostMapPresence.CurrentUTNow = () => liveUT;
            GhostMapPresence.RemoveAllGhostVessels("icon-reveal-drive-test-start");
            ShadowRenderDriver.Reset();

            uint pid = 0u;
            try
            {
                Vessel ghost = GhostMapPresence.CreateGhostVesselFromSource(
                    recordingIndex, rec, GhostMapPresence.TrackingStationGhostSource.Segment,
                    seg, default(TrajectoryPoint), effUT, loopEpochShiftSeconds: shift);
                if (ghost == null || ghost.orbitDriver == null || ghost.orbitDriver.orbit == null
                    || ghost.orbitRenderer == null)
                {
                    InGameAssert.Skip("Ghost / orbit renderer did not resolve in this context (no proto)");
                    return;
                }
                pid = ghost.persistentId;
                OrbitRenderer renderer = ghost.orbitRenderer;
                EnsureOrbitLine(renderer);
                InGameAssert.IsNotNull(renderer.OrbitLine,
                    "the test must give the ghost's renderer its line (the Postfix returns without one)");

                // 1. The polyline owns the leg. The Prefix hides the icon and defers to stock.
                int frame = Time.frameCount;
                ShadowRenderDriver.SetTracedPathIntentStampForTesting(pid, frame);
                // Stock's MainPhysics refreshes the vessel's world position after the step; without it the
                // position read below would still be the one ghost creation placed.
                ghost.orbitDriver.updateFromParameters();
                ghost.precalc.CalculatePhysicsStats();
                GhostOrbitLinePatch.Postfix(renderer);
                InGameAssert.IsTrue(GhostMapPresence.IsIconSuppressed(pid),
                    "while the polyline owns the leg the icon must be hidden");
                InGameAssert.IsTrue(renderer.drawIcons == OrbitRendererBase.DrawIcons.NONE,
                    "while the polyline owns the leg the proto icon must not be drawn");

                Vector3d expected = MapRenderProbe.OrbitRelativePositionYup(
                    MapRenderProbe.BuildPhaseMatchedReferenceOrbit(seg, kerbin, shift), liveUT);
                double staleMeters = (ghost.GetWorldPos3D() - kerbin.position - expected).magnitude;
                InGameAssert.IsGreaterThan(staleMeters, MinStaleMeters,
                    "stock must have left the hidden icon far from its orbit, or the handoff proves nothing");

                // 2. The Director hands the ghost back to its conic. The marker still covers it here: the
                //    icon is in the suppressed set until the drive places it.
                ShadowRenderDriver.SetTracedPathIntentStampForTesting(pid, -1);
                ShadowRenderDriver.SetStockConicSeedForTesting(pid, frame, seg, KerbinBodyName);
                InGameAssert.IsTrue(GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(pid),
                    "until the reveal the non-proto marker must draw");

                // 3. The reveal, with no FixedUpdate since the hide.
                GhostOrbitLinePatch.Postfix(renderer);
                double placedMeters = (ghost.GetWorldPos3D() - kerbin.position - expected).magnitude;

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "IconRevealDrive: pid={0} frame={1} staleMeters={2:F0} placedMeters={3:F3} drawIcons={4} "
                    + "suppressed={5} lineActive={6} testFrameUnchanged={7}",
                    pid, frame, staleMeters, placedMeters, renderer.drawIcons,
                    GhostMapPresence.IsIconSuppressed(pid), renderer.OrbitLine.active,
                    Time.frameCount == frame));

                InGameAssert.IsTrue(renderer.drawIcons == OrbitRendererBase.DrawIcons.OBJ,
                    "the reveal must show the proto icon");
                InGameAssert.IsFalse(GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(pid),
                    "a shown proto icon must not have the non-proto marker beside it");
                InGameAssert.IsLessThan(placedMeters, MaxPlacedMeters,
                    "a shown proto icon must sit where the drive puts it, not where stock left it");
            }
            finally
            {
                ShadowRenderDriver.Reset();
                if (pid != 0u)
                    GhostMapPresence.RemoveAllGhostVessels("icon-reveal-drive-test-cleanup");
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
                GhostMapPresence.CurrentUTNow = prevUTNow;
            }
        }

        private static void EnsureOrbitLine(OrbitRendererBase renderer)
        {
            if (renderer.OrbitLine != null)
                return;
            FieldInfo field = AccessTools.Field(typeof(OrbitRendererBase), "orbitLine");
            MethodInfo make = AccessTools.Method(typeof(OrbitRendererBase), "MakeLine");
            object[] args = { null };
            make.Invoke(renderer, args);
            field.SetValue(renderer, args[0]);
        }

        private static OrbitSegment BuildSegment(double effUT)
        {
            return new OrbitSegment
            {
                startUT = effUT - 100.0,
                endUT = effUT + 600.0,
                inclination = 0.0,
                eccentricity = Ecc,
                semiMajorAxis = Sma,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = effUT - 100.0,
                bodyName = KerbinBodyName,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            };
        }

        private static Recording BuildRecording(OrbitSegment seg)
        {
            var rec = new Recording
            {
                RecordingId = "icon-reveal-drive-" + System.Guid.NewGuid().ToString("N"),
                VesselName = "Parsek Icon Reveal Drive Test",
                TerminalStateValue = null,
                EndpointPhase = RecordingEndpointPhase.OrbitSegment,
                EndpointBodyName = KerbinBodyName,
                TerminalOrbitBody = KerbinBodyName,
                TerminalOrbitSemiMajorAxis = Sma,
                TerminalOrbitEccentricity = Ecc,
                TerminalOrbitInclination = 0.0,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.0,
                TerminalOrbitEpoch = seg.startUT,
                ExplicitStartUT = seg.startUT,
                ExplicitEndUT = seg.endUT,
                PlaybackEnabled = true,
            };
            rec.Points.Add(BuildPoint(seg.startUT, 0.0));
            rec.Points.Add(BuildPoint(seg.endUT, 5.0));
            rec.OrbitSegments.Add(seg);
            rec.MarkFilesDirty();
            return rec;
        }

        private static TrajectoryPoint BuildPoint(double ut, double longitude)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = longitude,
                altitude = 160000.0,
                rotation = Quaternion.identity,
                velocity = new Vector3(0f, 2200f, 0f),
                bodyName = KerbinBodyName,
            };
        }
    }
}
