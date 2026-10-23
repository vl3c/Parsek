using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// EVA-CONSTRUCTION-ON-FOREIGN-VESSEL-UNCLAIMED, the foreign EVA kerbal half: a kerbal a
    /// recording kerbal swaps cargo with joins that tree as its cargo partner (an
    /// InventoryTransfer member whose vessel is the kerbal himself). Mission 1 left Jeb
    /// standing on the surface (an open-ended Aboard hold on his kerbal vessel); mission 2's
    /// Bill takes a part out of Jeb's inventory, so mission 2's member carries Jeb on from the
    /// transfer. The transfer claims Jeb's vessel the way a rescue dock claims a station: mission
    /// 1's hold ends at the transfer and the member's own rows decide the rest, so a later
    /// recovery of Jeb frees him instead of leaving mission 1 holding him forever. The same
    /// hand-off covers crew aboard any adopted cargo partner (a crewed rover).
    /// </summary>
    [Collection("Sequential")]
    public class ForeignKerbalCargoHandoffTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Jeb = "Jebediah Kerman";
        private const string Bill = "Bill Kerman";

        private const uint JebPid = 7001u;
        private const string JebGuid = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
        private const uint BillPid = 8001u;
        private const string BillGuid = "11111111222233334444555555555555";

        private const string WalkTree = "tree-jeb-walk";
        private const string WalkRec = "rec-jeb-walk";
        private const string CargoTree = "tree-bill-cargo";
        private const string BillRec = "rec-bill";
        private const string MemberRec = "rec-jeb-member";
        private const string TransferBp = "bp-transfer";

        private const double TransferUT = 500.0;
        private const double MemberEndUT = 700.0;

        public ForeignKerbalCargoHandoffTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.LiveClockUTProviderForTesting = null;
            KerbalsModule.LoadedSaveUTProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>A kerbal on EVA as a saved vessel: one kerbalEVA part seating him.</summary>
        private static ConfigNode KerbalSnapshot(string kerbal)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", kerbal ?? "Empty kerbal");
            snapshot.AddValue("type", "EVA");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", "kerbalEVA");
            if (kerbal != null)
                part.AddValue("crew", kerbal);
            return snapshot;
        }

        private static Recording Rec(
            string id, string treeId, uint pid, string guid, double startUT, double endUT,
            TerminalState? terminal, string startCrew, string endCrew)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = id,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                GhostVisualSnapshot = KerbalSnapshot(startCrew),
                VesselSnapshot = KerbalSnapshot(endCrew),
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = terminal,
            };
        }

        private static void CommitTree(RecordingTree tree, params Recording[] recs)
        {
            foreach (var rec in recs)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
            RecordingStore.AddCommittedTreeForTesting(tree);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
        }

        /// <summary>Mission 1: Jeb walks off on EVA and is left standing on the surface.</summary>
        private static void CommitJebWalk()
        {
            var tree = new RecordingTree
            {
                Id = WalkTree,
                TreeName = "Jeb walk",
                RootRecordingId = WalkRec,
                ActiveRecordingId = WalkRec,
            };
            CommitTree(tree, Rec(WalkRec, WalkTree, JebPid, JebGuid, 100.0, 300.0,
                TerminalState.Landed, Jeb, Jeb));
        }

        /// <summary>
        /// Mission 2: Bill on EVA takes a part out of Jeb's inventory at the transfer UT, so
        /// Jeb's kerbal vessel joins Bill's tree as its InventoryTransfer member (the shape
        /// <see cref="ForeignInventoryTransfer.BuildMemberBranchData"/> builds). The member
        /// ends <paramref name="memberTerminal"/> at <see cref="MemberEndUT"/>.
        /// </summary>
        private static void CommitBillCargo(
            TerminalState memberTerminal = TerminalState.Landed,
            uint memberPid = JebPid, string memberGuid = JebGuid, string memberCrew = Jeb)
        {
            var tree = new RecordingTree
            {
                Id = CargoTree,
                TreeName = "Bill cargo",
                RootRecordingId = BillRec,
                ActiveRecordingId = BillRec,
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = TransferBp,
                UT = TransferUT,
                Type = BranchPointType.InventoryTransfer,
                ParentRecordingIds = new List<string> { BillRec },
                ChildRecordingIds = new List<string> { MemberRec },
            });
            var bill = Rec(BillRec, CargoTree, BillPid, BillGuid, 450.0, 650.0,
                TerminalState.Landed, Bill, Bill);
            string endCrew = memberTerminal == TerminalState.Recovered ? null : memberCrew;
            var member = Rec(MemberRec, CargoTree, memberPid, memberGuid, TransferUT, MemberEndUT,
                memberTerminal, memberCrew, endCrew);
            member.ParentBranchPointId = TransferBp;
            CommitTree(tree, bill, member);
        }

        private static KerbalsModule Walk(double clockUT)
        {
            KerbalsModule.LiveClockUTProviderForTesting = () => clockUT;
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(clockUT, "foreign-kerbal-cargo-test");
            return LedgerOrchestrator.Kerbals;
        }

        // catches: the adopted kerbal's own vessel snapshot naming nobody, so the cargo
        // flight would hold a kerbal it carries with no row at all.
        [Fact]
        public void KerbalMember_WritesAnAssignmentRowForTheKerbalItCarries()
        {
            CommitBillCargo();

            var member = System.Linq.Enumerable.First(RecordingStore.CommittedRecordings, r => r.RecordingId == MemberRec);
            List<GameAction> rows = LedgerOrchestrator.BuildKerbalAssignmentActions(
                member, MemberRec, TransferUT, MemberEndUT);

            Assert.Single(rows);
            Assert.Equal(Jeb, rows[0].KerbalName);
        }

        // catches: two missions holding one kerbal - the walk's own mission never lets him go
        // after the cargo flight carries him home.
        [Fact]
        public void TransferClaim_EndsTheEarlierMissionsOpenHold_AndRecoveryFreesJeb()
        {
            CommitJebWalk();
            CommitBillCargo(memberTerminal: TerminalState.Recovered);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.Equal(MemberEndUT, kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Reservation bounded by cross-tree dock handoff: 'Jebediah Kerman'")
                && l.Contains("recording '" + WalkRec + "'")
                && l.Contains("bp='" + TransferBp + "'")
                && l.Contains("child='" + MemberRec + "'"));
        }

        // catches: the hand-off releasing Jeb while the cargo flight's member still holds him.
        [Fact]
        public void TransferClaim_JebStaysHeldWhileTheMemberCarriesHim()
        {
            CommitJebWalk();
            CommitBillCargo();

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("Reservation bounded by cross-tree dock handoff: 'Jebediah Kerman'")
                && l.Contains("recording '" + WalkRec + "'"));
        }

        // catches: a cargo partner that is another launch (other guid) or carries someone else
        // ending the walk mission's hold on Jeb.
        [Theory]
        [InlineData(JebPid, "ffffffffffffffffffffffffffffffff", Jeb)]
        [InlineData(9999u, null, Jeb)]
        [InlineData(JebPid, JebGuid, "Valentina Kerman")]
        public void TransferClaimOnAnotherVesselOrKerbal_KeepsTheOpenHold(
            uint memberPid, string memberGuid, string memberCrew)
        {
            CommitJebWalk();
            CommitBillCargo(memberTerminal: TerminalState.Recovered,
                memberPid: memberPid, memberGuid: memberGuid, memberCrew: memberCrew);

            KerbalsModule kerbals = Walk(10000.0);

            Assert.True(double.IsPositiveInfinity(kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.DoesNotContain(logLines, l => l.Contains("Reservation bounded by cross-tree dock handoff: 'Jebediah Kerman'"));
        }
    }
}
