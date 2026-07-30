using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Parsek.Patches;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the R12 <c>SimulateStockSwitchClick</c> verb
    /// (<see cref="TestCommandSimulateSwitchClick"/>): arg parsing, target-selector
    /// resolution, the pre-click gate (including its ORDER), the terminal msg strings, the
    /// marker fields, and the payload.
    ///
    /// <para>There is no headless end-to-end verb driver in this suite by design - the addon
    /// is a Unity MonoBehaviour and is never instantiated here - so the Unity applier's own
    /// live behaviour (the <c>FlightGlobals.Vessels</c> sweep, <c>SetActiveVessel</c>, the
    /// Postfix-equivalent cleanup) is proven in-game / by a harness spec. Everything the
    /// applier DECIDES is factored into the pure core and covered below.</para>
    /// </summary>
    public class TestCommandSimulateSwitchClickTests
    {
        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // ----- site= parsing -----

        [Fact]
        public void ParseSite_Absent_DefaultsToMap()
        {
            // ABSENT means map: it is the only implemented site and the one every v1
            // consumer wants, and it keeps meaning map verbatim once ts / ksc land.
            Assert.True(TestCommandSimulateSwitchClick.TryParseSite(null, out SwitchClickSite site));
            Assert.Equal(SwitchClickSite.Map, site);
        }

        [Theory]
        [InlineData("map", "Map")]
        [InlineData("ts", "TrackingStation")]
        [InlineData("ksc", "Ksc")]
        public void ParseSite_AcceptsTheThreeWireSpellings(string raw, string expected)
        {
            Assert.True(TestCommandSimulateSwitchClick.TryParseSite(raw, out SwitchClickSite site));
            Assert.Equal(expected, site.ToString());
        }

        [Theory]
        [InlineData("")]            // present but empty: a template that failed to interpolate
        [InlineData("MAP")]         // case-sensitive, like LoadGame's scene= and RunTests' isolated=
        [InlineData("Map")]
        [InlineData("TS")]
        [InlineData(" map")]        // the codec preserves whitespace; a lenient trim would hide a typo
        [InlineData("map ")]
        [InlineData("trackstation")] // LoadGame's scene= spelling, NOT this verb's site= spelling
        [InlineData("spacecenter")]
        [InlineData("flight")]
        [InlineData("mapview")]
        public void ParseSite_RejectsAnythingElse(string raw)
        {
            // Fail-closed: a silently-defaulted site is the B10-shaped fail-open (the spec
            // believes it drove a TS Fly, the run drove a map click, the batch reads green).
            Assert.False(TestCommandSimulateSwitchClick.TryParseSite(raw, out _));
        }

        [Theory]
        [InlineData("Map", "map")]
        [InlineData("TrackingStation", "ts")]
        [InlineData("Ksc", "ksc")]
        public void SiteToken_RoundTripsThroughTryParseSite(string siteName, string expectedToken)
        {
            var site = (SwitchClickSite)Enum.Parse(typeof(SwitchClickSite), siteName);
            Assert.Equal(expectedToken, TestCommandSimulateSwitchClick.SiteToken(site));
            Assert.True(TestCommandSimulateSwitchClick.TryParseSite(expectedToken, out SwitchClickSite back));
            Assert.Equal(site, back);
        }

        [Fact]
        public void SiteImplemented_MapOnly()
        {
            Assert.True(TestCommandSimulateSwitchClick.IsSiteImplemented(SwitchClickSite.Map));
            Assert.False(TestCommandSimulateSwitchClick.IsSiteImplemented(SwitchClickSite.TrackingStation));
            Assert.False(TestCommandSimulateSwitchClick.IsSiteImplemented(SwitchClickSite.Ksc));
        }

        [Fact]
        public void SiteEnum_HasExactlyTheThreeStockArmingSites()
        {
            // One value per arming patch: MapFocusObjectOnSelectPatch,
            // SwitchIntentTrackingStationFlyPatch, KscVesselMarkerFlyPatch. A fourth value
            // without a fourth patch (or vice versa) is the drift this catches.
            Assert.Equal(3, Enum.GetValues(typeof(SwitchClickSite)).Length);
        }

        [Fact]
        public void SiteArgInvalidMsg_IsExact()
        {
            Assert.Equal("site-arg-invalid site=MAP", TestCommandSimulateSwitchClick.SiteArgInvalidMsg("MAP"));
            Assert.Equal("site-arg-invalid site=", TestCommandSimulateSwitchClick.SiteArgInvalidMsg(""));
            Assert.Equal("site-arg-invalid site=", TestCommandSimulateSwitchClick.SiteArgInvalidMsg(null));
        }

        [Fact]
        public void SiteNotImplementedMsg_IsExact()
        {
            Assert.Equal("site-not-implemented site=ts",
                TestCommandSimulateSwitchClick.SiteNotImplementedMsg(SwitchClickSite.TrackingStation));
            Assert.Equal("site-not-implemented site=ksc",
                TestCommandSimulateSwitchClick.SiteNotImplementedMsg(SwitchClickSite.Ksc));
        }

        // ----- vessel= / pid= selector -----

        [Fact]
        public void Selector_PidOnly_ResolvesByPid()
        {
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector(null, "123456");
            Assert.Null(d.Error);
            Assert.Equal(SwitchClickTargetSelector.ByPid, d.Selector);
            Assert.Equal(123456u, d.Pid);
            Assert.Null(d.Name);
        }

        [Fact]
        public void Selector_VesselOnly_ResolvesByName()
        {
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector("Kerbal X", null);
            Assert.Null(d.Error);
            Assert.Equal(SwitchClickTargetSelector.ByName, d.Selector);
            Assert.Equal("Kerbal X", d.Name);
            Assert.Equal(0u, d.Pid);
        }

        [Fact]
        public void Selector_Both_PidWins()
        {
            // pid is the unambiguous selector (KSP dedups persistentId among LIVE vessels,
            // names are freely duplicated), so a spec that supplies both gets the precise one
            // rather than a refusal it has to debug.
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector("Kerbal X", "77");
            Assert.Null(d.Error);
            Assert.Equal(SwitchClickTargetSelector.ByPid, d.Selector);
            Assert.Equal(77u, d.Pid);
        }

        [Fact]
        public void Selector_Neither_IsTargetArgMissing()
        {
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector(null, null);
            Assert.Equal(SwitchClickTargetSelector.None, d.Selector);
            Assert.Equal("target-arg-missing", d.Error);
        }

        [Theory]
        [InlineData("")]            // present but empty
        [InlineData("abc")]
        [InlineData("-1")]          // pids are uint; a negative is not "the last vessel"
        [InlineData("1.0")]
        [InlineData("0x10")]
        [InlineData("4294967296")]  // uint.MaxValue + 1
        public void Selector_UnparseablePid_IsPidArgInvalid(string raw)
        {
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector(null, raw);
            Assert.Equal(SwitchClickTargetSelector.None, d.Selector);
            Assert.Equal("pid-arg-invalid pid=" + raw, d.Error);
        }

        [Fact]
        public void Selector_UnparseablePid_DoesNotFallBackToVessel()
        {
            // A pid= that failed to interpolate must not silently retarget by name: the two
            // args address different things and the spec asked for one of them.
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector("Kerbal X", "");
            Assert.Equal("pid-arg-invalid pid=", d.Error);
        }

        [Fact]
        public void Selector_EmptyVessel_IsVesselArgInvalid()
        {
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector("", null);
            Assert.Equal(SwitchClickTargetSelector.None, d.Selector);
            Assert.Equal("vessel-arg-invalid vessel=", d.Error);
        }

        [Theory]
        [InlineData(" 12")]
        [InlineData("12 ")]
        public void Selector_Pid_ToleratesSurroundingWhitespace(string raw)
        {
            // NumberStyles.Integer (AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign)
            // is the seam's ONE numeric-arg parse style - EvaBoard's targetPid uses exactly
            // it - and one style across the seam is worth more than marginal strictness here.
            // Unlike a mis-spelled site=, stray whitespace cannot mis-TARGET anything: it
            // yields the same pid or no pid at all.
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector(null, raw);
            Assert.Null(d.Error);
            Assert.Equal(12u, d.Pid);
        }

        [Fact]
        public void Selector_Pid_ParsesMaxValue()
        {
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector(null, "4294967295");
            Assert.Null(d.Error);
            Assert.Equal(uint.MaxValue, d.Pid);
        }

        [Theory]
        [InlineData("Kerbal X Debris")]
        [InlineData("Station Alpha (Probe)")]
        [InlineData("Munar Lander 2")]
        public void Selector_NameKeepsPunctuationAndSpacesVerbatim(string name)
        {
            // The percent codec round-trips these losslessly on the wire, and resolution is
            // an EXACT ordinal match, so no normalisation may happen here.
            var d = TestCommandSimulateSwitchClick.DecideTargetSelector(name, null);
            Assert.Equal(name, d.Name);
        }

        [Fact]
        public void SelectorToken_RendersEachForm()
        {
            Assert.Equal("pid=42", TestCommandSimulateSwitchClick.SelectorToken(
                TestCommandSimulateSwitchClick.DecideTargetSelector(null, "42")));
            Assert.Equal("vessel=Kerbal X", TestCommandSimulateSwitchClick.SelectorToken(
                TestCommandSimulateSwitchClick.DecideTargetSelector("Kerbal X", null)));
            Assert.Equal("target=<none>", TestCommandSimulateSwitchClick.SelectorToken(
                TestCommandSimulateSwitchClick.DecideTargetSelector(null, null)));
        }

        // ----- target resolution -----

        [Theory]
        [InlineData(1, 0, "Resolved")]
        [InlineData(1, 3, "Resolved")]   // a ghost of the same craft alongside the real one
        [InlineData(2, 0, "Ambiguous")]
        [InlineData(5, 2, "Ambiguous")]
        [InlineData(0, 1, "GhostOnly")]
        [InlineData(0, 4, "GhostOnly")]
        [InlineData(0, 0, "NotFound")]
        public void ClassifyResolution_Matrix(int liveMatches, int ghostMatches, string expected)
        {
            Assert.Equal(expected,
                TestCommandSimulateSwitchClick.ClassifyResolution(liveMatches, ghostMatches).ToString());
        }

        [Fact]
        public void ResolutionMsg_NotFound_NamesTheSelector()
        {
            var byPid = TestCommandSimulateSwitchClick.DecideTargetSelector(null, "99");
            Assert.Equal("target-not-found pid=99",
                TestCommandSimulateSwitchClick.ResolutionMsg(SwitchClickTargetResolution.NotFound, byPid, 0));

            var byName = TestCommandSimulateSwitchClick.DecideTargetSelector("Kerbal X", null);
            Assert.Equal("target-not-found vessel=Kerbal X",
                TestCommandSimulateSwitchClick.ResolutionMsg(SwitchClickTargetResolution.NotFound, byName, 0));
        }

        [Fact]
        public void ResolutionMsg_Ambiguous_ReportsTheMatchCount()
        {
            // Vessel names are not unique (two "Debris", two launches of one craft), so the
            // count is what tells a spec author whether to switch to pid=.
            var byName = TestCommandSimulateSwitchClick.DecideTargetSelector("Debris", null);
            Assert.Equal("target-name-ambiguous vessel=Debris matches=3",
                TestCommandSimulateSwitchClick.ResolutionMsg(SwitchClickTargetResolution.Ambiguous, byName, 3));
        }

        [Fact]
        public void ResolutionMsg_GhostOnly_IsItsOwnReason()
        {
            // A Parsek ghost is a real entry in FlightGlobals.Vessels; collapsing it into
            // target-not-found would send a spec author hunting a vessel that IS there.
            var byPid = TestCommandSimulateSwitchClick.DecideTargetSelector(null, "5150");
            Assert.Equal("target-is-ghost pid=5150",
                TestCommandSimulateSwitchClick.ResolutionMsg(SwitchClickTargetResolution.GhostOnly, byPid, 0));
        }

        [Fact]
        public void ResolutionMsg_Resolved_HasNoRefusal()
        {
            var byPid = TestCommandSimulateSwitchClick.DecideTargetSelector(null, "5");
            Assert.Null(TestCommandSimulateSwitchClick.ResolutionMsg(
                SwitchClickTargetResolution.Resolved, byPid, 1));
        }

        // ----- the pre-click gate -----

        private const MapFocusObjectOnSelectPatch.PreSwitchDialogDecision NoSession =
            MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.NoPriorSession;
        private const MapFocusObjectOnSelectPatch.PreSwitchDialogDecision OpenDialog =
            MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.OpenDialog;
        private const MapFocusObjectOnSelectPatch.PreSwitchDialogDecision SameTarget =
            MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.SkipDialogSameTarget;
        private const MapFocusObjectOnSelectPatch.PreSwitchDialogDecision ReEntry =
            MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.SkipDialogReEntry;

        [Fact]
        public void Gate_CleanPlainPath_Proceeds()
        {
            Assert.Equal(SwitchClickGateDecision.Proceed,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: NoSession, targetIsLoaded: true));
        }

        [Fact]
        public void Gate_SameTargetReClick_Proceeds_MatchingThePatchFilter()
        {
            // The Prefix's SkipDialogSameTarget branch falls THROUGH to the plain
            // arm-and-switch flow (no dialog); the consume site answers it with
            // duplicate-intent-same-target. Matching that here keeps the simulated click
            // and the real click on the same path. (In practice the already-active check
            // usually pre-empts this, because the armed session's focused vessel normally IS
            // the active one - but a session whose focused pid is not the active vessel
            // reaches it.)
            Assert.Equal(SwitchClickGateDecision.Proceed,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: SameTarget, targetIsLoaded: true));
        }

        [Fact]
        public void Gate_ScenarioNull_RefusesFirst()
        {
            // Wins over EVERY other refusal: with no scenario nothing can hold the marker,
            // so no further question is meaningful.
            Assert.Equal(SwitchClickGateDecision.RefusedScenarioNotReady,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: false, canSwitchVesselsFar: false, targetIsActiveVessel: true,
                    dialogDecision: OpenDialog, targetIsLoaded: false));
        }

        [Fact]
        public void Gate_CannotSwitchVesselsFar_Refuses()
        {
            // The Prefix's gate 5: stock would not arm at all, so a simulated click that
            // armed anyway would certify a path the player cannot reach.
            Assert.Equal(SwitchClickGateDecision.RefusedCannotSwitchVesselsFar,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: false, targetIsActiveVessel: false,
                    dialogDecision: NoSession, targetIsLoaded: true));
        }

        [Fact]
        public void Gate_CannotSwitchVesselsFar_BeatsEveryLaterRefusal()
        {
            Assert.Equal(SwitchClickGateDecision.RefusedCannotSwitchVesselsFar,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: false, targetIsActiveVessel: true,
                    dialogDecision: OpenDialog, targetIsLoaded: false));
        }

        [Fact]
        public void Gate_TargetAlreadyActive_Refuses()
        {
            // SetActiveVessel early-returns false on an already-active vessel, so the verb
            // would be a no-op reporting OK - the fail-open shape this verb exists to close.
            Assert.Equal(SwitchClickGateDecision.RefusedTargetAlreadyActive,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: true,
                    dialogDecision: NoSession, targetIsLoaded: true));
        }

        [Fact]
        public void Gate_TargetAlreadyActive_BeatsTheDialogDecision()
        {
            // Deliberate ordering: a switch that cannot move the active vessel is a no-op
            // whatever the dialog machinery would have said about it.
            Assert.Equal(SwitchClickGateDecision.RefusedTargetAlreadyActive,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: true,
                    dialogDecision: OpenDialog, targetIsLoaded: true));
        }

        [Fact]
        public void Gate_OpenDialog_RefusesDialogRequired()
        {
            Assert.Equal(SwitchClickGateDecision.RefusedDialogRequired,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: OpenDialog, targetIsLoaded: true));
        }

        [Fact]
        public void Gate_OpenDialog_BeatsTheUnloadedScopeRefusal()
        {
            // An unloaded target WITH a live recording is Case B, and the design requires
            // the refusal to NAME the case rather than collapse to the generic v1 scope
            // refusal - which is exactly why the dialog check precedes the loaded check.
            Assert.Equal(SwitchClickGateDecision.RefusedDialogRequired,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: OpenDialog, targetIsLoaded: false));
        }

        [Fact]
        public void Gate_ReEntry_RefusesDialogPending()
        {
            // The Prefix falls through to the plain path when another merge dialog is open
            // (a player who clicked anyway, under a modal they can see). A DRIVEN run must
            // not: the modal holds a ControlTypes.All input lock, and no seam verb can
            // answer a plain whole-tree popup.
            Assert.Equal(SwitchClickGateDecision.RefusedDialogPending,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: ReEntry, targetIsLoaded: true));
        }

        [Fact]
        public void Gate_UnloadedTarget_RefusesLast()
        {
            // v1 is in-bubble only: an unloaded target sends stock through
            // FlightDriver.StartAndFocusVessel - a FLIGHT scene reload the 2 s marker TTL
            // cannot survive, and which by DESIGN starts no segment.
            Assert.Equal(SwitchClickGateDecision.RefusedTargetUnloaded,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: NoSession, targetIsLoaded: false));
        }

        [Fact]
        public void Gate_UnloadedTarget_RefusesUnderSameTargetToo()
        {
            Assert.Equal(SwitchClickGateDecision.RefusedTargetUnloaded,
                TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: SameTarget, targetIsLoaded: false));
        }

        [Fact]
        public void Gate_EveryDialogDecisionValueIsHandled()
        {
            // Exhaustive over the predicate's enum: two pass through, two refuse. A new
            // PreSwitchDialogDecision member added to the patch reds here rather than
            // silently falling into Proceed.
            var expected = new Dictionary<MapFocusObjectOnSelectPatch.PreSwitchDialogDecision, SwitchClickGateDecision>
            {
                [NoSession] = SwitchClickGateDecision.Proceed,
                [SameTarget] = SwitchClickGateDecision.Proceed,
                [OpenDialog] = SwitchClickGateDecision.RefusedDialogRequired,
                [ReEntry] = SwitchClickGateDecision.RefusedDialogPending,
            };
            var all = Enum.GetValues(typeof(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision));
            Assert.Equal(expected.Count, all.Length);
            foreach (MapFocusObjectOnSelectPatch.PreSwitchDialogDecision d in all)
            {
                Assert.Equal(expected[d], TestCommandSimulateSwitchClick.DecideSwitchGate(
                    scenarioReady: true, canSwitchVesselsFar: true, targetIsActiveVessel: false,
                    dialogDecision: d, targetIsLoaded: true));
            }
        }

        // ----- dialog case token + refusal msgs -----

        [Theory]
        [InlineData(true, false, "A-session")]
        [InlineData(true, true, "A-session")]   // a session always outranks the no-session sub-cases
        [InlineData(false, false, "B-unloaded")]
        [InlineData(false, true, "C-loaded-separate-committed")]
        public void DialogCaseToken_MirrorsThePrefixOpenCaseString(
            bool hasActiveSession, bool targetIsSeparateCommittedVessel, string expected)
        {
            // Byte-identical to MapFocusObjectOnSelectPatch's own `openCase` log token, so a
            // seam refusal and the dialog it predicted grep as one story.
            Assert.Equal(expected, TestCommandSimulateSwitchClick.DialogCaseToken(
                hasActiveSession, targetIsSeparateCommittedVessel));
        }

        [Theory]
        [InlineData("RefusedScenarioNotReady", "scenario-not-ready")]
        [InlineData("RefusedCannotSwitchVesselsFar", "cannot-switch-vessels-far")]
        [InlineData("RefusedTargetAlreadyActive", "target-already-active")]
        [InlineData("RefusedDialogPending", "dialog-pending")]
        [InlineData("RefusedTargetUnloaded", "target-unloaded")]
        public void GateRefusalMsg_IsExact(string decisionName, string expected)
        {
            var decision = (SwitchClickGateDecision)Enum.Parse(typeof(SwitchClickGateDecision), decisionName);
            Assert.Equal(expected, TestCommandSimulateSwitchClick.GateRefusalMsg(decision, "A-session"));
        }

        [Theory]
        [InlineData("A-session")]
        [InlineData("B-unloaded")]
        [InlineData("C-loaded-separate-committed")]
        public void GateRefusalMsg_DialogRequired_CarriesTheCase(string caseToken)
        {
            Assert.Equal("dialog-required case=" + caseToken,
                TestCommandSimulateSwitchClick.GateRefusalMsg(
                    SwitchClickGateDecision.RefusedDialogRequired, caseToken));
        }

        [Fact]
        public void GateRefusalMsg_Proceed_IsEmpty()
        {
            Assert.Equal(string.Empty,
                TestCommandSimulateSwitchClick.GateRefusalMsg(SwitchClickGateDecision.Proceed, "A-session"));
        }

        [Fact]
        public void GateRefusalMsg_EveryRefusalHasANonEmptyMsg()
        {
            // A new SwitchClickGateDecision member that forgot its msg would otherwise emit
            // REJECTED with an empty msg - a terminal a spec cannot match on.
            foreach (SwitchClickGateDecision d in Enum.GetValues(typeof(SwitchClickGateDecision)))
            {
                if (d == SwitchClickGateDecision.Proceed) continue;
                Assert.False(string.IsNullOrEmpty(
                    TestCommandSimulateSwitchClick.GateRefusalMsg(d, "A-session")));
            }
        }

        // ----- marker fidelity -----

        [Fact]
        public void Marker_CarriesTheMapSiteFieldsVerbatim()
        {
            // Field-for-field what MapFocusObjectOnSelectPatch builds at BOTH of its arm
            // sites (the Prefix's own construction and ArmIntentAndSwitchTo): the two
            // constants are fixed by the factory, the four sampled values pass through.
            Guid intentId = Guid.NewGuid();
            Guid processId = Guid.NewGuid();
            StockActionIntentMarker m = TestCommandSimulateSwitchClick.BuildMapSwitchToMarker(
                intentId, 4242u, 12.5f, 987.25, processId);

            Assert.Equal(intentId, m.IntentId);
            Assert.Equal(StockActionType.MapSwitchTo, m.Action);
            Assert.Equal(4242u, m.TargetVesselPersistentId);
            Assert.Equal(StockActionSourceScene.Flight, m.SourceScene);
            Assert.Equal(12.5f, m.CapturedRealtime);
            Assert.Equal(987.25, m.CapturedUT);
            Assert.Equal(processId, m.ProcessSessionId);
        }

        [Fact]
        public void Marker_TtlIsTheMapSwitchToTwoSeconds_AndIsNotAnInput()
        {
            // The TTL is DERIVED from Action, never passed in. A seam verb that lengthened it
            // would certify a marker lifetime no player click can produce - the exact
            // "certifies the wrong code path" failure this verb exists to close.
            StockActionIntentMarker m = TestCommandSimulateSwitchClick.BuildMapSwitchToMarker(
                Guid.NewGuid(), 1u, 0f, 0.0, Guid.NewGuid());

            MethodInfo ttl = typeof(StockActionIntentMarker).GetMethod(
                "GetTtlSeconds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(ttl);
            Assert.Equal(2.0, (double)ttl.Invoke(null, new object[] { m.Action }));
            Assert.Equal(2.0, StockActionIntentMarker.MapSwitchToTtlSeconds);
        }

        [Fact]
        public void Marker_FreshAtArm_ThenStaleOncePastTheTtl()
        {
            // The consume site re-evaluates freshness through EvaluateStaleness on every
            // path, and the verb never bypasses it. Same-process, same UT: fresh at arm,
            // TTL-expired 2.1 s later.
            Guid processId = Guid.NewGuid();
            StockActionIntentMarker m = TestCommandSimulateSwitchClick.BuildMapSwitchToMarker(
                Guid.NewGuid(), 7u, 100f, 500.0, processId);

            Assert.Equal(StockActionIntentStaleness.Fresh,
                StockActionIntentMarker.EvaluateStaleness(m, processId, 100f, 500.0));
            Assert.Equal(StockActionIntentStaleness.StaleIntentTtlExpired,
                StockActionIntentMarker.EvaluateStaleness(m, processId, 102.1f, 500.0));
            Assert.Equal(StockActionIntentStaleness.StaleCrossRun,
                StockActionIntentMarker.EvaluateStaleness(m, Guid.NewGuid(), 100f, 500.0));
        }

        [Fact]
        public void Marker_FieldSetIsUnchanged_SoTheFactoryCannotSilentlyMissOne()
        {
            // Drift guard: the factory sets all seven fields explicitly. An eighth field
            // added to StockActionIntentMarker would be left at its default by the factory -
            // silently, since C# object initialisers do not require exhaustiveness - so it
            // must red here and be routed through the factory deliberately.
            string[] fields = typeof(StockActionIntentMarker)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[]
                {
                    "Action", "CapturedRealtime", "CapturedUT", "IntentId", "ProcessSessionId",
                    "SourceScene", "TargetVesselPersistentId",
                },
                fields);
        }

        // ----- payload -----

        [Fact]
        public void Payload_KeyOrderIsStable()
        {
            var p = TestCommandSimulateSwitchClick.BuildPayload(
                "map", 10u, "Kerbal X", Guid.Empty, "plain-arm-and-switch", 10u, true);
            Assert.Equal(
                new[] { "site", "targetPid", "targetName", "intentId", "route", "activeVesselPid", "switched" },
                p.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void Payload_ReportsArmedAndObservedFacts()
        {
            Guid intentId = Guid.Parse("11112222-3333-4444-5555-666677778888");
            var p = TestCommandSimulateSwitchClick.BuildPayload(
                "map", 1234u, "Munar Lander 2", intentId, "plain-arm-and-switch", 1234u, true);

            Assert.Equal("map", Val(p, "site"));
            Assert.Equal("1234", Val(p, "targetPid"));
            Assert.Equal("Munar Lander 2", Val(p, "targetName"));
            Assert.Equal("11112222-3333-4444-5555-666677778888", Val(p, "intentId"));
            Assert.Equal("plain-arm-and-switch", Val(p, "route"));
            Assert.Equal("1234", Val(p, "activeVesselPid"));
            Assert.Equal("true", Val(p, "switched"));
        }

        [Fact]
        public void Payload_SwitchedFalse_IsWhatMakesARefusedSwitchRed()
        {
            // The observed half exists because stock SetActiveVessel has six documented ways
            // to return false without switching; a payload that only reported "we armed and
            // called" would be a commanded-not-observed claim.
            var p = TestCommandSimulateSwitchClick.BuildPayload(
                "map", 1234u, "Munar Lander 2", Guid.NewGuid(), "plain-arm-and-switch", 99u, false);
            Assert.Equal("false", Val(p, "switched"));
            Assert.Equal("99", Val(p, "activeVesselPid"));
        }

        [Fact]
        public void Payload_NullsRenderAsEmptyStrings()
        {
            var p = TestCommandSimulateSwitchClick.BuildPayload(
                null, 0u, null, Guid.Empty, null, 0u, false);
            Assert.Equal(string.Empty, Val(p, "site"));
            Assert.Equal(string.Empty, Val(p, "targetName"));
            Assert.Equal(string.Empty, Val(p, "route"));
            Assert.Equal("0", Val(p, "targetPid"));
            Assert.Equal("00000000-0000-0000-0000-000000000000", Val(p, "intentId"));
        }

        [Fact]
        public void Payload_SurvivesTheWireEncoding()
        {
            // A vessel name with a space and an '=' must round-trip through the response
            // formatter's percent codec rather than splitting the envelope.
            var p = TestCommandSimulateSwitchClick.BuildPayload(
                "map", 7u, "Probe A=B", Guid.Empty, "plain-arm-and-switch", 7u, true);
            string line = TestCommandResponse.FormatResponseLine(
                "0007", "SimulateStockSwitchClick", "OK", 7, null, p, null);
            Assert.Contains("targetName=Probe%20A%3DB", line);
            Assert.Contains("switched=true", line);
        }

        [Fact]
        public void RefusalMsgs_SurviveTheWireEncoding()
        {
            string line = TestCommandResponse.FormatResponseLine(
                "0008", "SimulateStockSwitchClick", "REJECTED", 8, null, null,
                TestCommandSimulateSwitchClick.GateRefusalMsg(
                    SwitchClickGateDecision.RefusedDialogRequired, "B-unloaded"));
            Assert.Contains("msg=dialog-required%20case%3DB-unloaded", line);
        }

        [Fact]
        public void PlainRoute_IsTheOnlyV1Route()
        {
            Assert.Equal("plain-arm-and-switch", TestCommandSimulateSwitchClick.PlainRoute);
        }

        [Fact]
        public void ClearReason_MatchesThePatchPostfix()
        {
            // The verb has no Harmony Postfix, so it performs the patch's cleanup itself; the
            // reason string must stay byte-identical so one grep finds both.
            Assert.Equal("refused-no-switch", TestCommandSimulateSwitchClick.RefusedNoSwitchClearReason);
        }
    }
}
