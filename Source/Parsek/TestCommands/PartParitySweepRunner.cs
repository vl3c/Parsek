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

            // A label whose host was damaged by an earlier step stops at once, before the
            // host admission (a broken host would only be refused host-not-orbiting, and
            // every case would read as never run for no recorded reason).
            PartParityHostDamage prior = ReadHostDamage(ctx.LabelDir);
            if (prior != null)
            {
                ctx.HostDamage = prior;
                PlanStep(ctx);
                ParsekLog.Info(Tag, $"host damage recorded by an earlier step: label={a.Label} reason={prior.Reason} " +
                    $"detail={prior.Detail ?? "<none>"}; no case runs, {CountUnrun(ctx).ToString(Ic)} case(s) left unrun");
                FinishStep(ctx);
                yield break;
            }

            IEnumerator unpack = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            try { while (unpack.MoveNext()) yield return unpack.Current; }
            finally { DisposeNested(unpack); }

            PartParityHostBaseline baseline = ReadHostBaseline(ctx.LabelDir);
            if (baseline == null)
            {
                // The label's first run step: admit the host and record what it was.
                if (!AdmitHost(ctx)) yield break;
                ctx.HostBaseline = CaptureHostBaseline(ctx.Host, TestCommandPartParitySweep.IsAirRun(a));
                WriteHostBaseline(ctx.LabelDir, ctx.HostBaseline);
                PlanStep(ctx);
            }
            else
            {
                // A later step compares against the label's FIRST host, so a host damaged
                // between two steps (after the last check of the previous one) still trips.
                ctx.HostBaseline = baseline;
                PlanStep(ctx);
                if (!CheckHost(ctx, "step start")) ctx.Host = FlightGlobals.ActiveVessel;
            }

            if (ctx.HostDamage == null)
            {
                IEnumerator waves = RunWaves(ctx);
                try { while (waves.MoveNext()) yield return waves.Current; }
                finally { DisposeNested(waves); }
            }
            FinishStep(ctx);
        }

        // =====================================================================================
        // host integrity (design 4.1)
        // =====================================================================================

        private static PartParityHostBaseline CaptureHostBaseline(Vessel host, bool air)
        {
            var b = new PartParityHostBaseline
            {
                Pid = host.persistentId,
                PartCount = host.parts != null ? host.parts.Count : 0,
                Situation = host.situation.ToString(),
                // An air run's host stays PRELAUNCH / LANDED (host.txt mode=air, design D.2).
                Air = air,
            };
            ParsekLog.Info(Tag, $"host baseline: pid={b.Pid.ToString(Ic)} name={host.vesselName} parts={b.PartCount.ToString(Ic)} " +
                $"situation={b.Situation} mode={(air ? PartParityHostIntegrity.ModeAir : PartParityHostIntegrity.ModeOrbit)} " +
                "(the integrity guard compares every later check against it)");
            return b;
        }

        private static PartParityHostObservation ObserveHost(uint pid)
        {
            Vessel v = FlightRecorder.FindVesselByPid(pid);
            bool present = v != null && v.state != Vessel.State.DEAD && v.rootPart != null;
            return new PartParityHostObservation
            {
                Present = present,
                Active = present && FlightGlobals.ActiveVessel == v,
                PartCount = present && v.parts != null ? v.parts.Count : 0,
                Orbiting = present && v.situation == Vessel.Situations.ORBITING && v.orbit != null,
                Situation = present ? v.situation.ToString() : "<none>",
            };
        }

        /// <summary>True when the host is damaged (now or earlier this step). On the first
        /// trip: records the damage on disk (every later step of the label stops at once),
        /// writes the one Warn line, and leaves the caller to stop.</summary>
        internal static bool CheckHost(StepContext ctx, string where)
        {
            if (ctx.HostDamage != null) return true;
            if (ctx.HostBaseline == null) return false;
            PartParityHostObservation now = ObserveHost(ctx.HostBaseline.Pid);
            string reason = PartParityHostIntegrity.Decide(ctx.HostBaseline, now);
            if (reason == null) return false;
            ctx.HostDamage = new PartParityHostDamage
            {
                Reason = reason,
                Detail = PartParityHostIntegrity.Describe(reason, ctx.HostBaseline, now, where),
            };
            try
            {
                FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(PartParityHostIntegrity.FormatDamage(ctx.HostDamage)),
                    Path.Combine(ctx.LabelDir, PartParityHostIntegrity.DamageFileName), Tag);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"host damage record write threw {ex.GetType().Name}: {ex.Message}");
            }
            ParsekLog.Warn(Tag, PartParityHostIntegrity.DamageLine(ctx.Args.Label, ctx.HostDamage, CountUnrun(ctx)));
            return true;
        }

        /// <summary>Planned cases and (when on) controls with no sidecar yet.</summary>
        private static int CountUnrun(StepContext ctx)
        {
            int n = 0;
            foreach (var p in ctx.Planned)
                if (!ctx.Known.ContainsKey(p.CaseId)) n++;
            if (ctx.Args.Controls)
                foreach (string id in ctx.ControlSet)
                    if (!ctx.Known.ContainsKey(id) && !ctx.NotApplicableControls.Contains(id)) n++;
            return n;
        }

        private static PartParityHostBaseline ReadHostBaseline(string labelDir)
        {
            string path = Path.Combine(labelDir, PartParityHostIntegrity.BaselineFileName);
            return File.Exists(path) ? PartParityHostIntegrity.ParseBaseline(File.ReadAllText(path)) : null;
        }

        private static void WriteHostBaseline(string labelDir, PartParityHostBaseline b)
        {
            Directory.CreateDirectory(labelDir);
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(PartParityHostIntegrity.FormatBaseline(b)),
                Path.Combine(labelDir, PartParityHostIntegrity.BaselineFileName), Tag);
        }

        private static PartParityHostDamage ReadHostDamage(string labelDir)
        {
            string path = Path.Combine(labelDir, PartParityHostIntegrity.DamageFileName);
            return File.Exists(path) ? PartParityHostIntegrity.ParseDamage(File.ReadAllText(path)) : null;
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
            ctx.Succeed(false, 0, 0, 0, 0, 0, TestCommandPartParitySweep.ControlsState(ctx.Args.Controls, null, ctx.ControlSet));
        }

        private static bool AdmitHost(StepContext ctx)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            // The args-aware rule (design D.2, D.4): an orbit run needs an orbiting host, an
            // air run one PRELAUNCH or LANDED within 500 m of the pad.
            string refusal = PartParityAirContext.DecideAdmission(ctx.Args, active, out string detail);
            if (refusal != null)
            {
                ctx.Refuse(refusal, detail);
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
                keys.AddRange(PartParitySkinRules.CaseKeys(e));
            }
            // A state run (design Appendix D.2) plans ONLY its state and context cases; a
            // stowed run reads exactly the stowed keys, in today's order.
            // A motion run (design Appendix H) plans ONLY its motion keys; it is checked first,
            // since a motion run with contexts=air also reads as a state run.
            bool motionRun = TestCommandPartParitySweep.IsMotionRun(ctx.Args);
            List<PartParityCaseKey> motionKeys = motionRun ? EnumerateMotion(ctx) : null;
            List<PartParityCaseKey> stateKeys = null;
            if (!motionRun && TestCommandPartParitySweep.IsStateRun(ctx.Args)) stateKeys = EnumerateStates(ctx);
            // An FX run (design Appendix E.5) plans ONLY its FX keys.
            List<PartParityCaseKey> fxKeys = !motionRun && TestCommandPartParitySweep.IsFxRun(ctx.Args) ? EnumerateFx(ctx) : null;
            ctx.Planned = motionKeys != null
                ? TestCommandPartParitySweep.PlanCases(keys, stateKeys, ctx.Args, fxKeys, motionKeys)
                : TestCommandPartParitySweep.PlanCases(keys, stateKeys, ctx.Args, fxKeys);
            LogSkinCounts(ctx, keys);
            if (stateKeys != null)
                ParsekLog.Info(Tag, PartParityStateRules.FormatCounts(TestCommandPartParitySweep.FormatStates(ctx.Args),
                    TestCommandPartParitySweep.FormatContexts(ctx.Args), stateKeys, ctx.Planned.Count, ctx.StateExclusions));
            if (fxKeys != null)
                ParsekLog.Info(Tag, PartParityFxRules.FormatCounts(TestCommandPartParitySweep.FormatFx(ctx.Args),
                    TestCommandPartParitySweep.FormatContexts(ctx.Args), fxKeys, ctx.Planned.Count, ctx.FxExclusions));
            if (motionKeys != null)
                ParsekLog.Info(Tag, PartParityMotionRules.FormatCounts(TestCommandPartParitySweep.FormatMotion(ctx.Args),
                    TestCommandPartParitySweep.FormatContexts(ctx.Args), motionKeys, ctx.Planned.Count, ctx.MotionExclusions));
            foreach (var p in ctx.Planned) LoadKnownSidecar(ctx, p.CaseId);
            if (ctx.Args.Controls)
                foreach (string id in ctx.ControlSet) LoadKnownSidecar(ctx, id);
            var keysById = new Dictionary<string, PartParityCaseKey>(StringComparer.Ordinal);
            foreach (var k in keys) keysById[TestCommandPartParitySweep.CaseId(k)] = k;
            if (stateKeys != null)
                foreach (var k in stateKeys) keysById[TestCommandPartParitySweep.CaseId(k)] = k;
            if (fxKeys != null)
                foreach (var k in fxKeys) keysById[TestCommandPartParitySweep.CaseId(k)] = k;
            if (motionKeys != null)
                foreach (var k in motionKeys) keysById[TestCommandPartParitySweep.CaseId(k)] = k;
            ResolveStaleInflight(ctx, keysById);
            ParsekLog.Info(Tag, $"step start: label={ctx.Args.Label} op=run parts={ctx.Entries.Count.ToString(Ic)} " +
                $"excluded={ctx.Exclusions.Count.ToString(Ic)} planned={ctx.Planned.Count.ToString(Ic)} " +
                $"knownSidecars={ctx.Known.Count.ToString(Ic)} controls={(ctx.Args.Controls ? "true" : "false")} " +
                $"filter={ctx.Args.Filter} shard={ctx.Args.ShardK.ToString(Ic)}/{ctx.Args.ShardN.ToString(Ic)} " +
                $"tile={ctx.Args.Tile.ToString(Ic)} views={string.Join(",", ctx.Args.Views.ToArray())} " +
                $"maxSeconds={ctx.Args.MaxSeconds.ToString(Ic)} staleInflight={ctx.StaleInflight ?? "<none>"} " +
                $"isolate={ctx.Isolate.Count.ToString(Ic)} states={TestCommandPartParitySweep.FormatStates(ctx.Args)} " +
                $"contexts={TestCommandPartParitySweep.FormatContexts(ctx.Args)} controlSet={ctx.ControlSet.Length.ToString(Ic)}");
        }

        /// <summary>Every state and context case the install offers (design D.1), at each
        /// part's base skin (ruling R2), with its exclusions.</summary>
        private static List<PartParityCaseKey> EnumerateStates(StepContext ctx)
        {
            var all = new PartParityStateEnumeration();
            foreach (var e in ctx.Entries)
            {
                PartParityStatePart sp = PartParityStateDriver.BuildStatePart(e.Info);
                all.AddRange(PartParityStateRules.Enumerate(sp, PartParityCaseRules.DefaultCaseKey(e)));
            }
            ctx.StateExclusions.AddRange(all.Exclusions);
            return all.Cases;
        }

        /// <summary>Every motion case the install offers with the run's exclusions (design H.1).</summary>
        private static List<PartParityCaseKey> EnumerateMotion(StepContext ctx)
        {
            bool air = TestCommandPartParitySweep.IsAirRun(ctx.Args);
            var all = new PartParityMotionEnumeration();
            foreach (var e in ctx.Entries)
            {
                PartParityStatePart sp = PartParityStateDriver.BuildStatePart(e.Info);
                all.AddRange(PartParityMotionRules.Enumerate(sp, PartParityCaseRules.DefaultCaseKey(e)));
            }
            foreach (PartParityMotionExclusion x in all.Exclusions)
                if (PartParityMotionRules.ExclusionApplies(x, ctx.Args.Motion, air)) ctx.MotionExclusions.Add(x);
            return all.Cases;
        }

        /// <summary>Every FX case the install offers with the run's exclusions (design E.1).</summary>
        private static List<PartParityCaseKey> EnumerateFx(StepContext ctx)
        {
            bool air = TestCommandPartParitySweep.IsAirRun(ctx.Args);
            PartParityFxEnumeration all = PartParityFxDriver.Enumerate(ctx);
            foreach (PartParityFxExclusion e in all.Exclusions)
                if (PartParityFxRules.ExclusionApplies(e, air)) ctx.FxExclusions.Add(e);
            return all.Cases;
        }

        /// <summary>The per-step case counts by skin kind (design 3.2): every case the install
        /// offers, and the cases this step's selection planned.</summary>
        private static void LogSkinCounts(StepContext ctx, List<PartParityCaseKey> keys)
        {
            PartParitySkinCounts all = PartParitySkinRules.Count(keys);
            PartParitySkinCounts planned = PartParitySkinRules.Count(ctx.Planned.ConvertAll(p => p.Key));
            ParsekLog.Info(Tag, PartParitySkinRules.FormatCounts(
                ctx.Args.Skins == PartParitySkinSelection.All ? "all" : "default", all, planned));
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
                    PartParityCaseSidecar crashedSidecar = PartParityJson.BuildCrashedSidecar(k, d.CrashedCaseId,
                        solo
                            ? "a previous process died while this case ran alone (isolation rerun, stale inflight.txt)"
                            : "a previous process died while this case was in flight (stale inflight.txt)");
                    // A crashed state case still names its family and state (design D.2).
                    crashedSidecar.StateProof = k.FxOption != null
                        ? PartParityStateWave.CrashedFxStateProof(k.FxOption, k.State)
                        : PartParityStateWave.CrashedStateProof(k.StateOption, k.State);
                    if (crashedSidecar.StateProof != null) crashedSidecar.StowedCaseId = PartParityStateWave.StowedCaseId(k);
                    WriteSidecar(ctx, crashedSidecar);
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
            bool air = TestCommandPartParitySweep.IsAirRun(ctx.Args);
            while (cursor < queue.Count)
            {
                if (CheckHost(ctx, "before wave " + (ctx.WavesDone + 1).ToString(Ic))) yield break;
                double elapsed = Time.realtimeSinceStartup - ctx.StartRealtime;
                List<int> picked = PickWave(queue, cursor, air, out string kind, out double trackerCap);
                bool motionKind = PartParityMotionPlan.IsMotionKind(kind);
                if (motionKind
                        ? !PartParityMotionPlan.AdmitWave(elapsed, ctx.Args.MaxSeconds, ctx.WavesDone == 0, picked.Count)
                        : !PartParityStateWave.AdmitWave(kind, elapsed, ctx.Args.MaxSeconds, lastWave, picked.Count, trackerCap,
                            FamiliesOf(queue, picked), ctx.WavesDone == 0, FxOptionsOf(queue, picked)))
                {
                    ParsekLog.Info(Tag, $"time box: elapsed={elapsed.ToString("0.0", Ic)}s lastWave={lastWave.ToString("0.0", Ic)}s " +
                        $"maxSeconds={ctx.Args.MaxSeconds.ToString(Ic)} kind={kind}; {(queue.Count - cursor).ToString(Ic)} case(s) left for the next step");
                    yield break;
                }
                queue = PartParityStateWave.BringToCursor(queue, cursor, picked);
                List<WaveCase> wave = TakeWave(queue, ref cursor, kind, picked.Count, air);
                float waveStart = Time.realtimeSinceStartup;
                // An FX wave (design E.2) runs its own flow: a hold, a press, the warm-up, the
                // layered capture. The one dispatch hook of the air context (design D.4): every wave of an air
                // run, its stowed controls included, spawns over the showcase band.
                IEnumerator w = air ? PartParityAirContext.RunWave(ctx, wave) : RunWave(ctx, wave);
                if (PartParityStateWave.IsFxKind(kind)) w = PartParityFxDriver.RunWave(ctx, wave);
                // A motion wave (Appendix H.2): the orbit flow here, the chute hook inside the air one.
                if (motionKind && !air) w = PartParityMotionDriver.RunWave(ctx, wave);
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
                foreach (string id in ctx.ControlSet)
                {
                    if (!ResolveApplicable(ctx, id)) continue;
                    ResumeOrQueue(ctx, id, () => BuildControlCase(ctx, id), solo, normal);
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

        /// <summary>True when the control has a subject (or is not an FX control); an optional one
        /// with none is recorded <c>n/a</c> and never queued (design E.5).</summary>
        private static bool ResolveApplicable(StepContext ctx, string id)
        {
            if (!TestCommandPartParitySweep.IsFxRun(ctx.Args)) return true;
            PartParityFxSubjectChoice choice = PartParityFxDriver.ChooseSubject(ctx, id, TestCommandPartParitySweep.IsAirRun(ctx.Args));
            if (choice.Outcome != TestCommandPartParitySweep.ControlsNotApplicable) return true;
            ctx.NotApplicableControls.Add(id);
            return false;
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
            var wc = new WaveCase
            {
                CaseId = p.CaseId, Index = p.Index, Key = p.Key, Entry = e, SkinOption = p.Key.SkinOption,
                RealVariant = PartParitySkinRules.RealVariant(p.Key.SkinOption, e?.BaseVariant), Views = ctx.Args.Views,
            };
            if (p.Key.MotionOption != null) { if (e != null) PartParityMotionDriver.Attach(wc, p.Key, e, controlId: null); }
            else if (p.Key.StateOption != null) wc.State = PartParityStateDriver.NewRun(p.Key, e, dropEvents: false);
            else if (p.Key.FxOption != null && e != null) PartParityFxDriver.Attach(wc, p.Key, e, control: null, dropEvents: false);
            return wc;
        }

        /// <summary>The queue positions the next wave takes (design D.3): a stowed wave as
        /// today, a state wave one bucket from the cursor on; the kind and the slowest sun
        /// tracker's cap (0 for none) come back for the admission.</summary>
        private static List<int> PickWave(List<WaveCase> queue, int cursor, bool air, out string kind, out double trackerCap)
        {
            var kinds = queue.ConvertAll(WaveKindOf);
            var solo = queue.ConvertAll(c => c.Solo);
            int maxState = PartParityMotionPlan.IsMotionKind(kinds[cursor])
                ? (kinds[cursor].EndsWith(PartParityMotionPlan.KindPairSuffix, StringComparison.Ordinal) ? 1 : PartParityMotionPlan.MaxWaveCases(air))
                : PartParityStateWave.IsFxKind(kinds[cursor]) ? PartParityFxRules.MaxFxWaveCases
                : air ? PartParitySweepPlan.MaxAirWaveCases : PartParitySweepPlan.MaxStateWaveCases;
            List<int> picked = PartParityStateWave.PickWave(kinds, solo, cursor, PartParitySweepPlan.MaxWaveCases, maxState);
            kind = kinds[cursor];
            trackerCap = 0.0;
            foreach (int i in picked)
                if (queue[i].State != null && queue[i].State.SunTracker)
                    trackerCap = Math.Max(trackerCap, queue[i].State.TrackerCapSeconds);
            return picked;
        }

        /// <summary>A queued case's wave kind: a motion case (or a motion-only control without
        /// one) its motion kind, a real-vs-real pair a kind of its own; every other case P6's.</summary>
        private static string WaveKindOf(WaveCase c)
        {
            if (c.Motion != null || (c.ControlId != null && PartParitySweepPlan.IsMotionOnlyControl(c.ControlId)))
                return PartParityMotionPlan.WaveKind(c.Key, c.Motion?.Twin != null);
            return PartParityStateWave.WaveKind(c.Key);
        }

        private static List<PartParityFxOption> FxOptionsOf(List<WaveCase> queue, List<int> picked)
        {
            var options = new List<PartParityFxOption>();
            foreach (int i in picked)
                if (queue[i].Key?.FxOption != null) options.Add(queue[i].Key.FxOption);
            return options;
        }

        private static List<string> FamiliesOf(List<WaveCase> queue, List<int> picked)
        {
            var families = new List<string>();
            foreach (int i in picked)
                if (queue[i].Key?.StateOption != null) families.Add(queue[i].Key.StateOption.Family);
            return families;
        }

        private static List<WaveCase> TakeWave(List<WaveCase> queue, ref int cursor, string kind, int count, bool air)
        {
            if (air)
            {
                // An air wave places its cases on the showcase band by slot (design D.4).
                List<WaveCase> airWave = queue.GetRange(cursor, count);
                cursor += count;
                return airWave;
            }
            if (kind != PartParityStateWave.KindStowed)
            {
                // A state wave: every other slot (design D.3), one bucket.
                List<double> stateOffsets = PartParityStateWave.PlanStateWave(count);
                var stateWave = new List<WaveCase>();
                for (int i = 0; i < stateOffsets.Count; i++)
                {
                    WaveCase wc = queue[cursor + i];
                    wc.AlongTrackM = stateOffsets[i];
                    stateWave.Add(wc);
                }
                cursor += stateWave.Count;
                return stateWave;
            }
            var soloFlags = new List<bool>();
            for (int i = cursor; i < queue.Count && soloFlags.Count <= PartParitySweepPlan.MaxWaveCases; i++)
            {
                if (PartParityStateWave.WaveKind(queue[i].Key) != PartParityStateWave.KindStowed) break;
                soloFlags.Add(queue[i].Solo);
            }
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
            IEnumerator ready = WaitReady(ctx, wave);
            try { while (ready.MoveNext()) yield return ready.Current; }
            finally { DisposeNested(ready); }
            if (ctx.HostDamage == null)
            {
                IEnumerator settle = WaitSettled(ctx, wave);
                try { while (settle.MoveNext()) yield return settle.Current; }
                finally { DisposeNested(settle); }
            }
            if (ctx.HostDamage == null && wave.Exists(c => c.State != null))
            {
                // Design D.3 steps 2 to 4: stowed inputs, the press, the wait and the settle.
                IEnumerator phase = PartParityStateDriver.RunPhase(ctx, wave, where => CheckHost(ctx, where),
                    (c, stage, body) => Guard(ctx, c, stage, body));
                try { while (phase.MoveNext()) yield return phase.Current; }
                finally { DisposeNested(phase); }
            }

            int unrun = 0;
            foreach (var wc in wave)
            {
                // A damaged host stops the wave: the cases not yet written stay unrun (no
                // sidecar, not a setup fault: their spawn or settle read the broken host).
                if (ctx.HostDamage != null) { unrun++; continue; }
                // During a capture only the case being captured is named.
                WriteInflight(ctx, new List<string> { wc.CaseId }, solo);
                IEnumerator one = RunCaseCapture(ctx, wc);
                try { while (one.MoveNext()) yield return one.Current; }
                finally { DisposeNested(one); }
                Guard(ctx, wc, "write", () => FinishCase(ctx, wc));
                DeleteInflight(ctx);
                CheckHost(ctx, "after case " + wc.CaseId);
            }
            if (ctx.HostDamage != null)
            {
                // No process died: the inflight names nothing to blame.
                DeleteInflight(ctx);
                ParsekLog.Info(Tag, $"wave stopped by the host damage: {unrun.ToString(Ic)} of {wave.Count.ToString(Ic)} case(s) " +
                    "left unrun (no sidecar); tearing the wave down");
            }
            IEnumerator down = TearDownWave(ctx, wave);
            try { while (down.MoveNext()) yield return down.Current; }
            finally { DisposeNested(down); }
            ctx.Live = null;
        }

        /// <summary>Runs one synchronous stage of a case; an exception becomes a case flag and
        /// (before capture) the case's setup detail, never a step failure.</summary>
        internal static void Guard(StepContext ctx, WaveCase wc, string stage, Action body)
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
                else if (stage.StartsWith("state-", StringComparison.Ordinal))
                {
                    // A press, wait or poll that threw ends the case as a press fault.
                    wc.Status = wc.Status ?? TestCommandPartParitySweep.SetupActionFailed;
                    if (wc.State != null)
                    {
                        wc.State.ActionResult = wc.State.ActionResult ?? PartParityStateRules.ActionThrew;
                        wc.State.Done = true;
                    }
                }
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
            if (wc.State != null)
            {
                // A state or context case: the subject plus its context fillers (design D.3).
                ConfigNode stateNode = PartParityStateDriver.AuthorVesselNode(wc, ctx.Host, out string stateFail);
                if (stateNode == null)
                {
                    wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = wc.Detail ?? stateFail ?? "state vessel node not authored";
                    return;
                }
                wc.Pid = GhostCoOrbitalRig.SpawnCoOrbital(stateNode, ctx.Host, wc.AlongTrackM, wc.CaseId, Tag);
                wc.SpawnRealtime = Time.realtimeSinceStartup;
                if (wc.Pid == 0)
                {
                    wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                    wc.Detail = "SpawnAtPosition returned pid=0";
                }
                return;
            }
            ConfigNode node = PartParityCaseAuthoring.AuthorVesselNode(
                wc.Entry.Info, wc.RealVariant, wc.SkinOption, wc.CaseId, ctx.Host, out bool b9Authored);
            if (wc.SkinOption != null && wc.SkinOption.Module == PartParityCaseRules.B9ModuleName)
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: authored B9 {PartParitySkinRules.B9SubtypeKey}={wc.SkinOption.Value} " +
                    $"moduleID={wc.SkinOption.ModuleId ?? "<none>"} b9Index={wc.SkinOption.ModuleIndex.ToString(Ic)} " +
                    $"written={(b9Authored ? "true" : "false")}");
            wc.Pid = GhostCoOrbitalRig.SpawnCoOrbital(node, ctx.Host, wc.AlongTrackM, wc.CaseId, Tag);
            wc.SpawnRealtime = Time.realtimeSinceStartup;
            if (wc.Pid == 0)
            {
                wc.Status = TestCommandPartParitySweep.SetupSpawnTimeout;
                wc.Detail = "SpawnAtPosition returned pid=0";
            }
        }

        internal static IEnumerator WaitReady(StepContext ctx, List<WaveCase> wave)
        {
            while (true)
            {
                if (CheckHost(ctx, "spawn wait")) yield break;
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

        internal static IEnumerator WaitSettled(StepContext ctx, List<WaveCase> wave)
        {
            while (true)
            {
                if (CheckHost(ctx, "settle wait")) yield break;
                bool pending = false;
                foreach (var wc in wave)
                {
                    if (wc.Status != null || wc.Real == null || wc.Settled) continue;
                    if (CaseVesselLost(wc))
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

        internal static IEnumerator TearDownWave(StepContext ctx, List<WaveCase> wave)
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
            // The FX case's hold, RCS input, plate and broken ghost first, then the jettison
            // debris and the start-route / stowed ghosts (design D.3 step 6).
            try { PartParityFxDriver.Release(wc); }
            catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: FX release threw {ex.GetType().Name}: {ex.Message}"); }
            try { PartParityMotionDriver.Release(wc); }
            catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: motion release threw {ex.GetType().Name}: {ex.Message}"); }
            try { PartParityStateDriver.DestroyStateParts(wc); }
            catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: state parts destroy threw {ex.GetType().Name}: {ex.Message}"); }
            if (wc.PlantedCube != null)
            {
                try { UnityEngine.Object.Destroy(wc.PlantedCube); }
                catch (Exception ex) { ParsekLog.Warn(Tag, $"case {wc.CaseId}: planted cube destroy threw {ex.GetType().Name}: {ex.Message}"); }
                wc.PlantedCube = null;
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: planted cube destroyed");
            }
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
            // A settled case can still die while the cases before it capture (a mirrored
            // variant's negative-scale collider explodes on unpack): Prepare on a destroyed
            // vessel would throw and read as an instrument crash, so it is died-settling.
            if (CaseVesselLost(wc))
            {
                wc.Status = TestCommandPartParitySweep.SetupDiedSettling;
                wc.Detail = "vessel lost before prepare";
                ParsekLog.Verbose(Tag, $"case {wc.CaseId}: real vessel or root part gone before prepare; recorded died-settling");
                yield break;
            }
            BeginCaseLog(ctx, wc);
            try
            {
                Guard(ctx, wc, "prepare", () => PartParityCaseCapture.Prepare(ctx, wc));
                if (wc.Status != null || wc.Shared == null) yield break;
                foreach (string view in wc.Views)
                {
                    yield return new WaitForEndOfFrame();
                    if (CaseVesselLost(wc))
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

        /// <summary>The case's real vessel is gone (Unity's null reads a destroyed object as
        /// null), has no root part, or is DEAD.</summary>
        internal static bool CaseVesselLost(WaveCase wc)
        {
            Vessel v = wc.Real;
            bool present = v != null;
            return PartParityHostIntegrity.IsCaseVesselLost(present, present && v.rootPart != null,
                present && v.state == Vessel.State.DEAD);
        }

        internal static void BeginCaseLog(StepContext ctx, WaveCase wc)
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

        internal static void EndCaseLog(StepContext ctx)
        {
            if (!ctx.ObserverInstalled) return;
            ParsekLog.TestObserverForTesting = ctx.PriorObserver;
            ctx.PriorObserver = null;
            ctx.ObserverInstalled = false;
        }

        internal static void FinishCase(StepContext ctx, WaveCase wc)
        {
            PartParityCaseSidecar sidecar = PartParityCaseCapture.BuildSidecar(ctx, wc);
            if (wc.RealTiles.Count > 0)
                PartParityCaseCapture.WritePictures(ctx, wc, sidecar);
            if (wc.Fx != null) PartParityFxDriver.WritePictures(ctx, wc, sidecar);
            if (wc.Motion != null) PartParityMotionDriver.WritePictures(ctx, wc);
            WriteSidecar(ctx, sidecar);
            if (wc.ControlId != null && PartParityMotionRules.HasNormalRoute(wc.ControlId)) PartParityMotionDriver.WriteNormalRoute(ctx, wc);
            ClearIsolated(ctx, wc.CaseId);
            if (wc.ControlId != null) JudgeAndLogControl(sidecar, wc.State != null, wc.Fx != null);
            string status = sidecar.Setup?.Status ?? TestCommandPartParitySweep.SetupOk;
            if (TestCommandPartParitySweep.IsSetupFault(status))
                ParsekLog.Warn(Tag, $"case {wc.CaseId}: setup fault {status} detail={sidecar.Setup?.Detail ?? "<none>"}");
            ParsekLog.Verbose(Tag, PartParityCaseCapture.DescribeCase(wc, sidecar));
        }

        private static void JudgeAndLogControl(PartParityCaseSidecar s, bool stateControl, bool fxControl)
        {
            string detail;
            string outcome = PartParitySweepPlan.IsMotionOnlyControl(s.CaseId)
                ? PartParityMotionDriver.JudgeControl(s, out detail)
                : fxControl
                ? PartParityFxControls.Judge(s.CaseId, s, out detail)
                : stateControl
                ? PartParitySweepPlan.JudgeStateControl(s.StateProof?.Reached == true, s.StateProof?.Events?.Count ?? 0,
                    s.Views, s.Setup?.Status, out detail)
                : PartParitySweepPlan.JudgeControl(s.CaseId, s.Views, s.Setup?.Status, s.Structure, out detail);
            ParsekLog.Info(Tag, $"control {s.CaseId}: outcome={outcome} subject={s.Part ?? "<none>"} " +
                $"setup={s.Setup?.Status ?? "<none>"} detail={detail ?? "<none>"}");
        }

        internal static void WriteSidecar(StepContext ctx, PartParityCaseSidecar sidecar)
        {
            string path = Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.SidecarFileName(sidecar.CaseId));
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(PartParityJson.WriteCase(sidecar)), path, Tag);
            ctx.Known[sidecar.CaseId] = sidecar;
        }

        /// <summary>Names the case(s) in flight, atomically (a torn file would name nothing).</summary>
        internal static void WriteInflight(StepContext ctx, IList<string> caseIds, bool solo)
        {
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes(TestCommandPartParitySweep.FormatInflight(caseIds, solo)),
                Path.Combine(ctx.LabelDir, TestCommandPartParitySweep.InflightFileName), Tag);
        }

        internal static void DeleteInflight(StepContext ctx)
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
            if (TestCommandPartParitySweep.IsMotionRun(ctx.Args) && PartParitySweepPlan.IsMotionOnlyControl(id))
                return PartParityMotionDriver.BuildControlCase(ctx, id, wc);
            if (TestCommandPartParitySweep.IsFxRun(ctx.Args)) return BuildFxControlCase(ctx, id, wc);
            if (id == TestCommandPartParitySweep.ControlStateEventsDropped
                || id == TestCommandPartParitySweep.ControlStateEventsDroppedAir)
                return BuildStateControlCase(ctx, id, wc);
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

        /// <summary>A state control (design D.6): the subject's own state case (the
        /// Communotron 16 at deployed, the Mk16 at semi in the air) with its event-route ghost
        /// built WITHOUT the polled events.</summary>
        private static WaveCase BuildStateControlCase(StepContext ctx, string id, WaveCase wc)
        {
            bool air = id == TestCommandPartParitySweep.ControlStateEventsDroppedAir;
            string part = air ? PartParitySweepPlan.ControlAirPart : PartParitySweepPlan.ControlStatePart;
            string state = air ? PartParityStateRules.StateSemi : PartParityStateRules.StateDeployed;
            wc.Key = new PartParityCaseKey { Part = part };
            if (!ctx.EntriesByName.TryGetValue(part, out PartParityPartEntry e))
            {
                wc.Detail = "control subject not loaded: " + part;
                return wc;
            }
            wc.Entry = e;
            wc.RealVariant = e.BaseVariant;
            PartParityStatePart sp = PartParityStateDriver.BuildStatePart(e.Info);
            foreach (PartParityCaseKey k in PartParityStateRules.Enumerate(sp, PartParityCaseRules.DefaultCaseKey(e)).Cases)
            {
                if (k.State != state) continue;
                wc.Key = k;
                wc.State = PartParityStateDriver.NewRun(k, e, dropEvents: true);
                break;
            }
            if (wc.State == null) wc.Detail = "control subject has no " + state + " case: " + part;
            return wc;
        }

        /// <summary>An FX control (design E.5): the subject part of its preference list and the
        /// FX key it stands for (the subject's burn-full plume, heat-hot, or lit lamp).</summary>
        private static WaveCase BuildFxControlCase(StepContext ctx, string id, WaveCase wc)
        {
            bool air = TestCommandPartParitySweep.IsAirRun(ctx.Args);
            PartParityFxSubjectChoice choice = PartParityFxDriver.ChooseSubject(ctx, id, air);
            wc.Key = new PartParityCaseKey { Part = choice.Subject ?? "<none>" };
            if (choice.Subject == null || !ctx.EntriesByName.TryGetValue(choice.Subject, out PartParityPartEntry e))
            {
                wc.Detail = "control subject: " + (choice.Detail ?? "not loaded");
                return wc;
            }
            wc.Entry = e;
            wc.RealVariant = e.BaseVariant;
            PartParityCaseKey key = PartParityFxDriver.ControlKey(ctx, id, e, air);
            if (key == null)
            {
                wc.Detail = "control subject has no FX case: " + choice.Subject;
                return wc;
            }
            wc.Key = key;
            PartParityFxDriver.Attach(wc, key, e, id, dropEvents: PartParityFxDriver.DropsEvents(id));
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
                case TestCommandPartParitySweep.ControlRealVsReal when TestCommandPartParitySweep.IsAirRun(ctx.Args):
                    // An air run's real-vs-real control photographs an armed, falling chute (D.2).
                    return PartParitySweepPlan.ControlAirPart;
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
            string controls = TestCommandPartParitySweep.ControlsState(ctx.Args.Controls, ConvertOutcomes(outcomes), ctx.ControlSet);
            bool controlsDone = !ctx.Args.Controls || AllControlsRan(ctx);
            int next = PartParitySweepPlan.NextIncomplete(completeFlags);
            // A damaged host never reads as done: the sweep stopped, it did not finish.
            bool done = next >= ctx.Planned.Count && controlsDone && ctx.HostDamage == null;
            PartParityManifestBuilder.Write(ctx, outcomes, done, complete, faults);
            ctx.Succeed(done, next, ctx.Planned.Count, complete, crashed, faults, controls);
            ParsekLog.Verbose(Tag, $"step end: waves={ctx.WavesDone.ToString(Ic)} (the step line follows)");
        }

        private static List<PartParityControlRecord> JudgeAllControls(StepContext ctx)
        {
            var list = new List<PartParityControlRecord>();
            if (!ctx.Args.Controls) return list;
            bool air = TestCommandPartParitySweep.IsAirRun(ctx.Args);
            bool fxRun = TestCommandPartParitySweep.IsFxRun(ctx.Args);
            foreach (string id in ctx.ControlSet)
            {
                var rec = new PartParityControlRecord
                {
                    Id = id, Subject = PartParitySweepPlan.ControlSubject(id, air, fxRun),
                    Required = fxRun ? PartParityFxRules.ControlRequiredWords(id) : PartParitySweepPlan.ControlRequired(id),
                    Outcome = TestCommandPartParitySweep.ControlsPending,
                };
                if (ctx.NotApplicableControls.Contains(id))
                {
                    rec.Outcome = TestCommandPartParitySweep.ControlsNotApplicable;
                    rec.Detail = "no eligible subject on this install";
                }
                else if (ctx.Known.TryGetValue(id, out PartParityCaseSidecar s))
                {
                    string detail;
                    if (PartParitySweepPlan.IsMotionOnlyControl(id))
                        rec.Outcome = PartParityMotionDriver.JudgeControl(s, out detail);
                    else if (fxRun)
                        rec.Outcome = PartParityFxControls.Judge(id, s, out detail);
                    else if (id == TestCommandPartParitySweep.ControlStateEventsDropped
                        || id == TestCommandPartParitySweep.ControlStateEventsDroppedAir)
                        rec.Outcome = PartParitySweepPlan.JudgeStateControl(s.StateProof?.Reached == true,
                            s.StateProof?.Events?.Count ?? 0, s.Views, s.Setup?.Status, out detail);
                    else
                        rec.Outcome = PartParitySweepPlan.JudgeControl(id, s.Views, s.Setup?.Status, s.Structure, out detail);
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
            foreach (string id in ctx.ControlSet)
                if (!ctx.Known.ContainsKey(id) && !ctx.NotApplicableControls.Contains(id)) return false;
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

            /// <summary>The skin case's option (design 3.2); null for a base case and a control.</summary>
            internal PartParitySkinOption SkinOption;
            internal string RealVariant;
            internal string GhostVariant;
            internal List<string> Views;
            internal double AlongTrackM;

            /// <summary>Owed a solo run (<c>isolate.txt</c>): flies in a wave of its own.</summary>
            internal bool Solo;

            /// <summary>A state or context case's live state (design D.3 to D.5); null for a
            /// stowed case, whose path is unchanged.</summary>
            internal PartParityStateRun State;

            /// <summary>An FX case's live FX run (design Appendix E); null for every other case.</summary>
            internal PartParityFxRun Fx;
            /// <summary>A motion case's live run (design Appendix H); null for every other case.</summary>
            internal PartParityMotionRun Motion;
            /// <summary>A state case's start-route and stowed-reference tiles (atlas rows 3
            /// and 4); empty for a stowed or a context case.</summary>
            internal List<byte[]> GhostStartTiles = new List<byte[]>();
            internal List<byte[]> GhostStowedTiles = new List<byte[]>();
            /// <summary>The real part as a load leaves its glow, one tile per captured view (atlas
            /// row 5); empty unless the start route was reloaded (<see cref="PartParityRampHold"/>).</summary>
            internal List<byte[]> RealStartTiles = new List<byte[]>();

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

            /// <summary>The missing-mesh control's planted cube on the real part (design 4.5);
            /// null for every other case and once destroyed.</summary>
            internal GameObject PlantedCube;
            internal int SeedEvents;
            internal int SeedApplied;
            internal PartParitySkinProof Skin;

            /// <summary>The live member the real skin was read from (the skin proof's log line).</summary>
            internal string SkinRealMember;
            /// <summary>Depth-mask modules the real-side repair could not fix (design 4.2 step 0).</summary>
            internal int DepthMaskUnrepaired;

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

            /// <summary>The label's host as its first run step found it (<c>host.txt</c>).</summary>
            internal PartParityHostBaseline HostBaseline;

            /// <summary>Set once the host-integrity guard tripped (this step or an earlier
            /// one): no further case runs.</summary>
            internal PartParityHostDamage HostDamage;
            internal List<PartParityPartEntry> Entries = new List<PartParityPartEntry>();
            internal readonly Dictionary<string, PartParityPartEntry> EntriesByName = new Dictionary<string, PartParityPartEntry>(StringComparer.Ordinal);
            internal readonly List<PartParityExclusion> Exclusions = new List<PartParityExclusion>();

            /// <summary>The state run's exclusions (design D.1), named in the manifest beside
            /// the stowed ones; empty for a stowed run.</summary>
            internal readonly List<PartParityStateExclusion> StateExclusions = new List<PartParityStateExclusion>();

            /// <summary>The run's FX exclusions (design E.1); empty outside an FX run.</summary>
            internal readonly List<PartParityFxExclusion> FxExclusions = new List<PartParityFxExclusion>();
            internal readonly List<PartParityMotionExclusion> MotionExclusions = new List<PartParityMotionExclusion>();

            /// <summary>The FX controls with no subject on this install (<c>n/a</c>): never queued.</summary>
            internal readonly HashSet<string> NotApplicableControls = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>The FX control subjects chosen this step (design E.5), one log line
            /// each.</summary>
            internal readonly Dictionary<string, PartParityFxSubjectChoice> FxSubjects =
                new Dictionary<string, PartParityFxSubjectChoice>(StringComparer.Ordinal);

            /// <summary><c>CheatOptions.InfinitePropellant</c> before the step's first plume or rcs
            /// case (design E.2), restored in <see cref="Cleanup"/>; null while never changed.</summary>
            internal bool? FxCheatBefore;

            /// <summary>The step's main-throttle cases, and the host's and the player's throttle
            /// before the first of them.</summary>
            internal readonly List<PartParityFxRun> FxMainThrottleRuns = new List<PartParityFxRun>();
            internal float? FxHostThrottleBefore;
            internal float FxInputThrottleBefore = float.NaN;

            /// <summary>The run's control set (design D.2), a function of the args.</summary>
            internal string[] ControlSet;
            internal List<PartParityPlannedCase> Planned = new List<PartParityPlannedCase>();
            internal readonly Dictionary<string, PartParityCaseSidecar> Known = new Dictionary<string, PartParityCaseSidecar>(StringComparer.Ordinal);
            /// <summary>The ids a stale <c>inflight.txt</c> named at step start, for the step
            /// start line; null when there was none.</summary>
            internal string StaleInflight;

            /// <summary>The cases owed a solo run (the on-disk <c>isolate.txt</c>), as a set
            /// and in file order.</summary>
            internal readonly HashSet<string> Isolate = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<string> IsolateOrder = new List<string>();
            internal PartParityCapture Capture;
            internal List<WaveCase> Live;
            internal int WavesDone;
            internal Action<string> PriorObserver;
            internal bool ObserverInstalled;
            internal PartParitySweepStepResult Result;

            internal StepContext(PartParitySweepArgs args)
            {
                Args = args;
                ControlSet = TestCommandPartParitySweep.ControlSetFor(args);
            }

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
                    Payload = TestCommandPartParitySweep.BuildPayload(Args.Label, done, next, cases, complete, crashed, faults, controls, wall,
                        PartParityHostIntegrity.PayloadToken(HostDamage)),
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
                if (FxCheatBefore.HasValue)
                {
                    bool before = FxCheatBefore.Value;
                    FxCheatBefore = null;
                    CheatOptions.InfinitePropellant = before;
                    ParsekLog.Info(Tag, $"cheat InfinitePropellant restored -> {CheatOptions.InfinitePropellant}");
                }
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
                string throttles = PartParityFxDriver.DescribeMainThrottles(this);
                if (throttles != null) ParsekLog.Info(Tag, throttles);
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
