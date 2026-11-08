using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins how a scene's <see cref="ParsekUI"/> owns, draws and reaches its sub-windows:
    /// <list type="bullet">
    ///   <item><description>the window set: both constructors build the same ten windows,
    ///     each once, in one fixed order, before the instance goes live and before the
    ///     signal hooks;</description></item>
    ///   <item><description>the per-scene draw sequence: FLIGHT draws eight windows after
    ///     the main window, the Space Center the same seven without Real Spawn
    ///     Control;</description></item>
    ///   <item><description>dispatch: each draw entry point draws its own window, each
    ///     launcher and toggle flips its own window, and the open, hover and teardown
    ///     calls reach their own windows in their own order.</description></item>
    /// </list>
    ///
    /// <para>Behavioral where headless code can observe it (the set, the toggles, the
    /// opens). Source order otherwise, read from each method's own brace-matched,
    /// comment-stripped body: IMGUI draws in call order, and the order of these calls IS
    /// the frame's window stacking and the teardown's lock release order.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ParsekUIWindowSetCharacterizationTests : IDisposable
    {
        private readonly List<ParsekUI> built = new List<ParsekUI>();
        private readonly Action savedTimelineHook;

        public ParsekUIWindowSetCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekUI.ResetUiComplexityModeForTesting();
            savedTimelineHook = LedgerOrchestrator.OnTimelineDataChanged;
            LedgerOrchestrator.OnTimelineDataChanged = null;
            DisplayNames.ResetSubscribersForTesting();
        }

        public void Dispose()
        {
            foreach (ParsekUI ui in built)
                CleanupIgnoringUnityTeardown(ui);
            LedgerOrchestrator.OnTimelineDataChanged = savedTimelineHook;
            DisplayNames.ResetSubscribersForTesting();
            ParsekUI.ResetUiComplexityModeForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // Cleanup resets the cached window styles last, which reads a Unity native:
        // SecurityException on the Windows .NET host, MissingMethodException on mono.
        private static void CleanupIgnoringUnityTeardown(ParsekUI ui)
        {
            try { ui.Cleanup(); }
            catch (SecurityException) { }
            catch (MissingMethodException) { }
        }

        private ParsekUI BuildKsc()
        {
            var ui = new ParsekUI(UIMode.KSC);
            built.Add(ui);
            return ui;
        }

        private ParsekUI BuildFlightWithoutHost()
        {
            var ui = new ParsekUI((ParsekFlight)null);
            built.Add(ui);
            return ui;
        }

        // Every window a ParsekUI reaches, by the name its accessor gives it.
        private static List<KeyValuePair<string, object>> Windows(ParsekUI ui)
        {
            return new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("RecordingsTable", ui.GetRecordingsTableUI()),
                new KeyValuePair<string, object>("Missions", ui.GetMissionsUI()),
                new KeyValuePair<string, object>("SpawnControl", ui.GetSpawnControlUI()),
                new KeyValuePair<string, object>("Timeline", ui.GetTimelineUI()),
                new KeyValuePair<string, object>("Kerbals", ui.GetKerbalsUI()),
                new KeyValuePair<string, object>("TestRunner", ui.GetTestRunnerUI()),
                new KeyValuePair<string, object>("Settings", ui.GetSettingsWindowUI()),
                new KeyValuePair<string, object>("Logistics", ui.GetLogisticsUI()),
                new KeyValuePair<string, object>("StructureList", ui.GetStructureListUI()),
                new KeyValuePair<string, object>("RouteHistory", ui.GetRouteHistoryUI()),
            };
        }

        // The open flag of every window that has one, by name.
        private static Dictionary<string, bool> OpenFlags(ParsekUI ui)
        {
            return new Dictionary<string, bool>
            {
                { "RecordingsTable", ui.GetRecordingsTableUI().IsOpen },
                { "SpawnControl", ui.GetSpawnControlUI().IsOpen },
                { "Timeline", ui.GetTimelineUI().IsOpen },
                { "Kerbals", ui.GetKerbalsUI().IsOpen },
                { "TestRunner", ui.GetTestRunnerUI().IsOpen },
                { "Settings", ui.GetSettingsWindowUI().IsOpen },
                { "Logistics", ui.GetLogisticsUI().IsOpen },
                { "StructureList", ui.GetStructureListUI().IsOpen },
                { "RouteHistory", ui.GetRouteHistoryUI().IsOpen },
            };
        }

        private static string OpenSet(ParsekUI ui)
        {
            return string.Join(",", OpenFlags(ui).Where(kv => kv.Value).Select(kv => kv.Key));
        }

        // ------------------------------------------------------------------
        // The window set
        // ------------------------------------------------------------------

        [Fact]
        public void BothConstructors_BuildTheSameTenWindows_EachItsOwnInstance()
        {
            foreach (ParsekUI ui in new[] { BuildKsc(), BuildFlightWithoutHost() })
            {
                List<KeyValuePair<string, object>> windows = Windows(ui);
                foreach (KeyValuePair<string, object> w in windows)
                    Assert.True(w.Value != null, "no " + w.Key + " window");
                Assert.Equal(windows.Count,
                    windows.Select(w => w.Value).Distinct(ReferenceEqualityComparer.Instance).Count());
                Assert.False(ui.GetStructureListUI().IsRouteHistory);
                Assert.True(ui.GetRouteHistoryUI().IsRouteHistory);
                // The table's runtime-only empty groups are the ParsekUI's own list.
                Assert.Same(ui.GetRecordingsTableUI().KnownEmptyGroups, ui.KnownEmptyGroups);
            }
        }

        [Fact]
        public void TwoInstances_ShareNoWindow()
        {
            ParsekUI previous = BuildKsc();
            ParsekUI next = BuildKsc();
            List<KeyValuePair<string, object>> a = Windows(previous);
            List<KeyValuePair<string, object>> b = Windows(next);
            for (int i = 0; i < a.Count; i++)
                Assert.False(ReferenceEquals(a[i].Value, b[i].Value), a[i].Key + " is shared");
        }

        [Fact]
        public void Constructed_EveryWindowStartsClosed_AndTheNewestInstanceIsLive()
        {
            ParsekUI previous = BuildKsc();
            Assert.Same(previous, ParsekUI.ActiveInstance);
            ParsekUI next = BuildFlightWithoutHost();
            Assert.Same(next, ParsekUI.ActiveInstance);
            Assert.Equal("", OpenSet(previous));
            Assert.Equal("", OpenSet(next));
        }

        // ------------------------------------------------------------------
        // Toggle and open dispatch
        // ------------------------------------------------------------------

        [Fact]
        public void EachToggle_FlipsOnlyItsOwnWindow()
        {
            ParsekUI ui = BuildKsc();

            ui.ToggleRecordingsWindow();
            Assert.Equal("RecordingsTable", OpenSet(ui));
            ui.ToggleRecordingsWindow();
            Assert.Equal("", OpenSet(ui));

            ui.ToggleSettingsWindow();
            Assert.Equal("Settings", OpenSet(ui));
            ui.ToggleSettingsWindow();
            Assert.Equal("", OpenSet(ui));

            ui.ToggleTestRunner();
            Assert.Equal("TestRunner", OpenSet(ui));
            ui.ToggleTestRunner();
            Assert.Equal("", OpenSet(ui));
        }

        [Fact]
        public void TheTwoLogOpens_EachOpenOnlyTheirOwnInstance()
        {
            ParsekUI ui = BuildKsc();

            ui.OpenStructureWindowForMission("tree-1", null, "Mission");
            Assert.Equal("StructureList", OpenSet(ui));

            ui.OpenRouteHistoryWindow("route-1", "Route");
            Assert.Equal("StructureList,RouteHistory", OpenSet(ui));
        }

        // ------------------------------------------------------------------
        // Source order
        // ------------------------------------------------------------------

        private static void AssertCallsInOrder(string where, string text, string[] calls)
        {
            int at = -1;
            string previous = "<start>";
            foreach (string call in calls)
            {
                int first = text.IndexOf(call, StringComparison.Ordinal);
                Assert.True(first >= 0, where + ": no " + call);
                Assert.True(text.IndexOf(call, first + 1, StringComparison.Ordinal) < 0,
                    where + ": " + call + " appears twice");
                Assert.True(first > at, where + ": " + call + " must come after " + previous);
                at = first;
                previous = call;
            }
        }

        private static string Body(string file, string signature)
            => MainWindowLauncherOrderTests.MethodBodyText(file, signature);

        private static readonly string[] SpaceCenterDrawSequence =
        {
            "ui.DrawWindow,",
            "ui.DrawRecordingsWindowIfOpen(windowRect);",
            "ui.DrawTimelineWindowIfOpen(windowRect);",
            "ui.DrawKerbalsWindowIfOpen(windowRect);",
            "ui.DrawLogisticsWindowIfOpen(windowRect);",
            "ui.DrawStructureWindowIfOpen(windowRect);",
            "ui.DrawSettingsWindowIfOpen(windowRect);",
            "ui.DrawTestRunnerWindowIfOpen(windowRect, this);",
        };

        [Fact]
        public void Flight_DrawsTheMainWindowThenEightWindows_InOrder()
        {
            string text = Body("ParsekFlight.cs", "private void DrawFlightWindows()");
            var flight = new List<string>(SpaceCenterDrawSequence);
            flight.Insert(1, "ui.LogMainWindowPosition(windowRect);");
            flight.Insert(flight.Count - 1, "ui.DrawSpawnControlWindowIfOpen(windowRect);");
            AssertCallsInOrder("ParsekFlight.DrawFlightWindows", text, flight.ToArray());
            Assert.Equal(8, CountDrawEntryCalls(text));
        }

        [Fact]
        public void SpaceCenter_DrawsTheSameWindowsWithoutRealSpawnControl_InOrder()
        {
            string text = Body("ParsekKSC.cs", "private void DrawKscWindows()");
            AssertCallsInOrder("ParsekKSC.DrawKscWindows", text, SpaceCenterDrawSequence);
            Assert.DoesNotContain("DrawSpawnControlWindowIfOpen", text);
            Assert.Equal(7, CountDrawEntryCalls(text));
        }

        private static int CountDrawEntryCalls(string text)
        {
            int count = 0;
            int at = 0;
            while ((at = text.IndexOf("WindowIfOpen(", at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at++;
            }
            return count;
        }

        // Each draw entry point's whole body: the one window it draws, with its arguments.
        public static IEnumerable<object[]> DrawEntryBodies => new[]
        {
            new object[] { "public void DrawRecordingsWindowIfOpen(Rect mainWindowRect)",
                new[] { "recordingsTableUI.DrawIfOpen(mainWindowRect);" } },
            new object[] { "public void DrawTimelineWindowIfOpen(Rect mainWindowRect)",
                new[] { "timelineUI.DrawIfOpen(mainWindowRect);" } },
            new object[] { "public void DrawKerbalsWindowIfOpen(Rect mainWindowRect)",
                new[] { "kerbalsUI.DrawIfOpen(mainWindowRect);" } },
            new object[] { "public void DrawLogisticsWindowIfOpen(Rect mainWindowRect)",
                new[] { "logisticsUI.DrawIfOpen(mainWindowRect);" } },
            new object[] { "public void DrawStructureWindowIfOpen(Rect mainWindowRect)",
                new[] { "structureListUI.DrawIfOpen(mainWindowRect);",
                        "routeHistoryUI.DrawIfOpen(mainWindowRect);" } },
            new object[] { "public void DrawSettingsWindowIfOpen(Rect mainWindowRect)",
                new[] { "settingsUI.DrawIfOpen(mainWindowRect);" } },
            new object[] { "public void DrawSpawnControlWindowIfOpen(Rect mainWindowRect)",
                new[] { "spawnControlUI.DrawIfOpen(mainWindowRect, flight, InFlight);" } },
            new object[] { "public void DrawTestRunnerWindowIfOpen(Rect mainWindowRect, MonoBehaviour host)",
                new[] { "testRunnerUI.DrawIfOpen(mainWindowRect, host);" } },
        };

        [Theory]
        [MemberData(nameof(DrawEntryBodies))]
        public void EachDrawEntryPoint_DrawsOnlyItsOwnWindow(string signature, string[] calls)
        {
            string body = Body("ParsekUI.cs", signature);
            string expected = "{" + string.Concat(calls) + "}";
            Assert.Equal(string.Concat(expected.Where(c => !char.IsWhiteSpace(c))),
                string.Concat(body.Where(c => !char.IsWhiteSpace(c))));
        }

        // Each main-window launcher flips its own window: the first window toggle after a
        // launcher's label, and before the next launcher, is that launcher's.
        [Fact]
        public void EachMainWindowLauncher_TogglesItsOwnWindow()
        {
            string text = MainWindowLauncherOrderTests.MainWindowDrawText();
            var launchers = new[]
            {
                new[] { "\"Real Spawn Control ({0})\"", "spawnControlUI.IsOpen = !spawnControlUI.IsOpen;" },
                new[] { "\"Timeline\"", "timelineUI.IsOpen = !timelineUI.IsOpen;" },
                new[] { "\"Missions\"", "ToggleRecordingsWindow();" },
                new[] { "\"Logistics\"", "IsOpen = !IsOpen;" },
                new[] { "GetKerbalsMainButtonLabel()", "kerbalsUI.IsOpen = !kerbalsUI.IsOpen;" },
                new[] { "\"Settings\"", "ToggleSettingsWindow();" },
            };
            for (int i = 0; i < launchers.Length; i++)
            {
                int label = text.IndexOf(launchers[i][0], StringComparison.Ordinal);
                Assert.True(label >= 0, "main window: no launcher " + launchers[i][0]);
                int end = i + 1 < launchers.Length
                    ? text.IndexOf(launchers[i + 1][0], label, StringComparison.Ordinal)
                    : text.IndexOf("\"Close\"", label, StringComparison.Ordinal);
                Assert.True(end > label, "main window: launcher order moved after " + launchers[i][0]);
                string span = text.Substring(label, end - label);
                Assert.True(span.IndexOf(launchers[i][1], StringComparison.Ordinal) >= 0,
                    "main window: " + launchers[i][0] + " no longer toggles via " + launchers[i][1]);
                foreach (string other in launchers.Where((_, j) => j != i).Select(l => l[1]))
                    Assert.True(span.IndexOf(other, StringComparison.Ordinal) < 0,
                        "main window: " + launchers[i][0] + " also toggles via " + other);
            }
        }

        [Fact]
        public void TheThreeToggles_FlipTheirOwnField()
        {
            AssertCallsInOrder("ParsekUI.ToggleRecordingsWindow",
                Body("ParsekUI.cs", "internal void ToggleRecordingsWindow()"),
                new[] { "recordingsTableUI.IsOpen = !recordingsTableUI.IsOpen;" });
            AssertCallsInOrder("ParsekUI.ToggleSettingsWindow",
                Body("ParsekUI.cs", "internal void ToggleSettingsWindow()"),
                new[] { "settingsUI.IsOpen = !settingsUI.IsOpen;" });
            AssertCallsInOrder("ParsekUI.ToggleTestRunner",
                Body("ParsekUI.cs", "internal void ToggleTestRunner()"),
                new[] { "testRunnerUI.IsOpen = !testRunnerUI.IsOpen;" });
        }

        // The click-through hover check reads three windows, in this order, short-circuit.
        [Fact]
        public void HoverCheck_ReadsTheTableThenSettingsThenTheTestRunner()
        {
            string body = Body("ParsekUI.cs",
                "internal bool IsMouseOverOpenAuxiliaryWindows(Vector2 mousePosition)");
            Assert.Equal(
                "{returnrecordingsTableUI.IsMouseOverOpenWindow(mousePosition)"
                + "||settingsUI.IsMouseOverOpenWindow(mousePosition)"
                + "||testRunnerUI.IsMouseOverOpenWindow(mousePosition);}",
                string.Concat(body.Where(c => !char.IsWhiteSpace(c))));
        }

        // Teardown: the signal unhooks first, then each window's input lock in this order,
        // then the style reset. The Logistics window and the test runner release no lock
        // here (the close handler and their own DrawIfOpen prologue do).
        [Fact]
        public void Cleanup_UnhooksThenReleasesTheLocksInOrder()
        {
            string body = Body("ParsekUI.cs", "public void Cleanup()");
            AssertCallsInOrder("ParsekUI.Cleanup", body, new[]
            {
                "timelineUI.UnsubscribeTimelineDataChanged();",
                "kerbalsUI.UnsubscribeTimelineDataChanged();",
                "DisplayNames.DisplayNamesChanged -= OnDisplayNamesChanged;",
                "kerbalsUI.UnsubscribeLiveCrewEvents();",
                "recordingsTableUI.ReleaseInputLock();",
                "timelineUI.ReleaseInputLock();",
                "kerbalsUI.ReleaseInputLock();",
                "settingsUI.ReleaseInputLock();",
                "spawnControlUI.ReleaseInputLock();",
                "structureListUI.ReleaseInputLock();",
                "routeHistoryUI.ReleaseInputLock();",
                "ResetCachedWindowStylesForSceneChange();",
            });
            Assert.DoesNotContain("logisticsUI.ReleaseInputLock", body);
            Assert.DoesNotContain("testRunnerUI.ReleaseInputLock", body);
        }

        // Construction: the ten windows in this order, then the instance goes live, the
        // mode latch seeds, and the signal hooks attach. Both constructors.
        [Theory]
        [InlineData("public ParsekUI(ParsekFlight flight)")]
        [InlineData("public ParsekUI(UIMode mode)")]
        public void Constructor_BuildsTheWindowsInOrder_BeforeGoingLive(string signature)
        {
            string body = Body("ParsekUI.cs", signature);
            AssertCallsInOrder("ParsekUI " + signature, body, new[]
            {
                "this.recordingsTableUI = new RecordingsTableUI(this);",
                "this.missionsUI = new MissionsWindowUI(this);",
                "this.spawnControlUI = new SpawnControlUI(this);",
                "this.timelineUI = new TimelineWindowUI(this);",
                "this.kerbalsUI = new KerbalsWindowUI(this);",
                "this.testRunnerUI = new TestRunnerUI(this);",
                "this.settingsUI = new SettingsWindowUI(this);",
                "this.logisticsUI = new LogisticsWindowUI(this);",
                "this.structureListUI = new StructureListWindowUI(this);",
                "this.routeHistoryUI = new StructureListWindowUI(this, routeHistory: true);",
                "activeInstance = this;",
                "InitializeAppliedUiComplexityModeFromSettings();",
                "timelineUI.SubscribeTimelineDataChanged();",
                "kerbalsUI.SubscribeTimelineDataChanged();",
                "DisplayNames.DisplayNamesChanged += OnDisplayNamesChanged;",
                "kerbalsUI.SubscribeLiveCrewEvents();",
            });
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
