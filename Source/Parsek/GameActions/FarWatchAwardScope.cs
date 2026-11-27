using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// The far-watch award belt (docs/dev/design-far-watch.md 10.3). The career fences stop
    /// the stock conditions upstream; this catches a fence that did not hold. It keys on the
    /// vessel an award is FOR, never on "a drone exists": the parked ship crossing an SOI on
    /// rails earns a genuine award mid-watch, and that award is booked as always.
    ///
    /// Attribution: a prefix on <c>ProgressNode.CrewSensitiveComplete(Vessel / ProtoVessel)</c>,
    /// which every vessel node calls with its host just before it completes and pays in the
    /// same call, stamps {node, host pid + guid, frame}. The records nodes pay without a host
    /// and track only the active vessel, so their award is the active vessel's.
    ///
    /// Lifetime (scripts/arch/lifetimes.toml, scene): a stamp is only ever read in its own
    /// frame, so a stale one is inert; the far-watch teardown and the test reset clear it.
    /// </summary>
    internal static class FarWatchAwardScope
    {
        internal const string Tag = "FarWatchFence";

        private static object stampNode;
        private static int stampFrame = -1;
        private static uint stampPid;
        private static Guid stampGuid;

        /// <summary>Stamps the host a vessel node is about to complete for (only while a drone exists).</summary>
        internal static void Stamp(object node, uint hostPid, Guid hostGuid, int frame)
        {
            stampNode = node;
            stampFrame = frame;
            stampPid = hostPid;
            stampGuid = hostGuid;
        }

        internal static void Clear()
        {
            stampNode = null;
            stampFrame = -1;
            stampPid = 0;
            stampGuid = Guid.Empty;
        }

        /// <summary>
        /// Pure: an award is the drone's when this node's stamp, taken in this frame, names the
        /// drone, or when it is a records node (active vessel only) and the drone is active.
        /// </summary>
        internal static bool IsDroneAttributed(
            bool stampIsThisNode, bool stampIsThisFrame, bool stampNamesDrone,
            bool isRecordsNode, bool droneActive)
        {
            if (stampIsThisNode && stampIsThisFrame && stampNamesDrone)
                return true;
            return isRecordsNode && droneActive;
        }

        internal static bool IsRecordsNode(ProgressNode node)
        {
            return node is KSPAchievements.RecordsSpeed
                || node is KSPAchievements.RecordsAltitude
                || node is KSPAchievements.RecordsDistance
                || node is KSPAchievements.RecordsDepth;
        }

        /// <summary>
        /// The belt both capture sites call: true means do not book this award. Returns false
        /// at once with no drone and no stamp, so the headless paths never touch Unity.
        /// </summary>
        internal static bool ShouldRefuseAward(ProgressNode node, string site, string milestoneId)
        {
            if (node == null)
                return false;
            if (!CameraDroneIdentity.Exists && stampNode == null)
                return false;
            bool thisNode = ReferenceEquals(stampNode, node);
            bool thisFrame = thisNode && stampFrame == ReadFrameCountCore();
            bool namesDrone = CameraDroneIdentity.IsDrone(stampPid, stampGuid);
            bool records = IsRecordsNode(node);
            bool droneActive = records && CameraDroneIdentity.Exists && ReadDroneActiveCore();
            if (!IsDroneAttributed(thisNode, thisFrame, namesDrone, records, droneActive))
                return false;
            ParsekLog.Warn(Tag, string.Format(CultureInfo.InvariantCulture,
                "award not booked: {0} node={1} is the camera drone's (dronePid={2} stamp={3} records={4}); " +
                "a career fence did not hold",
                site ?? "?", milestoneId ?? "?", CameraDroneIdentity.DronePid, thisNode && thisFrame, records));
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadFrameCountCore()
        {
            try
            {
                return UnityEngine.Time.frameCount;
            }
            catch (Exception)
            {
                return -2;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadDroneActiveCore()
        {
            try
            {
                Vessel av = FlightGlobals.ActiveVessel;
                return av != null && CameraDroneIdentity.IsDrone(av.persistentId, av.id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ReadFrameCountForStamp()
        {
            return ReadFrameCountCore();
        }

        internal static void ResetForTesting()
        {
            Clear();
        }
    }
}
