using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.InGameTests;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Where a state case's poll writes (design-part-ghost-parity.md Appendix D.3 step 4): the
    /// case's own event list, tag <c>PartParity</c>, the active recorder's wording. The shared
    /// pollers of <see cref="PartStatePoller"/> append exactly the events
    /// <see cref="FlightRecorder"/> would append for the case vessel.
    /// </summary>
    internal sealed class PartParityPollSink : PartPollSink
    {
        private readonly List<PartEvent> events = new List<PartEvent>();
        internal override string Tag => PartParitySweepRunner.Tag;
        internal override List<PartEvent> Events => events;
        internal override string EventSuffix() => " (part parity state poll)";
        internal override string DeployableBrokenSuffix(ModuleDeployablePart.DeployState ds) =>
            $" (deployable-broken edge, deployState={ds})";
        internal override string DeployableNoPoseRateKey() => "partparity-deployable-no-pose-animation";
        internal override string DeployableNoPoseNote() => "(deployable-no-pose-animation gate, part parity)";
        internal override string LabelSuffix(string label) => $" ({label})";
        internal override string VesselFirstLabelSuffix(string label) => $" ({label})";
        internal override string ConverterDepartureContext(Vessel v) => $"part parity case vessel pid={v.persistentId}";
        internal override string ClassificationMissDetail(PartModule module) => string.Empty;
        internal override string AnimateGenericMissDetail(ModuleAnimateGeneric module) => $" anim='{module.animationName}'";
        internal override string AnimateHeatEventSuffix(float normalizedHeat, string sourceField) =>
            $" heat={normalizedHeat.ToString("F2", CultureInfo.InvariantCulture)} src={sourceField ?? "<unknown>"}";
    }

    /// <summary>Everything one state or context case owns beyond a stowed case (design D.3
    /// to D.5), from the stowed inputs to the teardown. Null on a stowed case.</summary>
    internal sealed class PartParityStateRun
    {
        internal PartParityStateOption Option;

        /// <summary>The state control (<c>ctl-state-events-dropped</c>): the event-route
        /// ghost is built WITHOUT the polled events (design D.6).</summary>
        internal bool DropEvents;

        /// <summary>The prefab carries a sun-tracking solar panel (the tracking settle and the
        /// stepped primes apply).</summary>
        internal bool SunTracker;

        // ----- context (stacked) -----
        internal readonly List<PartParityContextNode> Nodes = new List<PartParityContextNode>();
        internal readonly List<uint> FillerFlightIds = new List<uint>();
        internal readonly List<uint> FillerPids = new List<uint>();

        // ----- an FGV context (D.10 S8) -----

        /// <summary>The subject's attach node holding the part FGV's separation takes off;
        /// null for none.</summary>
        internal string SeparateNode;

        /// <summary>That part on the live case vessel (resolved with the stowed inputs).</summary>
        internal Part SeparatePart;

        /// <summary>The vessel the separation made, killed once the state is reached (and at
        /// teardown when the case stopped before).</summary>
        internal Vessel SeparatedVessel;

        // ----- stowed inputs (step 2) -----
        internal ConfigNode StowedSnapshot;
        internal List<PartEvent> StowedSeeds = new List<PartEvent>();
        internal double SeedUT;
        internal PartTrackingSets Sets;
        internal readonly PartParityPollSink Sink = new PartParityPollSink();
        internal readonly PollScratch Scratch = new PollScratch();
        internal readonly List<Transform> JettisonTransforms = new List<Transform>();
        internal string SignatureBefore;

        /// <summary>The done signal already read reached before the press: the authored
        /// stowed pose is the target, so a press would leave it (an <c>action-failed</c>).</summary>
        internal bool ReachedBeforePress;

        // ----- press and wait (steps 3, 4) -----
        internal bool Pressed;
        internal string ActionResult;
        internal bool? EventVisible;
        internal float PressRealtime;
        internal int FramesSincePress;
        internal double TimeoutSeconds;
        internal double AnimRate = double.NaN;
        internal bool Reached;
        internal float ReachedRealtime;
        internal bool Done;
        internal double? WaitSeconds;
        internal string EndpointField;
        internal string EndpointValue;
        internal bool StillTracking;
        internal Transform RealPivot;
        internal Transform AirbrakeSurface;
        internal readonly List<KeyValuePair<float, Quaternion>> Samples = new List<KeyValuePair<float, Quaternion>>();

        // ----- the ghosts (D.5) -----
        internal GhostCoOrbitalRig.LiveGhost StartGhost;
        internal GhostCoOrbitalRig.LiveGhost StowedGhost;
        internal readonly List<Renderer> EventShown = new List<Renderer>();
        internal readonly List<Renderer> StartShown = new List<Renderer>();
        internal readonly List<Renderer> StowedShown = new List<Renderer>();
        internal readonly List<Renderer> GhostHidden = new List<Renderer>();
        internal List<PartParityRendererRecord> StartRecords;
        internal List<PartParityRendererRecord> StowedRecords;
        internal readonly SortedSet<int> StartLayers = new SortedSet<int>();
        internal readonly SortedSet<int> StowedLayers = new SortedSet<int>();
        internal string GhostEvent;
        internal string GhostStart;
        internal bool? StartFirstFrameAtTarget;
        internal double? SunTargetDeg;
        internal double? SunEventDeg;
        internal double? SunStartFirstFrameDeg;
        internal int ContextHidden;
        internal int DebrisHidden;
        internal PartParityStructureDiff StructureStart;

        /// <summary>Four atlas rows (a state case) or two (the stacked stowed context case).</summary>
        internal bool FourRows;

        // ----- the air context (D.4, PartParityAirContext) -----

        /// <summary>A chute case in the air (context <c>air</c>).</summary>
        internal bool Air;

        /// <summary>The full case raised its <c>deployAltitude</c> after the semi done.</summary>
        internal bool FullRaised;
        internal double? SemiDoneSeconds;

        /// <summary>When the case was done (the canopy window starts there).</summary>
        internal float CanopyDoneRealtime;
        internal double? CanopyTiltDeg;
        internal double? CanopyWobbleDeg;
        internal string CanopyTransform;

        /// <summary><c>canopy-wobble:&lt;deg&gt;</c> when the window's cap forced the capture.</summary>
        internal string CanopyWobbleTag;

        /// <summary>The per-vessel containers the shared pollers keep beside the tracking sets.</summary>
        internal sealed class PollScratch
        {
            internal readonly Dictionary<ulong, string> JettisonNameRaw = new Dictionary<ulong, string>();
            internal readonly Dictionary<ulong, string[]> JettisonNameParsed = new Dictionary<ulong, string[]>();
            internal readonly HashSet<uint> CargoDeployIndexIssues = new HashSet<uint>();
            internal readonly HashSet<uint> CargoAnimationIssues = new HashSet<uint>();
            internal readonly HashSet<uint> CargoClosedPositionIssues = new HashSet<uint>();
            internal readonly HashSet<uint> FairingReadFailures = new HashSet<uint>();
            internal readonly HashSet<ulong> LadderMisses = new HashSet<ulong>();
            internal readonly HashSet<ulong> AnimationGroupMisses = new HashSet<ulong>();
            internal readonly HashSet<ulong> AeroSurfaceMisses = new HashSet<ulong>();
            internal readonly HashSet<ulong> ControlSurfaceMisses = new HashSet<ulong>();
            internal readonly HashSet<ulong> RobotArmMisses = new HashSet<ulong>();
            internal readonly HashSet<ulong> AnimateHeatMisses = new HashSet<ulong>();
            internal readonly HashSet<ulong> AnimateGenericMisses = new HashSet<ulong>();
            internal readonly List<PartEvent> LightBuffer = new List<PartEvent>();
        }
    }

    /// <summary>
    /// The live orbit state engine of the part parity sweep (design-part-ghost-parity.md
    /// Appendix D.3 to D.5): the stacked context and the light's electric charge in the
    /// authored vessel, the stowed inputs, the stock press, the per-frame poll through the
    /// recorder's own shared pollers, the done / timeout / settle wait (sun tracker, airbrake,
    /// lights), the three ghosts per state case with their stepped primes, the N-side capture
    /// and the thrown debris (hidden for the capture, destroyed at teardown and in the step's
    /// cleanup). Every rule it reads is pure (<see cref="PartParityStateRules"/>,
    /// <see cref="PartParityStateWave"/>); this file only reads and drives the live game.
    /// </summary>
    internal static class PartParityStateDriver
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static string Tag => PartParitySweepRunner.Tag;

        /// <summary>The electric charge a light case's part carries (design D.3 step 1).</summary>
        internal const double LightChargeAmount = 50.0;

        // =====================================================================================
        // the state part (D.1 inputs)
        // =====================================================================================

        /// <summary>The live <see cref="PartParityStatePart"/> of a loaded part: its prefab
        /// modules in order (name, type chain up to <c>PartModule</c>, the matching MODULE node
        /// of the part config, the n-th node of that name for the n-th module of that name) and
        /// its stack attach node ids.</summary>
        internal static PartParityStatePart BuildStatePart(AvailablePart info)
        {
            var sp = new PartParityStatePart { Name = info?.name };
            Part prefab = info?.partPrefab;
            if (prefab == null || prefab.Modules == null) return sp;
            ConfigNode[] configs = info.partConfig != null ? info.partConfig.GetNodes("MODULE") : new ConfigNode[0];
            var used = new bool[configs.Length];
            for (int i = 0; i < prefab.Modules.Count; i++)
            {
                PartModule m = prefab.Modules[i];
                if (m == null) { sp.Modules.Add(null); continue; }
                var module = new PartParityStateModule { Index = i, Name = m.moduleName };
                for (Type t = m.GetType(); t != null; t = t.BaseType)
                {
                    module.TypeChain.Add(t.Name);
                    if (t == typeof(PartModule)) break;
                }
                for (int c = 0; c < configs.Length; c++)
                {
                    if (used[c] || configs[c].GetValue("name") != m.moduleName) continue;
                    used[c] = true;
                    module.Config = configs[c];
                    break;
                }
                sp.Modules.Add(module);
            }
            if (prefab.attachNodes != null)
                foreach (AttachNode n in prefab.attachNodes)
                    if (n != null && n.nodeType == AttachNode.NodeType.Stack && !string.IsNullOrEmpty(n.id))
                        sp.StackNodes.Add(n.id);
            return sp;
        }

        /// <summary>The prefab carries a tracking solar panel (the 9 sun trackers per install).</summary>
        internal static bool HasSunTracker(AvailablePart info)
        {
            Part prefab = info?.partPrefab;
            if (prefab == null) return false;
            foreach (PartModule m in prefab.Modules)
                if (m is ModuleDeployableSolarPanel sp && sp.isTracking) return true;
            return false;
        }

        internal static PartParityStateRun NewRun(PartParityCaseKey key, PartParityPartEntry entry, bool dropEvents)
        {
            var run = new PartParityStateRun
            {
                Option = key.StateOption,
                DropEvents = dropEvents,
                SunTracker = entry != null && HasSunTracker(entry.Info),
                FourRows = TestCommandPartParitySweep.IsStateCase(key),
                Air = key.Context == PartParityStateRules.ContextAir,
            };
            return run;
        }

        // =====================================================================================
        // authoring (D.3 step 1)
        // =====================================================================================

        /// <summary>
        /// The vessel node of a state or context case: the subject part as the root, every
        /// prefab module in order; a light case's part with <c>ElectricCharge</c> (stock
        /// <c>ModuleLight</c> switches itself off when its draw fails); a stacked context with
        /// one <see cref="PartParityStateWave.FillerPartName"/> on each node the option names,
        /// meeting it by the filler's opposite stack node. Null with a reason when the filler
        /// or a node is missing.
        /// </summary>
        internal static ConfigNode AuthorVesselNode(WaveCase wc, Vessel anchor, out string failReason)
        {
            failReason = null;
            PartParityStateRun run = wc.State;
            if (run.Option.Fgv != null) return AuthorFgvVesselNode(wc, anchor, out failReason);
            AvailablePart info = wc.Entry.Info;
            List<string> nodes = run.Option.NodeNames ?? new List<string>();
            AvailablePart filler = nodes.Count > 0 ? PartLoader.getPartInfoByName(PartParityStateWave.FillerPartName) : null;
            if (nodes.Count > 0 && filler == null)
            {
                failReason = "context filler part " + PartParityStateWave.FillerPartName + " not loaded";
                return null;
            }
            List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(1 + nodes.Count);
            var attachments = new Dictionary<string, int>();
            for (int i = 0; i < nodes.Count; i++) attachments[nodes[i]] = i + 1;
            bool light = run.Option.Family == PartParityStateRules.FamilyLight
                         || run.Option.Family == PartParityStateRules.FamilyCabinLight;
            var parts = new List<ConfigNode>();
            ConfigNode subject = GhostCoOrbitalRig.AuthorPartNode(info, ids[0], 0, Vector3.zero,
                attachments.Count > 0 ? attachments : null, wc.RealVariant, null);
            if (light) AddCharge(subject);
            AuthorStowedPose(subject, info, run.Option);
            parts.Add(subject);
            for (int i = 0; i < nodes.Count; i++)
            {
                AttachNode at = info.partPrefab.FindAttachNode(nodes[i]);
                if (at == null) { failReason = "subject node " + nodes[i] + " missing"; return null; }
                string fillerNodeId = PartParityStateWave.FillerAttachNode(at.position.y);
                AttachNode fn = filler.partPrefab.FindAttachNode(fillerNodeId);
                if (fn == null) { failReason = "filler node " + fillerNodeId + " missing"; return null; }
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(filler, ids[i + 1], 0, at.position - fn.position,
                    new Dictionary<string, int> { { fillerNodeId, 0 } }, null, null));
                run.Nodes.Add(new PartParityContextNode { Node = nodes[i], Part = filler.name });
                run.FillerFlightIds.Add(ids[i + 1]);
            }
            Orbit src = anchor.orbit;
            var placeholder = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: authored state vessel family={run.Option.Family} " +
                $"state={run.Option.State} parts={parts.Count.ToString(Ic)} nodes=[{string.Join(",", nodes.ToArray())}] " +
                $"charge={(light ? LightChargeAmount.ToString("0", Ic) : "none")}");
            return ProtoVessel.CreateVesselNode(PartParityCaseRules.VesselName(wc.CaseId), VesselType.Probe,
                placeholder, 0, parts.ToArray());
        }

        /// <summary>
        /// An FGV context's vessel (D.10 S8): FGV's own authoring of the configuration
        /// (<see cref="FairingGhostAuthoring"/>), with the case part as the vessel's root (the
        /// engine shroud configuration authored engine-rooted: the same craft, the tank on the
        /// engine's top node and the lower part on its bottom node), renamed to the case's
        /// vessel name. Every other part is the context, hidden on every side like a stacked
        /// filler. A colour-variant configuration's chosen variant becomes the case's real
        /// variant, so the skin proof requests it.
        /// </summary>
        private static ConfigNode AuthorFgvVesselNode(WaveCase wc, Vessel anchor, out string failReason)
        {
            PartParityStateRun run = wc.State;
            PartParityFgvContext c = run.Option.Fgv;
            FairingGhostConfig cfg = FairingGhostAuthoring.ByNumber(c.Number);
            if (cfg == null)
            {
                failReason = "no FGV configuration " + c.Number.ToString(Ic);
                return null;
            }
            FairingAuthoredVessel v = FairingGhostAuthoring.Build(cfg, anchor, Tag, wc.Entry.Info, true, wc.RealVariant, out failReason);
            if (v == null || v.Node == null)
            {
                failReason = "FGV " + c.Id + ": " + (failReason ?? "not authored");
                return null;
            }
            ConfigNode[] parts = v.Node.GetNodes("PART");
            string root = parts.Length > 0 ? parts[0].GetValue("name") : null;
            if (root != wc.Entry.Info.name)
            {
                failReason = "FGV " + c.Id + ": the authored root is " + (root ?? "<none>") + ", not the case part " + wc.Entry.Info.name;
                return null;
            }
            v.Node.SetValue("name", PartParityCaseRules.VesselName(wc.CaseId), true);
            if (v.VariantName != null) wc.RealVariant = v.VariantName;
            for (int i = 0; i < v.ContextParts.Count; i++)
            {
                run.Nodes.Add(new PartParityContextNode { Node = v.ContextParts[i].Key, Part = v.ContextParts[i].Value });
                run.FillerFlightIds.Add(v.ContextFlightIds[i]);
            }
            run.SeparateNode = run.Option.State == PartParityStateRules.StateSeparated ? v.SeparatingNode : null;
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: authored FGV context {c.Id} family={run.Option.Family} " +
                $"state={run.Option.State} parts={parts.Length.ToString(Ic)} variant={v.VariantName ?? "<base>"} " +
                $"separates={run.SeparateNode ?? "-"}");
            return v.Node;
        }

        /// <summary>The stowed pose of the pressed module of a gear or cargo bay case
        /// (<see cref="PartParityCaseAuthoring.AuthorStowedPose"/> restricted to it).</summary>
        private static void AuthorStowedPose(ConfigNode subject, AvailablePart info, PartParityStateOption opt)
        {
            if (opt.Family != PartParityStateRules.FamilyGear && opt.Family != PartParityStateRules.FamilyCargoBay) return;
            foreach (string d in PartParityCaseAuthoring.AuthorStowedPose(subject, info.partPrefab, opt.ModuleIndex))
                ParsekLog.Verbose(Tag, $"authored stowed pose {info.name}: {d}");
        }

        private static void AddCharge(ConfigNode part)
        {
            ConfigNode r = part.AddNode("RESOURCE");
            r.AddValue("name", "ElectricCharge");
            r.AddValue("amount", LightChargeAmount.ToString("R", Ic));
            r.AddValue("maxAmount", LightChargeAmount.ToString("R", Ic));
            r.AddValue("flowState", "True");
            r.AddValue("isTweakable", "True");
            r.AddValue("hideFlow", "False");
            r.AddValue("isVisible", "True");
            r.AddValue("flowMode", "Both");
        }

        // =====================================================================================
        // the state phase of a wave (D.3 steps 2 to 4)
        // =====================================================================================

        /// <summary>
        /// Runs a settled state wave to its states: stowed inputs, the press, then every frame
        /// the poll, the done signal, the settle, the refused and timeout checks, until every
        /// case is done or faulted. A case's fault is its own setup status; the host guard
        /// stops the wave.
        /// </summary>
        internal static IEnumerator RunPhase(StepContext ctx, List<WaveCase> wave, Func<string, bool> hostDamaged,
            Action<WaveCase, string, Action> guard)
        {
            foreach (var wc in wave)
            {
                if (!Healthy(wc)) continue;
                guard(wc, "state-inputs", () => CaptureStowedInputs(wc));
            }
            foreach (var wc in wave)
            {
                if (!Healthy(wc)) continue;
                guard(wc, "state-press", () => Press(wc));
            }
            while (true)
            {
                if (hostDamaged("state wait")) yield break;
                bool pending = false;
                foreach (var wc in wave)
                {
                    if (!Healthy(wc) || wc.State.Done) continue;
                    guard(wc, "state-wait", () => Step(wc));
                    if (Healthy(wc) && !wc.State.Done) pending = true;
                }
                if (!pending) break;
                yield return null;
            }
            int reached = 0, faults = 0;
            foreach (var wc in wave)
            {
                if (wc.State == null) continue;
                if (wc.State.Reached) reached++;
                if (wc.Status != null) faults++;
            }
            ParsekLog.Info(Tag, $"state wave: cases={wave.Count.ToString(Ic)} reached={reached.ToString(Ic)} " +
                $"faults={faults.ToString(Ic)}");
        }

        private static bool Healthy(WaveCase wc)
            => wc.State != null && wc.Status == null && wc.Real != null && wc.Real.rootPart != null;

        /// <summary>Step 2: the stowed snapshot, the case's own tracking sets seeded from the
        /// live vessel, the stowed seed events at <c>t0</c>, the jettison transforms the press
        /// may throw off, and the pressed module's state signature.</summary>
        internal static void CaptureStowedInputs(WaveCase wc)
        {
            PartParityStateRun run = wc.State;
            Vessel v = wc.Real;
            Part part = v.rootPart;
            foreach (Part p in v.parts)
                if (p != null && p != part) run.FillerPids.Add(p.persistentId);
            run.StowedSnapshot = FlightRecorder.TryBackupSnapshot(v);
            run.Sets = new PartTrackingSets();
            PartStateSeeder.SeedPartStates(v, run.Sets, FlightRecorder.CacheEngineModules(v),
                FlightRecorder.CacheRcsModules(v), seedColorChangerLights: true, logTag: Tag);
            run.SeedUT = Planetarium.GetUniversalTime();
            run.StowedSeeds = PartStateSeeder.EmitSeedEvents(run.Sets, PartNames(v), run.SeedUT, Tag);
            foreach (PartModule m in part.Modules)
            {
                if (m is ModuleJettison j && j.jettisonTransform != null && !run.JettisonTransforms.Contains(j.jettisonTransform))
                    run.JettisonTransforms.Add(j.jettisonTransform);
                // A deployed fairing's panels leave as free physical objects (D.3, thrown
                // parts): kept with the jettison transforms, hidden once thrown, destroyed at
                // teardown.
                if (m is ModuleProceduralFairing f && f.Panels != null)
                    foreach (var panel in f.Panels)
                        if (panel?.go != null && !run.JettisonTransforms.Contains(panel.go.transform))
                            run.JettisonTransforms.Add(panel.go.transform);
            }
            if (run.SeparateNode != null)
            {
                AttachNode sn = part.FindAttachNode(run.SeparateNode);
                run.SeparatePart = sn != null ? sn.attachedPart : null;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: FGV separation part " +
                    $"{(run.SeparatePart != null ? "'" + run.SeparatePart.partInfo?.name + "' pid=" + run.SeparatePart.persistentId.ToString(Ic) : "<not found>")} " +
                    $"node={run.SeparateNode}");
            }
            run.SignatureBefore = Signature(part, run.Option);
            run.ReachedBeforePress = run.Option.Action != null
                && PartParityStateRules.StateReached(run.Option, Observe(part, run.Option, out _, out _));
            ReadAnimation(part, out double clip, out double speed);
            run.TimeoutSeconds = PartParityStateRules.StateTimeoutSeconds(run.Option, clip, speed);
            if (run.Option.Family == PartParityStateRules.FamilyCabinLight
                && run.Option.ModuleIndex >= 0 && run.Option.ModuleIndex < part.Modules.Count
                && part.Modules[run.Option.ModuleIndex] is ModuleColorChanger cc)
                run.AnimRate = cc.animRate;
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: stowed inputs snapshot={(run.StowedSnapshot != null ? "true" : "false")} " +
                $"seeds={run.StowedSeeds.Count.ToString(Ic)} t0={run.SeedUT.ToString("0.00", Ic)} " +
                $"jettisonTransforms={run.JettisonTransforms.Count.ToString(Ic)} fillers={run.FillerPids.Count.ToString(Ic)} " +
                $"clip={clip.ToString("0.00", Ic)}s speed={speed.ToString("0.00", Ic)} timeout={run.TimeoutSeconds.ToString("0.0", Ic)}s " +
                $"sunTracker={(run.SunTracker ? "true" : "false")}");
        }

        private static Dictionary<uint, string> PartNames(Vessel v)
        {
            var names = new Dictionary<uint, string>();
            foreach (Part p in v.parts)
                if (p != null && !names.ContainsKey(p.persistentId)) names[p.persistentId] = p.partInfo?.name ?? "unknown";
            return names;
        }

        /// <summary>Step 3: the module's own stock event (or KSPAction, Activate), what a PAW
        /// click or an action group does; on every jettison module for the jettison family.
        /// A context case with no action has reached its state at once.</summary>
        internal static void Press(WaveCase wc)
        {
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            run.PressRealtime = Time.realtimeSinceStartup;
            if (run.Option.Action == null)
            {
                run.Reached = true;
                run.ReachedRealtime = run.PressRealtime;
                run.WaitSeconds = 0.0;
                run.EndpointField = "context";
                run.EndpointValue = run.Option.State;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: context case, nothing pressed");
                return;
            }
            if (run.ReachedBeforePress)
            {
                // Pressing would leave the target (every toggle the sweep presses flips it).
                Observe(part, run.Option, out string f0, out string v0);
                run.ActionResult = PartParityStateRules.ActionRefused;
                run.Done = true;
                wc.Status = TestCommandPartParitySweep.SetupActionFailed;
                wc.Detail = wc.Detail ?? "already " + run.Option.State + " before the press (" + f0 + "=" + v0 +
                    "): the authored stowed pose reads as the target";
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}; not pressed");
                return;
            }
            run.Pressed = true;
            string result = PartParityStateRules.ActionInvoked;
            string detail = null;
            if (run.Option.PreAction != null)
                result = InvokeEvent(run, part, run.Option.ModuleIndex, run.Option.PreAction, out detail);
            if (result == PartParityStateRules.ActionInvoked)
            {
                if (run.Option.Action == PartParityStateRules.ActionSeparate)
                    result = PressSeparate(wc, run, part, out detail);
                else
                    result = PressModules(run, part, out detail);
            }
            run.ActionResult = result;
            ParsekLog.Info(Tag, $"case {wc.CaseId}: pressed {run.Option.Module}.{run.Option.Action} " +
                (run.Option.PreAction != null ? "pre=" + run.Option.PreAction + " " : string.Empty) +
                $"modules=[{string.Join(",", run.Option.ModuleIndices.ConvertAll(i => i.ToString(Ic)).ToArray())}] " +
                $"kspAction={(run.Option.ActionIsKspAction ? "true" : "false")} " +
                $"eventVisible={(run.EventVisible.HasValue ? (run.EventVisible.Value ? "true" : "false") : "-")} " +
                $"result={result}{(detail != null ? " detail=" + detail : string.Empty)}");
            if (result != PartParityStateRules.ActionInvoked)
            {
                wc.Status = TestCommandPartParitySweep.SetupActionFailed;
                wc.Detail = wc.Detail ?? "press " + result + (detail != null ? ": " + detail : string.Empty);
                run.Done = true;
            }
            if (run.Option.Family == PartParityStateRules.FamilyAirbrake)
                run.AirbrakeSurface = FindNamedTransform(part, part.Modules[run.Option.ModuleIndex], "transformName");
            if (run.SunTracker)
            {
                var dep = part.FindModuleImplementing<ModuleDeployablePart>();
                run.RealPivot = dep != null ? dep.panelRotationTransform : null;
            }
        }

        /// <summary>The option's action on each of its modules: the KSPAction (Activate) or
        /// the KSPEvent, stopping at the first that is missing or throws.</summary>
        private static string PressModules(PartParityStateRun run, Part part, out string detail)
        {
            detail = null;
            foreach (int idx in run.Option.ModuleIndices)
            {
                PartModule m = idx >= 0 && idx < part.Modules.Count ? part.Modules[idx] : null;
                if (m == null) { detail = "no module at " + idx.ToString(Ic); return PartParityStateRules.ActionMissing; }
                try
                {
                    if (run.Option.ActionIsKspAction)
                    {
                        BaseAction a = m.Actions[run.Option.Action];
                        if (a == null) { detail = "no KSPAction " + run.Option.Action; return PartParityStateRules.ActionMissing; }
                        a.Invoke(new KSPActionParam(KSPActionGroup.None, KSPActionType.Activate));
                    }
                    else
                    {
                        string r = InvokeEvent(run, part, idx, run.Option.Action, out detail);
                        if (r != PartParityStateRules.ActionInvoked) return r;
                    }
                }
                catch (Exception ex)
                {
                    detail = ex.GetType().Name + ": " + ex.Message;
                    return PartParityStateRules.ActionThrew;
                }
            }
            return PartParityStateRules.ActionInvoked;
        }

        /// <summary>One module's stock KSPEvent, recording whether the event was visible (the
        /// first one pressed).</summary>
        private static string InvokeEvent(PartParityStateRun run, Part part, int idx, string eventName, out string detail)
        {
            detail = null;
            PartModule m = idx >= 0 && idx < part.Modules.Count ? part.Modules[idx] : null;
            if (m == null) { detail = "no module at " + idx.ToString(Ic); return PartParityStateRules.ActionMissing; }
            try
            {
                BaseEvent e = m.Events[eventName];
                if (e == null) { detail = "no KSPEvent " + eventName; return PartParityStateRules.ActionMissing; }
                if (!run.EventVisible.HasValue) run.EventVisible = e.guiActive && e.active;
                e.Invoke();
                return PartParityStateRules.ActionInvoked;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return PartParityStateRules.ActionThrew;
            }
        }

        /// <summary>
        /// FGV's separation (D.10 S8): the engine plate's own <c>ModuleDecouple.Decouple</c>,
        /// else the leaving part's <c>Part.decouple</c> (the interstage's upper stage, the part
        /// below the engine shroud), exactly the call FGV makes. The new vessel is killed once
        /// the state is reached (<see cref="KillSeparatedVessel"/>).
        /// </summary>
        private static string PressSeparate(WaveCase wc, PartParityStateRun run, Part part, out string detail)
        {
            detail = null;
            if (run.SeparatePart == null || run.SeparatePart.vessel != wc.Real)
            {
                detail = "no part on the subject's node " + (run.SeparateNode ?? "<none>") + " of the case vessel";
                return PartParityStateRules.ActionMissing;
            }
            try
            {
                ModuleDecouple dec = run.Option.Fgv != null && run.Option.Fgv.SeparateWithDecoupler
                    ? part.FindModuleImplementing<ModuleDecouple>() : null;
                if (run.Option.Fgv != null && run.Option.Fgv.SeparateWithDecoupler && dec == null)
                {
                    detail = "no ModuleDecouple on " + part.partInfo?.name;
                    return PartParityStateRules.ActionMissing;
                }
                if (dec != null) dec.Decouple();
                else run.SeparatePart.decouple();
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: FGV separation by {(dec != null ? "ModuleDecouple.Decouple" : "Part.decouple")} " +
                    $"of '{run.SeparatePart.partInfo?.name}' pid={run.SeparatePart.persistentId.ToString(Ic)}");
                return PartParityStateRules.ActionInvoked;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return PartParityStateRules.ActionThrew;
            }
        }

        /// <summary>Kills the vessel an FGV separation made (FGV's <c>Die</c>), once. Idempotent.</summary>
        internal static void KillSeparatedVessel(WaveCase wc, PartParityStateRun run, string why)
        {
            if (run == null || run.SeparatePart == null) return;
            Vessel sv = run.SeparatePart != null ? run.SeparatePart.vessel : null;
            if (sv == null || sv == wc.Real || sv.state == Vessel.State.DEAD) return;
            run.SeparatedVessel = sv;
            try
            {
                sv.Die();
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: separated vessel '{sv.vesselName}' pid={sv.persistentId.ToString(Ic)} killed ({why})");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: separated vessel Die threw {ex.GetType().Name}: {ex.Message} ({why})");
            }
        }

        /// <summary>One frame of step 4 for one case: poll, then the done signal, the refused
        /// check, the timeout, and once reached the settle.</summary>
        internal static void Step(WaveCase wc)
        {
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            float now = Time.realtimeSinceStartup;
            if (run.Pressed) Poll(wc.Real, run);
            run.FramesSincePress++;
            bool animating = AnyAnimationPlaying(part);
            if (run.Air && run.Pressed) PartParityAirContext.StepFull(wc, run, part, now, animating);
            if (!run.Reached)
            {
                PartParityStateObservation o = Observe(part, run.Option, out string field, out string value, run);
                if (PartParityStateRules.StateReached(run.Option, o))
                {
                    KillSeparatedVessel(wc, run, "reached");
                    run.Reached = true;
                    run.ReachedRealtime = now;
                    run.WaitSeconds = now - run.PressRealtime;
                    run.EndpointField = field;
                    run.EndpointValue = value;
                    ParsekLog.Verbose(Tag, $"case {wc.CaseId}: reached {run.Option.State} after " +
                        $"{run.WaitSeconds.Value.ToString("0.00", Ic)}s ({field}={value}) events={run.Sink.Events.Count.ToString(Ic)}");
                }
                else if (run.FramesSincePress == PartParityStateWave.RefuseCheckFrames
                         && PartParityStateWave.PressRefused(run.SignatureBefore, Signature(part, run.Option), animating, false))
                {
                    run.ActionResult = PartParityStateRules.ActionRefused;
                    wc.Status = TestCommandPartParitySweep.SetupActionFailed;
                    wc.Detail = wc.Detail ?? "press refused: module state unchanged " +
                        PartParityStateWave.RefuseCheckFrames.ToString(Ic) + " frames later (" + field + "=" + value + ")";
                    run.Done = true;
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                    return;
                }
                else if (now - run.PressRealtime > run.TimeoutSeconds)
                {
                    wc.Status = TestCommandPartParitySweep.SetupStateTimeout;
                    wc.Detail = wc.Detail ?? "never reached " + run.Option.State + " in " +
                        run.TimeoutSeconds.ToString("0.0", Ic) + " s (" + field + "=" + value + ")";
                    run.WaitSeconds = now - run.PressRealtime;
                    run.EndpointField = field;
                    run.EndpointValue = value;
                    run.Done = true;
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: {wc.Detail}");
                    return;
                }
                if (!run.Reached) return;
            }
            Settle(wc, run, part, now, animating);
        }

        /// <summary>The settle after the done signal (D.1, D.3): the family's minimum (lights
        /// 1 s, cabin lights 1 / animRate + 0.5 s, else 0.5 s) with no Animation playing (capped
        /// at the stowed settle cap, tagged still-animating); a sun tracker's pivot still for
        /// 0.5 s (cap 25 s, tagged still-tracking); an airbrake's surface still for 0.5 s (cap 5 s).</summary>
        private static void Settle(WaveCase wc, PartParityStateRun run, Part part, float now, bool animating)
        {
            double since = now - run.ReachedRealtime;
            Transform still = run.SunTracker ? run.RealPivot : run.AirbrakeSurface;
            if (still != null) run.Samples.Add(new KeyValuePair<float, Quaternion>(now, still.localRotation));
            double min = PartParityStateRules.PostDoneSettleSeconds(run.Option.Family, run.AnimRate);
            if (since < min) return;
            if (animating && since < PartParitySweepPlan.MaxSettleSeconds) return;
            if (animating) wc.StillAnimating = true;
            if (still != null)
            {
                double window = run.SunTracker ? PartParityStateRules.SunTrackerWindowSeconds : PartParityStateRules.AirbrakeWindowSeconds;
                double cap = run.SunTracker ? PartParityStateRules.SunTrackerCapSeconds : PartParityStateRules.AirbrakeCapSeconds;
                double delta = WindowDelta(run.Samples, now, window);
                bool settled = run.SunTracker ? PartParityStateRules.SunTrackerSettled(delta) : PartParityStateRules.AirbrakeSettled(delta);
                if (!settled && since < cap) return;
                if (!settled && run.SunTracker) run.StillTracking = true;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: {(run.SunTracker ? "sun tracker" : "airbrake surface")} " +
                    $"{(settled ? "still" : "still moving at the cap")} delta={(double.IsNaN(delta) ? "-" : delta.ToString("0.000", Ic))}deg " +
                    $"over {window.ToString("0.0", Ic)}s after {since.ToString("0.0", Ic)}s");
            }
            run.Done = true;
        }

        /// <summary>The rotation in degrees between the newest sample and the newest sample at
        /// least <paramref name="window"/> s older; NaN until the window is covered.</summary>
        private static double WindowDelta(List<KeyValuePair<float, Quaternion>> samples, float now, double window)
        {
            if (samples.Count < 2) return double.NaN;
            Quaternion latest = samples[samples.Count - 1].Value;
            for (int i = samples.Count - 2; i >= 0; i--)
                if (now - samples[i].Key >= window) return Quaternion.Angle(samples[i].Value, latest);
            return double.NaN;
        }

        /// <summary>
        /// The shared pollers over the case vessel into the case's sink (D.3 step 4): every
        /// family <see cref="PartStatePoller"/> holds, with the case's own tracking sets, at the
        /// live UT, so the events are the ones the active recorder would append.
        /// </summary>
        internal static void Poll(Vessel v, PartParityStateRun run)
        {
            if (v == null || run.Sets == null) return;
            double ut = Planetarium.GetUniversalTime();
            PartTrackingSets s = run.Sets;
            PartParityStateRun.PollScratch x = run.Scratch;
            PartParityPollSink sink = run.Sink;
            PartStatePoller.PollParachutes(v, s.parachuteStates, ut, sink);
            PartStatePoller.PollJettisons(v, s.jettisonedShrouds, x.JettisonNameRaw, x.JettisonNameParsed, ut, sink);
            PartStatePoller.PollDeployables(v, s.brokenDeployables, s.extendedDeployables, ut, sink);
            PartStatePoller.PollLights(v, s.lightsOn, s.blinkingLights, s.lightBlinkRates, x.LightBuffer, ut, sink);
            PartStatePoller.PollGear(v, s.deployedGear, ut, sink);
            PartStatePoller.PollCargoBays(v, s.openCargoBays, x.CargoDeployIndexIssues, x.CargoAnimationIssues,
                x.CargoClosedPositionIssues, ut, sink);
            PartStatePoller.PollLadders(v, s.deployedLadders, x.LadderMisses, ut, sink);
            PartStatePoller.PollAnimationGroups(v, s.deployedAnimationGroups, x.AnimationGroupMisses, ut, sink);
            PartStatePoller.PollAeroSurfaces(v, s.deployedAeroSurfaceModules, x.AeroSurfaceMisses, ut, sink);
            PartStatePoller.PollControlSurfaces(v, s.deployedControlSurfaceModules, x.ControlSurfaceMisses, ut, sink);
            PartStatePoller.PollRobotArmScanners(v, s.deployedRobotArmScannerModules, x.RobotArmMisses, ut, sink);
            PartStatePoller.PollAnimateHeat(v, s.animateHeatLevels, x.AnimateHeatMisses, ut, sink);
            PartStatePoller.PollAnimateGeneric(v, s.deployedAnimateGenericModules, x.AnimateGenericMisses, ut, sink);
            PartStatePoller.PollFairings(v, s.deployedFairings, x.FairingReadFailures, ut, sink);
        }

        /// <summary>The stock values the family's done signal reads (D.1), plus the field and
        /// value named in the proof's endpoint.</summary>
        internal static PartParityStateObservation Observe(Part part, PartParityStateOption opt,
            out string field, out string value, PartParityStateRun run = null)
        {
            var o = new PartParityStateObservation { AnimationPlaying = AnyAnimationPlaying(part) };
            field = null;
            value = null;
            if (opt.State == PartParityStateRules.StateSeparated && run != null)
            {
                o.SeparatedPartGone = run.SeparatePart == null || run.SeparatePart.vessel != part.vessel;
                field = "separated";
                value = o.SeparatedPartGone == true ? "gone" : "attached";
                return o;
            }
            PartModule m = opt.ModuleIndex >= 0 && opt.ModuleIndex < part.Modules.Count ? part.Modules[opt.ModuleIndex] : null;
            switch (opt.Family)
            {
                case PartParityStateRules.FamilyDeployable:
                    var dep = part.FindModuleImplementing<ModuleDeployablePart>();
                    o.DeployState = dep != null ? dep.deployState.ToString() : null;
                    field = "deployState"; value = o.DeployState;
                    break;
                case PartParityStateRules.FamilyGear:
                    o.GearState = (m as ModuleWheels.ModuleWheelDeployment)?.stateString;
                    field = "stateString"; value = o.GearState;
                    break;
                case PartParityStateRules.FamilyLadder:
                case PartParityStateRules.FamilyAnimationGroup:
                case PartParityStateRules.FamilyAnimateGeneric:
                case PartParityStateRules.FamilyAirbrake:
                    o.ClassifierDeployed = Classify(opt.Family, m);
                    field = "classifier";
                    value = o.ClassifierDeployed.HasValue ? (o.ClassifierDeployed.Value ? "deployed" : "not-deployed") : "unclassified";
                    break;
                case PartParityStateRules.FamilyCargoBay:
                    var cargo = part.FindModuleImplementing<ModuleCargoBay>();
                    var anim = m as ModuleAnimateGeneric;
                    if (cargo != null && anim != null)
                    {
                        o.CargoAnimTime = anim.animTime;
                        o.CargoClosedPosition = cargo.closedPosition;
                        o.CargoDeployLimitStop = FlightRecorder.ResolveCargoBayDeployLimitStop(anim, cargo.closedPosition);
                    }
                    field = "animTime";
                    value = o.CargoAnimTime.HasValue ? o.CargoAnimTime.Value.ToString("0.000", Ic) : null;
                    break;
                case PartParityStateRules.FamilyLight:
                    var light = part.FindModuleImplementing<ModuleLight>();
                    o.LightOn = light != null ? (bool?)light.isOn : null;
                    field = "isOn"; value = o.LightOn.HasValue ? (o.LightOn.Value ? "True" : "False") : null;
                    break;
                case PartParityStateRules.FamilyCabinLight:
                    var cc = m as ModuleColorChanger;
                    o.LightOn = cc != null ? (bool?)cc.animState : null;
                    field = "animState"; value = o.LightOn.HasValue ? (o.LightOn.Value ? "True" : "False") : null;
                    break;
                case PartParityStateRules.FamilyParachute:
                    var chute = part.FindModuleImplementing<ModuleParachute>();
                    o.ParachuteState = chute != null ? chute.deploymentState.ToString() : null;
                    field = "deploymentState"; value = o.ParachuteState;
                    break;
                case PartParityStateRules.FamilyJettison:
                    foreach (int idx in opt.ModuleIndices)
                    {
                        if (!(idx >= 0 && idx < part.Modules.Count && part.Modules[idx] is ModuleJettison j)) continue;
                        o.JettisonModules++;
                        if (j.isJettisoned) o.JettisonedModules++;
                    }
                    field = "isJettisoned";
                    value = o.JettisonedModules.ToString(Ic) + "/" + o.JettisonModules.ToString(Ic);
                    break;
                case PartParityStateRules.FamilyFairing:
                    var fairing = part.FindModuleImplementing<ModuleProceduralFairing>();
                    if (fairing != null) o.FairingScalar = fairing.GetScalar;
                    field = "GetScalar";
                    value = o.FairingScalar.HasValue ? o.FairingScalar.Value.ToString("0.000", Ic) : null;
                    break;
            }
            return o;
        }

        private static bool? Classify(string family, PartModule m)
        {
            if (m == null) return null;
            bool ok, deployed;
            switch (family)
            {
                case PartParityStateRules.FamilyLadder:
                    ok = FlightRecorder.TryClassifyRetractableLadderState(m, out deployed, out _);
                    break;
                case PartParityStateRules.FamilyAnimationGroup:
                    ok = FlightRecorder.TryClassifyAnimationGroupState(m, out deployed, out _);
                    break;
                case PartParityStateRules.FamilyAirbrake:
                    ok = FlightRecorder.TryClassifyAeroSurfaceState(m, out deployed, out _);
                    break;
                default:
                    var ag = m as ModuleAnimateGeneric;
                    if (ag == null) return null;
                    ok = FlightRecorder.TryClassifyAnimateGenericState(ag, out deployed, out _);
                    break;
            }
            return ok ? (bool?)deployed : null;
        }

        /// <summary>The pressed modules' KSPField values and the part's Animation play state,
        /// one string (the refused-press check compares two of them).</summary>
        internal static string Signature(Part part, PartParityStateOption opt)
        {
            var sb = new StringBuilder();
            foreach (int idx in opt.ModuleIndices)
            {
                PartModule m = idx >= 0 && idx < part.Modules.Count ? part.Modules[idx] : null;
                if (m == null || m.Fields == null) continue;
                sb.Append('[').Append(idx.ToString(Ic)).Append(']');
                for (int i = 0; i < m.Fields.Count; i++)
                {
                    BaseField f = m.Fields[i];
                    object v = null;
                    try { v = f.GetValue(m); } catch (Exception) { v = "<throws>"; }
                    sb.Append(f.name).Append('=').Append(v != null ? Convert.ToString(v, Ic) : "null").Append(';');
                }
            }
            foreach (Animation an in part.GetComponentsInChildren<Animation>(true))
            {
                if (an == null) continue;
                foreach (AnimationState st in an)
                    if (st != null) sb.Append(st.name).Append('@').Append(st.normalizedTime.ToString("0.000", Ic)).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>The longest Animation clip of the part and its play speed (the state
        /// timeout reads them); NaN when the part has none.</summary>
        private static void ReadAnimation(Part part, out double clip, out double speed)
        {
            clip = double.NaN;
            speed = 1.0;
            foreach (Animation an in part.GetComponentsInChildren<Animation>(true))
            {
                if (an == null) continue;
                foreach (AnimationState st in an)
                {
                    if (st == null || !(st.length > 0f)) continue;
                    if (double.IsNaN(clip) || st.length > clip)
                    {
                        clip = st.length;
                        speed = st.speed != 0f ? st.speed : 1.0;
                    }
                }
            }
        }

        internal static bool AnyAnimationPlaying(Part part)
        {
            foreach (Animation an in part.GetComponentsInChildren<Animation>(true))
                if (an != null && an.isPlaying) return true;
            return false;
        }

        private static Transform FindNamedTransform(Part part, PartModule m, string fieldName)
        {
            string name = m != null ? PartParitySkinRules.ReadStringMember(m, fieldName) : null;
            return string.IsNullOrEmpty(name) ? null : part.FindModelTransform(name);
        }

        // =====================================================================================
        // the ghosts (D.5) and the case's renderers
        // =====================================================================================

        /// <summary>
        /// The state half of the case preparation (design 4.2 with D.5): the real renderer
        /// records read AFTER the state is reached (the context fillers and, once jettisoned,
        /// the thrown shroud left out and hidden), then the three ghosts: the event route (the
        /// stowed snapshot, the stowed seeds and the polled events, primed past the clip, then
        /// stepped for a sun tracker), the start route (the deployed snapshot, its start seeds,
        /// primed at its first frame and read there, then stepped) and the stowed reference;
        /// each placed at the real part, kept out of physics and read back through
        /// <see cref="PartParityStateRules.GhostStateReached"/>. A context case with no press
        /// builds only its one ghost (two atlas rows).
        /// </summary>
        internal static void Prepare(StepContext ctx, WaveCase wc)
        {
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            Transform partT = part.transform;
            wc.Skin = PartParitySkinRules.NewProof(wc.SkinOption, wc.RealVariant, wc.Entry?.B9Modules);
            wc.Skin.Real = PartParityCaseAuthoring.LiveSkinName(part, wc.Skin, out string realMember);
            wc.SkinRealMember = realMember;

            ClassifyReal(wc, null, out List<Renderer> realShown, out List<Renderer> realHidden);
            run.ContextHidden = 0;
            run.DebrisHidden = 0;
            foreach (Renderer r in realHidden)
            {
                if (IsUnderAny(r.transform, run.JettisonTransforms)) run.DebrisHidden++;
                else run.ContextHidden++;
            }
            var recordRenderers = new List<Renderer>();
            wc.RealRecords = PartParityCaseCapture.Records(partT, realShown, wc.RealLayers, recordRenderers,
                GhostVisualBuilder.GetDepthMaskTransformNames(part.partInfo?.partConfig));
            int rehomed = RehomeJettisonPaths(part, run.JettisonTransforms, wc.RealRecords, recordRenderers);
            if (rehomed > 0)
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: {rehomed.ToString(Ic)} real record path(s) of a reparented shroud read at their prefab path");

            uint subjectPid = part.persistentId;
            var events = new List<PartEvent>(run.StowedSeeds);
            var eventUTs = new List<double>();
            if (!run.DropEvents)
                foreach (PartEvent e in run.Sink.Events) { events.Add(e); eventUTs.Add(e.ut); }

            // The event route: the stowed snapshot after the real part moved on.
            string name = "ParsekPartParity_" + wc.CaseId;
            if (!GhostCoOrbitalRig.TryBuildGhostFromSnapshot(run.StowedSnapshot?.CreateCopy(), NewRecId(), name,
                    events, wc.Real.vesselName, wc.Real.persistentId, run.SeedUT, out GhostCoOrbitalRig.LiveGhost evGhost, out string fail))
            {
                wc.Flags.Add(PartParityCaseCapture.FlagGhostNotBuilt);
                wc.Detail = wc.Detail ?? "event route: " + fail;
            }
            else
            {
                wc.Ghost = evGhost;
                double clip = ClampedClip(evGhost.State, subjectPid);
                List<double> primes = run.FourRows
                    ? PartParityStateWave.EventRoutePrimeUTs(run.SeedUT, eventUTs, clip, run.SunTracker)
                    : new List<double> { run.SeedUT };
                Place(evGhost, partT);
                wc.SeedEvents = events.Count;
                foreach (double ut in primes) wc.SeedApplied = GhostCoOrbitalRig.ApplySeedPrefix(evGhost, ut);
                run.GhostEvent = run.FourRows ? GhostOutcome(run.Option, evGhost.State, subjectPid) : null;
                ConfigNode snapPart = evGhost.Snapshot != null ? evGhost.Snapshot.GetNode("PART") : null;
                PartParityCaseCapture.RecordSkinProof(wc, snapPart, evGhost.Build);
                SplitGhost(evGhost, run.FillerPids, run.EventShown, run.GhostHidden);
                wc.GhostRenderers = new List<Renderer>(run.EventShown);
                wc.GhostRecords = PartParityCaseCapture.Records(partT, run.EventShown, wc.GhostLayers, null);
                wc.GhostRootOffsetM = GhostPartOffset(evGhost.Build.root.transform, subjectPid, partT);
                if (wc.GhostRootOffsetM.HasValue && wc.GhostRootOffsetM.Value > PartParityCaseCapture.GhostRootOffsetToleranceM)
                    wc.Flags.Add(PartParityCaseCapture.FlagGhostRootOffset);
            }
            if (wc.GhostRecords == null) wc.GhostRecords = new List<PartParityRendererRecord>();

            if (run.FourRows)
            {
                BuildStartGhost(wc, run, partT, subjectPid, name);
                BuildStowedGhost(wc, run, partT, subjectPid, name);
                ReadSunTracker(wc, run, part);
            }
            FrameState(wc, run);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: state ghosts event={run.GhostEvent ?? "-"} start={run.GhostStart ?? "-"} " +
                $"stowed={(run.StowedGhost != null ? "built" : "-")} startFirstFrame={(run.StartFirstFrameAtTarget.HasValue ? (run.StartFirstFrameAtTarget.Value ? "at-target" : "not-at-target") : "-")} " +
                $"events={(run.DropEvents ? "dropped" : run.Sink.Events.Count.ToString(Ic))} contextHidden={run.ContextHidden.ToString(Ic)} " +
                $"debrisHidden={run.DebrisHidden.ToString(Ic)} realRenderers={wc.RealRecords.Count.ToString(Ic)} " +
                $"ghostRenderers={wc.GhostRecords.Count.ToString(Ic)}");
        }

        private static void BuildStartGhost(WaveCase wc, PartParityStateRun run, Transform partT, uint subjectPid, string name)
        {
            Vessel v = wc.Real;
            ConfigNode deployed = FlightRecorder.TryBackupSnapshot(v);
            var sets = new PartTrackingSets();
            PartStateSeeder.SeedPartStates(v, sets, FlightRecorder.CacheEngineModules(v),
                FlightRecorder.CacheRcsModules(v), seedColorChangerLights: true, logTag: Tag);
            double t1 = Planetarium.GetUniversalTime();
            List<PartEvent> seeds = PartStateSeeder.EmitSeedEvents(sets, PartNames(v), t1, Tag);
            if (!GhostCoOrbitalRig.TryBuildGhostFromSnapshot(deployed, NewRecId(), name + "_start", seeds,
                    v.vesselName, v.persistentId, t1, out GhostCoOrbitalRig.LiveGhost g, out string fail))
            {
                wc.Flags.Add(PartParityCaseCapture.FlagGhostNotBuilt + ":start");
                wc.Detail = wc.Detail ?? "start route: " + fail;
                return;
            }
            run.StartGhost = g;
            Place(g, partT);
            List<double> primes = PartParityStateWave.StartRoutePrimeUTs(t1, run.SunTracker);
            GhostCoOrbitalRig.ApplySeedPrefix(g, primes[0]);
            run.StartFirstFrameAtTarget = GhostOutcome(run.Option, g.State, subjectPid) == PartParityStateRules.GhostApplied;
            Transform pivot = GhostPivot(g.State, subjectPid);
            if (pivot != null) run.SunStartFirstFrameDeg = PivotAngle(g.State, subjectPid, pivot.localRotation);
            for (int i = 1; i < primes.Count; i++) GhostCoOrbitalRig.ApplySeedPrefix(g, primes[i]);
            run.GhostStart = GhostOutcome(run.Option, g.State, subjectPid);
            SplitGhost(g, run.FillerPids, run.StartShown, run.GhostHidden);
            run.StartRecords = PartParityCaseCapture.Records(partT, run.StartShown, run.StartLayers, null);
            run.StructureStart = PartParityStructure.Diff(wc.RealRecords, run.StartRecords);
        }

        private static void BuildStowedGhost(WaveCase wc, PartParityStateRun run, Transform partT, uint subjectPid, string name)
        {
            if (!GhostCoOrbitalRig.TryBuildGhostFromSnapshot(run.StowedSnapshot?.CreateCopy(), NewRecId(), name + "_stowed",
                    new List<PartEvent>(run.StowedSeeds), wc.Real.vesselName, wc.Real.persistentId, run.SeedUT,
                    out GhostCoOrbitalRig.LiveGhost g, out string fail))
            {
                wc.Flags.Add(PartParityCaseCapture.FlagGhostNotBuilt + ":stowed");
                wc.Detail = wc.Detail ?? "stowed reference: " + fail;
                return;
            }
            run.StowedGhost = g;
            Place(g, partT);
            GhostCoOrbitalRig.ApplySeedPrefix(g, run.SeedUT);
            SplitGhost(g, run.FillerPids, run.StowedShown, run.GhostHidden);
            run.StowedRecords = PartParityCaseCapture.Records(partT, run.StowedShown, run.StowedLayers, null);
        }

        /// <summary>The sun tracker's angles from its neutral pose (the stowed reference
        /// ghost's pivot neutral rotation), degrees: the real pivot, the event route's pivot
        /// after its steps (the start route's first frame is read in its build).</summary>
        private static void ReadSunTracker(WaveCase wc, PartParityStateRun run, Part part)
        {
            if (!run.SunTracker || run.StowedGhost == null) return;
            uint pid = part.persistentId;
            SunTrackingGhostInfo neutral = SunInfo(run.StowedGhost.State, pid);
            if (neutral == null || neutral.pivotTransform == null) return;
            Transform realPivot = run.RealPivot ?? part.FindModelTransform(neutral.pivotTransform.name);
            if (realPivot != null) run.SunTargetDeg = Quaternion.Angle(neutral.neutralRotation, realPivot.localRotation);
            Transform evPivot = wc.Ghost != null ? GhostPivot(wc.Ghost.State, pid) : null;
            if (evPivot != null) run.SunEventDeg = Quaternion.Angle(neutral.neutralRotation, evPivot.localRotation);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: sun tracker targetDeg={Fmt(run.SunTargetDeg)} eventDeg={Fmt(run.SunEventDeg)} " +
                $"startFirstFrameDeg={Fmt(run.SunStartFirstFrameDeg)}");
        }

        private static string Fmt(double? d) => d.HasValue ? d.Value.ToString("0.0", Ic) : "-";

        private static double? PivotAngle(GhostPlaybackState state, uint pid, Quaternion local)
        {
            SunTrackingGhostInfo s = SunInfo(state, pid);
            return s != null ? (double?)Quaternion.Angle(s.neutralRotation, local) : null;
        }

        private static SunTrackingGhostInfo SunInfo(GhostPlaybackState state, uint pid)
        {
            List<SunTrackingGhostInfo> list = state?.synthesizedMotionInfos?.sunTrackers;
            if (list == null) return null;
            foreach (var s in list) if (s != null && s.partPersistentId == pid) return s;
            return null;
        }

        private static Transform GhostPivot(GhostPlaybackState state, uint pid) => SunInfo(state, pid)?.pivotTransform;

        private static string NewRecId() => "pp-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static void Place(GhostCoOrbitalRig.LiveGhost g, Transform partT)
        {
            GameObject root = g.Build.root;
            root.transform.SetParent(partT, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            root.SetActive(true);
            GhostCoOrbitalRig.NeutralisePhysics(root, out int _, out int _);
        }

        /// <summary>The ghost's deployable clip for the subject part, clamped the way playback
        /// clamps it (the 3 s default when the ghost has no deployable info).</summary>
        private static double ClampedClip(GhostPlaybackState state, uint pid)
        {
            if (state?.deployableInfos != null && state.deployableInfos.TryGetValue(pid, out DeployableGhostInfo d) && d != null)
                return GhostPlaybackLogic.ClampDeployableClipSeconds(d.clipLengthSeconds);
            return GhostPlaybackLogic.ClampDeployableClipSeconds(0f);
        }

        /// <summary>The ghost's own playback state for the case's family, read-only (D.5).</summary>
        internal static string GhostOutcome(PartParityStateOption opt, GhostPlaybackState state, uint pid)
            => PartParityStateRules.GhostStateReached(opt, ReadGhostInput(opt, state, pid));

        internal static PartParityGhostStateInput ReadGhostInput(PartParityStateOption opt, GhostPlaybackState state, uint pid)
        {
            var g = new PartParityGhostStateInput();
            if (state == null || opt == null) return g;
            switch (opt.Family)
            {
                case PartParityStateRules.FamilyLight:
                case PartParityStateRules.FamilyCabinLight:
                    bool has = opt.Family == PartParityStateRules.FamilyLight
                        ? state.lightInfos != null && state.lightInfos.ContainsKey(pid)
                        : HasCabinLight(state, pid);
                    g.HasInfo = has;
                    if (state.lightPlaybackStates != null && state.lightPlaybackStates.TryGetValue(pid, out LightPlaybackState lp) && lp != null)
                        g.On = lp.isOn;
                    else
                        g.On = false;
                    break;
                case PartParityStateRules.FamilyParachute:
                    if (state.parachuteInfos != null && state.parachuteInfos.TryGetValue(pid, out ParachuteGhostInfo pc) && pc != null)
                    {
                        g.HasInfo = true;
                        g.PoseSampled = pc.canopyTransform != null && (opt.State != PartParityStateRules.StateSemi || pc.semiDeployedSampled);
                        g.ParachuteApplied = CanopyWord(pc);
                    }
                    break;
                case PartParityStateRules.FamilyJettison:
                    if (state.jettisonInfos != null && state.jettisonInfos.TryGetValue(pid, out JettisonGhostInfo j) && j?.jettisonTransforms != null)
                    {
                        g.HasInfo = true;
                        foreach (Transform t in j.jettisonTransforms)
                        {
                            if (t == null) continue;
                            g.JettisonTransforms++;
                            if (!t.gameObject.activeSelf) g.JettisonHidden++;
                        }
                        g.PoseSampled = g.JettisonTransforms > 0;
                    }
                    break;
                case PartParityStateRules.FamilyFairing:
                    if (state.fairingInfos != null && state.fairingInfos.TryGetValue(pid, out FairingGhostInfo fi) && fi != null)
                    {
                        // The shell: gone (built from a deployed snapshot) or hidden by the
                        // jettison applier reads as deployed.
                        g.HasInfo = true;
                        g.On = fi.fairingMeshObject == null || !fi.fairingMeshObject.activeSelf;
                    }
                    break;
                default:
                    if (state.deployableInfos != null && state.deployableInfos.TryGetValue(pid, out DeployableGhostInfo d) && d != null)
                    {
                        g.HasInfo = true;
                        g.PoseSampled = GhostPlaybackLogic.HasDeployablePose(d);
                        g.DeployFraction = d.deployFraction;
                        g.CurrentDeployed = d.currentDeployed;
                        g.TransitionActive = d.transitionActive;
                    }
                    else if (GhostPlaybackLogic.HasDeployDrivenLoop(state, pid))
                    {
                        // An animation group with no deploy clip (the orbital scanner): its
                        // deployed state is its running loop.
                        bool running = DeployDrivenLoopRunning(state, pid);
                        g.HasInfo = true;
                        g.PoseSampled = true;
                        g.DeployFraction = running ? 1f : 0f;
                        g.CurrentDeployed = running;
                        g.TransitionActive = false;
                    }
                    break;
            }
            return g;
        }

        private static bool DeployDrivenLoopRunning(GhostPlaybackState state, uint pid)
        {
            List<ConverterLoopGhostInfo> loops = state?.synthesizedMotionInfos?.converterLoops;
            if (loops == null) return false;
            foreach (var l in loops)
                if (l != null && l.followsDeploy && l.partPersistentId == pid && l.active) return true;
            return false;
        }

        private static bool HasCabinLight(GhostPlaybackState state, uint pid)
        {
            if (state.colorChangerInfos == null || !state.colorChangerInfos.TryGetValue(pid, out List<ColorChangerGhostInfo> list) || list == null)
                return false;
            foreach (var c in list) if (c != null && c.isCabinLight) return true;
            return false;
        }

        private static string CanopyWord(ParachuteGhostInfo pc)
        {
            if (pc.canopyTransform == null) return null;
            Vector3 s = pc.canopyTransform.localScale;
            return ClassifyCanopyStage(
                (s - pc.deployedCanopyScale).sqrMagnitude < 1e-6f,
                CanopyBonesAt(pc, CanopyPoseStage.Deployed),
                pc.semiDeployedSampled,
                (s - pc.semiDeployedCanopyScale).sqrMagnitude < 1e-6f,
                CanopyBonesAt(pc, CanopyPoseStage.SemiDeployed));
        }

        /// <summary>
        /// The canopy stage a ghost canopy shows, from whether its root scale and its animated
        /// bones (a rigged canopy such as ReStock's, whose root keeps one scale while the bones
        /// open it) sit at each stage's sampled pose. A stage matches only when both do; when
        /// both stages match (no bones and one scale), the deployed reading wins.
        /// </summary>
        internal static string ClassifyCanopyStage(bool fullScale, bool fullBones,
            bool semiSampled, bool semiScale, bool semiBones)
        {
            bool full = fullScale && fullBones;
            bool semi = semiSampled && semiScale && semiBones;
            if (full) return PartParityStateRules.StateFull;
            if (semi) return PartParityStateRules.StateSemi;
            return PartParityStateRules.StateStowed;
        }

        /// <summary>True when every animated canopy bone sits at its pose for
        /// <paramref name="stage"/> (none to check reads true).</summary>
        private static bool CanopyBonesAt(ParachuteGhostInfo pc, CanopyPoseStage stage)
        {
            if (pc.canopyBones == null) return true;
            foreach (CanopyBonePose b in pc.canopyBones)
            {
                if (b?.bone == null) continue;
                if (!GhostPlaybackLogic.TrySelectCanopyBonePose(b, stage, out Vector3 pos, out Quaternion rot, out Vector3 scale))
                    continue;
                if ((b.bone.localPosition - pos).sqrMagnitude > 1e-6f) return false;
                if ((b.bone.localScale - scale).sqrMagnitude > 1e-6f) return false;
                if (Mathf.Abs(Quaternion.Dot(b.bone.localRotation, rot)) < 0.99999f) return false;
            }
            return true;
        }

        /// <summary>A ghost's renderers, split: the subject's shown, a context filler's
        /// (<c>ghost_part_&lt;fillerPid&gt;</c>) hidden.</summary>
        private static void SplitGhost(GhostCoOrbitalRig.LiveGhost g, List<uint> fillerPids, List<Renderer> shown, List<Renderer> hidden)
        {
            var fillerRoots = new List<Transform>();
            Transform root = g.Build.root.transform;
            foreach (uint pid in fillerPids)
            {
                Transform t = FindChildNamed(root, PartParityStructure.GhostPartSegmentPrefix + pid.ToString(Ic));
                if (t != null) fillerRoots.Add(t);
            }
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (IsUnderAny(r.transform, fillerRoots)) hidden.Add(r);
                else shown.Add(r);
            }
        }

        private static Transform FindChildNamed(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static bool IsUnderAny(Transform t, List<Transform> roots)
        {
            foreach (Transform r in roots)
                if (r != null && t.IsChildOf(r)) return true;
            return false;
        }

        private static double? GhostPartOffset(Transform ghostRoot, uint subjectPid, Transform partT)
        {
            Transform t = FindChildNamed(ghostRoot, PartParityStructure.GhostPartSegmentPrefix + subjectPid.ToString(Ic));
            return t != null ? (double?)(t.position - partT.position).magnitude : null;
        }

        /// <summary>
        /// The real side's renderers for a state case: the subject part's own (a context
        /// filler's left out), plus its jettison transforms wherever stock parented them (a
        /// fairing shroud moves to the part below it, a thrown one is free). Once jettisoned,
        /// those are hidden (thrown debris); before, they are part of the subject. Everything
        /// left out is returned in <paramref name="hidden"/>, so the capture forces it off.
        /// </summary>
        internal static void ClassifyReal(WaveCase wc, List<Transform> excludeRoots, out List<Renderer> shown, out List<Renderer> hidden)
        {
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            bool jettisoned = PartParityStateRules.ThrowsJettisonTransforms(run.Option);
            shown = new List<Renderer>();
            hidden = new List<Renderer>();
            var seen = new HashSet<Renderer>();
            var candidates = new List<Renderer>(part.GetComponentsInChildren<Renderer>(true));
            foreach (Part p in wc.Real.parts)
                if (p != null && p != part) candidates.AddRange(p.GetComponentsInChildren<Renderer>(true));
            foreach (Transform jt in run.JettisonTransforms)
                if (jt != null) candidates.AddRange(jt.GetComponentsInChildren<Renderer>(true));
            foreach (Renderer r in candidates)
            {
                if (r == null || !seen.Add(r)) continue;
                if (excludeRoots != null && IsUnderAny(r.transform, excludeRoots)) continue;
                bool underJettison = IsUnderAny(r.transform, run.JettisonTransforms);
                Part owner = r.GetComponentInParent<Part>();
                bool keep = underJettison ? !jettisoned : owner == part;
                if (keep) shown.Add(r); else hidden.Add(r);
            }
        }

        /// <summary>
        /// Stock parents a fairing-type shroud to the part on its bottom node
        /// (<c>ModuleJettison.SetupJettisonState</c>), so the real record path of a stacked
        /// shroud runs through the filler (<c>fuelTankSmallFlat/fairing</c>) while the ghost keeps
        /// it at its prefab place (<c>model/liquidEngine/fairing</c>): every record under a
        /// jettison transform that left the subject is given the transform's prefab path plus
        /// its own remainder, so the structural diff pairs it. Returns how many were rewritten.
        /// </summary>
        private static int RehomeJettisonPaths(Part part, List<Transform> jettison,
            List<PartParityRendererRecord> records, List<Renderer> renderers)
        {
            int n = 0;
            Part prefab = part.partInfo?.partPrefab;
            if (prefab == null || records == null || renderers == null) return 0;
            foreach (Transform jt in jettison)
            {
                if (jt == null || jt.GetComponentInParent<Part>() == part) continue;
                Transform home = prefab.FindModelTransform(jt.name);
                if (home == null) continue;
                string prefix = PartParityCaseCapture.RelativePath(prefab.transform, home);
                for (int i = 0; i < records.Count && i < renderers.Count; i++)
                {
                    Renderer r = renderers[i];
                    if (r == null || !r.transform.IsChildOf(jt)) continue;
                    string rest = PartParityCaseCapture.RelativePath(jt, r.transform);
                    records[i].Path = PartParityStructure.NormalizePath(
                        string.IsNullOrEmpty(rest) ? prefix : prefix + "/" + rest);
                    n++;
                }
            }
            return n;
        }

        /// <summary>The framing of a state case: the stowed rule over real and the event-route
        /// ghost, the shared box widened to every built ghost's rendered bounds, the culling
        /// mask over every side's layers.</summary>
        private static void FrameState(WaveCase wc, PartParityStateRun run)
        {
            PartParityCaseCapture.Frame(wc);
            bool grown = false;
            foreach (var recs in new[] { run.StartRecords, run.StowedRecords })
            {
                if (recs == null) continue;
                foreach (var r in recs)
                    if (r != null && r.Rendered) { wc.Shared.Encapsulate(PartParityFramingRule.BoundsOf(r)); grown = true; }
            }
            if (grown) wc.OrthoSize = PartParityCaptureMath.OrthoSize(wc.Shared.Extents());
            int mask = wc.CullingMask;
            foreach (int l in run.StartLayers) mask |= 1 << l;
            foreach (int l in run.StowedLayers) mask |= 1 << l;
            wc.CullingMask = mask;
        }

        // =====================================================================================
        // one view (D.5: N sides)
        // =====================================================================================

        /// <summary>One view of a state case: four sides (real, event route, start route,
        /// stowed reference) or two for a context case, every other renderer forced off, the
        /// context fillers and the thrown debris forced off on every side.</summary>
        internal static void CaptureView(StepContext ctx, WaveCase wc, string view)
        {
            PartParityStateRun run = wc.State;
            Part part = wc.Real.rootPart;
            var ghostRoots = new List<Transform>();
            foreach (var g in new[] { wc.Ghost, run.StartGhost, run.StowedGhost })
                if (g?.Build?.root != null) ghostRoots.Add(g.Build.root.transform);
            ClassifyReal(wc, ghostRoots, out List<Renderer> realShown, out List<Renderer> realHidden);
            var all = new List<Renderer>(realShown);
            all.AddRange(realHidden);
            all.AddRange(run.EventShown);
            all.AddRange(run.StartShown);
            all.AddRange(run.StowedShown);
            all.AddRange(run.GhostHidden);
            var sides = new List<IList<Renderer>> { realShown, run.EventShown };
            if (run.FourRows) { sides.Add(run.StartShown); sides.Add(run.StowedShown); }

            PartParityViewShot shot = ctx.Capture.CaptureSides(part.transform, wc.Shared, wc.OrthoSize, view,
                all, sides, wc.CullingMask);
            if (shot.Error != null)
            {
                wc.Flags.Add(PartParityCaseCapture.FlagCaptureErrorPrefix + view);
                wc.Detail = wc.Detail ?? view + ": " + shot.Error;
                ParsekLog.Warn(Tag, $"case {wc.CaseId} view {view}: capture error {shot.Error}");
                return;
            }
            int tile = ctx.Capture.Tile;
            PartParityViewMeasures m = PartParityMetrics.Measure(shot.Real, shot.Ghost, tile, tile, shot.ProjectedBoxAreaPx);
            bool border = m.TouchesBorderReal || m.TouchesBorderGhost;
            var record = new PartParityViewRecord
            {
                View = view, Measures = m, RealMatte = shot.RealMatte, GhostMatte = shot.GhostMatte,
            };
            if (run.FourRows)
            {
                PartParityViewMeasures start = PartParityMetrics.Measure(shot.Sides[0], shot.Sides[2], tile, tile, shot.ProjectedBoxAreaPx);
                PartParityViewMeasures stowed = PartParityMetrics.Measure(shot.Sides[0], shot.Sides[3], tile, tile, shot.ProjectedBoxAreaPx);
                record.Start = new PartParityViewRecord
                {
                    Measures = start, RealMatte = shot.SideMattes[0], GhostMatte = shot.SideMattes[2],
                };
                record.StowedRef = stowed;
                border |= start.TouchesBorderGhost || stowed.TouchesBorderGhost;
                wc.GhostStartTiles.Add(shot.Sides[2]);
                wc.GhostStowedTiles.Add(shot.Sides[3]);
            }
            if (border && !wc.Flags.Contains(PartParityCaseCapture.FlagClippedAtBorder))
                wc.Flags.Add(PartParityCaseCapture.FlagClippedAtBorder);
            wc.CapturedViews.Add(view);
            wc.RealTiles.Add(shot.Real);
            wc.GhostTiles.Add(shot.Ghost);
            wc.ViewRecords.Add(record);
        }

        // =====================================================================================
        // the sidecar half (D.2)
        // =====================================================================================

        /// <summary>The state proof, the start-route structure, the stowed case id and the
        /// state flags of a state or context case.</summary>
        internal static void FillSidecar(WaveCase wc, PartParityCaseSidecar s)
        {
            PartParityStateRun run = wc.State;
            PartParityStateOption o = run.Option;
            s.StateProof = new PartParityStateProof
            {
                Family = o.Family, Module = o.Module, ModuleIndex = o.ModuleIndex,
                State = wc.Key?.State ?? o.State,
                Action = o.Action, EventVisible = run.EventVisible,
                ActionResult = run.ActionResult,
                Reached = run.Reached,
                WaitS = run.WaitSeconds,
                EndpointField = run.EndpointField, EndpointValue = run.EndpointValue,
                Events = run.Pressed ? PartParityStateWave.ProofEvents(run.Sink.Events) : null,
                GhostEvent = run.GhostEvent, GhostStart = run.GhostStart,
                StartFirstFrameAtTarget = run.StartFirstFrameAtTarget,
                StartFirstFrameTrackerDeg = run.SunStartFirstFrameDeg,
                SunTargetDeg = run.SunTargetDeg, SunEventDeg = run.SunEventDeg,
                SunStartFirstFrameDeg = run.SunStartFirstFrameDeg,
                CanopyTiltDeg = run.CanopyTiltDeg, CanopyWobbleDeg = run.CanopyWobbleDeg,
                CanopyTransform = run.CanopyTransform,
                Nodes = run.Nodes.Count > 0 ? new List<PartParityContextNode>(run.Nodes) : null,
            };
            s.StructureStart = run.StructureStart;
            s.StowedCaseId = PartParityStateWave.StowedCaseId(wc.Key);
            double? sunDelta = run.SunTargetDeg.HasValue && run.SunStartFirstFrameDeg.HasValue
                ? (double?)Math.Abs(run.SunTargetDeg.Value - run.SunStartFirstFrameDeg.Value) : null;
            foreach (string f in PartParityStateWave.StateFlags(new PartParityStateWave.FlagInputs
            {
                Pressed = run.Pressed, Reached = run.Reached, EventsRecorded = run.Sink.Events.Count,
                GhostEvent = run.GhostEvent, GhostStart = run.GhostStart,
                StartFirstFrameAtTarget = run.StartFirstFrameAtTarget, SunFirstFrameDeltaDeg = sunDelta,
                StillTracking = run.StillTracking, ContextHidden = run.ContextHidden, DebrisHidden = run.DebrisHidden,
                CanopyWobbleTag = run.CanopyWobbleTag,
            }))
                if (!s.Flags.Contains(f)) s.Flags.Add(f);
            ParsekLog.Verbose(Tag, PartParityStateWave.DescribeState(wc.CaseId, s.StateProof, s.Setup?.Status));
        }

        // =====================================================================================
        // teardown (D.3 step 6)
        // =====================================================================================

        /// <summary>
        /// Destroys what a state case owns beyond a stowed case: its start-route and stowed
        /// reference ghosts, and every jettison object the press threw off that is no longer
        /// part of a vessel (a free physical object destroys itself only beyond 1000 m, so it
        /// would pile up at the slots). Idempotent; runs in the wave teardown and the step's
        /// cleanup alike.
        /// </summary>
        internal static void DestroyStateParts(WaveCase wc)
        {
            PartParityStateRun run = wc?.State;
            if (run == null) return;
            try { GhostCoOrbitalRig.DestroyLiveGhost(run.StartGhost); }
            catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: start ghost destroy threw {ex.GetType().Name}: {ex.Message}"); }
            run.StartGhost = null;
            try { GhostCoOrbitalRig.DestroyLiveGhost(run.StowedGhost); }
            catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: stowed ghost destroy threw {ex.GetType().Name}: {ex.Message}"); }
            run.StowedGhost = null;
            KillSeparatedVessel(wc, run, "teardown");
            int destroyed = 0, kept = 0;
            foreach (Transform t in run.JettisonTransforms)
            {
                if (t == null) continue;
                if (t.GetComponentInParent<Part>() != null) { kept++; continue; }
                try { UnityEngine.Object.Destroy(t.gameObject); destroyed++; }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: debris destroy threw {ex.GetType().Name}: {ex.Message}"); }
            }
            run.JettisonTransforms.Clear();
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: debris destroyed n={destroyed.ToString(Ic)} " +
                $"stillOnAPart={kept.ToString(Ic)} (go with their vessel)");
        }
    }
}
