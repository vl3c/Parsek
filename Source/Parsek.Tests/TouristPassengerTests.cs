using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC-12: a tourist is a passenger of the flight that carried him. His trait survives
    /// stock deleting him (baseline capture, tourist rows), a re-derived row keeps the
    /// tourist role, and a spawn re-creates a missing tourist as a tourist, never as crew.
    /// </summary>
    [Collection("Sequential")] // GameStateStore baselines + LedgerOrchestrator.Kerbals
    public class TouristPassengerTests : IDisposable
    {
        private readonly KerbalsModule priorKerbalsModule;

        public TouristPassengerTests()
        {
            priorKerbalsModule = LedgerOrchestrator.Kerbals;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.SetKerbalsForTesting(priorKerbalsModule);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameStateBaseline BaselineWith(string name, string trait)
        {
            var baseline = new GameStateBaseline();
            baseline.crewEntries.Add(new GameStateBaseline.CrewEntry { name = name, trait = trait });
            return baseline;
        }

        // ── Trait resolution ──

        // catches: the roster lookup reading only roster.Crew, which stock filters to
        // type == Crew, so an accepted tourist read "Pilot".
        [Fact]
        public void ResolveTrait_RosterTourist_IsTourist()
        {
            Assert.Equal("Tourist", KerbalsModule.ResolveTraitForKerbal(
                "Tourist Kerman", KerbalsModule.RosterKerbalKind.Tourist, null, null, null));
        }

        [Fact]
        public void ResolveTrait_RosterCrew_IsHisTrait_EvenWhenABaselineDisagrees()
        {
            var baselines = new List<GameStateBaseline> { BaselineWith("Bill Kerman", "Pilot") };
            Assert.Equal("Engineer", KerbalsModule.ResolveTraitForKerbal(
                "Bill Kerman", KerbalsModule.RosterKerbalKind.Crew, "Engineer", null, baselines));
            Assert.Equal("Pilot", KerbalsModule.ResolveTraitForKerbal(
                "Bill Kerman", KerbalsModule.RosterKerbalKind.Crew, null, null, null));
        }

        // catches: a tourist stock already deleted (and whose baselines a rewind pruned)
        // falling back to "Pilot" although an effective row says Tourist.
        [Fact]
        public void ResolveTrait_AbsentKerbal_TouristRowWinsOverBaselineAndDefault()
        {
            Func<string, bool> rows = n => n == "Tourist Kerman";
            var baselines = new List<GameStateBaseline> { BaselineWith("Tourist Kerman", "Pilot") };
            Assert.Equal("Tourist", KerbalsModule.ResolveTraitForKerbal(
                "Tourist Kerman", KerbalsModule.RosterKerbalKind.Absent, null, rows, baselines));
            Assert.Equal("Pilot", KerbalsModule.ResolveTraitForKerbal(
                "Jeb Kerman", KerbalsModule.RosterKerbalKind.Absent, null, rows, null));
        }

        [Fact]
        public void ResolveTrait_AbsentKerbal_LatestBaselineTraitThenPilot()
        {
            var baselines = new List<GameStateBaseline>
            {
                BaselineWith("Val Kerman", "Pilot"),
                BaselineWith("Val Kerman", "Scientist")
            };
            Assert.Equal("Scientist", KerbalsModule.ResolveTraitForKerbal(
                "Val Kerman", KerbalsModule.RosterKerbalKind.Absent, null, null, baselines));
            Assert.Equal("Pilot", KerbalsModule.ResolveTraitForKerbal(
                "Nobody Kerman", KerbalsModule.RosterKerbalKind.Absent, null, null, baselines));
        }

        // ── Baseline capture ──

        // catches: the capture skipping tourists, so nothing remembered the trait once stock
        // deleted the kerbal.
        [Fact]
        public void TryBuildCrewEntry_CapturesTouristsAsTourist_AndCrew_SkipsOthers()
        {
            GameStateBaseline.CrewEntry entry;

            Assert.True(GameStateBaseline.TryBuildCrewEntry(
                "Tourist Kerman", "Assigned", null, ProtoCrewMember.KerbalType.Tourist, out entry));
            Assert.Equal("Tourist Kerman", entry.name);
            Assert.Equal("Assigned", entry.status);
            Assert.Equal("Tourist", entry.trait);

            Assert.True(GameStateBaseline.TryBuildCrewEntry(
                "Bill Kerman", "Available", "Engineer", ProtoCrewMember.KerbalType.Crew, out entry));
            Assert.Equal("Engineer", entry.trait);

            Assert.False(GameStateBaseline.TryBuildCrewEntry(
                "Applicant Kerman", "Available", "Pilot", ProtoCrewMember.KerbalType.Applicant, out entry));
            Assert.False(GameStateBaseline.TryBuildCrewEntry(
                "Offered Kerman", "Assigned", "Tourist", ProtoCrewMember.KerbalType.Unowned, out entry));
            Assert.False(GameStateBaseline.TryBuildCrewEntry(
                null, "Available", "Pilot", ProtoCrewMember.KerbalType.Crew, out entry));
        }

        // ── Walk evidence ──

        [Fact]
        public void HasTouristEvidence_ReadsTheWalksTouristRows_AndBaselines()
        {
            var module = new KerbalsModule();
            var row = new GameAction
            {
                UT = 0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-not-in-store",
                KerbalName = "Tourist Kerman",
                KerbalRole = "Tourist",
                KerbalEndStateField = KerbalEndState.Aboard,
                Sequence = 1
            };
            module.Reset();
            module.PrePass(new List<GameAction> { row });
            module.ProcessAction(row);
            module.PostWalk();
            LedgerOrchestrator.SetKerbalsForTesting(module);

            // An orphaned row (no recording metadata) holds nobody but is still evidence.
            Assert.Empty(module.Reservations);
            Assert.True(KerbalsModule.HasTouristEvidence("Tourist Kerman"));
            Assert.Equal("Tourist", KerbalsModule.FindTraitForKerbal("Tourist Kerman"));
            Assert.False(KerbalsModule.HasTouristEvidence("Jeb Kerman"));

            GameStateStore.AddBaseline(BaselineWith("Old Tourist Kerman", "Tourist"));
            Assert.True(KerbalsModule.HasTouristEvidence("Old Tourist Kerman"));

            module.Reset();
            Assert.False(KerbalsModule.HasTouristEvidence("Tourist Kerman"));
        }

        // ── Spawn planning ──

        private static ConfigNode Snapshot(params string[][] partsCrew)
        {
            var vessel = new ConfigNode("VESSEL");
            foreach (var crew in partsCrew)
            {
                var part = vessel.AddNode("PART");
                foreach (var name in crew)
                    part.AddValue("crew", name);
            }
            return vessel;
        }

        // catches GC-12 (1): a missing tourist re-created as hired crew.
        [Fact]
        public void PlanMissingCrew_MissingTourist_AsTourist_MissingCrew_AsCrew_PresentSkipped()
        {
            var snapshot = Snapshot(
                new[] { "Jeb Kerman", "Tourist Kerman" },
                new[] { "Imported Kerman", "Tourist Kerman" });
            var inRoster = new HashSet<string> { "Jeb Kerman" };

            var plan = VesselSpawner.PlanMissingCrew(
                snapshot, inRoster.Contains, n => n == "Tourist Kerman");

            Assert.Equal(2, plan.Count);
            Assert.Equal("Tourist Kerman", plan[0].Name);
            Assert.True(plan[0].AsTourist);
            Assert.Equal("Imported Kerman", plan[1].Name);
            Assert.False(plan[1].AsTourist);
        }

        [Fact]
        public void PlanMissingCrew_EveryoneInRoster_PlansNothing()
        {
            var snapshot = Snapshot(new[] { "Jeb Kerman", "Tourist Kerman" });
            var plan = VesselSpawner.PlanMissingCrew(snapshot, n => true, n => true);
            Assert.Empty(plan);
            Assert.Empty(VesselSpawner.PlanMissingCrew(null, n => false, null));
        }

        [Fact]
        public void PlanMissingCrew_NoEvidenceSource_IsCrew()
        {
            var plan = VesselSpawner.PlanMissingCrew(
                Snapshot(new[] { "Synthetic Kerman" }), n => false, null);
            Assert.Single(plan);
            Assert.False(plan[0].AsTourist);
        }

        // ── Re-derived rows keep the tourist role ──

        private static GameAction Row(string name, string role)
        {
            return new GameAction
            {
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-1",
                KerbalName = name,
                KerbalRole = role
            };
        }

        // catches: the load repair re-deriving a deleted tourist's row as "Pilot" (the
        // roster cannot confirm him), which would put a stand-in under his hold.
        [Fact]
        public void CarryTouristRoles_ExistingTouristRow_KeepsTheRoleOnTheDerivedRow()
        {
            var existing = new List<GameAction> { Row("Tourist Kerman", "Tourist"), Row("Jeb Kerman", "Pilot") };
            var desired = new List<GameAction> { Row("Jeb Kerman", "Pilot"), Row("Tourist Kerman", "Pilot") };

            Assert.Equal(1, LedgerOrchestrator.CarryTouristRoles(existing, desired));
            Assert.Equal("Pilot", desired[0].KerbalRole);
            Assert.Equal("Tourist", desired[1].KerbalRole);

            Assert.Equal(0, LedgerOrchestrator.CarryTouristRoles(existing, desired));
            Assert.Equal(0, LedgerOrchestrator.CarryTouristRoles(null, desired));
            Assert.Equal(0, LedgerOrchestrator.CarryTouristRoles(
                new List<GameAction> { Row("Jeb Kerman", "Pilot") },
                new List<GameAction> { Row("Jeb Kerman", "Engineer") }));
        }
    }
}
