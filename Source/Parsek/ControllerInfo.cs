using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Describes a controller part on a vessel segment.
    /// Stored at segment start to identify what gives the vessel command authority.
    /// </summary>
    public struct ControllerInfo
    {
        public string type;              // "CrewedPod", "ExternalSeat", "ProbeCore", "KerbalEVA"
        public string partName;          // Part cfg name (e.g., "mk1pod.v2")
        public uint partPersistentId;    // Part's persistentId

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Controller type={0} part={1} pid={2}", type, partName, partPersistentId);
        }

        /// <summary>
        /// Captures the controllable part PIDs of a live vessel as a fresh
        /// <see cref="ControllerInfo"/> list. Called once at recording-start time
        /// (active recorder start, split-branch child birth, fresh-post-switch root)
        /// to pin the recorded controllable identity; never re-invoked on stop/remnant
        /// vessels — the captured list flows forward through copy/clone paths so a
        /// surviving 1-part remnant after destructive breakup cannot overwrite it.
        /// Returns null when the vessel is null or has no parts; returns an empty
        /// list when the vessel exists but exposes no controller parts (debris-shaped
        /// craft). EVA kerbals contribute a single <c>KerbalEVA</c> entry; external
        /// seats contribute <c>ExternalSeat</c>; command-bearing parts split between
        /// <c>CrewedPod</c> (any crew aboard) and <c>ProbeCore</c> (uncrewed).
        /// </summary>
        public static List<ControllerInfo> CaptureFromVessel(Vessel v)
        {
            if (v == null || v.parts == null)
                return null;

            var list = new List<ControllerInfo>();
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                string type = ClassifyControllerType(p);
                if (type == null) continue;

                list.Add(new ControllerInfo
                {
                    type = type,
                    partName = p.partInfo?.name ?? p.name ?? string.Empty,
                    partPersistentId = p.persistentId
                });
            }
            return list;
        }

        private static string ClassifyControllerType(Part p)
        {
            // KerbalEVA comes first — the EVA part also carries a ModuleCommand-like
            // capability in some KSP versions, but it is not a ModuleCommand instance,
            // and the EVA kerbal contract distinguishes it from probe cores.
            if (p.FindModuleImplementing<KerbalEVA>() != null)
                return "KerbalEVA";

            if (p.FindModuleImplementing<ModuleCommand>() != null)
            {
                bool hasCrew = p.protoModuleCrew != null && p.protoModuleCrew.Count > 0;
                return hasCrew ? "CrewedPod" : "ProbeCore";
            }

            // Note: `KerbalSeat` is captured here as start-of-recording controllable
            // identity, but `ParsekFlight.IsTrackableVessel` deliberately does NOT
            // treat a bare seat (no ModuleCommand, not EVA, not SpaceObject) as
            // trackable. The asymmetry is intentional: "what we recorded as
            // controllable" can be slightly broader than "what KSP would still let
            // the player control as a live vessel". If the only recorded controller
            // is a `KerbalSeat` and the seat dies, `ShouldClassifyRecordedIdentityLost`
            // still fires correctly because the live trackability check also fails.
            if (p.FindModuleImplementing<KerbalSeat>() != null)
                return "ExternalSeat";

            return null;
        }
    }
}
