using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// ScenarioModule that persists committed recordings to save games.
    /// Handles OnSave/OnLoad to serialize trajectory data into ConfigNodes.
    /// Also manages crew reservation for deferred vessel spawns.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
    public class ParsekScenario : ScenarioModule
    {
        public override void OnSave(ConfigNode node)
        {
            // Clear any existing recording nodes
            node.RemoveNodes("RECORDING");

            var recordings = RecordingStore.CommittedRecordings;
            Debug.Log($"[Parsek Scenario] Saving {recordings.Count} committed recordings");

            for (int r = 0; r < recordings.Count; r++)
            {
                var rec = recordings[r];
                ConfigNode recNode = node.AddNode("RECORDING");
                recNode.AddValue("vesselName", rec.VesselName);
                recNode.AddValue("pointCount", rec.Points.Count);

                for (int i = 0; i < rec.Points.Count; i++)
                {
                    var pt = rec.Points[i];
                    ConfigNode ptNode = recNode.AddNode("POINT");
                    ptNode.AddValue("ut", pt.ut.ToString("R"));
                    ptNode.AddValue("lat", pt.latitude.ToString("R"));
                    ptNode.AddValue("lon", pt.longitude.ToString("R"));
                    ptNode.AddValue("alt", pt.altitude.ToString("R"));
                    ptNode.AddValue("rotX", pt.rotation.x.ToString("R"));
                    ptNode.AddValue("rotY", pt.rotation.y.ToString("R"));
                    ptNode.AddValue("rotZ", pt.rotation.z.ToString("R"));
                    ptNode.AddValue("rotW", pt.rotation.w.ToString("R"));
                    ptNode.AddValue("body", pt.bodyName);
                    ptNode.AddValue("funds", pt.funds.ToString("R"));
                    ptNode.AddValue("science", pt.science.ToString("R"));
                    ptNode.AddValue("rep", pt.reputation.ToString("R"));
                }

                // Persist vessel snapshot if present
                if (rec.VesselSnapshot != null)
                {
                    recNode.AddNode("VESSEL_SNAPSHOT", rec.VesselSnapshot);
                }
            }
        }

        // Static flag: only load from save once per KSP session.
        // On revert, the launch quicksave has stale data — the in-memory
        // static list is the real source of truth within a session.
        private static bool initialLoadDone = false;

        public override void OnLoad(ConfigNode node)
        {
            var recordings = RecordingStore.CommittedRecordings;

            if (initialLoadDone)
            {
                // Reset VesselSpawned on all recordings so they re-spawn after revert
                for (int i = 0; i < recordings.Count; i++)
                    recordings[i].VesselSpawned = false;

                ReserveSnapshotCrew();
                Debug.Log($"[Parsek Scenario] Revert detected — preserving {recordings.Count} session recordings");
                return;
            }

            initialLoadDone = true;
            recordings.Clear();

            ConfigNode[] recNodes = node.GetNodes("RECORDING");
            Debug.Log($"[Parsek Scenario] Loading {recNodes.Length} committed recordings");

            for (int r = 0; r < recNodes.Length; r++)
            {
                var recNode = recNodes[r];
                var rec = new RecordingStore.Recording
                {
                    VesselName = recNode.GetValue("vesselName") ?? "Unknown"
                };

                ConfigNode[] ptNodes = recNode.GetNodes("POINT");
                for (int i = 0; i < ptNodes.Length; i++)
                {
                    var ptNode = ptNodes[i];
                    var pt = new ParsekSpike.TrajectoryPoint();

                    double.TryParse(ptNode.GetValue("ut"), out pt.ut);
                    double.TryParse(ptNode.GetValue("lat"), out pt.latitude);
                    double.TryParse(ptNode.GetValue("lon"), out pt.longitude);
                    double.TryParse(ptNode.GetValue("alt"), out pt.altitude);

                    float rx, ry, rz, rw;
                    float.TryParse(ptNode.GetValue("rotX"), out rx);
                    float.TryParse(ptNode.GetValue("rotY"), out ry);
                    float.TryParse(ptNode.GetValue("rotZ"), out rz);
                    float.TryParse(ptNode.GetValue("rotW"), out rw);
                    pt.rotation = new Quaternion(rx, ry, rz, rw);

                    pt.bodyName = ptNode.GetValue("body") ?? "Kerbin";

                    double funds;
                    double.TryParse(ptNode.GetValue("funds"), out funds);
                    pt.funds = funds;

                    float science, rep;
                    float.TryParse(ptNode.GetValue("science"), out science);
                    float.TryParse(ptNode.GetValue("rep"), out rep);
                    pt.science = science;
                    pt.reputation = rep;

                    rec.Points.Add(pt);
                }

                // Restore vessel snapshot if saved
                ConfigNode snapshotNode = recNode.GetNode("VESSEL_SNAPSHOT");
                if (snapshotNode != null)
                {
                    rec.VesselSnapshot = snapshotNode;
                }

                if (rec.Points.Count > 0)
                {
                    recordings.Add(rec);
                    Debug.Log($"[Parsek Scenario] Loaded recording: {rec.VesselName}, " +
                        $"{rec.Points.Count} points, UT {rec.StartUT:F0}-{rec.EndUT:F0}" +
                        (rec.VesselSnapshot != null ? " (has vessel snapshot)" : ""));
                }
            }

            ReserveSnapshotCrew();
        }

        #region Crew Reservation

        /// <summary>
        /// Mark crew from all unspawned vessel snapshots as Assigned so they
        /// can't be placed on new craft in the VAB/SPH.
        /// </summary>
        public static void ReserveSnapshotCrew()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            foreach (var rec in RecordingStore.CommittedRecordings)
                ReserveCrewIn(rec.VesselSnapshot, rec.VesselSpawned, roster);

            if (RecordingStore.HasPending && RecordingStore.Pending.VesselSnapshot != null)
                ReserveCrewIn(RecordingStore.Pending.VesselSnapshot, false, roster);
        }

        /// <summary>
        /// Set crew in a specific snapshot back to Available.
        /// Call when discarding, recovering, or wiping recordings.
        /// </summary>
        public static void UnreserveCrewInSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == name && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned)
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                            Debug.Log($"[Parsek Scenario] Unreserved crew '{name}'");
                            break;
                        }
                    }
                }
            }
        }

        private static void ReserveCrewIn(ConfigNode snapshot, bool alreadySpawned, KerbalRoster roster)
        {
            if (snapshot == null || alreadySpawned) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == name && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available)
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                            Debug.Log($"[Parsek Scenario] Reserved crew '{name}' for deferred vessel spawn");
                            break;
                        }
                    }
                }
            }
        }

        #endregion
    }
}
