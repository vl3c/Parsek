using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>Pure result of <see cref="ReFlySwitchGate.Decide"/>.</summary>
    internal struct ReFlySwitchDecision
    {
        public bool Allowed;

        /// <summary>Grep-stable reason token.</summary>
        public string Reason;
    }

    /// <summary>
    /// Design section 2.12 Rule 3, "Switching is blocked": while a Re-Fly session is live the
    /// player can fly only the re-flown vessel (its walked stretch, section 1.6, as it
    /// continues through the session) and its own crew on EVA. Every player-initiated switch
    /// to any other vessel is refused at its stock entry point: the map Switch-To / focus Fly
    /// (<c>MapFocusObjectOnSelectPatch</c>), the Tracking Station Fly
    /// (<c>GhostTrackingFlyPatch</c>), the KSC marker Fly (<c>KscVesselMarkerFlyPatch</c>),
    /// the <c>[</c> / <c>]</c> vessel cycle (<c>VesselCycleReFlySwitchPatch</c> on stock
    /// <c>VesselSwitching.Update</c>) and the stock alarm dialog's Jump
    /// (<c>AlarmJumpSwitchPatch</c>). <c>FlightGlobals.SetActiveVessel</c> itself is never
    /// gated: stock calls it for an EVA, a board, a dock and an undock, and those are not
    /// switches.
    /// </summary>
    internal static class ReFlySwitchGate
    {
        internal const string LogTag = "ReFlySession";

        internal const string PathMapSwitchTo = "mapSwitchTo";
        internal const string PathTrackingStationFly = "tsFly";
        internal const string PathKscMarkerFly = "kscMarkerFly";
        internal const string PathVesselCycle = "vesselCycle";
        internal const string PathAlarmJump = "alarmJump";

        internal const string ReasonNoSession = "noSession";
        internal const string ReasonSessionVessel = "sessionVessel";
        internal const string ReasonNotSessionVessel = "notSessionVessel";
        internal const string ReasonTargetUnknown = "targetUnknown";
        internal const string ReasonSessionVesselUnresolved = "sessionVesselUnresolved";

        /// <summary>Hard bound on the allowed-set walk (the visited set already makes it cycle-safe).</summary>
        internal const int MaxWalkRecordings = 4096;

        /// <summary>
        /// Test seam: when set, replaces the live (marker, tree) lookup of
        /// <see cref="ResolveLiveSession"/>. Null in production.
        /// </summary>
        internal static Func<LiveSession> LiveSessionProviderForTesting;

        internal static void ResetForTesting()
        {
            LiveSessionProviderForTesting = null;
        }

        /// <summary>The live session inputs the gate reads.</summary>
        internal struct LiveSession
        {
            public ReFlySessionMarker Marker;
            public RecordingTree Tree;
        }

        /// <summary>
        /// Pure: the switch decision. No live session allows everything. A live session
        /// allows a target whose pid is in <paramref name="allowedPids"/> and refuses every
        /// other target, an unknown target (pid 0) and any target when the allowed set could
        /// not be resolved (fail closed: a refused switch does nothing, an allowed one could
        /// let the session change a vessel the committed future uses).
        /// </summary>
        internal static ReFlySwitchDecision Decide(
            bool markerLive, uint targetPid, ICollection<uint> allowedPids, string path)
        {
            if (!markerLive)
                return new ReFlySwitchDecision { Allowed = true, Reason = ReasonNoSession };
            if (targetPid == 0u)
                return new ReFlySwitchDecision { Allowed = false, Reason = ReasonTargetUnknown };
            if (allowedPids == null || allowedPids.Count == 0)
                return new ReFlySwitchDecision { Allowed = false, Reason = ReasonSessionVesselUnresolved };
            if (allowedPids.Contains(targetPid))
                return new ReFlySwitchDecision { Allowed = true, Reason = ReasonSessionVessel };
            return new ReFlySwitchDecision { Allowed = false, Reason = ReasonNotSessionVessel };
        }

        /// <summary>
        /// Pure: the vessel pids the session may fly, walked over <paramref name="tree"/> from
        /// the session's recording <paramref name="startRecordingId"/>. Taken: the start's own
        /// vessel; its chain segments; a switch continuation; an EVA's vessel continuation
        /// (same pid) and the kerbals it put out (the vessel's own EVA crew, and their chain
        /// and switch segments); a Board whose merged child is a vessel already taken (own
        /// crew re-boarding); a Dock's merged child (the re-flown vessel continues as the
        /// docked whole); and at an Undock / JointBreak / Breakup only the child that keeps the
        /// parent's pid. A vessel the session itself splits off (a new pid at a split), a
        /// placed ground part and a foreign inventory partner are not taken.
        /// </summary>
        internal static HashSet<uint> ComputeAllowedPids(RecordingTree tree, string startRecordingId)
        {
            var pids = new HashSet<uint>();
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(startRecordingId))
                return pids;
            Recording start;
            if (!tree.Recordings.TryGetValue(startRecordingId, out start) || start == null)
                return pids;

            var bpsByParent = new Dictionary<string, List<BranchPoint>>(StringComparer.Ordinal);
            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = tree.BranchPoints[i];
                    if (bp == null || bp.ParentRecordingIds == null) continue;
                    for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                    {
                        string parentId = bp.ParentRecordingIds[p];
                        if (string.IsNullOrEmpty(parentId)) continue;
                        List<BranchPoint> list;
                        if (!bpsByParent.TryGetValue(parentId, out list))
                        {
                            list = new List<BranchPoint>();
                            bpsByParent[parentId] = list;
                        }
                        list.Add(bp);
                    }
                }
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<Recording>();
            queue.Enqueue(start);
            visited.Add(start.RecordingId);
            int processed = 0;
            while (queue.Count > 0 && processed < MaxWalkRecordings)
            {
                Recording rec = queue.Dequeue();
                processed++;
                if (rec.VesselPersistentId != 0u)
                    pids.Add(rec.VesselPersistentId);

                if (!string.IsNullOrEmpty(rec.ChainId))
                {
                    foreach (var kv in tree.Recordings)
                    {
                        Recording other = kv.Value;
                        if (other != null
                            && string.Equals(other.ChainId, rec.ChainId, StringComparison.Ordinal)
                            && visited.Add(other.RecordingId))
                            queue.Enqueue(other);
                    }
                }

                List<BranchPoint> bps;
                if (!bpsByParent.TryGetValue(rec.RecordingId ?? "", out bps)) continue;
                for (int b = 0; b < bps.Count; b++)
                {
                    BranchPoint bp = bps[b];
                    if (bp.ChildRecordingIds == null) continue;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        Recording child;
                        string childId = bp.ChildRecordingIds[c];
                        if (string.IsNullOrEmpty(childId)
                            || !tree.Recordings.TryGetValue(childId, out child)
                            || child == null)
                            continue;
                        if (!IsSessionContinuation(bp.Type, rec, child, pids)) continue;
                        if (visited.Add(child.RecordingId))
                            queue.Enqueue(child);
                    }
                }
            }
            return pids;
        }

        private static bool IsSessionContinuation(
            BranchPointType type, Recording parent, Recording child, HashSet<uint> takenPids)
        {
            switch (type)
            {
                case BranchPointType.VesselSwitchContinuation:
                case BranchPointType.Dock:
                    return true;
                case BranchPointType.EVA:
                    if (!string.IsNullOrEmpty(child.EvaCrewName))
                        return string.IsNullOrEmpty(parent.EvaCrewName);
                    return child.VesselPersistentId != 0u
                        && child.VesselPersistentId == parent.VesselPersistentId;
                case BranchPointType.Board:
                    return child.VesselPersistentId != 0u && takenPids.Contains(child.VesselPersistentId);
                case BranchPointType.Undock:
                case BranchPointType.JointBreak:
                case BranchPointType.Breakup:
                    return child.VesselPersistentId != 0u
                        && child.VesselPersistentId == parent.VesselPersistentId;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure: the index in <paramref name="orderedCandidatePids"/> (the stock cycle order)
        /// of the first candidate the gate allows, or -1 when none is allowed.
        /// </summary>
        internal static int FirstAllowedIndex(IList<uint> orderedCandidatePids, ICollection<uint> allowedPids)
        {
            if (orderedCandidatePids == null || allowedPids == null) return -1;
            for (int i = 0; i < orderedCandidatePids.Count; i++)
            {
                if (orderedCandidatePids[i] != 0u && allowedPids.Contains(orderedCandidatePids[i]))
                    return i;
            }
            return -1;
        }

        /// <summary>The refusal line, design section 2.12 Rule 3 format.</summary>
        internal static string FormatRefusal(string sessionId, string path, uint targetPid, string reason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Switch refused sess={0} path={1} target={2} reason={3}",
                string.IsNullOrEmpty(sessionId) ? "<no-id>" : sessionId,
                path ?? "<none>",
                targetPid,
                reason ?? "<none>");
        }

        /// <summary>
        /// The live (marker, tree) the gate reads: the scenario's Re-Fly marker and the tree it
        /// names (the flight's active tree, else the pending tree, else the committed tree with
        /// that id). Marker null means no session.
        /// </summary>
        internal static LiveSession ResolveLiveSession()
        {
            var provider = LiveSessionProviderForTesting;
            if (provider != null)
                return provider();

            var session = new LiveSession();
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
                return session;
            session.Marker = scenario.ActiveReFlySessionMarker;
            if (session.Marker == null)
                return session;
            session.Tree = FindSessionTree(session.Marker.TreeId);
            return session;
        }

        private static RecordingTree FindSessionTree(string treeId)
        {
            if (string.IsNullOrEmpty(treeId)) return null;
            var flight = ParsekFlight.Instance;
            RecordingTree active = ReferenceEquals(null, flight) ? null : flight.ActiveTreeForDisplay;
            if (active != null && string.Equals(active.Id, treeId, StringComparison.Ordinal))
                return active;
            RecordingTree pending = RecordingStore.PendingTree;
            if (pending != null && string.Equals(pending.Id, treeId, StringComparison.Ordinal))
                return pending;
            var committed = RecordingStore.CommittedTrees;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    if (committed[i] != null
                        && string.Equals(committed[i].Id, treeId, StringComparison.Ordinal))
                        return committed[i];
                }
            }
            return null;
        }

        /// <summary>The allowed pid set of a live session (empty when it cannot be walked).</summary>
        internal static HashSet<uint> AllowedPidsFor(LiveSession session)
        {
            if (session.Marker == null) return new HashSet<uint>();
            return ComputeAllowedPids(session.Tree, session.Marker.ActiveReFlyRecordingId);
        }

        /// <summary>
        /// The live gate for one switch: true when the switch to <paramref name="targetPid"/>
        /// through <paramref name="path"/> is refused. Logs the design's refusal line when
        /// <paramref name="log"/> is true (a sibling prefix mirroring a refusal another prefix
        /// already logged passes false).
        /// </summary>
        internal static bool IsRefused(uint targetPid, string path, bool log = true)
        {
            string reason;
            return IsRefused(targetPid, path, log, out reason);
        }

        /// <summary><see cref="IsRefused(uint,string,bool)"/> that also reports the reason token.</summary>
        internal static bool IsRefused(uint targetPid, string path, bool log, out string reason)
        {
            reason = ReasonNoSession;
            LiveSession session = ResolveLiveSession();
            if (session.Marker == null)
                return false;
            HashSet<uint> allowed = AllowedPidsFor(session);
            ReFlySwitchDecision decision = Decide(true, targetPid, allowed, path);
            reason = decision.Reason;
            if (decision.Allowed)
            {
                if (log)
                    ParsekLog.Verbose(LogTag, string.Format(CultureInfo.InvariantCulture,
                        "Switch allowed sess={0} path={1} target={2} reason={3} allowed={4}",
                        session.Marker.SessionId ?? "<no-id>", path, targetPid, decision.Reason,
                        allowed.Count));
                return false;
            }
            if (log)
                ParsekLog.Info(LogTag, FormatRefusal(session.Marker.SessionId, path, targetPid, decision.Reason));
            return true;
        }
    }
}
