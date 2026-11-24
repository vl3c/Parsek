using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Parsek.InGameTests;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The FX wave and its capture (design-part-ghost-parity.md E.2, E.3): spawn, settle, press,
    /// wait, the ghosts on the screen, the warm-up on every side, the layered time accumulation,
    /// the sidecar and the atlas. See <see cref="PartParityFxDriver"/>.
    /// </summary>
    internal static partial class PartParityFxDriver
    {
        // =====================================================================================
        // the wave (E.2)
        // =====================================================================================

        /// <summary>
        /// One FX wave: spawn and settle like a stowed wave; the hold, the stowed inputs, the press
        /// and the wait; every ghost built and primed BEFORE any warm-up so the warm-ups overlap;
        /// the plateau wait; then one capture per case, the plate and the heat cases captured the
        /// deterministic way. The host guard stops the wave like every other.
        /// </summary>
        internal static IEnumerator RunWave(StepContext ctx, List<WaveCase> wave)
        {
            ctx.Live = wave;
            bool solo = wave.Count == 1 && wave[0].Solo;
            PartParitySweepRunner.WriteInflight(ctx, wave.ConvertAll(c => c.CaseId), solo);
            foreach (var wc in wave)
            {
                if (wc.Fx != null && wc.Fx.Stochastic) EnsureCheat(ctx, wc);
                if (wc.Fx != null) NoteMainThrottle(ctx, wc);
            }
            for (int i = 0; i < wave.Count; i++)
            {
                WaveCase wc = wave[i];
                int slot = i;
                PartParitySweepRunner.Guard(ctx, wc, "spawn", () => SpawnCase(ctx, wc, slot, wave.Count));
            }
            IEnumerator ready = PartParitySweepRunner.WaitReady(ctx, wave);
            try
            {
                while (ready.MoveNext())
                {
                    PartParityAirContext.HoldReadyCases(ctx, wave);
                    yield return ready.Current;
                }
            }
            finally { PartParitySweepRunner.DisposeNested(ready); }
            PartParityAirContext.HoldReadyCases(ctx, wave);
            if (ctx.HostDamage == null)
            {
                IEnumerator settle = PartParitySweepRunner.WaitSettled(ctx, wave);
                try { while (settle.MoveNext()) yield return settle.Current; }
                finally { PartParitySweepRunner.DisposeNested(settle); }
            }
            if (ctx.HostDamage == null)
            {
                foreach (var wc in wave)
                    if (wc.Status == null && wc.Real != null && wc.Fx != null)
                        PartParitySweepRunner.Guard(ctx, wc, "prepare", () => Prepare(ctx, wc));
                IEnumerator phase = PartParityStateDriver.RunPhase(ctx, wave, where => PartParitySweepRunner.CheckHost(ctx, where),
                    (c, stage, body) => PartParitySweepRunner.Guard(ctx, c, stage, body));
                try { while (phase.MoveNext()) yield return phase.Current; }
                finally { PartParitySweepRunner.DisposeNested(phase); }
            }
            if (ctx.HostDamage == null)
            {
                foreach (var wc in wave)
                    if (Ready(wc))
                        PartParitySweepRunner.Guard(ctx, wc, "prepare", () =>
                        {
                            PartParityCaseCapture.Prepare(ctx, wc);
                            if (wc.Status == null) SetUpFx(ctx, wc);
                        });
                if (ctx.HostDamage == null && !PartParitySweepRunner.CheckHost(ctx, "fx warm-up start"))
                {
                    IEnumerator warm = WarmUp(ctx, wave);
                    try { while (warm.MoveNext()) yield return warm.Current; }
                    finally { PartParitySweepRunner.DisposeNested(warm); }
                }
            }

            int unrun = 0;
            foreach (var wc in wave)
            {
                if (ctx.HostDamage != null) { unrun++; continue; }
                PartParitySweepRunner.WriteInflight(ctx, new List<string> { wc.CaseId }, solo);
                IEnumerator one = CaptureCase(ctx, wc);
                try { while (one.MoveNext()) yield return one.Current; }
                finally { PartParitySweepRunner.DisposeNested(one); }
                PartParitySweepRunner.Guard(ctx, wc, "write", () => PartParitySweepRunner.FinishCase(ctx, wc));
                PartParitySweepRunner.DeleteInflight(ctx);
                PartParitySweepRunner.CheckHost(ctx, "after case " + wc.CaseId);
            }
            if (ctx.HostDamage != null)
            {
                PartParitySweepRunner.DeleteInflight(ctx);
                ParsekLog.Info(Tag, $"fx wave stopped by the host damage: {unrun.ToString(Ic)} of {wave.Count.ToString(Ic)} case(s) left unrun");
            }
            IEnumerator down = PartParitySweepRunner.TearDownWave(ctx, wave);
            try { while (down.MoveNext()) yield return down.Current; }
            finally { PartParitySweepRunner.DisposeNested(down); }
            ctx.Live = null;
        }

        /// <summary>The hold moved at render time by at least the gate (E.2): its drift in metres.</summary>
        internal const string FlagHoldDriftPrefix = "fx-hold-drift:";

        private static bool Ready(WaveCase wc)
            => wc.Status == null && wc.State != null && wc.Fx != null && wc.Real != null && wc.Real.rootPart != null && wc.State.Reached;

        // =====================================================================================
        // set-up after the ghosts exist
        // =====================================================================================

        /// <summary>
        /// After the ghosts of one case are built: the geometry, the control's broken ghost, the
        /// visible prime, the sides and the near and bottom boxes; the real FX inventory and the
        /// structure diff.
        /// </summary>
        private static void SetUpFx(StepContext ctx, WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityStateRun run = wc.State;
            fx.TilePx = ctx.Capture.Tile;
            if (fx.Stochastic) MeasureGeometry(wc, run);
            if (wc.Status != null) return;
            bool broken = fx.Control == TestCommandPartParitySweep.ControlFxPlumeShort
                          || fx.Control == TestCommandPartParitySweep.ControlFxPlumeHue
                          || fx.Control == TestCommandPartParitySweep.ControlFxPlumeShift
                          || fx.Control == TestCommandPartParitySweep.ControlFxSmokeDropped;
            if (broken) BuildBrokenGhost(wc);
            PrimeGhosts(wc);
            BuildSides(wc, broken);
            if (wc.Status != null) return;
            if (fx.Exhaust != null && wc.Shared != null && !wc.Shared.IsEmpty)
            {
                fx.NozzleDiameterM = PartParityFxFraming.NozzleDiameterM(fx.ThrustPoints, fx.Nozzle, fx.Exhaust, wc.Shared);
                fx.NearBox = PartParityFxFraming.NearBox(wc.Shared, fx.Exhaust);
                fx.BottomBox = PartParityFxFraming.BottomBox(fx.NearBox, fx.Exhaust);
            }
            if (fx.Stochastic) BreakGhost(wc);
            if (fx.Stochastic && fx.NearBox == null)
            {
                Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "no exhaust geometry: exhaust=" + (fx.Exhaust != null ? "ok" : "none") +
                    " box=" + (wc.Shared == null || wc.Shared.IsEmpty ? "none" : "ok"));
                return;
            }
            fx.CullingMask = CullingMaskFor(wc);
            ReadStructure(wc);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: FX set up family={fx.Option.Family} sides=[{SideNames(fx)}] " +
                $"nozzleDiameterM={fx.NozzleDiameterM.ToString("0.000", Ic)} cullingMask=0x{fx.CullingMask.ToString("X", Ic)} " +
                $"events={run.Sink.Events.Count.ToString(Ic)} ghostEvent={run.GhostEvent ?? "-"} ghostStart={run.GhostStart ?? "-"}");
        }

        private static string SideNames(PartParityFxRun fx)
        {
            var names = new List<string>();
            foreach (PartParityFxSide s in fx.Sides) names.Add(s.Name);
            return string.Join(",", names.ToArray());
        }

        /// <summary>The broken controls' second event-route ghost: built exactly like the intact
        /// one, primed with it, then broken by <see cref="BreakGhost"/>.</summary>
        private static void BuildBrokenGhost(WaveCase wc)
        {
            PartParityStateRun run = wc.State;
            var events = new List<PartEvent>(run.StowedSeeds);
            foreach (PartEvent e in run.Sink.Events) events.Add(e);
            if (!GhostCoOrbitalRig.TryBuildGhostFromSnapshot(run.StowedSnapshot?.CreateCopy(),
                    "pp-" + Guid.NewGuid().ToString("N").Substring(0, 8), "ParsekPartParity_" + wc.CaseId + "_broken", events,
                    wc.Real.vesselName, wc.Real.persistentId, run.SeedUT, out GhostCoOrbitalRig.LiveGhost g, out string fail))
            {
                Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "broken control ghost: " + fail);
                return;
            }
            wc.Fx.BrokenGhost = g;
            Transform partT = wc.Real.rootPart.transform;
            GameObject root = g.Build.root;
            root.transform.SetParent(partT, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            root.SetActive(true);
            GhostCoOrbitalRig.NeutralisePhysics(root, out int _, out int _);
            foreach (double ut in PartParityStateWave.EventRoutePrimeUTs(run.SeedUT, EventUTs(run), 3.0, false))
                GhostCoOrbitalRig.ApplySeedPrefix(g, ut);
        }

        private static List<double> EventUTs(PartParityStateRun run)
        {
            var uts = new List<double>();
            foreach (PartEvent e in run.Sink.Events) uts.Add(e.ut);
            return uts;
        }

        /// <summary>The shift control's fraction of the near box (the plume's length bound).</summary>
        internal const double ShiftOfNearBox = 0.12;

        /// <summary>Applies a broken plume control's change to its ghost, after the visible prime
        /// (E.5). The hue and smoke-dropped changes are the sides' own flags.</summary>
        private static void BreakGhost(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            if (fx.BrokenGhost == null || fx.Exhaust == null) return;
            List<EngineGhostInfo> engines = InfosOf(fx.BrokenGhost.State, wc.Real.rootPart.persistentId);
            if (fx.Control == TestCommandPartParitySweep.ControlFxPlumeShort) ShrinkFx(engines, ShortScale);
            else if (fx.Control == TestCommandPartParitySweep.ControlFxPlumeShift && wc.Shared != null && !wc.Shared.IsEmpty)
            {
                Transform partT = wc.Real.rootPart.transform;
                double[] u = fx.Exhaust;
                double[] a = PartParityFxFraming.SideDirection(u);
                // E.5 says 10 % of the body, but the fail lines are 10 % (origin) and 5 % (lateral)
                // of the PLUME, usually longer than the body, so a body-sized shift could not fail.
                // The near box is three body lengths and holds the plume: 12 % of it clears both.
                double near = fx.NearBox != null ? PartParityFxFraming.AlongSize(fx.NearBox, u) : 0.0;
                double across = Math.Max(0.1 * PartParityFxFraming.AcrossSize(wc.Shared, u), ShiftOfNearBox * near);
                double along = Math.Max(0.1 * PartParityFxFraming.AlongSize(wc.Shared, u), ShiftOfNearBox * near);
                var local = new Vector3((float)(a[0] * across + u[0] * along), (float)(a[1] * across + u[1] * along), (float)(a[2] * across + u[2] * along));
                Vector3 world = partT.TransformVector(local);
                var roots = FxRoots(engines);
                foreach (Transform t in roots) t.position += world;
                ParsekLog.Info(Tag, $"case {wc.CaseId}: broken ghost shifted {roots.Count.ToString(Ic)} FX root(s) by {world.magnitude.ToString("0.000", Ic)}m " +
                    $"(across={across.ToString("0.000", Ic)} along={along.ToString("0.000", Ic)})");
            }
        }

        private static List<EngineGhostInfo> InfosOf(GhostPlaybackState state, uint pid)
        {
            var list = new List<EngineGhostInfo>();
            if (state?.engineInfos == null) return list;
            foreach (EngineGhostInfo i in state.engineInfos.Values)
                if (i != null && i.partPersistentId == pid) list.Add(i);
            return list;
        }

        /// <summary>The short-plume control's scale.</summary>
        internal const float ShortScale = 0.4f;

        /// <summary>
        /// Shrinks a ghost's FX by <paramref name="k"/>: every system under an FX root takes the
        /// Hierarchy scaling mode and the root is scaled. E.5's own recipe (start speed, lifetime
        /// and energy scaled) does not shorten a stock legacy flame: it has no start speed and a
        /// stretched billboard whose length is its size and its velocity over lifetime, so at 0.35
        /// the LV-T45 flame still measured 0.73 of the intact one and its saturated bottom view
        /// left the hue line. A shrunk flame keeps every colour and loses its length.
        /// </summary>
        private static void ShrinkFx(List<EngineGhostInfo> infos, float k)
        {
            List<Transform> roots = FxRoots(infos);
            int systems = 0;
            foreach (Transform t in roots)
            {
                foreach (ParticleSystem ps in t.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    systems++;
                }
                t.localScale *= k;
            }
            ParsekLog.Info(Tag, $"broken ghost shrunk {systems.ToString(Ic)} FX system(s) on {roots.Count.ToString(Ic)} root(s) by {k.ToString("0.00", Ic)}");
        }

        /// <summary>The topmost FX transforms of these infos (a system under another moves with it).</summary>
        private static List<Transform> FxRoots(List<EngineGhostInfo> infos)
        {
            var all = new HashSet<Transform>();
            foreach (EngineGhostInfo info in infos)
            {
                foreach (ParticleSystem ps in info.particleSystems) if (ps != null) all.Add(ps.transform);
                foreach (KspEmitterRef r in info.kspEmitters) if (r.emitter != null) all.Add(r.emitter.transform);
            }
            var roots = new List<Transform>();
            foreach (Transform t in all)
            {
                bool nested = false;
                for (Transform p = t.parent; p != null && !nested; p = p.parent) nested = all.Contains(p);
                if (!nested) roots.Add(t);
            }
            return roots;
        }

        /// <summary>The sides of one case (or control), in atlas order.</summary>
        private static void BuildSides(WaveCase wc, bool broken)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityStateRun run = wc.State;
            float now = Time.realtimeSinceStartup;
            if (fx.Control == TestCommandPartParitySweep.ControlFxRealVsReal || fx.Control == TestCommandPartParitySweep.ControlFxRealVsRealAir)
            {
                // The real plume against itself in alternate samples: two sides, both the part.
                fx.Sides.Add(new PartParityFxSide { Name = PartParityFxRules.SideReal, StartRealtime = run.PressRealtime, Parity = 0 });
                fx.Sides.Add(new PartParityFxSide { Name = PartParityFxRules.SideGhost, StartRealtime = run.PressRealtime, Parity = 1, Route = PartParityStateRules.RouteEvent });
                return;
            }
            if (broken)
            {
                fx.Sides.Add(new PartParityFxSide { Name = PartParityFxRules.SideReal, Ghost = wc.Ghost, StartRealtime = now });
                var b = new PartParityFxSide { Name = PartParityFxRules.SideGhost, Ghost = fx.BrokenGhost, StartRealtime = now, Route = PartParityStateRules.RouteEvent };
                b.RotateHue = fx.Control == TestCommandPartParitySweep.ControlFxPlumeHue;
                b.HideSmoke = fx.Control == TestCommandPartParitySweep.ControlFxSmokeDropped;
                fx.Sides.Add(b);
                return;
            }
            fx.Sides.Add(new PartParityFxSide { Name = PartParityFxRules.SideReal, StartRealtime = run.PressRealtime });
            bool absent = fx.Control == TestCommandPartParitySweep.ControlGhostAbsent;
            fx.Sides.Add(new PartParityFxSide
            {
                Name = PartParityFxRules.SideGhost, Ghost = wc.Ghost, StartRealtime = now, Route = PartParityStateRules.RouteEvent,
                HideAll = absent || wc.Ghost == null,
            });
            if (fx.Control != null)
            {
                if (fx.Control == TestCommandPartParitySweep.ControlEmpty)
                    foreach (PartParityFxSide s in fx.Sides) s.HideAll = true;
                return;
            }
            fx.Sides.Add(new PartParityFxSide
            {
                Name = PartParityFxRules.SideGhostStart, Ghost = run.StartGhost, StartRealtime = now, Route = PartParityStateRules.RouteStart,
                HideAll = run.StartGhost == null,
            });
            if (fx.Option.Family == PartParityFxRules.FamilyRcs)
            {
                foreach (PartParityFxSide s in fx.Sides) ExcludeUnfiredNozzles(wc, s);
                if (wc.Status != null) return;
                fx.Sides.Add(new PartParityFxSide
                {
                    Name = PartParityFxRules.SideGhostAll, Ghost = wc.Ghost, StartRealtime = now, Route = RouteGhostAll,
                    HideAll = wc.Ghost == null,
                });
            }
        }

        /// <summary>The reported-only route of an rcs case's ghost with every nozzle shown (R4);
        /// F4 reads it as <c>all</c>.</summary>
        internal const string RouteGhostAll = "all";

        // =====================================================================================
        // renderers per side
        // =====================================================================================

        /// <summary>Every ghost root of one case (renderers under the real part that are not it).</summary>
        private static List<Transform> GhostRoots(WaveCase wc)
        {
            var roots = new List<Transform>();
            foreach (var g in new[] { wc.Ghost, wc.State?.StartGhost, wc.State?.StowedGhost, wc.Fx?.BrokenGhost })
                if (g?.Build?.root != null) roots.Add(g.Build.root.transform);
            return roots;
        }

        private static bool UnderAny(Transform t, List<Transform> roots)
        {
            foreach (Transform r in roots) if (r != null && t.IsChildOf(r)) return true;
            return false;
        }

        /// <summary>The renderers of the real part (its ghosts left out), or of a ghost's root.</summary>
        private static List<Renderer> RenderersOf(WaveCase wc, PartParityFxSide side)
        {
            var list = new List<Renderer>();
            if (side.Ghost != null)
            {
                if (side.Ghost.Build?.root == null) return list;
                foreach (Renderer r in side.Ghost.Build.root.GetComponentsInChildren<Renderer>(true))
                    if (r != null && !side.Excluded.Contains(r)) list.Add(r);
                return list;
            }
            Part part = wc.Real != null ? wc.Real.rootPart : null;
            if (part == null) return list;
            List<Transform> roots = GhostRoots(wc);
            foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                if (r != null && !UnderAny(r.transform, roots)) list.Add(r);
            return list;
        }

        private static string LayerOf(Renderer r)
            => PartParityFxRules.ClassifyFxRenderer(r.GetType().Name,
                r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : null);

        private static void Gather(WaveCase wc, PartParityFxSide side, out List<Renderer> flame, out List<Renderer> smoke)
        {
            flame = new List<Renderer>();
            smoke = new List<Renderer>();
            if (side.HideAll) return;
            foreach (Renderer r in RenderersOf(wc, side))
            {
                string layer = LayerOf(r);
                if (layer == PartParityFxRules.LayerFlame) flame.Add(r);
                else if (layer == PartParityFxRules.LayerSmoke && !side.HideSmoke) smoke.Add(r);
            }
        }

        /// <summary>The renderers of the FX capture that must never show: every renderer of every
        /// loaded vessel (the case's ghosts included, they hang under the part). Built per frame:
        /// Waterfall makes renderers as it runs.</summary>
        internal static List<Renderer> HideSet()
        {
            var list = new List<Renderer>();
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.parts == null) continue;
                foreach (Part p in v.parts)
                    if (p != null) list.AddRange(p.GetComponentsInChildren<Renderer>(true));
            }
            return list;
        }

        /// <summary>True for a renderer the body capture keeps (E.3: the P2 / P6 capture unchanged,
        /// effects hidden).</summary>
        internal static bool IsBodyRenderer(Renderer r) => r != null && LayerOf(r) == PartParityFxRules.LayerBody;

        private static int CullingMaskFor(WaveCase wc)
        {
            int mask = wc.CullingMask;
            foreach (PartParityFxSide s in wc.Fx.Sides)
                foreach (Renderer r in RenderersOf(wc, s)) mask |= 1 << r.gameObject.layer;
            return mask;
        }

        /// <summary>The rendered FX assets one side lacks (E.4, structure): judged, read once
        /// beside the prime, before any warm-up. Its inventories are dropped:
        /// <see cref="DescribeLayers"/> reads the ones the sidecar carries.</summary>
        private static void ReadStructure(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            foreach (PartParityFxSide s in fx.Sides) ReadInventory(wc, s);
            PartParityFxSide real = fx.Side(PartParityFxRules.SideReal);
            PartParityFxSide ghost = fx.Side(PartParityFxRules.SideGhost);
            if (real != null && ghost != null && ghost.Ghost != null && real.Ghost == null)
                CompareInventory(real, ghost, fx.FxMissingOnGhost, fx.FxExtraOnGhost, new List<string>());
            foreach (PartParityFxSide s in fx.Sides)
            {
                s.Inventory.Clear();
                s.InventoryRendered.Clear();
            }
        }

        /// <summary>The renderer keys per layer per side, the inventories and <c>fxDiffers</c>, once
        /// for the sidecar and the log, all report-only. Read after the warm-up: in the frame the
        /// ghosts are primed their KSPParticleEmitters have no material yet (so a flame classifies
        /// as smoke) and their systems have not taken their power's size and speed.</summary>
        private static void DescribeLayers(WaveCase wc)
        {
            var log = new StringBuilder();
            foreach (PartParityFxSide s in wc.Fx.Sides)
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string layer in PartParityFxRules.RendererLayers) { counts[layer] = 0; s.LayerKeys[layer] = new List<string>(); }
                int none = 0;
                Transform root = s.Ghost != null && s.Ghost.Build?.root != null ? s.Ghost.Build.root.transform : wc.Real.rootPart.transform;
                foreach (Renderer r in RenderersOf(wc, s))
                {
                    string layer = LayerOf(r);
                    if (layer == null) { none++; continue; }
                    counts[layer]++;
                    if (s.LayerKeys[layer].Count < 40) s.LayerKeys[layer].Add(PartParityCaseCapture.RelativePath(root, r.transform));
                }
                log.Append(' ').Append(s.Name).Append("=body").Append(counts[PartParityFxRules.LayerBody].ToString(Ic))
                    .Append("/flame").Append(counts[PartParityFxRules.LayerFlame].ToString(Ic))
                    .Append("/smoke").Append(counts[PartParityFxRules.LayerSmoke].ToString(Ic))
                    .Append("/none").Append(none.ToString(Ic));
                ReadInventory(wc, s);
            }
            PartParityFxSide real = wc.Fx.Side(PartParityFxRules.SideReal);
            PartParityFxSide ghost = wc.Fx.Side(PartParityFxRules.SideGhost);
            if (real != null && ghost != null && ghost.Ghost != null && real.Ghost == null)
                CompareInventory(real, ghost, new List<PartParityFxStructureEntry>(), new List<PartParityFxStructureEntry>(), wc.Fx.FxDiffers);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: fx layers (after warm-up){log} fxMissing=[{PartParityFxStructure.Join(wc.Fx.FxMissingOnGhost)}] " +
                $"fxExtra=[{PartParityFxStructure.Join(wc.Fx.FxExtraOnGhost)}] fxDiffers={wc.Fx.FxDiffers.Count.ToString(Ic)}");
        }

        /// <summary>The FX inventory of one side (E.4): the real side from its live particle
        /// systems, a ghost from its own engine and RCS infos, never from the rate-limited log
        /// line. Index-parallel: whether each system draws.</summary>
        private static void ReadInventory(WaveCase wc, PartParityFxSide s)
        {
            if (s.Ghost != null)
            {
                GhostPlaybackState state = s.Ghost.State;
                uint pid = wc.Real.rootPart.persistentId;
                if (state?.engineInfos != null)
                    foreach (EngineGhostInfo info in state.engineInfos.Values)
                        if (info != null && info.partPersistentId == pid) AddInventory(s, GhostFxFingerprint.BuildEntries(info), info.particleSystems);
                if (state?.rcsInfos != null)
                    foreach (RcsGhostInfo info in state.rcsInfos.Values)
                        if (info != null && info.partPersistentId == pid) AddInventory(s, GhostFxFingerprint.BuildEntries(info), info.particleSystems);
                return;
            }
            List<ParticleSystem> systems = SystemsOf(wc, s);
            AddInventory(s, GhostFxFingerprint.BuildEntries(systems, GhostFxFingerprint.ReadSystem, out _), systems);
        }

        private static void AddInventory(PartParityFxSide s, List<string> entries, List<ParticleSystem> systems)
        {
            s.Inventory.AddRange(entries);
            foreach (ParticleSystem ps in systems)
            {
                if (ps == null) continue;
                var pr = ps.GetComponent<ParticleSystemRenderer>();
                s.InventoryRendered.Add(pr != null && pr.enabled && ps.gameObject.activeInHierarchy
                                        && (ps.isPlaying || ps.particleCount > 0));
            }
        }

        private static List<ParticleSystem> SystemsOf(WaveCase wc, PartParityFxSide side)
        {
            var list = new List<ParticleSystem>();
            if (side.Ghost != null)
            {
                if (side.Ghost.Build?.root == null) return list;
                list.AddRange(side.Ghost.Build.root.GetComponentsInChildren<ParticleSystem>(true));
                return list;
            }
            Part part = wc.Real != null ? wc.Real.rootPart : null;
            if (part == null) return list;
            List<Transform> roots = GhostRoots(wc);
            foreach (ParticleSystem ps in part.GetComponentsInChildren<ParticleSystem>(true))
                if (ps != null && !UnderAny(ps.transform, roots)) list.Add(ps);
            return list;
        }

        /// <summary>The FX inventory diff (E.4, structure, deterministic): the real and ghost
        /// <c>FormatEntry</c> lines paired by canonical name and parent.</summary>
        private static void CompareInventory(PartParityFxSide real, PartParityFxSide ghost,
            List<PartParityFxStructureEntry> missing, List<PartParityFxStructureEntry> extra, List<string> differs)
            => PartParityFxStructure.Diff(real.Inventory, real.InventoryRendered, ghost.Inventory, ghost.InventoryRendered,
                missing, extra, differs);

        // =====================================================================================
        // particles (E.3)
        // =====================================================================================

        /// <summary>Every live particle of a side in part space, and the longest particle
        /// lifetime its own systems can make (E.2).</summary>
        private static IList<PartParityFxParticle> ReadParticles(WaveCase wc, PartParityFxSide side)
        {
            var list = new List<PartParityFxParticle>();
            if (side.HideAll || wc.Real == null || wc.Real.rootPart == null) return list;
            Transform partT = wc.Real.rootPart.transform;
            PartParityFxRun fx = wc.Fx;
            foreach (ParticleSystem ps in SystemsOf(wc, side))
            {
                if (ps == null) continue;
                // Particles of a renderer that draws nothing are not the plume: stock's vacuum
                // smoke keeps a population that drifts kilometres away in orbit while its renderer
                // is off (E0 U8), and it would set the far framing.
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer == null || !renderer.enabled || !ps.gameObject.activeInHierarchy) continue;
                var main = ps.main;
                double life = main.startLifetime.constantMax;
                KSPParticleEmitter emitter = ps.GetComponent<KSPParticleEmitter>();
                if (emitter != null) life = Math.Max(life, emitter.maxEnergy);
                if (life > side.LongestLifetime && life < 120.0) side.LongestLifetime = life;
                if (fx.ParticleBuffer == null) fx.ParticleBuffer = new ParticleSystem.Particle[20000];
                int n = ps.GetParticles(fx.ParticleBuffer);
                bool world = main.simulationSpace == ParticleSystemSimulationSpace.World;
                for (int i = 0; i < n; i++)
                {
                    Vector3 wp = world ? fx.ParticleBuffer[i].position : ps.transform.TransformPoint(fx.ParticleBuffer[i].position);
                    Vector3 wv = world ? fx.ParticleBuffer[i].velocity : ps.transform.TransformDirection(fx.ParticleBuffer[i].velocity);
                    Vector3 p = partT.InverseTransformPoint(wp), v = partT.InverseTransformDirection(wv);
                    list.Add(new PartParityFxParticle
                    {
                        Position = new double[] { p.x, p.y, p.z },
                        Velocity = new double[] { v.x, v.y, v.z },
                        Size = fx.ParticleBuffer[i].GetCurrentSize(ps),
                    });
                }
            }
            return list;
        }

        /// <summary>Whether the side draws at all this frame: its flame systems' particle count is
        /// zero while the flame layer covers pixels (E0 U5).</summary>
        private static int FlameParticles(WaveCase wc, PartParityFxSide side)
        {
            int n = 0;
            foreach (ParticleSystem ps in SystemsOf(wc, side))
            {
                if (ps == null) continue;
                var r = ps.GetComponent<ParticleSystemRenderer>();
                if (r == null || LayerOf(r) != PartParityFxRules.LayerFlame) continue;
                n += ps.particleCount;
            }
            return n;
        }

        // =====================================================================================
        // warm-up and plateau, on every side (E.2)
        // =====================================================================================

        /// <summary>The plateau wait of a wave's stochastic cases, after the last prime.</summary>
        private static IEnumerator WarmUp(StepContext ctx, List<WaveCase> wave)
        {
            var cases = new List<WaveCase>();
            foreach (var wc in wave)
                if (wc.Status == null && wc.Fx != null && wc.Fx.Stochastic && wc.Fx.Sides.Count > 0 && wc.Fx.NearBox != null)
                    cases.Add(wc);
            float start = Time.realtimeSinceStartup;
            foreach (var wc in cases) wc.Fx.WarmupStartRealtime = start;
            while (true)
            {
                if (PartParitySweepRunner.CheckHost(ctx, "fx warm-up")) yield break;
                yield return new WaitForEndOfFrame();
                bool pending = false;
                foreach (var wc in cases)
                {
                    if (wc.Status != null || wc.Fx.WarmupDone) continue;
                    if (PartParitySweepRunner.CaseVesselLost(wc))
                    {
                        wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                        wc.Detail = wc.Detail ?? "vessel lost in warm-up";
                        continue;
                    }
                    PartParitySweepRunner.Guard(ctx, wc, "fx-warmup", () => TickWarmUp(ctx, wc));
                    if (wc.Flags.Contains("exception-fx-warmup"))
                    {
                        // The same throw every tick would never end the wait.
                        wc.Status = wc.Status ?? TestCommandPartParitySweep.SetupCrashed;
                        wc.Fx.WarmupDone = true;
                        continue;
                    }
                    if (!wc.Fx.WarmupDone) pending = true;
                }
                if (!pending) break;
            }
            foreach (var wc in cases)
            {
                foreach (PartParityFxSide s in wc.Fx.Sides)
                {
                    ParsekLog.Info(Tag, $"case {wc.CaseId}: fx warm-up side={s.Name} signal={s.SignalName ?? "-"} settled={(s.Settled ? "true" : "false")} " +
                        $"warmupS={Fmt(s.WarmupS)} longestLifetime={s.LongestLifetime.ToString("0.00", Ic)}s samples={s.Signal.Count.ToString(Ic)} " +
                        $"particles={FlameParticles(wc, s).ToString(Ic)} startAge={(Time.realtimeSinceStartup - s.StartRealtime).ToString("0.0", Ic)}s");
                }
            }
        }

        private static string Fmt(double? d) => d.HasValue ? d.Value.ToString("0.00", Ic) : "-";

        private static void TickWarmUp(StepContext ctx, WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            float now = Time.realtimeSinceStartup;
            if (fx.NextTickRealtime > now) return;
            fx.NextTickRealtime = now + (float)PartParityFxRules.PlateauCoveragePeriodSeconds;
            bool anyUnsettled = false;
            using (PartParityCapture.FxFrame f = ctx.Capture.BeginFxFrame(HideSet(), fx.CullingMask, false))
            {
                if (fx.NearBox != null) PoseBox(f, wc, fx.NearBox, PartParityFxRules.ViewFxSideA, out _);
                foreach (PartParityFxSide s in fx.Sides)
                {
                    if (s.Settled) continue;
                    IList<PartParityFxParticle> particles = ReadParticles(wc, s);
                    s.WarmupParticles.Add(particles);
                    Gather(wc, s, out List<Renderer> flame, out List<Renderer> smoke);
                    double coverage = Coverage(f, fx, flame);
                    int flameN = FlameParticles(wc, s);
                    bool flameReadsZero = flameN == 0 && coverage > 0;
                    s.SignalName = PartParityFxRules.PlateauSignalFor(SystemsOf(wc, s).Count > 0, flameReadsZero);
                    double value = s.SignalName == PartParityFxRules.PlateauSignalParticles ? particles.Count : coverage;
                    s.Signal.Add(new KeyValuePair<float, double>(now, value));
                    double runSeconds = now - s.StartRealtime;
                    if (PlateauWindow(s.Signal, out double min, out double max)
                        && PartParityFxRules.PlateauSettled(runSeconds, s.LongestLifetime, min, max))
                    {
                        s.Settled = true;
                        s.WarmupS = runSeconds;
                        s.PlateauWaitS = now - fx.LastPrimeRealtime;
                    }
                    else anyUnsettled = true;
                }
            }
            double longest = 0.0;
            foreach (PartParityFxSide s in fx.Sides) longest = Math.Max(longest, s.LongestLifetime);
            bool capped = now - fx.LastPrimeRealtime >= PartParityFxRules.PlateauCapFor(longest);
            if (!anyUnsettled) fx.WarmupDone = true;
            else if (capped)
            {
                foreach (PartParityFxSide s in fx.Sides)
                {
                    if (s.Settled) continue;
                    s.WarmupS = now - s.StartRealtime;
                    s.PlateauWaitS = now - fx.LastPrimeRealtime;
                    string tag = PartParityFxRules.NotPlateauedTag(s.Name);
                    if (!wc.Flags.Contains(tag)) wc.Flags.Add(tag);
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: fx side {s.Name} not plateaued at the cap ({now - s.StartRealtime:0.0}s, signal={s.SignalName}, longestLifetime={longest.ToString("0.00", Ic)}s)");
                }
                fx.WarmupDone = true;
            }
        }

        /// <summary>The smallest and largest signal over the newest three ticks: two intervals of at
        /// least <see cref="PartParityFxRules.PlateauCoveragePeriodSeconds"/> each, so they span the
        /// plateau window. False until there are three.</summary>
        internal static bool PlateauWindow(IList<KeyValuePair<float, double>> signal, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            if (signal.Count < 3) return false;
            for (int i = signal.Count - 3; i < signal.Count; i++)
            {
                min = Math.Min(min, signal[i].Value);
                max = Math.Max(max, signal[i].Value);
            }
            return true;
        }

        /// <summary>The flame coverage of one side in the posed frame, in pixels of a
        /// <see cref="PartParityFxRules.PlateauCoverageTilePx"/> px tile.</summary>
        private static double Coverage(PartParityCapture.FxFrame f, PartParityFxRun fx, List<Renderer> flame)
        {
            byte[] img = f.Render(flame, Color.black);
            // Opaque before the downsample: an additive render leaves alpha 0, which the
            // downsample weighs colour by.
            for (int i = 3; i < img.Length; i += 4) img[i] = 255;
            int small = PartParityFxRules.PlateauCoverageTilePx;
            byte[] down = img.Length > small * small * 4 ? PartParityImage.Downsample(img, fx.TilePx, fx.TilePx, small, small) : img;
            int covered = 0;
            for (int i = 0; i < down.Length; i += 4)
                if (Math.Max(down[i], Math.Max(down[i + 1], down[i + 2])) >= PartParityFxRules.FlameValueMin) covered++;
            return covered;
        }

        // =====================================================================================
        // the capture (E.3)
        // =====================================================================================

        /// <summary>The case after its warm-up: the body views (or the plate), then, for a
        /// stochastic case, the layered accumulation; the results, the evidence and the
        /// flags.</summary>
        private static IEnumerator CaptureCase(StepContext ctx, WaveCase wc)
        {
            if (wc.Status != null || wc.Fx == null) yield break;
            if (PartParitySweepRunner.CaseVesselLost(wc))
            {
                wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                wc.Detail = wc.Detail ?? "vessel lost before capture";
                yield break;
            }
            PartParitySweepRunner.BeginCaseLog(ctx, wc);
            try
            {
                PartParitySweepRunner.Guard(ctx, wc, "fx-layers", () => DescribeLayers(wc));
                if (wc.Fx.Plate)
                {
                    IEnumerator plate = CapturePlate(ctx, wc);
                    try { while (plate.MoveNext()) yield return plate.Current; }
                    finally { PartParitySweepRunner.DisposeNested(plate); }
                }
                else
                {
                    if (wc.Fx.Heat != null) PartParitySweepRunner.Guard(ctx, wc, "fx-heat-log", () => LogHeat(wc));
                    foreach (string view in wc.Views)
                    {
                        yield return new WaitForEndOfFrame();
                        if (PartParitySweepRunner.CaseVesselLost(wc)) { LoseVessel(wc); yield break; }
                        PartParitySweepRunner.Guard(ctx, wc, "fx-nozzle-glow", () => AdvanceNozzleGlows(wc));
                        if (wc.Fx.Engine != null && view == wc.Views[0])
                            PartParitySweepRunner.Guard(ctx, wc, "fx-glow-log", () => LogGlow(wc));
                        PartParitySweepRunner.Guard(ctx, wc, "capture-" + view, () => PartParityCaseCapture.CaptureView(ctx, wc, view));
                    }
                }
                if (wc.Status == null) PartParitySweepRunner.Guard(ctx, wc, "fx-lights", () => CompareLights(wc));
                if (wc.Fx.Stochastic && wc.Status == null && wc.Fx.Exhaust != null)
                {
                    PartParitySweepRunner.Guard(ctx, wc, "fx-framing", () => FinalFraming(wc));
                    int frames = PartParityFxRules.SampleRounds(wc.Fx.Control) * PartParityFxRules.Views.Length;
                    for (int i = 0; i < frames; i++)
                    {
                        yield return new WaitForEndOfFrame();
                        if (PartParitySweepRunner.CaseVesselLost(wc)) { LoseVessel(wc); yield break; }
                        PartParitySweepRunner.Guard(ctx, wc, "fx-sample", () => SampleFrame(ctx, wc, i));
                    }
                    PartParitySweepRunner.Guard(ctx, wc, "fx-measure", () => Measure(wc));
                    if (wc.Flags.Contains("exception-fx-sample") || wc.Flags.Contains("exception-fx-measure")
                        || wc.Flags.Contains("exception-fx-framing"))
                        wc.Status = wc.Status ?? TestCommandPartParitySweep.SetupCrashed;
                }
                PartParitySweepRunner.Guard(ctx, wc, "fx-evidence", () => BuildEvidence(wc));
                if (wc.Fx.Hold != null)
                {
                    bool ok = PartParityFxRules.HoldOk(wc.Fx.Hold.MaxRenderDriftM);
                    string line = $"case {wc.CaseId}: hold drift {wc.Fx.Hold.Describe()} ok={(ok ? "true" : "false")}";
                    if (ok) ParsekLog.Verbose(Tag, line);
                    else
                    {
                        ParsekLog.Warn(Tag, line);
                        wc.Flags.Add(FlagHoldDriftPrefix + wc.Fx.Hold.MaxRenderDriftM.ToString("0.00000", Ic));
                    }
                }
            }
            finally
            {
                PartParitySweepRunner.EndCaseLog(ctx);
            }
        }

        /// <summary>The ghosts' nozzle glow brought to now, as every playback frame does: the real
        /// nozzle kept heating through the warm-up (an engine bell for minutes) while the ghosts
        /// stood at their prime.</summary>
        private static void AdvanceNozzleGlows(WaveCase wc)
        {
            double ut = Planetarium.GetUniversalTime();
            GhostPlaybackLogic.UpdateNozzleGlows(wc.Ghost?.State, ut);
            GhostPlaybackLogic.UpdateNozzleGlows(wc.State?.StartGhost?.State, ut);
            GhostPlaybackLogic.UpdateNozzleGlows(wc.Fx?.BrokenGhost?.State, ut);
        }

        /// <summary>One line on an engine case's glow at its first body view: how far each of the real
        /// part's throttle clips stands and how far each ghost's glows do, with the throttle they follow.</summary>
        private static void LogGlow(WaveCase wc)
        {
            double ut = Planetarium.GetUniversalTime();
            var sb = new StringBuilder();
            foreach (PartModule m in wc.Real.rootPart.Modules)
                if (m is FXModuleAnimateThrottle clip)
                    sb.Append(" real[").Append(clip.animationName).Append("]=").Append(clip.animState.ToString("0.000", Ic));
            AppendGlows(sb, "event", wc.Ghost?.State, ut);
            AppendGlows(sb, "start", wc.State?.StartGhost?.State, ut);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: glow burnS={(Time.realtimeSinceStartup - wc.State.PressRealtime).ToString("0.0", Ic)} " +
                $"mainThrottle={wc.Real.ctrlState.mainThrottle.ToString("0.000", Ic)} currentThrottle={wc.Fx.Engine.currentThrottle.ToString("0.000", Ic)}{sb}");
        }

        private static void AppendGlows(StringBuilder sb, string route, GhostPlaybackState state, double ut)
        {
            if (state?.nozzleGlows == null) return;
            foreach (NozzleGlowInfo g in state.nozzleGlows)
                sb.Append(' ').Append(route).Append('[').Append(g.animationName ?? g.moduleName).Append("]=")
                    .Append(GhostPlaybackLogic.NozzleGlowScalarAt(g, ut).ToString("0.000", Ic)).Append("->")
                    .Append(g.targetLevel.ToString("0.000", Ic));
        }

        private static void LoseVessel(WaveCase wc)
        {
            wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
            wc.Detail = wc.Detail ?? "vessel lost mid-capture";
        }

        /// <summary>The far box from the warm-up's near particles, over every side (E.3).</summary>
        private static void FinalFraming(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            double? along = null, across = null;
            foreach (PartParityFxSide s in fx.Sides)
            {
                PartParityFxParticleStats st = PartParityFxMetrics.ReduceParticles(s.WarmupParticles, fx.Nozzle, fx.Exhaust,
                    PartParityFxRules.FramingNearParticleM);
                if (st.AlongP95.HasValue) along = Math.Max(along ?? double.MinValue, st.AlongP95.Value);
                if (st.AcrossP95.HasValue) across = Math.Max(across ?? double.MinValue, st.AcrossP95.Value);
            }
            fx.FarBox = PartParityFxFraming.FarBox(wc.Shared, fx.Nozzle, fx.Exhaust, along, across, out bool clamped);
            fx.FramingClamped = clamped;
            if (clamped && !wc.Flags.Contains(PartParityFxRules.FlagFxFramingClamped)) wc.Flags.Add(PartParityFxRules.FlagFxFramingClamped);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: fx far box along={Fmt(along)}m across={Fmt(across)}m clamped={(clamped ? "true" : "false")}");
        }

        /// <summary>The box of one FX view (E.3).</summary>
        private static PartParityAabb BoxFor(PartParityFxRun fx, string view)
        {
            switch (view)
            {
                case PartParityFxRules.ViewFxBottom: return fx.BottomBox;
                case PartParityFxRules.ViewFxFar: return fx.FarBox ?? fx.NearBox;
                default: return fx.NearBox;
            }
        }

        private static double PoseBox(PartParityCapture.FxFrame f, WaveCase wc, PartParityAabb box, string view, out double orthoSize)
        {
            PartParityFxRun fx = wc.Fx;
            Transform partT = wc.Real.rootPart.transform;
            PartParityFxFraming.TryViewFrame(view, fx.Exhaust, out double[] dir, out double[] up);
            PartParityFxFraming.ProjectBox(box, dir, up, out double hw, out double hh, out double hd);
            orthoSize = PartParityFxFraming.OrthoSize(hw, hh);
            double[] c = box.Center();
            Vector3 centerW = partT.TransformPoint(new Vector3((float)c[0], (float)c[1], (float)c[2]));
            Vector3 dirW = partT.TransformDirection(new Vector3((float)dir[0], (float)dir[1], (float)dir[2])).normalized;
            Vector3 upW = partT.TransformDirection(new Vector3((float)up[0], (float)up[1], (float)up[2])).normalized;
            Quaternion rot = Quaternion.LookRotation(-dirW, upW);
            float stand = (float)hd + 2f;
            f.Pose(centerW + dirW * stand, rot, orthoSize, 1.0, 2.0 * hd + 3.0);
            return orthoSize;
        }

        /// <summary>The axis block of a view from the posed camera.</summary>
        private static PartParityFxAxis AxisOf(PartParityCapture.FxFrame f, WaveCase wc, string view, double orthoSize)
        {
            PartParityFxRun fx = wc.Fx;
            Transform partT = wc.Real.rootPart.transform;
            Vector3 nozzleW = partT.TransformPoint(new Vector3((float)fx.Nozzle[0], (float)fx.Nozzle[1], (float)fx.Nozzle[2]));
            Vector3 axisW = partT.TransformDirection(new Vector3((float)fx.Exhaust[0], (float)fx.Exhaust[1], (float)fx.Exhaust[2]));
            Vector3 a = f.ToCamera(nozzleW), b = f.ToCamera(nozzleW + axisW);
            return PartParityFxFraming.AxisFor(view, new double[] { a.x, a.y }, new double[] { b.x - a.x, b.y - a.y },
                orthoSize, fx.TilePx, fx.NozzleDiameterM);
        }

        /// <summary>One sample frame: the view of this frame round robin, every side's flame and
        /// smoke layer; every second frame, one strip frame of the side view.</summary>
        private static void SampleFrame(StepContext ctx, WaveCase wc, int index)
        {
            PartParityFxRun fx = wc.Fx;
            string view = PartParityFxRules.Views[index % PartParityFxRules.Views.Length];
            int sampleIndex = index / PartParityFxRules.Views.Length;
            if (fx.NearBox == null || fx.BottomBox == null) return;
            PartParityAabb box = BoxFor(fx, view);
            using (PartParityCapture.FxFrame f = ctx.Capture.BeginFxFrame(HideSet(), fx.CullingMask, true))
            {
                PoseBox(f, wc, box, view, out double ortho);
                if (!fx.Axes.ContainsKey(view)) fx.Axes[view] = AxisOf(f, wc, view, ortho);
                foreach (PartParityFxSide side in fx.Sides)
                {
                    if (side.Parity >= 0 && sampleIndex % 2 != side.Parity) continue;
                    Gather(wc, side, out List<Renderer> flame, out List<Renderer> smoke);
                    byte[] flamePx = f.Render(flame, Color.black);
                    if (side.RotateHue)
                        for (int p = 0; p < flamePx.Length; p += 4)
                            PartParityFxRules.RotateHueControl(ref flamePx[p], ref flamePx[p + 1], ref flamePx[p + 2]);
                    SetFor(side, view, PartParityFxRules.LayerFlame, fx).Add(flamePx);
                    byte[] over = f.Render(smoke, Color.black);
                    byte[] white = f.Render(smoke, Color.white);
                    SetFor(side, view, PartParityFxRules.LayerSmoke, fx).Add(PartParityMetrics.ComposeMatte(over, white, out _));
                }
                if (index % PartParityFxRules.Views.Length == 0)
                    foreach (PartParityFxSide side in fx.Sides)
                        if (side.Parity < 0 || sampleIndex % 2 == side.Parity) side.ParticleSamples.Add(ReadParticles(wc, side));
                if (index % PartParityFxRules.StripEverySampleFrames == 0) CaptureStrip(f, wc);
            }
        }

        private static PartParityFxSampleSet SetFor(PartParityFxSide side, string view, string layer, PartParityFxRun fx)
        {
            string key = PartParityFxSide.SetKey(view, layer);
            if (!side.Sets.TryGetValue(key, out PartParityFxSampleSet set))
            {
                set = new PartParityFxSampleSet(layer, fx.TilePx, fx.TilePx);
                side.Sets[key] = set;
            }
            return set;
        }

        /// <summary>One strip frame (E.3): the side view of the real part and of the event ghost,
        /// body and effects together over dark grey. The real and event sides only.</summary>
        private static void CaptureStrip(PartParityCapture.FxFrame f, WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityFxSide real = fx.Side(PartParityFxRules.SideReal), ghost = fx.Side(PartParityFxRules.SideGhost);
            if (real == null || ghost == null || real.Ghost != null || ghost.Ghost == null || fx.Control != null) return;
            if (!fx.Strip.TryGetValue("real", out List<byte[]> a)) fx.Strip["real"] = a = new List<byte[]>();
            if (!fx.Strip.TryGetValue("ghost", out List<byte[]> b)) fx.Strip["ghost"] = b = new List<byte[]>();
            if (a.Count >= PartParityFxRules.StripFrames) return;
            PoseBox(f, wc, fx.NearBox, PartParityFxRules.ViewFxSideA, out _);
            var grey = new Color(0.15f, 0.15f, 0.15f, 1f);
            a.Add(StripTile(f.Render(RenderersOf(wc, real), grey), fx.TilePx));
            b.Add(StripTile(f.Render(RenderersOf(wc, ghost), grey), fx.TilePx));
        }

        private static byte[] StripTile(byte[] full, int tile)
        {
            // Opaque before the downsample: an additive plume and the clear both leave alpha 0, and
            // the downsample weighs colour by alpha.
            for (int i = 3; i < full.Length; i += 4) full[i] = 255;
            int small = StripTilePx;
            return tile > small ? PartParityImage.Downsample(full, tile, tile, small, small) : full;
        }

        /// <summary>The strip's frame size: the side view downsampled, so a strip of 12 frames is a
        /// small file (the atlas keeps the full tile).</summary>
        internal const int StripTilePx = 256;

        // =====================================================================================
        // the measures and the atlas (E.4)
        // =====================================================================================

        private static void Measure(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityFxSide real = fx.Side(PartParityFxRules.SideReal);
            // Samples per side: a real-vs-real control's two sides take the alternate samples of
            // twice as many (SampleRounds).
            if (real != null && real.Sets.TryGetValue(PartParityFxSide.SetKey(PartParityFxRules.ViewFxSideA, PartParityFxRules.LayerFlame),
                    out PartParityFxSampleSet any))
                fx.SampleCount = any.All.Samples;
            foreach (PartParityFxSide s in fx.Sides)
            {
                foreach (string layer in PartParityFxRules.FxLayers)
                {
                    string row = PartParityFxMetrics.AtlasRowFor(s.Name, layer);
                    if (row == null) continue;
                    if (!fx.AtlasTiles.TryGetValue(row, out Dictionary<string, byte[]> cols))
                        fx.AtlasTiles[row] = cols = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (string view in PartParityFxRules.Views)
                        if (s.Sets.TryGetValue(PartParityFxSide.SetKey(view, layer), out PartParityFxSampleSet set))
                            cols[view] = set.All.ToTile();
                }
                if (s.Route == null || real == null) continue;
                foreach (string view in PartParityFxRules.Views)
                {
                    foreach (string layer in PartParityFxRules.FxLayers)
                    {
                        string key = PartParityFxSide.SetKey(view, layer);
                        if (!real.Sets.TryGetValue(key, out PartParityFxSampleSet rs) || !s.Sets.TryGetValue(key, out PartParityFxSampleSet gs)) continue;
                        fx.Axes.TryGetValue(view, out PartParityFxAxis axis);
                        fx.Results.Add(PartParityFxViewResult.Measure(view, s.Route, layer, axis, rs, gs));
                    }
                }
            }
            foreach (PartParityFxViewResult r in fx.Results)
            {
                if (r.Layer == PartParityFxRules.LayerFlame || r.Layer == PartParityFxRules.LayerSmoke)
                    ParsekLog.Verbose(Tag, $"case {wc.CaseId}: fx view {r.View} {r.Route} {r.Layer} core={r.Real.CorePx.ToString(Ic)}/{r.Ghost.CorePx.ToString(Ic)} " +
                        $"sat={r.Real.SaturatedPx.ToString(Ic)}/{r.Ghost.SaturatedPx.ToString(Ic)} lengthRatio={Fmt(r.Compare.LengthRatio)} widthRatio={Fmt(r.Compare.WidthRatio)} " +
                        $"originDelta={Fmt(r.Compare.OriginDeltaPx)} lateral={Fmt(r.Compare.LateralOffsetPx)} dir={Fmt(r.Compare.DirectionDeg)} " +
                        $"chroma={Fmt(r.Compare.ChromaDistance)} hueL1={Fmt(r.Compare.HueL1)} bright={Fmt(r.Compare.BrightnessRatio)} iou={r.Compare.CoreIou.ToString("0.00", Ic)} " +
                        $"halves={r.SplitHalfDisagreement() ?? "agree"}");
                string unsettled = r.SplitHalfDisagreement();
                if (unsettled != null && !wc.Flags.Contains(PartParityFxRules.CapFxUnsettled)) wc.Flags.Add(PartParityFxRules.CapFxUnsettled);
            }
            foreach (PartParityFxSide s in fx.Sides) s.Sets.Clear();
            if (fx.Option.Family == PartParityFxRules.FamilyRcs && fx.Control == null)
                wc.Flags.Add(PartParityFxRules.FormatRcsKnownDifference(fx.RcsFired.Count, fx.RcsNozzles, fx.RcsPowerRatio));
            fx.Captured = true;
        }

        /// <summary>The control's evidence from the captured measures (E.5), written as a flag.</summary>
        private static void BuildEvidence(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            if (fx.Control == null) return;
            // No measures, no evidence: the judge answers bad, never ok.
            if (fx.Stochastic && !fx.Captured) return;
            bool rcs = fx.Option.Family == PartParityFxRules.FamilyRcs;
            var e = new PartParityFxControlEvidence { SetupStatus = wc.Status ?? TestCommandPartParitySweep.SetupOk };
            foreach (PartParityFxViewResult r in fx.Results)
            {
                foreach (string m in r.FailingMeasures(rcs)) e.FailingMeasures.Add(m);
                if (r.Layer == PartParityFxRules.LayerFlame && r.Real.CorePx >= PartParityFxRules.PresenceMinPx) e.RealCorePresent = true;
                if (r.Real.EnvelopePx > 0) e.RealCoverageNonZero = true;
            }
            e.AllCoverageZero = fx.Results.Count > 0;
            e.GhostCoverageZero = fx.Results.Count > 0;
            foreach (PartParityFxViewResult r in fx.Results)
            {
                if (r.Real.EnvelopePx > 0 || r.Ghost.EnvelopePx > 0) e.AllCoverageZero = false;
                if (r.Ghost.EnvelopePx > 0) e.GhostCoverageZero = false;
            }
            foreach (PartParityViewRecord v in wc.ViewRecords)
                if (v?.Measures != null && (v.Measures.RealPx > 0 || v.Measures.GhostPx > 0)) e.AllCoverageZero = false;
            foreach (PartEvent ev in wc.State.Sink.Events) if (ev.eventType == PartEventType.EngineIgnited) e.EventsRecorded++;
            e.RealPlateLit = fx.PlateLit;
            e.PlateViewsFail = fx.PlateViewsFail;
            fx.Evidence = e;
            string flag = PartParityFxControls.FormatEvidence(e);
            wc.Flags.Add(flag);
            ParsekLog.Info(Tag, $"control {wc.CaseId}: {flag}");
        }

        // =====================================================================================
        // the sidecar and the pictures
        // =====================================================================================

        private static string PerSide(PartParityFxRun fx, Func<PartParityFxSide, string> value)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PartParityFxSide s in fx.Sides) d[s.Name] = value(s);
            return PartParityJson.KeyedBlock(PartParityJson.FxSideKeys, d);
        }

        private static string Strings(List<string> v) => PartParityJson.StrArray(v ?? new List<string>());

        /// <summary>The <c>fx</c>, <c>fxAtlas</c> and <c>strip</c> blocks of an FX case.</summary>
        internal static void FillSidecar(StepContext ctx, WaveCase wc, PartParityCaseSidecar s)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityFxOption o = fx.Option;
            var d = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "family", PartParityJson.Str(o.Family) },
                { "module", PartParityJson.Str(o.Module) },
                { "moduleIndex", PartParityJson.Int(o.ModuleIndex) },
                { "engineMode", PartParityJson.Str(fx.EngineMode) },
                { "throttle", PartParityJson.Num(o.Throttle) },
                { "samples", PartParityJson.Int(fx.SampleCount) },
                { "cullingMask", PartParityJson.Int(fx.CullingMask) },
                { "reference", o.Reference == null ? "null" : PartParityJson.Str(o.Reference) },
            };
            ReadThrottle(wc);
            if (fx.Engine != null || fx.Rcs != null)
                d["drive"] = PartParityJson.KeyedBlock(PartParityJson.FxDriveKeys, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "ignited", fx.DriveIgnited.HasValue ? PartParityJson.Bool(fx.DriveIgnited.Value) : "null" },
                    { "finalThrust", PartParityJson.Num(fx.DriveFinalThrust) },
                    { "currentThrottle", PartParityJson.Num(fx.DriveCurrentThrottle) },
                    { "recordedPower", PartParityJson.Num(fx.DriveRecordedPower) },
                    { "cheat", PartParityJson.Bool(ctx.FxCheatBefore.HasValue) },
                    { "mainThrottle", PartParityJson.Num(fx.DriveMainThrottle) },
                    { "independentThrottle", fx.DriveIndependentThrottle.HasValue ? PartParityJson.Bool(fx.DriveIndependentThrottle.Value) : "null" },
                });
            if (fx.Hold != null)
                d["hold"] = PartParityJson.KeyedBlock(PartParityJson.FxHoldKeys, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "maxDriftM", PartParityJson.Num(fx.Hold.MaxRenderDriftM) },
                    { "stepM", PartParityJson.Num(fx.Hold.MaxStepM) },
                    { "steps", PartParityJson.Int(fx.Hold.Steps) },
                });
            if (fx.Rcs != null)
                d["rcs"] = PartParityJson.KeyedBlock(PartParityJson.FxRcsKeys, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "input", fx.RcsChoice != null ? PartParityJson.NumArray(fx.RcsChoice.Direction) : "null" },
                    { "predicted", PartParityJson.IntArray(fx.RcsPredicted) },
                    { "fired", PartParityJson.IntArray(fx.RcsFired) },
                    { "nozzles", PartParityJson.Int(fx.RcsNozzles) },
                    { "ghostHidden", PartParityJson.Int(fx.RcsGhostHidden) },
                    { "powerRatio", PartParityJson.Num(fx.RcsPowerRatio) },
                });
            if (fx.Sides.Count > 0)
            {
                // A heat or lamp case waits for no plateau: no block, so none reads as missed.
                if (fx.Stochastic)
                {
                    d["warmupS"] = PerSide(fx, side => PartParityJson.Num(side.WarmupS));
                    d["plateau"] = PerSide(fx, side => PartParityJson.KeyedBlock(PartParityJson.FxPlateauKeys, new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        { "signal", side.SignalName == null ? "null" : PartParityJson.Str(side.SignalName) },
                        { "settled", PartParityJson.Bool(side.Settled) },
                        { "waitS", PartParityJson.Num(side.PlateauWaitS) },
                    }));
                }
                d["layers"] = PerSide(fx, side => PartParityJson.KeyedBlock(PartParityJson.FxLayerKeys, LayerFragments(side)));
                d["inventory"] = PerSide(fx, side => Strings(side.Inventory));
                d["particles"] = PerSide(fx, side => PartParityFxMetrics.ParticlesJson(
                    PartParityFxMetrics.ReduceParticles(side.ParticleSamples, fx.Nozzle, fx.Exhaust, PartParityFxRules.FramingNearParticleM)));
                d["structure"] = PartParityJson.KeyedBlock(PartParityJson.FxStructureKeys, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "fxMissingOnGhost", PartParityFxStructure.Json(fx.FxMissingOnGhost) },
                    { "fxExtraOnGhost", PartParityFxStructure.Json(fx.FxExtraOnGhost) },
                    { "fxDiffers", Strings(fx.FxDiffers) },
                    { "lightsDiffer", Strings(fx.LightsDiffer) },
                });
            }
            if (fx.NearBox != null)
                d["framing"] = PartParityJson.KeyedBlock(PartParityJson.FxFramingKeys, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "near", PartParityJson.BoxBlock(fx.NearBox.ToBox()) },
                    { "far", fx.FarBox != null ? PartParityJson.BoxBlock(fx.FarBox.ToBox()) : "null" },
                    { "clamped", PartParityJson.Bool(fx.FramingClamped) },
                });
            if (fx.Results.Count > 0)
            {
                var items = new List<string>();
                foreach (PartParityFxViewResult r in fx.Results) items.Add(r.Json());
                d["views"] = "[" + string.Join(",", items.ToArray()) + "]";
            }
            if (fx.Plate)
                d["plate"] = PartParityJson.KeyedBlock(PartParityJson.FxPlateKeys, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "litPct", PartParityJson.Num(fx.PlateLitPct) },
                    { "lightType", fx.PlateLightType == null ? "null" : PartParityJson.Str(fx.PlateLightType) },
                    { "range", PartParityJson.Num(fx.PlateRange) },
                    { "spotAngle", PartParityJson.Num(fx.PlateSpotAngle) },
                    { "cullingMask", fx.PlateCullingMask.HasValue ? PartParityJson.Int(fx.PlateCullingMask.Value) : "null" },
                });
            s.Fx = d;
            if (fx.AtlasTiles.Count > 0)
                s.FxAtlas = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "file", PartParityJson.Str(TestCommandPartParitySweep.FxAtlasFileName(wc.CaseId)) },
                    { "rows", PartParityJson.StrArray(PartParityJson.FxAtlasRowsFor(o.Family == PartParityFxRules.FamilyRcs)) },
                    { "cols", PartParityJson.StrArray(PartParityFxRules.Views) },
                    { "tile", PartParityJson.Int(fx.TilePx) },
                };
            if (fx.Strip.Count > 0)
                s.Strip = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "file", PartParityJson.Str(TestCommandPartParitySweep.StripFileName(wc.CaseId)) },
                    { "frames", PartParityJson.Int(fx.Strip["real"].Count) },
                };
        }

        private static Dictionary<string, string> LayerFragments(PartParityFxSide side)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string layer in PartParityFxRules.RendererLayers)
                d[layer] = side.LayerKeys.TryGetValue(layer, out List<string> keys) ? PartParityJson.StrArray(keys) : "[]";
            return d;
        }

        /// <summary>The FX atlas and the strip, beside the body atlas.</summary>
        internal static void WritePictures(StepContext ctx, WaveCase wc, PartParityCaseSidecar s)
        {
            PartParityFxRun fx = wc.Fx;
            if (s.FxAtlas != null)
            {
                string[] rowNames = PartParityJson.FxAtlasRowsFor(fx.Option.Family == PartParityFxRules.FamilyRcs);
                var rows = new byte[rowNames.Length][][];
                byte[] blank = new byte[fx.TilePx * fx.TilePx * 4];
                for (int r = 0; r < rowNames.Length; r++)
                {
                    rows[r] = new byte[PartParityFxRules.Views.Length][];
                    for (int c = 0; c < PartParityFxRules.Views.Length; c++)
                        rows[r][c] = fx.AtlasTiles.TryGetValue(rowNames[r], out Dictionary<string, byte[]> cols)
                                     && cols.TryGetValue(PartParityFxRules.Views[c], out byte[] tile) ? tile : blank;
                }
                byte[] atlas = PartParityImage.BuildAtlas(rows, PartParityFxRules.Views.Length, fx.TilePx, out int w, out int h);
                FileIOUtils.SafeWriteBytes(PartParityPng.EncodeRgba(w, h, atlas),
                    Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.FxAtlasFileName(wc.CaseId)), Tag);
                fx.AtlasTiles.Clear();
            }
            if (s.Strip != null && fx.Strip.Count > 0)
            {
                int frames = Math.Min(fx.Strip["real"].Count, fx.Strip["ghost"].Count);
                if (frames > 0)
                {
                    var rows = new[] { fx.Strip["real"].GetRange(0, frames).ToArray(), fx.Strip["ghost"].GetRange(0, frames).ToArray() };
                    byte[] strip = PartParityImage.BuildAtlas(rows, frames, StripTilePx, out int w, out int h);
                    FileIOUtils.SafeWriteBytes(PartParityPng.EncodeRgba(w, h, strip),
                        Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.StripFileName(wc.CaseId)), Tag);
                }
                fx.Strip.Clear();
            }
        }
    }
}
