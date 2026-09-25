using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>The pure half of the <c>StockScreen</c> seam verb and the capture-time
    /// stock-screen record lines.</summary>
    public class TestCommandStockScreenTests
    {
        private static bool Parse(string screen, string act, string item, string part, string pane,
            out StockScreenRequest r, out string reason)
        {
            return TestCommandStockScreen.TryParse(screen, act, item, part, pane, out r, out reason, out _);
        }

        [Fact]
        public void TokenTables_MatchTheEnumOrder()
        {
            Assert.Equal(new[] { "rnd", "astronaut", "missioncontrol", "administration", "facilitymenu",
                "launchsite", "editor", "crewdialog" }, TestCommandStockScreen.ScreenTokens);
            Assert.Equal(new[] { "open", "close", "select", "hover" }, TestCommandStockScreen.ActTokens);
            Assert.Equal("missioncontrol", TestCommandStockScreen.ScreenToken(StockScreenKind.MissionControl));
            Assert.Equal("crewdialog", TestCommandStockScreen.ScreenToken(StockScreenKind.CrewDialog));
            Assert.Equal("hover", TestCommandStockScreen.ActToken(StockScreenAct.Hover));
            Assert.Equal("none", TestCommandStockScreen.ScreenToken(StockScreenKind.None));
        }

        [Theory]
        [InlineData(null, "open", "stockscreen-screen-arg-missing")]
        [InlineData("vab", "open", "stockscreen-screen-arg-invalid")]
        [InlineData("RnD", "open", "stockscreen-screen-arg-invalid")]
        [InlineData("rnd", null, "stockscreen-act-arg-missing")]
        [InlineData("rnd", "click", "stockscreen-act-arg-invalid")]
        [InlineData("crewdialog", "close", "stockscreen-act-unsupported")]
        [InlineData("astronaut", "select", "stockscreen-act-unsupported")]
        [InlineData("missioncontrol", "hover", "stockscreen-act-unsupported")]
        [InlineData("rnd", "select", "stockscreen-item-arg-missing")]
        [InlineData("missioncontrol", "select", "stockscreen-item-arg-missing")]
        [InlineData("facilitymenu", "open", "stockscreen-item-arg-missing")]
        [InlineData("editor", "open", "stockscreen-item-arg-missing")]
        [InlineData("astronaut", "hover", "stockscreen-item-arg-missing")]
        public void TryParse_RefusesWithTheTypedReason(string screen, string act, string expected)
        {
            Assert.False(Parse(screen, act, null, null, null, out _, out string reason));
            Assert.Equal(expected, reason);
            Assert.Contains(expected, TestCommandStockScreen.Reasons);
        }

        [Fact]
        public void TryParse_PartAndPaneAreScopedToTheirScreens()
        {
            Assert.False(Parse("astronaut", "hover", "Bill Kerman", "mk1pod.v2", null, out _, out string r1));
            Assert.Equal(TestCommandStockScreen.PartNotForScreenReason, r1);
            Assert.False(Parse("rnd", "open", null, null, "active", out _, out string r2));
            Assert.Equal(TestCommandStockScreen.PaneNotForScreenReason, r2);
            Assert.False(Parse("missioncontrol", "open", null, null, "Active", out _, out string r3));
            Assert.Equal(TestCommandStockScreen.PaneArgInvalidReason, r3);

            Assert.True(Parse("rnd", "hover", null, "probeCoreSphere.v2", null, out var partHover, out _));
            Assert.Equal("probeCoreSphere.v2", partHover.Part);
            Assert.Null(partHover.Item);
            Assert.True(Parse("editor", "hover", null, "probeCoreSphere.v2", null, out _, out _));
            Assert.True(Parse("missioncontrol", "select", "abc", null, "active", out var select, out _));
            Assert.Equal("active", select.Pane);
            Assert.Equal(StockScreenKind.MissionControl, select.Screen);
            Assert.Equal(StockScreenAct.Select, select.Act);
            // The facility menu has one hover target, so it needs no item.
            Assert.True(Parse("facilitymenu", "hover", null, null, null, out _, out _));
        }

        [Fact]
        public void Supports_EveryScreenOpens_AndOnlyTheListedActsExist()
        {
            foreach (StockScreenKind screen in System.Enum.GetValues(typeof(StockScreenKind)))
            {
                if (screen == StockScreenKind.None) continue;
                Assert.True(TestCommandStockScreen.Supports(screen, StockScreenAct.Open), screen.ToString());
                Assert.False(TestCommandStockScreen.Supports(screen, StockScreenAct.None));
            }
            Assert.False(TestCommandStockScreen.Supports(StockScreenKind.None, StockScreenAct.Open));
            Assert.True(TestCommandStockScreen.Supports(StockScreenKind.LaunchSite, StockScreenAct.Select));
            Assert.False(TestCommandStockScreen.Supports(StockScreenKind.Editor, StockScreenAct.Select));
        }

        [Fact]
        public void IsValidScene_KscEditorAndBoth()
        {
            StockScreenRequest Req(StockScreenKind s, StockScreenAct a) => new StockScreenRequest { Screen = s, Act = a };
            var ksc = TestCommandScene.SpaceCenter;
            var editor = TestCommandScene.Editor;
            Assert.True(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.RnD, StockScreenAct.Open), ksc));
            Assert.False(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.RnD, StockScreenAct.Open), editor));
            Assert.True(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.Astronaut, StockScreenAct.Open), ksc));
            Assert.True(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.Astronaut, StockScreenAct.Open), editor));
            Assert.True(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.Editor, StockScreenAct.Open), ksc));
            Assert.False(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.Editor, StockScreenAct.Open), editor));
            Assert.True(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.Editor, StockScreenAct.Close), editor));
            Assert.False(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.Editor, StockScreenAct.Hover), ksc));
            Assert.True(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.CrewDialog, StockScreenAct.Open), editor));
            Assert.False(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.CrewDialog, StockScreenAct.Open), ksc));
            Assert.False(TestCommandStockScreen.IsValidScene(Req(StockScreenKind.MissionControl, StockScreenAct.Open),
                TestCommandScene.Flight));
        }

        [Fact]
        public void DecidePoll_NeedsReadinessAndTheFrameFloor()
        {
            int floor = TestCommandStockScreen.MinSettleFrames;
            Assert.Equal(StockScreenPollOutcome.NotYet, TestCommandStockScreen.DecidePoll(true, floor - 1, false));
            Assert.Equal(StockScreenPollOutcome.Ready, TestCommandStockScreen.DecidePoll(true, floor, false));
            Assert.Equal(StockScreenPollOutcome.NotYet, TestCommandStockScreen.DecidePoll(false, 100, false));
            Assert.Equal(StockScreenPollOutcome.TimedOut, TestCommandStockScreen.DecidePoll(false, 100, true));
            // A screen that is ready when the budget runs out still reports ready.
            Assert.Equal(StockScreenPollOutcome.Ready, TestCommandStockScreen.DecidePoll(true, floor, true));
        }

        [Fact]
        public void Lines_AreSingleFieldTokens()
        {
            var r = new StockScreenRequest
            {
                Screen = StockScreenKind.Astronaut, Act = StockScreenAct.Hover, Item = "Bill Kerman",
            };
            Assert.Equal("stockscreen start screen=astronaut act=hover item=Bill_Kerman part=- pane=- scene=SPACECENTER",
                TestCommandStockScreen.FormatStartLine(r, "SPACECENTER"));
            Assert.Equal("stockscreen ok screen=astronaut act=hover item=Bill_Kerman part=- pane=- scene=EDITOR frames=4 tooltip=T/direct/moved",
                TestCommandStockScreen.FormatOkLine(r, "EDITOR", "tooltip", "T/direct/moved", 4));
            var payload = TestCommandStockScreen.BuildPayload(r, "EDITOR", "tooltip", "a b", 4);
            Assert.Equal("a_b", payload.Single(kv => kv.Key == "tooltip").Value);
            Assert.Equal("Bill_Kerman", payload.Single(kv => kv.Key == "item").Value);
        }

        // ------------------------------------------------------------------ records

        [Fact]
        public void RecordLines_SummaryPerScreenAndTab_ThenMarkedOrBlockedItems()
        {
            var records = new List<StockScreenRecord>
            {
                new StockScreenRecord { Screen = "MissionControl", Tab = "Available", Id = "a", Kind = "ContractAccept",
                    Marked = true, Blocked = true, Why = "Accepted on Y1, D03.\nRule." },
                new StockScreenRecord { Screen = "MissionControl", Tab = "Available", Id = "b", Kind = "ContractSlot",
                    Marked = false, Blocked = true, Why = "Slot \"x\"" },
                new StockScreenRecord { Screen = "MissionControl", Tab = "Available", Id = "c", Kind = "None" },
                new StockScreenRecord { Screen = "MissionControl", Tab = "Active", Id = "d", Kind = "None" },
            };
            var lines = StockScreenRecords.FormatLines("stk-mc", records);
            Assert.Equal(4, lines.Count);
            Assert.Equal("record label=stk-mc screen=MissionControl tab=Available items=3 marked=1 blocked=2", lines[0]);
            Assert.Equal("record label=stk-mc screen=MissionControl tab=Active items=1 marked=0 blocked=0", lines[1]);
            Assert.Equal("record label=stk-mc screen=MissionControl tab=Available item=a kind=ContractAccept "
                         + "marked=true blocked=true why=\"Accepted on Y1, D03. | Rule.\"", lines[2]);
            Assert.EndsWith("why=\"Slot 'x'\"", lines[3]);
        }

        [Fact]
        public void RecordLines_NoScreenOpen_IsOneNoneLine()
        {
            Assert.Equal(new[] { "record label=x screens=none" },
                StockScreenRecords.FormatLines("x", new List<StockScreenRecord>()));
        }

        [Fact]
        public void ControlLine_NamesTheControlAndItsState()
        {
            var c = new StockScreenControl
            {
                Screen = "MissionControl", Name = "btnAccept:abc", State = null, Interactable = false, Visible = true,
            };
            Assert.Equal("control label=stk-mc screen=MissionControl name=btnAccept:abc state=- interactable=false visible=true",
                StockScreenRecords.FormatControlLine("stk-mc", c));
            c.State = "research";
            c.Interactable = true;
            Assert.EndsWith("state=research interactable=true visible=true", StockScreenRecords.FormatControlLine("x", c));
        }

        [Fact]
        public void Record_FromADecoration_CarriesItsFields()
        {
            var d = new StockUiDecoration
            {
                Screen = StockUiScreen.FacilityMenu, Tab = "Upgrade", Id = "SpaceCenter/TrackingStation",
                Kind = StockUiDecorationKind.FacilityUpgrade, Marked = true, Blocked = true, Why = "w",
            };
            var r = StockScreenRecord.From(d);
            Assert.Equal("FacilityMenu", r.Screen);
            Assert.Equal("FacilityUpgrade", r.Kind);
            Assert.Equal("SpaceCenter/TrackingStation", r.Id);
            Assert.True(r.Marked && r.Blocked);
            Assert.Equal("w", r.Why);
        }
    }
}
