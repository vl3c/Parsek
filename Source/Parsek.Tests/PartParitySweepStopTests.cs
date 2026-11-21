using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A part parity sweep step stopped by the applier's timeout must still release what it
    /// owns (design-part-ghost-parity.md 5.3, 7). <c>StopCoroutine</c> only abandons an
    /// iterator, so the applier disposes the runner's step iterator first, and every nested
    /// iterator the runner drives by hand is disposed in a finally so the dispose reaches the
    /// innermost one. These cells pin the language behaviour the fix leans on (through the
    /// runner's own <see cref="PartParitySweepRunner.DisposeNested"/>), the abandoned step's
    /// log line, and the source order in the runner and the applier; plus the order of the
    /// stale-inflight records before the inflight delete.
    /// </summary>
    [Collection("Sequential")]
    public class PartParitySweepStopTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PartParitySweepStopTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ----- the dispose chain (the runner's pattern, driven headless) -----

        private static IEnumerator Leaf(List<string> trace)
        {
            try
            {
                trace.Add("leaf-start");
                yield return null;
                yield return null;
                trace.Add("leaf-end");
            }
            finally
            {
                trace.Add("leaf-finally");
            }
        }

        private static IEnumerator Middle(List<string> trace, bool disposeNested)
        {
            IEnumerator leaf = Leaf(trace);
            try { while (leaf.MoveNext()) yield return leaf.Current; }
            finally
            {
                if (disposeNested) PartParitySweepRunner.DisposeNested(leaf);
                trace.Add("middle-finally");
            }
        }

        private static IEnumerator Outer(List<string> trace, bool disposeNested)
        {
            IEnumerator middle = Middle(trace, disposeNested);
            try { while (middle.MoveNext()) yield return middle.Current; }
            finally
            {
                if (disposeNested) PartParitySweepRunner.DisposeNested(middle);
                trace.Add("cleanup");
            }
        }

        [Fact]
        public void DisposingTheOuterIterator_RunsEveryNestedFinally_InsideOut()
        {
            var trace = new List<string>();
            IEnumerator step = Outer(trace, disposeNested: true);
            Assert.True(step.MoveNext());   // suspended mid-leaf, as at a timeout
            PartParitySweepRunner.DisposeNested(step);
            Assert.Equal(new[] { "leaf-start", "leaf-finally", "middle-finally", "cleanup" }, trace.ToArray());
        }

        [Fact]
        public void AbandoningTheIterator_RunsNoFinally()
        {
            // What StopCoroutine alone did: the step's cleanup never ran.
            var trace = new List<string>();
            IEnumerator step = Outer(trace, disposeNested: true);
            Assert.True(step.MoveNext());
            step = null;
            Assert.Equal(new[] { "leaf-start" }, trace.ToArray());
        }

        [Fact]
        public void WithoutDisposingTheNested_TheInnerFinallySkips()
        {
            // Why every hand-driven loop disposes its nested iterator: the outer finally runs,
            // the leaf's does not.
            var trace = new List<string>();
            IEnumerator step = Outer(trace, disposeNested: false);
            Assert.True(step.MoveNext());
            PartParitySweepRunner.DisposeNested(step);
            Assert.DoesNotContain("leaf-finally", trace);
            Assert.Contains("cleanup", trace);
        }

        [Fact]
        public void DisposeNested_ToleratesNullNonDisposableAndThrowing()
        {
            PartParitySweepRunner.DisposeNested(null);
            PartParitySweepRunner.DisposeNested(new ArrayList().GetEnumerator());
            PartParitySweepRunner.DisposeNested(new ThrowingEnumerator());
            Assert.Contains(logLines, l => l.Contains("[PartParity]")
                && l.Contains("nested iterator Dispose threw InvalidOperationException"));
        }

        private sealed class ThrowingEnumerator : IEnumerator, IDisposable
        {
            public object Current => null;
            public bool MoveNext() => false;
            public void Reset() { }
            public void Dispose() { throw new InvalidOperationException("boom"); }
        }

        [Fact]
        public void StepAbandonedLine_NamesTheStepAndWhatWasReleased()
        {
            var args = new PartParitySweepArgs { Label = "pp-1", Op = PartParitySweepOp.Run };
            string line = PartParitySweepRunner.StepAbandonedLine(args,
                PartParitySweepPlan.FormatCleanup(true, 8, true, 0));
            Assert.StartsWith(PartParitySweepRunner.StepAbandonedLiteral + " label=pp-1 op=run:", line);
            Assert.Contains("observerRestored=true casesDestroyed=8 captureReleased=true faults=0", line);
            Assert.Contains("inflight.txt", line);
        }

        // ----- source order -----

        private static string Source(params string[] rel)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            var parts = new List<string> { root, "Source", "Parsek" };
            parts.AddRange(rel);
            string path = Path.Combine(parts.ToArray());
            Assert.True(File.Exists(path), path);
            // Line comments stripped, so a comment can never satisfy an order cell.
            var sb = new System.Text.StringBuilder();
            foreach (string line in File.ReadAllLines(path))
            {
                string t = line.TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        private static string RunnerSource() => Source("TestCommands", "PartParitySweepRunner.cs");
        private static string ApplierSource() => Source("TestCommands", "ParsekTestCommandAddon.PartParitySweep.cs");

        private static string MethodText(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature);
            int open = text.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException("unbalanced " + signature);
        }

        [Fact]
        public void Applier_TimeoutDisposesTheStepBeforeStopCoroutine()
        {
            string text = ApplierSource();
            Assert.Contains("partParityInner = inner;", text);
            int timedOut = text.IndexOf("if (outcome == PartParitySweepPollOutcome.TimedOut)", StringComparison.Ordinal);
            Assert.True(timedOut >= 0);
            int branchEnd = text.IndexOf("return;", timedOut, StringComparison.Ordinal);
            int dispose = text.IndexOf("PartParitySweepRunner.DisposeNested(", timedOut, StringComparison.Ordinal);
            int stop = text.IndexOf("StopCoroutine(partParityRoutine)", timedOut, StringComparison.Ordinal);
            Assert.True(dispose > timedOut && stop > dispose && branchEnd > stop,
                $"timedOut={timedOut} dispose={dispose} stop={stop} branchEnd={branchEnd}");
        }

        [Fact]
        public void Runner_RunStepCleansUpInAFinally_AfterDisposingTheCore()
        {
            string body = MethodText(RunnerSource(), "internal static IEnumerator RunStep(");
            int fin = body.IndexOf("finally", StringComparison.Ordinal);
            int disposeCore = body.IndexOf("DisposeNested(core);", StringComparison.Ordinal);
            int cleanup = body.IndexOf("ctx.Cleanup();", StringComparison.Ordinal);
            int onDone = body.IndexOf("onDone?.Invoke(", StringComparison.Ordinal);
            Assert.True(fin >= 0 && disposeCore > fin && cleanup > disposeCore && onDone > cleanup,
                $"finally={fin} disposeCore={disposeCore} cleanup={cleanup} onDone={onDone}");
            Assert.Equal(1, Regex.Matches(body, @"ctx\.Cleanup\(\)").Count);
        }

        [Fact]
        public void Runner_EveryHandDrivenLoopDisposesItsIteratorInAFinally()
        {
            string text = RunnerSource();
            var loop = new Regex(@"while \((\w+)\.MoveNext\(\)\) yield return \1\.Current;");
            var guarded = new Regex(
                @"try \{ while \((\w+)\.MoveNext\(\)\) yield return \1\.Current; \}\s*\n\s*finally \{ DisposeNested\(\1\); \}");
            int loops = loop.Matches(text).Count;
            int ok = guarded.Matches(text).Count;
            Assert.True(loops >= 7, "loops=" + loops); // unpack, waves, wave, ready, settle, one case, teardown
            Assert.Equal(loops, ok);
        }

        [Fact]
        public void Runner_SpawnPhaseNamesTheWholeWave_CaptureNamesOneCase()
        {
            string body = MethodText(RunnerSource(), "private static IEnumerator RunWave(");
            int all = body.IndexOf("WriteInflight(ctx, wave.ConvertAll(c => c.CaseId), solo);", StringComparison.Ordinal);
            int spawn = body.IndexOf("\"spawn\"", StringComparison.Ordinal);
            int settle = body.IndexOf("WaitSettled(ctx, wave)", StringComparison.Ordinal);
            int one = body.IndexOf("WriteInflight(ctx, new List<string> { wc.CaseId }, solo);", StringComparison.Ordinal);
            int capture = body.IndexOf("RunCaseCapture(ctx, wc)", StringComparison.Ordinal);
            Assert.True(all >= 0 && spawn > all && settle > spawn && one > settle && capture > one,
                $"all={all} spawn={spawn} settle={settle} one={one} capture={capture}");
        }

        [Fact]
        public void PlantedCube_IsMadeBeforeTheRealRecords_AndDiesWithTheCase()
        {
            string capture = Source("TestCommands", "PartParityCaseCapture.cs");
            string prepare = MethodText(capture, "internal static void Prepare(");
            int plant = prepare.IndexOf("PlantCube(ctx, wc, part);", StringComparison.Ordinal);
            int records = prepare.IndexOf("wc.RealRecords = Records(", StringComparison.Ordinal);
            Assert.True(plant >= 0 && records > plant, $"plant={plant} records={records}");

            string plantBody = MethodText(capture, "private static void PlantCube(");
            int made = plantBody.IndexOf("GameObject.CreatePrimitive(PrimitiveType.Cube)", StringComparison.Ordinal);
            int owned = plantBody.IndexOf("wc.PlantedCube = cube;", StringComparison.Ordinal);
            int collider = plantBody.IndexOf("DestroyImmediate(collider)", StringComparison.Ordinal);
            Assert.True(made >= 0 && owned > made && collider > owned,
                $"made={made} owned={owned} collider={collider}"); // owned before anything can throw

            // DestroyCase removes it, and both the wave teardown and the step cleanup call it.
            string runner = RunnerSource();
            string destroy = MethodText(runner, "internal static void DestroyCase(");
            Assert.Contains("UnityEngine.Object.Destroy(wc.PlantedCube);", destroy);
            Assert.Contains("DestroyCase(wc)", MethodText(runner, "private static IEnumerator TearDownWave("));
            Assert.Contains("DestroyCase(wc)", MethodText(runner, "internal string Cleanup("));
        }

        [Fact]
        public void Runner_StaleInflightRecordsAreWrittenBeforeTheDelete()
        {
            string body = MethodText(RunnerSource(), "private static void ResolveStaleInflight(");
            int crashed = body.IndexOf("WriteSidecar(ctx, PartParityJson.BuildCrashedSidecar(", StringComparison.Ordinal);
            int isolate = body.IndexOf("WriteIsolate(ctx.LabelDir, isolate);", StringComparison.Ordinal);
            int delete = body.IndexOf("File.Delete(path);", StringComparison.Ordinal);
            Assert.True(crashed >= 0 && isolate > crashed && delete > isolate,
                $"crashed={crashed} isolate={isolate} delete={delete}");
            // The rule runs over the whole install's case keys, not only the selection.
            string plan = MethodText(RunnerSource(), "private static void PlanStep(");
            Assert.Contains("foreach (var k in keys) keysById[", plan);
            Assert.True(plan.IndexOf("ResolveStaleInflight(ctx, keysById);", StringComparison.Ordinal)
                > plan.IndexOf("SelectCases(", StringComparison.Ordinal));
        }
    }
}
