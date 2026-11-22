using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The sweep's real-side colour-changer leak repair (design-part-ghost-parity.md D.3,
    /// "Colour-changer leak on the real part"; todo PARTPARITY-STACKED-SHROUD-TOP-COLOR).
    ///
    /// Stock <c>ModuleColorChanger.UpdateColor</c> writes its colour into the PART's shared
    /// <c>Part.mpb</c> and applies that block to its own renderer list only, the list its
    /// <c>excludedRenderer</c> / <c>includedRenderer</c> keys filter. But <c>Part.SetOpacity</c>
    /// and the part highlight (<c>SetHighlightColor</c> / <c>SetHighlightDefault</c>) apply the
    /// same block to EVERY renderer of the part (decompiled KSP 1.12.5), so an excluded renderer
    /// shows the colour too once one of those writes runs after the colour changer's first
    /// write, and not before. Which came last differs run to run: ReStock's heat shields
    /// exclude their jettison fairing from the ablation char (<c>excludedRenderer =
    /// HeatShield25Fairing</c>), and the stacked stowed case photographed the real fairing at
    /// the char colour (0.85 of its texture, the full-ablator char) in some runs and plain in
    /// others, while the ghost honours the exclusion every time.
    ///
    /// The repair runs inside the sweep capture only (never on a production path), on the
    /// live real part before its renderers are read: for every colour changer whose renderer
    /// list it can read, each of the case's real renderers OUTSIDE that list whose property
    /// block carries exactly the module's current colour for its shader property (the block
    /// <c>Part.mpb</c> holds) gets that property set back to its own material's value, the
    /// look the module's own filter gives. One summary line per case that repaired, the flag
    /// <c>colorchanger-leak-repaired:&lt;count&gt;</c> (report-only).
    /// </summary>
    internal static class PartParityColorChangerLeakRepair
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        /// <summary>The sidecar flag counting the renderers the repair reset:
        /// <c>colorchanger-leak-repaired:&lt;count&gt;</c>.</summary>
        internal const string FlagRepairedPrefix = "colorchanger-leak-repaired:";

        /// <summary><c>ModuleColorChanger</c>'s protected renderer list (decompiled).</summary>
        internal const string FieldRenderers = "renderers";

        /// <summary>Two colours this close per channel are the same colour (the block holds the
        /// float the module wrote, so a leak matches exactly; the slack only absorbs a
        /// round-trip).</summary>
        internal const double SameColourEpsilon = 1e-4;

        /// <summary>
        /// Pure: a renderer's block value <paramref name="block"/> is a leak of the module's
        /// colour <paramref name="module"/> when the two are the same colour and the renderer's
        /// own material value <paramref name="material"/> is not (a leak that changes nothing
        /// is left alone). Each is [r, g, b, a].
        /// </summary>
        internal static bool IsLeak(double[] block, double[] module, double[] material)
            => SameColour(block, module) && !SameColour(block, material);

        internal static bool SameColour(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != 4 || b.Length != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                if (double.IsNaN(a[i]) || double.IsNaN(b[i])) return false;
                if (Math.Abs(a[i] - b[i]) > SameColourEpsilon) return false;
            }
            return true;
        }

        internal static string Flag(int repaired) => FlagRepairedPrefix + repaired.ToString(Ic);

        /// <summary>
        /// Resets every leaked colour on <paramref name="candidates"/> (the case's real
        /// renderers: the part's own and its jettison transforms' wherever they live). Returns
        /// how many renderer x property pairs it reset; <paramref name="detail"/> names them.
        /// </summary>
        internal static int Repair(Part part, IList<Renderer> candidates, out string detail)
        {
            detail = null;
            if (part == null || candidates == null || candidates.Count == 0) return 0;
            FieldInfo listField = typeof(ModuleColorChanger).GetField(FieldRenderers,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (listField == null) return 0;
            MaterialPropertyBlock partBlock = part.mpb;
            var block = new MaterialPropertyBlock();
            var names = new List<string>();
            int repaired = 0;
            foreach (PartModule pm in part.Modules)
            {
                if (!(pm is ModuleColorChanger cc) || string.IsNullOrEmpty(cc.shaderProperty)) continue;
                if (!(listField.GetValue(cc) is List<Renderer> kept)) continue;
                int id = Shader.PropertyToID(cc.shaderProperty);
                double[] moduleColour = Rgba(partBlock.GetColor(id));
                foreach (Renderer r in candidates)
                {
                    if (r == null || kept.Contains(r)) continue;
                    Material mat = FirstWithProperty(r, id);
                    if (mat == null) continue;
                    r.GetPropertyBlock(block);
                    if (block.isEmpty) continue;
                    Color own = mat.GetColor(id);
                    if (!IsLeak(Rgba(block.GetColor(id)), moduleColour, Rgba(own))) continue;
                    block.SetColor(id, own);
                    r.SetPropertyBlock(block);
                    repaired++;
                    names.Add(r.name + "." + cc.shaderProperty);
                }
            }
            if (repaired > 0) detail = string.Join(",", names.ToArray());
            return repaired;
        }

        private static Material FirstWithProperty(Renderer r, int id)
        {
            Material[] mats = r.sharedMaterials;
            if (mats == null) return null;
            foreach (Material m in mats)
                if (m != null && m.HasProperty(id)) return m;
            return null;
        }

        private static double[] Rgba(Color c) => new double[] { c.r, c.g, c.b, c.a };
    }
}
