using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Automated merge gate for the claw/grapple connection producer
    /// (docs/dev/design-logistics-claw-producer.md; M-MIS-10 cell #4, label
    /// mmis10-claw). Drives the REAL coupling primitives on live parts so the
    /// operator residual shrinks to stock contact physics:
    ///
    /// <para><b>Capture tier (feasibility decision).</b> The decompile findings
    /// (docs/dev/research/claw-grapple-coupling-internals.md 1.2 / 2.1) prove
    /// that ModuleGrappleNode.Grapple ends in exactly <c>Part.Couple</c> and
    /// Release ends in exactly <c>Part.Undock(DockedVesselInfo)</c> - the same
    /// primitives docking ports use, firing the same
    /// <c>GameEvents.onPartCouple</c> / <c>onPartUndock</c> /
    /// <c>onVesselsUndocking</c> in the same order. This test drives those
    /// primitives directly on live spawned parts (tier b): everything
    /// Parsek-side (classifier on real Parts, Grapple stamping, EVA-suppression
    /// silence, route window capture + undock completion, the PotatoRoid
    /// ghost-visual build, the structural-grab admission verdict) runs through
    /// the REAL event pipeline. NOT exercised (the honest residual, collect
    /// opportunistically in live play): the stock capture FSM itself (the
    /// 0.06 m raycast + captureMinFwdDot + rel-velocity gates) and
    /// ModuleGrappleNode's own bookkeeping around the primitives (synthetic
    /// grapple AttachNode, DOCKEDVESSEL persistence, pivot joint) - a full
    /// physics capture in a headless-driven scene is flaky by construction
    /// (tier a rejected).</para>
    ///
    /// <para><b>Shape.</b> Spawns a single-part GrapplingDevice vessel and a
    /// single-part PotatoRoid vessel near the active vessel via the production
    /// spawn path (VesselSpawner.SpawnAtPosition), couples the claw into the
    /// active vessel pre-recording (classifier check on the claw-absorbed
    /// direction, findings 1.3), records, couples the asteroid onto the claw
    /// part (the exact stock asteroid-grab event shape: from=PotatoRoid,
    /// to=GrapplingDevice, findings 4), asserts the live merge-branch window,
    /// builds the ghost visual from the merge child's live snapshot, releases
    /// via Part.Undock, and asserts the window completes and the tree analyzes
    /// as a structural grab (NoDeliveryManifest after the empty-grapple skip,
    /// never UnsupportedConnectionKind).</para>
    ///
    /// <para><b>Isolation.</b> Isolated tier only: mutates the active vessel
    /// (the claw part is coupled into it) and the live recording state. All
    /// spawned vessels, the ghost build, and the test tree are torn down in
    /// <c>finally</c>; the isolated-batch baseline quickload restores the
    /// active vessel afterwards.</para>
    /// </summary>
    public sealed class GrappleCaptureInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string ClawPartName = "GrapplingDevice";
        private const string AsteroidPartName = "PotatoRoid";

        // Both fixtures spawn east of the active vessel along the local
        // parallel: the claw at +30 m, the asteroid at +45 m (15 m past the
        // claw part it couples to, keeping the programmatic-couple joint's
        // lever arm short while clearing both hulls).
        private const double ClawSpawnOffsetMeters = 30.0;
        private const double AsteroidSpawnOffsetMeters = 45.0;
        private const float SpawnLoadTimeoutSeconds = 30f;
        private const float RecordingStartTimeoutSeconds = 5f;
        private const float CoupleEventTimeoutSeconds = 15f;
        private const float UndockEventTimeoutSeconds = 15f;
        private const int SettleFrames = 3;

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - spawns vessels, couples a claw part into the ACTIVE vessel, and " +
            "starts/stops a live recording. Use Run All + Isolated or the row play button in a " +
            "disposable FLIGHT session; the baseline quickload restores the active vessel afterwards.";

        [InGameTest(Category = "LogisticsGrapple", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Automated claw-producer gate (mmis10-claw): spawns a GrapplingDevice vessel and a " +
                "PotatoRoid via the production spawn path, drives the REAL Part.Couple / Part.Undock primitives " +
                "(the exact primitives ModuleGrappleNode ends in), and asserts the live onPartCouple classifier " +
                "stamps Grapple with EVA suppression silent, the merge-branch route window carries " +
                "kind=Grapple + the asteroid endpoint, the PotatoRoid ghost visual builds with non-empty " +
                "asteroid geometry from the live merge snapshot, the release completes the window through the " +
                "real onVesselsUndocking split, and the structural grab analyzes NoDeliveryManifest (admitted, " +
                "skipped as a non-stop - never UnsupportedConnectionKind). Residual: the stock 0.06 m contact " +
                "raycast capture FSM itself stays stock-physics territory")]
        public IEnumerator GrappleCapture_ProgrammaticCoupleReleaseCycle_StampsAndCompletesGrappleWindow()
        {
            // Post-restore unpack wait (yields BEFORE any mutation).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // PRECONDITIONS --------------------------------------------------
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
                InGameAssert.Skip("ParsekFlight.Instance is null; FLIGHT scene controller required");
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel");
            if (!(activeVessel.loaded && !activeVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={activeVessel.loaded}, packed={activeVessel.packed})");
            if (activeVessel.isEVA)
                InGameAssert.Skip(
                    "Active vessel is an EVA kerbal; a couple involving it would be EVA-suppressed by design - " +
                    "run from a normal vessel");
            if (Math.Abs(activeVessel.latitude) > 85.0)
                InGameAssert.Skip(
                    "Active vessel is within 5 degrees of a pole; the longitude-offset spawn math is unreliable there");
            // ALL-TESTS-AUTO self-setup: flight auto-records, so an active session
            // recording/tree is the NORMAL batch state, not an operator error - a
            // skip here made this gate unrunnable in any ordinary session (first
            // live batch, 2026-07-08). Stop and discard the ephemeral session
            // recording instead, through the same surface the RuntimeTests
            // cleanups use; the isolated tier's post-batch baseline quickload
            // restores the pre-batch world regardless, so no player data is lost.
            if (flight.IsRecording || flight.ActiveTreeForSerialization != null)
            {
                if (!flight.HasActiveTree)
                {
                    if (flight.IsRecording)
                        flight.StopRecording();
                }
                else
                {
                    System.Reflection.MethodInfo discard = typeof(ParsekFlight).GetMethod(
                        "DiscardActiveTreeForSuppressedSceneExit",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (discard == null)
                        InGameAssert.Skip(
                            "ParsekFlight.DiscardActiveTreeForSuppressedSceneExit reflection surface unavailable");
                    try
                    {
                        discard.Invoke(flight, new object[]
                        {
                            HighLogic.LoadedScene,
                            Planetarium.GetUniversalTime(),
                            "GrappleCapture gate setup: discard the ephemeral auto-record session tree",
                            false
                        });
                    }
                    catch (System.Reflection.TargetInvocationException ex)
                    {
                        InGameAssert.Fail(
                            "setup: DiscardActiveTreeForSuppressedSceneExit threw " +
                            $"{ex.InnerException?.GetType().Name ?? ex.GetType().Name}: " +
                            $"{ex.InnerException?.Message ?? ex.Message}");
                    }
                }
                ParsekLog.Info("TestRunner",
                    "GrappleCapture setup: stopped/discarded the active auto-record session so the gate can run");
                InGameAssert.IsFalse(flight.IsRecording,
                    "setup: the session recording must be stopped before the gate starts its own");
                InGameAssert.IsTrue(flight.ActiveTreeForSerialization == null,
                    "setup: the session tree must be discarded before the gate creates its own");
            }
            if (PrefabPart(ClawPartName) == null)
                InGameAssert.Skip($"Claw prefab '{ClawPartName}' not in PartLoader (part-pack-less install)");
            if (PrefabPart(AsteroidPartName) == null)
                InGameAssert.Skip($"Asteroid prefab '{AsteroidPartName}' not in PartLoader (part-pack-less install)");

            string runId = "mmis10-claw-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var captured = new List<string>();
            var priorObserver = ParsekLog.TestObserverForTesting;

            Vessel clawVessel = null;
            Part clawPart = null;
            Vessel roidVessel = null;
            Part roidPart = null;
            GhostBuildResult ghostBuild = null;

            try
            {
                ParsekLog.TestObserverForTesting = line => { captured.Add(line); priorObserver?.Invoke(line); };

                // ==========================================================
                // 1. Spawn the claw vessel via the production spawn path.
                // ==========================================================
                uint clawPid = SpawnSinglePartVessel(
                    ClawPartName, runId + "-claw", VesselType.Probe, activeVessel, ClawSpawnOffsetMeters);
                if (clawPid == 0)
                    InGameAssert.Skip("Claw vessel spawn failed (SpawnAtPosition returned 0); see Spawner log lines");
                IEnumerator clawWait = WaitForSpawnedVesselLive(clawPid, "claw", v => clawVessel = v);
                while (clawWait.MoveNext())
                    yield return clawWait.Current;
                if (clawVessel == null)
                    InGameAssert.Skip(
                        $"Spawned claw vessel pid={clawPid.ToString(IC)} never became loaded+unpacked within " +
                        $"{SpawnLoadTimeoutSeconds.ToString("R", IC)}s");
                clawPart = clawVessel.rootPart;
                InGameAssert.IsNotNull(clawPart, "spawned claw vessel must have a root part");
                InGameAssert.IsNotNull(clawPart.FindModuleImplementing<ModuleGrappleNode>(),
                    "spawned claw part must carry ModuleGrappleNode (live part, not prefab)");

                // ==========================================================
                // 2. Couple the claw INTO the active vessel (pre-recording).
                //    Direction from=claw / to=active mirrors findings 1.3
                //    ("claw ship absorbed into the dominant vessel"). No
                //    recording yet, so no window - this pins the CLASSIFIER
                //    on the real event with the grapple module on the FROM
                //    side only.
                // ==========================================================
                int beforeClawCouple = captured.Count;
                clawPart.Couple(activeVessel.rootPart);
                for (int i = 0; i < SettleFrames; i++)
                    yield return null;

                InGameAssert.IsTrue(
                    FindFrom(captured, beforeClawCouple, l =>
                        IsProducerClassifiedLine(l) && l.Contains("kind=Grapple")
                        && l.Contains($"fromPart={ClawPartName}") && l.Contains("involvesEva=False")),
                    "claw->active couple must classify kind=Grapple with involvesEva=False on the real " +
                    "onPartCouple (grapple module on the FROM side only)");
                InGameAssert.AreEqual(activeVessel, clawPart.vessel,
                    "claw part must belong to the active vessel after Part.Couple");

                // ==========================================================
                // 3. Spawn the PotatoRoid via the production spawn path.
                // ==========================================================
                uint roidSpawnPid = SpawnSinglePartVessel(
                    AsteroidPartName, runId + "-roid", VesselType.SpaceObject, activeVessel, AsteroidSpawnOffsetMeters);
                if (roidSpawnPid == 0)
                    InGameAssert.Skip("PotatoRoid vessel spawn failed (SpawnAtPosition returned 0); see Spawner log lines");
                IEnumerator roidWait = WaitForSpawnedVesselLive(roidSpawnPid, "asteroid", v => roidVessel = v);
                while (roidWait.MoveNext())
                    yield return roidWait.Current;
                if (roidVessel == null)
                    InGameAssert.Skip(
                        $"Spawned PotatoRoid vessel pid={roidSpawnPid.ToString(IC)} never became loaded+unpacked " +
                        $"within {SpawnLoadTimeoutSeconds.ToString("R", IC)}s");
                roidPart = roidVessel.rootPart;
                InGameAssert.IsNotNull(roidPart, "spawned asteroid vessel must have a root part");
                uint roidVesselPid = roidVessel.persistentId;
                uint roidPartPid = roidPart.persistentId;
                string roidVesselName = roidVessel.vesselName;

                // ==========================================================
                // 4. Record, then grab the asteroid: from=PotatoRoid,
                //    to=GrapplingDevice - the exact stock asteroid-grab
                //    event shape (findings 4: SpaceObject never dominates).
                // ==========================================================
                flight.StartRecording(suppressStartScreenMessage: true);
                yield return WaitUntil(() => flight.IsRecording,
                    RecordingStartTimeoutSeconds, "recording start");
                InGameAssert.IsTrue(flight.IsRecording,
                    $"StartRecording did not start within {RecordingStartTimeoutSeconds.ToString("R", IC)}s");
                RecordingTree tree = flight.ActiveTreeForSerialization;
                InGameAssert.IsNotNull(tree, "Active tree should exist while recording");

                int beforeRoidCouple = captured.Count;
                roidPart.Couple(clawPart);
                yield return WaitUntil(
                    () => FindFrom(captured, beforeRoidCouple, IsWindowCapturedLine),
                    CoupleEventTimeoutSeconds, "route window capture after asteroid couple");

                // ASSERT block 1 (yield-free): classifier + suppression silence
                // + the live merge-branch window.
                InGameAssert.IsTrue(
                    FindFrom(captured, beforeRoidCouple, l =>
                        IsProducerClassifiedLine(l) && l.Contains("kind=Grapple")
                        && l.Contains($"fromPart={AsteroidPartName}") && l.Contains($"toPart={ClawPartName}")
                        && l.Contains("involvesEva=False")),
                    "asteroid->claw couple must classify kind=Grapple (from=PotatoRoid, to=GrapplingDevice) " +
                    "with involvesEva=False on the real onPartCouple");
                InGameAssert.IsFalse(
                    FindFrom(captured, beforeRoidCouple, l => l.Contains("EVA grab, route window suppressed")),
                    "EVA-grab suppression must NOT trigger for an asteroid grab");
                InGameAssert.IsTrue(
                    FindFrom(captured, beforeRoidCouple, l => IsWindowCapturedLine(l) && l.Contains("kind=Grapple")),
                    "the captured route window must log kind=Grapple " +
                    $"(timeout {CoupleEventTimeoutSeconds.ToString("R", IC)}s)");

                string mergeChildId = tree.ActiveRecordingId;
                Recording mergeChild = null;
                InGameAssert.IsTrue(
                    !string.IsNullOrEmpty(mergeChildId)
                        && tree.Recordings.TryGetValue(mergeChildId, out mergeChild) && mergeChild != null,
                    $"merge child recording '{mergeChildId ?? "<null>"}' not found in the test tree");
                InGameAssert.IsTrue(
                    mergeChild.RouteConnectionWindows != null && mergeChild.RouteConnectionWindows.Count == 1,
                    "the merge child must carry exactly one route connection window, got " +
                    (mergeChild.RouteConnectionWindows?.Count ?? 0).ToString(IC));
                RouteConnectionWindow window = mergeChild.RouteConnectionWindows[0];
                InGameAssert.AreEqual(RouteConnectionKind.Grapple, window.TransferKind,
                    "the live claw window must be stamped Grapple (not DockingPort, not Unknown)");
                InGameAssert.AreEqual(roidVesselPid, window.TransferTargetVesselPid,
                    "the window endpoint must be the grabbed asteroid vessel's pre-couple pid");
                InGameAssert.IsFalse(double.IsNaN(window.DockUT), "the window must carry a DockUT");
                InGameAssert.IsFalse(window.IsComplete, "the window must be incomplete before the release");
                InGameAssert.IsTrue(
                    window.EndpointPartPersistentIds != null
                        && window.EndpointPartPersistentIds.Contains(roidPartPid),
                    "the window endpoint part set must contain the PotatoRoid part pid");

                // ==========================================================
                // 5. PotatoRoid ghost-visual build from the LIVE merge child
                //    snapshot (the M-MIS-10 highest-risk cell: the asteroid
                //    mesh is procedural; the build clones prefab renderers).
                // ==========================================================
                ghostBuild = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                    mergeChild, runId + "-ghost");
                InGameAssert.IsNotNull(ghostBuild,
                    "ghost visual build from the live coupled-vessel snapshot must produce a result");
                int roidRendererCount = CountRenderersUnderGhostPart(ghostBuild.root, roidPartPid);
                InGameAssert.IsTrue(roidRendererCount > 0,
                    $"the PotatoRoid part (pid={roidPartPid.ToString(IC)}) must contribute non-empty ghost " +
                    "geometry (M-MIS-10 cell #4 risk: procedural asteroid mesh vs prefab renderer clone) - " +
                    "a zero here means asteroid-run ghosts render without the asteroid");
                UnityEngine.Object.Destroy(ghostBuild.root);
                ghostBuild = null;

                // ==========================================================
                // 6. Release through the REAL claw release primitive
                //    (findings 2.1: ModuleGrappleNode.Release ends in
                //    Part.Undock, firing onPartUndock -> onVesselsUndocking).
                // ==========================================================
                yield return WaitUntil(() => flight.IsRecording,
                    RecordingStartTimeoutSeconds, "recorder restart on merge child");
                InGameAssert.IsTrue(flight.IsRecording,
                    "the recorder must be recording the merged vessel before the release");

                int beforeUndock = captured.Count;
                var releaseInfo = new DockedVesselInfo
                {
                    name = roidVesselName,
                    vesselType = VesselType.SpaceObject,
                    rootPartUId = roidPart.flightID
                };
                roidPart.Undock(releaseInfo);
                yield return WaitUntil(
                    () => FindFrom(captured, beforeUndock, IsWindowCompletedLine),
                    UndockEventTimeoutSeconds, "route window completion after release");

                // ASSERT block 2 (yield-free): the release split completed the
                // window and classification survived.
                InGameAssert.IsTrue(
                    FindFrom(captured, beforeUndock, IsWindowCompletedLine),
                    "the release must complete the route window through the real onVesselsUndocking split " +
                    $"(timeout {UndockEventTimeoutSeconds.ToString("R", IC)}s)");
                InGameAssert.IsTrue(window.IsComplete, "the window must be complete after the release");
                InGameAssert.AreEqual(RouteConnectionKind.Grapple, window.TransferKind,
                    "the Grapple stamp must survive the release completion");
                InGameAssert.IsTrue(window.UndockUT >= window.DockUT,
                    "the release UT must not precede the grab UT");

                // ==========================================================
                // 7. Admission verdict on the live tree: a structural grab
                //    (zero cargo both directions) is ADMITTED and skipped as
                //    a non-stop; with no other window the run rejects
                //    NoDeliveryManifest - never UnsupportedConnectionKind
                //    (fail-closed is for Unknown producers only).
                // ==========================================================
                flight.StopRecording();
                int beforeAnalysis = captured.Count;
                RouteAnalysisResult analysis = RouteAnalysisEngine.AnalyzeTree(tree);
                InGameAssert.IsNotNull(analysis, "AnalyzeTree returned null");
                InGameAssert.AreEqual(RouteAnalysisStatus.NoDeliveryManifest, analysis.Status,
                    $"a structural (empty) grapple window must skip as a non-stop and reject " +
                    $"NoDeliveryManifest, got {analysis.Status} (detail={analysis.RejectDetail ?? "<none>"})");
                InGameAssert.IsTrue(
                    FindFrom(captured, beforeAnalysis, l =>
                        l.Contains("skipped 1 empty grapple window(s) as non-stops")),
                    "the analysis must log the empty-grapple structural-grab skip");

                ParsekLog.Info("TestRunner",
                    $"GrappleCapture PASS: run={runId} window={window.WindowId} kind={window.TransferKind} " +
                    $"targetPid={roidVesselPid.ToString(IC)} complete={window.IsComplete} " +
                    $"roidGhostRenderers={roidRendererCount.ToString(IC)} analysis={analysis.Status} " +
                    $"dockUT={window.DockUT.ToString("F2", IC)} undockUT={window.UndockUT.ToString("F2", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                if (ghostBuild?.root != null)
                    UnityEngine.Object.Destroy(ghostBuild.root);
                CleanupRecordingAndTree(flight);
                CleanupSpawnedVessels(activeVessel, clawPart, roidPart, clawVessel, roidVessel);
            }
        }

        // ==================================================================
        // Spawn helpers
        // ==================================================================

        /// <summary>
        /// Spawns a single-part vessel near <paramref name="anchor"/> through
        /// the production spawn path (VesselSpawner.SpawnAtPosition), offset
        /// along the local parallel by <paramref name="offsetMeters"/> at the
        /// anchor's altitude + 1 m, co-moving with the anchor. Returns the
        /// spawned vessel pid, 0 on failure.
        /// </summary>
        private static uint SpawnSinglePartVessel(
            string partName, string vesselName, VesselType vtype, Vessel anchor, double offsetMeters)
        {
            CelestialBody body = anchor.mainBody;
            double lat = anchor.latitude;
            double alt = anchor.altitude + 1.0;
            double cosLat = Math.Cos(lat * Math.PI / 180.0);
            double lonOffsetDeg = offsetMeters / ((body.Radius + alt) * cosLat) * (180.0 / Math.PI);
            double lon = anchor.longitude + lonOffsetDeg;

            uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            ConfigNode partNode = ProtoVessel.CreatePartNode(partName, flightId);
            // Placeholder orbit for CreateVesselNode; SpawnAtPosition rebuilds
            // the ORBIT node from position + velocity.
            Orbit orbit = new Orbit(
                anchor.orbit.inclination, anchor.orbit.eccentricity, anchor.orbit.semiMajorAxis,
                anchor.orbit.LAN, anchor.orbit.argumentOfPeriapsis, anchor.orbit.meanAnomalyAtEpoch,
                anchor.orbit.epoch, body);
            // Pin the smallest untracked object class for space objects: the
            // PotatoRoid's procedural mesh scales with the discovery class, and
            // a class-A rock (~4 m) cannot spawn overlapping the fixture. The
            // mesh stays procedural (ModuleAsteroid seed), which is the point
            // of the ghost-geometry assertion.
            ConfigNode vesselNode;
            if (vtype == VesselType.SpaceObject)
            {
                ConfigNode discovery = ProtoVessel.CreateDiscoveryNode(
                    DiscoveryLevels.Owned, UntrackedObjectClass.A,
                    double.PositiveInfinity, double.PositiveInfinity);
                vesselNode = ProtoVessel.CreateVesselNode(
                    vesselName, vtype, orbit, 0, new[] { partNode }, discovery);
            }
            else
            {
                vesselNode = ProtoVessel.CreateVesselNode(
                    vesselName, vtype, orbit, 0, new[] { partNode });
            }

            // Surface anchors classify FLYING at low speed; the terminal-state
            // override maps them back to LANDED/SPLASHED (the #176/#264 seam).
            TerminalState? terminal = null;
            if (anchor.Landed) terminal = TerminalState.Landed;
            else if (anchor.Splashed) terminal = TerminalState.Splashed;

            uint pid = VesselSpawner.SpawnAtPosition(
                vesselNode, body, lat, lon, alt,
                anchor.obt_velocity, Planetarium.GetUniversalTime(),
                terminalState: terminal);
            ParsekLog.Info("TestRunner",
                $"GrappleCapture spawn: part={partName} vessel='{vesselName}' pid={pid.ToString(IC)} " +
                $"offset={offsetMeters.ToString("F0", IC)}m lat={lat.ToString("F4", IC)} " +
                $"lon={lon.ToString("F4", IC)} alt={alt.ToString("F1", IC)} terminal={terminal?.ToString() ?? "<none>"}");
            return pid;
        }

        /// <summary>
        /// Waits (bounded) for the spawned vessel to be loaded with live parts,
        /// hardens its part physics as soon as parts exist (prevents joint
        /// break / overheat / crash destruction while the programmatic couple
        /// holds a long lever arm), then waits for unpack. Reports the live
        /// vessel via <paramref name="onLive"/>; leaves it null on timeout or
        /// mid-wait destruction (caller skips loudly).
        /// </summary>
        private static IEnumerator WaitForSpawnedVesselLive(uint pid, string label, Action<Vessel> onLive)
        {
            float deadline = Time.realtimeSinceStartup + SpawnLoadTimeoutSeconds;
            bool hardened = false;
            int waitedFrames = 0;
            while (Time.realtimeSinceStartup < deadline)
            {
                Vessel v = FlightRecorder.FindVesselByPid(pid);
                if (v != null && v.loaded && v.parts != null && v.parts.Count > 0)
                {
                    if (!hardened)
                    {
                        GhostMapPresence.HardenGhostVesselPartPhysics(v, $"GrappleCapture {label} fixture");
                        hardened = true;
                    }
                    if (!v.packed)
                    {
                        ParsekLog.Verbose("TestRunner",
                            $"GrappleCapture {label} live after {waitedFrames.ToString(IC)} frame(s): " +
                            $"pid={pid.ToString(IC)} parts={v.parts.Count.ToString(IC)}");
                        onLive(v);
                        yield break;
                    }
                }
                waitedFrames++;
                yield return null;
            }
            ParsekLog.Warn("TestRunner",
                $"GrappleCapture {label} wait timed out after {SpawnLoadTimeoutSeconds.ToString("R", IC)}s " +
                $"(pid={pid.ToString(IC)}, waitedFrames={waitedFrames.ToString(IC)})");
        }

        // ==================================================================
        // Assertion helpers
        // ==================================================================

        private static Part PrefabPart(string name)
        {
            AvailablePart info = PartLoader.getPartInfoByName(name);
            return info != null ? info.partPrefab : null;
        }

        private static bool IsProducerClassifiedLine(string line)
        {
            return line.Contains("[Flight]") && line.Contains("OnPartCouple producer classified");
        }

        private static bool IsWindowCapturedLine(string line)
        {
            return line.Contains("[Flight]") && line.Contains("Route proof dock window captured");
        }

        private static bool IsWindowCompletedLine(string line)
        {
            return line.Contains("[Flight]") && line.Contains("Route proof dock window completed on undock");
        }

        /// <summary>Predicate scan over the log lines captured from index <paramref name="fromIndex"/> on.</summary>
        private static bool FindFrom(List<string> captured, int fromIndex, Predicate<string> predicate)
        {
            for (int i = fromIndex; i < captured.Count; i++)
            {
                if (predicate(captured[i]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Counts renderers under the ghost part node named by the part's
        /// persistentId (AddPartVisuals names part roots "ghost_part_{pid}").
        /// Returns 0 when the part contributed no node or no renderers.
        /// </summary>
        internal static int CountRenderersUnderGhostPart(GameObject ghostRoot, uint partPid)
        {
            if (ghostRoot == null)
                return 0;
            string wanted = "ghost_part_" + partPid.ToString(IC);
            Transform[] all = ghostRoot.GetComponentsInChildren<Transform>(true);
            int scanned = 0;
            for (int i = 0; i < all.Length; i++)
            {
                scanned++;
                if (all[i] != null && all[i].name == wanted)
                {
                    int renderers = all[i].GetComponentsInChildren<Renderer>(true).Length;
                    ParsekLog.Verbose("TestRunner",
                        $"GrappleCapture ghost scan: node='{wanted}' found renderers={renderers.ToString(IC)} " +
                        $"(scanned {scanned.ToString(IC)} transform(s))");
                    return renderers;
                }
            }
            ParsekLog.Verbose("TestRunner",
                $"GrappleCapture ghost scan: node='{wanted}' NOT found " +
                $"(scanned {scanned.ToString(IC)} transform(s))");
            return 0;
        }

        // ==================================================================
        // Cleanup helpers (finally; the isolated-batch baseline quickload is
        // the outer net for anything best-effort here)
        // ==================================================================

        private static void CleanupRecordingAndTree(ParsekFlight flight)
        {
            try
            {
                if (flight == null) return;
                if (flight.IsRecording)
                    flight.StopRecording();
                if (flight.ActiveTreeForSerialization != null)
                    flight.AutoDiscardIdleActiveTree("GrappleCapture test cleanup");
                ParsekLog.Verbose("TestRunner", "GrappleCapture cleanup: recording stopped + test tree discarded");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestRunner",
                    $"GrappleCapture cleanup: recording/tree teardown failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// Best-effort teardown of the spawned fixture vessels. The asteroid is
        /// destroyed whether it released (own vessel) or is still coupled
        /// (undocked first); the claw part is undocked off the active vessel
        /// and destroyed. Every step is individually guarded - the baseline
        /// quickload restores the active vessel regardless.
        /// </summary>
        private static void CleanupSpawnedVessels(
            Vessel activeVessel, Part clawPart, Part roidPart, Vessel clawVessel, Vessel roidVessel)
        {
            int destroyed = 0, detached = 0, failures = 0;

            // Asteroid: detach from the active vessel first if still coupled.
            try
            {
                if (roidPart != null && roidPart.vessel != null)
                {
                    if (activeVessel != null && roidPart.vessel == activeVessel)
                    {
                        roidPart.Undock(new DockedVesselInfo
                        {
                            name = "GrappleCapture cleanup roid",
                            vesselType = VesselType.SpaceObject,
                            rootPartUId = roidPart.flightID
                        });
                        detached++;
                    }
                    if (roidPart.vessel != null && roidPart.vessel != activeVessel)
                    {
                        roidPart.vessel.Die();
                        destroyed++;
                    }
                }
                else if (roidVessel != null && roidVessel.state != Vessel.State.DEAD)
                {
                    roidVessel.Die();
                    destroyed++;
                }
            }
            catch (Exception ex)
            {
                failures++;
                ParsekLog.Warn("TestRunner",
                    $"GrappleCapture cleanup: asteroid teardown failed ({ex.GetType().Name}: {ex.Message})");
            }

            // Claw: undock off the active vessel, then destroy its split vessel.
            try
            {
                if (clawPart != null && clawPart.vessel != null)
                {
                    if (activeVessel != null && clawPart.vessel == activeVessel)
                    {
                        clawPart.Undock(new DockedVesselInfo
                        {
                            name = "GrappleCapture cleanup claw",
                            vesselType = VesselType.Probe,
                            rootPartUId = clawPart.flightID
                        });
                        detached++;
                    }
                    if (clawPart.vessel != null && clawPart.vessel != activeVessel)
                    {
                        clawPart.vessel.Die();
                        destroyed++;
                    }
                }
                else if (clawVessel != null && clawVessel.state != Vessel.State.DEAD)
                {
                    clawVessel.Die();
                    destroyed++;
                }
            }
            catch (Exception ex)
            {
                failures++;
                ParsekLog.Warn("TestRunner",
                    $"GrappleCapture cleanup: claw teardown failed ({ex.GetType().Name}: {ex.Message})");
            }

            ParsekLog.Verbose("TestRunner",
                $"GrappleCapture cleanup: detached={detached.ToString(IC)} destroyed={destroyed.ToString(IC)} " +
                $"failures={failures.ToString(IC)} (baseline quickload restores the active vessel)");
        }

        /// <summary>
        /// Yields until <paramref name="condition"/> is true or the bounded
        /// wait times out (the caller's next assert reports the failure with
        /// its own message; the timeout itself is breadcrumbed).
        /// </summary>
        private static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (condition())
                    yield break;
                yield return null;
            }
            ParsekLog.Verbose("TestRunner",
                $"GrappleCapture wait timed out after {timeoutSeconds.ToString("R", IC)}s: {what}");
        }
    }
}
