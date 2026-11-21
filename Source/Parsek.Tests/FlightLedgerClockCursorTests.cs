using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The flight scene's ledger clock cursor (todo FLIGHT-CLOCK-CROSSING-COMMITTED-ROWS): a
    /// committed row the flight clock reaches takes effect at its UT, as at the Space Center
    /// and in the Tracking Station, instead of waiting for the next recalculation (a warp
    /// exit, a commit, a spend). Two parts are pure and pinned here: the crossing window the
    /// cursor hands its after-recalculate hook, and the building fold over that window that
    /// applies while a flight tree lives and the ordinary patch is deferred. The third part
    /// is a source gate on the ParsekFlight wiring.
    /// </summary>
    [Collection("Sequential")]
    public class FlightLedgerClockCursorTests
    {
        private const string Pad = "SpaceCenter/LaunchPad/Facility/Flag";
        private const string Vab = "SpaceCenter/VehicleAssemblyBuilding/Facility/mainBuilding";

        private readonly List<KeyValuePair<double, string>> recalcs =
            new List<KeyValuePair<double, string>>();
        private readonly List<Tuple<double, double, string>> windows =
            new List<Tuple<double, double, string>>();

        private CareerLedgerClockCursor Make(Func<double, double> next)
        {
            return new CareerLedgerClockCursor(
                "flight",
                nextActionUTAfter: next,
                recalculate: (ut, reason) => recalcs.Add(new KeyValuePair<double, string>(ut, reason)),
                afterRecalculate: (after, ut, reason) => windows.Add(Tuple.Create(after, ut, reason)));
        }

        private static GameAction Destroy(string id, double ut, string rec = null)
            => new GameAction { Type = GameActionType.FacilityDestruction, FacilityId = id, UT = ut, RecordingId = rec };

        private static GameAction Repair(string id, double ut)
            => new GameAction { Type = GameActionType.FacilityRepair, FacilityId = id, UT = ut, FacilityCost = 100f };

        // ---- the cursor's crossing window ----

        [Fact]
        public void Seed_RunsNoRecalculation_AndNoWindow()
        {
            var cursor = Make(_ => 120.0);
            Assert.False(cursor.Advance(45.0));
            Assert.Empty(recalcs);
            Assert.Empty(windows);
        }

        [Fact]
        public void FlightClockPastACommittedRow_RecalculatesOnce_ThenHandsTheWindowToTheHook()
        {
            var cursor = Make(after => after < 120.0 ? 120.0 : double.PositiveInfinity);
            cursor.Advance(45.0);
            Assert.False(cursor.Advance(119.9));
            Assert.True(cursor.Advance(121.0));

            var r = Assert.Single(recalcs);
            Assert.Equal(121.0, r.Key);
            Assert.Equal("flight-clock", r.Value);
            var w = Assert.Single(windows);
            Assert.Equal(45.0, w.Item1);
            Assert.Equal(121.0, w.Item2);
            Assert.Equal("flight-clock", w.Item3);

            // Past every row: no second recalculation at 1x or in warp.
            Assert.False(cursor.Advance(160.0));
            Assert.Single(recalcs);
        }

        [Fact]
        public void ConsecutiveCrossings_HandContiguousWindows()
        {
            var rows = new[] { 60.0, 120.0 };
            var cursor = Make(after =>
            {
                foreach (double r in rows) if (r > after) return r;
                return double.PositiveInfinity;
            });
            cursor.Advance(45.0);
            cursor.Advance(61.0);
            cursor.Advance(130.0);

            Assert.Equal(2, windows.Count);
            Assert.Equal(45.0, windows[0].Item1);
            Assert.Equal(61.0, windows[0].Item2);
            Assert.Equal(61.0, windows[1].Item1);
            Assert.Equal(130.0, windows[1].Item2);
        }

        [Fact]
        public void HookIsOptional_KscAndTrackingStationCursorsAreUnchanged()
        {
            var cursor = new CareerLedgerClockCursor("ksc",
                nextActionUTAfter: _ => 500.0,
                recalculate: (ut, reason) => recalcs.Add(new KeyValuePair<double, string>(ut, reason)));
            cursor.Advance(100.0);
            Assert.True(cursor.Advance(800.0));
            Assert.Single(recalcs);
        }

        // ---- the building fold over the crossing window ----

        [Fact]
        public void CrossedFold_TakesOnlyRowsInsideTheWindow()
        {
            var ledger = new List<GameAction>
            {
                Destroy(Vab, 40.0),        // at or before the window start: the load owned it
                Destroy(Pad, 120.0, "rec-a"),
                Repair(Vab, 200.0),        // still ahead of the clock
                new GameAction { Type = GameActionType.FacilityUpgrade, FacilityId = "SpaceCenter/LaunchPad", UT = 100.0, ToLevel = 2 },
                null,
            };
            var crossed = FacilityStatePatcher.ComputeBuildingStatesCrossed(ledger, 45.0, 121.0, null);
            Assert.Single(crossed);
            Assert.True(crossed[Pad]);

            Assert.Empty(FacilityStatePatcher.ComputeBuildingStatesCrossed(ledger, 121.0, 160.0, null));
            Assert.Empty(FacilityStatePatcher.ComputeBuildingStatesCrossed(null, 45.0, 160.0, null));
            // The upper bound is inclusive, the lower exclusive.
            Assert.True(FacilityStatePatcher.ComputeBuildingStatesCrossed(ledger, 119.0, 120.0, null)[Pad]);
            Assert.Empty(FacilityStatePatcher.ComputeBuildingStatesCrossed(ledger, 120.0, 130.0, null));
        }

        [Fact]
        public void CrossedFold_LastRowInTheWindowWins()
        {
            var ledger = new List<GameAction> { Destroy(Pad, 100.0), Repair(Pad, 110.0) };
            Assert.False(FacilityStatePatcher.ComputeBuildingStatesCrossed(ledger, 50.0, 120.0, null)[Pad]);
        }

        // The mirror direction of the live-recorder gate (KscBuildingLivePatchTests (b)): a
        // committed repair crossed at 120, then the live recording knocks the building down
        // at 130 (tagged, not in the ledger). The next crossing's window does not reach back
        // to the repair, so the live collapse is never undone. The full fold at now would.
        [Fact]
        public void LiveCollapseAfterACrossedRepair_IsNotRepairedByTheNextCrossing()
        {
            var ledger = new List<GameAction> { Destroy(Pad, 90.0), Repair(Pad, 120.0), Destroy(Vab, 140.0) };

            var next = FacilityStatePatcher.ComputeBuildingStatesCrossed(ledger, 121.0, 141.0, null);
            Assert.False(next.ContainsKey(Pad));
            Assert.True(next[Vab]);

            // The full fold the gate refuses while a recorder lives would repair it.
            Assert.False(FacilityStatePatcher.ComputeBuildingDestroyedAtUt(ledger, 141.0)[Pad]);
        }

        // During a Re-Fly the flight being re-flown is still in the ledger until the merge:
        // its own crash row must not knock the pad down under the attempt that avoids it.
        [Fact]
        public void CrossedFold_ExcludesRowsOfTheReFlySuppressedSubtree()
        {
            var ledger = new List<GameAction> { Destroy(Pad, 120.0, "rec-reflown"), Destroy(Vab, 125.0, "rec-other") };
            var crossed = FacilityStatePatcher.ComputeBuildingStatesCrossed(
                ledger, 45.0, 130.0, new[] { "rec-reflown" });
            Assert.False(crossed.ContainsKey(Pad));
            Assert.True(crossed[Vab]);
        }

        // ---- when the crossing building patch acts ----

        [Fact]
        public void CrossedPatchMode_GateOpen_TheRecalculationsLivePatchOwnsIt()
        {
            string reason;
            Assert.Equal(FacilityStatePatcher.CrossedBuildingPatchMode.FullPatchOwns,
                FacilityStatePatcher.ResolveCrossedBuildingPatchMode(
                    false, false, false, false, false, 45.0, 121.0, out reason));
        }

        [Theory]
        [InlineData(true, false, false, "live recorder active")]
        [InlineData(false, true, false, "active uncommitted flight tree")]
        [InlineData(false, false, true, "pending tree")]
        public void CrossedPatchMode_FlightTreeLives_AppliesTheCrossedRows(
            bool live, bool uncommitted, bool pending, string expected)
        {
            string reason;
            Assert.Equal(FacilityStatePatcher.CrossedBuildingPatchMode.Apply,
                FacilityStatePatcher.ResolveCrossedBuildingPatchMode(
                    live, uncommitted, pending, false, false, 45.0, 121.0, out reason));
            Assert.Equal(expected, reason);
        }

        [Fact]
        public void CrossedPatchMode_SkipsOnLoadHeldLedgerUnreadyClockAndEmptyWindow()
        {
            string reason;
            Assert.Equal(FacilityStatePatcher.CrossedBuildingPatchMode.Skip,
                FacilityStatePatcher.ResolveCrossedBuildingPatchMode(true, false, false, true, false, 45.0, 121.0, out reason));
            Assert.Equal("scene load in progress", reason);
            Assert.Equal(FacilityStatePatcher.CrossedBuildingPatchMode.Skip,
                FacilityStatePatcher.ResolveCrossedBuildingPatchMode(true, false, false, false, true, 45.0, 121.0, out reason));
            Assert.Equal("ledger held from a newer Parsek version", reason);
            Assert.Equal(FacilityStatePatcher.CrossedBuildingPatchMode.Skip,
                FacilityStatePatcher.ResolveCrossedBuildingPatchMode(true, false, false, false, false, 45.0, 0.0, out reason));
            Assert.Equal("universe clock not ready", reason);
            // A backward move hands an empty window.
            Assert.Equal(FacilityStatePatcher.CrossedBuildingPatchMode.Skip,
                FacilityStatePatcher.ResolveCrossedBuildingPatchMode(true, false, false, false, false, 300.0, 200.0, out reason));
            Assert.Equal("empty window", reason);
            Assert.Equal(FacilityStatePatcher.CrossedBuildingPatchMode.Skip,
                FacilityStatePatcher.ResolveCrossedBuildingPatchMode(true, false, false, false, false, double.NaN, 200.0, out reason));
            Assert.Equal("empty window", reason);
        }

        // ---- the live step, driven headless (the building lookup fails without Unity, which
        // the patch logs and survives) ----

        private List<string> CaptureLiveStep(bool liveRecorder, params GameAction[] rows)
        {
            var lines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            try
            {
                GameStateRecorder.HasLiveRecorderProviderForTesting = () => liveRecorder;
                GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => liveRecorder;
                foreach (var r in rows) Ledger.AddAction(r);
                FacilityStatePatcher.PatchCrossedBuildingRowsWhileTreeLives(45.0, 121.0, "flight-clock");
                return lines;
            }
            finally
            {
                GameStateRecorder.ResetForTesting();
                Ledger.ResetForTesting();
                KspStatePatcher.ResetForTesting();
                RecordingStore.ResetForTesting();
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void LiveStep_NoFlightTree_LeavesItToTheRecalculationsPatch()
        {
            var lines = CaptureLiveStep(false, Destroy(Pad, 120.0, "rec-a"));
            Assert.Contains(lines, l => l.Contains("[KspStatePatcher]")
                && l.Contains("PatchCrossedBuildingRows: FullPatchOwns")
                && l.Contains("window=(45, 121]"));
            Assert.DoesNotContain(lines, l => l.Contains("flight clock crossed"));
        }

        [Fact]
        public void LiveStep_WhileRecording_AppliesTheCrossedRow_UnderOneInfoLine()
        {
            var lines = CaptureLiveStep(true, Destroy(Pad, 120.0, "rec-a"), Destroy(Vab, 200.0));
            Assert.Contains(lines, l => l.Contains("[INFO][KspStatePatcher]")
                && l.Contains("PatchCrossedBuildingRows: flight clock crossed 1 committed building row(s) in (45, 121]")
                && l.Contains("(live recorder active)")
                && l.Contains("reason=flight-clock"));
            Assert.Contains(lines, l => l.Contains("PatchDestructionState: FindObjectsOfType unavailable"));
        }

        [Fact]
        public void LiveStep_WhileRecording_NoRowInTheWindow_LogsAndPatchesNothing()
        {
            var lines = CaptureLiveStep(true, Destroy(Pad, 130.0, "rec-a"));
            Assert.Contains(lines, l => l.Contains("PatchCrossedBuildingRows: no building row in window (45, 121]"));
            Assert.DoesNotContain(lines, l => l.Contains("PatchDestructionState"));
        }

        // ---- the ParsekFlight wiring ----

        private static string ReadSource(string rel)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", rel);
            Assert.True(File.Exists(path), "source not found at " + path);
            string src = File.ReadAllText(path).Replace("\r\n", "\n");
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(src, @"//[^\n]*", " ");
        }

        private static string MethodBody(string src, string declaration)
        {
            int start = src.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(start >= 0, declaration + " not found");
            int open = src.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced body for " + declaration);
        }

        [Fact]
        public void ParsekFlight_BuildsTheFlightCursor_WithTheGuardedRecalcAndTheBuildingHook()
        {
            string src = Regex.Replace(ReadSource("Controllers/ParsekFlight.cs"), @"\s+", " ");
            Assert.Contains(
                "private readonly CareerLedgerClockCursor flightLedgerClock = new CareerLedgerClockCursor( \"flight\", " +
                "recalculate: RecalculateLedgerAtFlightClockCrossing, " +
                "afterRecalculate: ApplyCrossedBuildingRowsAtFlightClockCrossing);", src);
        }

        [Fact]
        public void ParsekFlightUpdate_AdvancesTheFlightCursor_OnlyOnATrustedClock_AfterTheTeardownReturn()
        {
            string body = Regex.Replace(MethodBody(ReadSource("Controllers/ParsekFlight.cs"), "void Update()"), @"\s+", " ");
            int teardown = body.IndexOf("if (sceneChangeInProgress) return;", StringComparison.Ordinal);
            int guarded = body.IndexOf(
                "if (!RecordingStore.RewindUTAdjustmentPending) { double flightClockUT = Planetarium.GetUniversalTime(); " +
                "committedMilestoneClock.Advance(flightClockUT); if (flightClockUT > 0.0) flightLedgerClock.Advance(flightClockUT); }",
                StringComparison.Ordinal);
            Assert.True(teardown >= 0, "teardown early-out not found");
            Assert.True(guarded > teardown, "flight cursor advance not found after the teardown early-out");
            Assert.Single(Regex.Matches(body, @"flightLedgerClock\.Advance\("));
        }
    }
}
