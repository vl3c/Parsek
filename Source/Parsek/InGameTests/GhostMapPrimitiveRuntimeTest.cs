using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime contract of the ghost-map-vessel primitive: the mechanics that make a map ghost a
    /// stock vessel (build the ProtoVessel node, register its pid in the guard set before
    /// <c>ProtoVessel.Load</c>, load, harden, strip at save, unregister on removal). It drives the
    /// real create path (<c>GhostMapPresence.CreateGhostVessel</c> -> the private
    /// <c>BuildAndLoadGhostProtoVessel</c> -> <c>BuildAndLoadGhostProtoVesselCore</c>) with a
    /// trajectory that is NOT a <see cref="Recording"/>, so the path is proven to need only
    /// <see cref="IPlaybackTrajectory"/>. Everything runs inside one frame (no yield), so no
    /// per-frame map pass or probe ever sees the test ghosts.
    /// <para>What it asserts, each one a statement the moved code must keep:</para>
    /// <list type="number">
    /// <item>the ghost pid is in the guard set at the moment stock fires <c>onVesselCreate</c>
    /// for it (the last statement of <c>ProtoVessel.Load</c>, decompiled), so every guarded
    /// handler sees a ghost;</item>
    /// <item>the vessel name is <c>"Ghost: " + VesselName</c> and the ProtoVessel carries one
    /// <c>sensorBarometer</c> part, <c>prst</c> True and <c>cln</c> False (the signature the
    /// harness stock-load scan keys on);</item>
    /// <item>the create sequence runs the part-hardening pass on this vessel (its log line, read
    /// under a verbose override) and every part the vessel holds is hardened. A map ghost is
    /// normally unloaded, with no Part objects; its parts are hardened again on
    /// <c>Vessel.Load</c> by <c>GhostVesselLoadedInertPatch</c>, and when it is loaded here
    /// no collider of it may be enabled;</item>
    /// <item>the save strip, through the one production entry
    /// <c>ParsekScenario.StripGhostMapVesselsFailClosed</c>, removes exactly the ghost
    /// ProtoVessel from a flight state that also holds an ordinary ProtoVessel, and leaves the
    /// live registration alone;</item>
    /// <item>a single removal (<c>RemoveGhostVessel</c>) keeps the pid registered and the
    /// teardown flag raised while stock fires <c>onVesselWillDestroy</c>, then unregisters the
    /// pid and lowers the flag;</item>
    /// <item><c>RemoveAllGhostVessels</c> does the same inside its remove-all scope and leaves
    /// the guard set empty.</item>
    /// </list>
    /// </summary>
    public class GhostMapPrimitiveRuntimeTest
    {
        private const string Tag = "TestRunner";
        private const string KerbinBodyName = "Kerbin";
        // The literal the harness stock-load scan keys on; asserted beside the constant so a
        // change to either is caught here as well as by the source gate.
        private const string GhostNamePrefixLiteral = "Ghost: ";
        private const string GhostPartName = "sensorBarometer";
        private const string HardenLineMarker = "Ghost vessel parts hardened: vessel='";

        // A circular, fully exo-atmospheric Kerbin orbit (altitude about 200 km): far from any
        // pad or low-orbit active vessel, so the ghost stays unloaded.
        private const double Sma = 800000.0;
        private const double Ecc = 0.001;

        [InGameTest(Category = "GhostMap", Scene = GameScenes.FLIGHT,
            Description = "Ghost map primitive (FLIGHT): pid registered at onVesselCreate, 'Ghost: ' name and one-part signature, hardened, save strip removes only the ghost, removal and remove-all unregister")]
        public void GhostMapPrimitive_RuntimeContract_Flight()
        {
            RunPrimitiveContract("FLIGHT");
        }

        [InGameTest(Category = "GhostMap", Scene = GameScenes.TRACKSTATION,
            Description = "Ghost map primitive (TRACKSTATION): pid registered at onVesselCreate, 'Ghost: ' name and one-part signature, hardened, save strip removes only the ghost, removal and remove-all unregister")]
        public void GhostMapPrimitive_RuntimeContract_TrackingStation()
        {
            RunPrimitiveContract("TRACKSTATION");
        }

        private sealed class EventWitness
        {
            public string ExpectedName;
            public int CreateFired;
            public bool RegisteredAtCreate = true;
            public int WillDestroyFired;
            public bool RegisteredAtWillDestroy = true;
            public bool TeardownAtWillDestroy = true;
            public bool RemoveAllAtWillDestroy;
        }

        private static void RunPrimitiveContract(string sceneLabel)
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.flightState == null)
            {
                InGameAssert.Skip("No current game flight state");
                return;
            }
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                return;
            }

            double nowUT = Planetarium.GetUniversalTime();
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            var trajA = new PrimitiveWitnessTrajectory("Parsek Primitive Witness A " + suffix, kerbin.name, nowUT, 0.0);
            var trajB = new PrimitiveWitnessTrajectory("Parsek Primitive Witness B " + suffix, kerbin.name, nowUT, 180.0);
            uint chainBase = 0x7E000000u + (uint)(UnityEngine.Time.frameCount & 0xFFFF);
            var chainA = new GhostChain { OriginalVesselPid = chainBase, TipRecordingId = trajA.RecordingId };
            var chainB = new GhostChain { OriginalVesselPid = chainBase + 1u, TipRecordingId = trajB.RecordingId };

            var witnessA = new EventWitness { ExpectedName = GhostMapPresence.GhostVesselNamePrefix + trajA.VesselName };
            var witnessB = new EventWitness { ExpectedName = GhostMapPresence.GhostVesselNamePrefix + trajB.VesselName };
            var hardenLines = new List<string>();

            EventData<Vessel>.OnEvent onCreate = v =>
            {
                EventWitness w = Match(v, witnessA, witnessB);
                if (w == null) return;
                w.CreateFired++;
                if (!GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    w.RegisteredAtCreate = false;
            };
            EventData<Vessel>.OnEvent onWillDestroy = v =>
            {
                EventWitness w = Match(v, witnessA, witnessB);
                if (w == null) return;
                w.WillDestroyFired++;
                if (!GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    w.RegisteredAtWillDestroy = false;
                if (!GhostMapPresence.IsGhostTeardownInProgress)
                    w.TeardownAtWillDestroy = false;
                if (GhostMapPresence.IsRemoveAllGhostVesselsInProgress)
                    w.RemoveAllAtWillDestroy = true;
            };

            Action<string> priorObserver = ParsekLog.TestObserverForTesting;
            bool? priorVerbose = ParsekLog.VerboseOverrideForTesting;
            ParsekLog.TestObserverForTesting = line =>
            {
                priorObserver?.Invoke(line);
                if (line != null && line.Contains(HardenLineMarker))
                    hardenLines.Add(line);
            };
            ParsekLog.VerboseOverrideForTesting = true;
            GameEvents.onVesselCreate.Add(onCreate);
            GameEvents.onVesselWillDestroy.Add(onWillDestroy);

            Vessel ghostA = null;
            Vessel ghostB = null;
            uint pidA = 0u;
            uint pidB = 0u;
            try
            {
                // 1. Create through the production path; the pid is registered when onVesselCreate fires.
                ghostA = GhostMapPresence.CreateGhostVessel(chainA, trajA);
                InGameAssert.IsNotNull(ghostA,
                    "CreateGhostVessel(chain, IPlaybackTrajectory) must load a ghost map vessel from a non-Recording trajectory");
                pidA = ghostA.persistentId;
                InGameAssert.AreEqual(1, witnessA.CreateFired,
                    "ProtoVessel.Load must fire onVesselCreate exactly once for the ghost");
                InGameAssert.IsTrue(witnessA.RegisteredAtCreate,
                    "The ghost pid must already be in the guard set when onVesselCreate fires (pre-registration before Load)");
                InGameAssert.IsTrue(GhostMapPresence.IsGhostMapVessel(pidA),
                    "The ghost pid must stay registered after the create returns");

                // 2. Name and ProtoVessel signature.
                InGameAssert.AreEqual(GhostNamePrefixLiteral, GhostMapPresence.GhostVesselNamePrefix,
                    "The ghost name prefix is the literal the stock-load scan keys on");
                InGameAssert.AreEqual(GhostNamePrefixLiteral + trajA.VesselName, ghostA.vesselName,
                    "The ghost vessel name must be the prefix plus the trajectory's VesselName");
                ProtoVessel pvA = ghostA.protoVessel;
                InGameAssert.IsNotNull(pvA, "The ghost vessel must keep its ProtoVessel");
                InGameAssert.IsTrue(object.ReferenceEquals(pvA.vesselRef, ghostA),
                    "The ProtoVessel must reference the loaded ghost vessel");
                InGameAssert.AreEqual(pidA, pvA.persistentId,
                    "The ProtoVessel pid must be the loaded vessel's pid");
                InGameAssert.AreEqual(1, pvA.protoPartSnapshots != null ? pvA.protoPartSnapshots.Count : 0,
                    "The ghost ProtoVessel must hold exactly one part");
                InGameAssert.AreEqual(GhostPartName, pvA.protoPartSnapshots[0].partName,
                    "The ghost's one part must be the sensorBarometer marker");
                InGameAssert.IsTrue(pvA.persistent, "The ghost ProtoVessel must be written with prst True");
                InGameAssert.IsFalse(pvA.autoClean, "The ghost ProtoVessel must be written with cln False");
                InGameAssert.IsTrue(
                    HighLogic.CurrentGame.flightState.protoVessels.Contains(pvA),
                    "The ghost ProtoVessel must be in the live game's flight state (stock writes it; the save strip removes it)");

                // 3. Hardening.
                string hardenLine = FindHardenLine(hardenLines, witnessA.ExpectedName);
                InGameAssert.IsNotNull(hardenLine,
                    "The create sequence must run the part-hardening pass on the ghost (no 'Ghost vessel parts hardened' line for it)");
                int partCount = ghostA.parts != null ? ghostA.parts.Count : 0;
                InGameAssert.Contains(hardenLine, "' parts=" + partCount.ToString(CultureInfo.InvariantCulture) + " for ",
                    "The hardening pass must cover every part the vessel held at create");
                AssertPartsInert(ghostA);

                // 4. Save strip: exactly the ghost, the live registration untouched.
                ProtoVessel ordinary = BuildOrdinaryProtoVessel(kerbin, nowUT, suffix);
                InGameAssert.IsFalse(GhostMapPresence.IsGhostMapVessel(ordinary.persistentId),
                    "Precondition: the ordinary ProtoVessel's pid is not a ghost pid");
                var flightState = (FlightState)FormatterServices.GetUninitializedObject(typeof(FlightState));
                flightState.protoVessels = new List<ProtoVessel> { pvA, ordinary };
                int stripped = ParsekScenario.StripGhostMapVesselsFailClosed(
                    flightState, "GhostMapPrimitiveRuntimeTest", "primitive-witness", phasesCompleted: true);
                InGameAssert.AreEqual(1, stripped, "The save strip must remove exactly the one ghost ProtoVessel");
                InGameAssert.AreEqual(1, flightState.protoVessels.Count,
                    "The save strip must leave the ordinary ProtoVessel in place");
                InGameAssert.IsTrue(object.ReferenceEquals(ordinary, flightState.protoVessels[0]),
                    "The ProtoVessel left after the strip must be the ordinary one");
                InGameAssert.IsTrue(GhostMapPresence.IsGhostMapVessel(pidA),
                    "The save strip edits only the flight state it is given; the ghost stays registered");

                // 5. Single removal.
                int registeredBefore = GhostMapPresence.RegisteredGhostMapVesselCount;
                GhostMapPresence.RemoveGhostVessel(chainA.OriginalVesselPid, "ghost-map-primitive-test-single");
                ghostA = null;
                InGameAssert.AreEqual(1, witnessA.WillDestroyFired,
                    "RemoveGhostVessel must Die() the ghost (one onVesselWillDestroy)");
                InGameAssert.IsTrue(witnessA.RegisteredAtWillDestroy,
                    "The ghost pid must stay registered while stock fires onVesselWillDestroy");
                InGameAssert.IsTrue(witnessA.TeardownAtWillDestroy,
                    "The ghost teardown flag must be raised while stock fires onVesselWillDestroy");
                InGameAssert.IsFalse(witnessA.RemoveAllAtWillDestroy,
                    "A single removal must not raise the remove-all flag");
                InGameAssert.IsFalse(GhostMapPresence.IsGhostMapVessel(pidA),
                    "RemoveGhostVessel must unregister the ghost pid");
                InGameAssert.AreEqual(registeredBefore - 1, GhostMapPresence.RegisteredGhostMapVesselCount,
                    "RemoveGhostVessel must unregister exactly one pid");
                InGameAssert.IsFalse(GhostMapPresence.IsGhostTeardownInProgress,
                    "The teardown flag must be lowered after RemoveGhostVessel returns");

                // 6. Remove-all.
                ghostB = GhostMapPresence.CreateGhostVessel(chainB, trajB);
                InGameAssert.IsNotNull(ghostB, "The second ghost must load");
                pidB = ghostB.persistentId;
                InGameAssert.IsTrue(witnessB.CreateFired == 1 && witnessB.RegisteredAtCreate,
                    "The second ghost's pid must be registered when onVesselCreate fires");
                GhostMapPresence.RemoveAllGhostVessels("ghost-map-primitive-test-remove-all");
                ghostB = null;
                InGameAssert.AreEqual(1, witnessB.WillDestroyFired,
                    "RemoveAllGhostVessels must Die() the ghost (one onVesselWillDestroy)");
                InGameAssert.IsTrue(witnessB.RegisteredAtWillDestroy && witnessB.TeardownAtWillDestroy
                    && witnessB.RemoveAllAtWillDestroy,
                    "Inside RemoveAllGhostVessels the pid must stay registered with the teardown and remove-all flags raised while onVesselWillDestroy fires");
                InGameAssert.IsFalse(GhostMapPresence.IsGhostMapVessel(pidB),
                    "RemoveAllGhostVessels must unregister the ghost pid");
                InGameAssert.AreEqual(0, GhostMapPresence.RegisteredGhostMapVesselCount,
                    "RemoveAllGhostVessels must leave the guard set empty");
                InGameAssert.IsFalse(GhostMapPresence.IsGhostTeardownInProgress,
                    "The teardown flag must be lowered after RemoveAllGhostVessels returns");

                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "GhostMapPrimitive contract OK: scene={0} pidA={1} pidB={2} parts={3} stripped={4}",
                    sceneLabel, pidA, pidB, partCount, stripped));
            }
            finally
            {
                GameEvents.onVesselCreate.Remove(onCreate);
                GameEvents.onVesselWillDestroy.Remove(onWillDestroy);
                ParsekLog.TestObserverForTesting = priorObserver;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;
                bool leftover = (pidA != 0u && GhostMapPresence.IsGhostMapVessel(pidA))
                    || (pidB != 0u && GhostMapPresence.IsGhostMapVessel(pidB))
                    || ghostA != null || ghostB != null;
                if (leftover)
                    GhostMapPresence.RemoveAllGhostVessels("ghost-map-primitive-test-cleanup");
            }
        }

        private static EventWitness Match(Vessel v, EventWitness a, EventWitness b)
        {
            if (v == null || v.vesselName == null) return null;
            if (v.vesselName == a.ExpectedName) return a;
            if (v.vesselName == b.ExpectedName) return b;
            return null;
        }

        private static string FindHardenLine(List<string> lines, string vesselName)
        {
            string needle = HardenLineMarker + vesselName + "'";
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Contains(needle)) return lines[i];
            return null;
        }

        private static void AssertPartsInert(Vessel v)
        {
            if (v.parts == null) return;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                InGameAssert.IsTrue(double.IsPositiveInfinity(p.maxTemp)
                    && double.IsPositiveInfinity(p.skinMaxTemp)
                    && float.IsPositiveInfinity(p.crashTolerance)
                    && double.IsPositiveInfinity(p.gTolerance)
                    && float.IsPositiveInfinity(p.breakingForce)
                    && float.IsPositiveInfinity(p.breakingTorque),
                    "Every ghost part must carry infinite thermal, crash, g and breaking tolerances");
                if (!v.loaded) continue;
                UnityEngine.Collider[] colliders = p.GetComponentsInChildren<UnityEngine.Collider>(true);
                for (int c = 0; c < colliders.Length; c++)
                    InGameAssert.IsFalse(colliders[c] != null && colliders[c].enabled,
                        "A loaded ghost part must have no enabled collider");
            }
        }

        // A plain ProtoVessel that is never loaded: it exists only as an entry of the test flight
        // state, so constructing it creates no Vessel and fires no onVesselCreate.
        private static ProtoVessel BuildOrdinaryProtoVessel(CelestialBody body, double nowUT, string suffix)
        {
            var orbit = new Orbit(0.0, Ecc, Sma + 50000.0, 0.0, 0.0, 0.0, nowUT, body);
            ConfigNode partNode = ProtoVessel.CreatePartNode(GhostPartName, 0);
            ConfigNode discovery = ProtoVessel.CreateDiscoveryNode(
                DiscoveryLevels.Owned, UntrackedObjectClass.C,
                double.PositiveInfinity, double.PositiveInfinity);
            ConfigNode node = ProtoVessel.CreateVesselNode(
                "Parsek Primitive Ordinary " + suffix, VesselType.Probe, orbit, 0,
                new ConfigNode[] { partNode }, discovery);
            return new ProtoVessel(node, HighLogic.CurrentGame);
        }

        /// <summary>
        /// The smallest trajectory the create path accepts: a terminal orbit on one body and
        /// nothing else. Deliberately not a <see cref="Recording"/>.
        /// </summary>
        private sealed class PrimitiveWitnessTrajectory : IPlaybackTrajectory
        {
            private readonly string vesselName;
            private readonly string bodyName;
            private readonly double epoch;
            private readonly double meanAnomaly;

            internal PrimitiveWitnessTrajectory(string vesselName, string bodyName, double epoch, double meanAnomalyDeg)
            {
                this.vesselName = vesselName;
                this.bodyName = bodyName;
                this.epoch = epoch;
                meanAnomaly = meanAnomalyDeg * Math.PI / 180.0;
                RecordingId = "ghost-map-primitive-" + Guid.NewGuid().ToString("N");
            }

            public List<TrajectoryPoint> Points { get; } = new List<TrajectoryPoint>();
            public List<OrbitSegment> OrbitSegments { get; } = new List<OrbitSegment>();
            public bool HasOrbitSegments => false;
            public List<TrackSection> TrackSections { get; } = new List<TrackSection>();
            public double StartUT => epoch - 10.0;
            public double EndUT => epoch + 600.0;
            public int RecordingFormatVersion => 0;
            public List<PartEvent> PartEvents { get; } = new List<PartEvent>();
            public List<FlagEvent> FlagEvents { get; } = new List<FlagEvent>();
            public ConfigNode GhostVisualSnapshot => null;
            public ConfigNode VesselSnapshot => null;
            public string VesselName => vesselName;
            public string RecordedVesselGuid => null;
            public string RecordingId { get; }
            public bool LoopPlayback => false;
            public double LoopIntervalSeconds => 0.0;
            public LoopTimeUnit LoopTimeUnit => LoopTimeUnit.Sec;
            public uint LoopAnchorVesselId => 0u;
            public double LoopStartUT => double.NaN;
            public double LoopEndUT => double.NaN;
            public TerminalState? TerminalStateValue => TerminalState.Orbiting;
            public SurfacePosition? SurfacePos => null;
            public double TerrainHeightAtEnd => double.NaN;
            public bool PlaybackEnabled => true;
            public bool IsDebris => false;
            public string ParentAnchorRecordingId => null;
            public int LoopSyncParentIdx { get; set; } = -1;
            public string TerminalOrbitBody => bodyName;
            public double TerminalOrbitSemiMajorAxis => Sma;
            public double TerminalOrbitEccentricity => Ecc;
            public double TerminalOrbitInclination => 0.0;
            public double TerminalOrbitLAN => 0.0;
            public double TerminalOrbitArgumentOfPeriapsis => 0.0;
            public double TerminalOrbitMeanAnomalyAtEpoch => meanAnomaly;
            public double TerminalOrbitEpoch => epoch;
            public RecordingEndpointPhase EndpointPhase => RecordingEndpointPhase.Unknown;
            public string EndpointBodyName => null;
        }
    }
}
