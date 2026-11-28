using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Parsek.InGameTests;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>The live half of one dust case (design G.2.2): the engine held over its ground
    /// slot, the ghost at its own slot east of it, stock's raycast samples and the sample sets.</summary>
    internal sealed class PartParityDustRun
    {
        internal PartParityHeldSlot RealSlot;
        internal PartParityHeldSlot GhostSlot;
        internal double RealGroundM;
        internal double GhostGroundM;
        internal PartParityFxHold Hold;
        internal ModuleEngines Engine;
        internal ModuleSurfaceFX SurfaceFx;
        internal GhostCoOrbitalRig.LiveGhost Ghost;
        internal bool Widened;
        internal bool Ignited;
        internal float PressRealtime;
        internal float WarmStartRealtime;
        internal float LastCoverageRealtime;
        internal bool Warm;
        internal bool PlateauSettled;
        internal double PlateauMin = double.NaN;
        internal double PlateauMax = double.NaN;
        internal double WarmupS;
        internal readonly List<KeyValuePair<float, double>> Coverage = new List<KeyValuePair<float, double>>();
        internal int Rounds;
        internal int LazyBuilds;
        internal float Throttle = float.NaN;
        internal double NearOrthoM;
        internal double FarOrthoM;
        internal double StandM;
        internal readonly List<PartParityDustRaycastSample> Raycast = new List<PartParityDustRaycastSample>();
        internal readonly Dictionary<string, PartParityFxSampleSet> Sets = new Dictionary<string, PartParityFxSampleSet>(StringComparer.Ordinal);
        internal readonly Dictionary<string, PartParityFxAxis> Axes = new Dictionary<string, PartParityFxAxis>(StringComparer.Ordinal);
        internal readonly List<PartParityFxViewResult> Results = new List<PartParityFxViewResult>();
        internal PartParityDustRaycastSummary Summary;
        internal readonly SortedSet<string> RealNames = new SortedSet<string>(StringComparer.Ordinal);
        internal readonly SortedSet<string> GhostNames = new SortedSet<string>(StringComparer.Ordinal);
        internal bool Done;
    }

    internal static partial class PartParityBurstDriver
    {
        /// <summary>The longest a dust case waits for its engine to ignite after the press.</summary>
        internal const double DustIgnitionTimeoutSeconds = 5.0;

        /// <summary>The terrain capture layer (PQS scenery): never in a dust render's mask.</summary>
        internal const int TerrainLayer = 15;

        /// <summary>Raised when a dust renderer lives on the terrain layer (it renders nowhere).</summary>
        internal const string FlagDustOnTerrainLayer = "dust-on-terrain-layer";

        private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // =====================================================================================
        // the dust wave
        // =====================================================================================

        /// <summary>One dust wave: spawn every engine over its slot, hold it, press it, build its
        /// ghost and drive the ghost's launch dust as playback does, warm up to the plateau, take
        /// the rounds, measure, write, tear down.</summary>
        internal static IEnumerator RunDustWave(StepContext ctx, List<WaveCase> wave)
        {
            ctx.Live = wave;
            bool solo = wave.Count == 1 && wave[0].Solo;
            PartParitySweepRunner.WriteInflight(ctx, wave.ConvertAll(c => c.CaseId), solo);
            for (int i = 0; i < wave.Count; i++)
            {
                WaveCase wc = wave[i];
                int slot = i;
                if (wc.Burst?.Dust == null)
                {
                    wc.Status = wc.Status ?? TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = wc.Detail ?? "no dust run";
                    continue;
                }
                PartParityFxDriver.EnsureCheat(ctx, wc);
                PartParitySweepRunner.Guard(ctx, wc, "spawn", () => SpawnDust(ctx, wc, slot, wave.Count));
            }
            float deadline = Time.realtimeSinceStartup + (float)PartParitySweepPlan.SpawnTimeoutSeconds;
            while (true)
            {
                if (PartParitySweepRunner.CheckHost(ctx, "dust spawn wait")) break;
                bool pending = false;
                foreach (WaveCase wc in wave)
                {
                    if (wc.Status != null || wc.Real != null) continue;
                    PartParitySweepRunner.Guard(ctx, wc, "spawn", () => { if (!TryReady(ctx, wc)) pending = true; });
                    if (pending && Time.realtimeSinceStartup > deadline && wc.Real == null)
                    {
                        wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                        wc.Detail = "never loaded+unpacked in " + PartParitySweepPlan.SpawnTimeoutSeconds.ToString("0", Ic) + " s";
                    }
                }
                if (!pending || Time.realtimeSinceStartup > deadline) break;
                yield return null;
            }
            float settleUntil = Time.realtimeSinceStartup + 1.0f;
            while (ctx.HostDamage == null && Time.realtimeSinceStartup < settleUntil) yield return null;
            foreach (WaveCase wc in wave)
                if (wc.Status == null && wc.Real != null) PartParitySweepRunner.Guard(ctx, wc, "prepare", () => PressDust(wc));

            // ignition, the ghost, the warm-up to the plateau
            var eof = new WaitForEndOfFrame();
            while (ctx.HostDamage == null)
            {
                yield return eof;
                if (PartParitySweepRunner.CheckHost(ctx, "dust warm-up")) break;
                bool pending = false;
                foreach (WaveCase wc in wave)
                {
                    if (wc.Status != null || wc.Real == null || wc.Burst.Dust.Warm) continue;
                    PartParitySweepRunner.Guard(ctx, wc, "prepare", () => StepWarmUp(ctx, wc));
                    if (wc.Status == null && !wc.Burst.Dust.Warm) pending = true;
                }
                if (!pending) break;
            }

            // the rounds, one per frame per case
            while (ctx.HostDamage == null)
            {
                bool pending = false;
                foreach (WaveCase wc in wave)
                {
                    if (wc.Status != null || wc.Real == null || wc.Burst.Dust.Done) continue;
                    PartParitySweepRunner.Guard(ctx, wc, "capture", () => DustRound(ctx, wc));
                    if (wc.Status == null && !wc.Burst.Dust.Done) pending = true;
                }
                if (!pending) break;
                yield return eof;
                if (PartParitySweepRunner.CheckHost(ctx, "dust capture")) break;
            }

            foreach (WaveCase wc in wave)
            {
                if (ctx.HostDamage != null) break;
                PartParitySweepRunner.WriteInflight(ctx, new List<string> { wc.CaseId }, solo);
                if (wc.Status == null && wc.Burst?.Dust != null && wc.Burst.Dust.Done)
                    PartParitySweepRunner.Guard(ctx, wc, "measure", () => MeasureDust(wc));
                PartParitySweepRunner.Guard(ctx, wc, "write", () => PartParitySweepRunner.FinishCase(ctx, wc));
                PartParitySweepRunner.DeleteInflight(ctx);
            }
            if (ctx.HostDamage != null) PartParitySweepRunner.DeleteInflight(ctx);
            IEnumerator down = PartParitySweepRunner.TearDownWave(ctx, wave);
            try { while (down.MoveNext()) yield return down.Current; }
            finally { PartParitySweepRunner.DisposeNested(down); }
            ctx.Live = null;
        }

        // ----- the slot and the spawn -----

        private static Transform ThrustTransform(Part part, string name)
        {
            if (part == null) return null;
            string n = string.IsNullOrEmpty(name) ? "thrustTransform" : name;
            Transform[] ts = part.FindModelTransforms(n);
            return ts != null && ts.Length > 0 ? ts[0] : null;
        }

        private static void SpawnDust(StepContext ctx, WaveCase wc, int slot, int count)
        {
            PartParityDustRun d = wc.Burst.Dust;
            PartParityFxOption o = wc.Burst.Option;
            AvailablePart info = wc.Entry?.Info;
            if (info?.partPrefab == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = wc.Detail ?? "no part info";
                return;
            }
            CelestialBody body = ctx.Host != null ? ctx.Host.mainBody : FlightGlobals.GetHomeBody();
            double mpd = 2.0 * Math.PI * body.Radius / 360.0;
            double lat = PartParityAirPlan.PadLatitudeDeg + PartParityBurstPlan.DustSlotNorthOffsetM(slot, count) / mpd;
            double lon = PartParityAirPlan.PadLongitudeDeg + PartParityAirPlan.BandEastOfPadM / mpd;
            bool water = o.Surface == PartParityFxRules.SurfaceSlotWater;
            if (water)
            {
                double eastM = PartParityBurstPlan.FindWaterEastM(m => body.TerrainAltitude(lat, PartParityAirPlan.PadLongitudeDeg + m / mpd, true));
                if (double.IsNaN(eastM))
                {
                    wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = "no water slot east of the pad within " + PartParityBurstPlan.WaterSearchEndM.ToString("0", Ic) + " m";
                    return;
                }
                lon = PartParityAirPlan.PadLongitudeDeg + eastM / mpd;
            }
            double glon = lon + PartParityBurstPlan.DustSlotSpacingM / mpd;
            d.RealGroundM = water ? 0.0 : body.TerrainAltitude(lat, lon, true);
            d.GhostGroundM = water ? 0.0 : body.TerrainAltitude(lat, glon, true);

            // the exhaust straight down, the nozzle (the surface FX's thrust transform) at the slot height
            Quaternion upright = GhostCoOrbitalRig.UprightRotation(body, lat, lon, d.RealGroundM + 100.0, out Vector3 up);
            Transform tt = ThrustTransform(info.partPrefab, o.ThrustTransformName);
            Transform pt = info.partPrefab.transform;
            Vector3 exhaustLocal = tt != null ? pt.InverseTransformDirection(tt.forward) : Vector3.down;
            Vector3 nozzleLocal = tt != null ? pt.InverseTransformPoint(tt.position) : Vector3.zero;
            Quaternion rot = Quaternion.FromToRotation(upright * exhaustLocal, -up) * upright;
            double nozzleUp = Vector3.Dot(rot * nozzleLocal, up);
            double rootReal = PartParityBurstPlan.DustRootAltitudeM(d.RealGroundM, o.SlotHeightM, nozzleUp);
            double rootGhost = PartParityBurstPlan.DustRootAltitudeM(d.GhostGroundM, o.SlotHeightM, nozzleUp);
            Quaternion srf = Quaternion.Inverse(body.bodyTransform.rotation) * rot;
            d.RealSlot = new PartParityHeldSlot { Body = body, Latitude = lat, Longitude = lon, AltitudeM = rootReal, SurfaceRelativeRotation = srf };
            d.GhostSlot = new PartParityHeldSlot { Body = body, Latitude = lat, Longitude = glon, AltitudeM = rootGhost, SurfaceRelativeRotation = srf };
            d.NearOrthoM = Math.Max(10.0, 0.6 * o.SurfaceFxMaxDistanceM);
            d.FarOrthoM = 2.5 * d.NearOrthoM;
            d.StandM = Math.Max(PartParityBurstPlan.BurstCameraStandM, 3.0 * o.SurfaceFxMaxDistanceM);

            List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(1);
            ConfigNode subject = GhostCoOrbitalRig.AuthorPartNode(info, ids[0], 0, Vector3.zero, null, wc.RealVariant, (mn, modName) =>
            {
                if (modName != null && modName.StartsWith("ModuleEngines", StringComparison.Ordinal))
                {
                    mn.SetValue("independentThrottle", "True", true);
                    mn.SetValue("independentThrottlePercentage", "100", true);
                    mn.SetValue("thrustPercentage", "100", true);
                    mn.SetValue("exhaustDamage", "False", true);
                }
            });
            Orbit src = ctx.Host.orbit;
            var placeholder = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
            ConfigNode node = ProtoVessel.CreateVesselNode(PartParityCaseRules.VesselName(wc.CaseId), VesselType.Probe, placeholder, 0, new[] { subject });
            wc.Pid = GhostCoOrbitalRig.SpawnInAir(node, body, lat, lon, rootReal, PartParityAirPlan.HeldDescentMps, wc.CaseId, Tag, rot);
            wc.SpawnRealtime = Time.realtimeSinceStartup;
            ParsekLog.Info(Tag, $"dust spawn {wc.CaseId}: pid={wc.Pid.ToString(Ic)} slot={slot.ToString(Ic)}/{count.ToString(Ic)} surface={o.Surface} " +
                $"lat={lat.ToString("0.00000", Ic)} lon={lon.ToString("0.00000", Ic)} ground={d.RealGroundM.ToString("0.0", Ic)}m " +
                $"ghostGround={d.GhostGroundM.ToString("0.0", Ic)}m height={o.SlotHeightM.ToString("0.0", Ic)}m nozzleUp={nozzleUp.ToString("0.00", Ic)}m " +
                $"root={rootReal.ToString("0.0", Ic)}m reach={o.SurfaceFxMaxDistanceM.ToString("0.0", Ic)}m thrustTransform={(tt != null ? tt.name : "<none>")}");
            if (wc.Pid == 0)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = "SpawnAtPosition returned pid=0";
            }
        }

        /// <summary>Widens the case vessel's load and pack ranges once it exists (a water slot sits
        /// kilometres from the host on the pad), then takes it and holds it once it is unpacked.</summary>
        private static bool TryReady(StepContext ctx, WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            Vessel v = FlightRecorder.FindVesselByPid(wc.Pid);
            if (v == null) return false;
            if (!d.Widened)
            {
                WidenDustRanges(v);
                d.Widened = true;
                ParsekLog.Info(Tag, $"case {wc.CaseId}: vessel ranges widened to load {PartParityBurstPlan.DustLoadRangeM.ToString("0", Ic)} m");
            }
            if (!v.loaded || v.packed || v.rootPart == null || v.parts == null || v.parts.Count == 0) return false;
            wc.Real = v;
            wc.ReadyRealtime = Time.realtimeSinceStartup;
            d.Hold = PartParityFxHold.AttachAtSlot(v, ctx.Host, d.RealSlot);
            Part part = v.rootPart;
            int idx = wc.Burst.Option.ThrustProviderModuleIndex;
            d.Engine = idx >= 0 && idx < part.Modules.Count ? part.Modules[idx] as ModuleEngines : null;
            int sfx = wc.Burst.Option.ModuleIndex;
            d.SurfaceFx = sfx >= 0 && sfx < part.Modules.Count ? part.Modules[sfx] as ModuleSurfaceFX : null;
            if (d.Engine == null || d.SurfaceFx == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupActionFailed;
                wc.Detail = $"engine module {idx.ToString(Ic)} or surface FX module {sfx.ToString(Ic)} not found on the live part";
            }
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void WidenDustRanges(Vessel v)
        {
            float load = (float)PartParityBurstPlan.DustLoadRangeM;
            var s = new VesselRanges.Situation(load, load + 500f, load + 1000f, load - 1000f);
            v.vesselRanges.flying = s;
            v.vesselRanges.landed = s;
            v.vesselRanges.splashed = s;
            v.vesselRanges.subOrbital = s;
            v.vesselRanges.prelaunch = s;
        }

        private static void PressDust(WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            d.Hold?.ResetEvidence();
            BaseEvent ev = d.Engine.Events["Activate"];
            if (ev != null) ev.Invoke();
            else d.Engine.Activate();
            d.PressRealtime = Time.realtimeSinceStartup;
            ParsekLog.Info(Tag, $"case {wc.CaseId}: dust engine {d.Engine.moduleName} pressed (independent throttle 100%)");
        }

        // ----- ignition, the ghost, the warm-up -----

        private static void StepWarmUp(StepContext ctx, WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            float now = Time.realtimeSinceStartup;
            if (!d.Ignited)
            {
                bool lit = d.Engine.EngineIgnited && d.Engine.currentThrottle > 0.5f;
                if (!lit)
                {
                    if (now - d.PressRealtime > DustIgnitionTimeoutSeconds)
                    {
                        wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                        wc.Detail = "engine not lit " + DustIgnitionTimeoutSeconds.ToString("0", Ic) + " s after the press";
                    }
                    return;
                }
                d.Ignited = true;
                d.Throttle = d.Engine.currentThrottle;
                d.WarmStartRealtime = now;
                if (!PartParityBurstPlan.IsRealVsReal(wc.Burst.Control)) BuildDustGhost(wc);
            }
            DriveGhostDust(wc);
            if (now - d.LastCoverageRealtime < PartParityFxRules.PlateauCoveragePeriodSeconds) return;
            d.LastCoverageRealtime = now;
            d.Coverage.Add(new KeyValuePair<float, double>(now, RealDustCoverage(ctx, wc)));
            double run = now - d.WarmStartRealtime;
            double life = LongestDustLifetime(wc);
            bool window = PartParityFxDriver.PlateauWindow(d.Coverage, out double min, out double max);
            if (window) { d.PlateauMin = min; d.PlateauMax = max; }
            d.PlateauSettled = window && PartParityFxRules.PlateauSettled(run, life, min, max);
            if (d.PlateauSettled || run >= PartParityFxRules.PlateauCapFor(life))
            {
                d.Warm = true;
                d.WarmupS = run;
                ParsekLog.Info(Tag, $"case {wc.CaseId}: dust warm {(d.PlateauSettled ? "settled" : "capped")} after " +
                    $"{run.ToString("0.00", Ic)} s coverage={(window ? min.ToString("0", Ic) + ".." + max.ToString("0", Ic) : "-")} " +
                    $"life={life.ToString("0.00", Ic)} s signal={PartParityFxRules.PlateauSignalCoverage}");
            }
        }

        private static void BuildDustGhost(WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            string recId = "ppd-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!GhostCoOrbitalRig.TryBuildGhostFromLiveVessel(wc.Real, recId, "ParsekPartParity_" + wc.CaseId,
                    out GhostCoOrbitalRig.LiveGhost g, out string fail))
            {
                wc.Flags.Add(PartParityCaseCapture.FlagGhostNotBuilt);
                wc.Detail = wc.Detail ?? fail;
                return;
            }
            d.Ghost = g;
            if (g.Recording.PartEvents == null) g.Recording.PartEvents = new List<PartEvent>();
            PoseDustGhost(d);
            g.Build.root.SetActive(true);
            GhostCoOrbitalRig.NeutralisePhysics(g.Build.root, out int _, out int _);
            GhostPlaybackState state = g.State;
            state.lastInterpolatedBodyName = d.GhostSlot.Body.name;
            state.lastInterpolatedAltitude = d.GhostSlot.AltitudeM;
            state.visualFxSuppressed = false;
            GhostPlaybackEngine.ApplyUnsuppressedFxRestore(state, true);
            double ut = Planetarium.GetUniversalTime();
            uint pid = wc.Real.rootPart.persistentId;
            g.Recording.PartEvents.Add(new PartEvent
            {
                ut = ut, partPersistentId = pid, eventType = PartEventType.EngineIgnited,
                partName = wc.Entry?.Name, value = d.Throttle, moduleIndex = wc.Burst.Option.RecorderKeyIndex,
            });
            GhostPlaybackLogic.ApplyPartEvents(-1, g.Recording, ut, state, true);
            if (!PartParityBurstPlan.HidesGhostDust(wc.Burst.Control))
            {
                state.launchDustGroundRefAltitude = d.GhostGroundM;
                state.launchDustPendingBuild = true;
            }
            ParsekLog.Info(Tag, $"case {wc.CaseId}: dust ghost built engineKey={wc.Burst.Option.RecorderKeyIndex.ToString(Ic)} " +
                $"throttle={d.Throttle.ToString("0.00", Ic)} dust={(state.launchDustPendingBuild ? "pending" : "hidden")}");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PoseDustGhost(PartParityDustRun d)
        {
            GameObject root = d.Ghost?.Build?.root;
            if (root == null) return;
            PartParityHeldSlot s = d.GhostSlot;
            root.transform.SetPositionAndRotation((Vector3)SlotWorld(s), s.Body.bodyTransform.rotation * s.SurfaceRelativeRotation);
        }

        /// <summary>The ghost's launch dust, driven the way playback drives it (the engine's power
        /// summed from the ghost's own engine state, the latched ground reference).</summary>
        private static void DriveGhostDust(WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            if (d.Ghost == null) return;
            PoseDustGhost(d);
            GhostPlaybackState state = d.Ghost.State;
            if (PartParityBurstPlan.HidesGhostDust(wc.Burst.Control)) return;
            float power = GhostPlaybackLogic.SumEnginePower(state);
            GhostPlaybackEngine.DriveLaunchDustFrame(-1, state, power, d.GhostSlot.AltitudeM, d.GhostGroundM, ref d.LazyBuilds);
        }

        private static double LongestDustLifetime(WaveCase wc)
        {
            double life = 0.0;
            foreach (Renderer r in RealDustRenderers(wc))
            {
                ParticleSystem ps = r != null ? r.GetComponent<ParticleSystem>() : null;
                if (ps != null) life = Math.Max(life, ps.main.startLifetime.constantMax);
            }
            return life;
        }

        /// <summary>Stock's dust renderers of the case: its surface FX's current and next
        /// <c>SurfaceFX</c> and its launch pad effect (private fields, read by name).</summary>
        private static List<Renderer> RealDustRenderers(WaveCase wc)
        {
            var list = new List<Renderer>();
            ModuleSurfaceFX m = wc.Burst?.Dust?.SurfaceFx;
            if (m == null) return list;
            foreach (string name in new[] { "srfFX", "srfFXnext", "padFX" })
            {
                FieldInfo f = typeof(ModuleSurfaceFX).GetField(name, AnyInstance);
                if (f?.GetValue(m) is Component c && c != null)
                    foreach (Renderer r in c.GetComponentsInChildren<Renderer>(true))
                        if (r != null && !list.Contains(r)) list.Add(r);
            }
            return list;
        }

        private static List<Renderer> GhostDustRenderers(WaveCase wc)
        {
            var list = new List<Renderer>();
            GameObject dust = wc.Burst?.Dust?.Ghost?.State?.launchDustInfo?.dustObject;
            if (dust != null) list.AddRange(dust.GetComponentsInChildren<Renderer>(true));
            return list;
        }

        private static List<Renderer> FlameOf(IEnumerable<Renderer> rs, ICollection<Renderer> excluded)
        {
            var list = new List<Renderer>();
            foreach (Renderer r in rs)
            {
                if (r == null || (excluded != null && excluded.Contains(r))) continue;
                string layer = PartParityFxRules.ClassifyFxRenderer(r.GetType().Name,
                    r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : null);
                if (layer == PartParityFxRules.LayerFlame) list.Add(r);
            }
            return list;
        }

        private static List<Renderer> RealFlame(WaveCase wc)
            => wc.Real?.rootPart != null ? FlameOf(wc.Real.rootPart.GetComponentsInChildren<Renderer>(true), null) : new List<Renderer>();

        private static List<Renderer> GhostFlame(WaveCase wc)
        {
            GameObject root = wc.Burst?.Dust?.Ghost?.Build?.root;
            if (root == null) return new List<Renderer>();
            return FlameOf(root.GetComponentsInChildren<Renderer>(true), new HashSet<Renderer>(GhostDustRenderers(wc)));
        }

        /// <summary>Every renderer a dust render may toggle: P8's hide set and every dust case of
        /// the live wave (its stock dust, its ghost), so a neighbour never draws into a frame.</summary>
        private static List<Renderer> DustHideSet(StepContext ctx, WaveCase wc)
        {
            var all = PartParityFxDriver.HideSet();
            foreach (WaveCase c in ctx.Live ?? new List<WaveCase> { wc })
            {
                if (c.Burst?.Dust == null) continue;
                all.AddRange(RealDustRenderers(c));
                all.AddRange(GhostDustRenderers(c));
                GameObject root = c.Burst.Dust.Ghost?.Build?.root;
                if (root != null) all.AddRange(root.GetComponentsInChildren<Renderer>(true));
            }
            return all;
        }

        private static int MaskOf(WaveCase wc, IEnumerable<Renderer> rs)
        {
            int mask = 1;
            foreach (Renderer r in rs)
            {
                if (r == null) continue;
                int layer = r.gameObject.layer;
                if (layer == TerrainLayer)
                {
                    if (!wc.Flags.Contains(FlagDustOnTerrainLayer)) wc.Flags.Add(FlagDustOnTerrainLayer);
                    continue;
                }
                mask |= 1 << layer;
            }
            return mask;
        }

        private static Vector3 Footprint(PartParityHeldSlot s, double groundM)
            => (Vector3)s.Body.GetWorldSurfacePosition(s.Latitude, s.Longitude, groundM);

        private static void PoseDustView(PartParityCapture.FxFrame f, PartParityDustRun d, string view, Vector3 foot, Vector3 up, Vector3 north,
            double heightM, out double ortho)
        {
            float stand = (float)d.StandM;
            ortho = view == PartParityFxRules.ViewDustFar ? d.FarOrthoM : d.NearOrthoM;
            if (view == PartParityFxRules.ViewDustTop)
                f.Pose(foot + up * stand, Quaternion.LookRotation(-up, north), ortho, 1.0, 2.0 * stand);
            else
                f.Pose(foot + up * (float)(heightM * 0.5) - north * stand, Quaternion.LookRotation(north, up), ortho, 1.0, 2.0 * stand);
        }

        private static double RealDustCoverage(StepContext ctx, WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            List<Renderer> dust = RealDustRenderers(wc);
            using (PartParityCapture.FxFrame f = ctx.Capture.BeginFxFrame(DustHideSet(ctx, wc), MaskOf(wc, dust), false))
            {
                Frame(d.RealSlot.Body, new[] { d.RealSlot.Latitude, d.RealSlot.Longitude, d.RealGroundM }, out Vector3 up, out Vector3 north, out Vector3 _);
                PoseDustView(f, d, PartParityFxRules.ViewDustSide, Footprint(d.RealSlot, d.RealGroundM), up, north, wc.Burst.Option.SlotHeightM, out double _);
                byte[] px = f.Render(dust, Color.black);
                int lit = 0;
                for (int i = 0; i < px.Length; i += 4) if (px[i] > 8 || px[i + 1] > 8 || px[i + 2] > 8) lit++;
                return lit;
            }
        }

        // ----- the rounds -----

        /// <summary>One read of stock's raycast; the hit point and normal in the slot frame (east,
        /// up, north), the point relative to the thrust transform, so the sidecar is origin-free.</summary>
        private static PartParityDustRaycastSample ReadRaycast(WaveCase wc, ModuleSurfaceFX m)
        {
            var s = new PartParityDustRaycastSample();
            if (m == null) return s;
            object hit = typeof(ModuleSurfaceFX).GetField("hit", AnyInstance)?.GetValue(m);
            object rayHit = typeof(ModuleSurfaceFX).GetField("raycastHit", AnyInstance)?.GetValue(m);
            object dist = typeof(ModuleSurfaceFX).GetField("distance", AnyInstance)?.GetValue(m);
            int surface = hit != null ? Convert.ToInt32(hit, Ic) : 0;
            s.Hit = rayHit is bool b ? b && surface != 0 : surface != 0;
            s.Surface = s.Hit ? PartParityBurstPlan.SurfaceName(surface) : PartParityFxRules.DustSurfaceNone;
            s.DistanceM = dist != null ? Convert.ToDouble(dist, Ic) : double.NaN;
            s.ScaledFx = m.ScaledFX;
            if (!s.Hit) return s;
            PartParityHeldSlot slot = wc.Burst.Dust.RealSlot;
            Frame(slot.Body, new[] { slot.Latitude, slot.Longitude, slot.AltitudeM }, out Vector3 up, out Vector3 north, out Vector3 east);
            Transform nozzle = ThrustTransform(m.part, wc.Burst.Option.ThrustTransformName);
            Vector3 from = nozzle != null ? nozzle.position : m.transform.position;
            double[] e = V(east), u = V(up), n = V(north);
            s.PointM = PartParityBurstPlan.SlotFrameOffset(e, u, n, V(from), V(m.point));
            s.Normal = PartParityBurstPlan.SlotFrameOffset(e, u, n, new double[3], V(m.normal));
            return s;
        }

        private static PartParityFxSampleSet DustSet(StepContext ctx, PartParityDustRun d, string view, string layer, bool real)
        {
            string key = PartParityBurstRun.AccKey(view, layer, real);
            if (!d.Sets.TryGetValue(key, out PartParityFxSampleSet set))
            {
                set = new PartParityFxSampleSet(layer, ctx.Capture.Tile, ctx.Capture.Tile);
                d.Sets[key] = set;
            }
            return set;
        }

        private static void DustRound(StepContext ctx, WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            DriveGhostDust(wc);
            if (PartParitySweepRunner.CaseVesselLost(wc))
            {
                wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                wc.Detail = wc.Detail ?? "dust vessel lost during the capture";
                return;
            }
            d.Raycast.Add(ReadRaycast(wc, d.SurfaceFx));
            bool rvr = PartParityBurstPlan.IsRealVsReal(wc.Burst.Control);
            var sides = new List<bool>();
            if (rvr) sides.Add(d.Rounds % 2 == 0);
            else { sides.Add(true); sides.Add(false); }
            List<Renderer> realDust = RealDustRenderers(wc), realFlame = RealFlame(wc);
            List<Renderer> ghostDust = GhostDustRenderers(wc), ghostFlame = GhostFlame(wc);
            foreach (Renderer r in realDust) if (r != null) d.RealNames.Add(PartParityBurstPlan.StripClone(r.gameObject.name));
            foreach (Renderer r in ghostDust) if (r != null) d.GhostNames.Add(PartParityBurstPlan.StripClone(r.gameObject.name));
            var shown = new List<Renderer>(realDust);
            shown.AddRange(realFlame);
            shown.AddRange(ghostDust);
            shown.AddRange(ghostFlame);
            using (PartParityCapture.FxFrame f = ctx.Capture.BeginFxFrame(DustHideSet(ctx, wc), MaskOf(wc, shown), false))
            {
                foreach (bool sideReal in sides)
                {
                    // real-vs-real: the ghost side is the real part's next round, framed the same way
                    bool fromReal = rvr || sideReal;
                    PartParityHeldSlot slot = fromReal ? d.RealSlot : d.GhostSlot;
                    double ground = fromReal ? d.RealGroundM : d.GhostGroundM;
                    Vector3 foot = Footprint(slot, ground);
                    Frame(slot.Body, new[] { slot.Latitude, slot.Longitude, ground }, out Vector3 up, out Vector3 north, out Vector3 _);
                    foreach (string view in PartParityFxRules.DustViews)
                    {
                        PoseDustView(f, d, view, foot, up, north, wc.Burst.Option.SlotHeightM, out double ortho);
                        if (!d.Axes.ContainsKey(view))
                        {
                            Vector3 c = f.ToCamera(foot);
                            Vector3 a = f.ToCamera(foot + up) - c;
                            d.Axes[view] = PartParityFxFraming.AxisFor(view, new double[] { c.x, c.y }, new double[] { a.x, a.y },
                                ortho, ctx.Capture.Tile, wc.Burst.Option.SurfaceFxMaxDistanceM);
                        }
                        DustSet(ctx, d, view, PartParityFxRules.LayerDust, sideReal).Add(f.Render(fromReal ? realDust : ghostDust, Color.black));
                        DustSet(ctx, d, view, PartParityFxRules.LayerFlame, sideReal).Add(f.Render(fromReal ? realFlame : ghostFlame, Color.black));
                    }
                }
            }
            d.Rounds++;
            if (d.Rounds >= PartParityBurstPlan.DustSampleRounds(wc.Burst.Control)) d.Done = true;
        }

        private static void MeasureDust(WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            d.Summary = PartParityBurstPlan.SummarizeRaycast(d.Raycast);
            foreach (string view in PartParityFxRules.DustViews)
            {
                d.Axes.TryGetValue(view, out PartParityFxAxis axis);
                foreach (string layer in new[] { PartParityFxRules.LayerDust, PartParityFxRules.LayerFlame })
                {
                    if (!d.Sets.TryGetValue(PartParityBurstRun.AccKey(view, layer, true), out PartParityFxSampleSet real)
                        || !d.Sets.TryGetValue(PartParityBurstRun.AccKey(view, layer, false), out PartParityFxSampleSet ghost)) continue;
                    d.Results.Add(PartParityFxViewResult.Measure(view, PartParityStateRules.RouteEvent, layer, axis, real, ghost));
                }
            }
            d.Sets.Clear();
            string status = PartParityBurstPlan.DustSetupStatus(d.Summary.Hits);
            if (status != TestCommandPartParitySweep.SetupOk)
            {
                wc.Status = status;
                wc.Detail = wc.Detail ?? "stock's raycast hit nothing in " + d.Raycast.Count.ToString(Ic) + " sample(s)";
            }
            if (d.Hold != null && !PartParityFxRules.HoldOk(d.Hold.MaxRenderDriftM))
                wc.Flags.Add(PartParityFxDriver.FlagHoldDriftPrefix + d.Hold.MaxRenderDriftM.ToString("0.000", Ic));
            if (wc.ControlId != null)
            {
                var e = new PartParityDustControlEvidence
                {
                    SetupStatus = wc.Status ?? TestCommandPartParitySweep.SetupOk,
                    StockRaycastHit = d.Summary.Hits > 0,
                };
                foreach (PartParityFxViewResult r in d.Results)
                {
                    bool dust = r.Layer == PartParityFxRules.LayerDust;
                    foreach (string m in r.FailingMeasures(false)) (dust ? e.FailingDustMeasures : e.FailingFlameMeasures).Add(m);
                    if (dust && r.Real != null && r.Real.EnvelopePx > 0) e.RealDustCovered = true;
                    if (dust && r.Ghost != null && r.Ghost.EnvelopePx > 0) e.GhostDustCovered = true;
                }
                wc.Flags.Add(PartParityBurstPlan.FormatDustEvidence(e));
            }
            ParsekLog.Info(Tag, $"dust case {wc.CaseId}: rounds={d.Rounds.ToString(Ic)} hits={d.Summary.Hits.ToString(Ic)} " +
                $"misses={d.Summary.Misses.ToString(Ic)} surface={d.Summary.Surface} entries={d.Results.Count.ToString(Ic)} " +
                $"warmup={d.WarmupS.ToString("0.00", Ic)}s plateau={(d.PlateauSettled ? "settled" : "capped")} " +
                $"holdDrift={(d.Hold != null ? d.Hold.MaxRenderDriftM.ToString("0.000", Ic) : "-")}m status={wc.Status ?? "ok"}");
        }

        // ----- the sidecar and the atlas -----

        private static string Vec(double[] v) => v == null ? "null" : PartParityJson.NumArray(v);

        private static void FillDustSidecar(WaveCase wc, PartParityCaseSidecar s)
        {
            PartParityDustRun d = wc.Burst.Dust;
            if (d == null || d.Results.Count == 0 || d.Summary == null) return;
            PartParityFxOption o = wc.Burst.Option;
            PartParityDustRaycastSummary sum = d.Summary;
            var samples = new List<string>();
            foreach (PartParityDustRaycastSample x in sum.Samples)
                samples.Add(PartParityJson.KeyedBlock(PartParityJson.DustRaycastSampleKeys, new Dictionary<string, string>
                {
                    { "hit", PartParityJson.Bool(x.Hit) },
                    { "surface", PartParityJson.Str(x.Surface ?? PartParityFxRules.DustSurfaceNone) },
                    { "distanceM", PartParityJson.Num(double.IsNaN(x.DistanceM) ? (double?)null : x.DistanceM) },
                    { "scaledFx", PartParityJson.Num(double.IsNaN(x.ScaledFx) ? (double?)null : x.ScaledFx) },
                }));
            var layers = new List<string> { PartParityFxRules.LayerDust, PartParityFxRules.LayerFlame };
            s.Dust = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "family", PartParityJson.Str(o.Family) },
                { "module", PartParityJson.Str(o.Module) },
                { "moduleIndex", PartParityJson.Int(o.ModuleIndex) },
                { "engineModule", PartParityJson.Str(o.EngineModule) },
                { "engineModuleIndex", PartParityJson.Int(o.ThrustProviderModuleIndex) },
                { "surface", PartParityJson.Str(o.Surface) },
                { "slotFraction", PartParityJson.Num(o.SlotFraction) },
                { "heightM", PartParityJson.Num(o.SlotHeightM) },
                { "maxDistanceM", PartParityJson.Num(o.SurfaceFxMaxDistanceM) },
                { "falloff", PartParityJson.Num(o.SurfaceFxFalloff) },
                { "fxMax", PartParityJson.Num(o.SurfaceFxFxMax) },
                { "throttle", PartParityJson.Num(float.IsNaN(d.Throttle) ? (double?)null : d.Throttle) },
                { "drive", PartParityJson.Str(d.Ignited ? "ignited" : "not-ignited") },
                { "warmupS", PartParityJson.Num(d.WarmupS) },
                { "plateau", PartParityJson.KeyedBlock(new[] { "settled", "min", "max" }, new Dictionary<string, string>
                    {
                        { "settled", PartParityJson.Bool(d.PlateauSettled) },
                        { "min", PartParityJson.Num(double.IsNaN(d.PlateauMin) ? (double?)null : d.PlateauMin) },
                        { "max", PartParityJson.Num(double.IsNaN(d.PlateauMax) ? (double?)null : d.PlateauMax) },
                    }) },
                { "hold", d.Hold != null ? PartParityJson.KeyedBlock(new[] { "maxRenderDriftM", "maxStepM", "steps" }, new Dictionary<string, string>
                    {
                        { "maxRenderDriftM", PartParityJson.Num(d.Hold.MaxRenderDriftM) },
                        { "maxStepM", PartParityJson.Num(d.Hold.MaxStepM) },
                        { "steps", PartParityJson.Int(d.Hold.Steps) },
                    }) : "null" },
                { "samples", PartParityJson.Int(d.Results[0].EvenSamples + d.Results[0].OddSamples) },
                { "raycast", PartParityJson.KeyedBlock(PartParityJson.DustRaycastKeys, new Dictionary<string, string>
                    {
                        { "surface", PartParityJson.Str(sum.Surface) },
                        { "hits", PartParityJson.Int(sum.Hits) },
                        { "misses", PartParityJson.Int(sum.Misses) },
                        { "distanceM", PartParityJson.Num(double.IsNaN(sum.DistanceM) ? (double?)null : sum.DistanceM) },
                        { "scaledFx", PartParityJson.Num(double.IsNaN(sum.ScaledFx) ? (double?)null : sum.ScaledFx) },
                        { "pointM", Vec(sum.PointM) },
                        { "normal", Vec(sum.Normal) },
                        { "samples", JoinArray(samples) },
                    }) },
                { "framing", PartParityJson.KeyedBlock(new[] { "nearOrthoM", "farOrthoM", "standM" }, new Dictionary<string, string>
                    {
                        { "nearOrthoM", PartParityJson.Num(d.NearOrthoM) },
                        { "farOrthoM", PartParityJson.Num(d.FarOrthoM) },
                        { "standM", PartParityJson.Num(d.StandM) },
                    }) },
                { "layers", PartParityJson.StrArray(layers) },
                { "atlas", PartParityJson.KeyedBlock(PartParityJson.FxAtlasKeys, new Dictionary<string, string>
                    {
                        { "file", PartParityJson.Str(TestCommandPartParitySweep.DustAtlasFileName(wc.CaseId)) },
                        { "rows", PartParityJson.StrArray(PartParityJson.DustAtlasRows) },
                        { "cols", PartParityJson.StrArray(PartParityFxRules.DustViews) },
                        { "tile", PartParityJson.Int(d.Results[0].Width) },
                    }) },
                { "views", JoinArray(d.Results.ConvertAll(r => r.Json())) },
                { "structure", StructureJson(d.RealNames, d.GhostNames) },
            };
        }

        private static void WriteDustAtlas(StepContext ctx, WaveCase wc, PartParityCaseSidecar s)
        {
            PartParityDustRun d = wc.Burst.Dust;
            if (s.Dust == null || d == null || d.Results.Count == 0) return;
            int tile = d.Results[0].Width;
            byte[] blank = new byte[tile * tile * 4];
            string[] rowNames = PartParityJson.DustAtlasRows;
            var rows = new byte[rowNames.Length][][];
            for (int r = 0; r < rows.Length; r++)
            {
                rows[r] = new byte[PartParityFxRules.DustViews.Length][];
                for (int c = 0; c < rows[r].Length; c++) rows[r][c] = blank;
            }
            foreach (PartParityFxViewResult res in d.Results)
            {
                int c = Array.IndexOf(PartParityFxRules.DustViews, res.View);
                int realRow = Array.IndexOf(rowNames, PartParityFxRules.SideReal + "-" + res.Layer);
                int ghostRow = Array.IndexOf(rowNames, PartParityFxRules.SideGhost + "-" + res.Layer);
                if (c < 0 || realRow < 0 || ghostRow < 0) continue;
                rows[realRow][c] = res.RealTile ?? blank;
                rows[ghostRow][c] = res.GhostTile ?? blank;
            }
            byte[] atlas = PartParityImage.BuildAtlas(rows, PartParityFxRules.DustViews.Length, tile, out int w, out int h);
            FileIOUtils.SafeWriteBytes(PartParityPng.EncodeRgba(w, h, atlas),
                Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.DustAtlasFileName(wc.CaseId)), Tag);
        }

        private static void ReleaseDust(WaveCase wc)
        {
            PartParityDustRun d = wc.Burst.Dust;
            if (d.Hold != null)
            {
                try { UnityEngine.Object.Destroy(d.Hold.gameObject); }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: dust hold destroy threw {ex.GetType().Name}: {ex.Message}"); }
                d.Hold = null;
            }
            if (d.Ghost != null)
            {
                GhostPlaybackState state = d.Ghost.State;
                if (state?.launchDustInfo != null)
                {
                    GhostVisualBuilder.DestroyLaunchDust(state.launchDustInfo);
                    state.launchDustInfo = null;
                }
                try { GhostCoOrbitalRig.DestroyLiveGhost(d.Ghost); }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: dust ghost destroy threw {ex.GetType().Name}: {ex.Message}"); }
                d.Ghost = null;
            }
        }
    }
}
