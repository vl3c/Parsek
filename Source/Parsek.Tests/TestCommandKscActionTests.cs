using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C1 coverage for the pure KscAction accept / typed-refusal core: kind parse,
    /// manifest-kind mapping, and each refusal boundary. Fails if an unaffordable action is
    /// admitted (a false OK), a refusal maps to the wrong reason, or the manifest kind
    /// drifts. The Unity applier (real stock APIs + effect confirmation) is exercised
    /// in-game / PENDING-OPERATOR.
    /// </summary>
    public class TestCommandKscActionTests
    {
        // ----- ParseKind + ManifestKindFor -----

        [Fact]
        public void ParseKind_And_ManifestKind()
        {
            Assert.Equal(KscActionKind.ResearchNode, TestCommandKscAction.ParseKind("research-node"));
            Assert.Equal("tech-unlock", TestCommandKscAction.ManifestKindFor(KscActionKind.ResearchNode));

            Assert.Equal(KscActionKind.UpgradeFacility, TestCommandKscAction.ParseKind("upgrade-facility"));
            Assert.Equal("facility-upgrade", TestCommandKscAction.ManifestKindFor(KscActionKind.UpgradeFacility));

            Assert.Equal(KscActionKind.HireKerbal, TestCommandKscAction.ParseKind("hire-kerbal"));
            Assert.Equal("kerbal-hire", TestCommandKscAction.ManifestKindFor(KscActionKind.HireKerbal));

            Assert.Equal(KscActionKind.DismissKerbal, TestCommandKscAction.ParseKind("dismiss-kerbal"));
            Assert.Equal("kerbal-dismiss", TestCommandKscAction.ManifestKindFor(KscActionKind.DismissKerbal));
        }

        [Fact]
        public void ParseKind_And_ManifestKind_BuildingSubActions()
        {
            Assert.Equal(KscActionKind.DemolishBuilding, TestCommandKscAction.ParseKind("demolish-building"));
            Assert.Equal("facility-destruction", TestCommandKscAction.ManifestKindFor(KscActionKind.DemolishBuilding));
            Assert.Equal(KscActionKind.RepairFacility, TestCommandKscAction.ParseKind("repair-facility"));
            Assert.Equal("facility-repair", TestCommandKscAction.ManifestKindFor(KscActionKind.RepairFacility));
        }

        [Fact]
        public void Decide_Demolish_RefusalsAndAccept()
        {
            Assert.Equal("unknown-building", TestCommandKscAction.Decide("demolish-building", "x",
                new KscActionInputs { ArgPresent = true, TargetResolves = false }).RejectReason);
            Assert.Equal("building-already-down", TestCommandKscAction.Decide("demolish-building", "x",
                new KscActionInputs { ArgPresent = true, TargetResolves = true, AlreadyApplied = true }).RejectReason);
            var ok = TestCommandKscAction.Decide("demolish-building", "x",
                new KscActionInputs { ArgPresent = true, TargetResolves = true });
            Assert.True(ok.Accepted);
            Assert.Equal("facility-destruction", ok.ManifestKind);
        }

        [Fact]
        public void Decide_Repair_RefusalsAndAccept()
        {
            Assert.Equal("unknown-facility", TestCommandKscAction.Decide("repair-facility", "f",
                new KscActionInputs { ArgPresent = true, TargetResolves = false }).RejectReason);
            Assert.Equal("facility-intact", TestCommandKscAction.Decide("repair-facility", "f",
                new KscActionInputs { ArgPresent = true, TargetResolves = true, AlreadyApplied = true }).RejectReason);
            Assert.Equal("insufficient-funds", TestCommandKscAction.Decide("repair-facility", "f",
                new KscActionInputs { ArgPresent = true, TargetResolves = true, CostAmount = 100, AvailableAmount = 50 }).RejectReason);
            Assert.True(TestCommandKscAction.Decide("repair-facility", "f",
                new KscActionInputs { ArgPresent = true, TargetResolves = true, CostAmount = 50, AvailableAmount = 50 }).Accepted);
        }

        [Fact]
        public void ResolveBuildingId_ExactThenUniqueSuffix()
        {
            var ids = new[]
            {
                "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_waterTower",
                "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_cylTank",
                "SpaceCenter/Runway/Facility/a/Tank",
                "SpaceCenter/VehicleAssemblyBuilding/Facility/Tank",
            };
            Assert.Equal(ids[0], TestCommandKscAction.ResolveBuildingId(ids, "ksp_pad_waterTower"));
            Assert.Equal(ids[1], TestCommandKscAction.ResolveBuildingId(ids, ids[1]));
            // Ambiguous suffix and a partial segment both resolve to nothing.
            Assert.Null(TestCommandKscAction.ResolveBuildingId(ids, "Tank"));
            Assert.Null(TestCommandKscAction.ResolveBuildingId(ids, "waterTower"));
            Assert.Null(TestCommandKscAction.ResolveBuildingId(ids, ""));
            Assert.Null(TestCommandKscAction.ResolveBuildingId(null, "x"));
        }

        [Fact]
        public void AnyStructureSettling_OnlyTheBetweenStatesPair()
        {
            // (IsIntact, IsDestroyed): intact, destroyed, and mid-animation.
            var intact = new KeyValuePair<bool, bool>(true, false);
            var down = new KeyValuePair<bool, bool>(false, true);
            var moving = new KeyValuePair<bool, bool>(false, false);
            Assert.False(TestCommandKscAction.AnyStructureSettling(new[] { intact, down }));
            Assert.True(TestCommandKscAction.AnyStructureSettling(new[] { intact, moving }));
            Assert.False(TestCommandKscAction.AnyStructureSettling(null));
        }

        [Theory]
        [InlineData("ResearchNode")]      // wrong case
        [InlineData("research_node")]     // wrong separator
        [InlineData("")]
        [InlineData(null)]
        [InlineData("complete-contract")] // deferred batch-2 sub-action
        public void ParseKind_Unknown(string action)
        {
            Assert.Equal(KscActionKind.Unknown, TestCommandKscAction.ParseKind(action));
        }

        // ----- Decide: unknown-action / missing-arg -----

        [Fact]
        public void Decide_UnknownAction_Rejects()
        {
            var d = TestCommandKscAction.Decide("no-such-action", "x", new KscActionInputs { ArgPresent = true, TargetResolves = true });
            Assert.False(d.Accepted);
            Assert.Equal("unknown-action", d.RejectReason);
        }

        [Fact]
        public void Decide_MissingArg_Rejects()
        {
            var d = TestCommandKscAction.Decide("research-node", "", new KscActionInputs { ArgPresent = false });
            Assert.False(d.Accepted);
            Assert.Equal("missing-arg", d.RejectReason);
        }

        // ----- research-node -----

        private static KscActionInputs Research(bool resolves, bool already, double cost, double science)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, AlreadyApplied = already,
                CostAmount = cost, AvailableAmount = science, CostIsFunds = false,
            };

        [Fact]
        public void Decide_Research_Accept()
        {
            var d = TestCommandKscAction.Decide("research-node", "basicRocketry", Research(true, false, 45, 100));
            Assert.True(d.Accepted);
            Assert.Null(d.RejectReason);
            Assert.Equal("tech-unlock", d.ManifestKind);
            Assert.Equal(KscActionKind.ResearchNode, d.Kind);
        }

        [Fact]
        public void Decide_Research_UnknownNode()
        {
            var d = TestCommandKscAction.Decide("research-node", "nope", Research(false, false, 0, 100));
            Assert.Equal("unknown-tech-node", d.RejectReason);
        }

        [Fact]
        public void Decide_Research_AlreadyUnlocked()
        {
            var d = TestCommandKscAction.Decide("research-node", "basicRocketry", Research(true, true, 45, 100));
            Assert.Equal("node-already-unlocked", d.RejectReason);
        }

        [Fact]
        public void Decide_Research_InsufficientScience()
        {
            var d = TestCommandKscAction.Decide("research-node", "basicRocketry", Research(true, false, 45, 44.9));
            Assert.Equal("insufficient-science", d.RejectReason);
        }

        [Fact]
        public void Decide_Research_ExactCost_Affordable()
        {
            // cost == available is affordable (cost > available is the refusal boundary).
            var d = TestCommandKscAction.Decide("research-node", "basicRocketry", Research(true, false, 45, 45));
            Assert.True(d.Accepted);
        }

        // ----- upgrade-facility -----

        private static KscActionInputs Facility(bool resolves, bool atMax, double cost, double funds)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, AlreadyApplied = atMax,
                CostAmount = cost, AvailableAmount = funds, CostIsFunds = true,
            };

        [Fact]
        public void Decide_Facility_Accept()
        {
            var d = TestCommandKscAction.Decide("upgrade-facility", "LaunchPad", Facility(true, false, 20000, 100000));
            Assert.True(d.Accepted);
            Assert.Equal("facility-upgrade", d.ManifestKind);
        }

        [Fact]
        public void Decide_Facility_UnknownFacility()
        {
            var d = TestCommandKscAction.Decide("upgrade-facility", "Nope", Facility(false, false, 0, 100000));
            Assert.Equal("unknown-facility", d.RejectReason);
        }

        [Fact]
        public void Decide_Facility_AtMax()
        {
            var d = TestCommandKscAction.Decide("upgrade-facility", "LaunchPad", Facility(true, true, 20000, 100000));
            Assert.Equal("facility-at-max", d.RejectReason);
        }

        [Fact]
        public void Decide_Facility_InsufficientFunds()
        {
            var d = TestCommandKscAction.Decide("upgrade-facility", "LaunchPad", Facility(true, false, 20000, 19999));
            Assert.Equal("insufficient-funds", d.RejectReason);
        }

        // ----- hire-kerbal -----

        private static KscActionInputs Hire(bool resolves, bool applicant, double cost, double funds)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, IsApplicant = applicant,
                CostAmount = cost, AvailableAmount = funds, CostIsFunds = true,
            };

        [Fact]
        public void Decide_Hire_Accept()
        {
            var d = TestCommandKscAction.Decide("hire-kerbal", "Jeb Kerman", Hire(true, true, 40000, 100000));
            Assert.True(d.Accepted);
            Assert.Equal("kerbal-hire", d.ManifestKind);
        }

        [Fact]
        public void Decide_Hire_UnknownKerbal()
        {
            var d = TestCommandKscAction.Decide("hire-kerbal", "Nobody", Hire(false, false, 0, 100000));
            Assert.Equal("unknown-kerbal", d.RejectReason);
        }

        [Fact]
        public void Decide_Hire_NotApplicant()
        {
            // Resolves in the roster but not in the applicant pool (already crew).
            var d = TestCommandKscAction.Decide("hire-kerbal", "Jeb Kerman", Hire(true, false, 40000, 100000));
            Assert.Equal("kerbal-not-applicant", d.RejectReason);
        }

        [Fact]
        public void Decide_Hire_InsufficientFunds()
        {
            var d = TestCommandKscAction.Decide("hire-kerbal", "Jeb Kerman", Hire(true, true, 40000, 39999));
            Assert.Equal("insufficient-funds", d.RejectReason);
        }

        // ----- dismiss-kerbal -----

        private static KscActionInputs Dismiss(bool resolves, bool managed, bool dismissable)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, IsParsekManaged = managed, IsDismissable = dismissable,
            };

        [Fact]
        public void Decide_Dismiss_Accept()
        {
            var d = TestCommandKscAction.Decide("dismiss-kerbal", "Bob Kerman", Dismiss(true, false, true));
            Assert.True(d.Accepted);
            Assert.Equal("kerbal-dismiss", d.ManifestKind);
        }

        [Fact]
        public void Decide_Dismiss_UnknownKerbal()
        {
            var d = TestCommandKscAction.Decide("dismiss-kerbal", "Nobody", Dismiss(false, false, false));
            Assert.Equal("unknown-kerbal", d.RejectReason);
        }

        [Fact]
        public void Decide_Dismiss_ParsekManaged_TakesPrecedence()
        {
            // A Parsek-managed kerbal is pre-declined before the dismissability check,
            // mirroring the KerbalDismissalPatch IsManaged block.
            var d = TestCommandKscAction.Decide("dismiss-kerbal", "Jeb Kerman", Dismiss(true, managed: true, dismissable: false));
            Assert.Equal("kerbal-parsek-managed", d.RejectReason);
        }

        [Fact]
        public void Decide_Dismiss_NotDismissable()
        {
            // Not managed, but assigned / tourist / protected.
            var d = TestCommandKscAction.Decide("dismiss-kerbal", "Val Kerman", Dismiss(true, managed: false, dismissable: false));
            Assert.Equal("kerbal-not-dismissable", d.RejectReason);
        }

        // ----- activate-strategy / deactivate-strategy -----

        private static KscActionInputs Strategy(bool resolves = true, bool applied = false,
            bool factorInvalid = false, int used = 0, int limit = 1)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, AlreadyApplied = applied,
                FactorArgInvalid = factorInvalid, SlotsUsed = used, SlotLimit = limit,
            };

        [Fact]
        public void ParseKind_And_ManifestKind_StrategySubActions()
        {
            Assert.Equal(KscActionKind.ActivateStrategy, TestCommandKscAction.ParseKind("activate-strategy"));
            Assert.Equal("strategy-activate", TestCommandKscAction.ManifestKindFor(KscActionKind.ActivateStrategy));
            Assert.Equal(KscActionKind.DeactivateStrategy, TestCommandKscAction.ParseKind("deactivate-strategy"));
            Assert.Equal("strategy-deactivate", TestCommandKscAction.ManifestKindFor(KscActionKind.DeactivateStrategy));
            // Exact kebab-case only, like every other sub-action.
            Assert.Equal(KscActionKind.Unknown, TestCommandKscAction.ParseKind("Activate-Strategy"));
        }

        [Fact]
        public void Decide_ActivateStrategy_RefusalsInOrder()
        {
            Assert.Equal("missing-arg", TestCommandKscAction.Decide("activate-strategy", null,
                new KscActionInputs { ArgPresent = false }).RejectReason);
            Assert.Equal("unknown-strategy", TestCommandKscAction.Decide("activate-strategy", "Nope",
                Strategy(resolves: false)).RejectReason);
            // A bad factor is named before the strategy's own state, so a typo never reads
            // as "already active" or "no slot".
            Assert.Equal("factor-arg-invalid", TestCommandKscAction.Decide("activate-strategy", "S",
                Strategy(factorInvalid: true, applied: true, used: 1, limit: 1)).RejectReason);
            Assert.Equal("strategy-already-active", TestCommandKscAction.Decide("activate-strategy", "S",
                Strategy(applied: true, used: 1, limit: 1)).RejectReason);
            Assert.Equal("no-strategy-slot", TestCommandKscAction.Decide("activate-strategy", "S",
                Strategy(used: 1, limit: 1)).RejectReason);
            // A zero limit (the read failed) is a refusal, never a free slot.
            Assert.Equal("no-strategy-slot", TestCommandKscAction.Decide("activate-strategy", "S",
                Strategy(used: 0, limit: 0)).RejectReason);
        }

        [Fact]
        public void Decide_ActivateStrategy_Accepts_WithAFreeSlot()
        {
            var d = TestCommandKscAction.Decide("activate-strategy", "OutsourcedResearchCfg", Strategy(used: 1, limit: 2));
            Assert.True(d.Accepted);
            Assert.Null(d.RejectReason);
            Assert.Equal("strategy-activate", d.ManifestKind);
            Assert.Equal("OutsourcedResearchCfg", d.Target);
        }

        [Fact]
        public void Decide_DeactivateStrategy_RefusalsAndAccept()
        {
            Assert.Equal("unknown-strategy", TestCommandKscAction.Decide("deactivate-strategy", "Nope",
                Strategy(resolves: false)).RejectReason);
            Assert.Equal("strategy-not-active", TestCommandKscAction.Decide("deactivate-strategy", "S",
                Strategy(applied: true)).RejectReason);
            // Slots do not gate a cancel.
            var d = TestCommandKscAction.Decide("deactivate-strategy", "S", Strategy(used: 1, limit: 1));
            Assert.True(d.Accepted);
            Assert.Equal("strategy-deactivate", d.ManifestKind);
        }

        [Theory]
        [InlineData(null, true, float.NaN)]
        [InlineData("", true, float.NaN)]
        [InlineData("0.05", true, 0.05f)]
        [InlineData("1", true, 1f)]
        [InlineData("0.25", true, 0.25f)]
        [InlineData("0", false, float.NaN)]
        [InlineData("-0.1", false, float.NaN)]
        [InlineData("1.5", false, float.NaN)]
        [InlineData("NaN", false, float.NaN)]
        [InlineData("Infinity", false, float.NaN)]
        [InlineData("abc", false, float.NaN)]
        public void TryParseStrategyFactor_AcceptsOnlyAHalfOpenUnitRange(string raw, bool ok, float expected)
        {
            Assert.Equal(ok, TestCommandKscAction.TryParseStrategyFactor(raw, out float f));
            if (float.IsNaN(expected)) Assert.True(float.IsNaN(f));
            else Assert.Equal(expected, f);
        }

        [Fact]
        public void TryParseStrategyFactor_IsCultureInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.True(TestCommandKscAction.TryParseStrategyFactor("0.5", out float f));
                Assert.Equal(0.5f, f);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData(null, "(none)")]
        [InlineData("", "(none)")]
        [InlineData("   ", "(none)")]
        [InlineData("Not enough funds", "Not enough funds")]
        [InlineData("<color=orange>Requires 20%\ncommitment</color>", "Requires 20% commitment")]
        [InlineData("  a \t\r\n  b  ", "a b")]
        public void SanitizeStockReason_OneLineNoTags(string raw, string expected)
        {
            Assert.Equal(expected, TestCommandKscAction.SanitizeStockReason(raw));
        }
    }
}
