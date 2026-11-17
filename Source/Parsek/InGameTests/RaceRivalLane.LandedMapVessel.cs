using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek.InGameTests.Helpers;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The rival-lane in-game cells (category <c>RaceRivalLane</c>, FLIGHT). This part holds the
    /// landed ghost map vessel cell: it calls <see cref="GhostMapVessels.BuildLanded"/> the way
    /// the rival map presence will, and is the caller that keeps that member live until then.
    /// </summary>
    public partial class RaceRivalLane
    {
        private const string LandedTag = "TestRunner";
        private const string LandedPartName = "sensorBarometer";
        private const string OffRailsRefusalMarker = "Blocked GoOffRails for ghost vessel '";
        private const string LandedSaveSlotPrefix = "parsek_landed_ghost_ingame_test_";

        // North of the active vessel by this many metres: inside the landed unpack range
        // (stock 200 m), so KSP loads the marker and asks it to go off rails.
        private const double LandedOffsetMeters = 40.0;
        // Frames to wait for KSP to load the marker and try GoOffRails on it.
        private const int LandedSettleFrameLimit = 300;
        private const double LandedNearMeters = 200.0;

        [InGameTest(Category = "RaceRivalLane", Scene = GameScenes.FLIGHT,
            Description = "A landed ghost map vessel (GhostMapVessels.BuildLanded) built 40 m from a landed active vessel: registered in the guard set at onVesselCreate, LANDED one-part 'Ghost: ' marker, stays packed (GoOffRails refused) with no enabled collider once KSP loads it, absent from a real save, removed afterwards")]
        public IEnumerator LandedMapVesselIsInert()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.flightState == null)
            {
                InGameAssert.Skip("No current game flight state");
                yield break;
            }
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.mainBody == null)
            {
                InGameAssert.Skip("No active vessel with a main body in FLIGHT");
                yield break;
            }
            if (!active.LandedOrSplashed)
            {
                InGameAssert.Skip(string.Format(CultureInfo.InvariantCulture,
                    "Needs the active vessel landed or splashed (for example on the pad) so a landed marker 40 m away is in physics range; situation={0}",
                    active.situation));
                yield break;
            }
            if (TimeWarp.CurrentRateIndex != 0)
            {
                InGameAssert.Skip("Needs time warp off (vessels do not unpack under warp)");
                yield break;
            }

            CelestialBody body = active.mainBody;
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            string ghostName = GhostMapPresence.GhostVesselNamePrefix + "Parsek Landed Witness " + suffix;
            double lat = active.latitude + LandedOffsetMeters / body.Radius * (180.0 / Math.PI);
            double lon = active.longitude;
            double alt = active.altitude;
            var owner = new GhostMapVesselOwner("race-rival-lane-test", "landed-witness-" + suffix);
            string saveSlot = LandedSaveSlotPrefix + suffix;

            int createFired = 0;
            bool registeredAtCreate = true;
            var refusalLines = new List<string>();
            EventData<Vessel>.OnEvent onCreate = v =>
            {
                if (v == null || v.vesselName != ghostName) return;
                createFired++;
                if (!GhostMapVessels.IsGhostMapVessel(v.persistentId))
                    registeredAtCreate = false;
            };
            Action<string> priorObserver = ParsekLog.TestObserverForTesting;
            bool? priorVerbose = ParsekLog.VerboseOverrideForTesting;
            ParsekLog.TestObserverForTesting = line =>
            {
                priorObserver?.Invoke(line);
                if (line != null && line.Contains(OffRailsRefusalMarker + ghostName + "'"))
                    refusalLines.Add(line);
            };
            ParsekLog.VerboseOverrideForTesting = true;
            GameEvents.onVesselCreate.Add(onCreate);

            Vessel ghost = null;
            uint pid = 0u;
            try
            {
                // 1. Build: registered when onVesselCreate fires, a LANDED one-part marker.
                bool built = GhostMapVessels.BuildLanded(
                    body, lat, lon, alt, ghostName, VesselType.Ship, owner,
                    "RaceRivalLane.LandedMapVesselIsInert", out GhostMapVesselLoad load);
                InGameAssert.IsTrue(built, "BuildLanded must load a landed ghost map vessel next to the active vessel");
                ghost = load.Vessel;
                InGameAssert.IsNotNull(ghost, "BuildLanded must hand back the loaded vessel");
                pid = ghost.persistentId;
                InGameAssert.AreEqual(1, createFired, "ProtoVessel.Load must fire onVesselCreate exactly once for the landed ghost");
                InGameAssert.IsTrue(registeredAtCreate,
                    "The landed ghost pid must already be in the guard set when onVesselCreate fires");
                InGameAssert.IsTrue(GhostMapVessels.IsGhostMapVessel(pid), "The landed ghost pid must stay registered");
                InGameAssert.IsTrue(GhostMapVessels.TryGetForeignOwner(pid, out GhostMapVesselOwner recorded)
                    && recorded.LaneId == owner.LaneId && recorded.RecordingId == owner.RecordingId,
                    "The landed ghost must be registered under the owner it was built for");
                InGameAssert.AreEqual(ghostName, ghost.vesselName, "The landed ghost keeps the finished name it was handed");
                ProtoVessel pv = ghost.protoVessel;
                InGameAssert.IsNotNull(pv, "The landed ghost must keep its ProtoVessel");
                InGameAssert.AreEqual(1, pv.protoPartSnapshots != null ? pv.protoPartSnapshots.Count : 0,
                    "The landed ghost ProtoVessel must hold exactly one part");
                InGameAssert.AreEqual(LandedPartName, pv.protoPartSnapshots[0].partName,
                    "The landed ghost's one part must be the sensorBarometer marker");
                InGameAssert.AreEqual(Vessel.Situations.LANDED, ghost.situation, "The landed ghost must be LANDED");
                InGameAssert.IsTrue(ghost.Landed, "The landed ghost must report Landed");
                InGameAssert.IsFalse(object.ReferenceEquals(FlightGlobals.ActiveVessel, ghost),
                    "Building the landed ghost must not change the active vessel");
            }
            catch
            {
                CleanUpLandedGhost(ghost, pid, onCreate, priorObserver, priorVerbose, saveSlot);
                throw;
            }

            // 2. Near the active vessel: KSP loads it and asks it off rails; it stays packed.
            bool everUnpacked = false;
            int frames = 0;
            bool done = false;
            while (!done)
            {
                yield return null;
                frames++;
                try
                {
                    InGameAssert.IsTrue(ghost != null && GhostMapVessels.IsGhostMapVessel(pid),
                        "The landed ghost must stay alive and registered while it sits near the active vessel");
                    if (!ghost.packed) everUnpacked = true;
                    done = (ghost.loaded && refusalLines.Count > 0) || frames >= LandedSettleFrameLimit;
                }
                catch
                {
                    CleanUpLandedGhost(ghost, pid, onCreate, priorObserver, priorVerbose, saveSlot);
                    throw;
                }
            }

            string savePath = Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty, "saves",
                HighLogic.SaveFolder ?? string.Empty, saveSlot + ".sfs");
            try
            {
                double distance = Vector3d.Distance(ghost.GetWorldPos3D(), FlightGlobals.ActiveVessel.GetWorldPos3D());
                InGameAssert.IsLessThan(distance, LandedNearMeters, string.Format(CultureInfo.InvariantCulture,
                    "The landed ghost must sit inside the unpack range of the active vessel (distance={0:F1} m)", distance));
                InGameAssert.IsTrue(ghost.loaded, string.Format(CultureInfo.InvariantCulture,
                    "KSP must load the landed ghost inside physics range within {0} frames (distance={1:F1} m)",
                    LandedSettleFrameLimit, distance));
                InGameAssert.IsFalse(everUnpacked, "The landed ghost must never go off rails (GoOffRails refused)");
                InGameAssert.IsTrue(ghost.packed, "The landed ghost must still be packed");
                InGameAssert.IsTrue(refusalLines.Count > 0,
                    "KSP must have asked the landed ghost to go off rails and Parsek must have refused it (no 'Blocked GoOffRails' line)");
                AssertLandedPartsInert(ghost);
            }
            catch
            {
                CleanUpLandedGhost(ghost, pid, onCreate, priorObserver, priorVerbose, saveSlot);
                throw;
            }

            // 3. A real save writes no VESSEL for it, and leaves the live registration alone.
            try
            {
                string saved = GamePersistence.SaveGame(saveSlot, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                InGameAssert.IsTrue(!string.IsNullOrEmpty(saved),
                    "GamePersistence.SaveGame returned null/empty for slot '" + saveSlot + "'");
            }
            catch
            {
                CleanUpLandedGhost(ghost, pid, onCreate, priorObserver, priorVerbose, saveSlot);
                throw;
            }
            yield return null;
            try
            {
                InGameAssert.IsTrue(File.Exists(savePath), "Expected the save at '" + savePath + "'");
                ConfigNode root = ConfigNode.Load(savePath);
                InGameAssert.IsNotNull(root, "ConfigNode.Load returned null for '" + savePath + "'");
                ConfigNode game = root.GetNode("GAME") ?? root;
                ConfigNode flightState = game.GetNode("FLIGHTSTATE");
                InGameAssert.IsNotNull(flightState, "The save must hold a FLIGHTSTATE node");
                ConfigNode[] vessels = flightState.GetNodes("VESSEL");
                InGameAssert.IsTrue(vessels.Length > 0, "The save must hold at least the active vessel (anti-vacuity)");
                string pidText = pid.ToString(CultureInfo.InvariantCulture);
                int leaked = 0;
                for (int i = 0; i < vessels.Length; i++)
                {
                    if (vessels[i].GetValue("persistentId") == pidText || vessels[i].GetValue("name") == ghostName)
                        leaked++;
                }
                InGameAssert.AreEqual(0, leaked, "The landed ghost must be absent from the saved FLIGHTSTATE");
                InGameAssert.IsTrue(GhostMapVessels.IsGhostMapVessel(pid),
                    "Saving must leave the live landed ghost registered");

                ParsekLog.Info(LandedTag, string.Format(CultureInfo.InvariantCulture,
                    "RaceRivalLane landed map vessel OK: pid={0} frames={1} loaded={2} packed={3} refusals={4} savedVessels={5}",
                    pid, frames, ghost.loaded, ghost.packed, refusalLines.Count, vessels.Length));
            }
            finally
            {
                CleanUpLandedGhost(ghost, pid, onCreate, priorObserver, priorVerbose, saveSlot);
            }

            // 4. The cleanup removed it.
            InGameAssert.IsFalse(GhostMapVessels.IsGhostMapVessel(pid), "The cleanup must unregister the landed ghost");
            InGameAssert.IsFalse(GhostMapVessels.TryGetForeignOwner(pid, out _), "The cleanup must drop the landed ghost's owner");
        }

        private static void AssertLandedPartsInert(Vessel v)
        {
            if (v.parts == null) return;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;
                InGameAssert.IsTrue(double.IsPositiveInfinity(p.maxTemp)
                    && float.IsPositiveInfinity(p.crashTolerance),
                    "Every landed ghost part must carry infinite thermal and crash tolerances");
                UnityEngine.Collider[] colliders = p.GetComponentsInChildren<UnityEngine.Collider>(true);
                for (int c = 0; c < colliders.Length; c++)
                    InGameAssert.IsFalse(colliders[c] != null && colliders[c].enabled,
                        "A loaded landed ghost part must have no enabled collider");
            }
        }

        // Removes the test's landed ghost the way the ghost removers do (Die inside a teardown
        // scope, then unregister), restores the log hooks and deletes the scratch save slot.
        // Never throws.
        private static void CleanUpLandedGhost(
            Vessel ghost, uint pid, EventData<Vessel>.OnEvent onCreate,
            Action<string> priorObserver, bool? priorVerbose, string saveSlot)
        {
            GameEvents.onVesselCreate.Remove(onCreate);
            ParsekLog.TestObserverForTesting = priorObserver;
            ParsekLog.VerboseOverrideForTesting = priorVerbose;
            if (ghost != null)
            {
                GhostMapVessels.BeginTeardown();
                try { ghost.Die(); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(LandedTag, "RaceRivalLane landed ghost cleanup: Die() threw: " + ex.Message);
                }
                finally { GhostMapVessels.EndTeardown(); }
            }
            if (pid != 0u)
                GhostMapVessels.Unregister(pid);
            try
            {
                // SaveGame writes <slot>.sfs and <slot>.loadmeta; the helper deletes both.
                QuickloadResumeHelpers.TryDeleteSaveSlot(saveSlot);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(LandedTag, "RaceRivalLane landed ghost cleanup: save slot delete failed: " + ex.Message);
            }
            ParsekLog.Info(LandedTag, string.Format(CultureInfo.InvariantCulture,
                "RaceRivalLane landed ghost cleaned up: pid={0} registered={1}",
                pid, pid != 0u && GhostMapVessels.IsGhostMapVessel(pid)));
        }
    }
}
