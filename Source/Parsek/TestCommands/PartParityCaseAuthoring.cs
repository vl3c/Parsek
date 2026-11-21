using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Parsek.InGameTests;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>One part the sweep may capture, as the enumeration read it (design 3.1).</summary>
    internal sealed class PartParityPartEntry
    {
        internal string Name;
        internal string Title;
        internal string PartUrl;
        internal List<string> ModelUrls = new List<string>();
        internal string Source;
        internal string Category;
        internal List<string> Modules = new List<string>();
        internal List<string> Tags = new List<string>();

        /// <summary>The variant module's base variant, null when the part has no
        /// <c>ModulePartVariants</c> (P2 captures the default skin only).</summary>
        internal string BaseVariant;

        /// <summary>The prefab's largest mesh dimension, metres (the wave planner's size).</summary>
        internal double SizeMeters;

        /// <summary>The live part; null in tests.</summary>
        internal AvailablePart Info;
    }

    /// <summary>
    /// The pure rules of case authoring (design-part-ghost-parity.md 3.1): exclusions,
    /// model urls, source attribution and tags. The live half is
    /// <see cref="PartParityCaseAuthoring"/>.
    /// </summary>
    internal static class PartParityCaseRules
    {
        internal const string ExcludeEvaKerbal = "eva-kerbal";
        internal const string ExcludeFlag = "flag";
        internal const string ExcludeSpaceObject = "space-object";
        internal const string ExcludeNoPrefab = "no-prefab";

        internal const string TagHidden = "hidden";
        internal const string TagContextDependent = "context-dependent";
        internal const string TagB9PartSwitch = "b9partswitch";
        internal const string TagRestockModulePrefix = "restock-module:";
        internal const string TagTextureSwitchPrefix = "texture-switch:";
        internal const string TagFxModulePrefix = "fx-module:";
        internal const string TagUnknownModulePrefix = "unknown-module:";

        internal const string SourceUnknown = "unknown";
        internal const string VariantModuleName = "ModulePartVariants";

        internal const string RestockModulePrefix = "ModuleRestock";
        internal const string B9ModuleName = "ModuleB9PartSwitch";

        /// <summary>Known texture / mesh switchers (enumerated in a later phase through an
        /// adapter; tagged and captured in the default skin now).</summary>
        internal static readonly string[] TextureSwitchModules =
            { "FStextureSwitch", "FStextureSwitch2", "FSmeshSwitch", "KSPTextureSwitch", "TextureSwitcher" };

        /// <summary>FX-only modules (relevant from phase 7).</summary>
        internal static readonly string[] FxModules =
            { "ModuleWaterfallFX", "ModuleWaterfallAnimation", "ModuleWaterfallEffects" };

        /// <summary>Non-stock modules with no visual effect, so they do not tag every pod.</summary>
        internal static readonly string[] InertModules =
        {
            "MechJebCore", "MechJebAR202", "ModuleKrpc", "KRPCPartModule", "ModuleScienceAlert",
            "TweakScale", "ModuleKerbalEngineer", "FlightEngineerModule", "EngineerChipFlight",
            "ModuleB9AssignUiGroups",
        };

        /// <summary>Modules whose look depends on context (ground, legs extending in free fall).</summary>
        internal static readonly string[] ContextDependentModules =
        {
            "LaunchClamp", "ModuleWheelBase", "ModuleWheelSuspension", "ModuleWheelDeployment",
            "ModuleGroundPart", "ModuleGroundExperiment", "ModuleGroundSciencePart", "ModuleGroundCommsPart",
        };

        /// <summary>Null when the part is captured, else the manifest exclusion reason.</summary>
        internal static string ExclusionReason(string partName, bool hasPrefab)
        {
            if (string.IsNullOrEmpty(partName)) return ExcludeNoPrefab;
            if (partName.StartsWith("kerbalEVA", StringComparison.Ordinal)) return ExcludeEvaKerbal;
            if (partName == "flag") return ExcludeFlag;
            if (partName == "PotatoRoid" || partName == "PotatoComet") return ExcludeSpaceObject;
            if (!hasPrefab) return ExcludeNoPrefab;
            return null;
        }

        /// <summary>The directory of a part's cfg from its <c>partUrl</c>
        /// (<c>&lt;dir&gt;/&lt;cfgFile&gt;/&lt;partName&gt;</c>): the url minus its last two
        /// segments; null when it has fewer than three.</summary>
        internal static string PartDirectoryFromPartUrl(string partUrl)
        {
            if (string.IsNullOrEmpty(partUrl)) return null;
            string[] segs = partUrl.Split('/');
            if (segs.Length < 3) return null;
            return string.Join("/", segs, 0, segs.Length - 2);
        }

        /// <summary>
        /// Every model url a part config names: each MODEL node's <c>model</c> value, then a
        /// legacy top-level <c>mesh</c> resolved against the part's directory (its <c>.mu</c>
        /// extension dropped, the way the game database names models). Duplicates dropped.
        /// </summary>
        internal static List<string> CollectModelUrls(ConfigNode partConfig, string partUrl)
        {
            var urls = new List<string>();
            if (partConfig == null) return urls;
            foreach (ConfigNode model in partConfig.GetNodes("MODEL"))
            {
                foreach (string v in model.GetValues("model"))
                    AddUnique(urls, v);
            }
            string mesh = partConfig.GetValue("mesh");
            if (!string.IsNullOrEmpty(mesh))
            {
                string name = mesh.EndsWith(".mu", StringComparison.OrdinalIgnoreCase) ? mesh.Substring(0, mesh.Length - 3) : mesh;
                string dir = PartDirectoryFromPartUrl(partUrl);
                AddUnique(urls, dir != null ? dir + "/" + name : name);
            }
            return urls;
        }

        private static void AddUnique(List<string> list, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            string t = v.Trim();
            if (t.Length > 0 && !list.Contains(t)) list.Add(t);
        }

        /// <summary>First path segment of the first model url (ReStock keeps stock part names
        /// but swaps models, so the cfg url alone would attribute a ReStock model to Squad);
        /// else of the part url; else <c>unknown</c>.</summary>
        internal static string ResolveSource(IList<string> modelUrls, string partUrl)
        {
            if (modelUrls != null)
                foreach (string u in modelUrls)
                {
                    string s = FirstSegment(u);
                    if (s != null) return s;
                }
            return FirstSegment(partUrl) ?? SourceUnknown;
        }

        internal static string FirstSegment(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string t = url.Trim().TrimStart('/');
            int slash = t.IndexOf('/');
            string s = slash >= 0 ? t.Substring(0, slash) : t;
            return s.Length > 0 ? s : null;
        }

        /// <summary>
        /// The part's tags (design 3.1), in a stable order: <c>hidden</c>,
        /// <c>context-dependent</c>, then one per module in prefab order.
        /// <paramref name="moduleIsStock"/> is parallel to <paramref name="moduleNames"/>
        /// (a module whose type lives in the game's own assembly).
        /// </summary>
        internal static List<string> ComputeTags(IList<string> moduleNames, IList<bool> moduleIsStock, bool hidden)
        {
            var tags = new List<string>();
            if (hidden) tags.Add(TagHidden);
            if (moduleNames == null) return tags;
            foreach (string m in moduleNames)
            {
                if (Array.IndexOf(ContextDependentModules, m) >= 0)
                {
                    tags.Add(TagContextDependent);
                    break;
                }
            }
            for (int i = 0; i < moduleNames.Count; i++)
            {
                string tag = ModuleTag(moduleNames[i], moduleIsStock != null && i < moduleIsStock.Count && moduleIsStock[i]);
                if (tag != null && !tags.Contains(tag)) tags.Add(tag);
            }
            return tags;
        }

        internal static string ModuleTag(string module, bool isStock)
        {
            if (string.IsNullOrEmpty(module)) return null;
            if (module.StartsWith(RestockModulePrefix, StringComparison.Ordinal)) return TagRestockModulePrefix + module;
            if (module == B9ModuleName) return TagB9PartSwitch;
            if (Array.IndexOf(TextureSwitchModules, module) >= 0) return TagTextureSwitchPrefix + module;
            if (Array.IndexOf(FxModules, module) >= 0) return TagFxModulePrefix + module;
            if (isStock || Array.IndexOf(InertModules, module) >= 0) return null;
            return TagUnknownModulePrefix + module;
        }

        /// <summary>The default-skin case key of one part (P2: default skin only). A part with a
        /// base variant names it as its skin; any other part is <c>default</c>.</summary>
        internal static PartParityCaseKey DefaultCaseKey(PartParityPartEntry e)
        {
            return new PartParityCaseKey
            {
                Part = e.Name,
                Source = e.Source,
                Skin = string.IsNullOrEmpty(e.BaseVariant) ? TestCommandPartParitySweep.DefaultSkinName : e.BaseVariant,
                SkinOrdinal = 0,
            };
        }

        /// <summary>The spawned vessel's name: never the <c>Part Showcase -</c> prefix, which
        /// switches on showcase-only ghost tweaks in <c>GhostVisualBuilder</c>.</summary>
        internal static string VesselName(string caseId) => "PartParity " + (caseId ?? string.Empty);
    }

    /// <summary>
    /// The live half of case authoring: the <c>PartLoader</c> walk and the one-part vessel
    /// node (through <see cref="GhostCoOrbitalRig.AuthorPartNode"/>).
    /// </summary>
    internal static class PartParityCaseAuthoring
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        /// <summary>Every loaded part, captured ones as entries and the rest as exclusions.</summary>
        internal static List<PartParityPartEntry> EnumerateParts(List<PartParityExclusion> exclusions)
        {
            var entries = new List<PartParityPartEntry>();
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            if (loaded == null) return entries;
            for (int i = 0; i < loaded.Count; i++)
            {
                AvailablePart info = loaded[i];
                if (info == null) continue;
                string reason = PartParityCaseRules.ExclusionReason(info.name, info.partPrefab != null);
                if (reason != null)
                {
                    exclusions?.Add(new PartParityExclusion { Part = info.name, Reason = reason });
                    continue;
                }
                entries.Add(BuildEntry(info));
            }
            return entries;
        }

        internal static PartParityPartEntry BuildEntry(AvailablePart info)
        {
            Part prefab = info.partPrefab;
            var e = new PartParityPartEntry
            {
                Name = info.name,
                Title = info.title,
                PartUrl = info.partUrl,
                Category = info.category.ToString(),
                Info = info,
            };
            e.ModelUrls = PartParityCaseRules.CollectModelUrls(info.partConfig, info.partUrl);
            e.Source = PartParityCaseRules.ResolveSource(e.ModelUrls, info.partUrl);
            var isStock = new List<bool>();
            Assembly stockAssembly = typeof(Part).Assembly;
            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule m = prefab.Modules[i];
                if (m == null) continue;
                e.Modules.Add(m.moduleName);
                isStock.Add(m.GetType().Assembly == stockAssembly);
            }
            bool hidden = info.TechHidden || info.category == PartCategories.none;
            e.Tags = PartParityCaseRules.ComputeTags(e.Modules, isStock, hidden);
            if (prefab.FindModuleImplementing<ModulePartVariants>() != null && prefab.baseVariant != null)
                e.BaseVariant = prefab.baseVariant.Name;
            e.SizeMeters = PrefabSizeMeters(prefab);
            return e;
        }

        /// <summary>The prefab's largest mesh-bounds dimension in part space (works on the
        /// inactive prefab: mesh bounds and the transform matrices, never renderer bounds).</summary>
        internal static double PrefabSizeMeters(Part prefab)
        {
            Transform root = prefab.transform;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            bool any = false;
            foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                Bounds b = mf.sharedMesh.bounds;
                Matrix4x4 toPart = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 p = toPart.MultiplyPoint3x4(Corner(b, c));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    any = true;
                }
            }
            if (!any) return 0.0;
            Vector3 size = max - min;
            return Math.Max(size.x, Math.Max(size.y, size.z));
        }

        internal static Vector3 Corner(Bounds b, int i)
        {
            Vector3 c = b.center, e = b.extents;
            return c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
        }

        /// <summary>The one-part vessel node of a case: every prefab module in prefab order,
        /// the variant written in both keys KSP reads (design 3.2), on a placeholder orbit
        /// (the co-orbital spawn replaces it).</summary>
        internal static ConfigNode AuthorVesselNode(AvailablePart info, string variantName, string caseId, Vessel anchor)
        {
            uint flightId = GhostCoOrbitalRig.UniqueFlightIds(1)[0];
            ConfigNode part = GhostCoOrbitalRig.AuthorPartNode(info, flightId, 0, Vector3.zero, null, variantName, null);
            Orbit src = anchor.orbit;
            var placeholder = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
            return ProtoVessel.CreateVesselNode(
                PartParityCaseRules.VesselName(caseId), VesselType.Probe, placeholder, 0, new[] { part });
        }

        /// <summary>The live part's selected variant name, null when it has no variant module.</summary>
        internal static string LiveVariantName(Part part)
        {
            ModulePartVariants mv = part != null ? part.FindModuleImplementing<ModulePartVariants>() : null;
            return mv != null && mv.SelectedVariant != null ? mv.SelectedVariant.Name : null;
        }

        /// <summary>Formats a size for a log line, metres.</summary>
        internal static string FormatMeters(double m) => m.ToString("0.00", Ic);
    }
}
