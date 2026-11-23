using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The plate of a lamp case (design-part-ghost-parity.md E.2): the light a part casts on a
    /// matte plate, rendered per side with only that side's own lights on, and the light
    /// structure diff. See <see cref="PartParityFxDriver"/>.
    /// </summary>
    internal static partial class PartParityFxDriver
    {
        /// <summary>The plate picture's view name (a plate case has one view; it is not one of the
        /// part's camera views).</summary>
        internal const string ViewPlate = "plate";

        /// <summary>The plate's neutral matte grey.</summary>
        private static readonly Color PlateGrey = new Color(0.7f, 0.7f, 0.7f, 1f);

        private const float PlateSettleCapSeconds = 10f;
        private const float PlateStableSeconds = 0.6f;
        private const float PlateStableFraction = 0.01f;

        /// <summary>The real lights of the part (its ghosts left out), or every light of a ghost.</summary>
        private static List<Light> LightsOf(WaveCase wc, GameObject ghostRoot)
        {
            var list = new List<Light>();
            if (ghostRoot != null)
            {
                list.AddRange(ghostRoot.GetComponentsInChildren<Light>(true));
                return list;
            }
            List<Transform> roots = GhostRoots(wc);
            foreach (Light l in wc.Real.rootPart.GetComponentsInChildren<Light>(true))
                if (l != null && !UnderAny(l.transform, roots)) list.Add(l);
            return list;
        }

        /// <summary>The light the plate faces: the first enabled, active one, else the first.</summary>
        private static Light MainLight(List<Light> lights)
        {
            foreach (Light l in lights)
                if (l != null && l.enabled && l.gameObject.activeInHierarchy) return l;
            return lights.Count > 0 ? lights[0] : null;
        }

        private static string LightsLine(List<Light> lights)
        {
            var parts = new List<string>();
            foreach (Light l in lights)
                if (l != null) parts.Add(l.name + " type=" + l.type + " en=" + l.enabled + " act=" + l.gameObject.activeInHierarchy +
                    " int=" + l.intensity.ToString("0.000", Ic) + " range=" + l.range.ToString("0.0", Ic) + " spot=" + l.spotAngle.ToString("0.0", Ic) +
                    " col=(" + l.color.r.ToString("0.000", Ic) + "," + l.color.g.ToString("0.000", Ic) + "," + l.color.b.ToString("0.000", Ic) + ")" +
                    " mask=0x" + l.cullingMask.ToString("X", Ic));
            return string.Join("; ", parts.ToArray());
        }

        private static double LightSignal(List<Light> lights)
        {
            double s = 0.0;
            foreach (Light l in lights)
                if (l != null && l.enabled && l.gameObject.activeInHierarchy) s += l.intensity * (l.color.r + l.color.g + l.color.b);
            return s;
        }

        /// <summary>
        /// The plate capture of one case: waits for the real light to stop moving (stock fades its
        /// intensity and colour in toward the target, so the step's fixed settle can end before the
        /// fade; the trace is logged for E0 U9's question), then the four sides' pictures, each with
        /// only its own lights on.
        /// </summary>
        private static IEnumerator CapturePlate(StepContext ctx, WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityStateRun run = wc.State;
            List<Light> real = LightsOf(wc, null);
            float start = Time.realtimeSinceStartup;
            float lastChange = start;
            double previous = LightSignal(real), first = previous;
            float firstIntensity = MainLight(real) != null ? MainLight(real).intensity : 0f;
            while (Time.realtimeSinceStartup - start < PlateSettleCapSeconds && Time.realtimeSinceStartup - lastChange < PlateStableSeconds)
            {
                yield return null;
                if (PartParitySweepRunner.CaseVesselLost(wc)) { LoseVessel(wc); yield break; }
                double now = LightSignal(real);
                if (Math.Abs(now - previous) > PlateStableFraction * Math.Max(1e-6, Math.Abs(previous))) lastChange = Time.realtimeSinceStartup;
                previous = now;
            }
            bool stable = Time.realtimeSinceStartup - lastChange >= PlateStableSeconds;
            CompareLights(wc);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: plate light settle waited={(Time.realtimeSinceStartup - start).ToString("0.00", Ic)}s stable={(stable ? "true" : "false")} " +
                $"signal {first.ToString("0.000", Ic)} -> {previous.ToString("0.000", Ic)} intensity0={firstIntensity.ToString("0.000", Ic)} real[{LightsLine(real)}]");
            GameObject eventRoot = wc.Ghost != null ? wc.Ghost.Build.root : null;
            GameObject startRoot = run.StartGhost != null ? run.StartGhost.Build.root : null;
            GameObject stowedRoot = run.StowedGhost != null ? run.StowedGhost.Build.root : null;
            List<Light> ghost = LightsOf(wc, eventRoot);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: plate lights event ghost[{LightsLine(ghost)}]");
            PartParitySweepRunner.Guard(ctx, wc, "capture-" + ViewPlate, () => PlateCapture(ctx, wc, real, ghost, eventRoot, startRoot, stowedRoot));
        }

        private static void PlateCapture(StepContext ctx, WaveCase wc, List<Light> real, List<Light> ghost,
            GameObject eventRoot, GameObject startRoot, GameObject stowedRoot)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityStateRun run = wc.State;
            Light main = MainLight(real);
            if (main == null)
            {
                wc.Status = wc.Status ?? TestCommandPartParitySweep.SetupPlateNotLit;
                wc.Detail = wc.Detail ?? "the part has no Light component";
                return;
            }
            int layer = PlateLayer(wc, main);
            float dist = (float)PartParityFxRules.PlateDistanceM(main.type.ToString());
            bool spot = main.type == LightType.Spot;
            float size = spot ? Mathf.Max(2f, 2f * dist * Mathf.Tan(main.spotAngle * 0.5f * Mathf.Deg2Rad) * 1.3f) : 3f;
            BuildPlate(wc, main, layer, dist, size);
            fx.PlateLightType = main.type.ToString();
            fx.PlateRange = main.range;
            fx.PlateSpotAngle = main.spotAngle;
            fx.PlateCullingMask = main.cullingMask;
            Transform plateT = fx.PlateObject.transform;
            Vector3 forward = main.transform.forward;
            Vector3 center = plateT.position;
            Vector3 up = plateT.up;
            var pictures = new List<byte[]>();
            var mattes = new List<PartParityMatteStats>();
            Renderer plateRenderer = fx.PlateObject.GetComponent<Renderer>();
            var only = new List<Renderer> { plateRenderer };
            List<Renderer> hide = HideSet();
            hide.Add(plateRenderer);
            using (PartParityCapture.FxFrame f = ctx.Capture.BeginFxFrame(hide, 1 << layer, false))
            {
                f.Pose(center - forward * (dist * 0.5f), Quaternion.LookRotation(forward, up), size * 0.5f, 0.01, dist + 1.0);
                for (int i = 0; i < 4; i++)
                {
                    List<Light> keep = PlateSideLights(wc, i, real, ghost, startRoot, stowedRoot);
                    if (i == 1 && fx.GhostLightsOff) keep = new List<Light>();
                    f.KeepLights(keep);
                    byte[] over = f.Render(only, Color.black);
                    byte[] white = f.Render(only, Color.white);
                    pictures.Add(PartParityMetrics.ComposeMatte(over, white, out PartParityMatteStats matte));
                    mattes.Add(matte);
                }
            }
            int tile = ctx.Capture.Tile;
            PlateLitFraction(wc, pictures[0], tile, out double lit, out int plateLitPx, out int platePx);
            fx.PlateLitPct = lit;
            fx.PlateLit = PartParityFxRules.PlateLit(lit);
            PartParityViewMeasures ev = PartParityMetrics.Measure(pictures[0], pictures[1], tile, tile, tile * (double)tile);
            PartParityViewMeasures st = PartParityMetrics.Measure(pictures[0], pictures[2], tile, tile, tile * (double)tile);
            PartParityViewMeasures stowed = PartParityMetrics.Measure(pictures[0], pictures[3], tile, tile, tile * (double)tile);
            fx.PlateViewsFail = PartParitySweepPlan.ShapeFails(ev) || PartParitySweepPlan.ColorFails(ev);
            wc.CapturedViews.Add(ViewPlate);
            wc.RealTiles.Add(pictures[0]);
            wc.GhostTiles.Add(pictures[1]);
            wc.GhostStartTiles.Add(pictures[2]);
            wc.GhostStowedTiles.Add(pictures[3]);
            wc.ViewRecords.Add(new PartParityViewRecord
            {
                View = ViewPlate, Measures = ev, RealMatte = mattes[0], GhostMatte = mattes[1],
                Start = new PartParityViewRecord { Measures = st, RealMatte = mattes[0], GhostMatte = mattes[2] },
                StowedRef = stowed,
            });
            ParsekLog.Info(Tag, $"case {wc.CaseId}: plate light={main.name} type={main.type} dist={dist.ToString("0.0", Ic)} size={size.ToString("0.00", Ic)} layer={layer.ToString(Ic)} " +
                $"maskReaches={(main.cullingMask & (1 << layer)) != 0} litPct={(lit * 100).ToString("0.00", Ic)}% litPx={plateLitPx.ToString(Ic)} platePx={platePx.ToString(Ic)} " +
                $"meanLum real/ghost={PlateMeanLum(pictures[0]).ToString("0.0", Ic)}/{PlateMeanLum(pictures[1]).ToString("0.0", Ic)} " +
                $"viewsFail={(fx.PlateViewsFail ? "true" : "false")} eventColorBlobPx={ev.ColorBlobPx.ToString(Ic)} startColorBlobPx={st.ColorBlobPx.ToString(Ic)} stowedColorBlobPx={stowed.ColorBlobPx.ToString(Ic)} " +
                $"lightsOff={(fx.GhostLightsOff ? "true" : "false")}");
            if (!fx.PlateLit && !(wc.Fx.Control != null && wc.Status != null))
            {
                wc.Status = wc.Status ?? TestCommandPartParitySweep.SetupPlateNotLit;
                wc.Detail = wc.Detail ?? "real side lit " + (lit * 100).ToString("0.00", Ic) + "% of the plate (floor " +
                    (PartParityFxRules.PlateLitMinPct * 100).ToString("0", Ic) + "%), light type=" + main.type + " range=" + main.range.ToString("0.0", Ic) +
                    " spotAngle=" + main.spotAngle.ToString("0.0", Ic) + " cullingMask=0x" + main.cullingMask.ToString("X", Ic);
            }
        }

        /// <summary>The lights of the i-th side (real, event, start, stowed), the lights of the
        /// other sides off.</summary>
        private static List<Light> PlateSideLights(WaveCase wc, int side, List<Light> real, List<Light> ghost, GameObject startRoot, GameObject stowedRoot)
        {
            switch (side)
            {
                case 0: return real;
                case 1: return ghost;
                case 2: return startRoot != null ? LightsOf(wc, startRoot) : new List<Light>();
                default: return stowedRoot != null ? LightsOf(wc, stowedRoot) : new List<Light>();
            }
        }

        private static int PlateLayer(WaveCase wc, Light main)
        {
            List<Transform> roots = GhostRoots(wc);
            foreach (Renderer r in wc.Real.rootPart.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.enabled && r.gameObject.activeInHierarchy && !UnderAny(r.transform, roots))
                {
                    int layer = r.gameObject.layer;
                    return (main.cullingMask & (1 << layer)) != 0 ? layer : 0;
                }
            return 0;
        }

        /// <summary>A quad facing the light at <paramref name="dist"/> metres along its forward, a
        /// neutral matte material, no collider; the plate lives until the case is destroyed.</summary>
        private static void BuildPlate(WaveCase wc, Light main, int layer, float dist, float size)
        {
            PartParityFxRun fx = wc.Fx;
            Vector3 forward = main.transform.forward;
            GameObject plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Collider collider = plate.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            plate.name = "PartParityFxPlate";
            plate.layer = layer;
            Shader shader = Shader.Find("KSP/Diffuse") ?? Shader.Find("Diffuse") ?? Shader.Find("Standard");
            fx.PlateMaterial = new Material(shader) { color = PlateGrey };
            plate.GetComponent<Renderer>().sharedMaterial = fx.PlateMaterial;
            plate.transform.position = main.transform.position + forward * dist;
            Vector3 up = Mathf.Abs(Vector3.Dot(forward, wc.Real.rootPart.transform.up)) > 0.9f
                ? wc.Real.rootPart.transform.forward : wc.Real.rootPart.transform.up;
            plate.transform.rotation = Quaternion.LookRotation(forward, up);
            plate.transform.localScale = new Vector3(size, size, 1f);
            fx.PlateObject = plate;
        }

        /// <summary>The fraction of the plate's pixels at <see cref="PartParityFxRules.PlateLitMinLum"/>
        /// luminance or more, over the plate's own pixels (alpha from the matte).</summary>
        private static void PlateLitFraction(WaveCase wc, byte[] picture, int tile, out double fraction, out int litPx, out int platePx)
        {
            litPx = 0;
            platePx = 0;
            for (int p = 0; p < picture.Length; p += 4)
            {
                if (picture[p + 3] < PartParityFxRules.AlphaCovered) continue;
                platePx++;
                if (PartParityFxRules.Luminance(picture[p], picture[p + 1], picture[p + 2]) >= PartParityFxRules.PlateLitMinLum) litPx++;
            }
            fraction = platePx > 0 ? (double)litPx / platePx : double.NaN;
        }

        private static double PlateMeanLum(byte[] picture)
        {
            double sum = 0.0;
            int n = 0;
            for (int p = 0; p < picture.Length; p += 4)
            {
                if (picture[p + 3] < PartParityFxRules.AlphaCovered) continue;
                sum += PartParityFxRules.Luminance(picture[p], picture[p + 1], picture[p + 2]);
                n++;
            }
            return n > 0 ? sum / n : 0.0;
        }

        // =====================================================================================
        // light structure
        // =====================================================================================

        /// <summary>The part's <c>Light</c> components against the event ghost's (E.2,
        /// <c>lightsDiffer</c>): paired by name then position, every differing field named.</summary>
        private static void CompareLights(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            fx.LightsDiffer.Clear();
            if (wc.Ghost == null || wc.Ghost.Build?.root == null || wc.Real?.rootPart == null) return;
            List<Light> real = LightsOf(wc, null), ghost = LightsOf(wc, wc.Ghost.Build.root);
            if (real.Count == 0 && ghost.Count == 0) return;
            Transform partT = wc.Real.rootPart.transform;
            Comparison<Light> byName = (a, b) =>
            {
                int c = string.CompareOrdinal(a.name, b.name);
                return c != 0 ? c : partT.InverseTransformPoint(a.transform.position).sqrMagnitude.CompareTo(partT.InverseTransformPoint(b.transform.position).sqrMagnitude);
            };
            real.Sort(byName);
            ghost.Sort(byName);
            if (real.Count != ghost.Count) fx.LightsDiffer.Add("count real=" + real.Count.ToString(Ic) + " ghost=" + ghost.Count.ToString(Ic));
            int n = Math.Min(real.Count, ghost.Count);
            for (int i = 0; i < n; i++)
            {
                Light r = real[i], g = ghost[i];
                string name = r.name;
                void Field(string field, string rv, string gv, bool same)
                {
                    if (!same) fx.LightsDiffer.Add(name + "." + field + " real=" + rv + " ghost=" + gv);
                }
                Field("type", r.type.ToString(), g.type.ToString(), r.type == g.type);
                Field("intensity", r.intensity.ToString("0.000", Ic), g.intensity.ToString("0.000", Ic), Math.Abs(r.intensity - g.intensity) < 0.02f);
                Field("range", r.range.ToString("0.00", Ic), g.range.ToString("0.00", Ic), Math.Abs(r.range - g.range) < 0.1f);
                Field("spotAngle", r.spotAngle.ToString("0.0", Ic), g.spotAngle.ToString("0.0", Ic), Math.Abs(r.spotAngle - g.spotAngle) < 0.5f);
                Field("color", ColorText(r.color), ColorText(g.color), ((Vector4)(r.color - g.color)).magnitude < 0.03f);
                Vector3 rp = partT.InverseTransformPoint(r.transform.position), gp = partT.InverseTransformPoint(g.transform.position);
                Field("position", rp.ToString("F3"), gp.ToString("F3"), (rp - gp).magnitude < 0.02f);
                Vector3 rf = partT.InverseTransformDirection(r.transform.forward), gf = partT.InverseTransformDirection(g.transform.forward);
                Field("forward", rf.ToString("F3"), gf.ToString("F3"), Vector3.Angle(rf, gf) < 1f);
            }
        }

        private static string ColorText(Color c)
            => "(" + c.r.ToString("0.000", Ic) + "," + c.g.ToString("0.000", Ic) + "," + c.b.ToString("0.000", Ic) + ")";
    }
}
