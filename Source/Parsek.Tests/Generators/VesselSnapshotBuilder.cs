using System;
using System.Globalization;
using UnityEngine;

namespace Parsek.Tests.Generators
{
    public class VesselSnapshotBuilder
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private string name = "Vessel";
        private uint persistentId = 1000000;
        // KSP's vessel-level launch guid (VESSEL.pid). Null keeps the historical value
        // derived from persistentId, so every pre-existing caller is unchanged.
        private string launchGuid;
        private string type = "Ship";
        private string sit = "LANDED";
        private double lat, lon, alt;
        private bool landed = true;
        private bool splashed;

        // VESSEL.rot is ProtoVessel.rotation, which ProtoVessel.Load() assigns straight to
        // vesselRef.srfRelRotation - so this is the vessel's SURFACE-RELATIVE rotation, never
        // a world rotation. Identity by default so every pre-existing caller is unchanged.
        private Quaternion surfaceRelativeRotation = Quaternion.identity;

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

        public static VesselSnapshotBuilder EvaKerbal(string kerbalName, uint pid = 1000000)
        {
            var b = new VesselSnapshotBuilder();
            b.name = kerbalName;
            b.persistentId = pid;
            b.type = "EVA";
            b.AddPart("kerbalEVA", kerbalName);
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

        /// <summary>
        /// A post-claw-grab merged vessel (M-MIS-10 archetype 4): crewed pod + Advanced
        /// Grabbing Unit + the grabbed PotatoRoid asteroid coupled through the claw.
        /// Part names are the stock RUNTIME names ("GrapplingDevice" is the AGU cfg name;
        /// "PotatoRoid" has no underscore so cfg and runtime names coincide).
        /// </summary>
        public static VesselSnapshotBuilder ClawedAsteroidShip(string name, string crew, uint pid)
        {
            var b = new VesselSnapshotBuilder();
            b.name = name;
            b.persistentId = pid;
            b.AddPart("mk1pod.v2", crew);                                      // index 0: root
            b.AddPart("GrapplingDevice", position: "0,-1.0,0");                // index 1: the claw
            b.AddPart("PotatoRoid", position: "0,-3.2,0", parentIndex: 1);     // index 2: grabbed asteroid

            var parts = b.partsContainer.GetNodes("PART");
            parts[0].AddValue("attN", "bottom, 1");
            parts[1].AddValue("attN", "top, 0");
            // The asteroid has no attach nodes; KSP couples it through the claw's grapple
            // joint, so only the parent index (1 = the claw) models the coupling here.

            return b;
        }

        public static VesselSnapshotBuilder ReentryCapsule(string name, string crew, uint pid)
        {
            var b = new VesselSnapshotBuilder();
            b.name = name;
            b.persistentId = pid;
            b.AddPart("mk1pod.v2", crew);                                      // index 0: root
            b.AddPart("HeatShield1", position: "0,-0.5,0");                   // index 1: heat shield below
            b.AddPart("parachuteSingle", position: "0,0.657,0");               // index 2: parachute above

            var parts = b.partsContainer.GetNodes("PART");
            parts[0].AddValue("attN", "bottom, 1");
            parts[0].AddValue("attN", "top, 2");
            parts[1].AddValue("attN", "top, 0");
            parts[2].AddValue("attN", "bottom, 0");

            return b;
        }

        /// <summary>
        /// A CommNet relay satellite for ghost CommNet relay fixtures (design 15.6): the
        /// RC-L01 Remote Guidance Unit (<c>probeStackLarge</c>, runtime name; its prefab
        /// carries an INTERNAL 5000 antenna and a <c>ModuleProbeControlPoint</c> with
        /// minimumCrew=1 multiHop=True) with an RA-2 relay antenna (<c>RelayAntenna5</c>,
        /// RELAY 2e9 combinable) on top. Antenna power is prefab-only: the snapshot MODULE
        /// nodes carry only the persisted <c>canComm</c> state, exactly as KSP saves them.
        /// Pass <paramref name="pilot"/> to add a crewed Mk1 pod (<c>mk1pod.v2</c>) so the
        /// RC-L01 qualifies as a control point when that kerbal is a pilot in the roster.
        /// <paramref name="relayPart"/> picks a different fixed relay antenna (for example
        /// <c>RelayAntenna100</c>, the RA-100, RELAY 1e11).
        /// </summary>
        public static VesselSnapshotBuilder RelaySatellite(string name, uint pid, string pilot = null,
            string relayPart = "RelayAntenna5")
        {
            var b = new VesselSnapshotBuilder();
            b.name = name;
            b.persistentId = pid;
            b.type = "Relay";
            b.AddPart("probeStackLarge");                                          // index 0: root
            b.AddModuleToPart(0, "ModuleProbeControlPoint");
            b.AddModuleToPart(0, "ModuleCommand");
            b.AddModuleToPart(0, "ModuleDataTransmitter", ("xmitIncomplete", "False"), ("canComm", "True"));
            b.AddPart(relayPart, position: "0,0.6,0", parentIndex: 0);            // index 1
            b.AddModuleToPart(1, "ModuleDataTransmitter", ("xmitIncomplete", "False"), ("canComm", "True"));
            if (pilot != null)
            {
                b.AddPart("mk1pod.v2", pilot, position: "0,-1.0,0", parentIndex: 0);  // index 2
                b.AddModuleToPart(2, "ModuleCommand");
                b.AddModuleToPart(2, "ModuleDataTransmitter", ("xmitIncomplete", "False"), ("canComm", "True"));
            }
            return b;
        }

        /// <summary>
        /// A crewed probe control point with NO relay antenna (ghost CommNet lanes, design
        /// 15.6 scenario 3): the RC-L01 (<c>probeStackLarge</c>, INTERNAL 5000 antenna,
        /// <c>ModuleProbeControlPoint</c> minimumCrew=1 multiHop=True) plus a Mk1 pod
        /// (<c>mk1pod.v2</c>, INTERNAL antenna) seating <paramref name="pilot"/>. It is a
        /// control source when that kerbal is a pilot in the roster, and it can never relay.
        /// </summary>
        public static VesselSnapshotBuilder ControlPointSatellite(string name, uint pid, string pilot)
        {
            var b = new VesselSnapshotBuilder();
            b.name = name;
            b.persistentId = pid;
            b.type = "Probe";
            b.AddPart("probeStackLarge");                                          // index 0: root
            b.AddModuleToPart(0, "ModuleProbeControlPoint");
            b.AddModuleToPart(0, "ModuleCommand");
            b.AddModuleToPart(0, "ModuleDataTransmitter", ("xmitIncomplete", "False"), ("canComm", "True"));
            b.AddPart("mk1pod.v2", pilot, position: "0,-1.0,0", parentIndex: 0);   // index 1
            b.AddModuleToPart(1, "ModuleCommand");
            b.AddModuleToPart(1, "ModuleDataTransmitter", ("xmitIncomplete", "False"), ("canComm", "True"));
            return b;
        }

        /// <summary>
        /// A relay satellite whose relay antenna is DEPLOYABLE (design 15.6 scenario 9): the
        /// RC-L01 root plus the HG-5 High Gain Antenna (<c>HighGainAntenna5.v2</c>, runtime
        /// name of <c>HighGainAntenna5_v2</c>: RELAY 5e6 combinable, its
        /// <c>ModuleDataTransmitter</c> gated by <c>DeployFxModules = 0</c>, the part's
        /// <c>ModuleDeployableAntenna</c>). <paramref name="extended"/> writes the persisted
        /// <c>deployState</c>; a recording's deploy events decide the relay window, the
        /// snapshot state only when there are none.
        /// </summary>
        public static VesselSnapshotBuilder DeployableRelaySatellite(string name, uint pid, bool extended)
        {
            var b = new VesselSnapshotBuilder();
            b.name = name;
            b.persistentId = pid;
            b.type = "Relay";
            b.AddPart("probeStackLarge");                                          // index 0: root
            b.AddModuleToPart(0, "ModuleProbeControlPoint");
            b.AddModuleToPart(0, "ModuleCommand");
            b.AddModuleToPart(0, "ModuleDataTransmitter", ("xmitIncomplete", "False"), ("canComm", "True"));
            b.AddPart("HighGainAntenna5.v2", position: "0,0.6,0", parentIndex: 0); // index 1
            b.AddModuleToPart(1, "ModuleDeployableAntenna",
                ("deployState", extended ? "EXTENDED" : "RETRACTED"),
                ("storedAnimationTime", extended ? "1" : "0"),
                ("storedAnimationSpeed", "1"));
            b.AddModuleToPart(1, "ModuleDataTransmitter", ("xmitIncomplete", "False"),
                ("canComm", extended ? "True" : "False"));
            return b;
        }

        public VesselSnapshotBuilder WithName(string n) { name = n; return this; }
        public VesselSnapshotBuilder WithPersistentId(uint pid) { persistentId = pid; return this; }

        /// <summary>
        /// Sets <c>VESSEL.pid</c>, the launch guid KSP reads into <c>Vessel.id</c>. A fixture
        /// that also stamps <c>Recording.RecordedVesselGuid</c> passes the same value (for
        /// example <c>ScenarioWriter.DeriveVesselLaunchGuid(recordingId)</c>) so the snapshot
        /// and the recording agree on launch identity.
        /// </summary>
        public VesselSnapshotBuilder WithLaunchGuid(string guid) { launchGuid = guid; return this; }
        public VesselSnapshotBuilder WithType(string t) { type = t; return this; }
        public VesselSnapshotBuilder WithSituation(string s) { sit = s; return this; }

        /// <summary>
        /// Sets the snapshot's <c>rot</c> value. The argument is the recorded
        /// SURFACE-RELATIVE rotation (<c>Inverse(body.bodyTransform.rotation) *
        /// v.transform.rotation</c>), because that is exactly what KSP reads back out of
        /// <c>VESSEL.rot</c> into <c>Vessel.srfRelRotation</c>. Pass a non-identity value to
        /// build a fixture where the surface-relative and world frames are distinguishable.
        /// </summary>
        public VesselSnapshotBuilder WithSurfaceRelativeRotation(Quaternion srfRelRotation)
        {
            surfaceRelativeRotation = srfRelRotation;
            return this;
        }

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

        /// <summary>
        /// A vessel resting at a surface position whose snapshot was nevertheless captured
        /// with <c>sit = FLYING</c> (<c>landed = False</c>): the bug #169 shape, where an EVA
        /// kerbal or a hopping lander is saved mid-hop and its recording then ends Landed.
        /// Position and the placeholder surface orbit are exactly <see cref="AsLanded"/>'s.
        /// </summary>
        public VesselSnapshotBuilder AsFlyingAtSurface(double latitude, double longitude, double altitude)
        {
            AsLanded(latitude, longitude, altitude);
            sit = "FLYING";
            landed = false;
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
            string position = null, string rotation = null, int parentIndex = 0)
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
            part.AddValue("rotation", rotation ?? "0,0,0,1");
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

        /// <summary>
        /// Adds a RESOURCE node to the part at the given index.
        /// </summary>
        public VesselSnapshotBuilder AddResourceToPart(int partIndex, string name, double amount, double maxAmount)
        {
            var parts = partsContainer.GetNodes("PART");
            if (partIndex < 0 || partIndex >= parts.Length)
                throw new ArgumentOutOfRangeException(nameof(partIndex),
                    $"Part index {partIndex} out of range (0..{parts.Length - 1})");

            var resNode = parts[partIndex].AddNode("RESOURCE");
            resNode.AddValue("name", name);
            resNode.AddValue("amount", amount.ToString("R", IC));
            resNode.AddValue("maxAmount", maxAmount.ToString("R", IC));
            return this;
        }

        /// <summary>
        /// Adds a MODULE node to the part at the given index, with the supplied
        /// persisted key/value pairs. MODULE nodes are appended in call order, which is
        /// the order <c>ProtoPartSnapshot</c> serializes <c>part.Modules</c> in — so a
        /// caller reproducing a real part's module list must add them in prefab order
        /// for any ordinal-sensitive parse (robotic servos) to line up.
        /// </summary>
        public VesselSnapshotBuilder AddModuleToPart(
            int partIndex, string moduleName, params (string key, string value)[] values)
        {
            var parts = partsContainer.GetNodes("PART");
            if (partIndex < 0 || partIndex >= parts.Length)
                throw new ArgumentOutOfRangeException(nameof(partIndex),
                    $"Part index {partIndex} out of range (0..{parts.Length - 1})");

            var moduleNode = parts[partIndex].AddNode("MODULE");
            moduleNode.AddValue("name", moduleName);
            moduleNode.AddValue("isEnabled", "True");
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                    moduleNode.AddValue(values[i].key, values[i].value);
            }
            return this;
        }

        /// <summary>
        /// Stores one cargo part inside the part at <paramref name="partIndex"/>'s
        /// <c>ModuleInventoryPart</c>, in the exact node shape
        /// <c>VesselSnapshotOps.ExtractInventoryPayloadItems</c> walks:
        /// <c>PART -&gt; MODULE(name="ModuleInventoryPart") -&gt; STOREDPARTS -&gt; STOREDPART</c>.
        /// <para>
        /// The STOREDPART carries the keys the production parse reads —
        /// <c>partName</c> (required; a STOREDPART without it is dropped),
        /// <c>quantity</c> (absent =&gt; 1), and the optional
        /// <c>variantName</c> — plus <c>slotIndex</c> / <c>stackCapacity</c>
        /// for on-disk fidelity. Stored resources go on an inner <c>PART</c>
        /// node exactly as KSP writes them, which is where
        /// <c>ExtractStoredPartResourceManifest</c> looks (it also accepts
        /// RESOURCE nodes directly on the STOREDPART).
        /// </para>
        /// <para>
        /// Repeated calls against the SAME <paramref name="partIndex"/> append
        /// to the ONE inventory module's STOREDPARTS list — production emits a
        /// single <c>ModuleInventoryPart</c> per part, and
        /// <c>ExtractInventoryPayloadItems</c> aggregates identical payload
        /// identities by summing quantity + slots, so a second module would
        /// silently change the extracted <c>SlotsTaken</c>.
        /// </para>
        /// <para>
        /// Note that <c>slot</c> and <c>quantity</c> never enter
        /// <c>VesselSnapshotOps.ComputeInventoryPayloadKindKey</c>, and neither does
        /// module state: the KIND is part name + variant + per-resource fill
        /// bucket, and nothing else (2026-09-02 ruling). Vary
        /// <paramref name="storedPartName"/>, <paramref name="variantName"/>, or
        /// push a stored resource across a fill bucket (empty / partial / full)
        /// to author a DIFFERENT kind.
        /// </para>
        /// </summary>
        /// <param name="partIndex">Index of the host part (call order of <see cref="AddPart"/>).</param>
        /// <param name="storedPartName">Runtime part name of the cargo item (dot-form, e.g. <c>evaChute</c>).</param>
        /// <param name="slot">Inventory slot index the item occupies.</param>
        /// <param name="quantity">Stack size stored in that slot.</param>
        /// <param name="variantName">Optional part variant; part of the payload identity.</param>
        /// <param name="stackCapacity">Declared stack capacity of the slot (on-disk fidelity only).</param>
        /// <param name="storedResources">Optional resources carried inside the stored part.</param>
        public VesselSnapshotBuilder AddStoredPartToInventory(
            int partIndex,
            string storedPartName,
            int slot = 0,
            int quantity = 1,
            string variantName = null,
            int stackCapacity = 1,
            params (string name, double amount, double maxAmount)[] storedResources)
        {
            var parts = partsContainer.GetNodes("PART");
            if (partIndex < 0 || partIndex >= parts.Length)
                throw new ArgumentOutOfRangeException(nameof(partIndex),
                    $"Part index {partIndex} out of range (0..{parts.Length - 1})");
            if (string.IsNullOrEmpty(storedPartName))
                throw new ArgumentException(
                    "storedPartName is required — production drops a STOREDPART with no partName",
                    nameof(storedPartName));

            ConfigNode inventoryModule = FindInventoryModule(parts[partIndex]);
            if (inventoryModule == null)
            {
                inventoryModule = parts[partIndex].AddNode("MODULE");
                inventoryModule.AddValue("name", "ModuleInventoryPart");
                inventoryModule.AddValue("isEnabled", "True");
                // Not read by the Parsek parse; present because a real
                // ModuleInventoryPart snapshot always carries it.
                inventoryModule.AddValue("InventorySlots", "4");
            }

            ConfigNode storedPartsNode = inventoryModule.GetNode("STOREDPARTS")
                ?? inventoryModule.AddNode("STOREDPARTS");

            ConfigNode storedPart = storedPartsNode.AddNode("STOREDPART");
            storedPart.AddValue("slotIndex", slot.ToString(IC));
            storedPart.AddValue("partName", storedPartName);
            storedPart.AddValue("quantity", quantity.ToString(IC));
            storedPart.AddValue("stackCapacity", stackCapacity.ToString(IC));
            if (!string.IsNullOrEmpty(variantName))
                storedPart.AddValue("variantName", variantName);

            if (storedResources != null && storedResources.Length > 0)
            {
                ConfigNode innerPart = storedPart.AddNode("PART");
                innerPart.AddValue("name", storedPartName);
                for (int i = 0; i < storedResources.Length; i++)
                {
                    ConfigNode res = innerPart.AddNode("RESOURCE");
                    res.AddValue("name", storedResources[i].name);
                    res.AddValue("amount", storedResources[i].amount.ToString("R", IC));
                    res.AddValue("maxAmount", storedResources[i].maxAmount.ToString("R", IC));
                }
            }

            return this;
        }

        private static ConfigNode FindInventoryModule(ConfigNode partNode)
        {
            ConfigNode[] modules = partNode.GetNodes("MODULE");
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") == "ModuleInventoryPart")
                    return modules[i];
            }
            return null;
        }

        private static string D(double v) => v.ToString("R", IC);

        private static string F(float v) => v.ToString("R", IC);

        /// <summary>
        /// Serializes a quaternion the way KSP writes <c>VESSEL.rot</c>: four comma-separated
        /// components, x/y/z/w. Formatted through InvariantCulture so a comma-decimal machine
        /// locale cannot turn one component into two tokens. Identity renders as the historical
        /// literal "0,0,0,1", so snapshots built without an explicit rotation are unchanged.
        /// </summary>
        private static string Q(Quaternion q) =>
            F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w);

        public ConfigNode Build()
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("pid", launchGuid ?? persistentId.ToString("x8").PadLeft(32, '0'));
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
            v.AddValue("rot", Q(surfaceRelativeRotation));
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
