using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Patches;

namespace Parsek.TestCommands
{
    /// <summary>
    /// R12 partial: the thin Unity applier for the single-phase
    /// <c>SimulateStockSwitchClick</c> verb. Every decision it makes is factored into the
    /// pure <see cref="TestCommandSimulateSwitchClick"/> (or borrowed verbatim from
    /// <see cref="MapFocusObjectOnSelectPatch"/>); this file only samples live state,
    /// sweeps <c>FlightGlobals.Vessels</c>, arms the marker, and calls
    /// <c>FlightGlobals.SetActiveVessel</c>.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ----- SimulateStockSwitchClick (R12, single-phase, site=map) -----
        //
        // The switch-segment code path is UNREACHABLE from the mission driver: kRPC's
        // active_vessel setter calls FlightGlobals.SetActiveVessel directly, which arms no
        // StockActionIntentMarker, so the consume site reads NoIntent, no segment starts,
        // and a switch-segment scenario certifies nothing while MapFocusObjectOnSelectPatch
        // rots. This verb reproduces the CLICK's contract instead of the setter's.
        //
        // THREE CONTRACTS ARE LOAD-BEARING HERE:
        //
        // (1) MARKER FIDELITY. The marker is built by the pure factory with the map site's
        //     own Action / SourceScene and the same four sampled values the patch samples,
        //     and the TTL is never an input - it is derived from Action (2 s for
        //     MapSwitchTo) and re-evaluated by the consume site's EvaluateStaleness on every
        //     path. Lengthening it, or arranging to skip that evaluation, would certify a
        //     marker lifetime no player click can produce.
        //
        // (2) PLAIN PATH ONLY, and the dialog cases are REFUSALS. The real Prefix has three
        //     dialog branches (Case A armed-session, Case B unloaded-target, Case C
        //     loaded-separate-committed) and a re-entry branch. A seam verb cannot drive a
        //     ControlTypes.All-locking modal (AnswerMergeDialog is markerLive-gated to the
        //     re-fly popup alone), so each one is a typed REJECTED naming the case rather
        //     than a wedge. The classification comes from the patch's OWN
        //     DecidePreSwitchDialogAction, called with the same seven inputs the Prefix
        //     computes - a restatement would drift.
        //
        // (3) POSTFIX-EQUIVALENT CLEANUP. The patch relies on a Harmony Postfix to clear a
        //     marker that SetActiveVessel refused (reason=refused-no-switch); this verb has
        //     no Postfix, so it performs the identical cleanup itself under the identical
        //     IntentId-match guard. Without it, a refused switch would leave an armed marker
        //     to be mis-consumed by the NEXT switch in the same 2 s window.
        //
        // SINGLE-PHASE, deliberately. After the unloaded-target refusal the whole path is
        // synchronous: SetActiveVessel fires onVesselSwitching -> MakeActive ->
        // onVesselChange (hence Parsek's consume) inside the call, before returning. There is
        // nothing left to wait for, so a two-phase terminal would hold the FIFO head polling
        // for an event that already happened. At-most-once is unaffected: the pump journals
        // CLAIMED before invoking ANY executor, so a crash mid-switch replays as INTERRUPTED
        // and never re-switches.
        private void SimulateStockSwitchClickImpl(ParsedCommand cmd)
        {
            string siteArg = ArgOrNull(cmd, "site");
            string vesselArg = ArgOrNull(cmd, "vessel");
            string pidArg = ArgOrNull(cmd, "pid");

            if (!TestCommandSimulateSwitchClick.TryParseSite(siteArg, out SwitchClickSite site))
            {
                ParsekLog.Warn(Tag,
                    $"switchclick rejected reason={TestCommandSimulateSwitchClick.SiteArgInvalidReason} " +
                    $"site={siteArg ?? string.Empty} (accepted: map, ts, ksc)");
                SetExecResult("REJECTED", null, TestCommandSimulateSwitchClick.SiteArgInvalidMsg(siteArg));
                return;
            }

            SwitchClickTargetDecision selector =
                TestCommandSimulateSwitchClick.DecideTargetSelector(vesselArg, pidArg);

            ParsekLog.Info(Tag,
                $"switchclick start site={TestCommandSimulateSwitchClick.SiteToken(site)} " +
                $"selector={TestCommandSimulateSwitchClick.SelectorToken(selector)} " +
                $"scene={HighLogic.LoadedScene} activeVesselPid={ActiveVesselPidOrZero()}");

            if (selector.Error != null)
            {
                ParsekLog.Warn(Tag,
                    $"switchclick rejected reason={selector.Error} vessel={vesselArg ?? string.Empty} " +
                    $"pid={pidArg ?? string.Empty} (supply exactly one of vessel= / pid=; pid wins when both are given)");
                SetExecResult("REJECTED", null, selector.Error);
                return;
            }

            if (!TestCommandSimulateSwitchClick.IsSiteImplemented(site))
            {
                // ts / ksc arm through their OWN patched handlers (SpaceTracking.FlyVessel /
                // KSCVesselMarkers.FlyVessel) from their OWN scenes, and both cross into a
                // fresh FLIGHT load. Hand-rolling FlightDriver.StartAndFocusVessel here would
                // also skip GhostMapPresence.RemoveAllGhostVesselsBeforeStockFly, whose
                // absence is a live-list / saved-file index desync bug. They land with their
                // own consumers, not as an untested branch of the map verb.
                ParsekLog.Warn(Tag,
                    $"switchclick rejected reason={TestCommandSimulateSwitchClick.SiteNotImplementedReason} " +
                    $"site={TestCommandSimulateSwitchClick.SiteToken(site)} - v1 drives site=map only");
                SetExecResult("REJECTED", null, TestCommandSimulateSwitchClick.SiteNotImplementedMsg(site));
                return;
            }

            Vessel target = ResolveSwitchClickTarget(selector, out int liveMatches, out int ghostMatches);
            SwitchClickTargetResolution resolution =
                TestCommandSimulateSwitchClick.ClassifyResolution(liveMatches, ghostMatches);
            // Belt-and-braces: the sweep sets `found` on the first live match, so a Resolved
            // count with a null vessel is unreachable. Normalising rather than trusting it
            // keeps a hypothetical mismatch from emitting REJECTED with a NULL msg, which is
            // a terminal no spec could match on.
            if (target == null && resolution == SwitchClickTargetResolution.Resolved)
                resolution = SwitchClickTargetResolution.NotFound;
            if (resolution != SwitchClickTargetResolution.Resolved)
            {
                string resolveMsg = TestCommandSimulateSwitchClick.ResolutionMsg(resolution, selector, liveMatches);
                ParsekLog.Warn(Tag,
                    $"switchclick rejected reason={resolveMsg} resolution={resolution} " +
                    $"liveMatches={liveMatches} ghostMatches={ghostMatches} " +
                    $"vesselsScanned={(FlightGlobals.Vessels != null ? FlightGlobals.Vessels.Count : 0)}");
                SetExecResult("REJECTED", null, resolveMsg);
                return;
            }

            // ----- Sample exactly what the Prefix samples -----
            ParsekScenario scenario = ParsekScenario.Instance;
            ParsekFlight flight = ParsekFlight.Instance;
            bool scenarioReady = scenario != null;
            bool canSwitchVesselsFar = CanSwitchVesselsFarOrFalse();
            uint activePidBefore = ActiveVesselPidOrZero();
            bool targetIsActiveVessel = activePidBefore != 0u && activePidBefore == target.persistentId;
            bool hasActiveRecording = flight != null && flight.HasActiveTree;
            bool targetIsUnloaded = !target.loaded;
            SwitchSegmentSession session = scenario != null ? scenario.ActiveSwitchSegmentSession : null;
            bool anotherDialogOpen = ParsekScenario.MergeDialogPending;
            bool targetIsSeparateCommittedVessel =
                EvaluateTargetIsSeparateCommittedVessel(target, flight, hasActiveRecording, targetIsUnloaded);

            MapFocusObjectOnSelectPatch.PreSwitchDialogDecision dialogDecision =
                MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction(
                    hasActiveSession: session != null,
                    priorFocusedPid: session != null ? session.FocusedVesselPersistentId : 0u,
                    newTargetPid: target.persistentId,
                    anotherDialogOpen: anotherDialogOpen,
                    hasActiveRecording: hasActiveRecording,
                    targetIsUnloaded: targetIsUnloaded,
                    targetIsSeparateCommittedVessel: targetIsSeparateCommittedVessel);

            // The whole gate input set on one line, so a refusal is reconstructable from the
            // log without re-running anything.
            ParsekLog.Info(Tag,
                $"switchclick gate targetPid={target.persistentId} targetName={target.vesselName ?? string.Empty} " +
                $"scenarioReady={Bool(scenarioReady)} canSwitchVesselsFar={Bool(canSwitchVesselsFar)} " +
                $"targetIsActiveVessel={Bool(targetIsActiveVessel)} targetIsUnloaded={Bool(targetIsUnloaded)} " +
                $"hasActiveRecording={Bool(hasActiveRecording)} " +
                $"hasActiveSession={Bool(session != null)} " +
                $"priorFocusedPid={(session != null ? session.FocusedVesselPersistentId : 0u)} " +
                $"anotherDialogOpen={Bool(anotherDialogOpen)} " +
                $"targetIsSeparateCommittedVessel={Bool(targetIsSeparateCommittedVessel)} " +
                $"dialogDecision={dialogDecision}");

            SwitchClickGateDecision gate = TestCommandSimulateSwitchClick.DecideSwitchGate(
                scenarioReady, canSwitchVesselsFar, targetIsActiveVessel, dialogDecision, !targetIsUnloaded);

            if (gate != SwitchClickGateDecision.Proceed)
            {
                string caseToken = TestCommandSimulateSwitchClick.DialogCaseToken(
                    session != null, targetIsSeparateCommittedVessel);
                string gateMsg = TestCommandSimulateSwitchClick.GateRefusalMsg(gate, caseToken);
                ParsekLog.Warn(Tag,
                    $"switchclick refused gate={gate} reason={gateMsg} targetPid={target.persistentId} " +
                    "- v1 drives the plain arm-and-switch path only; a dialog case, an unloaded target " +
                    "or an already-active target is a typed refusal, never a driven modal or a silent no-op");
                SetExecResult("REJECTED", null, gateMsg);
                return;
            }

            // ----- The plain path: arm exactly as the map-site patch arms, then switch -----
            Guid intentId = Guid.NewGuid();
            StockActionIntentMarker marker = TestCommandSimulateSwitchClick.BuildMapSwitchToMarker(
                intentId,
                target.persistentId,
                UnityEngine.Time.realtimeSinceStartup,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                ParsekProcess.ProcessSessionId);
            scenario.ArmStockActionIntent(marker);
            ParsekLog.Info(Tag,
                $"switchclick armed intent: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId} sourceScene={marker.SourceScene} " +
                $"capturedUT={marker.CapturedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"ttl={StockActionIntentMarker.MapSwitchToTtlSeconds.ToString("R", CultureInfo.InvariantCulture)}s " +
                $"route={TestCommandSimulateSwitchClick.PlainRoute}");

            bool threw = false;
            bool accepted = false;
            try
            {
                accepted = FlightGlobals.SetActiveVessel(target);
                if (MapView.MapIsEnabled)
                    MapView.ExitMapView();
            }
            catch (Exception ex)
            {
                threw = true;
                ParsekLog.Error(Tag,
                    $"switchclick SetActiveVessel threw {ex.GetType().Name}: {ex.Message} " +
                    $"targetPid={target.persistentId}");
            }

            // Postfix equivalent. The patch's Postfix uses "marker still armed under MY
            // IntentId" as the proxy for "the consume never fired, so SetActiveVessel took an
            // early-return path", and clears with refused-no-switch. Same proxy, same guard
            // (only OUR intentId), same reason string - a later click's marker is left alone.
            if (scenario.CurrentStockActionIntent != null
                && scenario.CurrentStockActionIntent.IntentId == intentId)
            {
                scenario.ClearStockActionIntent(TestCommandSimulateSwitchClick.RefusedNoSwitchClearReason);
                ParsekLog.Info(Tag,
                    $"switchclick cleared own marker intentId={intentId:D} " +
                    $"reason={TestCommandSimulateSwitchClick.RefusedNoSwitchClearReason} " +
                    "- no consume fired (stock refused the switch)");
            }

            uint activePidAfter = ActiveVesselPidOrZero();
            bool switched = activePidAfter != 0u && activePidAfter == target.persistentId;
            List<KeyValuePair<string, string>> payload = TestCommandSimulateSwitchClick.BuildPayload(
                TestCommandSimulateSwitchClick.SiteToken(site),
                target.persistentId,
                target.vesselName,
                intentId,
                TestCommandSimulateSwitchClick.PlainRoute,
                activePidAfter,
                switched);

            if (threw)
            {
                SetExecResult("ERROR", payload, TestCommandSimulateSwitchClick.SwitchThrewReason);
                return;
            }

            if (!switched)
            {
                // OBSERVED, not commanded: stock returns false without switching for six
                // documented ClearToSave reasons plus a non-Owned discovery level. Reporting
                // OK here because "we called it" would be the fail-open this verb exists to
                // close.
                ParsekLog.Warn(Tag,
                    $"switchclick failed reason={TestCommandSimulateSwitchClick.SwitchRefusedReason} " +
                    $"targetPid={target.persistentId} activeVesselPid={activePidAfter} " +
                    $"setActiveVesselReturned={Bool(accepted)} " +
                    "- stock refused (ClearToSave: not in atmosphere / under acceleration / moving over surface / " +
                    "about to crash / on a ladder / throttled up, or DiscoveryInfo.Level != Owned)");
                SetExecResult("ERROR", payload, TestCommandSimulateSwitchClick.SwitchRefusedReason);
                return;
            }

            ParsekLog.Info(Tag,
                $"switchclick complete site={TestCommandSimulateSwitchClick.SiteToken(site)} " +
                $"targetPid={target.persistentId} targetName={target.vesselName ?? string.Empty} " +
                $"intentId={intentId:D} route={TestCommandSimulateSwitchClick.PlainRoute} " +
                $"activeVesselPid={activePidAfter} switched=true " +
                "- consume outcome (segment started / refused) is in the TryConsumeStockActionIntent lines");
            SetExecResult("OK", payload, null);
        }

        /// <summary>
        /// Sweep <c>FlightGlobals.Vessels</c> (NOT <c>VesselsLoaded</c> - the unloaded case
        /// must be DETECTED and refused with its own reason, not read as "no such vessel")
        /// for the selector, counting live and ghost matches separately. Parsek ghosts are
        /// real entries in that list, which is why all three arming patches guard them; a
        /// spec that addressed one gets <c>target-is-ghost</c> rather than a puzzling
        /// not-found.
        /// </summary>
        private static Vessel ResolveSwitchClickTarget(
            SwitchClickTargetDecision selector, out int liveMatches, out int ghostMatches)
        {
            liveMatches = 0;
            ghostMatches = 0;
            Vessel found = null;

            List<Vessel> all = FlightGlobals.Vessels;
            if (all == null)
                return null;

            for (int i = 0; i < all.Count; i++)
            {
                Vessel v = all[i];
                if (v == null) continue;

                bool match = selector.Selector == SwitchClickTargetSelector.ByPid
                    ? v.persistentId == selector.Pid
                    : string.Equals(v.vesselName, selector.Name, StringComparison.Ordinal);
                if (!match) continue;

                if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
                {
                    ghostMatches++;
                    continue;
                }

                liveMatches++;
                if (found == null) found = v;
            }

            return found;
        }

        /// <summary>
        /// The Prefix's Case C input, computed the same way it computes it: an active
        /// recording, an in-bubble target, and a committed tree matching the target whose id
        /// differs from the live active tree's. Any lookup failure falls back to false
        /// (today's in-bubble behaviour), Verbose-logged, exactly as the Prefix does.
        /// </summary>
        private static bool EvaluateTargetIsSeparateCommittedVessel(
            Vessel target, ParsekFlight flight, bool hasActiveRecording, bool targetIsUnloaded)
        {
            if (!hasActiveRecording || targetIsUnloaded || target == null)
                return false;

            try
            {
                // Guid "N" form is culture-invariant by definition (hex digits only); matches
                // the patch and the ParsekFlight consume call site.
                string liveGuid = target.id != Guid.Empty ? target.id.ToString("N") : null;
                bool matched = ParsekFlight.TryFindCommittedTreeMatchingVessel(
                    target.persistentId, liveGuid, out RecordingTree matchedTree);
                string activeTreeId = flight != null && flight.ActiveTreeForDisplay != null
                    ? flight.ActiveTreeForDisplay.Id
                    : null;
                if (matched && matchedTree != null
                    && !string.Equals(matchedTree.Id, activeTreeId, StringComparison.Ordinal))
                {
                    ParsekLog.Verbose(Tag,
                        $"switchclick Case C: loaded target is separate committed vessel " +
                        $"targetPid={target.persistentId} matchedTreeId={matchedTree.Id ?? "<null>"} " +
                        $"activeTreeId={activeTreeId ?? "<null>"}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"switchclick Case C lookup failed: {ex.GetType().Name}: {ex.Message} " +
                    $"targetPid={target.persistentId} - treating as not-separate");
            }

            return false;
        }

        /// <summary>The active vessel's persistentId, or 0 when there is none. 0 is not a
        /// valid live pid, so it doubles as the "no active vessel" sentinel in the payload.</summary>
        private static uint ActiveVesselPidOrZero()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            return active != null ? active.persistentId : 0u;
        }

        /// <summary>The Prefix's gate 5, read defensively: a missing game / parameter node
        /// reads FALSE (fail-closed), because a click stock would not have armed must not be
        /// simulated as one it would.</summary>
        private static bool CanSwitchVesselsFarOrFalse()
        {
            try
            {
                return HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Parameters != null
                    && HighLogic.CurrentGame.Parameters.Flight != null
                    && HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsFar;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"switchclick CanSwitchVesselsFar read failed: {ex.GetType().Name}: {ex.Message} - treating as false");
                return false;
            }
        }
    }
}
