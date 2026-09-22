using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Parsek.UI.Gallery;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of <c>UiAction op=mock</c>: the op wiring, the intent parse, the
    /// refusal vocabulary, the label derivation and the draw-produced witness predicate.
    /// </summary>
    public class TestCommandUiMockTests
    {
        // ----- op wiring: every co-equal declaration site -----

        [Fact]
        public void TheOpTokenParsesRoundTripsAndIsInTheValidSet()
        {
            UiActionOp op;
            string reject;
            Assert.True(TestCommandUiAction.TryParseOp(
                TestCommandUiAction.MockOpToken, out op, out reject));
            Assert.Equal(UiActionOp.Mock, op);
            Assert.Null(reject);
            Assert.Equal(TestCommandUiAction.MockOpToken,
                         TestCommandUiAction.OpToken(UiActionOp.Mock));
            Assert.Contains(TestCommandUiAction.MockOpToken,
                            TestCommandUiAction.ValidOpNames.Split(','));
        }

        [Fact]
        public void TheOpIsTwoPhaseAndDeliberatelyOutsideTheWindowAndHostGateSets()
        {
            // Two-phase: the capture IS its read-back, so it holds the head for one.
            Assert.True(TestCommandUiAction.OpIsTwoPhase(UiActionOp.Mock));

            // NOT in OpNeedsWindow: the describe form names no window, so an
            // unconditional requirement would refuse it outright. The apply and clear
            // forms check the window in the applier instead.
            Assert.False(TestCommandUiAction.OpNeedsWindow(UiActionOp.Mock));

            // NOT in the host-showUI settle refusal: that exists for the two ops that
            // read back a FIELD. This one reads a captured TREE, in which an undrawn
            // window is simply absent - which the witness check already answers.
            Assert.False(TestCommandUiAction.SettleChecksHostShowUi(UiActionOp.Mock));
            Assert.False(TestCommandUiAction.OpRequiresWindowOpen(UiActionOp.Mock));
        }

        [Fact]
        public void TheParseIsCaseSensitiveAndFailClosed()
        {
            UiActionOp op;
            string reject;
            Assert.False(TestCommandUiAction.TryParseOp("Mock", out op, out reject));
            Assert.Equal(TestCommandUiAction.OpArgInvalidReason, reject);
            Assert.False(TestCommandUiAction.TryParseOp("mocks", out op, out reject));
            Assert.Equal(TestCommandUiAction.OpArgInvalidReason, reject);
        }

        // ----- the intent parse -----

        [Fact]
        public void NeitherArgIsArgMissing()
        {
            TestCommandUiMock.MockIntent intent;
            string reject;
            Assert.False(TestCommandUiMock.TryResolveIntent(null, null, out intent,
                                                            out reject));
            Assert.Equal(TestCommandUiMock.ArgMissingReason, reject);
            Assert.Equal(TestCommandUiMock.MockIntent.None, intent);
        }

        [Fact]
        public void AStateIdIsApplyAndTheSentinelIsClear()
        {
            TestCommandUiMock.MockIntent intent;
            string reject;
            Assert.True(TestCommandUiMock.TryResolveIntent(
                "kerbals.roster.lost", null, out intent, out reject));
            Assert.Equal(TestCommandUiMock.MockIntent.Apply, intent);

            Assert.True(TestCommandUiMock.TryResolveIntent(
                TestCommandUiMock.ClearToken, null, out intent, out reject));
            Assert.Equal(TestCommandUiMock.MockIntent.Clear, intent);
            Assert.Equal("none", TestCommandUiMock.ClearToken);
        }

        [Fact]
        public void DescribeIsItsOwnIntentAndNamingBothArgsIsRefused()
        {
            TestCommandUiMock.MockIntent intent;
            string reject;
            Assert.True(TestCommandUiMock.TryResolveIntent(null, "true", out intent,
                                                           out reject));
            Assert.Equal(TestCommandUiMock.MockIntent.Describe, intent);

            // describe=false is not describe, and with no mockState it is arg-missing:
            // the absent-arg default rather than a silent apply of nothing.
            Assert.False(TestCommandUiMock.TryResolveIntent(null, "false", out intent,
                                                            out reject));
            Assert.Equal(TestCommandUiMock.ArgMissingReason, reject);

            // Naming BOTH is refused rather than silently preferring one: they do
            // opposite things, and guessing is how a lane photographs the wrong thing.
            Assert.False(TestCommandUiMock.TryResolveIntent(
                "kerbals.roster.lost", "true", out intent, out reject));
            Assert.Equal(TestCommandUiMock.ArgMissingReason, reject);
        }

        [Fact]
        public void AnInvalidDescribeValueReusesTheSeamsOwnBooleanRefusal()
        {
            TestCommandUiMock.MockIntent intent;
            string reject;
            Assert.False(TestCommandUiMock.TryResolveIntent(null, "yes", out intent,
                                                            out reject));
            Assert.Equal(TestCommandUiState.StateArgInvalidReason, reject);
            // Case-sensitive, like every closed vocabulary in this seam.
            Assert.False(TestCommandUiMock.TryResolveIntent(null, "True", out intent,
                                                            out reject));
            Assert.Equal(TestCommandUiState.StateArgInvalidReason, reject);
        }

        // ----- the refusal vocabulary -----

        [Fact]
        public void EveryRefusalTokenIsKebabCaseDistinctAndInTheValidSet()
        {
            string[] tokens = TestCommandUiMock.ValidRefusalNames.Split(',');
            Assert.Equal(11, tokens.Length);
            Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
            foreach (string token in tokens)
            {
                Assert.StartsWith("mock-", token, StringComparison.Ordinal);
                Assert.Equal(token.ToLowerInvariant(), token);
                Assert.DoesNotContain("_", token, StringComparison.Ordinal);
            }
            // Each one is named, so a rename is a compile error here rather than a
            // silently changed wire token.
            foreach (string token in new[]
                     {
                         TestCommandUiMock.ArgMissingReason,
                         TestCommandUiMock.StateUnknownReason,
                         TestCommandUiMock.WindowUnsupportedReason,
                         TestCommandUiMock.StateWindowMismatchReason,
                         TestCommandUiMock.RefusedSceneReason,
                         TestCommandUiMock.RefusedRecordingReason,
                         TestCommandUiMock.RefusedSessionLiveReason,
                         TestCommandUiMock.RefusedModeReason,
                         TestCommandUiMock.NotAppliedReason,
                         TestCommandUiMock.RestoreFailedReason,
                         TestCommandUiMock.ScopeBrokenReason,
                     })
            {
                Assert.Contains(token, tokens);
            }
        }

        [Fact]
        public void TheSaveRefusalTokenIsDeclaredOnTheVerbThatAnswersIt()
        {
            Assert.Equal("save-refused-gui-mock", TestCommandSaveGame.RefusedGuiMockReason);
        }

        // ----- labels -----

        [Fact]
        public void TheLabelIsTheMirrorsGrammarWithMockAsTheHost()
        {
            Assert.Equal("mock-kerbals-roster-lost-advanced",
                TestCommandUiMock.DeriveLabel("mock", "kerbals.roster.lost", false));
            Assert.Equal("mock-career-banner-divergent-basic",
                TestCommandUiMock.DeriveLabel("mock", "career.banner.divergent", true));
            // The default host is what makes a mocked capture file under the mirror's
            // `mock` dataset rather than under a real fixture's name.
            Assert.Equal("mock", TestCommandUiMock.DefaultLabelPrefix);
            Assert.Equal(
                TestCommandUiMock.DeriveLabel("mock", "structure.route.pickup", false),
                TestCommandUiMock.DeriveLabel(null, "structure.route.pickup", false));
        }

        // ----- the draw-produced read-back -----

        [Fact]
        public void AnEmptyWitnessListIsNotApplied()
        {
            // The anti-vacuity direction: a payload that produced nothing drawable must
            // NOT read as applied, or mock-not-applied would be vacuous for that state.
            string missing;
            Assert.False(TestCommandUiMock.WitnessesDrawn(
                new string[0], new[] { "anything" }, out missing));
            Assert.Equal("(no witness)", missing);
            Assert.False(TestCommandUiMock.WitnessesDrawn(
                null, new[] { "anything" }, out missing));
        }

        [Fact]
        public void EveryWitnessMustBeDrawnAndTheMatchIsAContains()
        {
            string missing;
            // CONTAINS, because a fold header draws as "<arrow> " + HeaderText and a
            // collapsed structure label carries its own xN suffix; equality would fail
            // those on a perfectly good mock.
            Assert.True(TestCommandUiMock.WitnessesDrawn(
                new[] { "Jebediah Kerman - 2 missions" },
                new[] { "\u25b6 Jebediah Kerman - 2 missions: 1 recovered, 1 lost" },
                out missing));
            Assert.Null(missing);

            Assert.False(TestCommandUiMock.WitnessesDrawn(
                new[] { "Lost", "Reserved until recovery" },
                new[] { "Lost", "Available" }, out missing));
            Assert.Equal("Reserved until recovery", missing);

            Assert.False(TestCommandUiMock.WitnessesDrawn(
                new[] { "Lost" }, new string[0], out missing));
            Assert.Equal("Lost", missing);
            Assert.False(TestCommandUiMock.WitnessesDrawn(
                new[] { "Lost" }, null, out missing));
        }

        [Fact]
        public void TheWitnessDeriverRefusesPlaceholdersAndDuplicates()
        {
            // A witness of "-" would match half the cells in any window, so the deriver
            // drops the shared placeholder, anything below MinWitnessLength, and repeats.
            // The floor is TWO characters rather than three because the Facilities tab's
            // Level cell ("L3") is the shortest discriminating cell in the program.
            var vm = new KerbalsWindowUI.KerbalsViewModel
            {
                Roster = new KerbalsPresentation.RosterRowSet
                {
                    Involved = new List<KerbalsPresentation.RosterRow>
                    {
                        new KerbalsPresentation.RosterRow
                        {
                            StatusText = KerbalsPresentation.EmptyCell,
                            LastFlightText = "Mun Landing 1 - Lost",
                        },
                        new KerbalsPresentation.RosterRow
                        {
                            StatusText = "x",
                            LastFlightText = "Mun Landing 1 - Lost",
                        },
                    },
                    Plain = new List<KerbalsPresentation.RosterRow>(),
                },
            };
            List<string> witnesses = GuiMockWitness.Expected(
                new GuiMockPayload { Window = GuiMockSession.KerbalsWindow, Kerbals = vm },
                "roster", new string[0]);
            Assert.Equal(new[] { "Mun Landing 1 - Lost" }, witnesses);
            Assert.Equal(2, GuiMockWitness.MinWitnessLength);
        }

        [Fact]
        public void TheWitnessSetIsTabScopedOnATabbedWindow()
        {
            // A roster witness under the Flights tab would answer not-applied over a
            // perfectly good mock, which is why the STATE pins the tab.
            GuiMockState rosterState = GuiMockCatalogue.ById("kerbals.roster.lost");
            Assert.NotEmpty(GuiMockWitness.Expected(
                rosterState.Build(), "roster", rosterState.Covers));

            GuiMockState flightState =
                GuiMockCatalogue.ById("kerbals.flights.lost-outcome");
            List<string> underOutcomes = GuiMockWitness.Expected(
                flightState.Build(), "outcomes", flightState.Covers);
            Assert.NotEmpty(underOutcomes);
            // And the two halves really are different strings, so the scoping is not a
            // distinction without a difference.
            Assert.NotEqual(
                GuiMockWitness.Expected(flightState.Build(), "roster", flightState.Covers),
                underOutcomes);
        }

        // ----- payloads -----

        [Fact]
        public void TheApplyPayloadCarriesTheLabelTheBatchVerbWillNeed()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiMock.BuildApplyPayload(
                    "kerbals", "kerbals.roster.lost", 2, 3,
                    "mock-kerbals-roster-lost-advanced", false, 184122);
            var map = payload.ToDictionary(kv => kv.Key, kv => kv.Value,
                                           StringComparer.Ordinal);
            Assert.Equal("mock", map["op"]);
            Assert.Equal("kerbals", map["window"]);
            Assert.Equal("kerbals.roster.lost", map[TestCommandUiMock.MockStateArg]);
            Assert.Equal("true", map["applied"]);
            Assert.Equal("2", map["covers"]);
            Assert.Equal("3", map["witness"]);
            Assert.Equal("mock-kerbals-roster-lost-advanced", map["label"]);
            Assert.Equal("advanced", map["mode"]);
            Assert.Equal("184122", map["frame"]);
        }

        [Fact]
        public void TheClearPayloadUsesTheDashSentinelWhenNothingWasLive()
        {
            var map = TestCommandUiMock.BuildClearPayload(false, null, null)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            Assert.Equal("false", map["cleared"]);
            // A trailing `window=` on the wire reads as a truncated line, so the describe
            // payload's `-` rule applies here too.
            Assert.Equal("-", map["window"]);
            Assert.Equal("-", map["state"]);
        }

        [Fact]
        public void TheDescribePayloadNamesEveryStateAndTheSupportedSet()
        {
            var ids = new List<string> { "a.b.c", "d.e.f" };
            var map = TestCommandUiMock.BuildDescribePayload(
                    "gui-mock/1", ids, "kerbals,career", "kerbals,career,structure", null)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            Assert.Equal("gui-mock/1", map["catalogue"]);
            Assert.Equal("2", map["states"]);
            Assert.Equal("kerbals,career", map["windows"]);
            Assert.Equal("kerbals,career,structure", map["supported"]);
            Assert.Equal("-", map["live"]);
            Assert.Equal("a.b.c", map["s0"]);
            Assert.Equal("d.e.f", map["s1"]);
        }

        [Fact]
        public void TheCatalogueIdIsTheOneTheDumpBlockCarries()
        {
            Assert.Equal("gui-mock/1", GuiMockSession.CatalogueId);
        }
    }
}
