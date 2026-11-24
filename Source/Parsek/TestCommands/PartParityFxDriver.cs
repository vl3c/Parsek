using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.InGameTests;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The live FX engine of the part parity sweep (design-part-ghost-parity.md Appendix E,
    /// piece F3): the authoring and spawn orientation of a lone engine, RCS block, heat part or
    /// lamp; the cheat scope and the hold; the press of each family and its done signal, polled
    /// with the recorder's own reads; the ghosts brought onto the screen as production does; the
    /// warm-up and plateau on every side; the layered time accumulation; the plate. Every rule it
    /// reads is pure (<see cref="PartParityFxRules"/>, <see cref="PartParityFxFraming"/>,
    /// <see cref="PartParityFxMetrics"/>); this file only reads and drives the live game. The
    /// capture half is <c>PartParityFxDriver.Capture.cs</c>, the plate half
    /// <c>PartParityFxDriver.Plate.cs</c>.
    /// </summary>
    internal static partial class PartParityFxDriver
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static string Tag => PartParitySweepRunner.Tag;

        /// <summary>The state-option action word of an FX case with no stock KSPEvent pressed
        /// through P6 (its press is this driver's).</summary>
        internal const string FxAction = "fx-drive";

        // =====================================================================================
        // enumeration (E.1) and case construction
        // =====================================================================================

        /// <summary>Every FX case the install offers with its exclusions (design E.1).</summary>
        internal static PartParityFxEnumeration Enumerate(StepContext ctx)
        {
            var all = new PartParityFxEnumeration();
            foreach (var e in ctx.Entries)
                all.AddRange(PartParityFxRules.Enumerate(PartParityStateDriver.BuildStatePart(e.Info),
                    PartParityCaseRules.DefaultCaseKey(e)));
            return all;
        }

        /// <summary>The state run and the FX run of one FX case (or control). A lamp on the plate
        /// presses with P6's own light option; every other family presses through this driver,
        /// its option a synthesized one holding the family and the module under test.</summary>
        internal static void Attach(WaveCase wc, PartParityCaseKey key, PartParityPartEntry entry, string control, bool dropEvents)
        {
            PartParityFxOption o = key.FxOption;
            wc.Fx = new PartParityFxRun { Option = o, Control = control };
            PartParityStateOption option = null;
            if (wc.Fx.Plate)
            {
                PartParityStatePart sp = PartParityStateDriver.BuildStatePart(entry.Info);
                foreach (PartParityCaseKey k in PartParityStateRules.Enumerate(sp, PartParityCaseRules.DefaultCaseKey(entry)).Cases)
                    if (k.StateOption != null && k.StateOption.Family == PartParityStateRules.FamilyLight
                        && k.StateOption.State == PartParityStateRules.StateLit && k.StateOption.ModuleIndex == o.ModuleIndex)
                    {
                        option = k.StateOption;
                        break;
                    }
                if (option == null)
                {
                    wc.Detail = wc.Detail ?? "no P6 lit option for " + o.Module + " at " + o.ModuleIndex.ToString(Ic);
                    wc.Fx = null;
                    return;
                }
            }
            else
            {
                option = new PartParityStateOption
                {
                    Family = o.Family, Module = o.Module, ModuleIndex = o.ModuleIndex, State = o.State, Action = FxAction,
                };
                option.ModuleIndices.Add(o.ModuleIndex);
            }
            wc.State = new PartParityStateRun
            {
                Option = option, DropEvents = dropEvents, FourRows = true,
                NoGhosts = control == TestCommandPartParitySweep.ControlGhostAbsent,
                GhostOutcomeOverride = (state, pid) => GhostOutcome(wc, state, pid),
            };
            wc.Fx.GhostLightsOff = control == TestCommandPartParitySweep.ControlPlateLightOff
                                   || control == TestCommandPartParitySweep.ControlPlateLightOffPoint;
        }

        /// <summary>The controls whose event-route ghost is built without the polled events.</summary>
        internal static bool DropsEvents(string control)
            => control == TestCommandPartParitySweep.ControlFxPlumeOff
               || control == TestCommandPartParitySweep.ControlFxPlumeOffAir
               || control == TestCommandPartParitySweep.ControlHeatEventsDropped;

        /// <summary>The subject of one control on this install (design E.5), chosen once per step
        /// and logged in full.</summary>
        internal static PartParityFxSubjectChoice ChooseSubject(StepContext ctx, string id, bool air)
        {
            if (ctx.FxSubjects.TryGetValue(id, out PartParityFxSubjectChoice known)) return known;
            var names = new List<string>();
            foreach (var e in ctx.Entries) names.Add(e.Name);
            PartParityFxSubjectChoice choice = PartParityFxRules.ChooseControlSubject(id, air, names, name => FactsOf(ctx, name));
            ctx.FxSubjects[id] = choice;
            ParsekLog.Info(Tag, $"fx control {id}: {choice.Detail} outcome={choice.Outcome ?? "run"}");
            return choice;
        }

        private static PartParityFxSubjectFacts FactsOf(StepContext ctx, string name)
        {
            var facts = new PartParityFxSubjectFacts();
            if (!ctx.EntriesByName.TryGetValue(name, out PartParityPartEntry e) || e.Info?.partPrefab == null) return facts;
            facts.Present = true;
            foreach (PartModule m in e.Info.partPrefab.Modules)
                if (m != null && m.moduleName == PartParityFxRules.ModuleWaterfallFx) facts.HasWaterfall = true;
            // A light counts only when the part has a lit plate case to photograph: a cockpit's
            // interior light is a Light component no ModuleLight turns on.
            bool lamp = false;
            foreach (PartParityCaseKey k in PartParityFxRules.Enumerate(PartParityStateDriver.BuildStatePart(e.Info),
                         PartParityCaseRules.DefaultCaseKey(e)).Cases)
                if (k.FxOption.Family == PartParityFxRules.FamilyLight) lamp = true;
            if (lamp)
                foreach (Light l in e.Info.partPrefab.GetComponentsInChildren<Light>(true))
                    facts.LightTypes.Add(l.type.ToString());
            ModuleAnimateHeat heat = e.Info.partPrefab.FindModuleImplementing<ModuleAnimateHeat>();
            if (heat != null && !string.IsNullOrEmpty(heat.animName))
            {
                Animation[] anims = e.Info.partPrefab.FindModelAnimators(heat.animName);
                facts.HasHeatClip = anims != null && anims.Length > 0;
            }
            return facts;
        }

        /// <summary>The FX key a control stands for: its subject's burn-full plume (or air plume),
        /// heat-hot, or lit plate case; null when the part offers none.</summary>
        internal static PartParityCaseKey ControlKey(StepContext ctx, string id, PartParityPartEntry entry, bool air)
        {
            List<PartParityCaseKey> cases = PartParityFxRules.Enumerate(PartParityStateDriver.BuildStatePart(entry.Info),
                PartParityCaseRules.DefaultCaseKey(entry)).Cases;
            foreach (PartParityCaseKey k in cases)
            {
                PartParityFxOption o = k.FxOption;
                switch (id)
                {
                    case TestCommandPartParitySweep.ControlHeatEventsDropped:
                        if (o.Family == PartParityFxRules.FamilyHeat && o.HeatScalar >= PartParityFxRules.HeatHotScalar) return k;
                        break;
                    case TestCommandPartParitySweep.ControlPlateLightOff:
                    case TestCommandPartParitySweep.ControlPlateLightOffPoint:
                        if (o.Family == PartParityFxRules.FamilyLight) return k;
                        break;
                    default:
                        return PartParityFxRules.ControlPlumeKey(cases, air);
                }
            }
            return null;
        }

        // =====================================================================================
        // authoring and spawn (E.2)
        // =====================================================================================

        /// <summary>Spawns the case vessel at slot <paramref name="slot"/> of a wave of
        /// <paramref name="count"/>: a lamp through P6's authoring, everything else the lone part
        /// with the independent throttle; in orbit its exhaust along the orbit normal, in an air run
        /// held over the showcase band (<see cref="PartParityAirContext.SpawnHeld"/>).</summary>
        internal static void SpawnCase(StepContext ctx, WaveCase wc, int slot, int count)
        {
            PartParityFxRun fx = wc.Fx;
            if (fx == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = wc.Detail ?? "no FX run";
                return;
            }
            string why = null;
            ConfigNode node = fx.Plate ? PartParityStateDriver.AuthorVesselNode(wc, ctx.Host, out why) : AuthorVesselNode(wc, ctx.Host, out why);
            if (node == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = wc.Detail ?? why ?? "FX vessel node not authored";
                return;
            }
            if (TestCommandPartParitySweep.IsAirRun(ctx.Args))
            {
                PartParityAirContext.SpawnHeld(ctx, wc, node, ExhaustAxis(wc), slot, count);
                return;
            }
            Quaternion? rotation = SpawnRotation(wc, ctx.Host);
            wc.Pid = GhostCoOrbitalRig.SpawnCoOrbital(node, ctx.Host, wc.AlongTrackM, wc.CaseId, Tag, rotation);
            wc.SpawnRealtime = Time.realtimeSinceStartup;
            if (wc.Pid == 0)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = "SpawnAtPosition returned pid=0";
            }
        }

        private static ConfigNode AuthorVesselNode(WaveCase wc, Vessel anchor, out string why)
        {
            why = null;
            AvailablePart info = wc.Entry.Info;
            PartParityFxOption o = wc.Fx.Option;
            string primary = null;
            if (o.MultiMode)
            {
                MultiModeEngine mme = info.partPrefab.FindModuleImplementing<MultiModeEngine>();
                primary = mme != null ? mme.primaryEngineID : null;
            }
            List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(1);
            ConfigNode subject = GhostCoOrbitalRig.AuthorPartNode(info, ids[0], 0, Vector3.zero, null, wc.RealVariant, (mn, modName) =>
            {
                if (modName != null && modName.StartsWith("ModuleEngines", StringComparison.Ordinal))
                {
                    mn.SetValue("independentThrottle", "True", true);
                    mn.SetValue("independentThrottlePercentage",
                        PartParityFxRules.IndependentThrottlePercentageFor(o).ToString(Ic), true);
                    mn.SetValue("thrustPercentage", "100", true);
                    mn.SetValue("exhaustDamage", "False", true);
                }
                else if (modName == "MultiModeEngine")
                {
                    mn.SetValue("autoSwitch", "False", true);
                    if (primary != null)
                        mn.SetValue("runningPrimary", string.Equals(o.EngineId, primary, StringComparison.Ordinal) ? "True" : "False", true);
                }
            });
            Orbit src = anchor.orbit;
            var placeholder = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: authored FX vessel part={info.name} family={o.Family} state={o.State} " +
                $"module={o.Module} midx={o.ModuleIndex.ToString(Ic)} engineId={o.EngineId ?? "-"} multiMode={(o.MultiMode ? "true" : "false")} " +
                $"throttle%={PartParityFxRules.IndependentThrottlePercentageFor(o).ToString(Ic)} primaryId={primary ?? "-"}");
            return ProtoVessel.CreateVesselNode(PartParityCaseRules.VesselName(wc.CaseId), VesselType.Probe, placeholder, 0, new[] { subject });
        }

        /// <summary>The rotation that turns the case's exhaust axis along the orbit normal (E.2:
        /// no plume points at a neighbour slot or the host); null when the axis is unknown or the
        /// case has no plume.</summary>
        private static Quaternion? SpawnRotation(WaveCase wc, Vessel anchor)
        {
            Vector3? exhaust = ExhaustAxis(wc);
            if (!exhaust.HasValue) return null;
            Vector3 position = anchor.GetWorldPos3D();
            Vector3 radial = (position - anchor.mainBody.position).normalized;
            Vector3 normal = Vector3.Cross(radial, anchor.obt_velocity).normalized;
            if (normal.sqrMagnitude < 0.5f) return null;
            Vector3 e = exhaust.Value;
            Quaternion rotation = Quaternion.FromToRotation(e, normal);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: spawn rotation exhaust=({e.x.ToString("0.00", Ic)}," +
                $"{e.y.ToString("0.00", Ic)},{e.z.ToString("0.00", Ic)}) along the orbit normal");
            return rotation;
        }

        /// <summary>The unit exhaust axis of a plume case in part space, off the prefab's thrust
        /// transforms (they point down the exhaust); null when unknown or the case has no plume.</summary>
        private static Vector3? ExhaustAxis(WaveCase wc)
        {
            if (wc.Fx.Option.Family != PartParityFxRules.FamilyPlume) return null;
            Part prefab = wc.Entry.Info.partPrefab;
            if (prefab == null || prefab.Modules == null) return null;
            if (wc.Fx.Option.ModuleIndex < 0 || wc.Fx.Option.ModuleIndex >= prefab.Modules.Count) return null;
            if (!(prefab.Modules[wc.Fx.Option.ModuleIndex] is ModuleEngines engine)) return null;
            Transform[] thrust = prefab.FindModelTransforms(engine.thrustVectorTransformName);
            if (thrust == null || thrust.Length == 0) return null;
            Vector3 sum = Vector3.zero;
            foreach (Transform t in thrust)
                if (t != null) sum += Quaternion.Inverse(prefab.transform.rotation) * t.forward;
            return sum.sqrMagnitude < 1e-6f ? (Vector3?)null : sum.normalized;
        }

        // =====================================================================================
        // geometry (part-local)
        // =====================================================================================

        private static double[] ToLocal(Transform partT, Vector3 world)
        {
            Vector3 v = partT.InverseTransformPoint(world);
            return new double[] { v.x, v.y, v.z };
        }

        private static double[] DirLocal(Transform partT, Vector3 worldDirection)
        {
            Vector3 v = partT.InverseTransformDirection(worldDirection);
            return new double[] { v.x, v.y, v.z };
        }

        /// <summary>The nozzle point, the exhaust axis and the thrust points of an engine or RCS
        /// case in part space (an engine's thrust transforms point down the exhaust; an RCS
        /// thruster's <c>up</c> or <c>forward</c> is the axis stock fires it along).</summary>
        private static void MeasureGeometry(WaveCase wc, PartParityStateRun run)
        {
            PartParityFxRun fx = wc.Fx;
            Transform partT = wc.Real.rootPart.transform;
            var points = new List<double[]>();
            var axes = new List<double[]>();
            if (fx.Engine != null && fx.Engine.thrustTransforms != null)
            {
                foreach (Transform t in fx.Engine.thrustTransforms)
                    if (t != null) { points.Add(ToLocal(partT, t.position)); axes.Add(DirLocal(partT, t.forward)); }
            }
            else if (fx.Rcs != null && fx.Rcs.thrusterTransforms != null)
            {
                foreach (int i in fx.RcsFired)
                {
                    if (i < 0 || i >= fx.Rcs.thrusterTransforms.Count) continue;
                    Transform t = fx.Rcs.thrusterTransforms[i];
                    if (t == null) continue;
                    points.Add(ToLocal(partT, t.position));
                    axes.Add(DirLocal(partT, fx.Rcs.useZaxis ? t.forward : t.up));
                }
            }
            if (points.Count == 0) return;
            var sum = new double[3];
            var mean = new double[3];
            foreach (double[] a in axes) for (int k = 0; k < 3; k++) sum[k] += a[k];
            foreach (double[] p in points) for (int k = 0; k < 3; k++) mean[k] += p[k] / points.Count;
            double[] u = PartParityFxFraming.Unit(sum);
            if (u == null) u = PartParityFxFraming.Unit(axes[0]);
            fx.ThrustPoints = points;
            fx.Nozzle = mean;
            fx.Exhaust = u;
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: exhaust axis=({u[0].ToString("0.000", Ic)},{u[1].ToString("0.000", Ic)}," +
                $"{u[2].ToString("0.000", Ic)}) nozzle=({mean[0].ToString("0.00", Ic)},{mean[1].ToString("0.00", Ic)},{mean[2].ToString("0.00", Ic)}) " +
                $"thrustPoints={points.Count.ToString(Ic)}");
        }

        // =====================================================================================
        // the press (E.2)
        // =====================================================================================

        /// <summary>Before the press: the module under test, the hold and the press timeout. Runs
        /// after the settle (the hold starts with the part settled; an air case has been held since it
        /// was unpacked).</summary>
        private static void Prepare(StepContext ctx, WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            if (fx.Hold == null) fx.Hold = PartParityFxHold.Attach(wc.Real, ctx.Host);
            PartModule m = fx.Option.ModuleIndex >= 0 && fx.Option.ModuleIndex < part.Modules.Count
                ? part.Modules[fx.Option.ModuleIndex] : null;
            switch (fx.Option.Family)
            {
                case PartParityFxRules.FamilyPlume:
                    fx.Engine = m as ModuleEngines;
                    if (fx.Engine == null) Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "no ModuleEngines at module " + fx.Option.ModuleIndex.ToString(Ic));
                    break;
                case PartParityFxRules.FamilyRcs:
                    fx.Rcs = m as ModuleRCS;
                    if (fx.Rcs == null) Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "no ModuleRCS at module " + fx.Option.ModuleIndex.ToString(Ic));
                    break;
                case PartParityFxRules.FamilyHeat:
                    fx.Heat = m;
                    if (fx.Heat == null || fx.Heat.moduleName != PartParityFxRules.ModuleAnimateHeat)
                        Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "no " + PartParityFxRules.ModuleAnimateHeat + " at module " + fx.Option.ModuleIndex.ToString(Ic));
                    break;
            }
        }

        private static void Fault(WaveCase wc, string status, string detail)
        {
            if (wc.Status == null) wc.Status = status;
            wc.Detail = wc.Detail ?? detail;
            if (wc.State != null)
            {
                wc.State.ActionResult = wc.State.ActionResult ?? PartParityStateRules.ActionMissing;
                wc.State.Done = true;
            }
            ParsekLog.Warn(Tag, $"case {wc.CaseId}: {detail}");
        }

        /// <summary>The press hook of <see cref="PartParityStateDriver.Press"/>: true when this
        /// driver handled it (a plate case presses P6's light event).</summary>
        internal static bool TryPress(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            if (fx == null) return false;
            fx.Hold?.ResetEvidence();
            if (fx.Plate) return false;
            PartParityStateRun run = wc.State;
            // The stowed inputs set the state rule's timeout; an FX family has its own (E.2).
            run.TimeoutSeconds = PartParityFxRules.PressTimeoutSeconds(fx.Option);
            run.PressRealtime = Time.realtimeSinceStartup;
            run.Pressed = true;
            run.ActionResult = PartParityStateRules.ActionInvoked;
            switch (fx.Option.Family)
            {
                case PartParityFxRules.FamilyPlume: PressPlume(wc, run, fx); break;
                case PartParityFxRules.FamilyRcs: PressRcs(wc, run, fx); break;
                case PartParityFxRules.FamilyHeat: PressHeat(wc, run, fx); break;
            }
            return true;
        }

        private static void PressPlume(WaveCase wc, PartParityStateRun run, PartParityFxRun fx)
        {
            ModuleEngines e = fx.Engine;
            MultiModeEngine mme = wc.Real.rootPart.FindModuleImplementing<MultiModeEngine>();
            if (mme != null)
            {
                bool primary = string.Equals(e.engineID, mme.primaryEngineID, StringComparison.Ordinal);
                mme.autoSwitch = false;
                if (primary && !mme.runningPrimary) mme.SetPrimary(true);
                else if (!primary && mme.runningPrimary) mme.SetSecondary(true);
                fx.EngineMode = primary ? "primary" : "secondary";
            }
            BaseEvent act = e.Events["Activate"];
            if (act == null) { Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "no KSPEvent Activate on " + e.moduleName); return; }
            act.Invoke();
            ParsekLog.Info(Tag, $"case {wc.CaseId}: pressed engine Activate id={e.engineID ?? "-"} mode={fx.EngineMode ?? "-"} " +
                $"independent={(e.independentThrottle ? "true" : "false")} percentage={e.independentThrottlePercentage.ToString("0", Ic)} " +
                $"throttleLocked={(e.throttleLocked ? "true" : "false")} responseTime={(e.useEngineResponseTime ? "true" : "false")} " +
                $"cheat={(CheatOptions.InfinitePropellant ? "true" : "false")} controllable={(wc.Real.IsControllable ? "true" : "false")}");
        }

        private static void PressRcs(WaveCase wc, PartParityStateRun run, PartParityFxRun fx)
        {
            ModuleRCS rcs = fx.Rcs;
            Vessel v = wc.Real;
            fx.RcsNozzles = rcs.thrusterTransforms != null ? rcs.thrusterTransforms.Count : 0;
            Transform refT = v.ReferenceTransform != null ? v.ReferenceTransform : v.rootPart.transform;
            var thrusters = new List<PartParityRcsThruster>();
            for (int i = 0; i < fx.RcsNozzles; i++)
            {
                Transform t = rcs.thrusterTransforms[i];
                if (t == null) { thrusters.Add(null); continue; }
                Vector3 axis = Quaternion.Inverse(refT.rotation) * (rcs.useZaxis ? t.forward : t.up);
                thrusters.Add(new PartParityRcsThruster
                {
                    Axis = new double[] { axis.x, axis.y, axis.z },
                    Active = t.gameObject.activeInHierarchy,
                    AtOrigin = t.position == Vector3.zero,
                });
            }
            fx.RcsChoice = PartParityFxRules.ChooseRcsInput(thrusters, rcs.enableX, rcs.enableY, rcs.enableZ);
            if (fx.RcsChoice == null)
            {
                Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "no direction fires any nozzle of " + rcs.moduleName +
                    " (nozzles=" + fx.RcsNozzles.ToString(Ic) + ")");
                return;
            }
            fx.RcsPredicted.AddRange(fx.RcsChoice.Fired);
            double[] ctrl = PartParityFxRules.RcsCtrlStateFor(fx.RcsChoice.Direction);
            fx.RcsInput = st => { st.X = (float)ctrl[0]; st.Y = (float)ctrl[1]; st.Z = (float)ctrl[2]; };
            v.OnPreAutopilotUpdate += fx.RcsInput;
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: pressed RCS direction=({fx.RcsChoice.Direction[0].ToString("0.000", Ic)}," +
                $"{fx.RcsChoice.Direction[1].ToString("0.000", Ic)},{fx.RcsChoice.Direction[2].ToString("0.000", Ic)}) predicted=[{JoinInts(fx.RcsPredicted)}] " +
                $"nozzles={fx.RcsNozzles.ToString(Ic)} useZaxis={(rcs.useZaxis ? "true" : "false")} thrusterPower={rcs.thrusterPower.ToString("0.00", Ic)} " +
                $"counted={fx.RcsChoice.Counted.ToString(Ic)} spread={fx.RcsChoice.Spread.ToString("0.000", Ic)} mean={fx.RcsChoice.Mean.ToString("0.000", Ic)} " +
                $"enable=({rcs.enableX},{rcs.enableY},{rcs.enableZ}) controllable={(v.IsControllable ? "true" : "false")}");
        }

        private static void PressHeat(WaveCase wc, PartParityStateRun run, PartParityFxRun fx)
        {
            if (!(fx.Heat is ModuleAnimateHeat heat)) { Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "no live ModuleAnimateHeat"); return; }
            double target = PartParityFxRules.HeatTemperatureFor(fx.Option.HeatScalar, heat.lerpOffset, heat.lerpDivRecip * heat.lerpScalar);
            if (double.IsNaN(target))
            {
                Fault(wc, TestCommandPartParitySweep.SetupActionFailed, "heat lerp not invertible (offset=" +
                    heat.lerpOffset.ToString("R", Ic) + " divRecip=" + heat.lerpDivRecip.ToString("R", Ic) + " scalar=" + heat.lerpScalar.ToString("R", Ic) + ")");
                return;
            }
            fx.Hold.HeatUsesSkin = heat.useSkinTemp;
            fx.Hold.HeatTargetK = target;
            ParsekLog.Info(Tag, $"case {wc.CaseId}: pressed heat scalar={fx.Option.HeatScalar.ToString("0.000", Ic)} targetK={target.ToString("0.0", Ic)} " +
                $"lerpOffset={heat.lerpOffset.ToString("0.0", Ic)} lerpDivRecip={heat.lerpDivRecip.ToString("R", Ic)} lerpScalar={heat.lerpScalar.ToString("R", Ic)} " +
                $"lerpMin={heat.lerpMin.ToString("0.0", Ic)} lerpMax={heat.lerpMax.ToString("0.0", Ic)} useSkin={(heat.useSkinTemp ? "true" : "false")} " +
                $"maxTemp={wc.Real.rootPart.maxTemp.ToString("0.0", Ic)}");
        }

        // =====================================================================================
        // the poll (E.2, the recorder's own reads)
        // =====================================================================================

        /// <summary>
        /// Polls the case's engines and RCS into the case's sink through the recorder's own
        /// statics and reads (pinned by <c>PartParityFxRecorderPinTests</c>): the ignition
        /// predicate, the recorded power and the 8-frame RCS debounce, then the shroud fallback
        /// an ignition sends in <c>FlightRecorder.CheckEngineState</c>.
        /// </summary>
        private static void PollFx(WaveCase wc, PartParityStateRun run)
        {
            PartParityFxRun fx = wc.Fx;
            Vessel v = wc.Real;
            PartTrackingSets sets = run.Sets;
            if (sets == null) return;
            double ut = Planetarium.GetUniversalTime();
            foreach (var (part, engine, moduleIndex) in FlightRecorder.CacheEngineModules(v))
            {
                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                bool ignited = FlightRecorder.ShouldRecordEngineAsIgnited(engine.EngineIgnited, engine.isOperational, engine.finalThrust);
                float power = FlightRecorder.ComputeRecordedEnginePower(engine.currentThrottle, engine.finalThrust, engine.maxThrust);
                fx.EventBuffer.Clear();
                FlightRecorder.CheckEngineTransition(key, part.persistentId, moduleIndex, part.partInfo?.name ?? "unknown",
                    ignited, power, sets.activeEngineKeys, sets.lastThrottle, ut, fx.EventBuffer);
                foreach (PartEvent e in fx.EventBuffer)
                {
                    run.Sink.Events.Add(e);
                    ParsekLog.Verbose(Tag, $"Part event: {e.eventType} '{e.partName}' pid={e.partPersistentId.ToString(Ic)} " +
                        $"midx={e.moduleIndex.ToString(Ic)} val={e.value.ToString("0.00", Ic)} (part parity fx poll)");
                    if (e.eventType != PartEventType.EngineIgnited || part.FindModuleImplementing<ModuleJettison>() == null) continue;
                    PartEvent? shroud = FlightRecorder.CheckJettisonTransition(part.persistentId, part.partInfo?.name ?? "unknown",
                        isJettisoned: true, jettisonedSet: sets.jettisonedShrouds, ut: ut);
                    if (shroud.HasValue) run.Sink.Events.Add(shroud.Value);
                }
            }
            foreach (var (part, rcs, moduleIndex) in FlightRecorder.CacheRcsModules(v))
            {
                ulong key = FlightRecorder.EncodeEngineKey(part.persistentId, moduleIndex);
                bool active = rcs.rcs_active && rcs.rcsEnabled;
                fx.EventBuffer.Clear();
                FlightRecorder.ProcessRcsDebounce(key, part.persistentId, moduleIndex, part.partInfo?.name ?? "unknown",
                    active, rcs.thrustForces, rcs.thrusterPower, ut, fx.RcsFrameCounts, sets.activeRcsKeys, sets.lastRcsThrottle, fx.EventBuffer);
                foreach (PartEvent e in fx.EventBuffer)
                {
                    run.Sink.Events.Add(e);
                    ParsekLog.Verbose(Tag, $"Part event: {e.eventType} '{e.partName}' pid={e.partPersistentId.ToString(Ic)} " +
                        $"midx={e.moduleIndex.ToString(Ic)} val={e.value.ToString("0.00", Ic)} (part parity fx poll)");
                }
            }
        }

        // =====================================================================================
        // the done signal
        // =====================================================================================

        /// <summary>The step hook of <see cref="PartParityStateDriver.Step"/>: polls, then the
        /// family's done signal, the timeout, and the settle (E.2). True when this driver handled it
        /// (a plate case steps P6's light signal).</summary>
        internal static bool TryStep(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            if (fx == null || fx.Plate) return false;
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            float now = Time.realtimeSinceStartup;
            PartParityStateDriver.Poll(wc.Real, run);
            PollFx(wc, run);
            run.FramesSincePress++;
            if (!run.Reached)
            {
                bool done = Observe(wc, run, part, out string field, out string value);
                if (done)
                {
                    run.Reached = true;
                    run.ReachedRealtime = now;
                    run.ReachedUT = Planetarium.GetUniversalTime();
                    run.WaitSeconds = now - run.PressRealtime;
                    run.EndpointField = field;
                    run.EndpointValue = value;
                    if (fx.Engine != null) ReadDrive(fx);
                    if (fx.Rcs != null && !OnRcsReached(wc, fx, part)) return true;
                    ParsekLog.Verbose(Tag, $"case {wc.CaseId}: FX reached {fx.Option.State} after {run.WaitSeconds.Value.ToString("0.00", Ic)}s " +
                        $"({field}={value}) events={run.Sink.Events.Count.ToString(Ic)} hold[{fx.Hold?.Describe()}]");
                }
                else if (now - run.PressRealtime > run.TimeoutSeconds)
                {
                    run.WaitSeconds = now - run.PressRealtime;
                    run.EndpointField = field;
                    run.EndpointValue = value;
                    wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                    wc.Detail = wc.Detail ?? "never reached " + fx.Option.State + " in " + run.TimeoutSeconds.ToString("0.0", Ic) + " s (" + field + "=" + value + ")";
                    run.Done = true;
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                    return true;
                }
            }
            if (!run.Reached) return true;
            if (now - run.ReachedRealtime >= PartParitySweepPlan.MinSettleSeconds) run.Done = true;
            return true;
        }

        private static bool Observe(WaveCase wc, PartParityStateRun run, Part part, out string field, out string value)
        {
            PartParityFxRun fx = wc.Fx;
            switch (fx.Option.Family)
            {
                case PartParityFxRules.FamilyPlume:
                {
                    ModuleEngines e = fx.Engine;
                    bool ignited = e != null && FlightRecorder.ShouldRecordEngineAsIgnited(e.EngineIgnited, e.isOperational, e.finalThrust);
                    bool recorded = HasEvent(run, PartEventType.EngineIgnited, part.persistentId, fx.Option.RecorderKeyIndex);
                    field = "currentThrottle";
                    value = (e != null ? e.currentThrottle.ToString("0.000", Ic) : "-") + " ignited=" + (ignited ? "true" : "false") +
                            " recorded=" + (recorded ? "true" : "false") + " request=" + fx.Option.Throttle.ToString("0.000", Ic) +
                            " finalThrust=" + (e != null ? e.finalThrust.ToString("0.00", Ic) : "-");
                    return PartParityFxRules.PlumeReached(ignited, e != null ? e.currentThrottle : double.NaN, fx.Option.Throttle, recorded);
                }
                case PartParityFxRules.FamilyRcs:
                {
                    bool recorded = HasEvent(run, PartEventType.RCSActivated, part.persistentId, fx.Option.RecorderKeyIndex);
                    field = "RCSActivated";
                    value = recorded ? "recorded" : "none";
                    return recorded;
                }
                case PartParityFxRules.FamilyHeat:
                {
                    bool read = FlightRecorder.TryClassifyAnimateHeatState(fx.Heat, out float scalar, out string src);
                    bool emitted = HasEvent(run, fx.Option.HeatScalar > 0.8 ? PartEventType.ThermalAnimationHot : PartEventType.ThermalAnimationMedium,
                        part.persistentId, fx.Option.RecorderKeyIndex);
                    field = "heat";
                    value = (read ? scalar.ToString("0.000", Ic) + " src=" + src : "unreadable") + " event=" + (emitted ? "true" : "false");
                    return PartParityFxRules.HeatReached(read ? scalar : double.NaN, fx.Option.HeatScalar, emitted);
                }
                default:
                    field = "family";
                    value = fx.Option.Family;
                    return false;
            }
        }

        private static bool HasEvent(PartParityStateRun run, PartEventType type, uint pid, int midx)
        {
            foreach (PartEvent e in run.Sink.Events)
                if (e.eventType == type && e.partPersistentId == pid && e.moduleIndex == midx) return true;
            return false;
        }

        private static void ReadDrive(PartParityFxRun fx)
        {
            ModuleEngines e = fx.Engine;
            fx.DriveIgnited = FlightRecorder.ShouldRecordEngineAsIgnited(e.EngineIgnited, e.isOperational, e.finalThrust);
            fx.DriveFinalThrust = e.finalThrust;
            fx.DriveCurrentThrottle = e.currentThrottle;
            fx.DriveRecordedPower = FlightRecorder.ComputeRecordedEnginePower(e.currentThrottle, e.finalThrust, e.maxThrust);
        }

        /// <summary>The RCS press check and the ghost nozzle hide (E.2): the nozzles stock fired
        /// against the prediction, the module's own record of them, and the check that the ghost's
        /// nozzle index is stock's (the FX transform name is the module's and every one of it sits
        /// under the model).</summary>
        private static bool OnRcsReached(WaveCase wc, PartParityFxRun fx, Part part)
        {
            ModuleRCS rcs = fx.Rcs;
            var forces = new List<double>();
            for (int i = 0; i < fx.RcsNozzles; i++)
                forces.Add(rcs.thrustForces != null && i < rcs.thrustForces.Length && rcs.thrusterPower > 0f
                    ? rcs.thrustForces[i] / rcs.thrusterPower : 0.0);
            for (int i = 0; i < forces.Count; i++) if (forces[i] > PartParityFxRules.RcsFireEpsilon) fx.RcsFired.Add(i);
            var firedForces = new List<double>();
            foreach (int i in fx.RcsFired) firedForces.Add(forces[i]);
            float recorded = FlightRecorder.ComputeRcsPower(rcs.thrustForces, rcs.thrusterPower);
            fx.DriveRecordedPower = recorded;
            fx.RcsPowerRatio = PartParityFxRules.RcsPowerRatio(recorded, firedForces);
            if (!PartParityFxRules.RcsFiredMatches(fx.RcsPredicted, forces, out string detail))
            {
                Fault(wc, TestCommandPartParitySweep.SetupRcsInputMismatch, detail);
                return false;
            }
            string name = rcs.thrusterTransformName;
            int total = part.FindModelTransforms(name)?.Length ?? 0, underModel = 0;
            foreach (Transform t in part.GetComponentsInChildren<Transform>(true))
                if (t.name == name) underModel++;
            if (underModel != total || total != fx.RcsNozzles)
            {
                Fault(wc, TestCommandPartParitySweep.SetupRcsInputMismatch, "nozzle index not stock's: transforms named " + name +
                    " under the part=" + underModel.ToString(Ic) + " under model=" + total.ToString(Ic) + " module nozzles=" + fx.RcsNozzles.ToString(Ic));
                return false;
            }
            ParsekLog.Info(Tag, $"case {wc.CaseId}: RCS fired=[{JoinInts(fx.RcsFired)}] of {fx.RcsNozzles.ToString(Ic)} predicted=[{JoinInts(fx.RcsPredicted)}] " +
                $"forces=[{string.Join(",", forces.ConvertAll(f => f.ToString("0.00", Ic)).ToArray())}] recordedPower={recorded.ToString("0.000", Ic)} " +
                $"powerRatio={fx.RcsPowerRatio.ToString("0.000", Ic)} transformName={name}");
            return true;
        }

        // =====================================================================================
        // the ghosts on the screen (E.2)
        // =====================================================================================

        /// <summary>
        /// Brings the event and start ghosts onto the screen the way production does: from the
        /// hidden prime P6 left them in, the active root, the body and altitude the smoke fade
        /// reads, then the first unsuppressed frame. Reads back each route's reach.
        /// </summary>
        internal static void PrimeGhosts(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            PartParityStateRun run = wc.State;
            uint pid = wc.Real.rootPart.persistentId;
            float now = Time.realtimeSinceStartup;
            if (wc.Ghost != null) { PrimeVisible(wc, wc.Ghost); run.GhostEvent = GhostOutcome(wc, wc.Ghost.State, pid); }
            if (run.StartGhost != null)
            {
                PrimeVisible(wc, run.StartGhost);
                run.GhostStart = GhostOutcome(wc, run.StartGhost.State, pid);
                // P6 read it under the hidden prime, which stops every FX system: the frame a
                // player sees first is this one.
                run.StartFirstFrameAtTarget = run.GhostStart == PartParityStateRules.GhostApplied;
            }
            if (fx.BrokenGhost != null) { PrimeVisible(wc, fx.BrokenGhost); ParsekLog.Verbose(Tag, $"case {wc.CaseId}: broken ghost primed"); }
            fx.LastPrimeRealtime = now;
        }

        /// <summary>One ghost: the body name, altitude and ground speed (the smoke fade's and the
        /// thrust share's inputs), the suppression lifted, the unsuppressed restore and the audio pass.</summary>
        private static void PrimeVisible(WaveCase wc, GhostCoOrbitalRig.LiveGhost g)
        {
            GhostPlaybackState state = g.State;
            // ctl-fx-smoke-dropped needs a ghost that draws smoke in orbit, where the fade hides it
            // in vacuum: with no body the fade returns before it writes and the smoke stays as built.
            bool leaveFade = wc.Fx.Control == TestCommandPartParitySweep.ControlFxSmokeDropped;
            // In orbit that smoke is also left behind (GhostWorldSpaceEmitter), as the real part's is;
            // the control drops the one thing that moves it, on both its ghosts.
            if (leaveFade)
                foreach (GhostWorldSpaceEmitter e in state.ghost.GetComponentsInChildren<GhostWorldSpaceEmitter>(true))
                    e.enabled = false;
            state.lastInterpolatedBodyName = leaveFade || wc.Real.mainBody == null ? null : wc.Real.mainBody.name;
            state.lastInterpolatedAltitude = wc.Real.altitude;
            // The held vessel's own ground speed, which playback's SetInterpolated gives a ghost:
            // a jet's thrust share follows mach.
            state.lastInterpolatedSurfaceVelocity = (Vector3)wc.Real.srf_velocity;
            state.lastInterpolatedSurfaceVelocitySource = GhostSurfaceVelocitySource.Positions;
            state.visualFxSuppressed = false;
            GhostPlaybackEngine.ApplyUnsuppressedFxRestore(state, true);
            GhostPlaybackLogic.UpdateAudioAtmosphere(state);
        }

        /// <summary>The ghost's own state for the case's family after its prime (E.2), in P6's
        /// words: <c>no-info</c>, <c>not-reached</c> or <c>applied</c>.</summary>
        private static string GhostOutcome(WaveCase wc, GhostPlaybackState state, uint pid)
        {
            PartParityFxRun fx = wc.Fx;
            if (fx == null || state == null) return PartParityStateRules.GhostNoInfo;
            switch (fx.Option.Family)
            {
                case PartParityFxRules.FamilyPlume:
                {
                    ulong key = FlightRecorder.EncodeEngineKey(pid, fx.Option.RecorderKeyIndex);
                    if (state.engineInfos == null || !state.engineInfos.TryGetValue(key, out EngineGhostInfo info) || info == null)
                        return PartParityStateRules.GhostNoInfo;
                    if (info.currentPower <= 0f) return PartParityStateRules.GhostNotReached;
                    return AnyEmitting(info.particleSystems, info.kspEmitters) || info.particleSystems.Count == 0
                        ? PartParityStateRules.GhostApplied : PartParityStateRules.GhostNotReached;
                }
                case PartParityFxRules.FamilyRcs:
                {
                    ulong key = FlightRecorder.EncodeEngineKey(pid, fx.Option.RecorderKeyIndex);
                    if (state.rcsInfos == null || !state.rcsInfos.TryGetValue(key, out RcsGhostInfo info) || info == null)
                        return PartParityStateRules.GhostNoInfo;
                    if (info.currentPower <= 0f) return PartParityStateRules.GhostNotReached;
                    return AnyEmitting(info.particleSystems, info.kspEmitters) || info.particleSystems.Count == 0
                        ? PartParityStateRules.GhostApplied : PartParityStateRules.GhostNotReached;
                }
                case PartParityFxRules.FamilyHeat:
                    return state.heatInfos != null && state.heatInfos.ContainsKey(pid)
                        ? PartParityStateRules.GhostApplied : PartParityStateRules.GhostNoInfo;
                default:
                    return PartParityStateRules.GhostStateReached(wc.State.Option,
                        PartParityStateDriver.ReadGhostInput(wc.State.Option, state, pid));
            }
        }

        private static bool AnyEmitting(List<ParticleSystem> systems, List<KspEmitterRef> emitters)
        {
            foreach (ParticleSystem ps in systems) if (ps != null && ps.isPlaying) return true;
            foreach (KspEmitterRef r in emitters)
                if (r.emitter != null && r.emitField != null && r.emitField.GetValue(r.emitter) is bool on && on) return true;
            return false;
        }

        /// <summary>
        /// Stock's nozzle for a ghost FX system, or -1 (a setup fault). Stock plays nozzle i's puff
        /// on emitter i of the EFFECTS node (<c>ModuleRCSFX.UpdatePowerFX(running, idx, power)</c> ->
        /// <c>Part.Effect(name, power, idx)</c> -> <c>ModelMultiParticleFX.OnEvent(power, idx)</c>,
        /// emitter idx over <c>FindModelTransforms(transformName)</c>; decompiled), so the index F2
        /// recorded IS the nozzle index whatever the two transforms are called, provided every
        /// transform of that name sits under the part's model (F2's walk is the whole prefab) and the
        /// module has a nozzle of that index.
        /// </summary>
        internal static int NozzleOf(int thrusterIndex, int nozzles, int matchesInPrefab, int matchesUnderModel)
            => thrusterIndex >= 0 && thrusterIndex < nozzles && matchesInPrefab == matchesUnderModel ? thrusterIndex : -1;

        private static int NozzleOf(WaveCase wc, RcsParticleThruster thruster, Dictionary<string, KeyValuePair<int, int>> counts)
        {
            PartParityFxRun fx = wc.Fx;
            if (fx.Rcs?.thrusterTransforms == null || string.IsNullOrEmpty(thruster.transformName)) return -1;
            if (!counts.TryGetValue(thruster.transformName, out KeyValuePair<int, int> n))
            {
                Part prefab = wc.Real.rootPart.partInfo?.partPrefab;
                if (prefab == null) return -1;
                int all = 0;
                foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
                    if (t.name == thruster.transformName) all++;
                Transform[] model = prefab.FindModelTransforms(thruster.transformName);
                n = new KeyValuePair<int, int>(all, model != null ? model.Length : 0);
                counts[thruster.transformName] = n;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: RCS FX transform {thruster.transformName} matches={n.Key.ToString(Ic)} underModel={n.Value.ToString(Ic)} " +
                    $"nozzles={fx.Rcs.thrusterTransforms.Count.ToString(Ic)} thrusterTransformName={fx.Rcs.thrusterTransformName}");
            }
            return NozzleOf(thruster.thrusterIndex, fx.Rcs.thrusterTransforms.Count, n.Key, n.Value);
        }

        /// <summary>The renderers of one ghost's nozzles stock did not fire (hidden on the judged
        /// rcs sides, shown on <c>ghost-all</c>).</summary>
        private static void ExcludeUnfiredNozzles(WaveCase wc, PartParityFxSide side)
        {
            PartParityFxRun fx = wc.Fx;
            if (side.Ghost == null || side.Ghost.State?.rcsInfos == null || side.Ghost.Build?.root == null) return;
            var fired = new HashSet<int>(fx.RcsFired);
            var counts = new Dictionary<string, KeyValuePair<int, int>>(StringComparer.Ordinal);
            int hidden = 0, unmapped = 0, misnamed = 0;
            foreach (RcsGhostInfo info in side.Ghost.State.rcsInfos.Values)
            {
                if (info == null || info.partPersistentId != wc.Real.rootPart.persistentId) continue;
                for (int i = 0; i < info.particleSystems.Count; i++)
                {
                    ParticleSystem ps = info.particleSystems[i];
                    if (ps == null) continue;
                    if (!info.TryGetParticleThruster(i, out RcsParticleThruster thruster)) { unmapped++; continue; }
                    int nozzle = NozzleOf(wc, thruster, counts);
                    if (nozzle < 0) { misnamed++; continue; }
                    if (fired.Contains(nozzle)) continue;
                    hidden++;
                    foreach (ParticleSystemRenderer r in ps.GetComponentsInChildren<ParticleSystemRenderer>(true)) side.Excluded.Add(r);
                }
            }
            if (misnamed > 0 || unmapped > 0)
                Fault(wc, TestCommandPartParitySweep.SetupRcsInputMismatch, "ghost FX systems have no stock nozzle: module thrusterTransformName=" +
                    (fx.Rcs != null ? fx.Rcs.thrusterTransformName : "-") + " withoutNozzle=" + misnamed.ToString(Ic) + " unmapped=" + unmapped.ToString(Ic));
            fx.RcsGhostHidden = Math.Max(fx.RcsGhostHidden, hidden);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: side {side.Name} nozzles hidden={hidden.ToString(Ic)} unmapped={unmapped.ToString(Ic)} fired=[{JoinInts(fx.RcsFired)}]");
        }

        /// <summary>One line on a heat case's real part at the capture: the module's scalar, each
        /// Animation's clip and time, and each renderer's emissive colour (the glow is read off the
        /// animation, so a frozen Animation would photograph a cold part).</summary>
        private static void LogHeat(WaveCase wc)
        {
            PartParityFxRun fx = wc.Fx;
            if (fx.Heat == null) return;
            var sb = new System.Text.StringBuilder();
            if (fx.Heat is ModuleAnimateHeat h)
                sb.Append("scalar=").Append(h.GetScalar.ToString("0.000", Ic)).Append(" animState=").Append(h.animState.ToString("0.000", Ic))
                    .Append(" animName=").Append(h.animName).Append(" states=").Append(h.stateCount.ToString(Ic)).Append(" temp=")
                    .Append(wc.Real.rootPart.temperature.ToString("0.0", Ic)).Append(" skin=").Append(wc.Real.rootPart.skinTemperature.ToString("0.0", Ic));
            foreach (Animation an in wc.Real.rootPart.GetComponentsInChildren<Animation>(true))
            {
                sb.Append(" anim[").Append(an.name).Append(" enabled=").Append(an.enabled).Append(" cull=").Append(an.cullingType)
                    .Append(" playing=").Append(an.isPlaying);
                foreach (AnimationState st in an) sb.Append(" ").Append(st.name).Append("@").Append(st.normalizedTime.ToString("0.000", Ic)).Append("/w").Append(st.weight.ToString("0.00", Ic)).Append("/on").Append(st.enabled);
                sb.Append("]");
            }
            foreach (Renderer r in wc.Real.rootPart.GetComponentsInChildren<Renderer>(true))
            {
                Material m = r.sharedMaterial;
                if (m == null || !m.HasProperty("_EmissiveColor")) continue;
                Color c = m.GetColor("_EmissiveColor");
                sb.Append(" emissive[").Append(r.name).Append(" vis=").Append(r.isVisible).Append(" ").Append(c.r.ToString("0.00", Ic)).Append(",").Append(c.g.ToString("0.00", Ic)).Append(",").Append(c.b.ToString("0.00", Ic)).Append("]");
            }
            ParsekLog.Info(Tag, $"case {wc.CaseId}: heat real state {sb}");
        }

        // =====================================================================================
        // teardown and shared helpers
        // =====================================================================================

        /// <summary>Releases what an FX case owns: the hold, the RCS input and group, the plate, the
        /// broken ghost. Idempotent.</summary>
        internal static void Release(WaveCase wc)
        {
            PartParityFxRun fx = wc?.Fx;
            if (fx == null) return;
            if (fx.Hold != null)
            {
                try { UnityEngine.Object.Destroy(fx.Hold.gameObject); }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: hold destroy threw {ex.GetType().Name}: {ex.Message}"); }
                fx.Hold = null;
            }
            Vessel v = wc.Real;
            if (v != null)
            {
                try
                {
                    if (fx.RcsInput != null) v.OnPreAutopilotUpdate -= fx.RcsInput;
                    if (fx.Rcs != null) v.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
                }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: RCS release threw {ex.GetType().Name}: {ex.Message}"); }
            }
            fx.RcsInput = null;
            if (fx.PlateObject != null) UnityEngine.Object.Destroy(fx.PlateObject);
            if (fx.PlateMaterial != null) UnityEngine.Object.Destroy(fx.PlateMaterial);
            fx.PlateObject = null;
            fx.PlateMaterial = null;
            if (fx.BrokenGhost != null)
            {
                try { GhostCoOrbitalRig.DestroyLiveGhost(fx.BrokenGhost); }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: broken ghost destroy threw {ex.GetType().Name}: {ex.Message}"); }
                fx.BrokenGhost = null;
            }
        }

        /// <summary>The cheat for the step (design E.2): infinite propellant, restored in the
        /// step's cleanup, one Info line each way.</summary>
        internal static void EnsureCheat(StepContext ctx, WaveCase wc)
        {
            if (ctx.FxCheatBefore.HasValue) return;
            ctx.FxCheatBefore = CheatOptions.InfinitePropellant;
            CheatOptions.InfinitePropellant = true;
            ParsekLog.Info(Tag, $"cheat InfinitePropellant {ctx.FxCheatBefore.Value} -> {CheatOptions.InfinitePropellant} (first case {wc.CaseId})");
        }

        /// <summary>The body renderers of one side (E.3), for the body picture.</summary>
        internal static IList<Renderer> BodyOnly(IList<Renderer> list)
        {
            var body = new List<Renderer>();
            if (list != null)
                foreach (Renderer r in list)
                    if (IsBodyRenderer(r)) body.Add(r);
            return body;
        }

        internal static string JoinInts(IList<int> v)
        {
            var parts = new string[v.Count];
            for (int i = 0; i < v.Count; i++) parts[i] = v[i].ToString(Ic);
            return string.Join(",", parts);
        }
    }
}
