using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The automation-only window helpers for parallel harness runs: the own-process window
    /// choice, the boot-time placement run (env parsing, the per-frame state machine against
    /// a fake platform, the one log line), the focus-lock suppression patch's targets and
    /// gate, and source gates on the wiring.
    /// </summary>
    [Collection("Sequential")]
    public class AutomationWindowPlacementTests
    {
        // The line format the harness reads (W2 falls back to minimising unless desktop=moved).
        private static readonly Regex LineShape = new Regex(
            @"^window placement desktop=(moved|failed hr=0x[0-9A-F]{8}|skipped|no-window) "
            + @"foreground=(returned|not-foreground|failed|skipped) hwnd=0x[0-9A-F]+ "
            + @"moveAttempts=[0-9]+ elapsedSec=[0-9]+\.[0-9]{2}( [A-Za-z]+=[^ ]+)*$");

        private static readonly Guid Desktop = new Guid("11111111-2222-3333-4444-555555555555");

        // ---------------------------------------------------------------- window choice

        private static OwnProcessWindowFinder.Candidate Cand(long h, uint pid, bool visible,
            string cls, bool owned = false)
        {
            return new OwnProcessWindowFinder.Candidate
            {
                Handle = new IntPtr(h), ProcessId = pid, Visible = visible,
                ClassName = cls, Owned = owned,
            };
        }

        [Fact]
        public void ChooseIndex_IgnoresOtherProcesses_EvenAVisibleUnityWindowFirstInZOrder()
        {
            var list = new List<OwnProcessWindowFinder.Candidate>
            {
                Cand(10, 99, true, "UnityWndClass"),   // another KSP
                Cand(20, 7, true, "UnityWndClass"),
            };
            Assert.Equal(1, OwnProcessWindowFinder.ChooseIndex(list, 7));
        }

        [Fact]
        public void ChooseIndex_PrefersVisibleUnity_ThenHiddenUnity_ThenVisibleOther()
        {
            var list = new List<OwnProcessWindowFinder.Candidate>
            {
                Cand(1, 7, true, "IME"),
                Cand(2, 7, false, "UnityWndClass"),
                Cand(3, 7, true, "UnityWndClass"),
            };
            Assert.Equal(2, OwnProcessWindowFinder.ChooseIndex(list, 7));
            list.RemoveAt(2);
            Assert.Equal(1, OwnProcessWindowFinder.ChooseIndex(list, 7));
            list.RemoveAt(1);
            Assert.Equal(0, OwnProcessWindowFinder.ChooseIndex(list, 7));
        }

        [Fact]
        public void ChooseIndex_SkipsOwnedHiddenNonUnityAndNullHandles()
        {
            var list = new List<OwnProcessWindowFinder.Candidate>
            {
                Cand(1, 7, true, "UnityWndClass", owned: true),
                Cand(2, 7, false, "MSCTFIME UI"),
                Cand(0, 7, true, "UnityWndClass"),
            };
            Assert.Equal(-1, OwnProcessWindowFinder.ChooseIndex(list, 7));
            Assert.Equal(-1, OwnProcessWindowFinder.ChooseIndex(null, 7));
            Assert.Equal(-1, OwnProcessWindowFinder.ChooseIndex(
                new List<OwnProcessWindowFinder.Candidate>(), 7));
        }

        [Fact]
        public void FindOwnWindow_OffWindows_AnswersZeroWithoutBinding()
        {
            if (OwnProcessWindowFinder.IsWindowsHost()) return;
            Assert.Equal(IntPtr.Zero, OwnProcessWindowFinder.FindOwnWindow(out bool visible));
            Assert.False(visible);
        }

        // ---------------------------------------------------------------- env parsing

        [Theory]
        [InlineData(null, "Unset")]
        [InlineData("", "Unset")]
        [InlineData("   ", "Unset")]
        [InlineData("11111111-2222-3333-4444-555555555555", "Valid")]
        [InlineData("{11111111-2222-3333-4444-555555555555}", "Valid")]
        [InlineData(" 11111111-2222-3333-4444-555555555555 ", "Valid")]
        [InlineData("00000000-0000-0000-0000-000000000000", "Invalid")]
        [InlineData("desktop2", "Invalid")]
        [InlineData("2", "Invalid")]
        public void ParseDesktopId(string raw, string expectedName)
        {
            var expected = (PlacementEnvState)Enum.Parse(typeof(PlacementEnvState), expectedName);
            PlacementEnvState state = WindowPlacementRun.ParseDesktopId(raw, out Guid id);
            Assert.Equal(expected, state);
            Assert.Equal(expected == PlacementEnvState.Valid ? Desktop : Guid.Empty, id);
        }

        [Theory]
        [InlineData(null, "Unset", 0L)]
        [InlineData("", "Unset", 0L)]
        [InlineData("132456", "Valid", 132456L)]
        [InlineData(" 42 ", "Valid", 42L)]
        [InlineData("0", "Invalid", 0L)]
        [InlineData("-5", "Invalid", 0L)]
        [InlineData("0x1A", "Invalid", 0L)]
        [InlineData("1,000", "Invalid", 0L)]
        [InlineData("abc", "Invalid", 0L)]
        public void ParseReturnHwnd(string raw, string expectedName, long hwnd)
        {
            var expected = (PlacementEnvState)Enum.Parse(typeof(PlacementEnvState), expectedName);
            Assert.Equal(expected, WindowPlacementRun.ParseReturnHwnd(raw, out long parsed));
            Assert.Equal(hwnd, parsed);
        }

        [Fact]
        public void EnvVarNames_AreTheHarnessContract()
        {
            Assert.Equal("PARSEK_AUTOMATION_DESKTOP_ID", WindowPlacementRun.DesktopIdEnvVar);
            Assert.Equal("PARSEK_AUTOMATION_RETURN_FOREGROUND", WindowPlacementRun.ReturnForegroundEnvVar);
        }

        [Theory]
        [InlineData(0, "0x00000000")]
        [InlineData(unchecked((int)0x8002802B), "0x8002802B")]
        [InlineData(unchecked((int)0x80070005), "0x80070005")]
        public void FormatHr(int hr, string expected)
        {
            Assert.Equal(expected, WindowPlacementRun.FormatHr(hr));
        }

        // ---------------------------------------------------------------- the run

        private sealed class FakePlatform : IWindowPlacementPlatform
        {
            internal bool Windows = true;
            internal IntPtr Window = new IntPtr(0x1234);
            internal bool Visible = true;
            internal Func<int, int> MoveResult = attempt => 0;
            internal IntPtr Foreground = new IntPtr(0x1234);
            internal bool ReturnIsWindow = true;
            internal bool SetForegroundOk = true;
            internal int MoveCalls;
            internal readonly List<IntPtr> SetForegroundCalls = new List<IntPtr>();
            internal int FindCalls;

            public bool IsWindows => Windows;

            public IntPtr FindOwnWindow(out bool visible)
            {
                FindCalls++;
                visible = Visible;
                return Window;
            }

            public int MoveWindowToDesktop(IntPtr hwnd, Guid desktopId, out string error)
            {
                MoveCalls++;
                int hr = MoveResult(MoveCalls);
                error = hr == 0 ? null : "COMException";
                return hr;
            }

            public IntPtr GetForegroundWindow() => Foreground;
            public bool IsWindow(IntPtr hwnd) => ReturnIsWindow;

            public bool SetForegroundWindow(IntPtr hwnd)
            {
                SetForegroundCalls.Add(hwnd);
                return SetForegroundOk;
            }
        }

        private static WindowPlacementConfig Config(string desktop, string ret)
            => WindowPlacementRun.ParseConfig(desktop, ret);

        private static string RunToEnd(WindowPlacementRun run, IWindowPlacementPlatform p,
            double step = 0.05, double limit = 60.0)
        {
            for (double t = 0.0; t <= limit; t += step)
            {
                if (run.Step(t, p))
                {
                    Assert.Matches(LineShape, run.Line);
                    return run.Line;
                }
            }
            throw new InvalidOperationException("the run never finished");
        }

        [Fact]
        public void NoEnv_FinishesAtOnce_WithoutTouchingTheWindow()
        {
            var p = new FakePlatform();
            var run = new WindowPlacementRun(Config(null, null), 0.0);
            Assert.True(run.Step(0.0, p));
            Assert.Equal("window placement desktop=skipped foreground=skipped hwnd=0x0 moveAttempts=0 elapsedSec=0.00", run.Line);
            Assert.Equal(0, p.FindCalls);
            Assert.Equal(0, p.MoveCalls);
        }

        [Fact]
        public void NotWindows_Skips()
        {
            var p = new FakePlatform { Windows = false };
            string line = RunToEnd(new WindowPlacementRun(Config(Desktop.ToString(), "77"), 0.0), p);
            Assert.StartsWith("window placement desktop=skipped foreground=skipped", line);
            Assert.Contains("fgReason=not-windows", line);
            Assert.Equal(0, p.MoveCalls);
        }

        [Fact]
        public void Moved_ThenForegroundReturned()
        {
            var p = new FakePlatform();
            string line = RunToEnd(new WindowPlacementRun(Config(Desktop.ToString(), "77"), 0.0), p);
            Assert.StartsWith("window placement desktop=moved foreground=returned hwnd=0x1234 moveAttempts=1 ", line);
            Assert.Equal(new[] { new IntPtr(77) }, p.SetForegroundCalls.ToArray());
        }

        [Fact]
        public void MoveRetried_UntilItSucceeds()
        {
            var p = new FakePlatform { MoveResult = n => n < 3 ? unchecked((int)0x8002802B) : 0 };
            string line = RunToEnd(new WindowPlacementRun(Config(Desktop.ToString(), null), 0.0), p);
            Assert.StartsWith("window placement desktop=moved foreground=skipped", line);
            Assert.Contains("moveAttempts=3", line);
            Assert.DoesNotContain("error=", line);
            Assert.Empty(p.SetForegroundCalls);
        }

        [Fact]
        public void MoveRetriesAreSpaced_NotOnePerFrame()
        {
            var p = new FakePlatform { MoveResult = n => unchecked((int)0x80070005) };
            var run = new WindowPlacementRun(Config(Desktop.ToString(), null), 0.0);
            for (double t = 0.0; t < 1.0; t += 0.01) run.Step(t, p);
            Assert.InRange(p.MoveCalls, 4, 5);
        }

        [Fact]
        public void MoveNeverSucceeds_FailsWithLastHr_AndStillReturnsTheForeground()
        {
            var p = new FakePlatform { MoveResult = n => unchecked((int)0x80070005) };
            string line = RunToEnd(new WindowPlacementRun(Config(Desktop.ToString(), "77"), 0.0), p);
            Assert.StartsWith("window placement desktop=failed hr=0x80070005 foreground=returned", line);
            Assert.Contains("error=COMException", line);
            Assert.Single(p.SetForegroundCalls);
        }

        [Fact]
        public void NoWindow_WithinTheWait_ReportsNoWindow()
        {
            var p = new FakePlatform { Window = IntPtr.Zero };
            var run = new WindowPlacementRun(Config(Desktop.ToString(), "77"), 0.0);
            Assert.False(run.Step(WindowPlacementRun.WindowWaitSeconds - 0.01, p));
            Assert.True(run.Step(WindowPlacementRun.WindowWaitSeconds, p));
            Assert.StartsWith("window placement desktop=no-window foreground=skipped hwnd=0x0", run.Line);
            Assert.Equal(0, p.MoveCalls);
        }

        [Fact]
        public void NoWindow_WithOnlyAReturn_ReportsSkippedDesktop()
        {
            var p = new FakePlatform { Window = IntPtr.Zero };
            string line = RunToEnd(new WindowPlacementRun(Config(null, "77"), 0.0), p);
            Assert.StartsWith("window placement desktop=skipped foreground=skipped", line);
        }

        [Fact]
        public void HiddenWindow_IsWaitedFor_ThenUsedAtTheDeadline()
        {
            var p = new FakePlatform { Visible = false };
            var run = new WindowPlacementRun(Config(Desktop.ToString(), null), 0.0);
            Assert.False(run.Step(1.0, p));
            Assert.Equal(0, p.MoveCalls);
            Assert.True(run.Step(WindowPlacementRun.WindowWaitSeconds, p));
            Assert.StartsWith("window placement desktop=moved", run.Line);
        }

        [Fact]
        public void NotForeground_WaitsTheGrace_ThenReportsNotForeground()
        {
            var p = new FakePlatform { Foreground = new IntPtr(0x999) };
            var run = new WindowPlacementRun(Config(null, "77"), 0.0);
            Assert.False(run.Step(0.0, p));
            Assert.False(run.Step(WindowPlacementRun.ForegroundGraceSeconds - 0.1, p));
            Assert.True(run.Step(WindowPlacementRun.ForegroundGraceSeconds, p));
            Assert.StartsWith("window placement desktop=skipped foreground=not-foreground", run.Line);
            Assert.Empty(p.SetForegroundCalls);
        }

        [Fact]
        public void ForegroundArrivingInsideTheGrace_IsReturned()
        {
            var p = new FakePlatform { Foreground = new IntPtr(0x999) };
            var run = new WindowPlacementRun(Config(null, "77"), 0.0);
            Assert.False(run.Step(0.0, p));
            p.Foreground = p.Window;
            Assert.True(run.Step(0.5, p));
            Assert.StartsWith("window placement desktop=skipped foreground=returned", run.Line);
        }

        [Fact]
        public void ReturnWindowGone_FailsWithoutCallingSetForeground()
        {
            var p = new FakePlatform { ReturnIsWindow = false };
            string line = RunToEnd(new WindowPlacementRun(Config(null, "77"), 0.0), p);
            Assert.StartsWith("window placement desktop=skipped foreground=failed", line);
            Assert.Contains("fgReason=return-window-gone", line);
            Assert.Empty(p.SetForegroundCalls);
        }

        [Fact]
        public void SetForegroundRefused_IsFailed()
        {
            var p = new FakePlatform { SetForegroundOk = false };
            string line = RunToEnd(new WindowPlacementRun(Config(null, "77"), 0.0), p);
            Assert.StartsWith("window placement desktop=skipped foreground=failed", line);
            Assert.Contains("fgReason=set-foreground-refused", line);
        }

        [Fact]
        public void InvalidEnv_IsNamed_AndTreatedAsUnset()
        {
            var p = new FakePlatform();
            var run = new WindowPlacementRun(Config("desktop2", "0x10"), 0.0);
            Assert.True(run.Step(0.0, p));
            Assert.Matches(LineShape, run.Line);
            Assert.Contains("desktop=skipped foreground=skipped", run.Line);
            Assert.Contains("desktopEnv=invalid", run.Line);
            Assert.Contains("returnEnv=invalid", run.Line);
            Assert.Equal(0, p.MoveCalls);
        }

        [Fact]
        public void FinishOnException_ReportsFailed_AndIsTerminal()
        {
            var run = new WindowPlacementRun(Config(Desktop.ToString(), "77"), 0.0);
            run.FinishOnException(1.5, new TypeLoadException("x"));
            Assert.True(run.Done);
            Assert.Matches(LineShape, run.Line);
            Assert.StartsWith("window placement desktop=failed hr=0x80004005 foreground=skipped", run.Line);
            Assert.Contains("error=TypeLoadException", run.Line);
            Assert.True(run.Step(2.0, new FakePlatform()));
        }

        [Fact]
        public void Line_IsCultureInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                string line = WindowPlacementRun.FormatLine("moved", 0, "returned", 0xABC, 2, 1.5,
                    Config(null, null), null, null);
                Assert.Equal("window placement desktop=moved foreground=returned hwnd=0xABC moveAttempts=2 elapsedSec=1.50", line);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---------------------------------------------------------------- focus-lock patch

        [Fact]
        public void FocusPatch_TargetsResolveToTheFivePrivateDecompiledMethods()
        {
            var targets = AutomationFocusLockSuppressionPatch.TargetsForTesting();
            Assert.Equal(new[] { "FlightDriver", "EditorDriver", "SpaceCenterMain", "UISpaceCenter", "MainMenu" },
                targets.Select(t => t.Owner.Name).ToArray());
            Assert.Equal(new[]
                {
                    "flightDriver_ApplicationFocus", "EditorDriver_AppFocus", "ksc_ApplicationFocus",
                    "ksc_ApplicationFocus", "applicationFocus",
                },
                targets.Select(t => t.LockId).ToArray());

            var methods = AutomationFocusLockSuppressionPatch.ResolveTargetMethodsForTesting();
            Assert.Equal(5, methods.Count);
            foreach (MethodBase m in methods)
            {
                Assert.True(m.IsPrivate, m.DeclaringType.Name + " OnApplicationFocus is not private");
                Assert.False(m.IsStatic);
                var ps = m.GetParameters();
                Assert.Single(ps);
                Assert.Equal(typeof(bool), ps[0].ParameterType);
                // The Prefix binds the argument by this name.
                Assert.Equal("focus", ps[0].Name);
            }
        }

        [Fact]
        public void FocusPatch_SkipsOnlyFocusLossWhenArmed()
        {
            Assert.True(AutomationFocusLockSuppressionPatch.ShouldSkipOriginal(true, false));
            Assert.False(AutomationFocusLockSuppressionPatch.ShouldSkipOriginal(true, true));
            Assert.False(AutomationFocusLockSuppressionPatch.ShouldSkipOriginal(false, false));
            Assert.False(AutomationFocusLockSuppressionPatch.ShouldSkipOriginal(false, true));
        }

        [Fact]
        public void FocusPatch_LockIdLookupAndLogLine()
        {
            Assert.Equal("ksc_ApplicationFocus",
                AutomationFocusLockSuppressionPatch.LockIdForOwner(typeof(KSP.UI.Screens.UISpaceCenter)));
            Assert.Equal("applicationFocus", AutomationFocusLockSuppressionPatch.LockIdForOwner(typeof(MainMenu)));
            Assert.Equal("?", AutomationFocusLockSuppressionPatch.LockIdForOwner(typeof(string)));
            Assert.Equal("?", AutomationFocusLockSuppressionPatch.LockIdForOwner(null));
            Assert.Equal("focus-lock-suppressed|applicationFocus",
                AutomationFocusLockSuppressionPatch.RateLimitKey("applicationFocus"));
            Assert.StartsWith("focus lock suppressed scene=FLIGHT owner=FlightDriver lockId=flightDriver_ApplicationFocus ",
                AutomationFocusLockSuppressionPatch.FormatSuppressionMessage(
                    "FLIGHT", "FlightDriver", "flightDriver_ApplicationFocus"));
        }

        [Fact]
        public void FocusPatch_PrepareFollowsTheSeamArmGate()
        {
            MethodInfo prepare = typeof(AutomationFocusLockSuppressionPatch).GetMethod("Prepare",
                BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            Assert.NotNull(prepare);
            string saved = Environment.GetEnvironmentVariable(ParsekTestCommandAddon.EnvVarName);
            try
            {
                Environment.SetEnvironmentVariable(ParsekTestCommandAddon.EnvVarName, null);
                Assert.False((bool)prepare.Invoke(null, null));
                Environment.SetEnvironmentVariable(ParsekTestCommandAddon.EnvVarName, "true");
                Assert.False((bool)prepare.Invoke(null, null));
                Environment.SetEnvironmentVariable(ParsekTestCommandAddon.EnvVarName, "1");
                Assert.True((bool)prepare.Invoke(null, null));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ParsekTestCommandAddon.EnvVarName, saved);
            }
        }

        [Fact]
        public void FocusPatch_IsAHarmonyPatchClassTheSweepPicksUp()
        {
            Assert.NotEmpty(typeof(AutomationFocusLockSuppressionPatch)
                .GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false));
            Assert.NotNull(typeof(AutomationFocusLockSuppressionPatch).GetMethod("Prefix",
                BindingFlags.Static | BindingFlags.NonPublic));
            Assert.NotNull(typeof(AutomationFocusLockSuppressionPatch).GetMethod("TargetMethods",
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        // ---------------------------------------------------------------- source gates

        private static string ReadSource(params string[] parts)
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts")) && Directory.Exists(Path.Combine(dir, "Source")))
                    return File.ReadAllText(Path.Combine(new[] { dir, "Source", "Parsek" }.Concat(parts).ToArray()));
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }

        private static string StripComments(string src)
        {
            src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(src, @"//[^\n]*", "");
        }

        [Theory]
        [InlineData("TestCommands", "ParsekTestCommandAddon.UiPointer.cs")]
        [InlineData("InGameTests", "DisabledHoverEchoImguiTest.cs")]
        public void WindowLookups_UseTheOwnProcessFinder_NeverATitleLookup(string folder, string file)
        {
            string code = StripComments(ReadSource(folder, file));
            Assert.DoesNotContain("FindWindow", code);
            Assert.Contains("OwnProcessWindowFinder.FindOwnWindow(", code);
        }

        [Fact]
        public void UiPointer_KeepsGetActiveWindowAsTheFirstRung()
        {
            string code = StripComments(ReadSource("TestCommands", "ParsekTestCommandAddon.UiPointer.cs"));
            int active = code.IndexOf("hwnd = GetActiveWindow();", StringComparison.Ordinal);
            int own = code.IndexOf("OwnProcessWindowFinder.FindOwnWindow(", StringComparison.Ordinal);
            Assert.True(active >= 0 && own > active);
        }

        [Fact]
        public void Placement_StartsOnlyInsideTheArmedBranchOfAwake()
        {
            string code = StripComments(ReadSource("TestCommands", "ParsekTestCommandAddon.cs"));
            int armedIf = code.IndexOf("if (armed)", StringComparison.Ordinal);
            int start = code.IndexOf("StartCoroutine(RunWindowPlacement())", StringComparison.Ordinal);
            int elseBranch = code.IndexOf("else", armedIf, StringComparison.Ordinal);
            Assert.True(armedIf >= 0, "armed branch not found");
            Assert.True(start > armedIf && start < elseBranch, "placement must start inside the armed branch");
            Assert.Equal(1, Regex.Matches(code, @"StartCoroutine\(RunWindowPlacement\(\)\)").Count);
        }
    }
}
