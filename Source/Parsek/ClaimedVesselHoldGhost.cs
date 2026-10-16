using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>Where a held-back vessel's despawn snapshot says it sits.</summary>
    internal struct ClaimedVesselHoldPose
    {
        public bool Landed;
        public int BodyIndex;
        public double Lat;
        public double Lon;
        public double Alt;
        public bool HasRotation;
        public double RotX;
        public double RotY;
        public double RotZ;
        public double RotW;
    }

    /// <summary>
    /// Pure decisions for the ghost of a vessel a ghost chain holds back before its first
    /// claim (design 12.5 / 13.10 steps 5-6). After a rewind, <c>VesselGhoster</c> despawns
    /// every real vessel a committed future claims, but the committed recording that carries
    /// it starts only at the claim (an EVA construction, a dock), so without this ghost the
    /// vessel vanished from the rewind until that moment. Before the claim the vessel was on
    /// rails in its despawn-snapshot form, so the ghost is built from that snapshot and sits
    /// where the snapshot says (body-fixed when landed, propagated on its orbit otherwise).
    /// </summary>
    internal static class ClaimedVesselHoldGhost
    {
        internal const string Tag = "ClaimedHoldGhost";

        /// <summary>
        /// Why the hold ghost must not be drawn now, or null when it should be. A recording of
        /// the same launch in its playback window already draws the vessel, so the hold ghost
        /// yields to it (the vessel is never drawn twice).
        /// </summary>
        internal static string DecideHideReason(
            bool hasSnapshot, bool chainActive, double currentUT, double firstClaimUT,
            bool coveredByRecording)
        {
            if (!hasSnapshot) return "no-snapshot";
            if (!chainActive) return "chain-inactive";
            if (double.IsNaN(firstClaimUT) || currentUT >= firstClaimUT) return "claimed";
            if (coveredByRecording) return "covered-by-recording";
            return null;
        }

        /// <summary>
        /// True when a recording of vessel <paramref name="pid"/> (launch guids not
        /// conclusively different) is inside its own [StartUT, EndUT] at <paramref name="ut"/>.
        /// </summary>
        internal static bool CoveredByRecording(
            uint pid, string launchGuid, IReadOnlyList<Recording> recordings, double ut)
        {
            if (pid == 0 || recordings == null) return false;
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording rec = recordings[i];
                if (rec == null || rec.VesselPersistentId != pid)
                    continue;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(rec.RecordedVesselGuid, launchGuid))
                    continue;
                if (ut >= rec.StartUT && ut <= rec.EndUT)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the despawn snapshot's pose: LANDED / SPLASHED / PRELAUNCH (or the landed /
        /// splashed flags) sit body-fixed at lat/lon/alt; anything else follows the ORBIT
        /// node. <c>rot</c> is the vessel's surface-relative rotation (ProtoVessel.rotation,
        /// loaded into <c>srfRelRotation</c>). False when the body or the coordinates are
        /// unreadable.
        /// </summary>
        internal static bool TryReadPose(ConfigNode vessel, out ClaimedVesselHoldPose pose)
        {
            pose = new ClaimedVesselHoldPose { BodyIndex = -1 };
            if (vessel == null) return false;
            string sit = vessel.GetValue("sit") ?? "";
            pose.Landed = sit == "LANDED" || sit == "SPLASHED" || sit == "PRELAUNCH"
                || vessel.GetValue("landed") == "True" || vessel.GetValue("splashed") == "True";

            ConfigNode orbit = vessel.GetNode("ORBIT");
            int refIndex;
            if (orbit == null
                || !int.TryParse(orbit.GetValue("REF"), NumberStyles.Integer, CultureInfo.InvariantCulture, out refIndex)
                || refIndex < 0)
                return false;
            pose.BodyIndex = refIndex;

            double[] rot = ParseComponents(vessel.GetValue("rot"));
            if (rot != null && rot.Length == 4)
            {
                pose.HasRotation = true;
                pose.RotX = rot[0];
                pose.RotY = rot[1];
                pose.RotZ = rot[2];
                pose.RotW = rot[3];
            }

            if (!pose.Landed)
                return true;
            return double.TryParse(vessel.GetValue("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out pose.Lat)
                && double.TryParse(vessel.GetValue("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out pose.Lon)
                && double.TryParse(vessel.GetValue("alt"), NumberStyles.Float, CultureInfo.InvariantCulture, out pose.Alt);
        }

        private static double[] ParseComponents(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string[] parts = raw.Split(',');
            var values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            }
            return values;
        }

        internal static string BuildShownMessage(
            uint pid, string vesselName, uint chainPid, bool landed, string bodyName,
            int partCount, double firstClaimUT, double currentUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Claimed vessel hold ghost shown: pid={0} name='{1}' chain={2} source={3} body={4} " +
                "parts={5} firstClaimUT={6:F2} UT={7:F2}",
                pid, vesselName ?? "(unnamed)", chainPid, landed ? "snapshot-surface" : "snapshot-orbit",
                bodyName ?? "(none)", partCount, firstClaimUT, currentUT);
        }

        internal static string BuildReleasedMessage(
            uint pid, string vesselName, string reason, double shownSinceUT, double currentUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Claimed vessel hold ghost released: pid={0} name='{1}' reason={2} shownSinceUT={3:F2} UT={4:F2}",
                pid, vesselName ?? "(unnamed)", reason ?? "(none)", shownSinceUT, currentUT);
        }
    }
}
