using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class UnfinishedFlightStashHandlerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public UnfinishedFlightStashHandlerTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindPointReaper.ResetTestOverrides();
            UnfinishedFlightStashHandler.ResetForTesting();
            UnfinishedFlightStashHandler.UtcNowForTesting =
                () => new DateTime(2026, 4, 29, 8, 9, 10, DateTimeKind.Utc);
        }

        public void Dispose()
        {
            UnfinishedFlightStashHandler.ResetForTesting();
            RewindPointReaper.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording Rec(
            string id,
            TerminalState terminal,
            MergeState state = MergeState.Immutable)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = "tree_1",
                MergeState = state,
                ParentBranchPointId = "bp_1",
                TerminalStateValue = terminal,
            };
        }

        private static ChildSlot Slot(int index, string origin)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = origin,
                Controllable = true,
            };
        }

        private static ParsekScenario InstallScenario(RewindPoint rp)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { rp },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        [Fact]
        public void TryStash_SetsSlotStashTimestamp_DoesNotChangeMergeState_LogsAndBlocksReap()
        {
            var landed = Rec("rec_landed", TerminalState.Landed);
            var focus = Rec("rec_focus", TerminalState.Orbiting);
            RecordingStore.AddRecordingWithTreeForTesting(focus, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(landed, "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_focus"),
                    Slot(1, "rec_landed")
                }
            };
            var scenario = InstallScenario(rp);
            int versionBefore = scenario.SupersedeStateVersion;

            bool ok = UnfinishedFlightStashHandler.TryStash(landed, out string reason);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.True(rp.ChildSlots[1].Stashed);
            Assert.Equal("2026-04-29T08:09:10.0000000Z", rp.ChildSlots[1].StashedRealTime);
            Assert.Equal(MergeState.Immutable, landed.MergeState);
            Assert.NotEqual(versionBefore, scenario.SupersedeStateVersion);
            Assert.True(EffectiveState.IsUnfinishedFlight(landed));
            Assert.False(RewindPointReaper.IsReapEligible(rp, scenario.RecordingSupersedes));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("Stashed slot=1")
                && l.Contains("rec=rec_landed")
                && l.Contains("reaperBlocked=True"));
        }

        [Fact]
        public void TryStash_DestroyedAutoIncludedRow_ReturnsAlreadyUnfinishedFlightAndLogsWarning()
        {
            var crashed = Rec("rec_crashed", TerminalState.Destroyed);
            RecordingStore.AddRecordingWithTreeForTesting(crashed, "tree_1");
            InstallScenario(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot> { Slot(0, "rec_crashed") }
            });

            bool ok = UnfinishedFlightStashHandler.TryStash(crashed, out string reason);

            Assert.False(ok);
            Assert.Equal("alreadyUnfinishedFlight", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[UnfinishedFlights]")
                && l.Contains("Stash unavailable")
                && l.Contains("reason=alreadyUnfinishedFlight"));
        }

        [Fact]
        public void TryStash_AlreadyStashedSlot_ReturnsFalseWithoutVersionBump()
        {
            var landed = Rec("rec_landed", TerminalState.Landed);
            RecordingStore.AddRecordingWithTreeForTesting(landed, "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_landed",
                        Controllable = true,
                        Stashed = true,
                        StashedRealTime = "2026-04-29T08:00:00.0000000Z",
                    }
                }
            };
            var scenario = InstallScenario(rp);
            int versionBefore = scenario.SupersedeStateVersion;

            bool ok = UnfinishedFlightStashHandler.TryStash(landed, out string reason);

            Assert.False(ok);
            Assert.Equal("alreadyStashed", reason);
            Assert.Equal(versionBefore, scenario.SupersedeStateVersion);
            Assert.Equal("2026-04-29T08:00:00.0000000Z", rp.ChildSlots[0].StashedRealTime);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[UnfinishedFlights]")
                && l.Contains("Stash unavailable")
                && l.Contains("reason=alreadyStashed"));
        }

        [Fact]
        public void TryStash_MissingSlot_ReturnsFalseAndLogsError()
        {
            var landed = Rec("rec_landed", TerminalState.Landed);
            RecordingStore.AddRecordingWithTreeForTesting(landed, "tree_1");
            InstallScenario(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                ChildSlots = new List<ChildSlot> { Slot(0, "rec_other") }
            });

            bool ok = UnfinishedFlightStashHandler.TryStash(landed, out string reason);

            Assert.False(ok);
            Assert.Equal("noMatchingRpSlot", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[ERROR]")
                && l.Contains("[UnfinishedFlights]")
                && l.Contains("Stash could not resolve slot")
                && l.Contains("rec=rec_landed"));
        }
    }
}
