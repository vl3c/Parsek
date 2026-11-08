using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        /// <summary>
        /// KERBAL-XP-UNTAGGED-RECOVERY-HAS-NO-LEDGER-ROW: writes the crew's
        /// <see cref="GameActionType.KerbalExperience"/> rows for a recovery that reached
        /// the XP seam with no live recorder to tag it - the KSC / tracking-station
        /// recovery, and (measured) the ordinary FLIGHT recovery too, whose reward burst
        /// fires in SPACECENTER milliseconds AFTER the scene-exit auto-commit has already
        /// closed the tree.
        ///
        /// <para>
        /// <b>The correlation is the whole fix.</b> The carve-out this replaces refused to
        /// write anything because a null-<c>RecordingId</c> XP row could never be
        /// tombstoned (<see cref="TombstoneEligibility"/> is scoped by recording), so a
        /// re-fly could not take the XP back and the monotone career-log re-assert would
        /// put it on the roster forever. Resolving an owner through
        /// <see cref="PickRecoveryRecordingId"/> removes that objection: the row is scoped,
        /// so the merge retires it exactly like the recovery's funds row.
        /// </para>
        ///
        /// <para>
        /// <b>Why THIS correlator and no other.</b>
        /// <see cref="ResurrectionRetirementEligibility"/> bundles a recovery's
        /// <c>FundsEarning(Recovery)</c>, <c>ScienceEarning</c>, <c>KerbalAssignment</c> and
        /// <c>KerbalExperience</c> rows by SHARED <c>RecordingId</c>. The funds and science
        /// legs both resolve through <see cref="PickRecoveryRecordingId"/>; picking any
        /// other way here would scope the XP row to a different recording and split a
        /// bundle that is retired as a unit.
        /// </para>
        ///
        /// <para>
        /// <b>A null pick REFUSES the write</b> rather than falling back to an untagged
        /// row - that is the carve-out's fail-safe kept intact, not a regression. The
        /// refusal is logged with the kerbal count so an unowned recovery is visible as a
        /// positive fact rather than as silence.
        /// </para>
        ///
        /// <para>
        /// <b>KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 2: an AMBIGUOUS pick refuses
        /// too</b>, with <c>reason=ambiguous-recovery-recording</c>. This is the only leg that
        /// does so. The predicate and its rationale live in <see cref="RecoveryPickAmbiguity"/>;
        /// the short form is that a funds or science row is re-derived idempotently on every
        /// recalc (wrong but revisable) while this row appends career-log entries through a
        /// facade with no remove counterpart (wrong and unreachable). Stage 1's guid filter
        /// runs first and is what turns "weak tier" into "weak tier AND genuinely ambiguous".
        /// </para>
        /// </summary>
        /// <returns>
        /// Number of input events that end the call WITH a ledger row - rows written plus
        /// rows an earlier pass had already written (dedup). 0 on refusal. Counting the
        /// deduped ones as covered is deliberate: the caller's summary counter names the
        /// events left WITHOUT a row, and a suppressed duplicate is not one of those.
        /// </returns>
        internal static int TryRecordRecoveryKerbalExperience(
            IList<GameStateEvent> experienceEvents,
            RecoveredVesselIdentity identity,
            double ut)
        {
            if (experienceEvents == null || experienceEvents.Count == 0)
                return 0;

            Initialize();

            string utText = ut.ToString("F1", CultureInfo.InvariantCulture);
            string countText = experienceEvents.Count.ToString(CultureInfo.InvariantCulture);

            var picked = PickRecoveryRecording(identity, ut);
            string recordingId = picked.RecordingId;
            if (string.IsNullOrEmpty(recordingId))
            {
                ParsekLog.Info(Tag,
                    $"Recovery kerbal XP refused: {identity.FormatForLog()} ut={utText} " +
                    $"kerbals={countText} reason=no-recovery-recording");
                return 0;
            }

            // KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 2. The XP row is the
            // IRREVERSIBLE one, so it is the only leg that refuses a pick nothing
            // determines: more than one candidate survived the stage-1 launch-guid filter,
            // the winner was chosen by a weak EndUT-ordering tier, AND the survivors are
            // not positively corroborated as one launch. That third clause is what keeps
            // this from being the bare tier-strength refusal the entry's "What NOT to do"
            // paragraph forbids: most-recent-ended over TWO survivors is what the ordinary
            // single-launch career recovery measurably produces (its chained segments), and
            // refusing that would empty L4's KerbalXp facet. See RecoveryPickAmbiguity.
            //
            // ONCE PER RECOVERY, not once per recalc: this method is reached only from
            // GameStateRecorder.OnVesselRecoveryProcessingForExperience, which fires on the
            // stock recovery event. RecalculateAndPatch re-derives rows FROM the ledger and
            // never re-enters here, so the refusal cannot repeat for one recovery and needs
            // no rate limiting - the same reason the no-recovery-recording line above is a
            // plain Info. A LATER recovery whose ambiguity has resolved (a candidate
            // retired, or guids became known) writes its row normally: nothing here is
            // sticky.
            var ambiguity = RecoveryPickAmbiguity.Evaluate(picked.Survivors, picked.Tier);
            if (ambiguity.IsAmbiguous)
            {
                ParsekLog.Info(Tag,
                    $"Recovery kerbal XP refused: {identity.FormatForLog()} ut={utText} " +
                    $"kerbals={countText} reason={RecoveryPickAmbiguity.AmbiguousReason} " +
                    $"survivors={ambiguity.SurvivorCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"nameMatches={picked.NameMatchCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"guidDropped={picked.GuidDropped.ToString(CultureInfo.InvariantCulture)} " +
                    $"tier={RecoveryPickAmbiguity.TierToken(ambiguity.Tier)} " +
                    $"corroboration={RecoveryPickAmbiguity.CorroborationToken(ambiguity.Corroboration)} " +
                    $"wouldHavePicked={recordingId} " +
                    $"survivorIds={RecoveryPickAmbiguity.FormatSurvivorIds(picked.Survivors)}");
                return 0;
            }

            var actions = new List<GameAction>(experienceEvents.Count);
            int noAction = 0;
            for (int i = 0; i < experienceEvents.Count; i++)
            {
                var action = GameStateEventConverter.ConvertEvent(experienceEvents[i], recordingId);
                if (action == null)
                {
                    noAction++;
                    continue;
                }
                actions.Add(action);
            }

            if (actions.Count == 0)
            {
                ParsekLog.Info(Tag,
                    $"Recovery kerbal XP refused: {identity.FormatForLog()} ut={utText} " +
                    $"kerbals={countText} reason=no-convertible-row");
                return 0;
            }

            int beforeDedup = actions.Count;
            actions = DeduplicateAgainstLedger(actions);
            if (actions.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Recovery kerbal XP: all {beforeDedup.ToString(CultureInfo.InvariantCulture)} " +
                    $"row(s) already in the ledger for {identity.FormatForLog()} ut={utText} " +
                    $"recordingId={recordingId}");
                return beforeDedup;
            }

            for (int i = 0; i < actions.Count; i++)
                actions[i].Sequence = AllocateKscSequence();

            Ledger.AddActions(actions, LedgerRowWriter.StockApplied);

            var names = new List<string>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
                names.Add(actions[i].KerbalName ?? "");

            ParsekLog.Info(Tag,
                $"Recovery kerbal XP recorded: {identity.FormatForLog()} ut={utText} " +
                $"recordingId={recordingId} " +
                $"rows={actions.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"deduped={(beforeDedup - actions.Count).ToString(CultureInfo.InvariantCulture)} " +
                $"noAction={noAction.ToString(CultureInfo.InvariantCulture)} " +
                $"kerbals='{string.Join(",", names.ToArray())}'");

            RecalculateAndPatchForLiveTimelineEvent(ut, "recovery-kerbal-xp");
            return beforeDedup;
        }

        /// <summary>
        /// KERBAL-ABOARD-RESERVATION-OUTLIVES-THE-REAL-VESSEL: KSP recovered a real vessel
        /// with <paramref name="liveCrewNames"/> aboard. When the vessel continues a
        /// committed recording (<see cref="CrewRecoveryReservationClose.SelectOwnerRecordings"/>:
        /// the same launch by POSITIVE guid match, or the vessel Parsek spawned from it),
        /// write one <see cref="GameActionType.KerbalRecovered"/> row per recovered kerbal
        /// whose open-ended hold from that recording's tree the recovery ends, then
        /// recalculate so the hold becomes UT 0 -> <paramref name="ut"/> and the kerbal is
        /// free from now on (a rewind to before <paramref name="ut"/> holds him again,
        /// because the row is part of the committed timeline the walk re-derives from).
        ///
        /// <para>Why a row and not a re-stamp: the ordinary in-flight Recover with auto-merge
        /// on commits the flight at the scene change BEFORE stock recovers the vessel, and
        /// committed recordings are never modified by a terminal event
        /// (<c>ParsekScenario.UpdateRecordingsForTerminalEvent</c>). The ledger stays the
        /// single source the reservation is derived from.</para>
        ///
        /// <para>Deduplicated per (owner recording, kerbal) inside the 0.1 s dedup window
        /// (<see cref="GetActionKey"/>), so the same recovery delivered twice writes once.
        /// The caller filters Parsek's own programmatic recoveries (crew-suppressed) and
        /// rewind strips.</para>
        /// </summary>
        /// <returns>Rows written (0 when nothing was open-ended or all were duplicates).</returns>
        internal static int OnRealVesselCrewRecovered(
            double ut,
            uint vesselPid,
            string launchGuid,
            string vesselName,
            IList<string> liveCrewNames)
        {
            string utText = ut.ToString("F1", CultureInfo.InvariantCulture);
            string vesselText = vesselName ?? "(null)";
            if (liveCrewNames == null || liveCrewNames.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Recovery crew reservation close: vessel='{vesselText}' pid={vesselPid} " +
                    $"ut={utText} - no crew aboard, nothing to close");
                return 0;
            }

            Initialize();

            var ers = EffectiveState.ComputeERS();
            var owners = CrewRecoveryReservationClose.SelectOwnerRecordings(
                ers, vesselPid, launchGuid, ut);
            if (owners.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Recovery crew reservation close: vessel='{vesselText}' pid={vesselPid} " +
                    $"guid={launchGuid ?? "(null)"} ut={utText} - no committed recording continues " +
                    "this vessel, nothing to close");
                return 0;
            }

            var ownerNames = new List<string>(liveCrewNames.Count);
            for (int i = 0; i < liveCrewNames.Count; i++)
            {
                if (!string.IsNullOrEmpty(liveCrewNames[i]))
                    ownerNames.Add(liveCrewNames[i]);
            }
            KerbalsModule.ReverseMapCrewNames(ownerNames, CrewReservationManager.CrewReplacements, null);

            var rows = CrewRecoveryReservationClose.BuildClosureRows(
                owners, ownerNames, EffectiveState.ComputeELS(), ers, ut);
            if (rows.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Recovery crew reservation close: vessel='{vesselText}' ut={utText} " +
                    $"owners={owners.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"crew='{string.Join(",", ownerNames.ToArray())}' - no open-ended hold in scope");
                return 0;
            }

            int written = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var single = DeduplicateAgainstLedger(new List<GameAction> { row.Action });
                if (single.Count == 0)
                {
                    ParsekLog.Verbose(Tag,
                        $"Recovery crew reservation close: '{row.Action.KerbalName}' " +
                        $"recordingId={row.Action.RecordingId} ut={utText} already in the ledger");
                    continue;
                }

                row.Action.Sequence = AllocateKscSequence();
                Ledger.AddAction(row.Action, LedgerRowWriter.StockApplied);
                written++;
                ParsekLog.Info(Tag,
                    $"Crew reservation closed by recovery: '{row.Action.KerbalName}' " +
                    $"recoveryUT={utText} recordingId={row.Action.RecordingId} " +
                    $"vessel='{vesselText}' openHolds={row.ClosedHolds.ToString(CultureInfo.InvariantCulture)}");
            }

            if (written > 0)
                RecalculateAndPatchForLiveTimelineEvent(ut, "recovery-crew-reservation-close");
            return written;
        }

        /// <summary>
        /// SPAWNED-VESSEL-RECOVERED-OUTSIDE-FLIGHT-RESPAWNS-ON-SANDBOX: the player recovered a
        /// real vessel. When it continues a committed recording
        /// (<see cref="CrewRecoveryReservationClose.SelectOwnerRecordings"/>: the same launch
        /// by POSITIVE guid match, or the vessel Parsek spawned from it; committed ERS only, one
        /// owner per tree, recordings ending after the recovery dropped), write one
        /// <see cref="GameActionType.VesselRecovered"/> row per owner at <paramref name="ut"/>.
        /// <see cref="RecoveredRecordingEvidence"/> reads it so the owner's vessel is never
        /// spawned again, in every game mode and with or without crew (the funds and
        /// crew-close rows are both conditional).
        ///
        /// <para>Player recoveries only: Parsek's own programmatic recoveries run under
        /// <c>SuppressionGuard.Crew()</c>, and this writer refuses while
        /// <see cref="GameStateRecorder.SuppressCrewEvents"/> is set (the caller filters the
        /// same flag, plus ghost-map vessels and rewind strips). A recording owned only by the
        /// pending tree gets no row: ERS excludes it and the pending tree's own terminal stamp
        /// records the recovery.</para>
        ///
        /// <para>Deduplicated per owner recording inside the 0.1 s dedup window
        /// (<see cref="GetActionKey"/>), so the same recovery delivered twice writes once.</para>
        /// </summary>
        /// <returns>Rows written (0 when no committed recording owns the vessel, the recovery
        /// was programmatic, or every row was a duplicate).</returns>
        internal static int OnRealVesselRecovered(
            double ut,
            uint vesselPid,
            string launchGuid,
            string vesselName)
        {
            var ic = CultureInfo.InvariantCulture;
            string utText = ut.ToString("F1", ic);
            string vesselText = vesselName ?? "(null)";
            if (GameStateRecorder.SuppressCrewEvents)
            {
                ParsekLog.Verbose(Tag,
                    $"Vessel recovery row skipped: vessel='{vesselText}' pid={vesselPid.ToString(ic)} " +
                    $"ut={utText} - crew events suppressed (programmatic recovery)");
                return 0;
            }
            if (vesselPid == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Vessel recovery row skipped: vessel='{vesselText}' ut={utText} - no vessel pid");
                return 0;
            }
            // A Re-Fly session owns its in-flight recovery: the Recovered stamp lands on the
            // session's provisional, and Merge / Discard decide the outcome. A row written here
            // would tag the committed origin (same launch guid) and survive a Discard.
            var scenarioForReFly = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenarioForReFly)
                && scenarioForReFly.ActiveReFlySessionMarker != null)
            {
                ParsekLog.Info(Tag,
                    $"Vessel recovery row skipped: vessel='{vesselText}' pid={vesselPid.ToString(ic)} " +
                    $"ut={utText} - Re-Fly session " +
                    $"{scenarioForReFly.ActiveReFlySessionMarker.SessionId ?? "<no-id>"} active");
                return 0;
            }

            Initialize();

            var ers = EffectiveState.ComputeERS();
            var owners = CrewRecoveryReservationClose.SelectOwnerRecordings(
                ers, vesselPid, launchGuid, ut);
            if (owners.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Vessel recovery row: vessel='{vesselText}' pid={vesselPid.ToString(ic)} " +
                    $"guid={launchGuid ?? "(null)"} ut={utText} - no committed recording continues " +
                    "this vessel, nothing to write");
                return 0;
            }

            int written = 0;
            int deduped = 0;
            for (int i = 0; i < owners.Count; i++)
            {
                var owner = owners[i];
                var row = new GameAction
                {
                    UT = ut,
                    Type = GameActionType.VesselRecovered,
                    RecordingId = owner.RecordingId,
                    RecoveredVesselName = vesselName,
                    RecoveredVesselPid = vesselPid,
                };
                var single = DeduplicateAgainstLedger(new List<GameAction> { row });
                if (single.Count == 0)
                {
                    deduped++;
                    ParsekLog.Verbose(Tag,
                        $"Vessel recovery row: recordingId={owner.RecordingId} ut={utText} " +
                        "already in the ledger");
                    continue;
                }

                row.Sequence = AllocateKscSequence();
                Ledger.AddAction(row, LedgerRowWriter.StockApplied);
                written++;
                ParsekLog.Info(Tag,
                    $"Vessel recovered recorded: vessel='{vesselText}' pid={vesselPid.ToString(ic)} " +
                    $"recoveryUT={utText} recordingId={owner.RecordingId} " +
                    $"terminal={(owner.TerminalStateValue.HasValue ? owner.TerminalStateValue.Value.ToString() : "<none>")} " +
                    $"spawnedPid={owner.SpawnedVesselPersistentId.ToString(ic)}");
            }

            ParsekLog.Info(Tag,
                $"Vessel recovery rows: vessel='{vesselText}' pid={vesselPid.ToString(ic)} ut={utText} " +
                $"owners={owners.Count.ToString(ic)} written={written.ToString(ic)} " +
                $"deduped={deduped.ToString(ic)}");

            if (written > 0)
                RecalculateAndPatchForLiveTimelineEvent(ut, "vessel-recovered");
            return written;
        }

        /// <summary>
        /// Stable fingerprint for a FundsChanged(VesselRecovery) event. Internal for
        /// direct testability.
        /// </summary>
        internal static string BuildRecoveryEventDedupKey(GameStateEvent e)
        {
            return LedgerRecoveryFundsPairing.BuildRecoveryEventDedupKey(e);
        }

        /// <summary>
        /// Locates the latest FundsChanged(VesselRecovery) event within the recovery
        /// epsilon window. When <paramref name="skipConsumed"/> is true, events whose
        /// dedup fingerprint is already consumed are ignored by
        /// <see cref="LedgerRecoveryFundsPairing"/>.
        /// </summary>
        private static bool TryFindRecoveryFundsEvent(
            IReadOnlyList<GameStateEvent> events,
            double ut,
            bool skipConsumed,
            out GameStateEvent matched,
            out string dedupKey)
        {
            return LedgerRecoveryFundsPairing.TryFindRecoveryFundsEvent(
                events,
                ut,
                skipConsumed,
                out matched,
                out dedupKey);
        }

        private static bool TryFindRecoveryFundsEvent(
            IReadOnlyList<GameStateEvent> events,
            double ut,
            bool skipConsumed,
            RecoveredVesselIdentity preferredIdentity,
            out GameStateEvent matched,
            out string dedupKey)
        {
            return LedgerRecoveryFundsPairing.TryFindRecoveryFundsEvent(
                events,
                ut,
                skipConsumed,
                preferredIdentity,
                out matched,
                out dedupKey);
        }

        internal static int PendingRecoveryFundsCountForTesting =>
            LedgerRecoveryFundsPairing.PendingRecoveryFundsCountForTesting;

        internal static void OnRecoveryFundsEventRecorded(GameStateEvent evt)
        {
            if (evt.eventType != GameStateEventType.FundsChanged)
                return;
            if (evt.key != VesselRecoveryReasonKey)
                return;

            // Initialize() is idempotent: it short-circuits on the `initialized` flag so
            // repeated calls cost one branch. See LedgerOrchestrator.Initialize().
            Initialize();

            LedgerRecoveryFundsPairing.OnRecoveryFundsEventRecorded(
                evt,
                TryAddVesselRecoveryFundsAction);
        }

        /// <summary>
        /// Picks the best pending recovery-funds request for a FundsChanged(VesselRecovery)
        /// event using the two-tier policy documented on <see cref="OnRecoveryFundsEventRecorded"/>.
        /// Internal static for direct testability.
        /// </summary>
        /// <remarks>
        /// <para>Ties after vessel-name matching at identical UT distance fall back to the
        /// first match (list order) but emit a WARN so the ambiguity is visible.</para>
        /// </remarks>
        internal static int FindBestPairingIndex(double eventUt, string eventVesselName)
        {
            return LedgerRecoveryFundsPairing.FindBestPairingIndex(eventUt, eventVesselName);
        }

        /// <summary>
        /// Drains unclaimed pending recovery-funds entries at a lifecycle
        /// boundary (scene switch, rewind end, load). Any entry still waiting for a
        /// paired FundsChanged(VesselRecovery) event past the boundary cannot pair
        /// afterward — the funds event would have arrived by now if stock was going to
        /// fire it. The WARN lists every unclaimed entry so a missing-payout bug stays
        /// visible instead of silently leaking into the next session's pending list.
        /// </summary>
        /// <param name="reason">Human-readable reason for the flush (e.g., "scene switch",
        /// "rewind end", "KSP load"). Appears in the WARN log line for diagnostics.</param>
        internal static void FlushStalePendingRecoveryFunds(string reason)
        {
            LedgerRecoveryFundsPairing.FlushStalePendingRecoveryFunds(reason);
        }

        /// <summary>
        /// Compatibility repair for saves written before recovery FundsEarning actions
        /// persisted <see cref="GameAction.DedupKey"/>. Rebuilds the missing key from
        /// the matching loaded FundsChanged(VesselRecovery) event.
        /// </summary>
        private static void RepairMissingRecoveryDedupKeys()
        {
            LedgerRecoveryFundsPairing.RepairMissingRecoveryDedupKeys(
                Ledger.Actions,
                GameStateStore.Events);
        }

        /// <summary>
        /// #444: Called from <see cref="ParsekScenario"/>'s <c>onVesselRecovered</c> handler
        /// when a vessel is recovered from outside the Flight scene (tracking station or the
        /// post-flight summary at KSC). KSP emits <c>FundsChanged(VesselRecovery)</c> at the
        /// recovery UT, but that event lies between recording windows for any committed
        /// recording — <see cref="CreateVesselCostActions"/> only emits <see cref="FundsEarningSource.Recovery"/>
        /// when the recording's <see cref="TerminalState"/> is <see cref="TerminalState.Recovered"/>
        /// AND the funds delta falls inside its <see cref="Recording.Points"/> window. Recovery
        /// performed after the recording has already ended misses both gates and the funds were
        /// silently dropped from the ledger.
        ///
        /// <para>This routes the recovery payout into the ledger as a real-time
        /// <see cref="GameActionType.FundsEarning"/> action with <see cref="FundsEarningSource.Recovery"/>.
        /// The amount comes from the matching <see cref="GameStateEventType.FundsChanged"/> event
        /// in <see cref="GameStateStore"/>. If stock fires <c>onVesselRecovered</c> before
        /// the funds event, the request is deferred and paired when
        /// <see cref="OnRecoveryFundsEventRecorded"/> sees the event. The recording tag is the committed recording whose
        /// <c>[StartUT, EndUT]</c> brackets <paramref name="ut"/> (preferred), then the most
        /// recent same-named flight that ended at or before <paramref name="ut"/>, then the
        /// global latest by EndUT. Falls back to <c>null</c> when no real recording matches.</para>
        ///
        /// <para>Each <see cref="GameStateStore.Events"/> entry can pair to at most one
        /// recovery call: consumed recovery-event fingerprints are tracked in
        /// the recovery pairing helper so a bulk-recovery burst (two
        /// <c>onVesselRecovered</c> callbacks within the epsilon window) routes each call
        /// to its own funds delta instead of double-latching the most recent event.</para>
        ///
        /// <para>The missing-pair <see cref="VesselType.Debris"/> case is handled before
        /// the deferred queue: once immediate pairing fails, debris callbacks return
        /// without entering the pending recovery-funds queue. Other vessel types,
        /// including <see cref="VesselType.SpaceObject"/>, keep the deferred-pair path.</para>
        ///
        /// <para>In-flight recovery is handled by the existing terminal-state path:
        /// <see cref="ParsekScenario.UpdateRecordingsForTerminalEvent"/> sets
        /// <see cref="TerminalState.Recovered"/> on the live recording, and the subsequent
        /// commit invokes <see cref="CreateVesselCostActions"/> which emits the same action.
        /// <see cref="ParsekScenario"/> only calls this helper when no pending-tree
        /// recording still owns the vessel, avoiding overlap with the commit-time path.</para>
        ///
        /// <para><b>Known false-positive in <see cref="ReconcileEarningsWindow"/>:</b> a future
        /// recording-commit whose <c>[startUT, endUT]</c> overlaps <paramref name="ut"/> will
        /// see this <c>FundsChanged(VesselRecovery)</c> event in-window but the matching
        /// <c>FundsEarning(Recovery)</c> action lives in the pre-existing ledger (added here),
        /// not in <c>newActions</c>. The reconciliation walk only sums <c>newActions</c>, so
        /// it will WARN about a missing earning channel of magnitude
        /// <c>delta</c>. Acceptable: the WARN is informational, the ledger is correct, and
        /// folding pre-existing in-window recovery actions into the comparison would also
        /// require accounting for in-window non-recovery deletions — out of scope for #444.</para>
        /// </summary>
        /// <param name="ut">UT at which <c>onVesselRecovered</c> fired.</param>
        /// <param name="vesselName">Recovered vessel name (used to tag the action with the matching recording).</param>
        /// <param name="quick">Stock's <c>onVesselRecovered</c> quick flag (true only from
        /// <c>ProtoVessel.Clean</c>); a pass-through diagnostic for the log line.</param>
        /// <param name="vesselType">Recovered vessel type; debris without an immediate
        /// pair is not deferred because debris-only recoveries can lack a ledger-worthy
        /// paired FundsChanged(VesselRecovery) event, except inside stock's own recovery
        /// dispatch for the vessel (the overload's <c>dispatchPersistentId</c>).</param>
        internal static void OnVesselRecoveryFunds(
            double ut,
            string vesselName,
            bool quick,
            VesselType vesselType = VesselType.Unknown)
        {
            OnVesselRecoveryFunds(
                ut,
                RecoveredVesselIdentity.FromRawName(vesselName),
                quick,
                vesselType,
                payoutContext: null);
        }

        internal static void OnVesselRecoveryFunds(
            double ut,
            RecoveredVesselIdentity identity,
            bool quick,
            VesselType vesselType = VesselType.Unknown,
            RecoveryPayoutContext payoutContext = null,
            uint dispatchPersistentId = 0)
        {
            Initialize();

            GetResourceTrackingAvailability(out bool fundsTracked, out _, out _);
            LedgerRecoveryFundsPairing.OnVesselRecoveryFunds(
                ut,
                identity,
                quick,
                vesselType,
                payoutContext,
                fundsTracked,
                TryAddVesselRecoveryFundsAction,
                dispatchPersistentId);
        }

        private static bool TryAddVesselRecoveryFundsAction(
            double ut,
            RecoveredVesselIdentity identity,
            bool quick)
        {
            return LedgerRecoveryFundsPairing.TryAddVesselRecoveryFundsAction(
                ut,
                identity,
                quick,
                PickRecoveryRecordingId,
                AllocateKscSequence,
                Ledger.Actions,
                actionUt => RecalculateAndPatchForLiveTimelineEvent(actionUt, "vessel-recovery"));
        }

        /// <summary>
        /// #444 review item 2: pick the committed recording id that best matches a
        /// vessel-recovery payout at <paramref name="ut"/>. Selection priority:
        /// <list type="number">
        ///   <item>Recording whose <c>[StartUT, EndUT]</c> brackets <paramref name="ut"/>
        ///   (the flight that actually covers the recovery moment). If multiple bracket,
        ///   pick the one with the largest EndUT — EXCEPT that the live Re-Fly session's
        ///   admitted provisional wins outright when it is among the bracketing set (see
        ///   the bracket-tie note below).</item>
        ///   <item>Most recent flight that ended at or before <paramref name="ut"/>
        ///   (largest EndUT with EndUT &lt;= ut).</item>
        ///   <item>Global latest by EndUT (preserved fallback for recoveries whose metadata
        ///   has drifted, e.g., manual EndUT trim).</item>
        /// </list>
        /// Skips ZOMBIE <see cref="MergeState.NotCommitted"/> recordings.
        /// <para>
        /// The NotCommitted rule is SESSION-AWARE, deliberately not a blanket skip.
        /// <c>RewindInvoker.BuildProvisionalRecording</c> is the only production creator of a
        /// NotCommitted recording, and it copies the origin child's <c>VesselName</c>, so such
        /// a recording always matches this picker's vessel-name filter and — being newest —
        /// normally wins the bracketing and global-latest tiers. Two populations wear that
        /// state and they need OPPOSITE treatment:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>
        ///     The ACTIVE session's provisional is ELIGIBLE. A payout earned mid-session
        ///     (recovery UT past the origin child's EndUT — the ordinary successful re-fly)
        ///     belongs to the provisional, which becomes the surviving fork at merge and is
        ///     NOT in the supersede subtree, so the attribution becomes valid on commit.
        ///     Skipping it would redirect the payout to the origin child, whose post-rewind
        ///     actions <c>RecordingTreeSplitter</c> retags to the superseded TIP — and
        ///     <c>CommitTombstones</c> would then tombstone a REAL payout away.
        ///     <c>RecordingId</c> is the tombstone SCOPING KEY
        ///     (<c>TombstoneAttributionHelper.InSupersedeScope</c>, no UT guard of its own),
        ///     not a cosmetic label. The write-set's separate pre-rewind screen does NOT
        ///     save this case: a mid-session payout's UT is at or after the rewind point
        ///     by construction, so it passes that screen and is tombstoned.
        ///   </description></item>
        ///   <item><description>
        ///     Any OTHER NotCommitted recording is a zombie awaiting <c>LoadTimeSweep</c> and
        ///     is SKIPPED: it is going away, so its id would dangle. That is the
        ///     phantom-attribution route this closes.
        ///   </description></item>
        /// </list>
        /// <para>
        /// <b>Bracket ties prefer the admitted session provisional</b>
        /// (TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT). A payout earned DURING the re-fly at
        /// <c>rewindUT &lt;= ut &lt;= origin.EndUT</c> is bracketed by BOTH the origin child
        /// and the provisional (whose StartUT is ~<c>rewindUT</c>). While the provisional has
        /// not yet flown past where the original flight ended, largest-EndUT hands the tie to
        /// the ORIGIN CHILD — whose post-rewind rows <c>RecordingTreeSplitter</c> retags to
        /// the superseded TIP, so <c>CommitTombstones</c> retires a payout that really was
        /// earned on the surviving fork. The row's UT is <c>&gt;= rewindUT</c> by
        /// construction, so the write-set's pre-rewind screen
        /// (<c>TombstoneAttributionHelper.IsPreRewindAttributedAction</c>) cannot save it:
        /// the defect is the ATTRIBUTION, not the retirement, so the fix belongs here.
        /// </para>
        /// <para>
        /// The override is scoped to tier 1 only and is inert whenever no session is armed
        /// (<c>sessionProvisionalId</c> is null, so nothing can be admitted). An unbound /
        /// trajectory-less provisional degrades to the status quo on its own: with no
        /// trajectory bounds and no <c>ExplicitStartUT</c>/<c>ExplicitEndUT</c>,
        /// <see cref="Recording.StartUT"/> and <see cref="Recording.EndUT"/> both resolve to
        /// <c>0.0</c>, so it brackets nothing but a <c>ut == 0.0</c> recovery. Tiers 2 and 3
        /// keep their existing max-EndUT semantics untouched.
        /// </para>
        /// <para>
        /// <b>The tie-break's identity test is DELIBERATELY WIDER than the NotCommitted
        /// eligibility rule above:</b> it asks only "is this the marker's provisional?", with
        /// no <c>MergeState</c> qualifier. That is not an accident of the hoist. On the
        /// <c>preserveMarker: true</c> merge paths there is a real window in which the
        /// provisional has already flipped to <c>CommittedProvisional</c> / <c>Immutable</c>
        /// while the session marker is STILL armed, and preferring it there is correct for
        /// the same reason as before the flip: it is the fork that survives, and past
        /// <c>SupersedeCommit.AppendRelations</c> it is provably not in the supersede
        /// subtree. Narrowing this test back to <c>NotCommitted</c> would reopen the bracket
        /// tie for exactly that window.
        /// </para>
        /// <para>
        /// <b>What this override does NOT decide, and what now narrows it:</b> the tie-break
        /// is name-blind - it inherits whatever candidate set reaches the tier walk. A
        /// same-name DIFFERENT-LAUNCH recovery during an armed session (an unrelated earlier
        /// launch of the same craft, still flying and recovered mid-session) would tag to
        /// the provisional where max-EndUT picked another recording; no career value is lost
        /// on either outcome, and the misattribution is the pre-existing name-identity class
        /// (<see cref="VesselLaunchIdentity"/>), not something this override introduced.
        /// <see cref="FilterRecoveryCandidatesByLaunchGuid"/> now narrows exactly that class
        /// AHEAD of the tier walk, and the two COMPOSE rather than fight: the origin child
        /// and the provisional share the same inherited launch guid, so the filter never
        /// separates the two sides of this tie, while a foreign launch it drops is one this
        /// tie-break can then never see. Filter first, then tiers.
        /// </para>
        /// Returns null when no eligible recording matches <paramref name="vesselName"/>;
        /// no-candidate is preferred over a doomed one.
        /// <para>
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 1: the name-matching candidate
        /// set is passed through <see cref="FilterRecoveryCandidatesByLaunchGuid"/> BEFORE
        /// the tier walk, so a candidate whose recorded launch guid conclusively differs from
        /// the recovering vessel's never reaches the tiers. This overload carries no live
        /// guid (its callers only have a name), so for it the filter is inert by
        /// construction; the identity overload is where a seam with a <c>ProtoVessel</c>
        /// supplies one.
        /// </para>
        /// Internal static for testability.
        /// </summary>
        internal static string PickRecoveryRecordingId(string vesselName, double ut)
        {
            return PickRecoveryRecordingId(RecoveredVesselIdentity.FromRawName(vesselName), ut);
        }

        /// <summary>
        /// The live Re-Fly session's provisional recording id, or null when no session is
        /// armed. <c>ReferenceEquals</c> bypasses Unity's <c>Object ==</c> override so the
        /// no-scenario case (unit tests, non-flight callers) resolves to null instead of
        /// throwing.
        /// </summary>
        private static string ResolveActiveReFlyProvisionalRecordingId()
        {
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
                return null;
            return scenario.ActiveReFlySessionMarker?.ActiveReFlyRecordingId;
        }

        /// <summary>
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 1: does a name-matching
        /// candidate recording belong to a launch the recovering vessel CONCLUSIVELY is not?
        ///
        /// <para>
        /// The predicate is <see cref="VesselLaunchIdentity.GuidsConclusivelyDiffer"/> and
        /// nothing stricter, on purpose. <c>persistentId</c> is craft-baked and reused on
        /// every launch of a craft, so it cannot separate two launches of one craft name; the
        /// launch <c>Guid</c> can. But the recovery seam cannot PROMISE a guid on either side
        /// (a pre-guid recording carries none; a recovery reached through a path with no
        /// <c>ProtoVessel</c> supplies none), so
        /// <c>ResurrectionRetirementEligibility.IsPositivelySameLaunch</c> - which requires a
        /// known guid on BOTH sides - is the wrong shape here: it would retire the
        /// correlation for every legacy recording, trading a rare wrong pick for a common
        /// missing one. An unknown guid on either side is NOT conclusive, so the candidate
        /// survives and the picker behaves exactly as it did before this filter existed.
        /// </para>
        ///
        /// <para>
        /// The Re-Fly provisional is safe by construction, not by exemption:
        /// <c>RewindInvoker.CopyInheritedIdentityForFork</c> inherits the origin's
        /// <c>RecordedVesselGuid</c> (the fork restores from a quicksave that preserves the
        /// origin's <c>Vessel.id</c>), and a provisional built before that copy carries a
        /// null guid - both non-conclusive, so the session-aware NotCommitted rule above is
        /// untouched.
        /// </para>
        /// </summary>
        internal static bool IsConclusiveLaunchGuidMismatch(Recording rec, string liveLaunchGuid)
        {
            if (rec == null) return false;
            return VesselLaunchIdentity.GuidsConclusivelyDiffer(
                rec.RecordedVesselGuid, liveLaunchGuid);
        }

        /// <summary>
        /// Applies <see cref="IsConclusiveLaunchGuidMismatch"/> to an ordered candidate set,
        /// returning the survivors IN INPUT ORDER and reporting how many were dropped.
        ///
        /// <para>
        /// <b>MONOTONE by construction.</b> The result is a subsequence of the input: the
        /// filter can only REMOVE candidates, never add or reorder one. That is what makes it
        /// the safe half of the fix - a recovery whose pick was already correct cannot be
        /// turned into a wrong pick by it, only into the same pick or (when every candidate
        /// is a conclusively different launch) into the existing
        /// <c>reason=no-recovery-recording</c> refusal. It runs BEFORE tier selection, so the
        /// bracketing / most-recent-ended / global-latest walk is unchanged and simply sees a
        /// smaller set.
        /// </para>
        ///
        /// <para>
        /// A null/empty <paramref name="liveLaunchGuid"/> is the documented no-op: every
        /// candidate survives and <paramref name="dropped"/> is 0.
        /// </para>
        /// </summary>
        internal static List<Recording> FilterRecoveryCandidatesByLaunchGuid(
            IReadOnlyList<Recording> candidates, string liveLaunchGuid, out int dropped)
        {
            dropped = 0;
            var survivors = new List<Recording>(candidates?.Count ?? 0);
            if (candidates == null) return survivors;

            for (int i = 0; i < candidates.Count; i++)
            {
                var rec = candidates[i];
                if (IsConclusiveLaunchGuidMismatch(rec, liveLaunchGuid))
                {
                    dropped++;
                    continue;
                }
                survivors.Add(rec);
            }
            return survivors;
        }

        /// <summary>
        /// The active Re-Fly session's provisional recording, IF it is present in
        /// <paramref name="candidates"/>, else null.
        ///
        /// <para>
        /// <b>Callers must pass the POST-FILTER candidate set.</b> This exists so that
        /// "the session provisional is admitted" means admitted to the set the tier walk
        /// actually walks, not merely to the name-match set. A tier tie-break that preferred
        /// a provisional resolved from the pre-filter set could reinstate a candidate
        /// <see cref="FilterRecoveryCandidatesByLaunchGuid"/> removed, which would break the
        /// filter's monotone property - the property the whole stage-1 safety argument rests
        /// on (KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY).
        /// </para>
        /// </summary>
        internal static Recording FindSessionProvisionalAmong(
            IReadOnlyList<Recording> candidates, string sessionProvisionalId)
        {
            if (candidates == null || string.IsNullOrEmpty(sessionProvisionalId))
                return null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var rec = candidates[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, sessionProvisionalId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        /// <summary>
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 3: does <paramref name="rec"/>
        /// POSITIVELY carry the recovering vessel's launch guid? Both sides must be known and
        /// equal (format-insensitive). <c>persistentId</c> is deliberately not consulted: the
        /// launch guid is launch-unique on its own, and the pid is craft-baked.
        /// </summary>
        internal static bool IsPositiveLaunchGuidMatch(Recording rec, string liveLaunchGuid)
        {
            if (rec == null) return false;
            string live = VesselLaunchIdentity.NormalizeGuid(liveLaunchGuid);
            if (live == null) return false;
            string recorded = VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid);
            if (recorded == null) return false;
            return string.Equals(recorded, live, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 3: is the recovering vessel
        /// (<paramref name="livePid"/>) a GENUINE Parsek spawn of <paramref name="rec"/>? A
        /// genuine spawn's pid is KSP-unique, so the match is conclusive on its own; an
        /// adoption stamp (<c>SpawnedVesselPersistentId == VesselPersistentId</c>) is the
        /// craft-baked pid again and is refused here - a same-launch adoption is reached by
        /// the launch-guid path instead. Mirrors the spawn arm of
        /// <c>CrewRecoveryReservationClose.IsRecoveredVesselRecording</c>.
        /// </summary>
        internal static bool IsGenuineSpawnPidMatch(Recording rec, uint livePid)
        {
            if (rec == null || livePid == 0) return false;
            uint spawnedPid = rec.SpawnedVesselPersistentId;
            return spawnedPid != 0
                && spawnedPid == livePid
                && spawnedPid != rec.VesselPersistentId;
        }

        /// <summary>
        /// The recording ids a recovery may be credited to: the Effective Recording Set
        /// (<see cref="EffectiveState.ComputeERS"/> - committed, not superseded, not
        /// rewind-retired) plus, during a live Re-Fly session, the members of the session's
        /// suppressed subtree that are themselves neither superseded nor retired.
        ///
        /// <para>
        /// [ERS-exempt file, ERS read here on purpose] This file is allowlisted for raw
        /// committed-list reads, but the recovery picker must not credit a recording that
        /// left the effective timeline: after a Re-Fly merge the superseded origin and its
        /// TIP still carry the fork's launch guid. ERS alone is not quite the set, because
        /// it also hides the live session's suppressed subtree - the origin being re-flown,
        /// which is not superseded until the merge. That subtree is re-admitted so the
        /// pre-existing mid-session behaviour holds, including the
        /// TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT tie-break in which the session
        /// provisional beats a bracketing origin. The provisional itself is NotCommitted and
        /// so never in ERS; the caller admits it by id.
        /// </para>
        /// </summary>
        internal static HashSet<string> ResolveRecoveryAdmissibleRecordingIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var ers = EffectiveState.ComputeERS();
            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                if (rec != null && !string.IsNullOrEmpty(rec.RecordingId))
                    ids.Add(rec.RecordingId);
            }

            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario)) return ids;
            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null) return ids;

            var suppressed = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            if (suppressed.Count == 0) return ids;
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return ids;
            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(
                recordings, scenario.RecordingRewindRetirements);
            var suppressedSet = suppressed as HashSet<string>
                ?? new HashSet<string>(suppressed, StringComparer.Ordinal);
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                if (!suppressedSet.Contains(rec.RecordingId)) continue;
                if (rec.MergeState == MergeState.NotCommitted) continue;
                if (!EffectiveState.IsVisible(rec, scenario.RecordingSupersedes)) continue;
                if (retired.Contains(rec.RecordingId)) continue;
                ids.Add(rec.RecordingId);
            }
            return ids;
        }

        internal static string PickRecoveryRecordingId(RecoveredVesselIdentity identity, double ut)
        {
            return PickRecoveryRecording(identity, ut).RecordingId;
        }

        /// <summary>
        /// The picker's FULL result: the chosen recording id plus the two facts only the
        /// KERBAL XP leg consumes - the post-filter survivor set and the tier that chose
        /// among it.
        ///
        /// <para>
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 2 needs both, because "the pick
        /// is genuinely ambiguous" is a statement about the SET the tier walked, not about
        /// the winner alone. The funds and science legs keep calling
        /// <see cref="PickRecoveryRecordingId"/> and see no behavior change whatever: their
        /// rows are re-derived idempotently from the effective ledger on every recalc, so a
        /// mis-scoped one is wrong but REVISABLE, and refusing them would drop a real payout
        /// to buy safety they do not need. That asymmetry is the whole argument of the entry.
        /// </para>
        ///
        /// <para>
        /// <see cref="RecoveryPickResult.Survivors"/> is the POST-filter list by
        /// construction - the same list the tier walk below iterates. Handing out the
        /// name-match set instead would let a consumer reason about candidates
        /// <see cref="FilterRecoveryCandidatesByLaunchGuid"/> dropped, which is exactly the
        /// monotonicity break the filter's safety argument forbids.
        /// </para>
        ///
        /// <para>
        /// STAGE 3 - IDENTITY FIRST. The candidate set the tiers walk is chosen by identity
        /// before names are consulted: (1) every eligible recording that POSITIVELY carries
        /// the recovering vessel's launch guid (<see cref="RecoveryPickPath.LaunchGuid"/>;
        /// names ignored, so a renamed vessel still reaches its own recordings and a
        /// same-name launch never enters); else (2) the recording a genuine Parsek spawn came
        /// from, by its KSP-unique spawn pid (<see cref="RecoveryPickPath.SpawnPid"/>); else
        /// (3) the stage-1 name walk plus guid filter (<see cref="RecoveryPickPath.NameFallback"/>),
        /// for legacy id-less data and vessels no recording knows. The UT tiers below then
        /// choose the segment exactly as before, so chained segments of one launch still
        /// resolve to the bracketing segment, else the last one ended. On the launch-guid
        /// path every identity match carries one known guid, so stage 2's corroboration
        /// clause reads one-known-launch and its refusal cannot fire there. The single
        /// exception is deliberate: an active Re-Fly provisional whose own guid is still
        /// unknown is admitted on name, and stage 2 then reads that id-less member as
        /// uncorroborated - the safe direction for the irreversible row.
        /// </para>
        /// </summary>
        internal static RecoveryPickResult PickRecoveryRecording(
            RecoveredVesselIdentity identity, double ut)
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null)
            {
                return new RecoveryPickResult
                {
                    RecordingId = null,
                    Tier = RecoveryPickTier.None,
                    Survivors = new List<Recording>(),
                    NameMatchCount = 0,
                    GuidDropped = 0
                };
            }

            // The ACTIVE session's provisional is a legitimate target (it survives the
            // merge as the fork); every other NotCommitted recording is a zombie whose
            // id would dangle. Resolved once, outside the loop.
            string sessionProvisionalId = ResolveActiveReFlyProvisionalRecordingId();

            List<Recording> nameMatches, guidMatches, spawnMatches;
            Recording unknownGuidSessionProvisional;
            int zombieByName, zombieByGuid, zombieBySpawn;
            ScanRecoveryCandidates(
                recordings, identity, ut, sessionProvisionalId,
                out nameMatches, out guidMatches, out spawnMatches,
                out unknownGuidSessionProvisional,
                out zombieByName, out zombieByGuid, out zombieBySpawn);

            List<Recording> candidates;
            int guidDropped;
            int skippedZombieNotCommitted;
            string utLog = ut.ToString("F1", CultureInfo.InvariantCulture);
            RecoveryPickPath path = SelectRecoveryCandidatePath(
                identity, utLog, nameMatches, guidMatches, spawnMatches,
                unknownGuidSessionProvisional, zombieByName, zombieByGuid, zombieBySpawn,
                out candidates, out guidDropped, out skippedZombieNotCommitted);
            int candidateCount = candidates.Count;

            // THE SESSION PROVISIONAL IS RESOLVED FROM THE CANDIDATE SET THE TIER WALK
            // WALKS, NOT PASS 1. Anything reading this - the summary line, and the tier-1
            // tie-break below - must see that set. Deriving it from the name-match set
            // instead would let a tie-break resurrect a candidate the stage-1 filter (or the
            // identity path) excluded, silently breaking the monotone property that is the
            // whole safety argument for the filter.
            // The TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT tie-break in the tier-1 body
            // below consumes THIS reference by identity (ReferenceEquals against a member of
            // `candidates`). It does NOT re-derive the provisional from `nameMatches`, from a
            // raw store walk, or from a per-iteration id comparison - all three would
            // reinstate an excluded candidate. Keep it that way.
            Recording admittedSessionProvisional =
                FindSessionProvisionalAmong(candidates, sessionProvisionalId);

            LogRecoveryGuidFilterOutcome(
                identity, utLog, path, guidDropped, candidateCount, nameMatches.Count);

            Recording bracketing;
            Recording bracketingSessionProvisional;
            Recording mostRecentEnded;
            Recording globalLatest;
            WalkRecoveryPickTiers(
                candidates, ut, admittedSessionProvisional,
                out bracketing, out bracketingSessionProvisional,
                out mostRecentEnded, out globalLatest);

            // Tier-1 resolution: the admitted session provisional wins the bracket tie.
            Recording bracketPick = bracketingSessionProvisional ?? bracketing;

            Recording pick = bracketPick ?? mostRecentEnded ?? globalLatest;
            if (pick == null)
            {
                // Worth a positive trace: when the only name matches were zombie
                // provisionals, this untagged payout is the zombie skip working, not a
                // missing recording. Without this line the two are indistinguishable in a log.
                if (skippedZombieNotCommitted > 0)
                {
                    ParsekLog.Verbose(Tag,
                        $"PickRecoveryRecordingId: {identity.FormatForLog()} " +
                        $"ut={utLog} path={RecoveryPickAmbiguity.PathToken(path)} " +
                        $"nameMatches={nameMatches.Count.ToString(CultureInfo.InvariantCulture)} survivors=0 " +
                        $"skippedZombieNotCommitted={skippedZombieNotCommitted} tier=none " +
                        $"pick=<null> (only zombie provisionals matched; payout left untagged)");
                }
                return new RecoveryPickResult
                {
                    RecordingId = null,
                    Tier = RecoveryPickTier.None,
                    Survivors = candidates,
                    NameMatchCount = nameMatches.Count,
                    GuidDropped = guidDropped,
                    Path = path
                };
            }

            RecoveryPickTier pickTier = bracketPick != null ? RecoveryPickTier.Bracketing
                                      : mostRecentEnded != null ? RecoveryPickTier.MostRecentEnded
                                      : RecoveryPickTier.GlobalLatest;
            // ONE spelling of the tier tokens, shared with the stage-2 refusal line, so a
            // rename cannot desynchronize the two greps a live proof reads.
            string tier = RecoveryPickAmbiguity.TierToken(pickTier);
            // The tie decision rides the existing tier line rather than a second line:
            // one grep per pick, and the token is meaningless off tier 1 by construction.
            string bracketTie = bracketingSessionProvisional != null ? "session-provisional"
                              : bracketPick != null ? "max-end-ut"
                              : "n/a";
            // `candidates=` is deliberately NOT reused here: it used to mean the name-match
            // count and would now silently mean the survivor count. Both are logged under
            // unambiguous names instead, and nothing pinned the old token. On the identity
            // paths `nameMatches` is diagnostic only and the stage-1 filter did not run, so
            // `guidDropped` reads n/a rather than a misleading 0.
            string guidDroppedText = path == RecoveryPickPath.NameFallback
                ? guidDropped.ToString(CultureInfo.InvariantCulture)
                : "n/a";
            ParsekLog.Verbose(Tag,
                $"PickRecoveryRecordingId: {identity.FormatForLog()} ut={utLog} " +
                $"path={RecoveryPickAmbiguity.PathToken(path)} " +
                $"nameMatches={nameMatches.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"survivors={candidateCount.ToString(CultureInfo.InvariantCulture)} " +
                $"guidDropped={guidDroppedText} " +
                $"skippedZombieNotCommitted={skippedZombieNotCommitted} " +
                $"sessionProvisionalAdmitted={admittedSessionProvisional != null} " +
                $"tier={tier} bracketTie={bracketTie} pick={pick.RecordingId}");

            return new RecoveryPickResult
            {
                RecordingId = pick.RecordingId,
                Tier = pickTier,
                Survivors = candidates,
                NameMatchCount = nameMatches.Count,
                GuidDropped = guidDropped,
                Path = path
            };
        }

        /// <summary>
        /// Pass 1 of <see cref="PickRecoveryRecording"/>: one walk over the store sorts every
        /// eligible recording into the name, launch-guid and spawn-pid match lists (store order),
        /// counts the zombie provisionals per key, and logs how many matches the effective-timeline
        /// filter skipped. The active session's provisional is admitted by id.
        /// </summary>
        internal static void ScanRecoveryCandidates(
            IReadOnlyList<Recording> recordings,
            RecoveredVesselIdentity identity,
            double ut,
            string sessionProvisionalId,
            out List<Recording> nameMatches,
            out List<Recording> guidMatches,
            out List<Recording> spawnMatches,
            out Recording unknownGuidSessionProvisional,
            out int zombieByName,
            out int zombieByGuid,
            out int zombieBySpawn)
        {
            // Pass 1: ONE walk over the store classifies every eligible recording against
            // the three identity keys, in store order. KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-
            // UT-ONLY stage 3 (operator principle 2026-10-01, "we should have unique
            // identities"): the launch guid decides first, the genuine spawn pid second, and
            // the vessel NAME only when neither identity reaches any recording.
            string liveGuid = VesselLaunchIdentity.NormalizeGuid(identity.LaunchGuid);
            uint livePid = identity.PersistentId;
            nameMatches = new List<Recording>();
            guidMatches = new List<Recording>();
            spawnMatches = new List<Recording>();
            unknownGuidSessionProvisional = null;
            zombieByName = 0; zombieByGuid = 0; zombieBySpawn = 0;
            var admissible = ResolveRecoveryAdmissibleRecordingIds();
            int skippedNotEffective = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null) continue;
                bool nameMatch = identity.MatchesName(rec.VesselName);
                bool guidMatch = IsPositiveLaunchGuidMatch(rec, liveGuid);
                bool spawnMatch = IsGenuineSpawnPidMatch(rec, livePid);
                bool isSessionProvisional =
                    !string.IsNullOrEmpty(sessionProvisionalId)
                    && string.Equals(rec.RecordingId, sessionProvisionalId, StringComparison.Ordinal);
                if (rec.MergeState == MergeState.NotCommitted && !isSessionProvisional)
                {
                    // Zombie awaiting LoadTimeSweep. Counted per key, not logged per-item.
                    if (nameMatch) zombieByName++;
                    if (guidMatch) zombieByGuid++;
                    if (spawnMatch) zombieBySpawn++;
                    continue;
                }
                // Superseded and rewind-retired recordings are off the effective timeline.
                // After a Re-Fly merge the superseded origin and its TIP keep the fork's
                // launch guid, so without this gate a later recovery could credit them.
                if (!isSessionProvisional
                    && (string.IsNullOrEmpty(rec.RecordingId) || !admissible.Contains(rec.RecordingId)))
                {
                    if (nameMatch || guidMatch || spawnMatch) skippedNotEffective++;
                    continue;
                }
                if (nameMatch) nameMatches.Add(rec);
                if (guidMatch) guidMatches.Add(rec);
                if (spawnMatch) spawnMatches.Add(rec);
                if (isSessionProvisional && nameMatch
                    && VesselLaunchIdentity.NormalizeGuid(rec.RecordedVesselGuid) == null)
                {
                    unknownGuidSessionProvisional = rec;
                }
            }

            if (skippedNotEffective > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PickRecoveryRecordingId effective filter: {identity.FormatForLog()} " +
                    $"ut={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"skippedNotEffective={skippedNotEffective.ToString(CultureInfo.InvariantCulture)} " +
                    "reason=superseded-or-rewind-retired");
            }
        }

        /// <summary>
        /// Chooses the candidate set the UT tiers of <see cref="PickRecoveryRecording"/> walk:
        /// the launch-guid matches first, else the spawn-pid matches, else the name matches after
        /// the stage-1 guid filter. Logs the path line and returns the path.
        /// </summary>
        internal static RecoveryPickPath SelectRecoveryCandidatePath(
            RecoveredVesselIdentity identity,
            string utLog,
            List<Recording> nameMatches,
            List<Recording> guidMatches,
            List<Recording> spawnMatches,
            Recording unknownGuidSessionProvisional,
            int zombieByName,
            int zombieByGuid,
            int zombieBySpawn,
            out List<Recording> candidates,
            out int guidDropped,
            out int skippedZombieNotCommitted)
        {
            uint livePid = identity.PersistentId;
            RecoveryPickPath path;
            guidDropped = 0;
            if (guidMatches.Count > 0)
            {
                // THE LAUNCH-GUID PATH. Names are ignored: a vessel renamed after its
                // recording captured the name still reaches its own recordings, and a
                // same-name recording of a DIFFERENT launch never enters the set. The one
                // exception is the active Re-Fly session's provisional while its own guid is
                // still unknown (RewindInvoker's placeholder, or a fork whose origin predates
                // the guid): it is admitted on name, the legacy fallback for an id-less
                // member, so the TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT tie-break below can
                // still see it. A provisional with a KNOWN guid is already in guidMatches or
                // belongs to a different launch.
                path = RecoveryPickPath.LaunchGuid;
                candidates = guidMatches;
                if (unknownGuidSessionProvisional != null)
                    candidates.Add(unknownGuidSessionProvisional);
                skippedZombieNotCommitted = zombieByGuid;
                int nameMismatch = 0;
                for (int i = 0; i < guidMatches.Count; i++)
                    if (!identity.MatchesName(guidMatches[i].VesselName)) nameMismatch++;
                int nameOnly = 0;
                for (int i = 0; i < nameMatches.Count; i++)
                    if (!candidates.Contains(nameMatches[i])) nameOnly++;
                ParsekLog.Info(Tag,
                    $"PickRecoveryRecordingId path: {identity.FormatForLog()} ut={utLog} " +
                    $"path={RecoveryPickAmbiguity.PathToken(path)} " +
                    $"identityMatches={guidMatches.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"identityNameMismatch={nameMismatch.ToString(CultureInfo.InvariantCulture)} " +
                    $"nameOnlyIgnored={nameOnly.ToString(CultureInfo.InvariantCulture)} " +
                    $"unknownGuidSessionProvisionalAdmitted={unknownGuidSessionProvisional != null}");
            }
            else if (spawnMatches.Count > 0)
            {
                // THE SPAWN-PID PATH. A genuine Parsek spawn regenerates the vessel guid, so
                // the guid reaches nothing, but the KSP-unique spawn pid names the recording
                // that spawned it - the same arm CrewRecoveryReservationClose uses.
                path = RecoveryPickPath.SpawnPid;
                candidates = spawnMatches;
                skippedZombieNotCommitted = zombieBySpawn;
                ParsekLog.Info(Tag,
                    $"PickRecoveryRecordingId path: {identity.FormatForLog()} ut={utLog} " +
                    $"path={RecoveryPickAmbiguity.PathToken(path)} " +
                    $"identityMatches={spawnMatches.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"spawnPid={livePid.ToString(CultureInfo.InvariantCulture)} " +
                    $"nameMatches={nameMatches.Count.ToString(CultureInfo.InvariantCulture)}");
            }
            else
            {
                // THE NAME FALLBACK - legacy id-less data, or a vessel no recording knows by
                // identity. Exactly the stage-1 walk: name, then the guid filter (which can
                // still drop a same-name recording of a conclusively different launch), then
                // the tiers; the XP leg keeps the stage-2 ambiguity refusal.
                path = RecoveryPickPath.NameFallback;
                skippedZombieNotCommitted = zombieByName;
                if (nameMatches.Count > 0 || identity.HasLaunchGuid)
                {
                    ParsekLog.Info(Tag,
                        $"PickRecoveryRecordingId path: {identity.FormatForLog()} ut={utLog} " +
                        $"path={RecoveryPickAmbiguity.PathToken(path)} " +
                        $"reason={(identity.HasLaunchGuid ? "no-recording-carries-launch-guid" : "live-launch-guid-unknown")} " +
                        $"nameMatches={nameMatches.Count.ToString(CultureInfo.InvariantCulture)}");
                }

                // Stage 1. Drop candidates whose recorded launch guid conclusively differs
                // from the recovering vessel's. Monotone and BEFORE the tier walk.
                candidates = FilterRecoveryCandidatesByLaunchGuid(
                    nameMatches, identity.LaunchGuid, out guidDropped);
            }
            return path;
        }

        /// <summary>
        /// The stage-1 guid filter's outcome line of <see cref="PickRecoveryRecording"/>. Only
        /// the name fallback runs the filter, so the identity paths write nothing here.
        /// </summary>
        internal static void LogRecoveryGuidFilterOutcome(
            RecoveredVesselIdentity identity,
            string utLog,
            RecoveryPickPath path,
            int guidDropped,
            int candidateCount,
            int nameMatchCount)
        {
            // ONE LINE PER PICK, NOT PER RECOVERY. The funds and XP legs pick once, but the
            // science leg picks once per science subject, so a science-heavy recovery emits
            // one of these per subject. That is bounded by the subject count and each line is
            // independently true, so it is left un-rate-limited rather than collapsed - a
            // dropped candidate is an attribution-changing decision on every leg that makes it.
            // Only the name fallback runs the stage-1 filter, so only it logs it.
            if (path == RecoveryPickPath.NameFallback && guidDropped > 0)
            {
                ParsekLog.Info(Tag,
                    $"PickRecoveryRecordingId guid filter: {identity.FormatForLog()} " +
                    $"ut={utLog} " +
                    $"dropped={guidDropped.ToString(CultureInfo.InvariantCulture)} " +
                    $"remaining={candidateCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"reason=guid-conclusive-mismatch");
            }
            else if (path == RecoveryPickPath.NameFallback && nameMatchCount > 0)
            {
                // Nothing dropped - but WHY is the load-bearing half, and a silent no-drop
                // cannot be told apart from a filter that never ran.
                ParsekLog.Verbose(Tag,
                    $"PickRecoveryRecordingId guid filter: {identity.FormatForLog()} " +
                    $"ut={utLog} " +
                    $"dropped=0 remaining={candidateCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"reason={(identity.HasLaunchGuid ? "no-conclusive-mismatch" : "live-launch-guid-unknown")}");
            }
        }

        /// <summary>
        /// The UT tier walk of <see cref="PickRecoveryRecording"/>: tier 1 the bracketing
        /// candidate with the largest EndUT (the admitted session provisional, by reference,
        /// as the tie-break), tier 2 the one that ended most recently at or before ut, tier 3
        /// the global latest. Ties keep the first in candidate order. Pure.
        /// </summary>
        internal static void WalkRecoveryPickTiers(
            List<Recording> candidates,
            double ut,
            Recording admittedSessionProvisional,
            out Recording bracketing,
            out Recording bracketingSessionProvisional,
            out Recording mostRecentEnded,
            out Recording globalLatest)
        {
            bracketing = null;                   // tier 1 (largest EndUT)
            bracketingSessionProvisional = null; // tier 1 tie-break override
            mostRecentEnded = null;              // tier 2
            globalLatest = null;                 // tier 3

            for (int i = 0; i < candidates.Count; i++)
            {
                var rec = candidates[i];
                double startUT = rec.StartUT;
                double endUT = rec.EndUT;

                // Tier 1: brackets ut
                if (startUT <= ut && ut <= endUT)
                {
                    if (bracketing == null || endUT > bracketing.EndUT)
                        bracketing = rec;
                    // TOMBSTONE-BRACKET-TIE-MID-SESSION-PAYOUT: the live session's
                    // provisional outranks largest-EndUT inside tier 1. It is the fork
                    // that SURVIVES the merge, so a mid-session payout tagged to it is
                    // out of the supersede subtree instead of being tombstoned away.
                    // Identity comes from `admittedSessionProvisional`, which is a member
                    // of `candidates` BY CONSTRUCTION - see the post-filter note above.
                    // ReferenceEquals, deliberately: an id comparison here would re-derive
                    // the provisional inside the tier walk and could match a recording the
                    // guid filter dropped, which is exactly what that note forbids.
                    if (ReferenceEquals(rec, admittedSessionProvisional))
                        bracketingSessionProvisional = rec;
                }

                // Tier 2: ended at or before ut
                if (endUT <= ut)
                {
                    if (mostRecentEnded == null || endUT > mostRecentEnded.EndUT)
                        mostRecentEnded = rec;
                }

                // Tier 3: global latest
                if (globalLatest == null || endUT > globalLatest.EndUT)
                    globalLatest = rec;
            }
        }
    }
}
