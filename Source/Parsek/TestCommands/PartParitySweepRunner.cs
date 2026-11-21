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
        // calls onDone exactly once when it runs to its end, never throws out of the coroutine.
        // A caller that abandons the step (the applier's timeout) DISPOSES this iterator, which
        // runs only the finally below: the nested iterators are disposed first (their own
        // finally blocks run, inside out), then everything the step owns is released. onDone is
        // not called on that path; the caller has already fenced the step.
        internal static IEnumerator RunStep(PartParitySweepArgs args, Action<PartParitySweepStepResult> onDone)
        {
            var ctx = new StepContext(args);
            IEnumerator core = StepCore(ctx);
            bool ranToEnd = false;
            try
            {
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
                ranToEnd = true;
            }
            finally
            {
                DisposeNested(core);
                string released = ctx.Cleanup();
                if (!ranToEnd)
                    ParsekLog.Warn(Tag, StepAbandonedLine(args, released));
                else
                    ParsekLog.Verbose(Tag, "step cleanup: " + released);
            }
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

        /// <summary>The literal of the one Warn line a step stopped before its end writes.</summary>
        internal const string StepAbandonedLiteral = "PartParity step abandoned";

        /// <summary>The Warn line of a step disposed before its end (the applier's timeout):
        /// what the cleanup released, and that a left <c>inflight.txt</c> stands for a crash.</summary>
        internal static string StepAbandonedLine(PartParitySweepArgs args, string released)
            => StepAbandonedLiteral + " label=" + (args?.Label ?? string.Empty)
               + " op=" + (args != null && args.Op == PartParitySweepOp.Reset ? "reset" : "run")
               + ": stopped before its end, cleanup ran (" + (released ?? "<none>") + "); an inflight.txt left "
               + "behind is read by the next step as a crash of the case(s) it names";

        /// <summary>Disposes a hand-driven nested iterator so its pending finally blocks run
        /// (a compiler-generated iterator runs them on Dispose); never throws.</summary>
        internal static void DisposeNested(IEnumerator nested)
        {
            if (!(nested is IDisposable d)) return;
            try { d.Dispose(); }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"nested iterator Dispose threw {ex.GetType().Name}: {ex.Message}");
            }
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
            try { while (unpack.MoveNext()) yield return unpack.Current; }
            finally { DisposeNested(unpack); }
            if (!AdmitHost(ctx)) yield break;

            PlanStep(ctx);
            IEnumerator waves = RunWaves(ctx);
            try { while (waves.MoveNext()) yield return waves.Current; }
            finally { DisposeNested(waves); }
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
            foreach (var p in ctx.Planned) LoadKnownSidecar(ctx, p.CaseId);
            if (ctx.Args.Controls)
            {
                foreach (string id in TestCommandPartParitySweep.ControlIds) LoadKnownSidecar(ctx, id);
                PlanMissingMeshFamily(ctx);
                foreach (var sub in ctx.MissingMeshFamily) LoadKnownSidecar(ctx, sub.SubCaseId);
            }
            var keysById = new Dictionary<string, PartParityCaseKey>(StringComparer.Ordinal);
            foreach (var k in keys) keysById[TestCommandPartParitySweep.CaseId(k)] = k;
            ResolveStaleInflight(ctx, keysById);
            ParsekLog.Info(Tag, $"step start: label={ctx.Args.Label} op=run parts={ctx.Entries.Count.ToString(Ic)} " +
                $"excluded={ctx.Exclusions.Count.ToString(Ic)} planned={ctx.Planned.Count.ToString(Ic)} " +
                $"knownSidecars={ctx.Known.Count.ToString(Ic)} controls={(ctx.Args.Controls ? "true" : "false")} " +
                $"filter={ctx.Args.Filter} shard={ctx.Args.ShardK.ToString(Ic)}/{ctx.Args.ShardN.ToString(Ic)} " +
                $"tile={ctx.Args.Tile.ToString(Ic)} views={string.Join(",", ctx.Args.Views.ToArray())} " +
                $"maxSeconds={ctx.Args.MaxSeconds.ToString(Ic)} staleInflight={ctx.StaleInflight ?? "<none>"} " +
                $"isolate={ctx.Isolate.Count.ToString(Ic)}");
        }

        /// <summary>
        /// The stale-inflight rule at step start (design 5.3), over the WHOLE install, not
        /// only this step's selection: the crash record (one name) or the isolation list
        /// (several names) is written to the label directory FIRST, and <c>inflight.txt</c> is
        /// deleted only after both are on disk, so a stale case outside this step's limit,
        /// filter, shard or controls keeps its record. A write that throws fails the step and
        /// leaves <c>inflight.txt</c> for the next one. Then loads the isolation list.
        /// </summary>
        private static void ResolveStaleInflight(StepContext ctx, Dictionary<string, PartParityCaseKey> keysById)
        {
            List<string> isolate = ReadIsolate(ctx.LabelDir);
            string path = Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.InflightFileName);
            if (File.Exists(path))
            {
                PartParityInflight inflight = TestCommandPartParitySweep.ParseInflight(File.ReadAllText(path));
                var complete = new HashSet<string>(StringComparer.Ordinal);
                if (inflight != null)
                    foreach (string id in inflight.CaseIds)
                    {
                        if (!ctx.Known.ContainsKey(id)) LoadKnownSidecar(ctx, id);
                        if (ctx.Known.ContainsKey(id)) complete.Add(id);
                    }
                PartParityInflightDecision d = TestCommandPartParitySweep.DecideInflight(inflight, complete);
                string named = inflight == null ? "<unreadable>" : string.Join(",", inflight.CaseIds.ToArray());
                bool solo = inflight != null && inflight.Solo;
                if (d.CrashedCaseId != null)
                {
                    keysById.TryGetValue(d.CrashedCaseId, out PartParityCaseKey key);
                    var k = key ?? new PartParityCaseKey { Part = d.CrashedCaseId };
                    WriteSidecar(ctx, PartParityJson.BuildCrashedSidecar(k, d.CrashedCaseId,
                        solo
                            ? "a previous process died while this case ran alone (isolation rerun, stale inflight.txt)"
                            : "a previous process died while this case was in flight (stale inflight.txt)"));
                    ParsekLog.Warn(Tag, $"case {d.CrashedCaseId}: setup fault crashed (recorded from the stale inflight.txt, " +
                        $"solo={(solo ? "true" : "false")}, skipped)");
                }
                if (d.Isolate.Count > 0)
                    ParsekLog.Warn(Tag, $"stale inflight.txt names {inflight.CaseIds.Count.ToString(Ic)} cases ({named}): the previous " +
                        $"process died during a wave's spawn or settle; no case blamed, {d.Isolate.Count.ToString(Ic)} case(s) " +
                        "re-queued to run alone");
                isolate = TestCommandPartParitySweep.MergeIsolate(isolate, d);
                WriteIsolate(ctx.LabelDir, isolate);
                File.Delete(path);
                ParsekLog.Info(Tag, $"stale inflight.txt resolved: names={named} solo={(solo ? "true" : "false")} " +
                    $"crashed={d.CrashedCaseId ?? "<none>"} isolated={d.Isolate.Count.ToString(Ic)} " +
                    $"alreadyComplete={d.Finished.Count.ToString(Ic)} (records written before the delete)");
                ctx.StaleInflight = named;
            }
            foreach (string id in isolate)
            {
                if (ctx.Isolate.Contains(id)) continue;
                ctx.Isolate.Add(id);
                ctx.IsolateOrder.Add(id);
            }
        }

        private static List<string> ReadIsolate(string labelDir)
        {
            string path = Path.Combine(labelDir, TestCommandPartParitySweep.IsolateFileName);
            return File.Exists(path)
                ? TestCommandPartParitySweep.ParseCaseIdLines(File.ReadAllText(path))
                : new List<string>();
        }

        /// <summary>Writes the isolation list atomically, or deletes the file when it is empty.</summary>
        private static void WriteIsolate(string labelDir, IList<string> isolate)
        {
            string path = Path.Combine(labelDir, TestCommandPartParitySweep.IsolateFileName);
            if (isolate == null || isolate.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(TestCommandPartParitySweep.FormatCaseIdLines(isolate)), path, Tag);
        }

        /// <summary>A case owed a solo run has just finished (its sidecar is on disk): drop it
        /// from the isolation list on disk.</summary>
        private static void ClearIsolated(StepContext ctx, string caseId)
        {
            if (!ctx.Isolate.Remove(caseId)) return;
            ctx.IsolateOrder.Remove(caseId);
            WriteIsolate(ctx.LabelDir, ctx.IsolateOrder);
            ParsekLog.Info(Tag, $"case {caseId}: solo rerun finished; {ctx.IsolateOrder.Count.ToString(Ic)} case(s) still owed a solo run");
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
                try { while (w.MoveNext()) yield return w.Current; }
                finally { DisposeNested(w); }
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

        /// <summary>Every case this step still has to run: the cases owed a solo run first
        /// (one wave each, so the culprit of a multi-case crash is found before anything else
        /// runs), then the rest; within each group controls first, in the stable order. The
        /// crash records were written at step start (the stale-inflight rule), so a crashed
        /// case reads as complete here.</summary>
        private static List<WaveCase> BuildQueue(StepContext ctx)
        {
            var solo = new List<WaveCase>();
            var normal = new List<WaveCase>();
            if (ctx.Args.Controls)
            {
                foreach (string id in TestCommandPartParitySweep.ControlIds)
                {
                    if (id != TestCommandPartParitySweep.ControlGhostMissingMesh)
                    {
                        ResumeOrQueue(ctx, id, () => BuildControlCase(ctx, id), solo, normal);
                        continue;
                    }
                    foreach (var sub in ctx.MissingMeshFamily)
                    {
                        PartParityMissingMeshSubject member = sub;
                        ResumeOrQueue(ctx, member.SubCaseId, () => BuildMissingMeshSubCase(ctx, member), solo, normal);
                    }
                    if (ctx.MissingMeshFamily.Count == 0) TryLogMissingMeshFamily(ctx);
                }
            }
            foreach (var p in ctx.Planned)
                ResumeOrQueue(ctx, p.CaseId, () => BuildPartCase(ctx, p), solo, normal);
            if (solo.Count > 0)
                ParsekLog.Info(Tag, $"queue: {solo.Count.ToString(Ic)} case(s) owed a solo run go first, one per wave " +
                    $"({string.Join(",", solo.ConvertAll(c => c.CaseId).ToArray())})");
            solo.AddRange(normal);
            return solo;
        }

        private static void ResumeOrQueue(StepContext ctx, string caseId, Func<WaveCase> build,
            List<WaveCase> solo, List<WaveCase> normal)
        {
            var decision = TestCommandPartParitySweep.DecideCaseResume(
                ctx.Known.ContainsKey(caseId), ctx.Isolate.Contains(caseId));
            if (decision == PartParityCaseResume.SkipComplete)
            {
                // A complete case still listed (a process that died between its sidecar and
                // the list rewrite) owes nothing more.
                ClearIsolated(ctx, caseId);
                return;
            }
            WaveCase wc = build();
            if (decision == PartParityCaseResume.RunSolo)
            {
                wc.Solo = true;
                solo.Add(wc);
            }
            else
            {
                normal.Add(wc);
            }
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
            var soloFlags = new List<bool>();
            for (int i = cursor; i < queue.Count && soloFlags.Count <= PartParitySweepPlan.MaxWaveCases; i++)
                soloFlags.Add(queue[i].Solo);
            int candidates = PartParitySweepPlan.WaveCandidateCount(soloFlags);
            var sizes = new List<double>();
            for (int i = cursor; i < cursor + candidates; i++)
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
            bool solo = wave.Count == 1 && wave[0].Solo;
            // Spawn, unpack and settle are when a bad modded part most likely kills KSP, and
            // every case of the wave is live: name them all (design 5.3), so a crash here
            // blames none and isolates each.
            WriteInflight(ctx, wave.ConvertAll(c => c.CaseId), solo);
            foreach (var wc in wave) Guard(ctx, wc, "spawn", () => SpawnCase(ctx, wc));
            IEnumerator ready = WaitReady(wave);
            try { while (ready.MoveNext()) yield return ready.Current; }
            finally { DisposeNested(ready); }
            IEnumerator settle = WaitSettled(wave);
            try { while (settle.MoveNext()) yield return settle.Current; }
            finally { DisposeNested(settle); }

            foreach (var wc in wave)
            {
                // During a capture only the case being captured is named.
                WriteInflight(ctx, new List<string> { wc.CaseId }, solo);
                IEnumerator one = RunCaseCapture(ctx, wc);
                try { while (one.MoveNext()) yield return one.Current; }
                finally { DisposeNested(one); }
                Guard(ctx, wc, "write", () => FinishCase(ctx, wc));
                DeleteInflight(ctx);
            }
            IEnumerator down = TearDownWave(ctx, wave);
            try { while (down.MoveNext()) yield return down.Current; }
            finally { DisposeNested(down); }
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
                if (wc.ControlId == TestCommandPartParitySweep.ControlGhostMissingMesh
                    && !wc.Flags.Contains(PartParitySweepPlan.FlagControlNoSubject))
                {
                    // The measured pick renders, so it runs where the views do: at a frame end.
                    yield return new WaitForEndOfFrame();
                    if (wc.Real == null || wc.Real.rootPart == null || wc.Real.state == Vessel.State.DEAD)
                    {
                        wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                        wc.Detail = "vessel lost before the missing-mesh pick";
                        yield break;
                    }
                    Guard(ctx, wc, "pick", () => PartParityCaseCapture.MeasuredHide(ctx, wc));
                    if (wc.Flags.Contains("exception-pick") && !wc.Flags.Contains(PartParitySweepPlan.FlagControlNoSubject))
                        wc.Flags.Add(PartParitySweepPlan.FlagControlNoSubject);
                    // A sub-case is captured only when its pick found a renderer.
                    if (wc.Flags.Contains(PartParitySweepPlan.FlagControlNoSubject)) yield break;
                }
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
            ClearIsolated(ctx, wc.CaseId);
            if (PartParitySweepPlan.IsMissingMeshSubCase(wc.CaseId))
            {
                ParsekLog.Info(Tag, $"control sub-case {wc.CaseId}: subject={sidecar.Part ?? "<none>"} " +
                    $"found={(PartParitySweepPlan.HasFlagPrefix(sidecar.Flags, PartParitySweepPlan.FlagControlPickPrefix) ? "true" : "false")} " +
                    $"setup={sidecar.Setup?.Status ?? "<none>"} views={sidecar.Views.Count.ToString(Ic)} detail={sidecar.Setup?.Detail ?? "<none>"}");
                TryLogMissingMeshFamily(ctx);
            }
            else if (wc.ControlId != null)
            {
                JudgeAndLogControl(sidecar);
            }
            string status = sidecar.Setup?.Status ?? TestCommandPartParitySweep.SetupOk;
            if (TestCommandPartParitySweep.IsSetupFault(status))
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: setup fault {status} detail={sidecar.Setup?.Detail ?? "<none>"}");
            ParsekLog.Verbose(Tag, PartParityCaseCapture.DescribeCase(wc, sidecar));
        }

        private static void JudgeAndLogControl(PartParityCaseSidecar s)
        {
            string outcome = PartParitySweepPlan.JudgeControl(s.CaseId, s.Views, s.Setup?.Status, s.Flags, out string detail);
            string pick = PickOf(s);
            ParsekLog.Info(Tag, $"control {s.CaseId}: outcome={outcome} subject={s.Part ?? "<none>"} " +
                $"setup={s.Setup?.Status ?? "<none>"} detail={detail ?? "<none>"}" + (pick != null ? " pick=" + pick : string.Empty));
        }

        internal static void WriteSidecar(StepContext ctx, PartParityCaseSidecar sidecar)
        {
            string path = Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.SidecarFileName(sidecar.CaseId));
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(PartParityJson.WriteCase(sidecar)), path, Tag);
            ctx.Known[sidecar.CaseId] = sidecar;
        }

        /// <summary>Names the case(s) in flight, atomically (a torn file would name nothing).</summary>
        private static void WriteInflight(StepContext ctx, IList<string> caseIds, bool solo)
        {
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(TestCommandPartParitySweep.FormatInflight(caseIds, solo)),
                Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.InflightFileName), Tag);
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

        /// <summary>
        /// One sub-case of the missing-mesh family (design 4.5): a still subject spawned and
        /// built like a control case under its own sub-case id. It runs the contribution pick
        /// and is captured only when the pick found a renderer.
        /// </summary>
        private static WaveCase BuildMissingMeshSubCase(StepContext ctx, PartParityMissingMeshSubject sub)
        {
            var wc = new WaveCase
            {
                CaseId = sub.SubCaseId, Index = -1, ControlId = TestCommandPartParitySweep.ControlGhostMissingMesh,
                Views = new List<string>(TestCommandPartParitySweep.RequiredViews),
                Key = new PartParityCaseKey { Part = sub.Subject },
                MissingMeshSubject = sub,
            };
            if (ctx.EntriesByName.TryGetValue(sub.Subject, out PartParityPartEntry e))
            {
                wc.Entry = e;
                wc.RealVariant = e.BaseVariant;
                wc.Key.Skin = e.BaseVariant ?? TestCommandPartParitySweep.DefaultSkinName;
            }
            else
            {
                wc.Detail = "control subject not loaded: " + sub.Subject;
            }
            return wc;
        }

        /// <summary>The still subjects of the install in pick order, the first
        /// <see cref="PartParitySweepPlan.MaxMissingMeshSubCases"/> of them as the family.</summary>
        private static void PlanMissingMeshFamily(StepContext ctx)
        {
            var views = new List<string>(TestCommandPartParitySweep.RequiredViews);
            var candidates = new List<PartParityMissingMeshCandidate>();
            var seen = new StringBuilder();
            foreach (string n in PartParitySweepPlan.MissingMeshCandidates)
            {
                if (!ctx.EntriesByName.TryGetValue(n, out var e)) continue;
                PartParityMissingMeshCandidate c = PartParityCaseCapture.PrefabCandidate(n, e.Info);
                if (c == null) continue;
                candidates.Add(c);
                bool still = PartParitySweepPlan.IsStillSubject(c.HasAnimation, c.ModuleNames, out string notStill);
                seen.Append(seen.Length > 0 ? " " : string.Empty).Append(n).Append('=')
                    .Append(still ? "still" : "animated(" + notStill + ")");
            }
            ctx.MissingMeshFamily = PartParitySweepPlan.MissingMeshFamily(
                PartParitySweepPlan.OrderMissingMeshSubjects(candidates, views));
            ParsekLog.Verbose(Tag, $"control {TestCommandPartParitySweep.ControlGhostMissingMesh}: subject candidates " +
                (seen.Length > 0 ? seen.ToString() : "<none loaded>"));
            var family = new StringBuilder();
            foreach (var sub in ctx.MissingMeshFamily)
                family.Append(family.Length > 0 ? " " : string.Empty).Append(sub.SubCaseId).Append(" (").Append(sub.Describe(views)).Append(')');
            ParsekLog.Info(Tag, $"control {TestCommandPartParitySweep.ControlGhostMissingMesh}: family " +
                $"subCases={ctx.MissingMeshFamily.Count.ToString(Ic)} " + (family.Length > 0 ? family.ToString() : "<none> (no-subject)"));
        }

        private static List<string> MissingMeshFamilyIds(StepContext ctx)
            => ctx.MissingMeshFamily.ConvertAll(f => f.SubCaseId);

        /// <summary>Logs the family's control line once, as soon as the family is decided
        /// (the first sub-case that found a renderer has its sidecar, or every sub-case found
        /// none, or the family is empty). Every sub-case's counts were logged when measured.</summary>
        private static void TryLogMissingMeshFamily(StepContext ctx)
        {
            if (ctx.MissingMeshFamilyLogged) return;
            string outcome = PartParitySweepPlan.ResolveMissingMeshFamily(MissingMeshFamilyIds(ctx),
                id => ctx.Known.TryGetValue(id, out PartParityCaseSidecar s) ? s : null,
                out PartParityCaseSidecar chosen, out string detail);
            if (outcome == TestCommandPartParitySweep.ControlsPending) return;
            ctx.MissingMeshFamilyLogged = true;
            var subs = new StringBuilder();
            foreach (var sub in ctx.MissingMeshFamily)
            {
                bool has = ctx.Known.TryGetValue(sub.SubCaseId, out PartParityCaseSidecar s);
                subs.Append(subs.Length > 0 ? "," : string.Empty).Append(sub.SubCaseId).Append(':')
                    .Append(!has ? "not-run" : PartParitySweepPlan.HasFlagPrefix(s.Flags, PartParitySweepPlan.FlagControlPickPrefix) ? "found" : "none");
            }
            ParsekLog.Info(Tag, $"control {TestCommandPartParitySweep.ControlGhostMissingMesh}: outcome={outcome} " +
                $"subject={chosen?.Part ?? "<none>"} setup={chosen?.Setup?.Status ?? "<none>"} detail={detail ?? "<none>"}" +
                (chosen != null ? " pick=" + PickOf(chosen) : string.Empty) + " subs=" + (subs.Length > 0 ? subs.ToString() : "<none>"));
        }

        private static string PickOf(PartParityCaseSidecar s)
        {
            string pick = null;
            foreach (string f in s.Flags)
                if (f != null && f.StartsWith(PartParitySweepPlan.FlagControlPickPrefix, StringComparison.Ordinal))
                    pick = f.Substring(PartParitySweepPlan.FlagControlPickPrefix.Length);
            return pick;
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
                if (id == TestCommandPartParitySweep.ControlGhostMissingMesh)
                {
                    rec.Outcome = PartParitySweepPlan.ResolveMissingMeshFamily(MissingMeshFamilyIds(ctx),
                        fid => ctx.Known.TryGetValue(fid, out PartParityCaseSidecar fs) ? fs : null,
                        out PartParityCaseSidecar chosen, out string fdetail);
                    rec.Detail = (chosen?.Part != null ? "part=" + chosen.Part + " " : string.Empty) + (fdetail ?? string.Empty);
                }
                else if (ctx.Known.TryGetValue(id, out PartParityCaseSidecar s))
                {
                    rec.Outcome = PartParitySweepPlan.JudgeControl(id, s.Views, s.Setup?.Status, s.Flags, out string detail);
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
            {
                if (id == TestCommandPartParitySweep.ControlGhostMissingMesh)
                {
                    foreach (var sub in ctx.MissingMeshFamily)
                        if (!ctx.Known.ContainsKey(sub.SubCaseId)) return false;
                    continue;
                }
                if (!ctx.Known.ContainsKey(id)) return false;
            }
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
                $"solo={(wave.Count == 1 && wave[0].Solo ? "true" : "false")} " +
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

            /// <summary>Owed a solo run (<c>isolate.txt</c>): flies in a wave of its own.</summary>
            internal bool Solo;

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

            /// <summary>The missing-mesh control's subject in prefab order (null for every
            /// other case, and when no still candidate is loaded).</summary>
            internal PartParityMissingMeshSubject MissingMeshSubject;

            /// <summary>The ghost's mesh renderers, parallel to <c>GhostRecords</c> before the
            /// missing-mesh hide re-reads them.</summary>
            internal List<Renderer> GhostMeshRenderers;
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
            /// <summary>The ids a stale <c>inflight.txt</c> named at step start, for the step
            /// start line; null when there was none.</summary>
            internal string StaleInflight;

            /// <summary>The cases owed a solo run (the on-disk <c>isolate.txt</c>), as a set
            /// and in file order.</summary>
            internal readonly HashSet<string> Isolate = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>The missing-mesh family (design 4.5): its sub-cases in order; empty when
            /// no still subject is loaded (or controls are off).</summary>
            internal List<PartParityMissingMeshSubject> MissingMeshFamily = new List<PartParityMissingMeshSubject>();
            internal bool MissingMeshFamilyLogged;
            internal readonly List<string> IsolateOrder = new List<string>();
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

            /// <summary>
            /// Idempotent, and safe wherever a stop lands (between waves, mid-wave while
            /// spawning or settling, mid-case between two views, in teardown): restores the
            /// case-log observer to the one installed before the case (so its closure and the
            /// tile arrays it holds become unreachable), destroys every case of a live wave (its
            /// ghost with the state's cloned materials, its spawned vessel killed), releases the
            /// capture camera, render targets and readback texture. Each part runs even when
            /// another throws. Returns what it released, for the step's log lines.
            /// </summary>
            internal string Cleanup()
            {
                bool observerRestored = false, captureReleased = false;
                int casesDestroyed = 0, faults = 0;
                if (ObserverInstalled)
                {
                    ParsekLog.TestObserverForTesting = PriorObserver;
                    PriorObserver = null;
                    ObserverInstalled = false;
                    observerRestored = true;
                }
                if (Live != null)
                {
                    List<WaveCase> live = Live;
                    Live = null;
                    foreach (var wc in live)
                    {
                        try { DestroyCase(wc); casesDestroyed++; }
                        catch (Exception ex)
                        {
                            faults++;
                            ParsekLog.Warn(Tag, $"cleanup: case {wc?.CaseId ?? "<null>"} destroy threw {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                if (Capture != null)
                {
                    PartParityCapture capture = Capture;
                    Capture = null;
                    try { capture.Dispose(); captureReleased = true; }
                    catch (Exception ex)
                    {
                        faults++;
                        ParsekLog.Warn(Tag, $"cleanup: capture Dispose threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
                return PartParitySweepPlan.FormatCleanup(observerRestored, casesDestroyed, captureReleased, faults);
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
