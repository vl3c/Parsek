using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Part parity P8 (design-part-ghost-parity.md, Appendix E.2, recorder side): the FX sweep
    /// polls a case's engine and RCS modules with the recorder's own statics
    /// (<c>CheckEngineTransition</c>, <c>ShouldRecordEngineAsIgnited</c>,
    /// <c>ComputeRecordedEnginePower</c>, <c>ProcessRcsDebounce</c>, which runs
    /// <c>ComputeRcsPower</c>), and must feed them the recorder's own live reads. This pins those
    /// reads at every call site of the four statics in the recorders (the reference) AND in every
    /// part parity file (the sweep), comments stripped, receiver name free. A sweep file that
    /// calls none of them is not checked, so the sweep half binds as soon as the FX driver lands.
    /// The post-switch auto-record watch (<c>ParsekFlightPostSwitchAutoRecord</c>) is NOT a
    /// reference: it passes <c>engine.EngineIgnited &amp;&amp; engine.isOperational</c> and the raw
    /// throttle to <c>CheckEngineTransition</c> to detect activity, which is not what a recording holds.
    /// </summary>
    public class PartParityFxRecorderPinTests
    {
        private const string Ignited =
            @"ShouldRecordEngineAsIgnited\(\s*(\w+)\.EngineIgnited\s*,\s*\1\.isOperational\s*,\s*\1\.finalThrust\s*\)";
        private const string Power =
            @"ComputeRecordedEnginePower\(\s*(\w+)\.currentThrottle\s*,\s*\1\.finalThrust\s*,\s*\1\.maxThrust\s*\)";
        private const string RcsActive = @"(\w+)\.rcs_active\s*&&\s*\1\.rcsEnabled";
        private const string RcsForces = @"(\w+)\.thrustForces\s*,\s*\1\.thrusterPower";

        private static string Root() => SyntheticRecordingTests.ResolveProjectRoot();

        private static string Read(params string[] parts)
            => PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                new[] { Root(), "Source", "Parsek" }.Concat(parts).ToArray())));

        /// <summary>The whole text of one method, its signature through the brace that balances.</summary>
        private static string Body(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature);
            int depth = 0, i = text.IndexOf('{', start);
            for (; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) break;
            }
            return text.Substring(start, i - start + 1);
        }

        /// <summary>
        /// The rig drives one vessel's main throttle and nothing else's: every write of a main
        /// throttle in the sweep's files is the armed callback's <c>st</c> (the case vessel's own
        /// <c>ctrlState</c>, handed to it by stock) or the release back to 0; the player's input and
        /// the host are only read. The release is in <c>Release</c>, which every teardown path runs,
        /// and the step's cleanup logs what it left.
        /// </summary>
        [Fact]
        public void TheMainThrottle_IsWrittenOnTheCaseVesselOnly_AndReleasedToZero()
        {
            var writes = new List<string>();
            foreach (string file in Directory.GetFiles(Path.Combine(Root(), "Source", "Parsek", "TestCommands"), "PartParity*.cs"))
                foreach (Match m in Regex.Matches(PartParityStateRulesTests.StripComments(File.ReadAllText(file)),
                    @"[\w.]+\.mainThrottle\s*(?:[-+*/]?=)(?!=)[^;]*;"))
                    writes.Add(Path.GetFileName(file) + ": " + m.Value);
            Assert.Equal(new[]
            {
                "PartParityFxDriver.cs: st.mainThrottle = throttle;",
                "PartParityFxDriver.cs: v.ctrlState.mainThrottle = 0f;",
            }, writes.OrderBy(s => s, StringComparer.Ordinal));
            string driver = Read("TestCommands", "PartParityFxDriver.cs");
            string arm = Body(driver, "private static void ArmMainThrottle(");
            Assert.Contains("Vessel v = wc.Real;", arm);
            Assert.Contains("v.OnPreAutopilotUpdate += fx.MainThrottleInput;", arm);
            Assert.Contains("if (fx.Option.MainThrottle) ArmMainThrottle(wc, fx);", Body(driver, "private static void PressPlume("));
            Assert.Equal(2, Regex.Matches(driver, @"\bArmMainThrottle\(").Count);
            // What makes it a main-throttle case, and what its done signal and its proof read.
            string author = Body(driver, "private static ConfigNode AuthorVesselNode(");
            Assert.Contains("mn.SetValue(\"independentThrottle\", o.MainThrottle ? \"False\" : \"True\", true);", author);
            Assert.Contains("st.mainThrottle = throttle;", arm);
            Assert.Contains("float throttle = (float)fx.Option.Throttle;", arm);
            string observe = Body(driver, "private static bool Observe(");
            Assert.Contains("if (fx.Option.MainThrottle && (e == null || !PartParityFxRules.MainThrottleHeld(", observe);
            string step = Body(driver, "internal static bool TryStep(WaveCase wc)");
            Assert.Contains("ReadThrottle(wc);", step);
            Assert.Contains("&& now - run.PressRealtime >= PartParityFxRules.MinBurnSeconds(fx.Option)) run.Done = true;", step);
            Assert.Equal(1, Regex.Matches(step, @"run\.Done = true;").Count - 1);
            Assert.Contains("ReadThrottle(wc);", Body(Read("TestCommands", "PartParityFxDriver.Capture.cs"), "internal static void FillSidecar("));
            string release = Body(driver, "internal static void Release(WaveCase wc)");
            Assert.Contains("v.OnPreAutopilotUpdate -= fx.MainThrottleInput;", release);
            Assert.Contains("v.ctrlState.mainThrottle = 0f;", release);
            Assert.Contains("fx.MainThrottleInput = null;", release);
            string runner = Read("TestCommands", "PartParitySweepRunner.cs");
            Assert.Contains("PartParityFxDriver.Release(wc);", Body(runner, "internal static void DestroyCase(WaveCase wc)"));
            string cleanup = Body(runner, "internal string Cleanup()");
            Assert.True(cleanup.IndexOf("DestroyCase(wc)", StringComparison.Ordinal)
                        < cleanup.IndexOf("PartParityFxDriver.DescribeMainThrottles(this)", StringComparison.Ordinal));
            // The engine is handed stock's own value, and only where stock would not hand it on.
            Assert.Contains("if (e != null && e.EngineIgnited && !e.part.isControllable)", arm);
            Assert.Contains("e.requestedThrottle = PartParityFxRules.RequestedThrottleFor(throttle, e.thrustPercentage);", arm);
        }

        /// <summary>Every call of <paramref name="name"/> in <paramref name="text"/>, as the call's
        /// whole text (name through the balancing close paren). A declaration is skipped.</summary>
        internal static List<string> CallSites(string text, string name)
        {
            var sites = new List<string>();
            foreach (Match m in Regex.Matches(text, @"\b" + Regex.Escape(name) + @"\s*\("))
            {
                string head = text.Substring(Math.Max(0, m.Index - 40), Math.Min(40, m.Index));
                if (Regex.IsMatch(head, @"\b(static|void|bool|float)\s+$")) continue;
                int depth = 0;
                int i = m.Index + m.Length - 1;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')' && --depth == 0) break;
                }
                sites.Add(text.Substring(m.Index, i - m.Index + 1));
            }
            return sites;
        }

        /// <summary>The problems one poll source has against the recorder's reads; empty = pinned.</summary>
        internal static List<string> PollProblems(string label, string text)
        {
            var problems = new List<string>();
            foreach (string site in CallSites(text, "ShouldRecordEngineAsIgnited"))
                if (!Regex.IsMatch(site, "^" + Ignited + "$"))
                    problems.Add(label + ": ignition read differs: " + site);
            foreach (string site in CallSites(text, "ComputeRecordedEnginePower"))
                if (!Regex.IsMatch(site, "^" + Power + "$"))
                    problems.Add(label + ": engine power read differs: " + site);
            List<string> transitions = CallSites(text, "CheckEngineTransition");
            if (transitions.Count > 0)
            {
                if (!Regex.IsMatch(text, Ignited) || !Regex.IsMatch(text, Power))
                    problems.Add(label + ": CheckEngineTransition without the recorder's ignition and power reads");
                foreach (string site in transitions)
                    if (Regex.IsMatch(site, @"\.EngineIgnited|\.currentThrottle"))
                        problems.Add(label + ": CheckEngineTransition fed a raw engine read: " + site);
            }
            foreach (string site in CallSites(text, "ProcessRcsDebounce"))
            {
                if (!Regex.IsMatch(site, RcsForces))
                    problems.Add(label + ": RCS power inputs differ: " + site);
                if (!Regex.IsMatch(site, RcsActive) && !Regex.IsMatch(text, @"\bactive\s*=\s*" + RcsActive))
                    problems.Add(label + ": RCS active read differs: " + site);
            }
            foreach (Match m in Regex.Matches(text, @"\w+\.rcs_active\b[^;]*"))
                if (!Regex.IsMatch(m.Value, "^" + RcsActive) && !Regex.IsMatch(m.Value, @"^\w+\.rcs_active\s*\|\|"))
                    problems.Add(label + ": rcs_active read without rcsEnabled: " + m.Value);
            return problems;
        }

        private static IEnumerable<(string label, string text)> Recorders()
        {
            yield return ("FlightRecorder.EngineAndRcs.cs", Read("Recording", "FlightRecorder.EngineAndRcs.cs"));
            yield return ("BackgroundRecorder.PartEventPolling.cs", Read("Recording", "BackgroundRecorder.PartEventPolling.cs"));
        }

        private static IEnumerable<(string label, string text)> SweepFiles()
        {
            foreach (string dir in new[] { "TestCommands", "InGameTests" })
            {
                string full = Path.Combine(Root(), "Source", "Parsek", dir);
                foreach (string file in Directory.GetFiles(full, "*PartParity*.cs").OrderBy(f => f, StringComparer.Ordinal))
                    yield return (dir + "/" + Path.GetFileName(file),
                        PartParityStateRulesTests.StripComments(File.ReadAllText(file)));
            }
        }

        [Fact]
        public void TheRecorders_PollEnginesAndRcsWithTheReadsTheSweepCopies()
        {
            foreach (var (label, text) in Recorders())
            {
                // Non-vacuous: each recorder carries every call and every read.
                Assert.True(Regex.IsMatch(text, Ignited), label + " lost the ignition read");
                Assert.True(Regex.IsMatch(text, Power), label + " lost the engine power read");
                Assert.True(Regex.IsMatch(text, @"\bactive\s*=\s*" + RcsActive), label + " lost the RCS active read");
                Assert.NotEmpty(CallSites(text, "CheckEngineTransition"));
                Assert.NotEmpty(CallSites(text, "ProcessRcsDebounce"));
                Assert.Empty(PollProblems(label, text));
            }
        }

        [Fact]
        public void TheDebounceAndRcsPower_AreTheRecordersOwn()
        {
            Assert.Equal(8, FlightRecorder.RcsDebounceFrameThreshold);
            string text = Read("Recording", "FlightRecorder.EngineAndRcs.cs");
            string body = PartParityStateRulesTests.MethodBody(text, "internal static void ProcessRcsDebounce(");
            Assert.True(body != null, "FlightRecorder.ProcessRcsDebounce moved; this cell is vacuous");
            Assert.Contains("ShouldStartRcsRecording(count, RcsDebounceFrameThreshold)", body);
            Assert.Contains("ComputeRcsPower(thrustForces, thrusterPower)", body);
        }

        [Fact]
        public void EveryPartParityFile_PollsWithTheRecordersReads()
        {
            var problems = SweepFiles().SelectMany(f => PollProblems(f.label, f.text)).ToList();
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void ThePin_RejectsTheReadsAPollerMustNotUse()
        {
            // The post-switch watch's activity shape, a dropped rcsEnabled, a swapped argument.
            Assert.NotEmpty(PollProblems("t", "x = CheckEngineTransition(k, p, m, n, "
                + "engine.EngineIgnited && engine.isOperational, engine.currentThrottle, a, b, ut, ev);"));
            Assert.NotEmpty(PollProblems("t", "bool active = rcs.rcs_active; ProcessRcsDebounce(k, p, m, n, "
                + "active, rcs.thrustForces, rcs.thrusterPower, ut, f, a, t, ev);"));
            Assert.NotEmpty(PollProblems("t", "bool i = ShouldRecordEngineAsIgnited(e.EngineIgnited, e.isOperational, e.maxThrust);"));
            Assert.NotEmpty(PollProblems("t", "float p = ComputeRecordedEnginePower(e.currentThrottle, e.maxThrust, e.finalThrust);"));
            Assert.NotEmpty(PollProblems("t", "bool active = rcs.rcs_active && rcs.rcsEnabled; "
                + "ProcessRcsDebounce(k, p, m, n, active, rcs.thrustForces, 1f, ut, f, a, t, ev);"));
            // And accepts the recorder's own shape under another receiver name.
            Assert.Empty(PollProblems("t",
                "bool ig = FlightRecorder.ShouldRecordEngineAsIgnited(eng.EngineIgnited, eng.isOperational, eng.finalThrust);"
                + "float pw = FlightRecorder.ComputeRecordedEnginePower(eng.currentThrottle, eng.finalThrust, eng.maxThrust);"
                + "FlightRecorder.CheckEngineTransition(k, p, m, n, ig, pw, a, b, ut, ev);"
                + "bool active = r.rcs_active && r.rcsEnabled;"
                + "FlightRecorder.ProcessRcsDebounce(k, p, m, n, active, r.thrustForces, r.thrusterPower, ut, f, a, t, ev);"));
        }
    }
}
