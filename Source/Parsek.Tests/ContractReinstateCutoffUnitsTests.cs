using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests on the two units of
    /// <see cref="LedgerOrchestrator.BuildContractReinstateCutoffsForPatch"/>:
    /// <see cref="LedgerOrchestrator.MapContractGuidsByActionId"/> and
    /// <see cref="LedgerOrchestrator.ResolveContractReinstateStartUt"/>. The whole-method
    /// behaviour is pinned by <see cref="BuildContractReinstateCutoffsCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class ContractReinstateCutoffUnitsTests : IDisposable
    {
        private static readonly Guid GA = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid GB = new Guid("bbbbbbbb-0000-0000-0000-000000000002");

        public ContractReinstateCutoffUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        private static GameAction Row(string id, GameActionType type, string contractId) =>
            new GameAction { ActionId = id, Type = type, ContractId = contractId };

        [Fact]
        public void Map_KeepsOnlyContractShapedRowsWithIdAndParseableGuid()
        {
            var map = LedgerOrchestrator.MapContractGuidsByActionId(new List<GameAction>
            {
                null,
                Row("acc", GameActionType.ContractAccept, GA.ToString()),
                Row("cmp", GameActionType.ContractComplete, GA.ToString()),
                Row("fail", GameActionType.ContractFail, GB.ToString()),
                Row("cancel", GameActionType.ContractCancel, GB.ToString()),
                Row("", GameActionType.ContractAccept, GA.ToString()),
                Row("nocontract", GameActionType.ContractAccept, ""),
                Row("badguid", GameActionType.ContractAccept, "not-a-guid"),
                Row("offer", GameActionType.MilestoneAchievement, GA.ToString()),
            });

            Assert.Equal(4, map.Count);
            Assert.Equal(GA, map["acc"]);
            Assert.Equal(GA, map["cmp"]);
            Assert.Equal(GB, map["fail"]);
            Assert.Equal(GB, map["cancel"]);
        }

        [Fact]
        public void Map_RepeatedActionId_KeepsTheLastRow()
        {
            var map = LedgerOrchestrator.MapContractGuidsByActionId(new List<GameAction>
            {
                Row("dup", GameActionType.ContractAccept, GA.ToString()),
                Row("dup", GameActionType.ContractComplete, GB.ToString()),
            });

            Assert.Equal(GB, Assert.Single(map).Value);
        }

        [Fact]
        public void Map_EmptySource_IsEmptyAndOrdinal()
        {
            var map = LedgerOrchestrator.MapContractGuidsByActionId(new List<GameAction>());
            Assert.Empty(map);
            map["a"] = GA;
            Assert.False(map.ContainsKey("A"));
        }

        [Fact]
        public void Resolve_FoundRecording_UsesItsStartUtAndCachesIt()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec-a", VesselName = "A", ExplicitStartUT = 300.0,
            });
            var cache = new Dictionary<string, double>(StringComparer.Ordinal);

            double ut = LedgerOrchestrator.ResolveContractReinstateStartUt(
                new LedgerTombstone { ActionId = "x", RetiringRecordingId = "rec-a", UT = 900.0 }, cache);

            Assert.Equal(300.0, ut);
            Assert.Equal(300.0, cache["rec-a"]);
        }

        [Fact]
        public void Resolve_MissingRecording_CachesNaNAndFallsBackToTheTombstoneUt()
        {
            var cache = new Dictionary<string, double>(StringComparer.Ordinal);

            double ut = LedgerOrchestrator.ResolveContractReinstateStartUt(
                new LedgerTombstone { ActionId = "x", RetiringRecordingId = "rec-gone", UT = 1234.0 }, cache);

            Assert.Equal(1234.0, ut);
            Assert.True(double.IsNaN(cache["rec-gone"]));
        }

        [Fact]
        public void Resolve_CachedValue_IsUsedWithoutScanningTheStore()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec-a", VesselName = "A", ExplicitStartUT = 300.0,
            });
            var cache = new Dictionary<string, double>(StringComparer.Ordinal) { { "rec-a", 42.0 } };

            double ut = LedgerOrchestrator.ResolveContractReinstateStartUt(
                new LedgerTombstone { ActionId = "x", RetiringRecordingId = "rec-a", UT = 900.0 }, cache);

            Assert.Equal(42.0, ut);
        }

        [Fact]
        public void Resolve_CachedNaN_FallsBackToEachTombstonesOwnUt()
        {
            var cache = new Dictionary<string, double>(StringComparer.Ordinal) { { "rec-gone", double.NaN } };

            Assert.Equal(10.0, LedgerOrchestrator.ResolveContractReinstateStartUt(
                new LedgerTombstone { RetiringRecordingId = "rec-gone", UT = 10.0 }, cache));
            Assert.Equal(20.0, LedgerOrchestrator.ResolveContractReinstateStartUt(
                new LedgerTombstone { RetiringRecordingId = "rec-gone", UT = 20.0 }, cache));
        }

        [Fact]
        public void Resolve_MissingRecordingAndNaNTombstoneUt_ReturnsNaN()
        {
            var cache = new Dictionary<string, double>(StringComparer.Ordinal);

            Assert.True(double.IsNaN(LedgerOrchestrator.ResolveContractReinstateStartUt(
                new LedgerTombstone { RetiringRecordingId = "rec-gone", UT = double.NaN }, cache)));
        }
    }
}
