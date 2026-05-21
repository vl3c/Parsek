using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class UnfinishedFlightSealHandlerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public UnfinishedFlightSealHandlerTests()
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
            UnfinishedFlightSealHandler.ResetForTesting();
            UnfinishedFlightSealHandler.UtcNowForTesting =
                () => new DateTime(2026, 4, 28, 12, 34, 56, DateTimeKind.Utc);
            RewindPointReaper.DeleteQuicksaveForTesting = _ => true;
        }

        public void Dispose()
        {
            UnfinishedFlightSealHandler.ResetForTesting();
            RewindPointReaper.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording Rec(string id, MergeState state = MergeState.CommittedProvisional)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = "tree_1",
                MergeState = state,
                ParentBranchPointId = "bp_1",
                TerminalStateValue = TerminalState.Orbiting,
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
            return scenario;
        }

        [Fact]
        public void TrySeal_FlipsSlotTipToImmutable_BumpsStateVersion_Logs()
        {
            var rec = Rec("rec_probe");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording
                {
                    RecordingId = "rec_focus",
                    TreeId = "tree_1",
                    MergeState = MergeState.CommittedProvisional,
                },
                "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_focus"),
                    Slot(1, "rec_probe")
                }
            };
            var scenario = InstallScenario(rp);
            int versionBefore = scenario.SupersedeStateVersion;

            bool ok = UnfinishedFlightSealHandler.TrySeal(rec, out string reason);

            Assert.True(ok);
            Assert.Null(reason);
            // Seal flips the slot's effective tip (rec_probe) to Immutable.
            Assert.Equal(MergeState.Immutable, rec.MergeState);
            Assert.NotEqual(versionBefore, scenario.SupersedeStateVersion);
            // The other slot's tip (rec_focus) is still CommittedProvisional
            // (open), so the RP is still blocked.
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("Sealed slot=1")
                && l.Contains("rec=rec_probe")
                && l.Contains("mergeState=CommittedProvisional->Immutable")
                && l.Contains("reaperImpact=stillBlocked"));
        }

        [Fact]
        public void TrySeal_AlreadyImmutableTip_IsIdempotent()
        {
            var rec = Rec("rec_probe", MergeState.Immutable);
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording
                {
                    RecordingId = "rec_focus",
                    TreeId = "tree_1",
                    MergeState = MergeState.CommittedProvisional,
                },
                "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_focus"),
                    Slot(1, "rec_probe")
                }
            };
            InstallScenario(rp);

            bool ok = UnfinishedFlightSealHandler.TrySeal(rec, out string reason);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.Equal(MergeState.Immutable, rec.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("Sealed slot=1")
                && l.Contains("mergeState=Immutable->Immutable"));
        }

        [Fact]
        public void TrySeal_WhenLastOpenSlot_ReapsRewindPoint()
        {
            var rec = Rec("rec_probe");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording
                {
                    RecordingId = "rec_focus",
                    TreeId = "tree_1",
                    MergeState = MergeState.Immutable,
                },
                "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_focus"),
                    Slot(1, "rec_probe")
                }
            };
            var scenario = InstallScenario(rp);

            bool ok = UnfinishedFlightSealHandler.TrySeal(rec, out string reason);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.Equal(MergeState.Immutable, rec.MergeState);
            Assert.Empty(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("reaperImpact=willReap")
                && l.Contains("reaped=1"));
        }

        [Fact]
        public void TrySeal_PersistsSealBeforeDeletingQuicksave()
        {
            var rec = Rec("rec_probe");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording
                {
                    RecordingId = "rec_focus",
                    TreeId = "tree_1",
                    MergeState = MergeState.Immutable,
                },
                "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_focus"),
                    Slot(1, "rec_probe")
                }
            };
            var scenario = InstallScenario(rp);
            bool saveHookObservedSeal = false;
            bool deleteHookObservedAfterSave = false;

            UnfinishedFlightSealHandler.SavePersistentForTesting = () =>
            {
                Assert.Equal(MergeState.Immutable, rec.MergeState);
                Assert.Single(scenario.RewindPoints);
                saveHookObservedSeal = true;
                return true;
            };
            RewindPointReaper.DeleteQuicksaveForTesting = rpId =>
            {
                Assert.Equal("rp_1", rpId);
                Assert.Equal(MergeState.Immutable, rec.MergeState);
                Assert.True(saveHookObservedSeal);
                deleteHookObservedAfterSave = true;
                return true;
            };

            bool ok = UnfinishedFlightSealHandler.TrySeal(rec, out string reason);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.True(saveHookObservedSeal);
            Assert.True(deleteHookObservedAfterSave);
            Assert.Empty(scenario.RewindPoints);
        }

        [Fact]
        public void TrySeal_WhenPersistentSaveFails_DoesNotDeleteQuicksaveOrReap()
        {
            var rec = Rec("rec_probe");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording
                {
                    RecordingId = "rec_focus",
                    TreeId = "tree_1",
                    MergeState = MergeState.Immutable,
                },
                "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_focus"),
                    Slot(1, "rec_probe")
                }
            };
            var scenario = InstallScenario(rp);
            bool deleteHookCalled = false;

            UnfinishedFlightSealHandler.SavePersistentForTesting = () => false;
            RewindPointReaper.DeleteQuicksaveForTesting = _ =>
            {
                deleteHookCalled = true;
                return true;
            };

            bool ok = UnfinishedFlightSealHandler.TrySeal(rec, out string reason);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.Equal(MergeState.Immutable, rec.MergeState);
            Assert.False(deleteHookCalled);
            Assert.Single(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[UnfinishedFlights]")
                && l.Contains("Seal deferred RP reap until persistent save succeeds")
                && l.Contains("rp=rp_1"));
            Assert.Contains(logLines, l =>
                l.Contains("reaperImpact=deferredPersistence")
                && l.Contains("reaped=0"));
        }

        [Fact]
        public void TrySeal_MissingSlot_ReturnsFalseAndLogsError()
        {
            var rec = Rec("rec_probe");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            InstallScenario(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                ChildSlots = new List<ChildSlot> { Slot(0, "rec_other") }
            });

            bool ok = UnfinishedFlightSealHandler.TrySeal(rec, out string reason);

            Assert.False(ok);
            Assert.Equal("noMatchingRpSlot", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[ERROR]")
                && l.Contains("[UnfinishedFlights]")
                && l.Contains("Seal could not resolve slot")
                && l.Contains("rec=rec_probe"));
        }
    }
}
