using System;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The shared table-window chrome (<see cref="TableWindowChrome"/>): its pure placement
    /// and input-lock decisions, and the identity the log-table window hands it, which must
    /// stay what the window used inline before the lift (window id key, lock id, log names,
    /// minimum size, first-open rect).
    /// </summary>
    public class TableWindowChromeTests
    {
        [Fact]
        public void AnUnplacedRectNeedsTheFirstOpenPlacement()
        {
            Assert.True(TableWindowChrome.NeedsFirstOpenPlacement(default(Rect)));
            Assert.True(TableWindowChrome.NeedsFirstOpenPlacement(new Rect(10f, 20f, 0.5f, 300f)));
        }

        [Fact]
        public void APlacedOrCommandedRectKeepsItsPlace()
        {
            Assert.False(TableWindowChrome.NeedsFirstOpenPlacement(new Rect(10f, 20f, 1f, 0f)));
            Assert.False(TableWindowChrome.NeedsFirstOpenPlacement(new Rect(0f, 0f, 900f, 320f)));
        }

        [Theory]
        [InlineData(false, true, "Acquire")]
        [InlineData(true, true, "Keep")]
        [InlineData(true, false, "Release")]
        [InlineData(false, false, "Keep")]
        public void TheInputLockIsHeldExactlyWhileThePointerIsOverTheWindow(
            bool hasLock, bool pointerInside, string expected)
        {
            Assert.Equal(expected, TableWindowChrome.DecideInputLock(hasLock, pointerInside).ToString());
        }

        [Fact]
        public void TheReleaseWithoutALockIsANoOp()
        {
            var chrome = new TableWindowChrome(null, "k", "lock", "pos", "resize",
                10f, 10f, r => r, new TooltipEchoBox(), () => { });
            chrome.ReleaseInputLock();
        }

        [Fact]
        public void ACommandedRectIsTheChromesRect()
        {
            var ui = new StructureListWindowUI(null);
            var commanded = new Rect(50f, 60f, 1200f, 700f);
            ui.WindowRectForTesting = commanded;
            Assert.Equal(commanded, ChromeOf(ui).WindowRect);
            Assert.Equal(commanded, ui.WindowRectForTesting);
        }

        [Theory]
        [InlineData(false, "ParsekStructureList", "Parsek_StructureListWindow", "StructureList", "Structure window")]
        [InlineData(true, "ParsekRouteHistory", "Parsek_RouteHistoryWindow", "RouteHistory", "Route History window")]
        public void TheLogTableWindowKeepsItsIdentityOnTheChrome(
            bool routeHistory, string windowIdKey, string lockId, string positionLogName, string resizeLogName)
        {
            var ui = new StructureListWindowUI(null, routeHistory);
            TableWindowChrome chrome = ChromeOf(ui);
            Assert.Equal(windowIdKey, Field<string>(chrome, "windowIdKey"));
            Assert.Equal(ui.InstanceWindowIdKey, Field<string>(chrome, "windowIdKey"));
            Assert.Equal(lockId, Field<string>(chrome, "inputLockId"));
            Assert.Equal(positionLogName, Field<string>(chrome, "positionLogName"));
            Assert.Equal(resizeLogName, Field<string>(chrome, "resizeLogName"));
            Assert.Equal(StructureListWindowUI.MinWindowWidth, Field<float>(chrome, "minWidth"));
            Assert.Equal(StructureListWindowUI.MinWindowHeight, Field<float>(chrome, "minHeight"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheFirstOpenRectIsTheWindowsOwnPlacement(bool routeHistory)
        {
            var ui = new StructureListWindowUI(null, routeHistory);
            var place = Field<Func<Rect, Rect>>(ChromeOf(ui), "firstOpenRect");
            var main = new Rect(100f, 80f, 400f, 600f);
            Assert.Equal(StructureListWindowUI.DefaultWindowRect(main, routeHistory), place(main));
        }

        private static TableWindowChrome ChromeOf(StructureListWindowUI ui)
            => Field<TableWindowChrome>(ui, "chrome");

        private static T Field<T>(object owner, string name)
        {
            FieldInfo f = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(f != null, owner.GetType().Name + "." + name + " not found");
            return (T)f.GetValue(owner);
        }
    }
}
