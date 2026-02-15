using System;
using System.Globalization;

namespace Parsek.Tests.Generators
{
    public class VesselSnapshotBuilder
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private string name = "Vessel";
        private uint persistentId = 1000000;
        private string type = "Ship";
        private string sit = "LANDED";
        private double lat, lon, alt;
        private bool landed = true;
        private bool splashed;

        // Orbit params
        private double sma, ecc, inc, lan, argPe, mna, epoch;
        private int refBody = 1; // Kerbin

        // Parts
        private readonly ConfigNode partsContainer = new ConfigNode("_parts");

        public VesselSnapshotBuilder() { }

        public static VesselSnapshotBuilder CrewedShip(string name, string crewMember, uint pid = 1000000)
        {
            var b = new VesselSnapshotBuilder();
            b.name = name;
            b.persistentId = pid;
            b.AddPart("mk1pod.v2", crewMember);
            return b;
        }

        public static VesselSnapshotBuilder ProbeShip(string name, uint pid = 1000000)
        {
            var b = new VesselSnapshotBuilder();
            b.name = name;
            b.persistentId = pid;
            b.type = "Probe";
            b.AddPart("probeCoreSphere");
            return b;
        }

        public static VesselSnapshotBuilder FleaRocket(string name, string crew, uint pid)
        {
            var b = new VesselSnapshotBuilder();
            b.name = name;
            b.persistentId = pid;
            // Real part positions from KSP save data (Y-up vessel-local coords)
            b.AddPart("mk1pod.v2", crew);                                      // index 0: root
            b.AddPart("solidBooster.sm.v2", position: "0,-1.163,0");           // index 1: below pod
            b.AddPart("parachuteSingle", position: "0,0.657,0");               // index 2: above pod

            // Attachment nodes for proper part tree
            var parts = b.partsContainer.GetNodes("PART");
            parts[0].AddValue("attN", "bottom, 1");
            parts[0].AddValue("attN", "top, 2");
            parts[1].AddValue("attN", "top, 0");
            parts[2].AddValue("attN", "bottom, 0");

            return b;
        }

        public VesselSnapshotBuilder WithName(string n) { name = n; return this; }
        public VesselSnapshotBuilder WithPersistentId(uint pid) { persistentId = pid; return this; }
        public VesselSnapshotBuilder WithType(string t) { type = t; return this; }

        public VesselSnapshotBuilder AsLanded(double latitude, double longitude, double altitude)
        {
            sit = "LANDED";
            landed = true;
            splashed = false;
            lat = latitude;
            lon = longitude;
            alt = altitude;
            // KSP landed convention
            sma = 0; ecc = 1; inc = 0; lan = 0; argPe = 0; mna = 0; epoch = 0;
            return this;
        }

        public VesselSnapshotBuilder AsOrbiting(double sma, double ecc, double inc,
            double lan = 0, double argPe = 0, double mna = 0, double epoch = 0,
            int refBody = 1)
        {
            sit = "ORBITING";
            landed = false;
            splashed = false;
            this.sma = sma;
            this.ecc = ecc;
            this.inc = inc;
            this.lan = lan;
            this.argPe = argPe;
            this.mna = mna;
            this.epoch = epoch;
            this.refBody = refBody;
            lat = 0; lon = 0; alt = sma - 600000; // rough surface alt
            return this;
        }

        public VesselSnapshotBuilder AddPart(string partName, string crew = null,
            string position = null, int parentIndex = 0)
        {
            var part = new ConfigNode("PART");
            uint uid = (uint)(100000 + partsContainer.CountNodes * 1111);
            part.AddValue("name", partName);
            part.AddValue("cid", partsContainer.CountNodes.ToString(IC));
            part.AddValue("uid", uid.ToString(IC));
            part.AddValue("mid", uid.ToString(IC));
            part.AddValue("persistentId", uid.ToString(IC));
            part.AddValue("launchID", "1");
            part.AddValue("parent", parentIndex.ToString(IC));
            part.AddValue("position", position ?? "0,0,0");
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
            part.AddValue("mass", "1");
            part.AddValue("shielded", "False");
            part.AddValue("temp", "300");
            part.AddValue("tempExt", "300");
            part.AddValue("expt", "0.5");
            part.AddValue("state", "0");
            part.AddValue("attached", "True");
            part.AddValue("autostrutMode", "Off");
            part.AddValue("rigidAttachment", "False");
            part.AddValue("flag", "");
            part.AddValue("rTrf", partName);

            if (crew != null)
                part.AddValue("crew", crew);

            partsContainer.AddNode(part);
            return this;
        }

        private static string D(double v) => v.ToString("R", IC);

        public ConfigNode Build()
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("pid", persistentId.ToString("x8").PadLeft(32, '0'));
            v.AddValue("persistentId", persistentId.ToString(IC));
            v.AddValue("name", name);
            v.AddValue("type", type);
            v.AddValue("sit", sit);
            v.AddValue("landed", landed ? "True" : "False");
            v.AddValue("splashed", splashed ? "True" : "False");
            v.AddValue("met", "0");
            v.AddValue("lct", "0");
            v.AddValue("lastUT", "-1");
            v.AddValue("root", "0");
            v.AddValue("lat", D(lat));
            v.AddValue("lon", D(lon));
            v.AddValue("alt", D(alt));
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

            // ORBIT sub-node
            var orbit = v.AddNode("ORBIT");
            orbit.AddValue("SMA", D(sma));
            orbit.AddValue("ECC", D(ecc));
            orbit.AddValue("INC", D(inc));
            orbit.AddValue("LPE", D(argPe));
            orbit.AddValue("LAN", D(lan));
            orbit.AddValue("MNA", D(mna));
            orbit.AddValue("EPH", D(epoch));
            orbit.AddValue("REF", refBody.ToString(IC));

            // PART sub-nodes
            foreach (ConfigNode part in partsContainer.GetNodes("PART"))
                v.AddNode(part);

            // Required sub-nodes
            v.AddNode("ACTIONGROUPS");

            var disc = v.AddNode("DISCOVERY");
            // Must match (int)DiscoveryLevels.Owned — VesselSpawner.EnsureSpawnReadiness
            // overwrites this at spawn time, but tests that bypass spawning need a valid value
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
