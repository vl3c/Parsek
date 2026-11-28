using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Parsek.InGameTests;
using UnityEngine;
using WaveCase = Parsek.TestCommands.PartParitySweepRunner.WaveCase;
using StepContext = Parsek.TestCommands.PartParitySweepRunner.StepContext;

namespace Parsek.TestCommands
{
    /// <summary>One trial of a burst case (design G.2.1): a real slot over the showcase band and a
    /// ghost slot <see cref="PartParityFxRules.BurstGhostSeparationM"/> east of it, the trial
    /// vessel and its hold, the ghost built from it, both sources, what each side's trigger
    /// queued with FXMonger and the effect objects tagged to each side.</summary>
    internal sealed class PartParityBurstTrial
    {
        internal int Index;
        internal PartParityHeldSlot RealSlot;
        internal PartParityHeldSlot GhostSlot;
        internal uint Pid;
        internal Vessel Real;
        internal PartParityFxHold Hold;
        internal readonly List<uint> PartPids = new List<uint>();
        internal GhostCoOrbitalRig.LiveGhost Ghost;
        internal Part SourcePart;
        internal double SourceExtentM;

        /// <summary>The sources at the trigger, body-fixed (latitude, longitude, altitude).</summary>
        internal double[] RealSource;
        internal double[] GhostSource;
        internal bool RealTriggered;
        internal bool GhostTriggered;
        internal readonly List<uint> DiedPids = new List<uint>();
        internal readonly List<uint> BrokenChildPids = new List<uint>();
        internal readonly List<PartParityBurstBlast> RealBlasts = new List<PartParityBurstBlast>();
        internal readonly List<PartParityBurstBlast> GhostBlasts = new List<PartParityBurstBlast>();
        internal readonly HashSet<GameObject> RealObjects = new HashSet<GameObject>();
        internal readonly HashSet<GameObject> GhostObjects = new HashSet<GameObject>();
        internal readonly List<Renderer> RealEmitters = new List<Renderer>();
    }

    /// <summary>The live run of one burst or dust case (design Appendix G, piece X4).</summary>
    internal sealed class PartParityBurstRun
    {
        internal PartParityFxOption Option;
        internal string Control;
        internal bool IsDust;
        internal int TilePx;

        /// <summary>The burst case's own capture at <see cref="TilePx"/> (the run's tile capped at
        /// <see cref="PartParityBurstPlan.BurstMaxTilePx"/>); disposed in the release.</summary>
        internal PartParityCapture Capture;
        internal readonly List<PartParityBurstTrial> Trials = new List<PartParityBurstTrial>();

        // the trigger
        internal int TriggerFrame = -1;
        internal float TriggerTime;
        internal double TriggerUT = double.NaN;
        internal string GhostEvent;
        internal bool GhostPending;
        internal int RealTagged;
        internal int GhostTagged;
        internal bool ArmedRecord;
        internal int RecordBaseline;
        internal readonly HashSet<GameObject> SeenFxObjects = new HashSet<GameObject>();
        internal readonly HashSet<GameObject> SeenRecordObjects = new HashSet<GameObject>();
        internal int StrayObjects;

        // the capture
        internal int Bins;
        internal readonly Dictionary<string, PartParityBurstAccumulator> Accumulators =
            new Dictionary<string, PartParityBurstAccumulator>(StringComparer.Ordinal);
        internal readonly List<PartParityBurstViewResult> Results = new List<PartParityBurstViewResult>();
        internal int Frames;
        internal PartParityBurstScheduleCompare ScheduleCompare;
        internal List<PartParityBurstBlast> ScheduleReal = new List<PartParityBurstBlast>();
        internal List<PartParityBurstBlast> ScheduleGhost = new List<PartParityBurstBlast>();
        internal readonly SortedSet<string> RealNames = new SortedSet<string>(StringComparer.Ordinal);
        internal readonly SortedSet<string> GhostNames = new SortedSet<string>(StringComparer.Ordinal);
        internal bool StructureMeasured;

        /// <summary>The dust half (null for a burst case).</summary>
        internal PartParityDustRun Dust;

        internal static string AccKey(string view, string layer, bool real)
            => view + "|" + layer + "|" + (real ? PartParityFxRules.SideReal : PartParityFxRules.SideGhost);
    }

    /// <summary>
    /// The live burst and dust driver of the part parity sweep (design-part-ghost-parity.md
    /// Appendix G, piece X4). A burst wave is one case: four trials over the showcase band, each a
    /// real slot and a ghost slot 20 m east, triggered in one frame (the ghost through the
    /// production seam the family replays with: a part event, the terminal explosion, the
    /// stand-in fireball or puff), then one trial rendered per frame, round robin, into the
    /// time-binned accumulators of every burst view and layer; FXMonger's queue read after each
    /// trigger call is the schedule. A dust wave (<c>PartParityBurstDriver.Dust.cs</c>) holds up
    /// to four engines over the ground. Every rule is pure (<see cref="PartParityBurstPlan"/>,
    /// <see cref="PartParityFxRules"/>, <see cref="PartParityBurstMetrics"/>); this file reads and
    /// drives the live game.
    /// </summary>
    internal static partial class PartParityBurstDriver
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static string Tag => PartParitySweepRunner.Tag;

        // =====================================================================================
        // enumeration and case construction
        // =====================================================================================

        /// <summary>Every burst and dust case the install offers with its exclusions (design G.2).</summary>
        internal static PartParityFxEnumeration Enumerate(StepContext ctx)
        {
            var install = new List<PartParityBurstPart>();
            foreach (var e in ctx.Entries)
                if (e?.Info != null) install.Add(BurstPartOf(e));
            return PartParityFxRules.EnumerateBurstDust(install);
        }

        private static PartParityBurstPart BurstPartOf(PartParityPartEntry e)
        {
            var p = new PartParityBurstPart
            {
                Part = PartParityStateDriver.BuildStatePart(e.Info),
                BaseKey = PartParityCaseRules.DefaultCaseKey(e),
            };
            var values = new List<KeyValuePair<string, string>>();
            ConfigNode cfg = e.Info.partConfig;
            if (cfg != null)
                foreach (ConfigNode.Value v in cfg.values)
                    if (v != null) values.Add(new KeyValuePair<string, string>(v.name, v.value));
            PartParityBurstPlan.ReadPartConfig(values, p);
            return p;
        }

        /// <summary>The live run of one burst or dust case or control.</summary>
        internal static void Attach(WaveCase wc, PartParityCaseKey key, string control)
        {
            PartParityFxOption o = key.FxOption;
            wc.Burst = new PartParityBurstRun
            {
                Option = o, Control = control, IsDust = PartParityFxRules.IsDustFamily(o.Family),
            };
            if (wc.Burst.IsDust) wc.Burst.Dust = new PartParityDustRun();
        }

        /// <summary>A burst or dust control (design G.4): its subject's explode case, its dust-low
        /// slot, or the above-reach slot.</summary>
        internal static WaveCase BuildControlCase(StepContext ctx, string id, WaveCase wc, bool dustRun)
        {
            PartParityFxSubjectChoice choice = PartParityFxDriver.ChooseSubject(ctx, id, !dustRun);
            wc.Key = new PartParityCaseKey { Part = choice.Subject ?? "<none>" };
            if (choice.Subject == null || !ctx.EntriesByName.TryGetValue(choice.Subject, out PartParityPartEntry e))
            {
                wc.Detail = "control subject: " + (choice.Detail ?? "not loaded");
                return wc;
            }
            wc.Entry = e;
            wc.RealVariant = e.BaseVariant;
            PartParityCaseKey key = PartParityCaseRules.DefaultCaseKey(e);
            PartParityFxOption o = null;
            if (!dustRun)
            {
                PartParityBurstPart bp = BurstPartOf(e);
                o = new PartParityFxOption
                {
                    Family = PartParityFxRules.FamilyExplode, State = PartParityFxRules.StateExplode,
                    Context = PartParityFxRules.ContextBurst, ModuleIndex = -1, RecorderKeyIndex = -1,
                    ExplosionPotential = bp.ExplosionPotential,
                    BurstWindowSeconds = PartParityFxRules.BurstWindowFor(PartParityFxRules.FamilyExplode),
                };
            }
            else
            {
                foreach (PartParityCaseKey k in PartParityFxRules.EnumerateBurstDust(new[] { BurstPartOf(e) }).Cases)
                    if (k.FxOption != null && k.FxOption.Family == PartParityFxRules.FamilyDust
                        && k.FxOption.State == PartParityFxRules.StateDustLow) { o = k.FxOption; break; }
                if (o != null && id == TestCommandPartParitySweep.ControlDustAboveReach) o = PartParityBurstPlan.AboveReachOption(o);
            }
            if (o == null)
            {
                wc.Detail = "control subject has no " + (dustRun ? "dust" : "burst") + " case: " + choice.Subject;
                return wc;
            }
            key.FxOption = o;
            key.State = o.State;
            key.Context = o.Context;
            wc.Key = key;
            Attach(wc, key, id);
            ParsekLog.Info(Tag, $"control {id}: built on {choice.Subject} family={o.Family} state={o.State} " +
                $"potential={o.ExplosionPotential.ToString("0.###", Ic)}");
            return wc;
        }

        // =====================================================================================
        // the burst wave (G.2.1)
        // =====================================================================================

        /// <summary>One burst wave (one case): spawn and hold the trials, build the ghosts, trigger
        /// every trial in one frame, capture the window, measure, write, tear down.</summary>
        internal static IEnumerator RunWave(StepContext ctx, List<WaveCase> wave)
        {
            ctx.Live = wave;
            bool solo = wave.Count == 1 && wave[0].Solo;
            PartParitySweepRunner.WriteInflight(ctx, wave.ConvertAll(c => c.CaseId), solo);
            foreach (var wc in wave)
            {
                if (ctx.HostDamage != null) break;
                PartParitySweepRunner.BeginCaseLog(ctx, wc);
                IEnumerator one = RunBurstCase(ctx, wc);
                try { while (one.MoveNext()) yield return one.Current; }
                finally
                {
                    PartParitySweepRunner.DisposeNested(one);
                    PartParitySweepRunner.EndCaseLog(ctx);
                }
                PartParitySweepRunner.Guard(ctx, wc, "write", () => PartParitySweepRunner.FinishCase(ctx, wc));
                PartParitySweepRunner.DeleteInflight(ctx);
                PartParitySweepRunner.CheckHost(ctx, "after case " + wc.CaseId);
            }
            if (ctx.HostDamage != null) PartParitySweepRunner.DeleteInflight(ctx);
            IEnumerator gone = WaitTrialVesselsGone(wave);
            try { while (gone.MoveNext()) yield return gone.Current; }
            finally { PartParitySweepRunner.DisposeNested(gone); }
            IEnumerator down = PartParitySweepRunner.TearDownWave(ctx, wave);
            try { while (down.MoveNext()) yield return down.Current; }
            finally { PartParitySweepRunner.DisposeNested(down); }
            ctx.Live = null;
        }

        private static IEnumerator RunBurstCase(StepContext ctx, WaveCase wc)
        {
            PartParityBurstRun run = wc.Burst;
            if (run == null || wc.Status != null)
            {
                if (wc.Status == null)
                {
                    wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = wc.Detail ?? "no burst run";
                }
                yield break;
            }
            run.TilePx = PartParityBurstPlan.BurstTilePx(ctx.Args.Tile);
            run.Bins = PartParityFxRules.BurstBinCount(PartParityFxRules.BurstWindowFor(run.Option.Family));
            run.Capture = PartParityCapture.Create(run.TilePx);
            PartParitySweepRunner.Guard(ctx, wc, "spawn", () => SpawnTrials(ctx, wc));
            if (wc.Status != null) yield break;

            // load, unpack, hold
            float deadline = Time.realtimeSinceStartup + (float)PartParitySweepPlan.SpawnTimeoutSeconds;
            while (true)
            {
                if (PartParitySweepRunner.CheckHost(ctx, "burst spawn wait")) yield break;
                bool pending = false;
                foreach (PartParityBurstTrial t in run.Trials)
                {
                    if (t.Pid == 0 || t.Hold != null) continue;
                    Vessel v = FlightRecorder.FindVesselByPid(t.Pid);
                    if (v != null && v.loaded && !v.packed && v.rootPart != null && v.parts != null && v.parts.Count > 0)
                    {
                        t.Real = v;
                        t.Hold = PartParityFxHold.AttachAtSlot(v, ctx.Host, t.RealSlot);
                        foreach (Part p in v.parts) if (p != null) t.PartPids.Add(p.persistentId);
                        continue;
                    }
                    pending = true;
                }
                if (!pending) break;
                if (Time.realtimeSinceStartup > deadline)
                {
                    wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = "a trial vessel never loaded+unpacked in " + PartParitySweepPlan.SpawnTimeoutSeconds.ToString("0", Ic) + " s";
                    yield break;
                }
                yield return null;
            }
            if (run.Trials.Count > 0 && run.Trials[0].Real != null) wc.Real = run.Trials[0].Real;
            float settleUntil = Time.realtimeSinceStartup + (float)PartParityBurstPlan.BurstSettleSeconds;
            while (Time.realtimeSinceStartup < settleUntil)
            {
                if (PartParitySweepRunner.CheckHost(ctx, "burst settle")) yield break;
                foreach (PartParityBurstTrial t in run.Trials)
                    if (t.Pid != 0 && (t.Real == null || t.Real.state == Vessel.State.DEAD || t.Real.rootPart == null))
                    {
                        wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                        wc.Detail = "trial " + t.Index.ToString(Ic) + " vessel lost while settling";
                        yield break;
                    }
                yield return null;
            }

            PartParitySweepRunner.Guard(ctx, wc, "prepare", () => PrepareTrials(wc));
            if (wc.Status != null) yield break;

            // the trigger frame (after Update, before LateUpdate: FXMonger reads its queue there)
            yield return null;
            PartParitySweepRunner.Guard(ctx, wc, "trigger", () => TriggerAll(wc));
            if (wc.Status != null) yield break;

            // the window
            double window = PartParityFxRules.BurstWindowFor(run.Option.Family);
            double delay = PartParityBurstPlan.GhostTriggerDelaySeconds(run.Control);
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (ctx.HostDamage != null) yield break;
                double t = Time.time - run.TriggerTime;
                if (run.GhostPending && t >= delay)
                    PartParitySweepRunner.Guard(ctx, wc, "trigger", () => TriggerGhosts(wc, Time.frameCount - run.TriggerFrame, t));
                int bin = PartParityFxRules.BurstBinIndex(t, window);
                if (bin < 0) break;
                int frame = Time.frameCount - run.TriggerFrame;
                PartParitySweepRunner.Guard(ctx, wc, "capture", () => CaptureFrame(ctx, wc, frame, bin));
                if (wc.Status != null) yield break;
            }
            PartParitySweepRunner.Guard(ctx, wc, "measure", () => MeasureBurst(wc));
            ParsekLog.Info(Tag, $"burst case {wc.CaseId}: captured frames={run.Frames.ToString(Ic)} bins={run.Bins.ToString(Ic)} " +
                $"realTagged={run.RealTagged.ToString(Ic)} ghostTagged={run.GhostTagged.ToString(Ic)} stray={run.StrayObjects.ToString(Ic)} " +
                $"realBlasts={run.ScheduleReal.Count.ToString(Ic)} ghostBlasts={run.ScheduleGhost.Count.ToString(Ic)} " +
                $"ghostEvent={run.GhostEvent ?? "<none>"} status={wc.Status ?? "ok"}");
        }

        // ----- spawn -----

        private static void SpawnTrials(StepContext ctx, WaveCase wc)
        {
            PartParityBurstRun run = wc.Burst;
            PartParityFxOption o = run.Option;
            CelestialBody body = ctx.Host != null ? ctx.Host.mainBody : FlightGlobals.GetHomeBody();
            double mpd = 2.0 * Math.PI * body.Radius / 360.0;
            double alt = PartParityStateRules.AirSpawnAltitudeM;
            bool standIn = o.StandIn != null;
            for (int i = 0; i < PartParityFxRules.BurstTrials; i++)
            {
                double lat = PartParityAirPlan.PadLatitudeDeg + PartParityBurstPlan.TrialNorthOffsetM(i) / mpd;
                double lon = PartParityAirPlan.PadLongitudeDeg + PartParityAirPlan.BandEastOfPadM / mpd;
                double glon = lon + PartParityFxRules.BurstGhostSeparationM / mpd;
                Quaternion rot = GhostCoOrbitalRig.UprightRotation(body, lat, lon, alt, out Vector3 _);
                Quaternion srf = Quaternion.Inverse(body.bodyTransform.rotation) * rot;
                var t = new PartParityBurstTrial
                {
                    Index = i,
                    RealSlot = new PartParityHeldSlot { Body = body, Latitude = lat, Longitude = lon, AltitudeM = alt, SurfaceRelativeRotation = srf },
                    GhostSlot = new PartParityHeldSlot { Body = body, Latitude = lat, Longitude = glon, AltitudeM = alt, SurfaceRelativeRotation = srf },
                };
                run.Trials.Add(t);
                if (standIn) continue;
                ConfigNode node = AuthorTrialNode(ctx, wc, i, out string why);
                if (node == null)
                {
                    wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = wc.Detail ?? why ?? "burst trial vessel not authored";
                    return;
                }
                t.Pid = GhostCoOrbitalRig.SpawnInAir(node, body, lat, lon, alt, PartParityAirPlan.HeldDescentMps,
                    wc.CaseId + "-t" + i.ToString(Ic), Tag, rot);
                if (t.Pid == 0)
                {
                    wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = "SpawnAtPosition returned pid=0 for trial " + i.ToString(Ic);
                    return;
                }
                if (i == 0) wc.Pid = t.Pid;
            }
            wc.SpawnRealtime = Time.realtimeSinceStartup;
            ParsekLog.Info(Tag, $"burst spawn {wc.CaseId}: family={o.Family} state={o.State} trials={run.Trials.Count.ToString(Ic)} " +
                $"standIn={o.StandIn ?? "-"} potential={o.ExplosionPotential.ToString("0.###", Ic)} stack={o.StackSize.ToString(Ic)} " +
                $"tile={run.TilePx.ToString(Ic)} bins={run.Bins.ToString(Ic)} control={run.Control ?? "-"}");
        }

        private static Vector3 NodePos(AvailablePart info, string id)
        {
            AttachNode n = info?.partPrefab != null ? info.partPrefab.FindAttachNode(id) : null;
            return n != null ? n.position : Vector3.zero;
        }

        private static bool HasNode(AvailablePart info, string id)
            => info?.partPrefab != null && info.partPrefab.FindAttachNode(id) != null;

        /// <summary>The trial vessel of the case's family (design G.2.1): the lone part, a stack of
        /// <c>StackSize</c>, a decoupler between two tanks (or surface-attached to one), or a
        /// two-part stack for a plain joint break.</summary>
        private static ConfigNode AuthorTrialNode(StepContext ctx, WaveCase wc, int trial, out string why)
        {
            why = null;
            PartParityFxOption o = wc.Burst.Option;
            AvailablePart info = wc.Entry?.Info;
            if (info == null) { why = "no part info"; return null; }
            var parts = new List<ConfigNode>();
            string variant = wc.RealVariant;
            if (o.Family == PartParityFxRules.FamilyVesselExplode || o.State == PartParityFxRules.StateJointBreak)
            {
                int n = o.Family == PartParityFxRules.FamilyVesselExplode ? Math.Max(1, o.StackSize) : 2;
                Vector3 step = NodePos(info, "bottom") - NodePos(info, "top");
                List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(n);
                for (int i = 0; i < n; i++)
                {
                    var att = new Dictionary<string, int>(StringComparer.Ordinal);
                    if (i > 0) att["top"] = i - 1;
                    if (i < n - 1) att["bottom"] = i + 1;
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(info, ids[i], Math.Max(0, i - 1), step * i, att, variant, null));
                }
            }
            else if (o.Family == PartParityFxRules.FamilyDecouple)
            {
                AvailablePart tank = PartLoader.getPartInfoByName("fuelTankSmallFlat") ?? PartLoader.getPartInfoByName("fuelTank");
                if (tank == null) { why = "no neighbour tank for the decoupler"; return null; }
                List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(3);
                if (HasNode(info, "top") && HasNode(info, "bottom"))
                {
                    Vector3 decPos = NodePos(tank, "bottom") - NodePos(info, "top");
                    Vector3 tank2Pos = decPos + NodePos(info, "bottom") - NodePos(tank, "top");
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(tank, ids[0], 0, Vector3.zero,
                        new Dictionary<string, int> { { "bottom", 1 } }, null, null));
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(info, ids[1], 0, decPos,
                        new Dictionary<string, int> { { "top", 0 }, { "bottom", 2 } }, variant, null));
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(tank, ids[2], 1, tank2Pos,
                        new Dictionary<string, int> { { "top", 1 } }, null, null));
                }
                else
                {
                    // A radial decoupler: surface-attached to the tank's side (srfN written by hand).
                    parts.Add(GhostCoOrbitalRig.AuthorPartNode(tank, ids[0], 0, Vector3.zero, null, null, null));
                    ConfigNode dec = GhostCoOrbitalRig.AuthorPartNode(info, ids[1], 0, new Vector3(0.625f, 0f, 0f), null, variant, null);
                    dec.AddValue("srfN", "srfAttach,0");
                    parts.Add(dec);
                }
            }
            else
            {
                List<uint> ids = GhostCoOrbitalRig.UniqueFlightIds(1);
                parts.Add(GhostCoOrbitalRig.AuthorPartNode(info, ids[0], 0, Vector3.zero, null, variant, null));
            }
            Orbit src = ctx.Host.orbit;
            var placeholder = new Orbit(src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch, src.epoch, src.referenceBody);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: authored burst trial {trial.ToString(Ic)} part={info.name} parts={parts.Count.ToString(Ic)}");
            return ProtoVessel.CreateVesselNode(PartParityCaseRules.VesselName(wc.CaseId + "-t" + trial.ToString(Ic)),
                VesselType.Probe, placeholder, 0, parts.ToArray());
        }

        // ----- the ghosts and sources -----

        private static void PrepareTrials(WaveCase wc)
        {
            PartParityBurstRun run = wc.Burst;
            PartParityFxOption o = run.Option;
            foreach (PartParityBurstTrial t in run.Trials)
            {
                if (t.Real != null)
                {
                    t.Hold?.ResetEvidence();
                    List<Part> ps = t.Real.parts;
                    if (o.Family == PartParityFxRules.FamilyVesselExplode)
                    {
                        t.SourcePart = t.Real.rootPart;
                        t.SourceExtentM = (wc.Entry?.SizeMeters ?? 1.0) * ps.Count;
                    }
                    else if (o.Family == PartParityFxRules.FamilyDecouple)
                    {
                        t.SourcePart = ps.Count > 1 ? ps[1] : t.Real.rootPart;
                        t.SourceExtentM = wc.Entry?.SizeMeters ?? 1.0;
                    }
                    else
                    {
                        t.SourcePart = t.Real.rootPart;
                        t.SourceExtentM = wc.Entry?.SizeMeters ?? 1.0;
                    }
                    if (!PartParityBurstPlan.DropsGhostEvent(run.Control) || PartParityBurstPlan.IsRealVsReal(run.Control))
                        BuildTrialGhost(wc, t);
                }
                else
                {
                    t.SourceExtentM = o.StandInLengthM > 0.0 ? o.StandInLengthM : (wc.Entry?.SizeMeters ?? 1.0);
                }
                Vector3d real = SourceWorld(wc, t);
                Vector3d offset = SlotWorld(t.GhostSlot) - SlotWorld(t.RealSlot);
                t.RealSource = ToBodyFixed(t.RealSlot.Body, real);
                t.GhostSource = ToBodyFixed(t.RealSlot.Body, real + offset);
            }
            run.RecordBaseline = GhostTransientFxRecord.Entries.Count;
            if (!GhostTransientFxRecord.IsArmed)
            {
                GhostTransientFxRecord.Arm();
                run.ArmedRecord = true;
                run.RecordBaseline = 0;
            }
            foreach (FXObject x in GhostVisualBuilder.ResolveFxMongerExplosionObjects() ?? new List<FXObject>())
                if (x?.effectObj != null) run.SeenFxObjects.Add(x.effectObj);
            ParsekLog.Verbose(Tag, $"case {wc.CaseId}: burst trials prepared ghosts={CountGhosts(run).ToString(Ic)} " +
                $"preexistingFx={run.SeenFxObjects.Count.ToString(Ic)} recordArmedHere={(run.ArmedRecord ? "true" : "false")}");
        }

        private static int CountGhosts(PartParityBurstRun run)
        {
            int n = 0;
            foreach (PartParityBurstTrial t in run.Trials) if (t.Ghost != null) n++;
            return n;
        }

        private static void BuildTrialGhost(WaveCase wc, PartParityBurstTrial t)
        {
            string recId = "ppb-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!GhostCoOrbitalRig.TryBuildGhostFromLiveVessel(t.Real, recId, "ParsekPartParity_" + wc.CaseId + "_t" + t.Index.ToString(Ic),
                    out GhostCoOrbitalRig.LiveGhost g, out string fail))
            {
                wc.Flags.Add(PartParityCaseCapture.FlagGhostNotBuilt);
                wc.Detail = wc.Detail ?? fail;
                return;
            }
            t.Ghost = g;
            if (g.Recording.PartEvents == null) g.Recording.PartEvents = new List<PartEvent>();
            GameObject root = g.Build.root;
            PoseGhost(t);
            root.SetActive(true);
            GhostCoOrbitalRig.NeutralisePhysics(root, out int _, out int _);
            GhostPlaybackState state = g.State;
            state.lastInterpolatedBodyName = t.RealSlot.Body.name;
            state.lastInterpolatedAltitude = t.GhostSlot.AltitudeM;
            state.visualFxSuppressed = false;
            GhostPlaybackEngine.ApplyUnsuppressedFxRestore(state, true);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PoseGhost(PartParityBurstTrial t)
        {
            GameObject root = t.Ghost?.Build?.root;
            if (root == null) return;
            PartParityHeldSlot s = t.GhostSlot;
            root.transform.SetPositionAndRotation((Vector3)SlotWorld(s), s.Body.bodyTransform.rotation * s.SurfaceRelativeRotation);
        }

        private static Vector3d SlotWorld(PartParityHeldSlot s) => s.Body.GetWorldSurfacePosition(s.Latitude, s.Longitude, s.AltitudeM);

        private static Vector3d SourceWorld(WaveCase wc, PartParityBurstTrial t)
        {
            if (t.Real == null) return SlotWorld(t.RealSlot);
            if (wc.Burst.Option.Family == PartParityFxRules.FamilyVesselExplode)
            {
                Vector3d sum = Vector3d.zero;
                int n = 0;
                foreach (Part p in t.Real.parts) if (p != null) { sum += (Vector3d)p.transform.position; n++; }
                if (n > 0) return sum / n;
            }
            return t.SourcePart != null ? (Vector3d)t.SourcePart.transform.position : SlotWorld(t.RealSlot);
        }

        private static double[] ToBodyFixed(CelestialBody body, Vector3d world)
            => new[] { body.GetLatitude(world), body.GetLongitude(world), body.GetAltitude(world) };

        private static Vector3d FromBodyFixed(CelestialBody body, double[] p)
            => body.GetWorldSurfacePosition(p[0], p[1], p[2]);

        private static void Frame(CelestialBody body, double[] at, out Vector3 up, out Vector3 north, out Vector3 east)
        {
            Vector3d w = FromBodyFixed(body, at);
            up = ((Vector3)(w - body.position)).normalized;
            Vector3d n = body.GetWorldSurfacePosition(at[0] + 0.001, at[1], at[2]);
            north = Vector3.ProjectOnPlane((Vector3)(n - w), up).normalized;
            east = Vector3.Cross(up, north).normalized;
        }

        // ----- the trigger -----

        private static PartParityBurstTrial currentTrial;

        private static void OnPartDie(Part p)
        {
            if (currentTrial != null && p != null) currentTrial.DiedPids.Add(p.persistentId);
        }

        private static void OnJointBreak(PartJoint joint, float force)
        {
            if (currentTrial != null && joint?.Child != null) currentTrial.BrokenChildPids.Add(joint.Child.persistentId);
        }

        private static void TriggerAll(WaveCase wc)
        {
            PartParityBurstRun run = wc.Burst;
            run.TriggerFrame = Time.frameCount;
            run.TriggerTime = Time.time;
            run.TriggerUT = Planetarium.GetUniversalTime();
            bool nothing = PartParityBurstPlan.TriggersNothing(run.Control);
            if (!nothing)
            {
                GameEvents.onPartDie.Add(OnPartDie);
                GameEvents.onPartJointBreak.Add(OnJointBreak);
                try
                {
                    foreach (PartParityBurstTrial t in run.Trials)
                    {
                        currentTrial = t;
                        List<object> before = QueuedNow();
                        try { TriggerReal(wc, t); }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn(Tag, $"case {wc.CaseId}: trial {t.Index.ToString(Ic)} real trigger threw {ex.GetType().Name}: {ex.Message}");
                        }
                        ReadSchedule(wc, t, before, true, 0, 0.0);
                    }
                }
                finally
                {
                    currentTrial = null;
                    GameEvents.onPartDie.Remove(OnPartDie);
                    GameEvents.onPartJointBreak.Remove(OnJointBreak);
                }
            }
            run.GhostPending = !PartParityBurstPlan.DropsGhostEvent(run.Control);
            if (run.GhostPending && PartParityBurstPlan.GhostTriggerDelaySeconds(run.Control) <= 0.0) TriggerGhosts(wc, 0, 0.0);
            ParsekLog.Info(Tag, $"case {wc.CaseId}: burst trigger frame={run.TriggerFrame.ToString(Ic)} ut={run.TriggerUT.ToString("0.00", Ic)} " +
                $"nothing={(nothing ? "true" : "false")} ghostPending={(run.GhostPending ? "true" : "false")} " +
                $"realTriggered={CountTriggered(run, true).ToString(Ic)}/{run.Trials.Count.ToString(Ic)}");
        }

        private static int CountTriggered(PartParityBurstRun run, bool real)
        {
            int n = 0;
            foreach (PartParityBurstTrial t in run.Trials) if (real ? t.RealTriggered : t.GhostTriggered) n++;
            return n;
        }

        private static void TriggerReal(WaveCase wc, PartParityBurstTrial t)
        {
            PartParityFxOption o = wc.Burst.Option;
            if (o.StandIn != null)
            {
                FXMonger.Explode(null, SlotWorld(t.RealSlot), o.ExplosionPotential);
                t.RealTriggered = true;
                return;
            }
            if (t.Real == null || t.SourcePart == null) return;
            // An exploding trial loses its hold first (it holds a vessel that is about to go); a
            // decoupling one keeps it, so the decoupler's own effect stays where the ghost's is.
            bool explodes = o.Family == PartParityFxRules.FamilyExplode || o.Family == PartParityFxRules.FamilyVesselExplode;
            if (explodes && t.Hold != null) { UnityEngine.Object.Destroy(t.Hold.gameObject); t.Hold = null; }
            if (o.Family == PartParityFxRules.FamilyExplode) t.SourcePart.explode();
            else if (o.Family == PartParityFxRules.FamilyVesselExplode)
            {
                Part[] all = t.Real.parts.ToArray();
                for (int i = all.Length - 1; i >= 0; i--) if (all[i] != null) all[i].explode();
            }
            else if (o.State == PartParityFxRules.StateJointBreak) t.SourcePart.decouple(0f);
            else
            {
                PartModule m = o.ModuleIndex >= 0 && o.ModuleIndex < t.SourcePart.Modules.Count ? t.SourcePart.Modules[o.ModuleIndex] : null;
                if (m == null) return;
                FXGroup group = string.IsNullOrEmpty(o.FxGroup) ? null : t.SourcePart.findFxGroup(o.FxGroup);
                if (group?.fxEmittersNewSystem != null)
                    foreach (ParticleSystem ps in group.fxEmittersNewSystem)
                    {
                        ParticleSystemRenderer r = ps != null ? ps.GetComponent<ParticleSystemRenderer>() : null;
                        if (r != null) t.RealEmitters.Add(r);
                    }
                BaseEvent ev = m.Events["Decouple"];
                if (ev != null) ev.Invoke();
                else
                {
                    MethodInfo mi = m.GetType().GetMethod("Decouple", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (mi == null) return;
                    mi.Invoke(m, null);
                }
            }
            t.RealTriggered = true;
        }

        private static void TriggerGhosts(WaveCase wc, int frame, double tSeconds)
        {
            PartParityBurstRun run = wc.Burst;
            run.GhostPending = false;
            foreach (PartParityBurstTrial t in run.Trials)
            {
                List<object> before = QueuedNow();
                try { TriggerGhost(wc, t); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag, $"case {wc.CaseId}: trial {t.Index.ToString(Ic)} ghost trigger threw {ex.GetType().Name}: {ex.Message}");
                }
                ReadSchedule(wc, t, before, false, frame, tSeconds);
            }
            ParsekLog.Info(Tag, $"case {wc.CaseId}: burst ghost trigger event={run.GhostEvent ?? "<none>"} frame={frame.ToString(Ic)} " +
                $"t={tSeconds.ToString("0.000", Ic)}s triggered={CountTriggered(run, false).ToString(Ic)}/{run.Trials.Count.ToString(Ic)}");
        }

        private static void TriggerGhost(WaveCase wc, PartParityBurstTrial t)
        {
            PartParityBurstRun run = wc.Burst;
            PartParityFxOption o = run.Option;
            Vector3 ghostSource = (Vector3)FromBodyFixed(t.RealSlot.Body, t.GhostSource);
            if (o.StandIn == PartParityFxRules.StandInCustom)
            {
                // Production's custom route: the kept stock copy first, the hand-built one last.
                GameObject go = GhostVisualBuilder.SpawnFallbackExplosionFx((Vector3)SlotWorld(t.GhostSlot), o.ExplosionPotential,
                    (float)o.StandInLengthM);
                if (go != null) t.GhostObjects.Add(go);
                run.GhostEvent = "custom-explosion";
                t.GhostTriggered = true;
                return;
            }
            if (o.StandIn == PartParityFxRules.StandInPuff)
            {
                float scale = (float)((wc.Entry?.SizeMeters ?? 1.0) * 0.5);
                GameObject go = GhostVisualBuilder.SpawnPartPuffFx((Vector3)SlotWorld(t.GhostSlot), scale);
                if (go != null) t.GhostObjects.Add(go);
                run.GhostEvent = "puff";
                t.GhostTriggered = true;
                return;
            }
            GhostCoOrbitalRig.LiveGhost g = t.Ghost;
            if (g == null) return;
            PoseGhost(t);
            double ut = Planetarium.GetUniversalTime();
            bool shift = PartParityBurstPlan.GhostTriggerShiftM(run.Control) > 0.0;
            bool lower = PartParityBurstPlan.LowersGhostPower(run.Control);
            if (shift || lower)
            {
                Frame(t.RealSlot.Body, t.GhostSource, out Vector3 _, out Vector3 north, out Vector3 _);
                Vector3 at = ghostSource + north * (float)PartParityBurstPlan.GhostTriggerShiftM(run.Control);
                double power = o.ExplosionPotential;
                if (lower)
                {
                    FXMonger m = GhostVisualBuilder.ResolveLiveFxMonger();
                    int n = m?.explosions != null ? m.explosions.Length : 0;
                    power = PartParityBurstPlan.LowerIndexPower(power, n);
                }
                GhostVisualBuilder.TryTriggerStockExplosionFx(at, power, out string fail);
                if (fail != null) ParsekLog.Verbose(Tag, $"case {wc.CaseId}: ghost stock explosion: {fail}");
                GhostPlaybackLogic.HideAllGhostParts(g.State);
                run.GhostEvent = shift ? "explosion-shifted" : "explosion-lowered";
                t.GhostTriggered = true;
                return;
            }
            if (o.Family == PartParityFxRules.FamilyVesselExplode)
            {
                float len = GhostVisualBuilder.ComputeGhostLength(g.Build.root);
                GhostPlaybackLogic.TryTriggerStockExplosionFxOrCustom(ghostSource, GhostPlaybackEngine.TerminalExplosionPower(len), len,
                    "part parity " + wc.CaseId);
                GhostPlaybackLogic.HideAllGhostParts(g.State);
                run.GhostEvent = "terminal-explosion";
                t.GhostTriggered = true;
                return;
            }
            var names = new Dictionary<uint, string>();
            if (g.Snapshot != null)
                foreach (ConfigNode p in g.Snapshot.GetNodes("PART"))
                    if (uint.TryParse(p.GetValue("persistentId"), NumberStyles.Integer, Ic, out uint pid)) names[pid] = p.GetValue("name");
            int added = 0;
            foreach (uint pid in t.BrokenChildPids)
            {
                g.Recording.PartEvents.Add(FlightRecorder.BuildDecoupledPartEvent(pid, NameOf(names, pid), ut));
                added++;
            }
            foreach (uint pid in t.DiedPids)
            {
                PartEventType death = FlightRecorder.ClassifyPartDeath(pid, false, new Dictionary<uint, int>());
                g.Recording.PartEvents.Add(FlightRecorder.BuildDestroyedPartEvent(pid, NameOf(names, pid), ut, death));
                added++;
            }
            if (added == 0) return;
            GhostPlaybackLogic.ApplyPartEvents(-1, g.Recording, ut, g.State, true);
            run.GhostEvent = t.BrokenChildPids.Count > 0 && t.DiedPids.Count == 0 ? PartEventType.Decoupled.ToString()
                : t.DiedPids.Count > 0 && t.BrokenChildPids.Count == 0 ? PartEventType.Destroyed.ToString()
                : PartEventType.Decoupled + "+" + PartEventType.Destroyed;
            t.GhostTriggered = true;
        }

        private static string NameOf(Dictionary<uint, string> names, uint pid) => names.TryGetValue(pid, out string n) ? n : "unknown";

        // ----- FXMonger's queue (the schedule) -----

        private static FieldInfo queuedField;
        private static readonly Dictionary<Type, KeyValuePair<FieldInfo, FieldInfo>> protoFields = new Dictionary<Type, KeyValuePair<FieldInfo, FieldInfo>>();

        private static List<object> QueuedNow()
        {
            var list = new List<object>();
            FXMonger m = GhostVisualBuilder.ResolveLiveFxMonger();
            if (m == null) return list;
            if (queuedField == null)
                queuedField = typeof(FXMonger).GetField("queuedExplosions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (queuedField?.GetValue(m) is IList q)
                foreach (object x in q) if (x != null) list.Add(x);
            return list;
        }

        private static bool ReadProto(object x, out Vector3d position, out double power)
        {
            position = Vector3d.zero;
            power = double.NaN;
            Type t = x.GetType();
            if (!protoFields.TryGetValue(t, out KeyValuePair<FieldInfo, FieldInfo> f))
            {
                f = new KeyValuePair<FieldInfo, FieldInfo>(
                    t.GetField("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                    t.GetField("power", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                protoFields[t] = f;
            }
            if (f.Key == null || f.Value == null) return false;
            position = (Vector3d)f.Key.GetValue(x);
            power = Convert.ToDouble(f.Value.GetValue(x), Ic);
            return true;
        }

        /// <summary>The blasts one trigger call queued (new queue entries by reference), relative to
        /// that side's own source in the slot frame.</summary>
        private static void ReadSchedule(WaveCase wc, PartParityBurstTrial t, List<object> before, bool real, int frame, double tSeconds)
        {
            var known = new HashSet<object>(before);
            FXMonger m = GhostVisualBuilder.ResolveLiveFxMonger();
            int n = m?.explosions != null ? m.explosions.Length : 0;
            CelestialBody body = t.RealSlot.Body;
            double[] src = real ? t.RealSource : t.GhostSource;
            Frame(body, src, out Vector3 up, out Vector3 north, out Vector3 east);
            Vector3d s = FromBodyFixed(body, src);
            foreach (object x in QueuedNow())
            {
                if (known.Contains(x) || !ReadProto(x, out Vector3d pos, out double power)) continue;
                int idx = PartParityFxRules.FxMongerPrefabIndex(power, n);
                (real ? t.RealBlasts : t.GhostBlasts).Add(new PartParityBurstBlast
                {
                    Frame = frame, T = tSeconds,
                    OffsetM = PartParityBurstPlan.SlotFrameOffset(V(east), V(up), V(north), V(s), V(pos)),
                    Power = power, Index = idx,
                    Prefab = idx >= 0 && idx < n && m.explosions[idx] != null ? m.explosions[idx].name : null,
                });
            }
        }

        private static double[] V(Vector3 v) => new double[] { v.x, v.y, v.z };
        private static double[] V(Vector3d v) => new[] { v.x, v.y, v.z };

        // ----- tagging and the capture -----

        private static void Tag8(WaveCase wc)
        {
            PartParityBurstRun run = wc.Burst;
            CelestialBody body = run.Trials[0].RealSlot.Body;
            var sources = new List<double[]>();
            var ghostSources = new List<double[]>();
            double extent = 0.0;
            foreach (PartParityBurstTrial t in run.Trials) { sources.Add(V(FromBodyFixed(body, t.RealSource))); extent = Math.Max(extent, t.SourceExtentM); }
            foreach (PartParityBurstTrial t in run.Trials) { double[] g = V(FromBodyFixed(body, t.GhostSource)); sources.Add(g); ghostSources.Add(g); }
            double radius = PartParityBurstPlan.TagRadiusM(extent);
            foreach (FXObject x in GhostVisualBuilder.ResolveFxMongerExplosionObjects() ?? new List<FXObject>())
            {
                GameObject go = x?.effectObj;
                if (go == null || run.SeenFxObjects.Contains(go)) continue;
                run.SeenFxObjects.Add(go);
                int i = PartParityBurstPlan.NearestSource(sources, V(go.transform.position), radius);
                if (i < 0) { run.StrayObjects++; continue; }
                if (i < run.Trials.Count) { run.Trials[i].RealObjects.Add(go); run.RealTagged++; run.RealNames.Add(PartParityBurstPlan.StripClone(go.name)); }
                else { run.Trials[i - run.Trials.Count].GhostObjects.Add(go); run.GhostTagged++; run.GhostNames.Add(PartParityBurstPlan.StripClone(go.name)); }
            }
            IReadOnlyList<GhostTransientFxEntry> entries = GhostTransientFxRecord.Entries;
            for (int k = run.RecordBaseline; k < entries.Count; k++)
            {
                GameObject go = entries[k].Object;
                if (go == null || run.SeenRecordObjects.Contains(go) || run.SeenFxObjects.Contains(go)) continue;
                run.SeenRecordObjects.Add(go);
                int i = PartParityBurstPlan.NearestSource(ghostSources, V(go.transform.position), radius);
                if (i < 0) { run.StrayObjects++; continue; }
                if (run.Trials[i].GhostObjects.Add(go)) { run.GhostTagged++; run.GhostNames.Add(PartParityBurstPlan.StripClone(go.name)); }
            }
        }

        private static void AddRenderers(IEnumerable<GameObject> objects, List<Renderer> into)
        {
            foreach (GameObject go in objects)
                if (go != null) into.AddRange(go.GetComponentsInChildren<Renderer>(true));
        }

        private static void CaptureFrame(StepContext ctx, WaveCase wc, int frame, int bin)
        {
            PartParityBurstRun run = wc.Burst;
            Tag8(wc);
            foreach (PartParityBurstTrial t in run.Trials) PoseGhost(t);
            int trial = PartParityBurstPlan.TrialOfFrame(frame);
            PartParityBurstTrial tr = run.Trials[trial];
            bool rvr = PartParityBurstPlan.IsRealVsReal(run.Control);
            var all = PartParityFxDriver.HideSet();
            foreach (PartParityBurstTrial t in run.Trials)
            {
                if (t.Ghost?.Build?.root != null) all.AddRange(t.Ghost.Build.root.GetComponentsInChildren<Renderer>(true));
                AddRenderers(t.RealObjects, all);
                AddRenderers(t.GhostObjects, all);
                all.AddRange(t.RealEmitters);
            }
            foreach (GameObject go in run.SeenFxObjects) if (go != null) all.AddRange(go.GetComponentsInChildren<Renderer>(true));
            foreach (GameObject go in run.SeenRecordObjects) if (go != null) all.AddRange(go.GetComponentsInChildren<Renderer>(true));
            var sides = new List<KeyValuePair<bool, int>>();
            if (rvr)
            {
                PartParityBurstPlan.RealVsRealTrial(trial, out bool realSide, out int number);
                sides.Add(new KeyValuePair<bool, int>(realSide, number));
            }
            else
            {
                sides.Add(new KeyValuePair<bool, int>(true, trial));
                sides.Add(new KeyValuePair<bool, int>(false, trial));
            }
            int mask = 1 | 2;
            var shows = new Dictionary<bool, KeyValuePair<List<Renderer>, List<Renderer>>>();
            foreach (KeyValuePair<bool, int> side in sides)
            {
                var objs = new List<Renderer>();
                if (rvr || side.Key) { AddRenderers(tr.RealObjects, objs); objs.AddRange(tr.RealEmitters); }
                else AddRenderers(tr.GhostObjects, objs);
                var flame = new List<Renderer>();
                var smoke = new List<Renderer>();
                foreach (Renderer r in objs)
                {
                    if (r == null) continue;
                    string layer = PartParityFxRules.ClassifyFxRenderer(r.GetType().Name,
                        r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : null);
                    if (layer == PartParityFxRules.LayerFlame) flame.Add(r);
                    else if (layer == PartParityFxRules.LayerSmoke) smoke.Add(r);
                    else continue;
                    mask |= 1 << r.gameObject.layer;
                }
                shows[side.Key] = new KeyValuePair<List<Renderer>, List<Renderer>>(flame, smoke);
            }
            run.Frames++;
            CelestialBody body = tr.RealSlot.Body;
            using (PartParityCapture.FxFrame f = run.Capture.BeginFxFrame(all, mask, false))
            {
                foreach (KeyValuePair<bool, int> side in sides)
                {
                    double[] src = rvr || side.Key ? tr.RealSource : tr.GhostSource;
                    Vector3 s = (Vector3)FromBodyFixed(body, src);
                    Frame(body, src, out Vector3 up, out Vector3 north, out Vector3 _);
                    KeyValuePair<List<Renderer>, List<Renderer>> show = shows[side.Key];
                    foreach (string view in PartParityFxRules.BurstViews)
                    {
                        PoseBurstView(f, view, s, up, north);
                        byte[] flamePx = f.Render(show.Key, Color.black);
                        if (!side.Key && PartParityBurstPlan.RotatesGhostHue(run.Control))
                            for (int p = 0; p < flamePx.Length; p += 4)
                                PartParityFxRules.RotateHueControl(ref flamePx[p], ref flamePx[p + 1], ref flamePx[p + 2]);
                        Acc(run, view, PartParityFxRules.LayerFlame, side.Key).Add(bin, side.Value, flamePx);
                        byte[] over = f.Render(show.Value, Color.black);
                        byte[] white = f.Render(show.Value, Color.white);
                        Acc(run, view, PartParityFxRules.LayerSmoke, side.Key).Add(bin, side.Value, PartParityMetrics.ComposeMatte(over, white, out _));
                    }
                }
            }
        }

        private static void PoseBurstView(PartParityCapture.FxFrame f, string view, Vector3 source, Vector3 up, Vector3 north)
        {
            float stand = (float)PartParityBurstPlan.BurstCameraStandM;
            double ortho = PartParityBurstPlan.BurstOrthoM(view);
            if (view == PartParityFxRules.ViewBurstTop)
                f.Pose(source + up * stand, Quaternion.LookRotation(-up, north), ortho, 1.0, 2.0 * stand);
            else
                f.Pose(source - north * stand, Quaternion.LookRotation(north, up), ortho, 1.0, 2.0 * stand);
        }

        private static PartParityBurstAccumulator Acc(PartParityBurstRun run, string view, string layer, bool real)
        {
            string key = PartParityBurstRun.AccKey(view, layer, real);
            if (!run.Accumulators.TryGetValue(key, out PartParityBurstAccumulator a))
            {
                a = new PartParityBurstAccumulator(layer, run.TilePx, run.TilePx, run.Bins);
                run.Accumulators[key] = a;
            }
            return a;
        }

        // ----- the measures -----

        private static void MeasureBurst(WaveCase wc)
        {
            PartParityBurstRun run = wc.Burst;
            PartParityFxOption o = run.Option;
            bool rvr = PartParityBurstPlan.IsRealVsReal(run.Control);
            PartParityBurstTrial first = run.Trials[0];
            run.ScheduleReal = new List<PartParityBurstBlast>(first.RealBlasts);
            run.ScheduleGhost = rvr && run.Trials.Count > 1 ? new List<PartParityBurstBlast>(run.Trials[1].RealBlasts)
                : new List<PartParityBurstBlast>(first.GhostBlasts);
            run.ScheduleCompare = PartParityBurstMetrics.CompareSchedule(run.ScheduleReal, run.ScheduleGhost);
            double size = o.StandInLengthM > 0.0 ? o.StandInLengthM : (wc.Entry?.SizeMeters ?? 1.0);
            foreach (string view in PartParityFxRules.BurstViews)
            {
                double ortho = PartParityBurstPlan.BurstOrthoM(view);
                double c = run.TilePx / 2.0;
                var source = new PartParityBurstSource
                {
                    RealPx = new[] { c, c }, GhostPx = new[] { c, c },
                    DiameterPx = PartParityBurstPlan.MetresToPx(size, ortho, run.TilePx),
                };
                foreach (string layer in PartParityFxRules.BurstLayers)
                    run.Results.Add(PartParityBurstViewResult.Measure(view, layer, source, Acc(run, view, layer, true), Acc(run, view, layer, false)));
            }
            run.Accumulators.Clear();
            bool envelope = false;
            foreach (PartParityBurstViewResult r in run.Results)
                foreach (PartParityBurstBinMeasures b in r.Real.Bins) if (b.EnvelopePx > 0) envelope = true;
            bool triggered = CountTriggered(run, true) > 0;
            int realTagged = run.RealTagged;
            foreach (PartParityBurstTrial t in run.Trials) realTagged += t.RealEmitters.Count;
            string status = PartParityBurstPlan.BurstSetupStatus(run.Control, o.State, o.Family, triggered, envelope,
                realTagged, run.ScheduleReal.Count);
            if (status != TestCommandPartParitySweep.SetupOk)
            {
                wc.Status = status;
                wc.Detail = wc.Detail ?? $"triggered={(triggered ? "true" : "false")} envelope={(envelope ? "true" : "false")} " +
                    $"realTagged={realTagged.ToString(Ic)} realBlasts={run.ScheduleReal.Count.ToString(Ic)}";
            }
            run.StructureMeasured = o.Family != PartParityFxRules.FamilyBurstCustom;
            if (o.Family == PartParityFxRules.FamilyDecouple)
                foreach (PartParityBurstTrial t in run.Trials)
                    foreach (Renderer r in t.RealEmitters) if (r != null) run.RealNames.Add(PartParityBurstPlan.StripClone(r.gameObject.name));
            if (wc.ControlId != null)
            {
                var e = new PartParityBurstControlEvidence
                {
                    SetupStatus = wc.Status ?? TestCommandPartParitySweep.SetupOk,
                    RealBlasts = run.ScheduleReal.Count, GhostBlasts = run.ScheduleGhost.Count,
                    ScheduleIndexDiffers = run.ScheduleCompare != null && run.ScheduleCompare.IndexDiffers,
                };
                bool any = false;
                foreach (PartParityBurstViewResult r in run.Results)
                {
                    foreach (string m in r.FailingMeasures()) e.FailingMeasures.Add(m);
                    foreach (PartParityBurstBinMeasures b in r.Real.Bins) if (b.EnvelopePx > 0) any = true;
                    foreach (PartParityBurstBinMeasures b in r.Ghost.Bins) if (b.EnvelopePx > 0) any = true;
                }
                foreach (string m in PartParityBurstMetrics.FailingScheduleMeasures(run.ScheduleCompare)) e.FailingMeasures.Add(m);
                e.AllCoverageZero = !any;
                wc.Flags.Add(PartParityBurstPlan.FormatBurstEvidence(e));
            }
        }

        // =====================================================================================
        // the sidecar, the atlas, the teardown
        // =====================================================================================

        /// <summary>The <c>burst</c> or <c>dust</c> block of the case's sidecar (design G.2, the X5 contract).</summary>
        internal static void FillSidecar(StepContext ctx, WaveCase wc, PartParityCaseSidecar s)
        {
            PartParityBurstRun run = wc.Burst;
            if (run == null) return;
            s.StateProof = null;
            s.Fx = null;
            s.FxAtlas = null;
            s.Strip = null;
            if (run.IsDust) { FillDustSidecar(wc, s); return; }
            if (run.Results.Count == 0) return;
            PartParityFxOption o = run.Option;
            double window = PartParityFxRules.BurstWindowFor(o.Family);
            PartParityBurstViewResult firstResult = run.Results[0];
            var d = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "family", PartParityJson.Str(o.Family) },
                { "module", PartParityJson.Str(o.Module) },
                { "moduleIndex", o.ModuleIndex >= 0 ? PartParityJson.Int(o.ModuleIndex) : "null" },
                { "potential", PartParityJson.Num(o.ExplosionPotential) },
                { "stackSize", o.StackSize > 0 ? PartParityJson.Int(o.StackSize) : "null" },
                { "standIn", PartParityJson.Str(o.StandIn) },
                { "lengthM", o.StandInLengthM > 0.0 ? PartParityJson.Num(o.StandInLengthM) : "null" },
                { "fxGroup", PartParityJson.Str(o.FxGroup) },
                { "windowS", PartParityJson.Num(window) },
                { "binS", PartParityJson.Num(PartParityFxRules.BurstBinSeconds) },
                { "bins", PartParityJson.Int(run.Bins) },
                { "trials", PartParityJson.Int(PartParityFxRules.BurstTrials) },
                { "samples", PartParityJson.KeyedBlock(PartParityJson.BurstSideKeys, new Dictionary<string, string>
                    {
                        { "real", PartParityJson.IntArray(firstResult.RealSamples) },
                        { "ghost", PartParityJson.IntArray(firstResult.GhostSamples) },
                    }) },
                { "trigger", run.TriggerFrame < 0 ? "null" : PartParityJson.KeyedBlock(PartParityJson.BurstTriggerKeys, new Dictionary<string, string>
                    {
                        { "frame", PartParityJson.Int(Math.Max(0, run.TriggerFrame)) },
                        { "ut", PartParityJson.Num(run.TriggerUT) },
                        { "realTagged", PartParityJson.Int(run.RealTagged) },
                        { "ghostTagged", PartParityJson.Int(run.GhostTagged) },
                        { "ghostEvent", PartParityJson.Str(run.GhostEvent) },
                    }) },
                { "schedule", PartParityBurstMetrics.ScheduleJson(run.ScheduleReal, run.ScheduleGhost) },
                { "scheduleCompare", PartParityBurstMetrics.ScheduleCompareJson(run.ScheduleCompare) },
                { "atlas", PartParityJson.KeyedBlock(PartParityJson.BurstAtlasKeys, new Dictionary<string, string>
                    {
                        { "file", PartParityJson.Str(TestCommandPartParitySweep.BurstAtlasFileName(wc.CaseId)) },
                        { "rows", PartParityJson.StrArray(PartParityJson.BurstAtlasRows) },
                        { "views", PartParityJson.StrArray(PartParityFxRules.BurstViews) },
                        { "bins", PartParityJson.Int(run.Bins) },
                        { "tile", PartParityJson.Int(run.TilePx) },
                    }) },
                { "views", JoinArray(run.Results.ConvertAll(r => r.Json())) },
                { "structure", run.StructureMeasured ? StructureJson(run.RealNames, run.GhostNames) : "null" },
            };
            s.Burst = d;
        }

        internal static string JoinArray(List<string> items) => "[" + string.Join(",", items.ToArray()) + "]";

        private static string StructureJson(ICollection<string> real, ICollection<string> ghost)
        {
            var missing = new List<PartParityFxStructureEntry>();
            var extra = new List<PartParityFxStructureEntry>();
            PartParityBurstPlan.DiffStructure(real, ghost, missing, extra);
            return PartParityJson.KeyedBlock(PartParityJson.FxStructureKeys, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "fxMissingOnGhost", PartParityFxStructure.Json(missing) },
                { "fxExtraOnGhost", PartParityFxStructure.Json(extra) },
                { "fxDiffers", PartParityJson.StrArray(new List<string>()) },
                { "lightsDiffer", PartParityJson.StrArray(new List<string>()) },
            });
        }

        /// <summary>The burst atlas (rows <see cref="PartParityJson.BurstAtlasRows"/>, columns view-major
        /// views x bins) or the dust atlas.</summary>
        internal static void WritePictures(StepContext ctx, WaveCase wc, PartParityCaseSidecar s)
        {
            PartParityBurstRun run = wc.Burst;
            if (run == null) return;
            if (run.IsDust) { WriteDustAtlas(ctx, wc, s); return; }
            if (s.Burst == null || run.Results.Count == 0) return;
            int views = PartParityFxRules.BurstViews.Length;
            int cols = views * run.Bins;
            byte[] blank = new byte[run.TilePx * run.TilePx * 4];
            var rows = new byte[PartParityJson.BurstAtlasRows.Length][][];
            for (int r = 0; r < rows.Length; r++)
            {
                rows[r] = new byte[cols][];
                for (int c = 0; c < cols; c++) rows[r][c] = blank;
            }
            foreach (PartParityBurstViewResult res in run.Results)
            {
                int vi = Array.IndexOf(PartParityFxRules.BurstViews, res.View);
                int realRow = Array.IndexOf(PartParityJson.BurstAtlasRows, PartParityFxRules.SideReal + "-" + res.Layer);
                int ghostRow = Array.IndexOf(PartParityJson.BurstAtlasRows, PartParityFxRules.SideGhost + "-" + res.Layer);
                if (vi < 0 || realRow < 0 || ghostRow < 0) continue;
                for (int b = 0; b < run.Bins; b++)
                {
                    rows[realRow][vi * run.Bins + b] = res.RealTiles[b] ?? blank;
                    rows[ghostRow][vi * run.Bins + b] = res.GhostTiles[b] ?? blank;
                }
            }
            byte[] atlas = PartParityImage.BuildAtlas(rows, cols, run.TilePx, out int w, out int h);
            FileIOUtils.SafeWriteBytes(PartParityPng.EncodeRgba(w, h, atlas),
                Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.BurstAtlasFileName(wc.CaseId)), Tag);
            foreach (PartParityBurstViewResult res in run.Results) { res.RealTiles = null; res.GhostTiles = null; }
        }

        /// <summary>Destroys every trial vessel (debris included), ghost, hold and stand-in effect
        /// object of the case; disarms the transient record when this run armed it (idempotent).</summary>
        internal static void Release(WaveCase wc)
        {
            PartParityBurstRun run = wc?.Burst;
            if (run == null) return;
            var pids = new HashSet<uint>();
            foreach (PartParityBurstTrial t in run.Trials)
            {
                if (t.Hold != null) { UnityEngine.Object.Destroy(t.Hold.gameObject); t.Hold = null; }
                if (t.Ghost != null)
                {
                    try { GhostCoOrbitalRig.DestroyLiveGhost(t.Ghost); }
                    catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: burst ghost destroy threw {ex.GetType().Name}: {ex.Message}"); }
                    t.Ghost = null;
                }
                foreach (uint p in t.PartPids) pids.Add(p);
                t.Real = null;
            }
            if (run.Dust != null) ReleaseDust(wc);
            if (run.Capture != null)
            {
                run.Capture.Dispose();
                run.Capture = null;
            }
            int killed = KillVesselsWithParts(pids);
            if (run.ArmedRecord)
            {
                GhostTransientFxRecord.Disarm();
                run.ArmedRecord = false;
            }
            if (killed > 0 || pids.Count > 0)
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: burst release killed={killed.ToString(Ic)} partPids={pids.Count.ToString(Ic)}");
        }

        /// <summary>Kills every loaded or unloaded vessel holding one of <paramref name="pids"/>
        /// (a decouple leaves two), never the active vessel.</summary>
        internal static int KillVesselsWithParts(HashSet<uint> pids)
        {
            if (pids == null || pids.Count == 0) return 0;
            int n = 0;
            foreach (Vessel v in FlightGlobals.Vessels.ToArray())
            {
                if (v == null || v == FlightGlobals.ActiveVessel || v.state == Vessel.State.DEAD || v.parts == null) continue;
                bool ours = false;
                foreach (Part p in v.parts) if (p != null && pids.Contains(p.persistentId)) { ours = true; break; }
                if (!ours) continue;
                try { v.Die(); n++; }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"burst release: Die threw {ex.GetType().Name}: {ex.Message}"); }
            }
            return n;
        }

        private static IEnumerator WaitTrialVesselsGone(List<WaveCase> wave)
        {
            var pids = new HashSet<uint>();
            foreach (WaveCase wc in wave)
            {
                if (wc.Burst == null) continue;
                foreach (PartParityBurstTrial t in wc.Burst.Trials) foreach (uint p in t.PartPids) pids.Add(p);
                Release(wc);
            }
            float deadline = Time.realtimeSinceStartup + (float)PartParitySweepPlan.VesselGoneTimeoutSeconds;
            while (pids.Count > 0 && Time.realtimeSinceStartup < deadline)
            {
                bool any = false;
                foreach (Vessel v in FlightGlobals.Vessels)
                {
                    if (v == null || v.state == Vessel.State.DEAD || v.parts == null) continue;
                    foreach (Part p in v.parts) if (p != null && pids.Contains(p.persistentId)) { any = true; break; }
                    if (any) break;
                }
                if (!any) yield break;
                yield return null;
            }
            if (pids.Count > 0) ParsekLog.Warn(Tag, $"burst teardown: trial vessels still present after {PartParitySweepPlan.VesselGoneTimeoutSeconds.ToString("0", Ic)} s");
        }
    }
}
