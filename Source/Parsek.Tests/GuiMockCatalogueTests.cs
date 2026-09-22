using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek.TestCommands;
using Parsek.UI.Gallery;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The compiled GUI-state-gallery catalogue: id grammar, uniqueness, label safety,
    /// and - the cell that matters - that every state's builder constructs a REAL,
    /// DRAWABLE view model.
    ///
    /// <para><b>Why "drawable" is asserted as row counts and statuses rather than as
    /// strings.</b> A builder that typed its own cell text would pass any string
    /// assertion and teach the owner about a picture the product cannot produce. So these
    /// cells assert the SHAPE the draw method needs (a roster row exists, its status is
    /// the classified one, a facility row carries the level the ledger walk projected) and
    /// leave the rendering to the real pure helpers - which is exactly what makes the
    /// derived witness set (<see cref="GuiMockWitness"/>) a meaningful read-back.</para>
    /// </summary>
    [Collection("Sequential")]
    public class GuiMockCatalogueTests : IDisposable
    {
        private readonly bool savedSuppressLogging;
        private readonly List<string> logLines = new List<string>();

        public GuiMockCatalogueTests()
        {
            savedSuppressLogging = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GuiMockSession.ResetForTesting();
        }

        public void Dispose()
        {
            GuiMockSession.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = savedSuppressLogging;
        }

        // The id grammar the mirror's label parser and the capture verb both depend on.
        private static readonly Regex IdShape =
            new Regex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$", RegexOptions.CultureInvariant);

        // TestCommandCaptureScreenshot.IsValidLabel's rule, mirrored here so a catalogue
        // id that produces an unusable label reds LOCALLY rather than after a KSP boot.
        private static readonly Regex LabelShape =
            new Regex(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9-])?$",
                      RegexOptions.CultureInvariant);

        private const int LabelMaxLength = 96;

        [Fact]
        public void TheCatalogueCarriesTheP1WindowsAndNothingElse()
        {
            // A count FLOOR rather than an exact number: states are added by later
            // phases and a pinned total would be a merge conflict on every one. The
            // floor is what stops the loop below going vacuous.
            Assert.True(GuiMockCatalogue.All.Count >= 40,
                "the P1 catalogue is ~45 states across three windows; found "
                + GuiMockCatalogue.All.Count);

            Assert.Equal(
                new[] { "kerbals", "career", "structure" },
                GuiMockCatalogue.SupportedWindows);

            foreach (string window in GuiMockCatalogue.SupportedWindows)
            {
                Assert.True(GuiMockCatalogue.ForWindow(window).Count > 0,
                    "window '" + window + "' is declared supported and has no states, so "
                    + "op=mock on it can only ever answer mock-state-unknown");
            }
        }

        [Fact]
        public void EveryWindowTokenIsARealSeamWindow()
        {
            // The gallery declares its own window constants so the UI layer does not
            // reach into the seam layer for a string; this is what keeps the two equal.
            Assert.Equal(TestCommandUiAction.KerbalsWindow, GuiMockSession.KerbalsWindow);
            Assert.Equal(TestCommandUiAction.CareerWindow, GuiMockSession.CareerWindow);
            Assert.Equal(TestCommandUiAction.StructureWindow, GuiMockSession.StructureWindow);

            string[] seamWindows = TestCommandUiAction.Windows.Select(w => w.Name).ToArray();
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                Assert.True(seamWindows.Contains(state.Window),
                    "state '" + state.Id + "' names window '" + state.Window
                    + "', which is not in the seam's window table");
                Assert.True(GuiMockCatalogue.IsSupportedWindow(state.Window),
                    "state '" + state.Id + "' names window '" + state.Window
                    + "', which the applier does not claim to support");
            }
        }

        [Fact]
        public void EveryIdIsUniqueWellShapedAndPrefixedByItsWindow()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                Assert.True(seen.Add(state.Id),
                    "duplicate catalogue id '" + state.Id + "'. Nothing in the harness "
                    + "validates within-run label uniqueness, so two states sharing an id "
                    + "would silently overwrite one capture with the other.");
                Assert.True(IdShape.IsMatch(state.Id),
                    "id '" + state.Id + "' is not <window>.<family>.<variant> in lower-case "
                    + "ASCII with dots and dashes only");
                Assert.StartsWith(state.Window + ".", state.Id, StringComparison.Ordinal);
                Assert.Same(state, GuiMockCatalogue.ById(state.Id));
            }
        }

        [Fact]
        public void EveryDerivedLabelIsUniqueAndSurvivesTheCaptureVerbsRule()
        {
            foreach (bool basic in new[] { false, true })
            {
                var labels = new HashSet<string>(StringComparer.Ordinal);
                foreach (GuiMockState state in GuiMockCatalogue.All)
                {
                    string label = TestCommandUiMock.DeriveLabel(
                        TestCommandUiMock.DefaultLabelPrefix, state.Id, basic);
                    Assert.True(label.Length <= LabelMaxLength,
                        "label '" + label + "' is " + label.Length + " chars; the capture "
                        + "verb caps at " + LabelMaxLength);
                    Assert.True(LabelShape.IsMatch(label),
                        "label '" + label + "' does not match the capture verb's own rule, "
                        + "which is deliberately stricter than the dump recorder's "
                        + "sanitiser so an accepted label survives it unchanged");
                    Assert.True(labels.Add(label),
                        "two catalogue states derive the SAME label '" + label + "'. A "
                        + "gallery run would overwrite one capture with the other and "
                        + "nothing in the harness would say so.");
                }
            }
        }

        [Fact]
        public void EveryBuilderProducesAPayloadOfItsOwnWindowAndAtLeastOneWitness()
        {
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                GuiMockPayload payload = state.Build();
                Assert.NotNull(payload);
                Assert.Equal(state.Window, payload.Window);

                // Exactly one carrier is populated: a payload with two would be a state
                // the applier installs into one window and witnesses through another.
                int carriers = (payload.Kerbals.HasValue ? 1 : 0)
                               + (payload.Career.HasValue ? 1 : 0)
                               + (payload.Structure != null ? 1 : 0);
                Assert.True(carriers == 1,
                    "state '" + state.Id + "' populated " + carriers + " payload carriers");

                List<string> witnesses = GuiMockWitness.Expected(payload, state.Tab,
                                                                 state.Covers);
                Assert.True(witnesses.Count > 0,
                    "state '" + state.Id + "' produced NO witness string, so its "
                    + "mock-not-applied read-back would be vacuous for exactly that state. "
                    + "Give it a drawable row, or do not catalogue it.");
                Assert.True(witnesses.Count <= GuiMockWitness.MaxWitnesses);
                foreach (string w in witnesses)
                    Assert.False(string.IsNullOrWhiteSpace(w));
            }
        }

        [Fact]
        public void EveryStateThatClaimsABranchWitnessesTheRowThatCarriesIt()
        {
            // The review's finding: 22 of 46 witness sets missed the state's point, and 7
            // matched what a REAL window draws. A witness derived from "the first row" is
            // a statement about the window, not about the state - so a state that claims a
            // branch must witness the ROW that carries it.
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                if (state.Covers.Length == 0) continue;
                List<string> covered = GuiMockWitness.Covered(
                    state.Build(), state.Tab, state.Covers);
                Assert.True(covered.Count > 0,
                    "state '" + state.Id + "' claims " + string.Join(",", state.Covers)
                    + " and no witness derives from the row carrying it. Either the state "
                    + "does not actually produce that branch, or GuiMockWitness.Covered "
                    + "has no derivation for it - both are real gaps.");
                List<string> all = GuiMockWitness.Expected(
                    state.Build(), state.Tab, state.Covers);
                foreach (string c in covered) Assert.Contains(c, all);
            }
        }

        [Fact]
        public void NoTwoStatesShareAnIdenticalWitnessSet()
        {
            // Two states with the same witness set are one picture filed twice: whatever
            // distinguishes them is invisible to the read-back, so a lane cannot tell
            // which one it captured. The review found four such pairs; each was dropped or
            // differentiated rather than exempted, so this cell has no allowlist on
            // purpose - an exemption here would be the very thing it exists to prevent.
            var byWitness = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                string key = state.Window + "|" + (state.Tab ?? "-") + "|"
                    + string.Join("\u0001", GuiMockWitness.Expected(
                        state.Build(), state.Tab, state.Covers));
                string prior;
                Assert.False(byWitness.TryGetValue(key, out prior),
                    "states '" + (prior ?? "?") + "' and '" + state.Id + "' derive the "
                    + "SAME witness set on the same window and tab, so nothing in the "
                    + "read-back can tell them apart. Differentiate one or drop it.");
                byWitness[key] = state.Id;
            }
        }

        [Fact]
        public void NoWitnessIsABareNumber()
        {
            // The doc claimed this exclusion before the code implemented it, which the
            // review caught. A UT, a level, a reward and a deadline are all numbers a real
            // window draws every frame, so a numeric witness is satisfied by any capture
            // of the same window.
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                foreach (string witness in GuiMockWitness.Expected(
                             state.Build(), state.Tab, state.Covers))
                {
                    Assert.True(witness.Any(char.IsLetter),
                        "state '" + state.Id + "' witness '" + witness
                        + "' has no letter in it, so it is a number the window draws "
                        + "whatever model it holds");
                }
            }
        }

        [Fact]
        public void EveryMockableWindowIsRefusedInAModeThatHidesIt()
        {
            // Basic HIDES the Kerbals and Career launchers and the mode switch
            // force-closes both, so a Basic apply would photograph a window no player can
            // open. Checked against the PRODUCTION visibility predicate rather than a
            // per-state pin, which is why design 7.1's per-state Mode field was dropped.
            Assert.False(GuiMockCatalogue.IsMockableInMode(
                GuiMockSession.KerbalsWindow, UiComplexityMode.Basic));
            Assert.False(GuiMockCatalogue.IsMockableInMode(
                GuiMockSession.CareerWindow, UiComplexityMode.Basic));
            foreach (string window in GuiMockCatalogue.SupportedWindows)
            {
                Assert.True(GuiMockCatalogue.IsMockableInMode(
                    window, UiComplexityMode.Advanced),
                    "window '" + window + "' is not mockable even in Advanced");
            }
            // Structure List has no launcher surface at all, so it is mockable in both -
            // it is opened from a row and is not in the gated close set.
            UiSurface unused;
            Assert.False(GuiMockCatalogue.TryGetLauncherSurface(
                GuiMockSession.StructureWindow, out unused));
            Assert.True(GuiMockCatalogue.IsMockableInMode(
                GuiMockSession.StructureWindow, UiComplexityMode.Basic));
        }

        [Fact]
        public void EveryBuilderIsIndependentOfTheLastOne()
        {
            // Build twice and assert the second call produced a FRESH object graph. A
            // builder that returned a shared mutable list would let one capture's
            // expansion state (or one applier's restore) leak into the next state.
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                GuiMockPayload a = state.Build();
                GuiMockPayload b = state.Build();
                Assert.NotSame(a, b);
                if (a.Structure != null)
                    Assert.NotSame(a.Structure.Steps, b.Structure.Steps);
                if (a.Kerbals.HasValue)
                {
                    Assert.NotSame(a.Kerbals.Value.Flights, b.Kerbals.Value.Flights);
                    Assert.NotSame(a.Kerbals.Value.Roster.Involved,
                                   b.Kerbals.Value.Roster.Involved);
                }
                if (a.Career.HasValue)
                {
                    Assert.NotSame(a.Career.Value.Facilities.Rows,
                                   b.Career.Value.Facilities.Rows);
                }
            }
        }

        [Fact]
        public void EveryStateDeclaresATabExactlyWhenItsWindowHasOne()
        {
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                UiWindowSpec spec;
                string reject;
                Assert.True(TestCommandUiAction.TryResolveWindow(
                    state.Window, out spec, out reject));
                bool windowHasTabs = spec.Tabs != null && spec.Tabs.Length > 0;
                if (!windowHasTabs)
                {
                    Assert.True(state.Tab == null,
                        "state '" + state.Id + "' pins tab '" + state.Tab
                        + "' on a window with no selector");
                    continue;
                }
                Assert.True(state.Tab != null,
                    "state '" + state.Id + "' is on a tabbed window and pins no tab. The "
                    + "witness set is tab-scoped, so an unpinned tab makes the "
                    + "draw-produced read-back a coin flip.");
                int index;
                Assert.True(TestCommandUiAction.TryResolveTab(
                    spec, state.Tab, out index, out reject),
                    "state '" + state.Id + "' pins tab '" + state.Tab
                    + "', which window '" + state.Window + "' does not offer");
            }
        }

        [Fact]
        public void EveryStateDeclaresAWindowSizeAndANote()
        {
            foreach (GuiMockState state in GuiMockCatalogue.All)
            {
                // Every state sizes its window (design D2), because the default first-open
                // size clips the wide tables and a clipped capture reads as a layout
                // defect rather than as a small window.
                Assert.True(state.RectW >= 420 && state.RectH >= 160,
                    "state '" + state.Id + "' declares rect " + state.RectW + "x"
                    + state.RectH + ", below the narrowest window minimum in the program");
                Assert.False(string.IsNullOrWhiteSpace(state.Note),
                    "state '" + state.Id + "' carries no Note. The note is what tells a "
                    + "reader of KSP.log why the state exists and which LIVE cells it "
                    + "cannot pin.");
                Assert.NotNull(state.Covers);
                Assert.NotNull(state.ExpandKeys);
            }
        }

        // ----- drawability, per window, asserted as SHAPE -----

        [Fact]
        public void TheLostRosterStateClassifiesItsKerbalThroughTheRealRule()
        {
            GuiMockPayload payload = GuiMockCatalogue.ById("kerbals.roster.lost").Build();
            KerbalsPresentation.RosterRowSet set = payload.Kerbals.Value.Roster;
            KerbalsPresentation.RosterRow lost = set.Involved.Concat(set.Plain)
                .Single(r => r.Status == KerbalsPresentation.RosterStatus.Lost);
            Assert.Equal("Jebediah Kerman", lost.Name);
            // The Since cell is dated - which only the real builder can do, from the
            // flight that produced the loss.
            Assert.NotEqual(KerbalsPresentation.EmptyCell, lost.SinceText);
            Assert.NotEqual(KerbalsPresentation.EmptyCell, lost.LastFlightText);
        }

        [Fact]
        public void TheAllStatusRosterStateReachesEverySixStatuses()
        {
            GuiMockPayload payload =
                GuiMockCatalogue.ById("kerbals.roster.all-statuses").Build();
            KerbalsPresentation.RosterRowSet set = payload.Kerbals.Value.Roster;
            var statuses = set.Involved.Concat(set.Plain).Select(r => r.Status).ToList();
            foreach (KerbalsPresentation.RosterStatus want in
                     Enum.GetValues(typeof(KerbalsPresentation.RosterStatus))
                         .Cast<KerbalsPresentation.RosterStatus>())
            {
                Assert.True(statuses.Contains(want),
                    "kerbals.roster.all-statuses is the overview state and produced no "
                    + want + " row. It is built from INPUTS, so a status it cannot reach "
                    + "is a statement about ClassifyStatus, not about this test.");
            }
        }

        [Fact]
        public void TheTwoDeepChainStateProducesADisplacedMemberAndAnActiveOne()
        {
            GuiMockState state = GuiMockCatalogue.ById("kerbals.roster.chain-two-deep");
            // The chain only DRAWS when its row is expanded, so the state has to say so.
            Assert.Contains("Jebediah Kerman", state.ExpandKeys);

            GuiMockPayload payload = state.Build();
            KerbalsPresentation.RosterRowSet set = payload.Kerbals.Value.Roster;
            KerbalsPresentation.RosterRow owner = set.Involved.Concat(set.Plain)
                .Single(r => r.Name == "Jebediah Kerman");
            Assert.Equal(2, owner.Chain.Count);
            var chainStatuses = owner.Chain.Select(m => m.Status).ToList();
            Assert.Contains(KerbalsWindowUI.ChainMemberStatus.Active, chainStatuses);
            Assert.Contains(KerbalsWindowUI.ChainMemberStatus.Displaced, chainStatuses);
        }

        [Fact]
        public void TheStandInAboardStateMovesItsVesselIntoTheHoverText()
        {
            KerbalsPresentation.RosterRow row = StandInRow("kerbals.roster.standin-aboard");
            Assert.False(string.IsNullOrEmpty(row.StatusTooltipText),
                "an active stand-in who is aboard a craft carries the only hover text in "
                + "the whole window, and this is the state that photographs it");
            Assert.DoesNotContain("aboard", row.StatusText, StringComparison.Ordinal);
        }

        [Fact]
        public void TheInlineStandInVesselFormIsUnreachableForRealNames()
        {
            // A PRODUCT FINDING the catalogue surfaced, pinned so it stays visible: the
            // inline "Stand-in for X (aboard Y)" branch of FormatStatus cannot fire for
            // any real kerbal. It keeps the vessel inline only while the composed text
            // fits StatusCellMaxChars, and the fixed scaffolding alone
            // ("Stand-in for " + " (aboard " + ")") is 23 characters, leaving 8 for the
            // owner name AND the vessel together - while the shortest stock kerbal name
            // is "Bob Kerman" at 10.
            //
            // The catalogue therefore has no inline state, and this cell is what makes
            // that absence a measured fact rather than an omission. It is written against
            // the REAL formatter, so a widened column or a shortened form flips it - and
            // the fix then is to ADD the inline state, which is what the failure says.
            const string shortestStockName = "Bob Kerman";
            string inline = KerbalsPresentation.FormatStatus(
                KerbalsPresentation.RosterStatus.StandIn,
                name: "Lars Kerman", slotOwnerName: shortestStockName,
                reservation: null, assignedVesselName: "X", formatDate: null);
            Assert.DoesNotContain("aboard", inline, StringComparison.Ordinal);
            Assert.True(
                ("Stand-in for " + shortestStockName + " (aboard X)").Length
                    > KerbalsPresentation.StatusCellMaxChars,
                "the inline stand-in form now FITS for the shortest stock kerbal name. "
                + "Add kerbals.roster.standin-aboard-inline to the catalogue and update "
                + "the todo entry - the branch is reachable again.");
        }

        private static KerbalsPresentation.RosterRow StandInRow(string stateId)
        {
            GuiMockPayload payload = GuiMockCatalogue.ById(stateId).Build();
            KerbalsPresentation.RosterRowSet set = payload.Kerbals.Value.Roster;
            return set.Involved.Concat(set.Plain)
                .Single(r => r.Status == KerbalsPresentation.RosterStatus.StandIn);
        }

        [Fact]
        public void TheDivergentCareerBannerStateActuallyDiverges()
        {
            GuiMockPayload payload = GuiMockCatalogue.ById("career.banner.divergent").Build();
            CareerStateWindowUI.CareerStateViewModel vm = payload.Career.Value;
            Assert.True(vm.HasDivergence,
                "the banner's whole point is the divergence tail; the real VM walk decides "
                + "it, so a state that does not diverge photographs the ordinary banner");
            Assert.True(vm.TerminalUT > vm.LiveUT);
            Assert.True(vm.Contracts.ProjectedRows.Count > vm.Contracts.CurrentRows.Count);
            Assert.Contains(vm.Contracts.ProjectedRows, r => r.IsPendingAccept);
        }

        [Fact]
        public void TheClosingContractStateTagsAllThreeClosingCauses()
        {
            GuiMockPayload payload = GuiMockCatalogue.ById("career.contracts.closing").Build();
            CareerStateWindowUI.ContractsTabVM tab = payload.Career.Value.Contracts;
            // Completed / failed / cancelled after live UT each carry their own outcome
            // on the CURRENT row - the classification is the ledger walk's, not this
            // test's - so a future failure never reads like a completion.
            Assert.Equal(3, tab.CurrentRows.Count(r => r.IsClosingByTimelineEnd));
            Assert.Equal(
                new[]
                {
                    CareerStateWindowUI.TimelineEndKind.Cancelled,
                    CareerStateWindowUI.TimelineEndKind.Completed,
                    CareerStateWindowUI.TimelineEndKind.Failed,
                },
                tab.CurrentRows.Select(r => r.EndKind).OrderBy(k => k.ToString()).ToArray());
            foreach (CareerStateWindowUI.ContractRow row in tab.CurrentRows)
                Assert.NotEqual("",
                    CareerStateWindowUI.FormatContractRow_TimelineEnd(row, ut => "D"));
        }

        [Fact]
        public void TheFacilityStatesReachEveryLevelAndStatusForm()
        {
            CareerStateWindowUI.FacilitiesTabVM upgraded =
                GuiMockCatalogue.ById("career.facilities.level-above-one")
                    .Build().Career.Value.Facilities;
            Assert.Contains(upgraded.Rows, r => r.CurrentLevel > 1);

            CareerStateWindowUI.FacilitiesTabVM upcoming =
                GuiMockCatalogue.ById("career.facilities.upcoming-upgrade")
                    .Build().Career.Value.Facilities;
            CareerStateWindowUI.FacilityRow pending =
                upcoming.Rows.First(r => r.HasUpcomingChange
                                         && r.CurrentLevel != r.ProjectedLevel);
            Assert.StartsWith("upgrades to L",
                CareerStateWindowUI.FormatFacilityRow_TimelineEnd(pending, true, ut => "D"),
                StringComparison.Ordinal);

            CareerStateWindowUI.FacilitiesTabVM destroyed =
                GuiMockCatalogue.ById("career.facilities.destroyed")
                    .Build().Career.Value.Facilities;
            Assert.Contains(destroyed.Rows,
                r => CareerStateWindowUI.FormatFacilityRow_Level(r).EndsWith(
                         "(destroyed)", StringComparison.Ordinal)
                     && r.ProjectedDestroyed);

            CareerStateWindowUI.FacilitiesTabVM repairPending =
                GuiMockCatalogue.ById("career.facilities.destroyed-repair-pending")
                    .Build().Career.Value.Facilities;
            Assert.Contains(repairPending.Rows,
                r => r.CurrentDestroyed
                     && CareerStateWindowUI.FormatFacilityRow_TimelineEnd(r, true, ut => "D")
                        .StartsWith("repaired", StringComparison.Ordinal));
        }

        [Fact]
        public void TheStrategyStateProducesAFlowCell()
        {
            CareerStateWindowUI.StrategiesTabVM tab =
                GuiMockCatalogue.ById("career.strategies.active-rows")
                    .Build().Career.Value.Strategies;
            Assert.NotEmpty(tab.CurrentRows);
            string flow = CareerStateWindowUI.FormatStrategyRow_Flow(tab.CurrentRows[0]);
            // The Flow column has no picture at all in the census; assert the real
            // formatter produced its three parts rather than pinning the whole string.
            Assert.Contains("->", flow, StringComparison.Ordinal);
            Assert.Contains("@", flow, StringComparison.Ordinal);
            Assert.Contains("%", flow, StringComparison.Ordinal);
        }

        // NOTE: the "every structure step label is in the shared vocabulary" cell that
        // used to live here is GONE, and its removal is the point rather than a loss. It
        // asserted that a step's Label came from MissionCompositionBuilder's branch-event
        // or terminal namer, which encoded the SAME misreading the catalogue had: the
        // terminal pass writes Label = "End" and puts the terminal word in STATUS, staging
        // rows read "Staged <part>" / "Fairing jettisoned", and route rows read "Dock" /
        // "Undock". A cell built on a wrong premise agreed with wrong data. The mechanical
        // replacement is GuiMockStructureBuilderFidelityTests, which asserts every state's
        // rows EQUAL real-builder output for its OWN inputs and fails on any string literal
        // assigned to a drawn StructureStep field - a property no vocabulary list can be
        // wrong about.

        [Fact]
        public void TheRouteStatesProduceTheRealStopLabels()
        {
            GuiMockStructure pickup =
                GuiMockCatalogue.ById("structure.route.pickup").Build().Structure;
            Assert.True(pickup.RouteMode);
            StructureStep pickupStep =
                pickup.Steps.Single(s => s.Kind == StructureStepKind.Delivery);
            Assert.StartsWith("Pick up", pickupStep.Label, StringComparison.Ordinal);

            GuiMockStructure mixed =
                GuiMockCatalogue.ById("structure.route.mixed").Build().Structure;
            StructureStep mixedStep =
                mixed.Steps.Single(s => s.Kind == StructureStepKind.Delivery);
            Assert.Contains(" / Pick up", mixedStep.Label, StringComparison.Ordinal);

            GuiMockStructure depot =
                GuiMockCatalogue.ById("structure.route.origin-depot").Build().Structure;
            StructureStep origin =
                depot.Steps.Single(s => s.Kind == StructureStepKind.Origin);
            Assert.Equal("Origin: depot", origin.Label);
            // The Origin pseudo-step has no single UT; the window renders NaN as "-".
            Assert.True(double.IsNaN(origin.UT));
        }

        [Fact]
        public void EveryNumberInABuiltPayloadIsInvariantCultureRendered()
        {
            // The catalogue builds the same strings on a ro-RO machine as on an en-US
            // one, because every formatter it reaches passes InvariantCulture. Pinning
            // de-DE here PROVES that rather than making a culture-dependent site pass.
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    CultureInfo.GetCultureInfo("de-DE");
                CareerStateWindowUI.StrategiesTabVM tab =
                    GuiMockCatalogue.ById("career.strategies.active-rows")
                        .Build().Career.Value.Strategies;
                string flow = CareerStateWindowUI.FormatStrategyRow_Flow(tab.CurrentRows[0]);
                Assert.DoesNotContain(",", flow, StringComparison.Ordinal);
                Assert.Contains(".", flow, StringComparison.Ordinal);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
