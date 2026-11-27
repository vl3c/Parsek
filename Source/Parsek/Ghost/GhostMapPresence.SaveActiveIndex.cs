using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Parsek
{
    // Keeps the active-vessel index stock writes into a save pointing at the same vessel after the
    // ghost strip, and strips the ghosts before stock's far (unloaded) Switch-To reads a live index.
    internal static partial class GhostMapPresence
    {
        /// <summary>What <see cref="ResolveActiveVesselIdxAfterGhostStrip"/> did with the index.</summary>
        internal enum ActiveVesselIdxOutcome
        {
            /// <summary>No ghost sat before the active entry: the index already names it.</summary>
            Unchanged = 0,
            /// <summary>Ghosts sat before the active entry: the index moved down by their count.</summary>
            Shifted = 1,
            /// <summary>The index named a ghost (or nothing in range stock would write): set to 0,
            /// stock's own off-flight default.</summary>
            ActiveWasGhost = 2,
            /// <summary>The pre-strip index was out of range (stock writes -1 when the active
            /// vessel's ProtoVessel is not in the list): left as it was.</summary>
            OutOfRange = 3,
            /// <summary>Far-watch drone ids are set: the index names the player ship found by its
            /// Guid in the stripped list, whatever the captured index said.</summary>
            ShipByGuid = 4,
            /// <summary>Far-watch drone ids are set but no surviving ProtoVessel carries the ship's
            /// Guid: fail closed (-1), never 0.</summary>
            ShipMissing = 5,
            /// <summary>No ship to point at (it was lost, or the ids are the scene-exit
            /// leftovers): the index names a removed entry (the drone or a ghost), so it is -1
            /// (stock's value for an active vessel missing from the list), never 0.</summary>
            ActiveRemoved = 6,
        }

        // Reason string handed to RemoveAllGhostVessels on the pre-stock far Switch-To strip.
        internal const string FarSwitchBeforeStockIndexofReason = "far-switch-before-stock-indexof";

        /// <summary>
        /// PURE. Stock computes <c>FlightState.activeVesselIdx</c> in the <c>FlightState</c>
        /// constructor (decompiled <c>Game.Updated</c>: the flight state is built before
        /// <c>ScenarioRunner.GetUpdatedProtoModules</c> runs <c>ParsekScenario.OnSave</c>), so the
        /// index counts every ghost map ProtoVessel the strip then removes, and stock writes it
        /// verbatim as <c>activeVessel</c>. A ghost created before the active vessel (an EVA kerbal,
        /// a planted flag, a decoupled stage or a Parsek spawn appended after a live ghost) would
        /// make F9 focus a vessel that many slots later. Returns the index of the same entry in the
        /// stripped list: <paramref name="oldIdx"/> minus the ghosts before it.
        /// <para><paramref name="pidsBefore"/> is the pre-strip <c>protoVessels</c> pid list and
        /// <paramref name="ghostPids"/> the set the strip removes by (the same predicate, so the
        /// result names exactly the entry that survives).</para>
        /// </summary>
        internal static int ResolveActiveVesselIdxAfterGhostStrip(
            IReadOnlyList<uint> pidsBefore,
            ICollection<uint> ghostPids,
            int oldIdx,
            out int ghostsBefore,
            out ActiveVesselIdxOutcome outcome)
        {
            ghostsBefore = 0;
            if (pidsBefore == null || oldIdx < 0 || oldIdx >= pidsBefore.Count)
            {
                outcome = ActiveVesselIdxOutcome.OutOfRange;
                return oldIdx;
            }

            if (ghostPids == null || ghostPids.Count == 0)
            {
                outcome = ActiveVesselIdxOutcome.Unchanged;
                return oldIdx;
            }

            if (ghostPids.Contains(pidsBefore[oldIdx]))
            {
                outcome = ActiveVesselIdxOutcome.ActiveWasGhost;
                return 0;
            }

            for (int i = 0; i < oldIdx; i++)
            {
                if (ghostPids.Contains(pidsBefore[i]))
                    ghostsBefore++;
            }

            outcome = ghostsBefore > 0 ? ActiveVesselIdxOutcome.Shifted : ActiveVesselIdxOutcome.Unchanged;
            return oldIdx - ghostsBefore;
        }

        /// <summary>
        /// PURE. The far-watch extension of <see cref="ResolveActiveVesselIdxAfterGhostStrip"/>
        /// (design-far-watch.md 9.1). With no drone ids (<paramref name="droneGuid"/> empty) it is
        /// that resolver unchanged. With drone ids set the captured index is not trusted (it names
        /// the drone after a hand-over and is 0 between a switch and the next FixedUpdate): the
        /// target is the entry whose Guid is <paramref name="shipGuid"/>, and the result is its
        /// index in the list after the ghosts AND the drone (pid and Guid) are removed;
        /// <see cref="ActiveVesselIdxOutcome.ShipMissing"/> and -1 when no such entry survives.
        /// <paramref name="removedBefore"/> counts the removed entries ahead of the target.
        /// </summary>
        internal static int ResolveActiveVesselIdxAfterGhostStrip(
            IReadOnlyList<uint> pidsBefore,
            IReadOnlyList<Guid> guidsBefore,
            ICollection<uint> ghostPids,
            int oldIdx,
            uint dronePid,
            Guid droneGuid,
            Guid shipGuid,
            out int removedBefore,
            out ActiveVesselIdxOutcome outcome)
        {
            if (droneGuid == Guid.Empty)
                return ResolveActiveVesselIdxAfterGhostStrip(pidsBefore, ghostPids, oldIdx, out removedBefore, out outcome);

            removedBefore = 0;
            if (pidsBefore == null || guidsBefore == null || guidsBefore.Count != pidsBefore.Count
                || shipGuid == Guid.Empty)
            {
                outcome = ActiveVesselIdxOutcome.ShipMissing;
                return -1;
            }
            int removed = 0;
            for (int i = 0; i < pidsBefore.Count; i++)
            {
                bool isGhost = ghostPids != null && ghostPids.Contains(pidsBefore[i]);
                bool isDrone = pidsBefore[i] == dronePid && guidsBefore[i] == droneGuid;
                if (isGhost || isDrone)
                {
                    removed++;
                    continue;
                }
                if (guidsBefore[i] == shipGuid)
                {
                    removedBefore = removed;
                    outcome = ActiveVesselIdxOutcome.ShipByGuid;
                    return i - removed;
                }
            }
            removedBefore = removed;
            outcome = ActiveVesselIdxOutcome.ShipMissing;
            return -1;
        }

        /// <summary>
        /// PURE. The far-watch strip with no ship to point at (the ship was lost, 7.6 V2, or the
        /// ids are only the scene-exit leftovers): the ghosts AND the drone (pid and Guid) are
        /// removed and the captured index follows its own entry. It moves down by the removed
        /// entries before it (<see cref="ActiveVesselIdxOutcome.Shifted"/> or
        /// <see cref="ActiveVesselIdxOutcome.Unchanged"/>); when it names a removed entry it is
        /// -1 (<see cref="ActiveVesselIdxOutcome.ActiveRemoved"/>), never 0; an out-of-range index
        /// is left as it was. Never refuses the save.
        /// </summary>
        internal static int ResolveActiveVesselIdxAfterStripWithoutShip(
            IReadOnlyList<uint> pidsBefore,
            IReadOnlyList<Guid> guidsBefore,
            ICollection<uint> ghostPids,
            int oldIdx,
            uint dronePid,
            Guid droneGuid,
            out int removedBefore,
            out ActiveVesselIdxOutcome outcome)
        {
            removedBefore = 0;
            if (pidsBefore == null || oldIdx < 0 || oldIdx >= pidsBefore.Count)
            {
                outcome = ActiveVesselIdxOutcome.OutOfRange;
                return oldIdx;
            }
            bool haveGuids = guidsBefore != null && guidsBefore.Count == pidsBefore.Count;
            for (int i = 0; i <= oldIdx; i++)
            {
                bool isGhost = ghostPids != null && ghostPids.Contains(pidsBefore[i]);
                bool isDrone = haveGuids && IsDroneProtoVessel(pidsBefore[i], guidsBefore[i], dronePid, droneGuid);
                if (!isGhost && !isDrone)
                    continue;
                if (i == oldIdx)
                {
                    outcome = ActiveVesselIdxOutcome.ActiveRemoved;
                    return -1;
                }
                removedBefore++;
            }
            outcome = removedBefore > 0 ? ActiveVesselIdxOutcome.Shifted : ActiveVesselIdxOutcome.Unchanged;
            return oldIdx - removedBefore;
        }

        /// <summary>
        /// Applies <see cref="ResolveActiveVesselIdxAfterStripWithoutShip"/> to a flight state the
        /// strip is about to run on and logs it. Call BEFORE the removal.
        /// </summary>
        internal static int ComputeActiveVesselIdxForStripWithoutShip(
            List<ProtoVessel> protoVessels, int oldIdx, uint dronePid, Guid droneGuid, bool shipLost)
        {
            var pids = new List<uint>(protoVessels.Count);
            var guids = new List<Guid>(protoVessels.Count);
            for (int i = 0; i < protoVessels.Count; i++)
            {
                pids.Add(protoVessels[i] != null ? protoVessels[i].persistentId : 0u);
                guids.Add(protoVessels[i] != null ? protoVessels[i].vesselID : Guid.Empty);
            }
            int newIdx = ResolveActiveVesselIdxAfterStripWithoutShip(
                pids, guids, ghostMapVesselPids, oldIdx, dronePid, droneGuid,
                out int removedBefore, out ActiveVesselIdxOutcome outcome);
            ParsekLog.Info(Tag, string.Format(ic,
                "Save activeVesselIdx {0}->{1} removedBefore={2} outcome={3} shipLost={4} - " +
                "no ship ProtoVessel; drone stripped with no ship repair",
                oldIdx, newIdx, removedBefore, outcome, shipLost));
            return newIdx;
        }

        /// <summary>The removal predicate of the far-watch strip: the drone by pid AND Guid.</summary>
        internal static bool IsDroneProtoVessel(uint pid, Guid guid, uint dronePid, Guid droneGuid)
        {
            return droneGuid != Guid.Empty && pid == dronePid && guid == droneGuid;
        }

        /// <summary>
        /// Applies <see cref="ResolveActiveVesselIdxAfterGhostStrip"/> to a flight state the strip is
        /// about to run on and logs the move. Call BEFORE the removal; returns the new index the
        /// caller writes after it. <paramref name="flightScene"/> picks Warn over Verbose for an
        /// index that named a ghost (outside FLIGHT stock writes a placeholder 0 there).
        /// Drone-aware (design-far-watch.md 9.1): with no drone ids (0, Guid.Empty) it is the plain
        /// ghost strip; with drone ids set the target is the ship by Guid; <paramref name="outcome"/> reports <see cref="ActiveVesselIdxOutcome.ShipMissing"/>
        /// (and -1) when its ProtoVessel is not in the list, and the caller fails closed.
        /// </summary>
        internal static int ComputeActiveVesselIdxForStrip(
            List<ProtoVessel> protoVessels, int oldIdx, bool flightScene,
            uint dronePid, Guid droneGuid, Guid shipGuid, out ActiveVesselIdxOutcome outcome)
        {
            var pids = new List<uint>(protoVessels.Count);
            var guids = new List<Guid>(protoVessels.Count);
            for (int i = 0; i < protoVessels.Count; i++)
            {
                pids.Add(protoVessels[i] != null ? protoVessels[i].persistentId : 0u);
                guids.Add(protoVessels[i] != null ? protoVessels[i].vesselID : Guid.Empty);
            }

            int newIdx = ResolveActiveVesselIdxAfterGhostStrip(
                pids, guids, ghostMapVesselPids, oldIdx, dronePid, droneGuid, shipGuid,
                out int ghostsBefore, out outcome);

            switch (outcome)
            {
                case ActiveVesselIdxOutcome.ShipByGuid:
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Save activeVesselIdx {0}->{1} removedBefore={2} target=ship-by-guid " +
                            "shipGuid={3} - far-watch drone ids set, captured index not trusted",
                            oldIdx, newIdx, ghostsBefore, shipGuid.ToString("N")));
                    break;
                case ActiveVesselIdxOutcome.ShipMissing:
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "Save activeVesselIdx {0}: far-watch drone ids set but no ProtoVessel carries " +
                            "the ship guid={1} (protoVessels={2})",
                            oldIdx, shipGuid.ToString("N"), protoVessels.Count));
                    break;
                case ActiveVesselIdxOutcome.Shifted:
                    ParsekLog.Info(Tag,
                        string.Format(ic,
                            "Save activeVesselIdx {0}->{1} ghostsBefore={2} activePid={3} - kept the " +
                            "saved active vessel on the same craft after the ghost strip",
                            oldIdx, newIdx, ghostsBefore, pids[oldIdx]));
                    break;
                case ActiveVesselIdxOutcome.ActiveWasGhost:
                    string line = string.Format(ic,
                        "Save activeVesselIdx {0}->0: the index named ghost map vessel pid={1}; " +
                        "fell back to 0 (stock's off-flight default)",
                        oldIdx, pids[oldIdx]);
                    if (flightScene)
                        ParsekLog.Warn(Tag, line);
                    else
                        ParsekLog.Verbose(Tag, line);
                    break;
                case ActiveVesselIdxOutcome.OutOfRange:
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "Save activeVesselIdx {0} out of range (protoVessels={1}); left unchanged",
                            oldIdx, protoVessels.Count));
                    break;
            }

            return newIdx;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadIsFlightSceneForSaveIndexLogCore()
        {
            return HighLogic.LoadedScene == GameScenes.FLIGHT;
        }

        private static bool IsFlightSceneForSaveIndexLog()
        {
            try
            {
                return ReadIsFlightSceneForSaveIndexLogCore();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Far Switch-To fix. Stock <c>FlightGlobals.setActiveVessel</c> with an unloaded target fires
        /// <c>onVesselSwitchingToUnloaded</c>, then saves persistent.sfs, then calls
        /// <c>FlightDriver.StartAndFocusVessel(HighLogic.CurrentGame.Updated(), vessels.IndexOf(v))</c>
        /// with no return path in between (decompiled, KSP 1.12.5). <c>Updated()</c> runs first and
        /// strips the ghosts from the game it hands over, while <c>IndexOf</c> reads the LIVE list
        /// that still holds them, so a target created after a live ghost would be focused that many
        /// slots late. Removing every ghost map vessel from the live list in that event makes the
        /// live index and the stripped game agree; the scene reloads right after, so the ghosts
        /// would be torn down a moment later anyway. A ghost target is left alone (stripping would
        /// remove the target itself).
        /// </summary>
        /// <summary>
        /// Belt (design-far-watch.md 9.1): the switch-token patch refuses every switch Parsek did
        /// not start while the drone exists, so a far switch with the drone up means something
        /// called <c>FlightDriver.StartAndFocusVessel</c> directly. Treat the drone like a ghost
        /// there: remove it from the live list so stock's <c>IndexOf</c> matches the stripped save.
        /// </summary>
        private static void RemoveCameraDroneBeforeFarSwitch(Vessel target)
        {
            if (!CameraDroneIdentity.Exists)
                return;
            ParsekLog.Error("CameraDrone",
                string.Format(ic,
                    "far Switch-To while the camera drone exists dronePid={0} targetPid={1} - removing the drone from the live list",
                    CameraDroneIdentity.DronePid, target != null ? target.persistentId : 0u));
            var live = FlightGlobals.Vessels;
            if (live == null)
                return;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Vessel v = live[i];
                if (v == null || !CameraDroneIdentity.IsDrone(v.persistentId, v.id))
                    continue;
                if (v == FlightGlobals.ActiveVessel)
                    FlightGlobals.RemoveVessel(v);
                else
                    v.Die();
            }
        }

        internal static void RemoveAllGhostVesselsBeforeFarSwitch(Vessel target)
        {
            if (target == null)
            {
                ParsekLog.Verbose(Tag, "Far Switch-To pre-stock ghost strip: no target, nothing done");
                return;
            }

            uint targetPid = target.persistentId;
            RemoveCameraDroneBeforeFarSwitch(target);
            int ghostCount = ghostMapVesselPids.Count;
            if (ghostCount == 0)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(ic,
                        "Far Switch-To pre-stock ghost strip: no ghost map vessels targetPid={0} - " +
                        "stock IndexOf already ghost-consistent",
                        targetPid));
                return;
            }

            if (IsGhostMapVessel(targetPid))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "Far Switch-To pre-stock ghost strip skipped: target pid={0} is a ghost map " +
                        "vessel (ghostCount={1})",
                        targetPid, ghostCount));
                return;
            }

            var livePids = new List<uint>();
            var liveVessels = FlightGlobals.Vessels;
            if (liveVessels != null)
            {
                for (int i = 0; i < liveVessels.Count; i++)
                {
                    Vessel vessel = liveVessels[i];
                    if (vessel != null)
                        livePids.Add(vessel.persistentId);
                }
            }

            int drift = ComputeFlyIndexDrift(livePids, ghostMapVesselPids, targetPid);
            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Far Switch-To pre-stock ghost strip: targetPid={0} ghostCount={1} " +
                    "liveVesselsBefore={2} indexDriftAvoided={3} - removed all ghost map vessels " +
                    "before stock IndexOf so the live index matches the stripped game",
                    targetPid, ghostCount, livePids.Count, drift));

            RemoveAllGhostVessels(FarSwitchBeforeStockIndexofReason);
        }
    }
}
