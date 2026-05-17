using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Smoke tests for the Phase B.2 stock-action-intent Harmony patches. The
    /// full end-to-end behavior is exercised by in-game tests in
    /// <c>RuntimeTests.cs</c>; these xUnit smoke tests pin that the patch types
    /// load cleanly, that <c>[HarmonyPatch]</c> is wired, and that the gate
    /// predicates fall the way the plan requires. KSP's
    /// <c>FlightGlobals.SetActiveVessel</c> / live Harmony patching itself are
    /// covered only by in-game tests because they require Unity + KSP runtime
    /// state we cannot stand up under xUnit.
    /// </summary>
    public class SwitchIntentPatchSmokeTests
    {
        [Fact]
        public void TrackingStationFlyPatch_HasHarmonyPatchAttribute()
        {
            // Fails if: KSP renames or removes SpaceTracking.FlyVessel, the
            // [HarmonyPatch] attribute on SwitchIntentTrackingStationFlyPatch is
            // dropped, or the patch class is removed.
            var attrs = typeof(SwitchIntentTrackingStationFlyPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false);
            Assert.NotEmpty(attrs);

            var spaceTrackingType = typeof(KSP.UI.Screens.SpaceTracking);
            MethodInfo flyVessel = spaceTrackingType.GetMethod(
                "FlyVessel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(flyVessel);
        }

        [Fact]
        public void KscVesselMarkerFlyPatch_HasHarmonyPatchAttribute()
        {
            // Fails if: KSP renames or removes KSCVesselMarkers.FlyVessel(Vessel)
            // (the non-public handler the patch arms its intent on), or the
            // [HarmonyPatch] attribute is dropped.
            var attrs = typeof(KscVesselMarkerFlyPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false);
            Assert.NotEmpty(attrs);

            var kscType = typeof(KSP.UI.Screens.KSCVesselMarkers);
            MethodInfo flyVessel = kscType.GetMethod(
                "FlyVessel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Vessel) },
                modifiers: null);
            Assert.NotNull(flyVessel);
        }

        [Fact]
        public void MapFocusObjectOnSelectPatch_TargetMethodResolves()
        {
            // Fails if: KSP renames or moves
            // KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject.OnSelect
            // or its containing namespace, so the patch can no longer find its
            // target via reflection.
            var attrs = typeof(MapFocusObjectOnSelectPatch)
                .GetCustomAttributes(typeof(HarmonyPatch), inherit: false);
            Assert.NotEmpty(attrs);

            // Invoke the patch's static TargetMethod() helper directly.
            MethodInfo helper = typeof(MapFocusObjectOnSelectPatch).GetMethod(
                "TargetMethod",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(helper);
            var resolved = helper.Invoke(null, null) as MethodBase;
            Assert.NotNull(resolved);
            Assert.Equal("OnSelect", resolved.Name);
            Assert.Equal(
                typeof(KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject),
                resolved.DeclaringType);
        }

        [Theory]
        // isOwnedVesselMode, canSwitchVesselsFar, vesselNotNull, expected
        [InlineData(true, true, true, true)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, false, false)]
        [InlineData(false, false, false, false)]
        public void MapFocusObjectOnSelectPatch_ShouldArm_GateMatrix(
            bool isOwnedVesselMode,
            bool canSwitchVesselsFar,
            bool vesselNotNull,
            bool expected)
        {
            // Fails if: ShouldArmMapSwitchTo changes its truth table and one of
            // the three gates (FocusMode / CanSwitchVesselsFar / vessel) stops
            // being load-bearing for the arm decision.
            bool actual = MapFocusObjectOnSelectPatch.ShouldArmMapSwitchTo(
                isOwnedVesselMode, canSwitchVesselsFar, vesselNotNull);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_NoSession_ReturnsNoPriorSession()
        {
            // Fails if: the pre-switch decision helper opens a dialog when
            // no SwitchSegmentSession is armed and no Case B (active
            // recording + unloaded target) preconditions hold. The
            // regular arm-and-skip flow must run unchanged in that
            // case (rapid-switch interception only triggers when
            // there's a prior session or a pending in-flight recording
            // heading into a scene reload).
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: false,
                priorFocusedPid: 0u,
                newTargetPid: 1234u,
                anotherDialogOpen: false,
                hasActiveRecording: false,
                targetIsUnloaded: false);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.NoPriorSession, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_DifferentTarget_OpensDialog()
        {
            // Fails if: a Switch-To to a different vessel while a prior
            // session is armed does NOT open the pre-switch dialog. This
            // is the rapid-switch case the dialog is for - Bug A/B from
            // logs/2026-05-17_1805_switch-fly-post-scene-discard-bug.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: true,
                priorFocusedPid: 100u,
                newTargetPid: 200u,
                anotherDialogOpen: false,
                hasActiveRecording: false,
                targetIsUnloaded: false);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.OpenDialog, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_SameTarget_SkipsDialog()
        {
            // Fails if: a duplicate Switch-To on the same vessel as the
            // active session opens a redundant dialog. The consume
            // helper's `duplicate-intent-same-target` branch already
            // handles same-target clicks; opening a dialog would be
            // confusing UX.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: true,
                priorFocusedPid: 200u,
                newTargetPid: 200u,
                anotherDialogOpen: false,
                hasActiveRecording: false,
                targetIsUnloaded: false);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.SkipDialogSameTarget, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_AnotherDialogOpen_SkipsDialogReEntry()
        {
            // Fails if: a Switch-To click while another merge dialog is
            // already open spawns a second dialog on top. The re-entry
            // guard must defer to the existing dialog so the player
            // resolves it first.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: true,
                priorFocusedPid: 100u,
                newTargetPid: 200u,
                anotherDialogOpen: true,
                hasActiveRecording: false,
                targetIsUnloaded: false);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.SkipDialogReEntry, actual);
        }

        // -----------------------------------------------------------------
        // Case B (no-session active-recording + unloaded target) tests.
        // The Map Switch-To Prefix extends the predicate to fire the
        // pre-switch dialog when no SwitchSegmentSession is armed but
        // a recording is in flight AND the target is unloaded (out-of-
        // bubble). Stock's FlightDriver.StartAndFocusVessel would
        // otherwise scene-reload silently and bypass the SceneExit
        // Esc-to-Space-Center dialog filter (FLIGHT→FLIGHT skip).
        // -----------------------------------------------------------------

        [Fact]
        public void DecidePreSwitchDialogAction_NoSession_NoActiveRecording_ReturnsNoPriorSession()
        {
            // Fails if: predicate spuriously opens a dialog when there's
            // nothing to ask about (no session AND no active recording).
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: false,
                priorFocusedPid: 0u,
                newTargetPid: 1234u,
                anotherDialogOpen: false,
                hasActiveRecording: false,
                targetIsUnloaded: true);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.NoPriorSession, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_NoSession_ActiveRecording_LoadedTarget_ReturnsNoPriorSession()
        {
            // Fails if: predicate opens a dialog for in-bubble switches,
            // breaking the existing in-FLIGHT auto-record-on-switch
            // flow. The in-bubble path stays "no dialog, prior recording
            // continues in BG, new vessel auto-records or gets the
            // first-modification watcher".
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: false,
                priorFocusedPid: 0u,
                newTargetPid: 1234u,
                anotherDialogOpen: false,
                hasActiveRecording: true,
                targetIsUnloaded: false);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.NoPriorSession, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_NoSession_ActiveRecording_UnloadedTarget_OpensDialog()
        {
            // Fails if: predicate fails to fire for out-of-bubble
            // Switch-To with an active recording — the user's chosen
            // UX gap. Stock would silently scene-reload via
            // FlightDriver.StartAndFocusVessel; the dialog forces the
            // player to commit to Merge or Discard before the reload.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: false,
                priorFocusedPid: 0u,
                newTargetPid: 1234u,
                anotherDialogOpen: false,
                hasActiveRecording: true,
                targetIsUnloaded: true);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.OpenDialog, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_NoSession_ActiveRecording_UnloadedTarget_AnotherDialogOpen_SkipsReEntry()
        {
            // Fails if: re-entry guard doesn't apply to the new Case B,
            // letting a second dialog spawn over the first. The existing
            // dialog must be resolved first regardless of which case
            // triggered it.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: false,
                priorFocusedPid: 0u,
                newTargetPid: 1234u,
                anotherDialogOpen: true,
                hasActiveRecording: true,
                targetIsUnloaded: true);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.SkipDialogReEntry, actual);
        }

        [Fact]
        public void DecidePreSwitchDialogAction_SessionActive_ActiveRecording_UnloadedTarget_StillSessionPath()
        {
            // Fails if: a future refactor merges the two cases and breaks
            // the session-first priority. When both Case A (session) and
            // Case B (active recording + unloaded) inputs are set, the
            // session-armed handler MUST take precedence — Case A keeps
            // its scoped-discard / ClearSwitchSegmentSession bookkeeping
            // that Case B doesn't have.
            var actual = MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                hasActiveSession: true,
                priorFocusedPid: 100u,
                newTargetPid: 200u,
                anotherDialogOpen: false,
                hasActiveRecording: true,
                targetIsUnloaded: true);
            Assert.Equal(MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.OpenDialog, actual);
        }

        // -----------------------------------------------------------------
        // Bug B (post-#876 playtest 2026-05-17): the pre-switch Merge
        // handler must clear the prior SwitchSegmentSession marker
        // synchronously after CommitTreeFlight succeeds, not lean on
        // the defensive `superseded-by-new-switch` branch in
        // ParsekFlight.TryConsumeStockActionIntent. Without the
        // explicit clear, the marker survived OnSave / OnLoad and the
        // next switch only collected it through the defensive fallback.
        //
        // The Merge / Discard button handlers spawn Unity dialog
        // callbacks (MergeDialog.ShowPreSwitchDecisionDialog) and the
        // CommitTreeFlight pathway touches the live recorder, so a
        // direct xUnit runtime assertion against MergePriorAndSwitchTo
        // is not feasible without standing up KSP. The source-text
        // gates below pin the load-bearing shape: (a) the clear call
        // appears, (b) it carries a non-empty reason, and (c) it
        // precedes ArmIntentAndSwitchTo. Discard is covered by a
        // separate gate confirming the implicit clear via
        // TryDiscardActiveSwitchSegmentAttempt's scoped-discard path.
        // -----------------------------------------------------------------

        private static string ReadMapFocusObjectPatchSource()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "Patches", "MapFocusObjectOnSelectPatch.cs");
            return File.ReadAllText(path);
        }

        // Fails if: a future refactor drops the explicit clear in the
        // Merge handler, regressing to the defensive-supersede backstop
        // and leaking the prior session across save/load round-trips.
        [Fact]
        public void MergePriorAndSwitchTo_AfterCommit_ClearsSwitchSegmentSession()
        {
            string source = ReadMapFocusObjectPatchSource();

            // (a) The clear call exists inside the Merge handler.
            int mergeStart = source.IndexOf(
                "private static void MergePriorAndSwitchTo(",
                StringComparison.Ordinal);
            Assert.True(mergeStart > 0,
                "MergePriorAndSwitchTo handler must be defined");
            int mergeEnd = source.IndexOf(
                "private static void DiscardPriorAndSwitchTo(",
                mergeStart, StringComparison.Ordinal);
            Assert.True(mergeEnd > mergeStart,
                "DiscardPriorAndSwitchTo handler must follow Merge handler");
            string mergeBody = source.Substring(mergeStart, mergeEnd - mergeStart);

            // The new clear line — distinguished from the pre-existing
            // "pre-switch-dialog-merge-no-active-tree" defensive clear
            // by the merge-committed reason.
            Assert.Contains(
                "ClearSwitchSegmentSession(\"merge-committed\")",
                mergeBody);
            Assert.Contains(
                "pre-switch-dialog-session-cleared",
                mergeBody);
            Assert.Contains(
                "reason=merge-committed",
                mergeBody);

            // (b) The reason argument is non-empty (the merge-committed
            // literal above is itself the evidence; assert it explicitly
            // so a future refactor that swaps to a Guid / null can't
            // sneak past).
            Assert.DoesNotContain(
                "ClearSwitchSegmentSession(null)", mergeBody);
            Assert.DoesNotContain(
                "ClearSwitchSegmentSession(\"\")", mergeBody);

            // (c) The clear precedes ArmIntentAndSwitchTo so the
            // synchronous onVesselChange consume for the new target
            // sees a clean slate. The defensive clear branch (no active
            // tree) also calls ArmIntentAndSwitchTo afterward, so the
            // assertion is "every ClearSwitchSegmentSession call inside
            // the Merge handler appears before the (single)
            // ArmIntentAndSwitchTo invocation".
            int armIdx = mergeBody.IndexOf(
                "ArmIntentAndSwitchTo(target)", StringComparison.Ordinal);
            Assert.True(armIdx > 0,
                "MergePriorAndSwitchTo must call ArmIntentAndSwitchTo");
            int searchFrom = 0;
            int clearCount = 0;
            while (true)
            {
                int idx = mergeBody.IndexOf(
                    "ClearSwitchSegmentSession(",
                    searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;
                Assert.True(idx < armIdx,
                    "ClearSwitchSegmentSession must precede ArmIntentAndSwitchTo");
                clearCount++;
                searchFrom = idx + 1;
            }
            Assert.True(clearCount >= 1,
                "Merge handler must clear the prior session at least once");
        }

        // Fails if: future refactor of TryDiscardActiveSwitchSegmentAttempt
        // drops the implicit clear, leaving the Discard handler in the
        // same orphan state Bug B fixed for the Merge handler.
        [Fact]
        public void DiscardPriorAndSwitchTo_ClearsSwitchSegmentSession_ViaScopedDiscard()
        {
            // The Discard handler calls
            // RecordingStore.TryDiscardActiveSwitchSegmentAttempt which
            // calls scenario.ClearSwitchSegmentSession("scoped-discard")
            // internally. This test pins both ends of that contract
            // (the Discard handler routes through the helper, and the
            // helper clears the session).
            string mapPatchSource = ReadMapFocusObjectPatchSource();
            int discardStart = mapPatchSource.IndexOf(
                "private static void DiscardPriorAndSwitchTo(",
                StringComparison.Ordinal);
            Assert.True(discardStart > 0,
                "DiscardPriorAndSwitchTo handler must be defined");
            int discardEnd = mapPatchSource.IndexOf(
                "private static void ArmIntentAndSwitchTo(",
                discardStart, StringComparison.Ordinal);
            Assert.True(discardEnd > discardStart,
                "ArmIntentAndSwitchTo helper must follow Discard handler");
            string discardBody = mapPatchSource.Substring(
                discardStart, discardEnd - discardStart);
            Assert.Contains(
                "RecordingStore.TryDiscardActiveSwitchSegmentAttempt",
                discardBody);

            // Pin the helper still clears the session.
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string storePath = Path.Combine(projectRoot,
                "Source", "Parsek", "RecordingStore.cs");
            string storeSource = File.ReadAllText(storePath);
            // TryDiscardActiveSwitchSegmentAttempt's success path always
            // calls ClearSwitchSegmentSession("scoped-discard") before
            // returning.
            Assert.Contains(
                "ClearSwitchSegmentSession(\"scoped-discard\")",
                storeSource);
        }

        // -----------------------------------------------------------------
        // Case B (no-session) dialog handler source-text gates. These
        // pin the load-bearing shape of the new
        // MergeActiveTreeAndSwitchTo / DiscardActiveRecordingAndSwitchTo
        // handlers without standing up Unity:
        //
        // - Merge path: calls CommitTreeFlight (NOT
        //   ClearSwitchSegmentSession — no session to clear), invokes
        //   MergeDialog.OnTreeCommitted so ghost-chain evaluation
        //   picks up the newly committed recordings, and logs the
        //   distinct "merge-chosen-no-session" line.
        // - Discard path: calls AutoDiscardIdleActiveTree (the active-
        //   tree-discard helper that also rolls back the launch ledger
        //   via LedgerOrchestrator), NOT
        //   TryDiscardActiveSwitchSegmentAttempt (which no-ops without
        //   a session), and logs the distinct
        //   "discard-chosen-no-session" line.
        // -----------------------------------------------------------------

        // Fails if: the no-session Merge path tries to clear a
        // non-existent session and throws, or skips the CommitTreeFlight
        // call (leaving the active tree leaking into the new FLIGHT
        // scene).
        [Fact]
        public void MergePriorAndSwitchTo_NoSession_CommitsActiveTree()
        {
            string source = ReadMapFocusObjectPatchSource();

            int mergeStart = source.IndexOf(
                "private static void MergeActiveTreeAndSwitchTo(",
                StringComparison.Ordinal);
            Assert.True(mergeStart > 0,
                "MergeActiveTreeAndSwitchTo handler must be defined");
            int mergeEnd = source.IndexOf(
                "private static void DiscardActiveRecordingAndSwitchTo(",
                mergeStart, StringComparison.Ordinal);
            Assert.True(mergeEnd > mergeStart,
                "DiscardActiveRecordingAndSwitchTo handler must follow Merge handler");
            string mergeBody = source.Substring(mergeStart, mergeEnd - mergeStart);

            // (a) Commits the active tree via the session-agnostic
            //     in-flight commit entry point.
            Assert.Contains("flight.CommitTreeFlight()", mergeBody);

            // (b) Invokes the chain-evaluation hook (CommitTreeFlight
            //     itself does not fire OnTreeCommitted).
            Assert.Contains("MergeDialog.OnTreeCommitted", mergeBody);

            // (c) Does NOT touch the switch-segment session (no session
            //     is armed in Case B; trying to clear one would either
            //     no-op or throw on null).
            Assert.DoesNotContain("ClearSwitchSegmentSession", mergeBody);

            // (d) Emits the new distinguishable log line.
            Assert.Contains(
                "pre-switch-dialog-merge-chosen-no-session",
                mergeBody);

            // (e) Mirrors the merge-journal guard from the session-
            //     armed handler (CommitTreeFlight + journal finisher
            //     race).
            Assert.Contains(
                "merge-refused-active-merge-journal-no-session",
                mergeBody);

            // (f) ArmIntentAndSwitchTo runs at the end.
            Assert.Contains("ArmIntentAndSwitchTo(target)", mergeBody);
        }

        // Fails if: the no-session Discard path tries to discard a
        // non-existent session (TryDiscardActiveSwitchSegmentAttempt
        // would no-op, leaking the active tree across the scene
        // reload). The active-tree-discard helper
        // AutoDiscardIdleActiveTree is the load-bearing call here
        // because it tears down the recorder AND rolls back the
        // launch ledger via
        // LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions
        // (mirroring the scene-exit Discard behavior).
        [Fact]
        public void DiscardActiveRecordingAndSwitchTo_NoSession_DiscardsActiveTree()
        {
            string source = ReadMapFocusObjectPatchSource();

            int discardStart = source.IndexOf(
                "private static void DiscardActiveRecordingAndSwitchTo(",
                StringComparison.Ordinal);
            Assert.True(discardStart > 0,
                "DiscardActiveRecordingAndSwitchTo handler must be defined");
            // The handler is the last in this file before the Postfix; find
            // the closing of the method via the next method declaration or
            // class brace. ArmIntentAndSwitchTo is referenced from many
            // sites — pick the next 'static void' / 'static bool' after the
            // discard start, or the closing brace.
            int searchFrom = discardStart + 1;
            int nextMethod = source.IndexOf(
                "        static void Postfix",
                searchFrom, StringComparison.Ordinal);
            Assert.True(nextMethod > discardStart,
                "Postfix should follow DiscardActiveRecordingAndSwitchTo");
            string discardBody = source.Substring(
                discardStart, nextMethod - discardStart);

            // (a) Uses AutoDiscardIdleActiveTree — the active-tree
            //     teardown helper that ALSO rolls back the launch
            //     ledger via LedgerOrchestrator. A simple
            //     `activeTree = null` would leak the ledger entries.
            Assert.Contains(
                "flight.AutoDiscardIdleActiveTree",
                discardBody);

            // (b) Does NOT call the session-scoped discard helper
            //     (would no-op with NoActiveSession disposition, no
            //     active tree teardown).
            Assert.DoesNotContain(
                "TryDiscardActiveSwitchSegmentAttempt",
                discardBody);

            // (c) Emits the new distinguishable log line.
            Assert.Contains(
                "pre-switch-dialog-discard-chosen-no-session",
                discardBody);

            // (d) Mirrors the merge-journal guard (symmetry with the
            //     no-session Merge handler).
            Assert.Contains(
                "discard-refused-active-merge-journal-no-session",
                discardBody);

            // (e) ArmIntentAndSwitchTo runs at the end.
            Assert.Contains("ArmIntentAndSwitchTo(target)", discardBody);
        }

        // Fails if: ShowPreSwitchDecisionDialog drops the
        // priorTreeOverride parameter or stops routing the Case B
        // tree through it. Case B (no-session) must hand the live
        // active tree directly to the dialog so the body renders
        // "{TreeName} - {Duration}" instead of falling back to the
        // session-id resolver (which would not find a session).
        [Fact]
        public void ShowPreSwitchDecisionDialog_AcceptsPriorTreeOverride()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string mergeDialogPath = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string mergeDialogSource = File.ReadAllText(mergeDialogPath);

            // The override parameter is the load-bearing seam between
            // the session-aware and Case B paths.
            Assert.Contains(
                "RecordingTree priorTreeOverride",
                mergeDialogSource);

            // The patch's no-session opener routes through the override.
            string patchSource = ReadMapFocusObjectPatchSource();
            Assert.Contains(
                "priorTreeOverride: activeTree",
                patchSource);
        }
    }
}
