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
        /// Applies <see cref="ResolveActiveVesselIdxAfterGhostStrip"/> to a flight state the strip is
        /// about to run on and logs the move. Call BEFORE the removal; returns the new index the
        /// caller writes after it. <paramref name="flightScene"/> picks Warn over Verbose for an
        /// index that named a ghost (outside FLIGHT stock writes a placeholder 0 there).
        /// </summary>
        internal static int ComputeActiveVesselIdxForStrip(
            List<ProtoVessel> protoVessels, int oldIdx, bool flightScene)
        {
            var pids = new List<uint>(protoVessels.Count);
            for (int i = 0; i < protoVessels.Count; i++)
                pids.Add(protoVessels[i] != null ? protoVessels[i].persistentId : 0u);

            int newIdx = ResolveActiveVesselIdxAfterGhostStrip(
                pids, ghostMapVesselPids, oldIdx, out int ghostsBefore, out ActiveVesselIdxOutcome outcome);

            switch (outcome)
            {
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
        internal static void RemoveAllGhostVesselsBeforeFarSwitch(Vessel target)
        {
            if (target == null)
            {
                ParsekLog.Verbose(Tag, "Far Switch-To pre-stock ghost strip: no target, nothing done");
                return;
            }

            uint targetPid = target.persistentId;
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
