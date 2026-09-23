using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Operator ruling 2026-09-23: a recording whose vessel ENDS its flight parked in the
    /// KSC exclusion zone (the 50 m pad-centre and runway-threshold circles, home world,
    /// non-EVA) has ended its flight and is RETIRED - it never becomes a real vessel in any
    /// scene, the ghost is not held for spawn retries, and the kerbals aboard are freed at
    /// the recording's EndUT as if recovered, with no ledger row. Only the FINAL stop of the
    /// whole flight counts.
    /// </summary>
    [Collection("Sequential")]
    public class KscPadRetirementTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private string tempSaveRoot;

        private const double KerbinRadius = 600000.0;
        private const string Jeb = "Jebediah Kerman";

        private static double MetersToDegrees(double meters)
            => meters / (KerbinRadius * Math.PI / 180.0);

        // Mid-runway: 1 km east of the west threshold, well outside both circles.
        private static readonly double MidRunwayLat = SpawnCollisionDetector.KscRunwayLatitude;
        private static readonly double MidRunwayLon =
            SpawnCollisionDetector.KscRunwayLongitude + MetersToDegrees(1000.0);

        public KscPadRetirementTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();

            var kerbin = TestBodyRegistry.CreateBody("Kerbin", KerbinRadius, 3.5316e12);
            SetHomeWorld(kerbin, true);
            var mun = TestBodyRegistry.CreateBody("Mun", 200000.0, 6.5138398e10);
            SetHomeWorld(mun, false);
            VesselSpawner.BodyResolverForTesting = (string name, out CelestialBody body) =>
            {
                body = name == "Kerbin" ? kerbin : name == "Mun" ? mun : null;
                return !ReferenceEquals(body, null);
            };
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => false);
        }

        public void Dispose()
        {
            VesselSpawner.BodyResolverForTesting = null;
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            RecordingPaths.SaveRootOverrideForTesting = null;
            if (tempSaveRoot != null && System.IO.Directory.Exists(tempSaveRoot))
            {
                try { System.IO.Directory.Delete(tempSaveRoot, true); } catch (System.IO.IOException) { }
            }
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GhostPlaybackLogic.ResetForTesting();
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.LiveClockUTProviderForTesting = null;
            KerbalsModule.LoadedSaveUTProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void SetHomeWorld(CelestialBody body, bool isHome)
        {
            FieldInfo field = typeof(CelestialBody).GetField("isHomeWorld");
            Assert.NotNull(field);
            field.SetValue(body, isHome);
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static ConfigNode Snapshot(double lat, double lon, string sit, params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", sit);
            snapshot.AddValue("lat", lat.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            snapshot.AddValue("lon", lon.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            snapshot.AddValue("alt", "70");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        private static TrajectoryPoint Point(double ut, double lat, double lon, string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = 70.0,
                bodyName = body,
            };
        }

        /// <summary>
        /// A landed recording that starts at (startLat, startLon) and ends parked at
        /// (endLat, endLon); the vessel snapshot sits at the end position, as a real
        /// end-of-flight snapshot does.
        /// </summary>
        private static Recording MakeParked(
            string id,
            double endLat,
            double endLon,
            string body = "Kerbin",
            double startLat = double.NaN,
            double startLon = double.NaN,
            TerminalState? terminal = TerminalState.Landed,
            double startUT = 100.0,
            double endUT = 200.0,
            params string[] crew)
        {
            if (double.IsNaN(startLat)) startLat = endLat;
            if (double.IsNaN(startLon)) startLon = endLon;
            var snapshot = Snapshot(endLat, endLon, "LANDED", crew);
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Pad Rig",
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                TerminalStateValue = terminal,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                CrewEndStatesResolved = true,
            };
            rec.Points.Add(Point(startUT, startLat, startLon, body));
            rec.Points.Add(Point(endUT, endLat, endLon, body));
            return rec;
        }

        /// <summary>
        /// A landed recording whose vessel snapshot sits at (snapLat, snapLon) while its
        /// trajectory ENDS at (endLat, endLon): the stale start-of-flight snapshot shape.
        /// </summary>
        private static Recording MakeStaleSnapshot(
            string id, double snapLat, double snapLon, double endLat, double endLon)
        {
            var rec = MakeParked(id, endLat, endLon);
            rec.VesselSnapshot = Snapshot(snapLat, snapLon, "LANDED");
            rec.GhostVisualSnapshot = rec.VesselSnapshot;
            return rec;
        }

        private static readonly double OffPadLat =
            SpawnCollisionDetector.KscPadLatitude + MetersToDegrees(400.0);

        private static Recording MakeOnPad(string id, params string[] crew)
            => MakeParked(id,
                SpawnCollisionDetector.KscPadLatitude,
                SpawnCollisionDetector.KscPadLongitude,
                crew: crew);

        private static int IndexOf(Recording rec)
        {
            var committed = RecordingStore.CommittedRecordings;
            for (int k = 0; k < committed.Count; k++)
                if (ReferenceEquals(committed[k], rec)) return k;
            return -1;
        }

        // ------------------------------------------------------------------
        // THE predicate (pure)
        // ------------------------------------------------------------------

        [Fact]
        public void Predicate_LandedAtPadCentre_RetiresAsPad()
        {
            Assert.Equal(KscExclusionZone.Pad,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, isEva: false, bodyIsHomeWorld: true,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius));
        }

        [Fact]
        public void Predicate_LandedAtRunwayThreshold_RetiresAsRunway()
        {
            Assert.Equal(KscExclusionZone.Runway,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, false, true,
                    SpawnCollisionDetector.KscRunwayLatitude,
                    SpawnCollisionDetector.KscRunwayLongitude + MetersToDegrees(20.0),
                    KerbinRadius));
        }

        [Fact]
        public void Predicate_PlaneStoppedMidRunway_IsNotRetired()
        {
            Assert.Equal(KscExclusionZone.None,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, false, true, MidRunwayLat, MidRunwayLon, KerbinRadius));
        }

        [Fact]
        public void Predicate_JustOutsideThePadCircle_IsNotRetired()
        {
            Assert.Equal(KscExclusionZone.None,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, false, true,
                    SpawnCollisionDetector.KscPadLatitude + MetersToDegrees(55.0),
                    SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius));
        }

        [Fact]
        public void Predicate_OffTheHomeWorld_IsNotRetired()
        {
            Assert.Equal(KscExclusionZone.None,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, false, bodyIsHomeWorld: false,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius));
        }

        [Fact]
        public void Predicate_EvaKerbalOnThePad_IsNotRetired()
        {
            Assert.Equal(KscExclusionZone.None,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, isEva: true, bodyIsHomeWorld: true,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius));
        }

        [Theory]
        [InlineData(TerminalState.Orbiting)]
        [InlineData(TerminalState.SubOrbital)]
        [InlineData(TerminalState.Destroyed)]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void Predicate_NonParkedTerminalOverThePad_IsNotRetired(TerminalState terminal)
        {
            Assert.Equal(KscExclusionZone.None,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    terminal, false, true,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius));
        }

        [Fact]
        public void Predicate_NoTerminalEvidence_IsNotRetired()
        {
            Assert.Equal(KscExclusionZone.None,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    null, false, true,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius));
        }

        [Fact]
        public void Predicate_Splashed_UsesTheOneSurfaceTerminalDefinition()
        {
            Assert.Equal(KscExclusionZone.Pad,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Splashed, false, true,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius));
        }

        // ------------------------------------------------------------------
        // Recording-level evaluation (position = where the spawn would place it)
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluate_ParkedOnThePad_Retires()
        {
            var decision = VesselSpawner.EvaluateKscEndOfFlightRetirement(MakeOnPad("rec-pad"));
            Assert.True(decision.Retire);
            Assert.Equal(KscExclusionZone.Pad, decision.Zone);
            Assert.Equal("Kerbin", decision.BodyName);
        }

        [Fact]
        public void Evaluate_SatOnThePadThenDroveOff_IsNotRetired()
        {
            // Only the final stop counts: the flight starts on the pad and ends 400 m away.
            var rec = MakeParked("rec-drove-off",
                SpawnCollisionDetector.KscPadLatitude + MetersToDegrees(400.0),
                SpawnCollisionDetector.KscPadLongitude,
                startLat: SpawnCollisionDetector.KscPadLatitude,
                startLon: SpawnCollisionDetector.KscPadLongitude);
            Assert.False(VesselSpawner.EvaluateKscEndOfFlightRetirement(rec).Retire);
        }

        [Fact]
        public void Evaluate_PadCoordinatesOnTheMun_IsNotRetired()
        {
            var rec = MakeParked("rec-mun",
                SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                body: "Mun");
            Assert.False(VesselSpawner.EvaluateKscEndOfFlightRetirement(rec).Retire);
        }

        [Fact]
        public void Evaluate_UnfinalizedPrelaunchSnapshotOnThePad_Retires()
        {
            var rec = MakeOnPad("rec-unfinalized");
            rec.TerminalStateValue = null;
            rec.VesselSnapshot.SetValue("sit", "PRELAUNCH");
            var decision = VesselSpawner.EvaluateKscEndOfFlightRetirement(rec);
            Assert.True(decision.Retire);
            Assert.Equal(TerminalState.Landed, decision.EffectiveTerminal);
        }

        [Fact]
        public void Evaluate_NoBodyRegistry_IsNotRetired()
        {
            VesselSpawner.BodyResolverForTesting = (string name, out CelestialBody body) =>
            {
                body = null;
                return false;
            };
            Assert.False(VesselSpawner.EvaluateKscEndOfFlightRetirement(MakeOnPad("rec-no-registry")).Retire);
        }

        // ------------------------------------------------------------------
        // Final-segment notion
        // ------------------------------------------------------------------

        [Fact]
        public void FinalSegment_StandaloneRecording_IsFinal()
        {
            var rec = MakeOnPad("rec-standalone");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.True(GhostPlaybackLogic.IsFinalSpawnSegment(rec));
        }

        [Fact]
        public void FinalSegment_IntermediateChainLinkOnThePad_IsNotFinal_TheTipIs()
        {
            var head = MakeOnPad("rec-chain-head");
            head.ChainId = "chain-1";
            head.ChainIndex = 0;
            RecordingStore.AddRecordingWithTreeForTesting(head);
            var tip = MakeParked("rec-chain-tip", MidRunwayLat, MidRunwayLon, startUT: 200.0, endUT: 300.0);
            tip.ChainId = "chain-1";
            tip.ChainIndex = 1;
            tip.TreeId = head.TreeId;
            RecordingStore.AddRecordingWithTreeForTesting(tip);

            Assert.False(GhostPlaybackLogic.IsFinalSpawnSegment(head));
            Assert.True(GhostPlaybackLogic.IsFinalSpawnSegment(tip));
            Assert.False(VesselSpawner.IsKscRetiredFinalFlight(head));
        }

        [Fact]
        public void FinalSegment_NonLeafTreeRecordingOnThePad_IsNotFinal()
        {
            // The vessel ended this segment on the pad, then a same-vessel continuation
            // (a switch-continuation / chain segment) carried the flight on.
            var tree = new RecordingTree { Id = "tree-continued", TreeName = "Continued" };
            var parent = MakeOnPad("rec-parent");
            parent.VesselPersistentId = 4242u;
            parent.TreeId = tree.Id;
            var child = MakeParked("rec-child", MidRunwayLat, MidRunwayLon, startUT: 200.0, endUT: 300.0);
            child.VesselPersistentId = 4242u;
            child.TreeId = tree.Id;
            var bp = new BranchPoint { Id = "bp-continue" };
            bp.ParentRecordingIds.Add(parent.RecordingId);
            bp.ChildRecordingIds.Add(child.RecordingId);
            tree.BranchPoints.Add(bp);
            parent.ChildBranchPointId = bp.Id;
            tree.AddOrReplaceRecording(parent);
            tree.AddOrReplaceRecording(child);
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(parent);
            RecordingStore.AddRecordingWithTreeForTesting(child);

            Assert.False(GhostPlaybackLogic.IsFinalSpawnSegment(parent));
            Assert.False(VesselSpawner.IsKscRetiredFinalFlight(parent));
        }

        [Fact]
        public void FinalSegment_TerminalSpawnOwnedByALaterContinuation_IsNotFinal()
        {
            var rec = MakeOnPad("rec-superseded-spawn");
            rec.TerminalSpawnSupersededByRecordingId = "rec-later";
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.False(GhostPlaybackLogic.IsFinalSpawnSegment(rec));
        }

        [Fact]
        public void FinalSegment_DebrisAndGhostOnly_AreNotFinal()
        {
            var debris = MakeOnPad("rec-debris");
            debris.IsDebris = true;
            var gloops = MakeOnPad("rec-gloops");
            gloops.IsGhostOnly = true;
            Assert.False(GhostPlaybackLogic.IsFinalSpawnSegment(debris));
            Assert.False(GhostPlaybackLogic.IsFinalSpawnSegment(gloops));
        }

        // ------------------------------------------------------------------
        // Spawn entry points: retire, log once, never spawn, never hold
        // ------------------------------------------------------------------

        [Fact]
        public void TryRetire_ParkedOnThePad_SettlesWithNoVessel_AndLogsOnce()
        {
            var rec = MakeOnPad("rec-retire");

            Assert.True(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 3));
            Assert.True(rec.VesselSpawned);
            Assert.True(rec.SpawnAbandoned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.True(VesselSpawner.IsSettledAsKscRetirement(rec));

            // Idempotent: a settled recording is not retired (or logged) again.
            Assert.False(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 3));
            var lines = logLines.FindAll(l => l.Contains("[Spawner]")
                && l.Contains("Spawn RETIRED for #3 (Pad Rig): flight ended within KSC exclusion zone (pad) - no vessel")
                && l.Contains("lat=-0.0972") && l.Contains("lon=-74.5575")
                && l.Contains("body=Kerbin") && l.Contains("terminal=Landed")
                && l.Contains("rec=rec-retire"));
            Assert.Single(lines);
            Assert.Contains("[INFO]", lines[0].ToUpperInvariant());
        }

        [Fact]
        public void TryRetire_PlaneMidRunway_LeavesTheRecordingUntouched()
        {
            var rec = MakeParked("rec-mid-runway", MidRunwayLat, MidRunwayLon);
            Assert.False(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 1));
            Assert.False(rec.VesselSpawned);
            Assert.False(rec.SpawnAbandoned);
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn RETIRED"));
        }

        [Fact]
        public void TryRetire_RequireNoMaterializedSource_LeavesARealCounterpartToAdoption()
        {
            var rec = MakeOnPad("rec-real-counterpart");
            rec.VesselPersistentId = 777u;
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == 777u);

            Assert.False(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 0, requireNoMaterializedSource: true));
            Assert.False(rec.VesselSpawned);
        }

        [Fact]
        public void SpawnOrRecover_ParkedOnThePad_RetiresInsteadOfSpawning()
        {
            // The shared flight / Tracking Station / tree-leaf spawn helper. The retirement
            // returns before any KSP call, so this runs headless end to end.
            var rec = MakeOnPad("rec-shared-helper");

            VesselSpawner.SpawnOrRecoverIfTooClose(rec, 5);

            Assert.True(rec.VesselSpawned);
            Assert.True(rec.SpawnAbandoned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(0, rec.SpawnAttempts);
            Assert.Contains(logLines, l => l.Contains("Spawn RETIRED for #5 (Pad Rig)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn blocked for #5"));
        }

        [Fact]
        public void SpawnOrRecover_RealCounterpartStillOnThePad_IsAdoptedNotRetired()
        {
            var rec = MakeOnPad("rec-adopt");
            rec.VesselPersistentId = 9001u;
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == 9001u);

            VesselSpawner.SpawnOrRecoverIfTooClose(rec, 2);

            Assert.True(rec.VesselSpawned);
            Assert.False(rec.SpawnAbandoned);
            Assert.Equal(9001u, rec.SpawnedVesselPersistentId);
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn RETIRED"));
        }

        [Fact]
        public void ScenePredicates_AfterRetirement_NeverSpawnAgain()
        {
            var rec = MakeOnPad("rec-scene-predicates");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.True(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 0));

            var flight = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false, false);
            var ksc = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec, 1000.0);
            Assert.False(flight.needsSpawn);
            Assert.False(ksc.needsSpawn);
            Assert.Contains("already spawned", flight.reason);
            Assert.Contains("already spawned", ksc.reason);
        }

        private static ParsekFlight MakeHost()
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            typeof(ParsekFlight).GetField("watchMode", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, new WatchModeController(host));
            return host;
        }

        private static TrajectoryPlaybackFlags MakeFlags(Recording rec)
        {
            return new TrajectoryPlaybackFlags
            {
                recordingId = rec.RecordingId,
                needsSpawn = true,
                chainEndUT = rec.EndUT,
            };
        }

        [Fact]
        public void Policy_CompletionOfAPadFlight_RetiresAndDoesNotHoldTheGhost()
        {
            var rec = MakeOnPad("rec-policy");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = IndexOf(rec);
            var engine = new GhostPlaybackEngine(null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            var policy = new ParsekPlaybackPolicy(engine, MakeHost())
            {
                IsWarpActiveOverrideForTesting = () => false,
                CurrentRealTimeOverrideForTesting = () => 30f,
                // The real non-chain spawn route the host would take.
                SpawnVesselOrChainTipOverrideForTesting = (r, i) => VesselSpawner.SpawnOrRecoverIfTooClose(r, i),
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };

            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), rec.EndUT + 1.0, queueCompletion: true);

            Assert.True(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(policy.heldGhosts.ContainsKey(index));
            Assert.False(engine.HasGhost(index));
            Assert.Contains(logLines, l => l.Contains("Spawn RETIRED for #" + index + " (Pad Rig)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Ghost held pending spawn retry"));
        }

        [Fact]
        public void Policy_CompletionOfAPadFlightDuringWarp_RetiresWithoutDeferringOrHolding()
        {
            var rec = MakeOnPad("rec-policy-warp");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = IndexOf(rec);
            int spawnCalls = 0;
            var engine = new GhostPlaybackEngine(null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            var policy = new ParsekPlaybackPolicy(engine, MakeHost())
            {
                IsWarpActiveOverrideForTesting = () => true,
                CurrentRealTimeOverrideForTesting = () => 30f,
                SpawnVesselOrChainTipOverrideForTesting = (r, i) => spawnCalls++,
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };

            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), rec.EndUT + 1.0, queueCompletion: true);

            Assert.Equal(0, spawnCalls);
            Assert.True(rec.VesselSpawned);
            Assert.Empty(policy.pendingSpawnRecordingIds);
            Assert.False(policy.heldGhosts.ContainsKey(index));
            Assert.False(engine.HasGhost(index));
            Assert.Contains(logLines, l => l.Contains("Spawn RETIRED for #" + index + " (Pad Rig)"));
            Assert.DoesNotContain(logLines, l => l.Contains("Ghost held during warp-deferred spawn"));
            Assert.DoesNotContain(logLines, l => l.Contains("Deferred spawn during warp"));
        }

        [Fact]
        public void Policy_WarpCompletionOfAPadChainTip_LeavesItToTheChainPath()
        {
            // A ghost-chain tip's retirement belongs to the chain path (it also closes the
            // chain), so the warp shortcut must not settle it behind the chain's back.
            var rec = MakeOnPad("rec-policy-chain-tip");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = IndexOf(rec);
            var host = MakeHost();
            typeof(ParsekFlight).GetField("activeGhostChains", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, new Dictionary<uint, GhostChain>
                {
                    { 555u, new GhostChain { OriginalVesselPid = 555u, TipRecordingId = rec.RecordingId } },
                });
            var engine = new GhostPlaybackEngine(null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            var policy = new ParsekPlaybackPolicy(engine, host)
            {
                IsWarpActiveOverrideForTesting = () => true,
                CurrentRealTimeOverrideForTesting = () => 30f,
                SpawnVesselOrChainTipOverrideForTesting = (r, i) => { },
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };

            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), rec.EndUT + 1.0, queueCompletion: true);

            Assert.False(rec.VesselSpawned);
            Assert.Contains(rec.RecordingId, policy.pendingSpawnRecordingIds);
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn RETIRED"));
        }

        [Fact]
        public void Policy_CompletionOfAMidRunwayFlight_StillTakesTheSpawnRoute()
        {
            var rec = MakeParked("rec-policy-runway", MidRunwayLat, MidRunwayLon);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            int index = IndexOf(rec);
            int spawnCalls = 0;
            var engine = new GhostPlaybackEngine(null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            var policy = new ParsekPlaybackPolicy(engine, MakeHost())
            {
                IsWarpActiveOverrideForTesting = () => true,
                CurrentRealTimeOverrideForTesting = () => 30f,
                SpawnVesselOrChainTipOverrideForTesting = (r, i) => spawnCalls++,
            };
            engine.ghostStates[index] = new GhostPlaybackState { vesselName = rec.VesselName };

            engine.RunPastEndFrameTailForTesting(index, rec, MakeFlags(rec), rec.EndUT + 1.0, queueCompletion: true);

            Assert.False(rec.VesselSpawned);
            Assert.Contains(rec.RecordingId, policy.pendingSpawnRecordingIds);
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn RETIRED"));
        }

        // ------------------------------------------------------------------
        // Crew: freed at EndUT as if recovered, no ledger row
        // ------------------------------------------------------------------

        private static GameAction Assignment(string recordingId, string kerbal, double startUT,
            double endUT, KerbalEndState endState, int sequence, string role = "Pilot")
        {
            return new GameAction
            {
                UT = startUT,
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbal,
                KerbalRole = role,
                StartUT = (float)startUT,
                EndUT = (float)endUT,
                KerbalEndStateField = endState,
                Sequence = sequence,
            };
        }

        private static KerbalsModule Kerbals => LedgerOrchestrator.Kerbals;

        [Fact]
        public void Crew_AboardAPadRetiredFlight_IsFreedAtEndUT_WithNoLedgerRow()
        {
            var rec = MakeOnPad("rec-crew-pad", Jeb);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(Assignment(rec.RecordingId, Jeb, 100.0, 200.0, KerbalEndState.Aboard, 1));
            int rowsBefore = Ledger.Actions.Count;

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(150.0, "ksc-retire-test");
            Assert.Equal(200.0, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.True(Kerbals.IsReservedNow(Jeb));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(250.0, "ksc-retire-test");
            Assert.False(Kerbals.IsReservedNow(Jeb));
            Assert.True(Kerbals.IsKerbalAvailable(Jeb));

            Assert.Equal(rowsBefore, Ledger.Actions.Count);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by KSC retirement: 'Jebediah Kerman' recording 'rec-crew-pad'"));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("1 KSC-retired recording(s) free 1 aboard crew hold(s)"));
        }

        [Fact]
        public void Crew_AboardAMidRunwayFlight_StaysReservedOpenEnded()
        {
            var rec = MakeParked("rec-crew-runway", MidRunwayLat, MidRunwayLon, crew: new[] { Jeb });
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(Assignment(rec.RecordingId, Jeb, 100.0, 200.0, KerbalEndState.Aboard, 1));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(250.0, "ksc-retire-test");
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(Kerbals.IsReservedNow(Jeb));
        }

        [Fact]
        public void Crew_PadFlightAlreadyMaterializedAsARealVessel_StaysReserved()
        {
            // Mirror direction: a pad vessel that DID become real (spawned before this
            // ruling, or adopted) carries the kerbal, so he must not be freed.
            var rec = MakeOnPad("rec-crew-materialized", Jeb);
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = 31337u;
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(Assignment(rec.RecordingId, Jeb, 100.0, 200.0, KerbalEndState.Aboard, 1));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(250.0, "ksc-retire-test");
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
        }

        [Fact]
        public void Crew_RealCounterpartStillSitsOnThePad_StaysReserved()
        {
            // Mirror direction: the recorded launch still exists in the live save (the
            // spawn path would adopt it, not retire it) - the kerbal is aboard a real vessel.
            var rec = MakeOnPad("rec-crew-live-counterpart", Jeb);
            rec.VesselPersistentId = 5150u;
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == 5150u);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(Assignment(rec.RecordingId, Jeb, 100.0, 200.0, KerbalEndState.Aboard, 1));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(250.0, "ksc-retire-test");
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
        }

        [Fact]
        public void Crew_EarlierSegmentOfTheSameFlight_IsClosedAtTheRetirement()
        {
            // HEAD (open-ended Unknown hold, ends mid-flight) + TIP retired on the pad, one
            // tree: both holds end at the TIP's EndUT, exactly like a recovery would.
            var head = MakeParked("rec-head", MidRunwayLat, MidRunwayLon,
                terminal: TerminalState.SubOrbital, startUT: 100.0, endUT: 150.0, crew: new[] { Jeb });
            RecordingStore.AddRecordingWithTreeForTesting(head);
            var tip = MakeParked("rec-tip",
                SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                startUT: 150.0, endUT: 200.0, crew: new[] { Jeb });
            tip.TreeId = head.TreeId;
            RecordingStore.AddRecordingWithTreeForTesting(tip);
            Ledger.AddAction(Assignment(head.RecordingId, Jeb, 100.0, 150.0, KerbalEndState.Unknown, 1));
            Ledger.AddAction(Assignment(tip.RecordingId, Jeb, 150.0, 200.0, KerbalEndState.Aboard, 2));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(250.0, "ksc-retire-test");
            Assert.Equal(200.0, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(Kerbals.IsReservedNow(Jeb));
        }

        [Fact]
        public void Crew_ALaterFlightInAnotherMission_KeepsItsOwnHold()
        {
            var pad = MakeOnPad("rec-pad-first", Jeb);
            RecordingStore.AddRecordingWithTreeForTesting(pad);
            var later = MakeParked("rec-later-mission", MidRunwayLat, MidRunwayLon,
                startUT: 300.0, endUT: 400.0, crew: new[] { Jeb });
            RecordingStore.AddRecordingWithTreeForTesting(later);
            Ledger.AddAction(Assignment(pad.RecordingId, Jeb, 100.0, 200.0, KerbalEndState.Aboard, 1));
            Ledger.AddAction(Assignment(later.RecordingId, Jeb, 300.0, 400.0, KerbalEndState.Aboard, 2));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(450.0, "ksc-retire-test");
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
        }

        // ------------------------------------------------------------------
        // Review fix-ups: stale snapshot, shared hydration, silent probe, chains
        // ------------------------------------------------------------------

        [Fact]
        public void Predicate_SnapshotOnPadButEndpointOffPad_IsNotRetired()
        {
            Assert.Equal(KscExclusionZone.None,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, false, true,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius,
                    positionIsSnapshot: true,
                    endpointLatitude: OffPadLat,
                    endpointLongitude: SpawnCollisionDetector.KscPadLongitude));
        }

        [Fact]
        public void Predicate_SnapshotAndEndpointBothOnPad_Retires()
        {
            Assert.Equal(KscExclusionZone.Pad,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, false, true,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius,
                    positionIsSnapshot: true,
                    endpointLatitude: SpawnCollisionDetector.KscPadLatitude + MetersToDegrees(10.0),
                    endpointLongitude: SpawnCollisionDetector.KscPadLongitude));
        }

        [Fact]
        public void Predicate_SnapshotOnPadWithNoKnownEndpoint_Retires()
        {
            Assert.Equal(KscExclusionZone.Pad,
                SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                    TerminalState.Landed, false, true,
                    SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    KerbinRadius, positionIsSnapshot: true));
        }

        [Fact]
        public void Evaluate_StaleSnapshotOnPadFlightEndedElsewhere_IsNotRetired_BothSides()
        {
            // "Parked for a while, then moved: no conflict." The snapshot was taken on the
            // pad, the trajectory ends 400 m away.
            var rec = MakeStaleSnapshot("rec-stale-snapshot",
                SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                OffPadLat, SpawnCollisionDetector.KscPadLongitude);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.False(VesselSpawner.EvaluateKscEndOfFlightRetirement(rec).Retire);
            Assert.False(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 0));
            Assert.False(VesselSpawner.IsKscRetiredFinalFlight(rec));
        }

        [Fact]
        public void Evaluate_SnapshotAndTrajectoryBothEndOnPad_Retires_BothSides()
        {
            var rec = MakeStaleSnapshot("rec-both-on-pad",
                SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                SpawnCollisionDetector.KscPadLatitude + MetersToDegrees(5.0),
                SpawnCollisionDetector.KscPadLongitude);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.True(VesselSpawner.IsKscRetiredFinalFlight(rec));
            Assert.True(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 0));
        }

        private void WriteVesselSidecar(string recordingId, ConfigNode snapshot)
        {
            tempSaveRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "parsek-ksc-retire-" + Guid.NewGuid().ToString("N"));
            RecordingPaths.SaveRootOverrideForTesting = tempSaveRoot;
            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildVesselSnapshotRelativePath(recordingId));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            RecordingStore.WriteSnapshotSidecarForTesting(path, snapshot);
        }

        [Fact]
        public void CrewSide_DroppedSnapshot_ReadsTheSidecarPositionTheSpawnWillUse()
        {
            // The disagreement the review found: the trajectory ends on the pad, but the
            // durable snapshot (the position the spawn uses after re-hydrating) sits 400 m
            // away. Without re-hydration the crew side read the endpoint and freed the crew
            // of a vessel that will spawn.
            var rec = MakeParked("rec-dropped-snapshot",
                SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                crew: new[] { Jeb });
            WriteVesselSidecar(rec.RecordingId,
                Snapshot(OffPadLat, SpawnCollisionDetector.KscPadLongitude, "LANDED", Jeb));
            rec.VesselSnapshot = null;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.False(VesselSpawner.IsKscRetiredFinalFlight(rec));
            Assert.NotNull(rec.VesselSnapshot); // re-hydrated, as the spawn gate would
        }

        [Fact]
        public void CrewSide_DroppedSnapshotOnThePad_StillRetires()
        {
            var rec = MakeOnPad("rec-dropped-pad-snapshot", Jeb);
            WriteVesselSidecar(rec.RecordingId,
                Snapshot(SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                    "LANDED", Jeb));
            rec.VesselSnapshot = null;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.True(VesselSpawner.IsKscRetiredFinalFlight(rec));
        }

        [Fact]
        public void CrewSide_EndpointOffPad_NeverTouchesTheSidecar()
        {
            var rec = MakeParked("rec-no-disk", MidRunwayLat, MidRunwayLon, crew: new[] { Jeb });
            rec.VesselSnapshot = null;
            // A save root with no sidecar: a hydration attempt would poison the cache flag.
            tempSaveRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "parsek-ksc-retire-" + Guid.NewGuid().ToString("N"));
            RecordingPaths.SaveRootOverrideForTesting = tempSaveRoot;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.False(VesselSpawner.IsKscRetiredFinalFlight(rec));
            Assert.False(rec.VesselSnapshotHydrationFailed);
        }

        [Fact]
        public void CrewSide_SettledSpawnSideRetirement_IsTakenAsIs()
        {
            var rec = MakeOnPad("rec-settled", Jeb);
            rec.VesselPersistentId = 4321u;
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.True(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 0));

            // Even a later same-pid real vessel does not undo what the spawn side settled.
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == 4321u);
            Assert.True(VesselSpawner.IsKscRetiredFinalFlight(rec));
        }

        [Fact]
        public void CrewSide_RelaunchOfTheSameCraft_IsSilentAndStillRetires()
        {
            // The live vessel shares the craft-baked pid but is a different launch: not a
            // real counterpart, and the crew predicate must not log the adoption rejection
            // on every ledger walk.
            var rec = MakeOnPad("rec-relaunched-craft", Jeb);
            rec.VesselPersistentId = 2468u;
            rec.RecordedVesselGuid = "11111111111111111111111111111111";
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == 2468u);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(
                pid => "22222222222222222222222222222222");

            Assert.True(VesselSpawner.IsKscRetiredFinalFlight(rec));
            Assert.DoesNotContain(logLines, l => l.Contains("Adoption rejected"));
        }

        private static GhostChain PadChain(uint pid, string tipRecId)
        {
            return new GhostChain
            {
                OriginalVesselPid = pid,
                SpawnUT = 150.0,
                GhostStartUT = 50.0,
                TipRecordingId = tipRecId,
                TipTreeId = "tree-" + pid,
            };
        }

        [Fact]
        public void ChainTip_OnThePad_IsRetiredByTheGhoster()
        {
            var tip = MakeOnPad("rec-chain-tip-pad");
            RecordingStore.AddRecordingWithTreeForTesting(tip);
            var chain = PadChain(900u, tip.RecordingId);

            uint pid = new VesselGhoster().SpawnAtChainTip(chain);

            Assert.Equal(0u, pid);
            Assert.True(tip.VesselSpawned);
            Assert.True(VesselGhoster.IsChainTipSettledAsKscRetirement(chain));
            Assert.Contains(logLines, l => l.Contains("Spawn RETIRED for #-1 (Pad Rig)"));
        }

        [Fact]
        public void ChainTip_Released_LogsAndLeavesNoMapGhost()
        {
            var chain = PadChain(901u, "rec-released");
            VesselGhoster.ReleaseChainRetiredAtKsc(chain, "unit");
            Assert.Contains(logLines, l => l.Contains("[Ghoster]")
                && l.Contains("Chain tip retired at KSC (unit): originalPid=901 tip=rec-released mapGhostRemoved=False"));
        }

        [Fact]
        public void TimeJump_CrossedPadChainTip_IsRetiredAndItsChainSettled()
        {
            var tip = MakeOnPad("rec-jump-tip-pad");
            RecordingStore.AddRecordingWithTreeForTesting(tip);
            var chains = new Dictionary<uint, GhostChain> { { 902u, PadChain(902u, tip.RecordingId) } };

            var keys = TimeJumpManager.SpawnCrossedChainTips(
                chains, new VesselGhoster(), 100.0, 300.0, out int retired);

            Assert.Equal(new List<uint> { 902u }, keys);
            Assert.Equal(1, retired);
            Assert.Single(chains); // not mutated (#79): the caller removes the key
            Assert.True(tip.VesselSpawned);
            Assert.Contains(logLines, l => l.Contains("Chain tip retired at KSC (time-jump): originalPid=902"));
            Assert.DoesNotContain(logLines, l => l.Contains("Chain tip spawned during jump"));
        }

        [Fact]
        public void TimeJump_CrossedMidRunwayChainTip_IsNotCountedAsRetired()
        {
            var tip = MakeParked("rec-jump-tip-runway", MidRunwayLat, MidRunwayLon);
            tip.VesselSnapshot = null; // the ghoster stops at "no VesselSnapshot": no spawn, no retirement
            RecordingStore.AddRecordingWithTreeForTesting(tip);
            var chains = new Dictionary<uint, GhostChain> { { 903u, PadChain(903u, tip.RecordingId) } };

            var keys = TimeJumpManager.SpawnCrossedChainTips(
                chains, new VesselGhoster(), 100.0, 300.0, out int retired);

            Assert.Empty(keys);
            Assert.Equal(0, retired);
            Assert.DoesNotContain(logLines, l => l.Contains("Chain tip retired at KSC"));
        }

        [Fact]
        public void Flight_RetireChainAtKsc_DropsTheChainFromTheActiveSet()
        {
            var rec = MakeOnPad("rec-flight-chain");
            var chain = PadChain(904u, rec.RecordingId);
            var host = MakeHost();
            var active = new Dictionary<uint, GhostChain> { { 904u, chain } };
            typeof(ParsekFlight).GetField("activeGhostChains", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(host, active);

            typeof(ParsekFlight).GetMethod("RetireChainAtKsc", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(host, new object[] { chain, rec, 7 });

            Assert.Empty(active);
            Assert.Contains(logLines, l => l.Contains("Chain tip retired at KSC (flight #7 \"Pad Rig\"): originalPid=904"));
        }

        [Fact]
        public void CollectClosures_OnlyAboardAndUnknownRowsOfRetiredRecordings()
        {
            var actions = new List<GameAction>
            {
                Assignment("rec-retired", "Aboard Kerman", 0, 10, KerbalEndState.Aboard, 1),
                Assignment("rec-retired", "Aboard Kerman", 0, 10, KerbalEndState.Aboard, 2), // duplicate
                Assignment("rec-retired", "Unknown Kerman", 0, 10, KerbalEndState.Unknown, 3),
                Assignment("rec-retired", "Dead Kerman", 0, 10, KerbalEndState.Dead, 4),
                Assignment("rec-retired", "Recovered Kerman", 0, 10, KerbalEndState.Recovered, 5),
                Assignment("rec-retired", "Tourist Kerman", 0, 10, KerbalEndState.Aboard, 6, role: "Tourist"),
                Assignment("rec-other", "Other Kerman", 0, 10, KerbalEndState.Aboard, 7),
            };
            var retired = new Dictionary<string, double> { { "rec-retired", 123.5 } };
            var into = new Dictionary<string, List<KerbalsModule.RecoveryClosure>>();

            int added = KerbalsModule.CollectKscRetirementClosures(actions, retired, into);

            Assert.Equal(2, added);
            Assert.Equal(2, into.Count);
            Assert.Single(into["Aboard Kerman"]);
            Assert.Equal("rec-retired", into["Aboard Kerman"][0].OwnerRecordingId);
            Assert.Equal(123.5, into["Aboard Kerman"][0].RecoveryUT);
            Assert.True(into.ContainsKey("Unknown Kerman"));
        }
    }
}
