using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests.Helpers
{
    /// <summary>
    /// Builds minimum-viable <c>VESSEL</c> ConfigNodes for synthetic <c>kerbalEVA</c>
    /// vessels used by in-game tests. Parallel to the test-assembly
    /// <c>Parsek.Tests.Generators.VesselSnapshotBuilder.EvaKerbal</c> — the in-game test
    /// runner lives in the mod assembly and cannot reference the test project.
    ///
    /// The field set intentionally mirrors <c>VesselSnapshotBuilder.AddPart</c> /
    /// <c>Build</c> so a snapshot produced here survives <c>ProtoVessel.Load()</c>
    /// without KSP throwing on missing fields. Used by <c>EvaSpawnPositionTests</c>
    /// (#264) to drive <c>VesselSpawner.SpawnOrRecoverIfTooClose</c> without scripting
    /// a live EVA.
    /// </summary>
    internal static class InGameKerbalEvaSnapshot
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Build a VESSEL ConfigNode for a 1-part <c>kerbalEVA</c> vessel at the given
        /// surface position with the given crew member assigned.
        /// </summary>
        internal static ConfigNode Build(
            string crewName,
            double lat, double lon, double alt,
            int referenceBodyIndex,
            uint persistentId)
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("pid", persistentId.ToString("x8").PadLeft(32, '0'));
            v.AddValue("persistentId", persistentId.ToString(IC));
            v.AddValue("name", crewName);
            v.AddValue("type", "EVA");
            // Start as FLYING — SpawnAtPosition will recompute and override via the new
            // OverrideSituationFromTerminalState helper when terminalState=Landed is passed.
            v.AddValue("sit", "FLYING");
            v.AddValue("landed", "False");
            v.AddValue("splashed", "False");
            v.AddValue("met", "0");
            v.AddValue("lct", "0");
            v.AddValue("lastUT", "-1");
            v.AddValue("root", "0");
            v.AddValue("lat", lat.ToString("R", IC));
            v.AddValue("lon", lon.ToString("R", IC));
            v.AddValue("alt", alt.ToString("R", IC));
            v.AddValue("hgt", "-1");
            v.AddValue("nrm", "0,1,0");
            v.AddValue("rot", "0,0,0,1");
            v.AddValue("CoM", "0,0,0");
            v.AddValue("stg", "0");
            v.AddValue("prst", "True");
            v.AddValue("ref", "0");
            v.AddValue("ctrl", "False");
            v.AddValue("GroupOverride", "0");
            v.AddValue("OverrideDefault", "False,False,False,False");
            v.AddValue("OverrideActionControl", "0,0,0,0");
            v.AddValue("OverrideAxisControl", "0,0,0,0");
            v.AddValue("OverrideGroupNames", ",,,");

            // Minimal ORBIT subnode. SpawnAtPosition rebuilds this from lat/lon/alt+velocity,
            // so the values here are placeholders, but ProtoVessel.Load still requires the
            // subnode to exist with a valid REF.
            var orbit = v.AddNode("ORBIT");
            orbit.AddValue("SMA", "600000");
            orbit.AddValue("ECC", "0");
            orbit.AddValue("INC", "0");
            orbit.AddValue("LPE", "0");
            orbit.AddValue("LAN", "0");
            orbit.AddValue("MNA", "0");
            orbit.AddValue("EPH", "0");
            orbit.AddValue("REF", referenceBodyIndex.ToString(IC));

            // Single kerbalEVA part. Field set mirrors VesselSnapshotBuilder.AddPart.
            var part = v.AddNode("PART");
            uint uid = 100000;
            part.AddValue("name", "kerbalEVA");
            part.AddValue("cid", "0");
            part.AddValue("uid", uid.ToString(IC));
            part.AddValue("mid", uid.ToString(IC));
            part.AddValue("persistentId", uid.ToString(IC));
            part.AddValue("launchID", "1");
            part.AddValue("parent", "0");
            part.AddValue("position", "0,0,0");
            part.AddValue("rotation", "0,0,0,1");
            part.AddValue("mirror", "1,1,1");
            part.AddValue("symMethod", "Radial");
            part.AddValue("istg", "0");
            part.AddValue("resPri", "0");
            part.AddValue("dstg", "0");
            part.AddValue("sqor", "0");
            part.AddValue("sepI", "0");
            part.AddValue("sidx", "0");
            part.AddValue("attm", "0");
            part.AddValue("sameVesselCollision", "False");
            part.AddValue("srfN", "None, -1");
            part.AddValue("mass", "0.094");
            part.AddValue("shielded", "False");
            part.AddValue("temp", "300");
            part.AddValue("tempExt", "300");
            part.AddValue("expt", "0.5");
            part.AddValue("state", "0");
            part.AddValue("attached", "True");
            part.AddValue("autostrutMode", "Off");
            part.AddValue("rigidAttachment", "False");
            part.AddValue("flag", "");
            part.AddValue("rTrf", "kerbalEVA");
            part.AddValue("crew", crewName);

            // KerbalEVA FSM state — mirrors StripEvaLadderState's expected idle-ish state.
            var module = part.AddNode("MODULE");
            module.AddValue("name", "KerbalEVA");
            module.AddValue("isEnabled", "True");
            module.AddValue("state", "st_idle_gr");
            module.AddValue("OnALadder", "False");

            // Required empty sub-nodes for ProtoVessel.Load to not throw
            v.AddNode("ACTIONGROUPS");
            var disc = v.AddNode("DISCOVERY");
            disc.AddValue("state", "29");
            disc.AddValue("lastObservedTime", "0");
            disc.AddValue("lifetime", "Infinity");
            disc.AddValue("refTime", "0");
            disc.AddValue("size", "2");
            v.AddNode("FLIGHTPLAN");
            v.AddNode("CTRLSTATE");
            v.AddNode("VESSELMODULES");

            return v;
        }
    }
}
