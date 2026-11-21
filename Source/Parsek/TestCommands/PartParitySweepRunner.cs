using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Parsek.InGameTests;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>The outcome of one sweep step (the seam verb's or the in-game cell's).</summary>
    internal sealed class PartParitySweepStepResult
    {
        public bool Ok;            // false = the step could not run (reason/detail set)
        public string Reason;      // a TestCommandPartParitySweep.Reasons entry when !Ok
        public string Detail;
        public List<KeyValuePair<string, string>> Payload; // TestCommandPartParitySweep.BuildPayload output when Ok
    }

    /// <summary>
    /// The wave coroutine of the part parity sweep (design-part-ghost-parity.md 4.1, 4.2,
    /// 4.5, 5.1, 5.3). One <see cref="RunStep"/> is one seam step: <c>op=reset</c> clears the
    /// label directory; <c>op=run</c> enumerates the install's parts, runs the controls
    /// first (when <c>controls=true</c>), then the selected cases in the stable order in
    /// waves of up to 8 one-part vessels spawned co-orbital within 180 m of the host, until
    /// the time box ends, and rewrites the manifest from every sidecar on disk. All sweep
    /// state lives in the step's context object and the label directory: no statics.
    /// </summary>
    internal static class PartParitySweepRunner
    {
        internal const string Tag = "PartParity";

        /// <summary>The <c>Reason</c> of a step that failed on an unexpected exception (an
        /// ERROR, not one of the parse / admission refusals in
        /// <see cref="TestCommandPartParitySweep.Reasons"/>).</summary>
        internal const string StepExceptionReason = "step-exception";

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private const int MaxCaseLogLines = 200;

        // One seam step (or the in-game cell): honours args.Op (reset|run) and args.MaxSeconds,
        // calls onDone exactly once, never throws out of the coroutine.
        internal static IEnumerator RunStep(PartParitySweepArgs args, Action<PartParitySweepStepResult> onDone)
        {
            var ctx = new StepContext(args);
            IEnumerator core = StepCore(ctx);
            while (true)
            {
                object current = null;
                bool more;
                try
                {
                    more = core.MoveNext();
                    if (more) current = core.Current;
                }
                catch (Exception ex)
                {
                    ctx.FailWithException(ex);
                    more = false;
                }
                if (!more) break;
                yield return current;
            }
            ctx.Cleanup();
            PartParitySweepStepResult result = ctx.Result ?? new PartParitySweepStepResult
            {
                Ok = false, Reason = StepExceptionReason, Detail = "step ended without a result",
            };
            ParsekLog.Info(Tag, StepSummaryLine(args, result));
            try { onDone?.Invoke(result); }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"step onDone threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>The literal every step's one summary line carries (the lane's log contract
        /// pins it; hlib mirrors it as <c>PARTPARITY_STEP_LINE_LITERAL</c>).</summary>
        internal const string StepLineLiteral = "PartParity step";

        /// <summary>The one Info line per <see cref="RunStep"/> call, reset and run alike:
        /// <c>PartParity step label=&lt;label&gt; op=&lt;op&gt; ok=&lt;bool&gt;</c> then the payload
        /// when ok, else <c>reason= detail=</c>.</summary>
        internal static string StepSummaryLine(PartParitySweepArgs args, PartParitySweepStepResult result)
        {
            var sb = new StringBuilder(StepLineLiteral);
            sb.Append(" label=").Append(args?.Label ?? string.Empty)
                .Append(" op=").Append(args != null && args.Op == PartParitySweepOp.Reset ? "reset" : "run")
                .Append(" ok=").Append(result != null && result.Ok ? "true" : "false");
            if (result != null && result.Ok)
                sb.Append(' ').Append(TestCommandPartParitySweep.FormatPayload(result.Payload));
            else
                sb.Append(" reason=").Append(result?.Reason ?? "<none>").Append(" detail=").Append(result?.Detail ?? "<none>");
            return sb.ToString();
        }

        // =====================================================================================
        // step
        // =====================================================================================

        private static IEnumerator StepCore(StepContext ctx)
        {
            PartParitySweepArgs a = ctx.Args;
            if (a == null || string.IsNullOrEmpty(a.Label))
            {
                ctx.Refuse(TestCommandPartParitySweep.LabelArgMissingReason, "label=");
                yield break;
            }
            ctx.LabelDir = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty,
                TestCommandPartParitySweep.RootDirName), a.Label);

            if (a.Op == PartParitySweepOp.Reset)
            {
                DoReset(ctx);
                yield break;
            }

            IEnumerator unpack = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpack.MoveNext()) yield return unpack.Current;
            if (!AdmitHost(ctx)) yield break;

            PlanStep(ctx);
            IEnumerator waves = RunWaves(ctx);
            while (waves.MoveNext()) yield return waves.Current;
            FinishStep(ctx);
        }

        private static void DoReset(StepContext ctx)
        {
            int removed = 0;
            if (Directory.Exists(ctx.LabelDir))
            {
                removed = Directory.GetFiles(ctx.LabelDir, "*", SearchOption.AllDirectories).Length;
                Directory.Delete(ctx.LabelDir, true);
            }
            Directory.CreateDirectory(ctx.LabelDir);
            ParsekLog.Info(Tag, $"reset: label={ctx.Args.Label} dir={ctx.LabelDir} removedFiles={removed.ToString(Ic)}");
            ctx.Succeed(false, 0, 0, 0, 0, 0, TestCommandPartParitySweep.ControlsState(ctx.Args.Controls, null));
        }

        private static bool AdmitHost(StepContext ctx)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            bool orbiting = active != null && active.situation == Vessel.Situations.ORBITING && active.orbit != null;
            string refusal = TestCommandPartParitySweep.DecideHostAdmission(active != null, orbiting);
            if (refusal != null)
            {
                ctx.Refuse(refusal, active == null ? "no active vessel" : "situation=" + active.situation);
                return false;
            }
            ctx.Host = active;
            return true;
        }

        private static void PlanStep(StepContext ctx)
        {
            Directory.CreateDirectory(ctx.LabelDir);
            ctx.Entries = PartParityCaseAuthoring.EnumerateParts(ctx.Exclusions);
            var byKey = new Dictionary<string, PartParityPartEntry>(StringComparer.Ordinal);
            var keys = new List<PartParityCaseKey>();
            foreach (var e in ctx.Entries)
            {
                ctx.EntriesByName[e.Name] = e;
                keys.Add(PartParityCaseRules.DefaultCaseKey(e));
            }
            ctx.Planned = TestCommandPartParitySweep.SelectCases(keys, ctx.Args);
            ctx.StaleInflight = ReadStaleInflight(ctx.LabelDir);
            foreach (var p in ctx.Planned) LoadKnownSidecar(ctx, p.CaseId);
            if (ctx.Args.Controls)
                foreach (string id in TestCommandPartParitySweep.ControlIds) LoadKnownSidecar(ctx, id);
            ParsekLog.Info(Tag, $"step start: label={ctx.Args.Label} op=run parts={ctx.Entries.Count.ToString(Ic)} " +
                $"excluded={ctx.Exclusions.Count.ToString(Ic)} planned={ctx.Planned.Count.ToString(Ic)} " +
                $"knownSidecars={ctx.Known.Count.ToString(Ic)} controls={(ctx.Args.Controls ? "true" : "false")} " +
                $"filter={ctx.Args.Filter} shard={ctx.Args.ShardK.ToString(Ic)}/{ctx.Args.ShardN.ToString(Ic)} " +
                $"tile={ctx.Args.Tile.ToString(Ic)} views={string.Join(",", ctx.Args.Views.ToArray())} " +
                $"maxSeconds={ctx.Args.MaxSeconds.ToString(Ic)} staleInflight={ctx.StaleInflight ?? "<none>"}");
        }

        private static string ReadStaleInflight(string labelDir)
        {
            string path = Path.Combine(labelDir, TestCommandPartParitySweep.InflightFileName);
            if (!File.Exists(path)) return null;
            string id = TestCommandPartParitySweep.ParseInflight(File.ReadAllText(path));
            File.Delete(path);
            ParsekLog.Warn(Tag, $"stale inflight.txt names case={id ?? "<unreadable>"}: the previous process died mid-case");
            return id;
        }

        private static void LoadKnownSidecar(StepContext ctx, string caseId)
        {
            string path = Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.SidecarFileName(caseId));
            if (!File.Exists(path)) return;
            try
            {
                ctx.Known[caseId] = PartParityJsonReader.ReadCaseSidecar(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"sidecar {caseId}: unreadable ({ex.GetType().Name}: {ex.Message}); the case reruns");
            }
        }

        // =====================================================================================
        // waves
        // =====================================================================================

        private static IEnumerator RunWaves(StepContext ctx)
        {
            var queue = BuildQueue(ctx);
            ParsekLog.Info(Tag, $"queue: cases={queue.Count.ToString(Ic)} (controls first)");
            if (queue.Count == 0) yield break;
            ctx.Capture = PartParityCapture.Create(ctx.Args.Tile);
            int cursor = 0;
            double lastWave = 0.0;
            while (cursor < queue.Count)
            {
                double elapsed = Time.realtimeSinceStartup - ctx.StartRealtime;
                if (!TestCommandPartParitySweep.ShouldStartWave(elapsed, ctx.Args.MaxSeconds, lastWave))
                {
                    ParsekLog.Info(Tag, $"time box: elapsed={elapsed.ToString("0.0", Ic)}s lastWave={lastWave.ToString("0.0", Ic)}s " +
                        $"maxSeconds={ctx.Args.MaxSeconds.ToString(Ic)}; {(queue.Count - cursor).ToString(Ic)} case(s) left for the next step");
                    yield break;
                }
                List<WaveCase> wave = TakeWave(queue, ref cursor);
                float waveStart = Time.realtimeSinceStartup;
                IEnumerator w = RunWave(ctx, wave);
                while (w.MoveNext()) yield return w.Current;
                lastWave = Time.realtimeSinceStartup - waveStart;
                ctx.WavesDone++;
                LogWave(ctx, wave, lastWave);
                if (PartParitySweepPlan.ShouldUnloadAfterWave(ctx.WavesDone))
                {
                    yield return Resources.UnloadUnusedAssets();
                    LogMemory(ctx);
                }
            }
        }

        /// <summary>Every case this step still has to run, controls first, in the stable
        /// order; crash records are written here (the resume rule).</summary>
        private static List<WaveCase> BuildQueue(StepContext ctx)
        {
            var queue = new List<WaveCase>();
            if (ctx.Args.Controls)
            {
                foreach (string id in TestCommandPartParitySweep.ControlIds)
                {
                    WaveCase wc = ResumeOrQueue(ctx, id, null, () => BuildControlCase(ctx, id));
                    if (wc != null) queue.Add(wc);
                }
            }
            foreach (var p in ctx.Planned)
            {
                WaveCase wc = ResumeOrQueue(ctx, p.CaseId, p.Key, () => BuildPartCase(ctx, p));
                if (wc != null) queue.Add(wc);
            }
            return queue;
        }

        private static WaveCase ResumeOrQueue(StepContext ctx, string caseId, PartParityCaseKey key, Func<WaveCase> build)
        {
            var decision = TestCommandPartParitySweep.DecideCaseResume(caseId, ctx.Known.ContainsKey(caseId), ctx.StaleInflight);
            if (decision == PartParityCaseResume.SkipComplete) return null;
            if (decision == PartParityCaseResume.RecordCrashed)
            {
                var k = key ?? new PartParityCaseKey { Part = caseId };
                PartParityCaseSidecar crashed = PartParityJson.BuildCrashedSidecar(k, caseId,
                    "a previous process died while this case was in flight (stale inflight.txt)");
                WriteSidecar(ctx, crashed);
                ParsekLog.Warn(Tag, $"case {caseId}: setup fault crashed (recorded from the stale inflight.txt, skipped)");
                return null;
            }
            return build();
        }

        private static WaveCase BuildPartCase(StepContext ctx, PartParityPlannedCase p)
        {
            ctx.EntriesByName.TryGetValue(p.Key.Part, out PartParityPartEntry e);
            return new WaveCase
            {
                CaseId = p.CaseId, Index = p.Index, Key = p.Key, Entry = e,
                RealVariant = e?.BaseVariant, Views = ctx.Args.Views,
            };
        }

        private static List<WaveCase> TakeWave(List<WaveCase> queue, ref int cursor)
        {
            var sizes = new List<double>();
            for (int i = cursor; i < queue.Count && sizes.Count < PartParitySweepPlan.MaxWaveCases; i++)
                sizes.Add(queue[i].Entry != null ? queue[i].Entry.SizeMeters : 0.0);
            List<double> offsets = PartParitySweepPlan.PlanWave(sizes);
            var wave = new List<WaveCase>();
            for (int i = 0; i < offsets.Count; i++)
            {
                WaveCase wc = queue[cursor + i];
                wc.AlongTrackM = offsets[i];
                wave.Add(wc);
            }
            cursor += wave.Count;
            return wave;
        }

        private static IEnumerator RunWave(StepContext ctx, List<WaveCase> wave)
        {
            ctx.Live = wave;
            WriteInflight(ctx, wave[0].CaseId);
            foreach (var wc in wave) Guard(ctx, wc, "spawn", () => SpawnCase(ctx, wc));
            IEnumerator ready = WaitReady(wave);
            while (ready.MoveNext()) yield return ready.Current;
            IEnumerator settle = WaitSettled(wave);
            while (settle.MoveNext()) yield return settle.Current;

            foreach (var wc in wave)
            {
                WriteInflight(ctx, wc.CaseId);
                IEnumerator one = RunCaseCapture(ctx, wc);
                while (one.MoveNext()) yield return one.Current;
                Guard(ctx, wc, "write", () => FinishCase(ctx, wc));
                DeleteInflight(ctx);
            }
            IEnumerator down = TearDownWave(ctx, wave);
            while (down.MoveNext()) yield return down.Current;
            ctx.Live = null;
        }

        /// <summary>Runs one synchronous stage of a case; an exception becomes a case flag and
        /// (before capture) the case's setup detail, never a step failure.</summary>
        private static void Guard(StepContext ctx, WaveCase wc, string stage, Action body)
        {
            try { body(); }
            catch (Exception ex)
            {
                string what = stage + ": " + ex.GetType().Name + ": " + ex.Message;
                wc.Flags.Add("exception-" + stage);
                if (wc.Detail == null) wc.Detail = what;
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: {what}");
                // A spawn that threw never produced a vessel; a prepare that threw broke the
                // instrument on this part (the in-process cousin of a crash). Either way the
                // case is not captured; a throw in one view or in the write keeps the rest.
                if (stage == "spawn") wc.Status = wc.Status ?? TestCommandPartParitySweep.SetupSpawnTimeout;
                else if (stage == "prepare") wc.Status = wc.Status ?? TestCommandPartParitySweep.SetupCrashed;
            }
        }

        private static void SpawnCase(StepContext ctx, WaveCase wc)
        {
            if (wc.Entry == null || wc.Entry.Info == null)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = wc.Detail ?? "part not loaded";
                return;
            }
            ConfigNode node = PartParityCaseAuthoring.AuthorVesselNode(wc.Entry.Info, wc.RealVariant, wc.CaseId, ctx.Host);
            wc.Pid = GhostCoOrbitalRig.SpawnCoOrbital(node, ctx.Host, wc.AlongTrackM, wc.CaseId, Tag);
            wc.SpawnRealtime = Time.realtimeSinceStartup;
            if (wc.Pid == 0)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = "SpawnAtPosition returned pid=0";
            }
        }

        private static IEnumerator WaitReady(List<WaveCase> wave)
        {
            while (true)
            {
                bool pending = false;
                foreach (var wc in wave)
                {
                    if (wc.Status != null || wc.Real != null) continue;
                    Vessel v = FlightRecorder.FindVesselByPid(wc.Pid);
                    if (v != null && v.loaded && !v.packed && v.parts != null && v.parts.Count > 0 && v.rootPart != null)
                    {
                        wc.Real = v;
                        wc.ReadyRealtime = Time.realtimeSinceStartup;
                        continue;
                    }
                    if (Time.realtimeSinceStartup - wc.SpawnRealtime > PartParitySweepPlan.SpawnTimeoutSeconds)
                    {
                        wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                        wc.Detail = "never loaded+unpacked in " + PartParitySweepPlan.SpawnTimeoutSeconds.ToString("0", Ic) + " s";
                        continue;
                    }
                    pending = true;
                }
                if (!pending) yield break;
                yield return null;
            }
        }

        private static IEnumerator WaitSettled(List<WaveCase> wave)
        {
            while (true)
            {
                bool pending = false;
                foreach (var wc in wave)
                {
                    if (wc.Status != null || wc.Real == null || wc.Settled) continue;
                    if (wc.Real.state == Vessel.State.DEAD || wc.Real.rootPart == null)
                    {
                        wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                        wc.Detail = "vessel died while settling";
                        continue;
                    }
                    var d = PartParitySweepPlan.DecideSettle(Time.realtimeSinceStartup - wc.ReadyRealtime, AnyAnimationPlaying(wc.Real.rootPart));
                    if (d == PartParitySettle.Wait) { pending = true; continue; }
                    wc.Settled = true;
                    wc.StillAnimating = d == PartParitySettle.StillAnimating;
                }
                if (!pending) yield break;
                yield return null;
            }
        }

        private static bool AnyAnimationPlaying(Part part)
        {
            foreach (Animation an in part.GetComponentsInChildren<Animation>(true))
                if (an != null && an.isPlaying) return true;
            return false;
        }

        private static IEnumerator TearDownWave(StepContext ctx, List<WaveCase> wave)
        {
            foreach (var wc in wave) DestroyCase(wc);
            float deadline = Time.realtimeSinceStartup + (float)PartParitySweepPlan.VesselGoneTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                bool any = false;
                foreach (var wc in wave)
                {
                    if (wc.Pid == 0) continue;
                    Vessel v = FlightRecorder.FindVesselByPid(wc.Pid);
                    if (v != null && v.state != Vessel.State.DEAD) { any = true; break; }
                }
                if (!any) yield break;
                yield return null;
            }
            ParsekLog.Warn(Tag, $"wave teardown: spawned vessels still present after {PartParitySweepPlan.VesselGoneTimeoutSeconds.ToString("0", Ic)} s");
        }

        /// <summary>Destroys the case's ghost and kills its spawned vessel (idempotent; the
        /// step's cleanup calls it again for a wave an exception cut short).</summary>
        internal static void DestroyCase(WaveCase wc)
        {
            if (wc == null) return;
            try { GhostCoOrbitalRig.DestroyLiveGhost(wc.Ghost); }
            catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: ghost destroy threw {ex.GetType().Name}: {ex.Message}"); }
            wc.Ghost = null;
            Vessel v = wc.Real ?? (wc.Pid != 0 ? FlightRecorder.FindVesselByPid(wc.Pid) : null);
            if (v != null && v != FlightGlobals.ActiveVessel && v.state != Vessel.State.DEAD)
            {
                try { v.Die(); }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: Die threw {ex.GetType().Name}: {ex.Message}"); }
            }
            wc.Real = null;
        }

        // =====================================================================================
        // one case
        // =====================================================================================

        private static IEnumerator RunCaseCapture(StepContext ctx, WaveCase wc)
        {
            if (wc.Status != null) yield break;
            BeginCaseLog(ctx, wc);
            try
            {
                Guard(ctx, wc, "prepare", () => PartParityCaseCapture.Prepare(ctx, wc));
                if (wc.Status != null || wc.Shared == null) yield break;
                foreach (string view in wc.Views)
                {
                    yield return new WaitForEndOfFrame();
                    if (wc.Real == null || wc.Real.rootPart == null || wc.Real.state == Vessel.State.DEAD)
                    {
                        wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                        wc.Detail = "vessel lost mid-capture";
                        yield break;
                    }
                    Guard(ctx, wc, "capture-" + view, () => PartParityCaseCapture.CaptureView(ctx, wc, view));
                }
            }
            finally
            {
                EndCaseLog(ctx);
            }
        }

        private static void BeginCaseLog(StepContext ctx, WaveCase wc)
        {
            Action<string> prior = ParsekLog.TestObserverForTesting;
            ctx.PriorObserver = prior;
            ctx.ObserverInstalled = true;
            ParsekLog.TestObserverForTesting = line =>
            {
                if (wc.Log.Count < MaxCaseLogLines) wc.Log.Add(line);
                prior?.Invoke(line);
            };
        }

        private static void EndCaseLog(StepContext ctx)
        {
            if (!ctx.ObserverInstalled) return;
            ParsekLog.TestObserverForTesting = ctx.PriorObserver;
            ctx.PriorObserver = null;
            ctx.ObserverInstalled = false;
        }

        private static void FinishCase(StepContext ctx, WaveCase wc)
        {
            PartParityCaseSidecar sidecar = PartParityCaseCapture.BuildSidecar(ctx, wc);
            if (wc.RealTiles.Count > 0)
                PartParityCaseCapture.WritePictures(ctx, wc, sidecar);
            WriteSidecar(ctx, sidecar);
            if (wc.ControlId != null) JudgeAndLogControl(sidecar);
            string status = sidecar.Setup?.Status ?? TestCommandPartParitySweep.SetupOk;
            if (TestCommandPartParitySweep.IsSetupFault(status))
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: setup fault {status} detail={sidecar.Setup?.Detail ?? "<none>"}");
            ParsekLog.Verbose(Tag, PartParityCaseCapture.DescribeCase(wc, sidecar));
        }

        private static void JudgeAndLogControl(PartParityCaseSidecar s)
        {
            string outcome = PartParitySweepPlan.JudgeControl(s.CaseId, s.Views, s.Setup?.Status, out string detail);
            ParsekLog.Info(Tag, $"control {s.CaseId}: outcome={outcome} subject={s.Part ?? "<none>"} detail={detail ?? "<none>"}");
        }

        internal static void WriteSidecar(StepContext ctx, PartParityCaseSidecar sidecar)
        {
            string path = Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.SidecarFileName(sidecar.CaseId));
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(PartParityJson.WriteCase(sidecar)), path, Tag);
            ctx.Known[sidecar.CaseId] = sidecar;
        }

        private static void WriteInflight(StepContext ctx, string caseId)
        {
            File.WriteAllText(Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.InflightFileName),
                TestCommandPartParitySweep.FormatInflight(caseId));
        }

        private static void DeleteInflight(StepContext ctx)
        {
            string path = Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.InflightFileName);
            if (File.Exists(path)) File.Delete(path);
        }

        // =====================================================================================
        // controls
        // =====================================================================================

        private static WaveCase BuildControlCase(StepContext ctx, string id)
        {
            var wc = new WaveCase
            {
                CaseId = id, Index = -1, ControlId = id,
                Views = new List<string>(TestCommandPartParitySweep.RequiredViews),
            };
            string part = ControlPart(ctx, id, out string ghostVariant, out string why);
            wc.Key = new PartParityCaseKey { Part = part ?? why };
            if (part != null && ctx.EntriesByName.TryGetValue(part, out PartParityPartEntry e))
            {
                wc.Entry = e;
                wc.RealVariant = e.BaseVariant;
                wc.Key.Skin = e.BaseVariant ?? TestCommandPartParitySweep.DefaultSkinName;
            }
            wc.GhostVariant = ghostVariant;
            if (wc.Entry == null)
                wc.Detail = "control subject not loaded: " + (part ?? why);
            return wc;
        }

        private static string ControlPart(StepContext ctx, string id, out string ghostVariant, out string why)
        {
            ghostVariant = null;
            why = null;
            switch (id)
            {
                case TestCommandPartParitySweep.ControlGhostRot90:
                case TestCommandPartParitySweep.ControlGhostFlip:
                    return PartParitySweepPlan.ControlPodPart;
                case TestCommandPartParitySweep.ControlGhostMissingMesh:
                    foreach (string n in PartParitySweepPlan.MissingMeshCandidates)
                        if (ctx.EntriesByName.TryGetValue(n, out var e) && CountMeshRenderers(e.Info) >= PartParitySweepPlan.MinMissingMeshRenderers)
                            return n;
                    why = "no multi-renderer candidate";
                    return null;
                case TestCommandPartParitySweep.ControlWrongSkin:
                    foreach (string n in PartParitySweepPlan.WrongSkinCandidates)
                    {
                        if (!ctx.EntriesByName.TryGetValue(n, out var e) || e.BaseVariant == null) continue;
                        ModulePartVariants mv = e.Info.partPrefab.FindModuleImplementing<ModulePartVariants>();
                        ghostVariant = FairingGhostVisualMath.ChooseColourVariant(mv != null ? mv.GetVariantNames() : null, e.BaseVariant);
                        if (ghostVariant != null) return n;
                    }
                    why = "no colour-variant candidate";
                    return null;
                default:
                    return PartParitySweepPlan.ControlTankPart;
            }
        }

        private static int CountMeshRenderers(AvailablePart info)
            => info?.partPrefab == null ? 0 : info.partPrefab.GetComponentsInChildren<MeshRenderer>(true).Length;

        // =====================================================================================
        // step end
        // =====================================================================================

        private static void FinishStep(StepContext ctx)
        {
            var completeFlags = new List<bool>();
            int complete = 0, crashed = 0, faults = 0;
            foreach (var p in ctx.Planned)
            {
                bool has = ctx.Known.TryGetValue(p.CaseId, out PartParityCaseSidecar s);
                completeFlags.Add(has);
                if (!has) continue;
                complete++;
                string status = s.Setup?.Status ?? TestCommandPartParitySweep.SetupOk;
                if (status == TestCommandPartParitySweep.SetupCrashed) crashed++;
                if (TestCommandPartParitySweep.IsSetupFault(status)) faults++;
            }
            var outcomes = JudgeAllControls(ctx);
            string controls = TestCommandPartParitySweep.ControlsState(ctx.Args.Controls, ConvertOutcomes(outcomes));
            bool controlsDone = !ctx.Args.Controls || AllControlsRan(ctx);
            int next = PartParitySweepPlan.NextIncomplete(completeFlags);
            bool done = next >= ctx.Planned.Count && controlsDone;
            PartParityManifestBuilder.Write(ctx, outcomes, done, complete, faults);
            ctx.Succeed(done, next, ctx.Planned.Count, complete, crashed, faults, controls);
            ParsekLog.Verbose(Tag, $"step end: waves={ctx.WavesDone.ToString(Ic)} (the step line follows)");
        }

        private static List<PartParityControlRecord> JudgeAllControls(StepContext ctx)
        {
            var list = new List<PartParityControlRecord>();
            if (!ctx.Args.Controls) return list;
            foreach (string id in TestCommandPartParitySweep.ControlIds)
            {
                var rec = new PartParityControlRecord
                {
                    Id = id, Subject = PartParitySweepPlan.ControlSubject(id),
                    Required = PartParitySweepPlan.ControlRequired(id),
                    Outcome = TestCommandPartParitySweep.ControlsPending,
                };
                if (ctx.Known.TryGetValue(id, out PartParityCaseSidecar s))
                {
                    rec.Outcome = PartParitySweepPlan.JudgeControl(id, s.Views, s.Setup?.Status, out string detail);
                    rec.Detail = (s.Part != null ? "part=" + s.Part + " " : string.Empty) + (detail ?? string.Empty);
                }
                list.Add(rec);
            }
            return list;
        }

        private static List<string> ConvertOutcomes(List<PartParityControlRecord> records)
        {
            var o = new List<string>();
            foreach (var r in records) o.Add(r.Outcome);
            return o;
        }

        private static bool AllControlsRan(StepContext ctx)
        {
            foreach (string id in TestCommandPartParitySweep.ControlIds)
                if (!ctx.Known.ContainsKey(id)) return false;
            return true;
        }

        private static void LogWave(StepContext ctx, List<WaveCase> wave, double wall)
        {
            int faults = 0, animating = 0;
            foreach (var wc in wave)
            {
                if (TestCommandPartParitySweep.IsSetupFault(PartParityCaseCapture.ResolveStatus(wc))) faults++;
                if (wc.StillAnimating) animating++;
            }
            ParsekLog.Info(Tag, $"wave {ctx.WavesDone.ToString(Ic)}: cases={wave.Count.ToString(Ic)} setupFaults={faults.ToString(Ic)} " +
                $"stillAnimating={animating.ToString(Ic)} wall={wall.ToString("0.0", Ic)}s first={wave[0].CaseId} " +
                $"elapsed={(Time.realtimeSinceStartup - ctx.StartRealtime).ToString("0.0", Ic)}s");
        }

        private static void LogMemory(StepContext ctx)
        {
            ParsekLog.Info(Tag, $"memory: waves={ctx.WavesDone.ToString(Ic)} gcBytes={GC.GetTotalMemory(false).ToString(Ic)} " +
                $"unityAllocated={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong().ToString(Ic)} " +
                $"textureMemory={Texture.currentTextureMemory.ToString(Ic)} (after UnloadUnusedAssets)");
        }

        // =====================================================================================
        // context
        // =====================================================================================

        /// <summary>One case of a wave, from spawn to sidecar.</summary>
        internal sealed class WaveCase
        {
            internal string CaseId;
            internal int Index;
            internal PartParityCaseKey Key;
            internal PartParityPartEntry Entry;
            internal string ControlId;
            internal string RealVariant;
            internal string GhostVariant;
            internal List<string> Views;
            internal double AlongTrackM;

            internal uint Pid;
            internal float SpawnRealtime;
            internal float ReadyRealtime;
            internal Vessel Real;
            internal bool Settled;
            internal bool StillAnimating;

            /// <summary>A setup fault status, null while the case is healthy.</summary>
            internal string Status;
            internal string Detail;

            internal GhostCoOrbitalRig.LiveGhost Ghost;
            internal List<Renderer> GhostRenderers = new List<Renderer>();
            internal List<PartParityRendererRecord> RealRecords;
            internal List<PartParityRendererRecord> GhostRecords;
            internal PartParityFramingDecision Framing;
            internal PartParityAabb Shared;
            internal double OrthoSize;
            internal int CullingMask;
            internal SortedSet<int> RealLayers = new SortedSet<int>();
            internal SortedSet<int> GhostLayers = new SortedSet<int>();
            internal double? GhostRootOffsetM;
            internal int SeedEvents;
            internal int SeedApplied;
            internal PartParitySkinProof Skin;

            internal List<string> CapturedViews = new List<string>();
            internal List<byte[]> RealTiles = new List<byte[]>();
            internal List<byte[]> GhostTiles = new List<byte[]>();
            internal List<PartParityViewRecord> ViewRecords = new List<PartParityViewRecord>();
            internal List<string> Flags = new List<string>();
            internal List<string> Log = new List<string>();
        }

        /// <summary>Everything one step owns; never shared between steps.</summary>
        internal sealed class StepContext
        {
            internal readonly PartParitySweepArgs Args;
            internal readonly float StartRealtime = Time.realtimeSinceStartup;
            internal string LabelDir;
            internal Vessel Host;
            internal List<PartParityPartEntry> Entries = new List<PartParityPartEntry>();
            internal readonly Dictionary<string, PartParityPartEntry> EntriesByName = new Dictionary<string, PartParityPartEntry>(StringComparer.Ordinal);
            internal readonly List<PartParityExclusion> Exclusions = new List<PartParityExclusion>();
            internal List<PartParityPlannedCase> Planned = new List<PartParityPlannedCase>();
            internal readonly Dictionary<string, PartParityCaseSidecar> Known = new Dictionary<string, PartParityCaseSidecar>(StringComparer.Ordinal);
            internal string StaleInflight;
            internal PartParityCapture Capture;
            internal List<WaveCase> Live;
            internal int WavesDone;
            internal Action<string> PriorObserver;
            internal bool ObserverInstalled;
            internal PartParitySweepStepResult Result;

            internal StepContext(PartParitySweepArgs args) { Args = args; }

            internal void Refuse(string reason, string detail)
            {
                Result = new PartParitySweepStepResult { Ok = false, Reason = reason, Detail = detail };
                ParsekLog.Info(Tag, $"step refused: reason={reason} detail={detail ?? "<none>"}");
            }

            internal void Succeed(bool done, int next, int cases, int complete, int crashed, int faults, string controls)
            {
                double wall = Time.realtimeSinceStartup - StartRealtime;
                Result = new PartParitySweepStepResult
                {
                    Ok = true,
                    Payload = TestCommandPartParitySweep.BuildPayload(Args.Label, done, next, cases, complete, crashed, faults, controls, wall),
                };
            }

            internal void FailWithException(Exception ex)
            {
                Result = new PartParitySweepStepResult
                {
                    Ok = false, Reason = StepExceptionReason, Detail = ex.GetType().Name + ": " + ex.Message,
                };
                ParsekLog.Warn(Tag, $"step failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            /// <summary>Idempotent: restores the log observer, destroys a wave an exception cut
            /// short, releases the capture camera and targets.</summary>
            internal void Cleanup()
            {
                if (ObserverInstalled)
                {
                    ParsekLog.TestObserverForTesting = PriorObserver;
                    ObserverInstalled = false;
                }
                if (Live != null)
                {
                    foreach (var wc in Live) DestroyCase(wc);
                    Live = null;
                }
                if (Capture != null)
                {
                    Capture.Dispose();
                    Capture = null;
                }
            }
        }

        /// <summary>SHA-256 of a file as lower-case hex, null when unreadable.</summary>
        internal static string FileSha256(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (var sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(File.ReadAllBytes(path));
                    var sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2", Ic));
                    return sb.ToString();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
