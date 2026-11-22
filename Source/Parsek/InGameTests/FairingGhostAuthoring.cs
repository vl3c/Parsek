using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    internal enum FairingNoseShape { Capped, OpenTop }

    internal enum FairingSkirtKind { Fairing, EnginePlate, EngineShroud }

    /// <summary>One of FGV's six stock configurations (design-part-ghost-parity.md D.10 S8):
    /// four procedural fairings and two engine skirts.</summary>
    internal sealed class FairingGhostConfig
    {
        public int Number;
        public string Name;
        public string FairingPart;
        // Heights in units of the part's baseRadius; radii in units of baseRadius except
        // the last one of a capped shape, which is the part's capRadius exactly.
        public double[] HeightsOverBase;
        public double[] RadiiOverBase;
        public FairingNoseShape Nose;
        public bool Interstage;
        public bool ColourVariant;
        public bool PayloadOnTop;
        public double AlongTrackMeters;
        public double CameraElevationDeg;
        public FairingSkirtKind Kind;
    }

    /// <summary>One authored FGV vessel: its node and what the authoring decided.</summary>
    internal sealed class FairingAuthoredVessel
    {
        internal ConfigNode Node;
        internal double MaxFairingRadius;
        internal string VariantName;

        /// <summary>The subject part (the fairing, the plate or the engine): its flight id.</summary>
        internal uint SubjectFlightId;

        /// <summary>Every other authored part as (the subject node it sits on, its part name,
        /// its flight id), in authoring order.</summary>
        internal readonly List<KeyValuePair<string, string>> ContextParts = new List<KeyValuePair<string, string>>();
        internal readonly List<uint> ContextFlightIds = new List<uint>();

        /// <summary>The part that leaves at FGV's separation (the interstage's upper stage,
        /// the part below a skirt); 0 when the configuration does not separate.</summary>
        internal uint SeparatingFlightId;
    }

    /// <summary>
    /// The vessel authoring of FGV-1's six stock configurations, shared by the FGV in-game
    /// test and the part parity sweep's <c>fgv-&lt;n&gt;-&lt;name&gt;</c> contexts (design
    /// D.10 S8), so both photograph the same craft. Every part through
    /// <see cref="GhostCoOrbitalRig.AuthorPartNode"/>; the log lines go to the caller's tag.
    /// </summary>
    internal static class FairingGhostAuthoring
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal static readonly string[] PayloadProbeCandidates = { "probeCoreOcto.v2", "probeCoreOcto", "probeCoreSphere.v2" };
        internal static readonly string[] UpperStageCandidates = { "fuelTankSmall", "fuelTankSmallFlat", "fuelTank" };
        internal static readonly string[] PlateCandidates = { "EnginePlate2", "EnginePlate1p5" };
        internal static readonly string[] PlateEngineCandidates = { "liquidEngine3.v2", "liquidEngine3" };
        internal static readonly string[] ShroudEngineCandidates = { "liquidEngine2.v2", "liquidEngine2", "liquidEngine.v2" };
        internal static readonly string[] SkirtTankCandidates = { "fuelTank", "fuelTankSmall" };
        internal static readonly string[] SkirtLowerCandidates = { "fuelTankSmallFlat", "fuelTankSmall" };

        /// <summary>FGV's four fairing configurations (cell 1).</summary>
        internal static List<FairingGhostConfig> FairingConfigs()
        {
            return new List<FairingGhostConfig>
            {
                new FairingGhostConfig
                {
                    Number = 1, Name = "plain-capped", FairingPart = "fairingSize1",
                    HeightsOverBase = new[] { 0.0, 1.6, 3.2, 4.4 },
                    RadiiOverBase = new[] { 1.0, 1.25, 1.25, 0.0 },
                    Nose = FairingNoseShape.Capped, PayloadOnTop = true,
                    AlongTrackMeters = 70.0, CameraElevationDeg = 12.0,
                },
                new FairingGhostConfig
                {
                    Number = 2, Name = "interstage-truss", FairingPart = "fairingSize2",
                    HeightsOverBase = new[] { 0.0, 0.8, 2.1, 2.9 },
                    RadiiOverBase = new[] { 1.0, 1.05, 1.05, 0.0 },
                    Nose = FairingNoseShape.Capped, Interstage = true,
                    AlongTrackMeters = -70.0, CameraElevationDeg = 18.0,
                },
                new FairingGhostConfig
                {
                    Number = 3, Name = "open-top", FairingPart = "fairingSize2",
                    HeightsOverBase = new[] { 0.0, 1.0, 2.0 },
                    RadiiOverBase = new[] { 1.0, 1.05, 1.05 },
                    Nose = FairingNoseShape.OpenTop,
                    AlongTrackMeters = 140.0, CameraElevationDeg = 25.0,
                },
                new FairingGhostConfig
                {
                    Number = 4, Name = "colour-variant", FairingPart = "fairingSize1",
                    HeightsOverBase = new[] { 0.0, 1.6, 3.2, 4.4 },
                    RadiiOverBase = new[] { 1.0, 1.25, 1.25, 0.0 },
                    Nose = FairingNoseShape.Capped, ColourVariant = true, PayloadOnTop = true,
                    AlongTrackMeters = -140.0, CameraElevationDeg = 12.0,
                },
            };
        }

        /// <summary>FGV's two engine skirt configurations (cell 2).</summary>
        internal static List<FairingGhostConfig> SkirtConfigs()
        {
            return new List<FairingGhostConfig>
            {
                new FairingGhostConfig
                {
                    Number = 5, Name = "engine-plate", Kind = FairingSkirtKind.EnginePlate,
                    AlongTrackMeters = 70.0, CameraElevationDeg = 15.0,
                },
                new FairingGhostConfig
                {
                    Number = 6, Name = "engine-shroud", Kind = FairingSkirtKind.EngineShroud,
                    AlongTrackMeters = -70.0, CameraElevationDeg = 12.0,
                },
            };
        }

        /// <summary>The configuration with FGV number <paramref name="number"/>; null when none.</summary>
        internal static FairingGhostConfig ByNumber(int number)
        {
            foreach (var c in FairingConfigs()) if (c.Number == number) return c;
            foreach (var c in SkirtConfigs()) if (c.Number == number) return c;
            return null;
        }

        /// <summary>Either kind: <see cref="BuildFairingVessel"/> for a fairing,
        /// <see cref="BuildSkirtVessel"/> for a skirt.</summary>
        internal static FairingAuthoredVessel Build(FairingGhostConfig cfg, Vessel anchor, string tag,
            AvailablePart subject, bool subjectAsRoot, out string failReason)
            => cfg.Kind == FairingSkirtKind.Fairing
                ? BuildFairingVessel(cfg, anchor, tag, out failReason)
                : BuildSkirtVessel(cfg, anchor, tag, subject, subjectAsRoot, out failReason);

        internal static AvailablePart FirstKnownPart(string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                AvailablePart info = PartLoader.getPartInfoByName(candidates[i]);
                if (info != null && info.partPrefab != null)
                    return info;
            }
            return null;
        }

        internal static string FormatVector(Vector3 v)
        {
            return "(" + v.x.ToString("F3", IC) + "," + v.y.ToString("F3", IC) + "," + v.z.ToString("F3", IC) + ")";
        }

        /// <summary>
        /// A skirt vessel. The engine plate: plate (root, mount set Double), probe on top, tank
        /// on the bottom, two engines on N2_1 / N2_2. The engine shroud: tank &gt; engine &gt;
        /// lower part, or with <paramref name="subjectAsRoot"/> the engine as the root with the
        /// tank on its top node and the lower part on its bottom node (the same craft; the
        /// sweep's subject is its vessel's root). <paramref name="subject"/> replaces FGV's
        /// plate / engine candidate pick when given.
        /// </summary>
        internal static FairingAuthoredVessel BuildSkirtVessel(FairingGhostConfig cfg, Vessel anchor, string tag,
            AvailablePart subject, bool subjectAsRoot, out string failReason)
        {
            failReason = null;
            var result = new FairingAuthoredVessel();
            var parts = new List<ConfigNode>();
            if (cfg.Kind == FairingSkirtKind.EnginePlate)
            {
                AvailablePart plate = subject ?? FirstKnownPart(PlateCandidates);
                AvailablePart probe = FirstKnownPart(PayloadProbeCandidates);
                AvailablePart tank = FirstKnownPart(SkirtLowerCandidates);
                AvailablePart engine = FirstKnownPart(PlateEngineCandidates);
                if (plate == null) { failReason = "no part known (" + string.Join(",", PlateCandidates) + ")"; return null; }
                if (probe == null || tank == null || engine == null) { failReason = "probe, tank or engine part missing"; return null; }
                Part pp = plate.partPrefab;
                AttachNode top = pp.FindAttachNode("top"), bottom = pp.FindAttachNode("bottom");
                AttachNode n1 = pp.FindAttachNode("N2_1"), n2 = pp.FindAttachNode("N2_2");
                AttachNode probeBottom = probe.partPrefab.FindAttachNode("bottom");
                AttachNode tankTop = tank.partPrefab.FindAttachNode("top");
                AttachNode engTop = engine.partPrefab.FindAttachNode("top");
                if (top == null || bottom == null || n1 == null || n2 == null || probeBottom == null || tankTop == null || engTop == null)
                {
                    failReason = "attach nodes missing on plate / probe / tank / engine";
                    return null;
                }
                var ids = GhostCoOrbitalRig.UniqueFlightIds(5);
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(plate, ids[0], 0, Vector3.zero,
                    new Dictionary<string, int> { { "top", 1 }, { "bottom", 2 }, { "N2_1", 3 }, { "N2_2", 4 } }, null,
                    (module, name) =>
                    {
                        if (name == "ModuleDynamicNodes")
                        {
                            module.AddValue("setIndex", "1");
                            module.AddValue("NodeSetIdx", "1");
                        }
                    }));
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(probe, ids[1], 0, top.position - probeBottom.position,
                    new Dictionary<string, int> { { "bottom", 0 } }, null, null));
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(tank, ids[2], 0, bottom.position - tankTop.position,
                    new Dictionary<string, int> { { "top", 0 } }, null, null));
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(engine, ids[3], 0, n1.position - engTop.position,
                    new Dictionary<string, int> { { "top", 0 } }, null, null));
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(engine, ids[4], 0, n2.position - engTop.position,
                    new Dictionary<string, int> { { "top", 0 } }, null, null));
                result.MaxFairingRadius = 1.4;
                result.SubjectFlightId = ids[0];
                AddContext(result, "top", probe.name, ids[1]);
                AddContext(result, "bottom", tank.name, ids[2]);
                AddContext(result, "N2_1", engine.name, ids[3]);
                AddContext(result, "N2_2", engine.name, ids[4]);
                result.SeparatingFlightId = ids[2];
                ParsekLog.Info(tag, $"config {cfg.Number} {cfg.Name}: authored {plate.name} mount=Double (NodeSetIdx 1) " +
                    $"engines={engine.name}x2 on N2_1/N2_2 below={tank.name} on bottom top={probe.name}");
            }
            else
            {
                AvailablePart tank = FirstKnownPart(SkirtTankCandidates);
                AvailablePart engine = subject ?? FirstKnownPart(ShroudEngineCandidates);
                AvailablePart lower = FirstKnownPart(SkirtLowerCandidates);
                if (tank == null || engine == null || lower == null) { failReason = "tank, engine or lower part missing"; return null; }
                AttachNode tankBottom = tank.partPrefab.FindAttachNode("bottom");
                AttachNode engTop = engine.partPrefab.FindAttachNode("top");
                AttachNode engBottom = engine.partPrefab.FindAttachNode("bottom");
                AttachNode lowerTop = lower.partPrefab.FindAttachNode("top");
                if (tankBottom == null || engTop == null || engBottom == null || lowerTop == null)
                {
                    failReason = "attach nodes missing on tank / engine / lower part";
                    return null;
                }
                var ids = GhostCoOrbitalRig.UniqueFlightIds(3);
                if (subjectAsRoot)
                {
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(engine, ids[1], 0, Vector3.zero,
                        new Dictionary<string, int> { { "top", 1 }, { "bottom", 2 } }, null, null));
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(tank, ids[0], 0, engTop.position - tankBottom.position,
                        new Dictionary<string, int> { { "bottom", 0 } }, null, null));
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(lower, ids[2], 0, engBottom.position - lowerTop.position,
                        new Dictionary<string, int> { { "top", 0 } }, null, null));
                }
                else
                {
                    Vector3 enginePos = tankBottom.position - engTop.position;
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(tank, ids[0], 0, Vector3.zero,
                        new Dictionary<string, int> { { "bottom", 1 } }, null, null));
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(engine, ids[1], 0, enginePos,
                        new Dictionary<string, int> { { "top", 0 }, { "bottom", 2 } }, null, null));
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(lower, ids[2], 1, enginePos + engBottom.position - lowerTop.position,
                        new Dictionary<string, int> { { "top", 1 } }, null, null));
                }
                result.MaxFairingRadius = 0.9;
                result.SubjectFlightId = ids[1];
                AddContext(result, "top", tank.name, ids[0]);
                AddContext(result, "bottom", lower.name, ids[2]);
                result.SeparatingFlightId = ids[2];
                ParsekLog.Info(tag, $"config {cfg.Number} {cfg.Name}: authored {tank.name} > {engine.name} > {lower.name} (shroud shown by the part below)" +
                    (subjectAsRoot ? " root=" + engine.name : string.Empty));
            }
            result.Node = VesselNode(cfg, anchor, parts);
            return result;
        }

        /// <summary>
        /// A fairing vessel: the fairing part (root) with FGV's cross-sections scaled by the
        /// part's base radius, capped only when the last radius is the part's capRadius, the
        /// colour variant in both keys when the configuration asks for one, and its payload on
        /// the top node or its upper stage on the up-facing interstage node.
        /// </summary>
        internal static FairingAuthoredVessel BuildFairingVessel(FairingGhostConfig cfg, Vessel anchor, string tag, out string failReason)
        {
            failReason = null;
            var result = new FairingAuthoredVessel();
            AvailablePart fairingInfo = PartLoader.getPartInfoByName(cfg.FairingPart);
            if (fairingInfo == null || fairingInfo.partPrefab == null)
            {
                failReason = "no prefab for " + cfg.FairingPart;
                return null;
            }
            Part fairingPrefab = fairingInfo.partPrefab;
            ModuleProceduralFairing fairingModulePrefab = fairingPrefab.FindModuleImplementing<ModuleProceduralFairing>();
            if (fairingModulePrefab == null)
            {
                failReason = cfg.FairingPart + " prefab has no ModuleProceduralFairing";
                return null;
            }
            double baseRadius = fairingModulePrefab.baseRadius;
            double capRadius = fairingModulePrefab.capRadius;

            // Cross-sections: radii scale with the part's base radius; a capped nose ends at
            // the part's capRadius exactly (stock caps only then).
            var heights = new List<double>();
            var radii = new List<double>();
            for (int i = 0; i < cfg.HeightsOverBase.Length; i++)
            {
                heights.Add(cfg.HeightsOverBase[i] * baseRadius);
                bool last = i == cfg.HeightsOverBase.Length - 1;
                radii.Add(last && cfg.Nose == FairingNoseShape.Capped ? capRadius : cfg.RadiiOverBase[i] * baseRadius);
            }
            bool capped = FairingGhostVisualMath.EndsAtCapRadius(radii, capRadius);
            double maxR = 0.0;
            for (int i = 0; i < radii.Count; i++) maxR = Math.Max(maxR, radii[i]);
            result.MaxFairingRadius = maxR;

            string variant = null;
            if (cfg.ColourVariant)
            {
                ModulePartVariants variants = fairingPrefab.FindModuleImplementing<ModulePartVariants>();
                List<string> offered = variants != null ? variants.GetVariantNames() : null;
                string baseName = fairingPrefab.baseVariant != null ? fairingPrefab.baseVariant.Name : null;
                variant = FairingGhostVisualMath.ChooseColourVariant(offered, baseName);
                ParsekLog.Info(tag, $"config {cfg.Number} {cfg.Name}: variants offered=[{(offered != null ? string.Join(",", offered.ToArray()) : "")}] " +
                    $"base={baseName ?? "<none>"} chosen={variant ?? "<none>"}");
                if (variant == null)
                {
                    failReason = cfg.FairingPart + " offers no non-base colour variant";
                    return null;
                }
                result.VariantName = variant;
            }

            // Optional second part: a payload on the top node, or an upper stage on an
            // interstage node.
            AvailablePart childInfo = null;
            string parentNodeId = null;
            if (cfg.PayloadOnTop)
            {
                childInfo = FirstKnownPart(PayloadProbeCandidates);
                parentNodeId = "top";
            }
            else if (cfg.Interstage)
            {
                childInfo = FirstKnownPart(UpperStageCandidates);
                var candidates = new List<KeyValuePair<string, double>>();
                foreach (string id in new[] { "interstage02a", "interstage02b" })
                {
                    AttachNode n = fairingPrefab.FindAttachNode(id);
                    if (n != null)
                        candidates.Add(new KeyValuePair<string, double>(id, n.orientation.y));
                }
                parentNodeId = FairingGhostVisualMath.PickUpwardNode(candidates, "interstage02b");
                var described = new List<string>();
                foreach (var kv in candidates)
                {
                    AttachNode n = fairingPrefab.FindAttachNode(kv.Key);
                    described.Add($"{kv.Key}@pos={FormatVector(n.position)} orient={FormatVector(n.orientation)}");
                }
                ParsekLog.Info(tag, $"config {cfg.Number} {cfg.Name}: interstage nodes [{string.Join("; ", described.ToArray())}] chosen={parentNodeId}");
                if (childInfo == null)
                {
                    failReason = "no upper-stage part known (" + string.Join(",", UpperStageCandidates) + ")";
                    return null;
                }
            }

            var parts = new List<ConfigNode>();
            uint fairingFlightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            result.SubjectFlightId = fairingFlightId;
            var fairingAtt = new Dictionary<string, int>();
            if (childInfo != null && parentNodeId != null)
                fairingAtt[parentNodeId] = 1;
            ConfigNode fairingNode = GhostCoOrbitalRig.AuthorPartNode(fairingInfo, fairingFlightId, 0, Vector3.zero, fairingAtt, variant,
                (module, name) =>
                {
                    if (name == "ModuleProceduralFairing")
                    {
                        module.AddValue("nArcs", "3");
                        module.AddValue("isCapped", capped ? "True" : "False");
                        module.AddValue("interstageCraftID", "0");
                        module.AddValue("fsm", "st_idle");
                        for (int i = 0; i < heights.Count; i++)
                        {
                            ConfigNode x = module.AddNode("XSECTION");
                            x.AddValue("h", heights[i].ToString("R", IC));
                            x.AddValue("r", radii[i].ToString("R", IC));
                        }
                    }
                    else if (name == "ModuleStructuralNodeToggle")
                    {
                        module.AddValue("showMesh", "True");
                        module.AddValue("showNodes", cfg.Interstage ? "True" : "False");
                    }
                    else if (name == "ModuleStructuralNode")
                    {
                        module.AddValue("spawnState", "False");
                        module.AddValue("visibilityState", "True");
                    }
                });
            parts.Add(fairingNode);

            if (childInfo != null && parentNodeId != null)
            {
                AttachNode parentNode = fairingPrefab.FindAttachNode(parentNodeId);
                AttachNode childBottom = childInfo.partPrefab.FindAttachNode("bottom");
                if (parentNode == null || childBottom == null)
                {
                    failReason = $"attach nodes missing (parent {parentNodeId}={(parentNode != null)}, child bottom={(childBottom != null)})";
                    return null;
                }
                Vector3 childPos = parentNode.position - childBottom.position;
                uint childFlightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                if (childFlightId == fairingFlightId)
                    childFlightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                var childAtt = new Dictionary<string, int> { { "bottom", 0 } };
                ConfigNode childNode = GhostCoOrbitalRig.AuthorPartNode(childInfo, childFlightId, 0, childPos, childAtt, null, null);
                parts.Add(childNode);
                AddContext(result, parentNodeId, childInfo.name, childFlightId);
                if (cfg.Interstage) result.SeparatingFlightId = childFlightId;
                ParsekLog.Info(tag, $"config {cfg.Number} {cfg.Name}: child part {childInfo.name} on {cfg.FairingPart}.{parentNodeId} " +
                    $"at root-local {FormatVector(childPos)}");
            }

            var sections = new List<string>();
            for (int i = 0; i < heights.Count; i++)
                sections.Add(heights[i].ToString("F3", IC) + "/" + radii[i].ToString("F3", IC));
            ParsekLog.Info(tag, $"config {cfg.Number} {cfg.Name}: authored {cfg.FairingPart} baseRadius={baseRadius.ToString("F3", IC)} " +
                $"capRadius={capRadius.ToString("F3", IC)} isCapped={capped} xsections(h/r)=[{string.Join(" ", sections.ToArray())}] " +
                $"interstage={cfg.Interstage} variant={variant ?? "<base>"} parts={parts.Count.ToString(IC)}");
            result.Node = VesselNode(cfg, anchor, parts);
            return result;
        }

        private static void AddContext(FairingAuthoredVessel r, string node, string part, uint flightId)
        {
            r.ContextParts.Add(new KeyValuePair<string, string>(node, part));
            r.ContextFlightIds.Add(flightId);
        }

        private static ConfigNode VesselNode(FairingGhostConfig cfg, Vessel anchor, List<ConfigNode> parts)
        {
            Orbit placeholder = new Orbit(
                anchor.orbit.inclination, anchor.orbit.eccentricity, anchor.orbit.semiMajorAxis,
                anchor.orbit.LAN, anchor.orbit.argumentOfPeriapsis, anchor.orbit.meanAnomalyAtEpoch,
                anchor.orbit.epoch, anchor.orbit.referenceBody);
            return ProtoVessel.CreateVesselNode(
                "FGV-" + cfg.Number.ToString(IC) + " " + cfg.Name, VesselType.Ship, placeholder, 0, parts.ToArray());
        }
    }
}
