using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // Strip, activate and marker: the post-load strip / activate / marker run, the hold on resurrected vessels with a committed future, the post-strip spawn reconcile and the stage manager rebuild.
    internal static partial class RewindInvoker
    {
        private static void RunStripActivateMarker(
            RewindPoint rp,
            ChildSlot selected,
            string sessionId,
            int slotIdx,
            string tempPath,
            double retireCutoffUT)
        {
            try
            {
                // Step 2: post-load strip (§6.4 step 4).
                PostLoadStripResult stripResult;
                try
                {
                    // stripUnmatchedVessels stays FALSE so the LeftAlone branch
                    // is live: a vessel matching no slot in this RP is unrelated
                    // to the re-fly and design doc §6.4 step 4 says leave it
                    // alone. Strict mode here (plus the pre-load scrub) used to
                    // delete the player's whole fleet - stations, satellites,
                    // rovers, discovered asteroids/comets and planted flags -
                    // and made the Flag carve-out below unreachable. Prior-career
                    // debris that trips stock patched conics is removed by the
                    // narrow #587 name-matched pass, not by this flag.
                    stripResult = PostLoadStripper.Strip(
                        rp,
                        slotIdx,
                        stripUnmatchedVessels: false);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Invocation failed: strip threw: {ex.Message}");
                    ShowUserError($"Rewind failed: post-load strip error ({ex.Message})");
                    return;
                }

                if (stripResult.SelectedPid == 0u || stripResult.SelectedVessel == null)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Activate failed: selected vessel not present on reload " +
                        $"rp={rp.RewindPointId} slot={slotIdx}");
                    ShowUserError("Rewind failed: selected vessel not present in quicksave");
                    return;
                }

                // Step 3: activate selected child's vessel (§6.4 step 5).
                try
                {
                    FlightGlobals.SetActiveVessel(stripResult.SelectedVessel);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Activate failed: SetActiveVessel threw: {ex.Message}");
                    ShowUserError($"Rewind failed: could not activate vessel ({ex.Message})");
                    return;
                }

                // Step 3a (#16 item 7): a DISABLED slot's vessel is still in the world.
                // PostLoadStripper keys on PidSlotMap, and a slot disabled at author time
                // ("no-live-vessel") has no entry there, so the strip cannot see its craft
                // even though the RP's own topology says that sibling belongs to this split.
                // The old strict strip removed it as collateral; with the fleet preserved it
                // now stays, and it is BOTH a real vessel and a replaying ghost. Every other
                // non-selected slot has its vessel removed and its recording continued as
                // history, so this restores that invariant for PID-STABLE disabled slots -
                // narrowly, via the launch-identity gate, so no unrelated preserved craft is
                // touched. A disabled slot's craft live under a DIFFERENT vessel pid (docking pid
                // churn between recording and RP capture) is outside the pass's reach and stays
                // as it is today; see the ResolveDisabledSlotVesselsToStrip remarks.
                //
                // Runs AFTER Activate deliberately: the selected vessel is the active one by
                // then, so no Die() here can land on the vessel the player is about to fly
                // even if some future refactor loosened the pid exclusions. Its kills are
                // appended to stripResult.StrippedPids so the reconcile below subtracts them
                // too - Vessel.Die() does not remove the matching ProtoVessel, so a disabled
                // slot's recording would otherwise keep reading as "still spawned".
                try
                {
                    var disabledSlotKills = StripDisabledSlotVessels(
                        rp, slotIdx, stripResult.SelectedPid);
                    if (disabledSlotKills.Count > 0 && stripResult.StrippedPids != null)
                        stripResult.StrippedPids.AddRange(disabledSlotKills);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Disabled-slot strip supplement threw (non-fatal): {ex.Message}");
                }

                // Reconcile committed recordings whose SpawnedVesselPersistentId
                // points at a vessel the strip just removed (#168 analogue for
                // the Re-Fly load path). A prior merge can leave a sibling
                // recording stamped with a persisted-vessel PID that survived
                // into the save; Re-Fly's PostLoadStripper.Strip then deletes
                // that vessel along with every other non-selected sibling, but
                // without this reconcile pass the recording stays
                // VesselSpawned=true and ShouldSpawnAtRecordingEnd's PID dedup
                // gate blocks the engine from re-spawning the ghost at its
                // terminal endpoint.
                //
                // Survivor-set contract: PostLoadStripper.Strip removes vessels
                // via Vessel.Die() but does NOT remove the matching ProtoVessel
                // from HighLogic.CurrentGame.flightState.protoVessels — that
                // list is the save-shape mirror and does not auto-sync with
                // Vessel.Die(). So a survivor set built from protoVessels alone
                // still contains every stripped capsule's PID, which masks the
                // bug ShouldResetSpawnState is supposed to detect. So
                // ReconcilePostStripSpawnState subtracts stripResult.StrippedPids
                // explicitly, and routes (pid, launch-guid) identities to the
                // GUID-AWARE reconcile overload — the bare-pid one cannot tell a
                // relaunch of the same craft from the recorded launch, which the
                // preserved-fleet world makes a common case.
                try
                {
                    var fsReconcile = HighLogic.CurrentGame?.flightState;
                    if (fsReconcile != null)
                    {
                        var committed = RecordingStore.CommittedRecordings;
                        if (committed != null && committed.Count > 0)
                        {
                            // (pid, launch-guid) identities, NOT bare pids: see
                            // ReconcilePostStripSpawnState. Same collector the
                            // revert path uses (ParsekScenario.cs:3376).
                            var protoVesselIdentities =
                                ParsekScenario.CollectSurvivingVesselIdentities(
                                    fsReconcile.protoVessels);

                            // The survivor-set + log + reconcile glue is extracted
                            // into ReconcilePostStripSpawnState so it is directly
                            // xUnit-testable. The Unity-only ProtoVessel walk above
                            // stays here because ProtoVessel cannot be built in
                            // xUnit.
                            ReconcilePostStripSpawnState(
                                protoVesselIdentities, stripResult.StrippedPids, committed);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Post-strip spawn-state reconcile threw (non-fatal): {ex.Message}");
                }

                // Step 4: §6.3 step 4 phases 1 + 2 — atomic provisional + marker write.
                // NO yield, NO await between checkpoints A and B.
                try
                {
                    AtomicMarkerWrite(rp, selected, stripResult, sessionId);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Invocation failed: atomic marker write threw: {ex.Message}");
                    ShowUserError($"Rewind failed: marker write error ({ex.Message})");
                    return;
                }

                // Step 4b (design section 2.12 Rule 3): hand every survivor a committed
                // recording flies, docks with, boards or recovers after the rewind UT to the
                // claimed-vessel hold. The strip preserved unrelated vessels, so a craft the
                // committed future uses later (or recovers) is live again; it is despawned and
                // drawn as a hold ghost until its first committed use, and its committed rows
                // (a later recovery included) stay. Nothing is tombstoned at invoke: the earlier
                // retirement of such a vessel's recovery rows ran outside any merge, bypassed the
                // drawdown guard and outlived a Discard. Runs after the post-strip reconcile, so
                // the spawn stamps the hold releases are already settled, and after the marker
                // write, so the session's own replaced flight is known (its docks hold nothing)
                // and the held entries are saved with the marker for a reload to redraw.
                try
                {
                    HoldResurrectedVesselsWithCommittedFuture(stripResult, retireCutoffUT, sessionId);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Resurrected vessel hold threw (non-fatal): {ex.Message}");
                }

                // Phase 2 (ghost rendering pipeline, design doc §6.3 / §17.2 /
                // §18 Phase 2): rebuild the in-memory anchor ε map from the
                // freshly-written marker. The catch is HR-9 visible-failure —
                // a thrown exception inside the rebuild must not abort the
                // re-fly itself; we already own a valid marker on disk so the
                // session is live regardless of rendering-side anchor state.
                try
                {
                    Parsek.Rendering.RenderSessionState.RebuildFromMarker(
                        ParsekScenario.Instance?.ActiveReFlySessionMarker);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Pipeline-Session",
                        $"RebuildFromMarker threw (non-fatal): {ex.Message}");
                }

                // Step 5: post-atomic ledger recalc.
                // Pass `double.MaxValue` as the cutoff so every action in
                // the reconciled ledger applies, and — critically — so
                // `bypassPatchDeferral` flips true inside `RecalculateAndPatch`.
                // Without that bypass, `LedgerOrchestrator` skips tech-tree
                // patching when no cutoff is supplied, and the live KSP R&D
                // state stays at the OLD RP quicksave's pre-rewind tech set.
                // Tech unlocks made after the RP would silently disappear,
                // violating the design's "career state sticks" rule (§2.3
                // of the rewind-staging design doc). `double.MaxValue`
                // means "no time filter" — the full ledger walks and every
                // tech unlock re-applies to KSP's R&D after the quicksave's
                // old state overwrote it.
                //
                // Rewind read-back divergence guard (audit rec #1): arm the guard with the
                // two economy witnesses immediately before the recalc that PatchAll runs.
                // E_before = the pre-rewind live career parked on the context before
                // LoadGame; E_rp = the live economy NOW (post-load = the loaded quicksave /
                // OLD at-RP economy). The floor = min(E_before, E_rp); the recalc target is
                // flagged (and optionally aborted) only when it drops below that floor. The
                // finally clears the guard so it never stays armed past this boundary, even
                // if the recalc throws — it must never leak onto an ordinary recalc patch.
                EconomySnapshot eBefore =
                    RewindInvokeContext.ReadbackPreRewind ?? new EconomySnapshot();
                EconomySnapshot eRp = EconomySnapshot.Capture();
                RewindReadbackGuard.Arm(eBefore, eRp);
                try
                {
                    // The cutoff is a horizon, not a clock: contract deadlines are judged
                    // at the session's own UT (design section 2.12 Rule 3), so a deadline
                    // still ahead of the session does not read as expired.
                    LedgerOrchestrator.RecalculateAndPatch(
                        double.MaxValue, deadlineClockUT: retireCutoffUT);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Post-invoke ledger recalculate threw (non-fatal): {ex.Message}");
                }
                finally
                {
                    RewindReadbackGuard.Clear();
                }

                ParsekLog.Info(InvokeTag,
                    $"Invocation complete: sess={sessionId} rp={rp.RewindPointId} " +
                    $"slot={slotIdx} activePid={stripResult.SelectedPid}");

                // Bug #587: pre-existing debris-vessel-supplement to PostLoadStripper.Strip.
                // Runs AFTER AtomicMarkerWrite so the marker-aware short-circuit in
                // ParsekPlaybackPolicy.RunSpawnDeathChecks is engaged and our Die()
                // calls cannot leak back into the policy as "spawned vessel died,
                // please re-spawn" (#573 contract). Pure no-op when the marker is
                // not in-place continuation (placeholder pattern keeps the live
                // pre-rewind active vessel in scene; killing matching debris there
                // would risk taking the player's actively-re-flown vessel).
                StripPreExistingDebrisForInPlaceContinuation(stripResult);

                // Bug: KSP's StageManager stack ends up unresponsive after a
                // ProtoVessel.Load when the underlying quicksave was captured
                // mid-decoupling-tick (the case for any Parsek breakup-RP).
                // The saved `vessel.currentStage` ends up referencing a stage
                // slot whose decoupler is gone from the post-decouple part
                // list, so the next [Space] press fires a no-op stage and the
                // auto-advance logic only runs on launchpad initial load.
                // KSPCommunityFixes has no fix for this and stock has none
                // either. Calling Vessel.ResumeStaging() — the same KSP API
                // that ProtoVessel.Load itself invokes during initial load —
                // forces the StageManager to re-discover the stack from the
                // current part hierarchy. Idempotent + harmless when the
                // stack was already correct, so we run it on every Re-Fly
                // load (in-place AND placeholder paths). See open-bug entry
                // "capsule upper-stage staging unresponsive after Re-Fly load".
                ForceStageManagerRebuildAfterReFlyLoad(stripResult, sessionId);

                // Diagnostic hint: a pre-existing quicksave vessel whose name
                // matches a recording in the re-fly tree produces two
                // identical-looking objects in the scene (real orbital relic +
                // playback ghost). The 01:53 playtest hit exactly this with a
                // prior-career "Kerbal X" at 162 km. Warn so the player knows
                // not to blame the re-fly pipeline — the real vessel predates
                // the rewind and is outside the tree.
                WarnOnLeftAloneNameCollisions(stripResult);

                // Vessel names (design 7.6): the Re-Fly attempt starts a new flight epoch (its
                // Discard / Retry cancel exactly its stock renames), and the post-load live write
                // pass runs now that the strip has finished.
                VesselNameLiveSync.OnReFlyStripComplete(sessionId, UnityEngine.Time.realtimeSinceStartup);
            }
            finally
            {
                // Always drop the temp quicksave copy and clear the context,
                // whether we succeeded or failed. A leftover Parsek_Rewind_*.sfs
                // in the save root is user-visible clutter.
                TryDeleteTemp(tempPath);
                RewindInvokeContext.Clear();
            }
        }

        /// <summary>
        /// Design section 2.12 Rule 3: hands every survivor of the Re-Fly load that a committed
        /// recording flies, docks with, boards or recovers after <paramref name="rewindUT"/> to
        /// the claimed-vessel hold (<see cref="ReFlyInvokeHold"/>,
        /// <c>ParsekFlight.HoldReFlyInvokeVessels</c>) and saves the held entries on the live
        /// session marker. The re-flown vessel and the stripped vessels are never held, and a
        /// dock or board by a recording that no longer plays (superseded, or the session's own
        /// replaced flight) holds nothing. Logs one line per held vessel and one summary.
        /// </summary>
        internal static void HoldResurrectedVesselsWithCommittedFuture(
            PostLoadStripResult stripResult, double rewindUT, string sessionId)
        {
            var flightState = HighLogic.CurrentGame?.flightState;
            if (flightState == null || flightState.protoVessels == null)
            {
                ParsekLog.Verbose(InvokeTag, "Resurrected vessel hold: no flightState - skipping");
                return;
            }
            var flight = ParsekFlight.Instance;
            if (object.ReferenceEquals(null, flight))
            {
                ParsekLog.Verbose(InvokeTag, "Resurrected vessel hold: no ParsekFlight instance - skipping");
                return;
            }

            var survivors = ParsekScenario.CollectSurvivingVesselIdentities(flightState.protoVessels);
            var excluded = BuildInvokeHoldExcludedPids(stripResult);
            var supersededClaimants = GhostChainWalker.BuildSupersededClaimantMap(
                ParsekScenario.Instance?.RecordingSupersedes);
            int claimsSkipped;
            List<ReFlyInvokeHold.ChangeClaim> changeClaims = ReFlyInvokeHold.CollectChangeClaims(
                RecordingStore.CommittedTrees, rewindUT,
                id => supersededClaimants.ContainsKey(id) || SessionSuppressionState.IsSuppressed(id),
                out claimsSkipped);
            int matchedWithoutFuture;
            List<ReFlyHoldEntry> entries = ReFlyInvokeHold.Classify(
                survivors, excluded, RecordingStore.CommittedRecordings, Ledger.Actions,
                changeClaims, rewindUT, out matchedWithoutFuture);

            int notLive, alreadyHeld;
            List<ReFlyHoldEntry> held = flight.HoldReFlyInvokeVessels(entries, out notLive, out alreadyHeld);
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            if (marker != null)
                marker.InvokeHolds = new List<ReFlyHoldEntry>(held);

            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < held.Count; i++)
            {
                ParsekLog.Info(InvokeTag,
                    $"Strip heldForCommittedFuture v={held[i].Pid.ToString(ic)} rec={held[i].RecordingId} " +
                    $"firstUT={held[i].FirstClaimUT.ToString("F1", ic)} evidence={held[i].Evidence} " +
                    $"sess={sessionId ?? "<no-id>"}");
            }
            ParsekLog.Info(InvokeTag,
                $"Resurrected vessel hold: survivors={survivors.Count.ToString(ic)} " +
                $"held={held.Count.ToString(ic)} notLive={notLive.ToString(ic)} " +
                $"alreadyHeld={alreadyHeld.ToString(ic)} " +
                $"matchedWithoutFuture={matchedWithoutFuture.ToString(ic)} " +
                $"cutoffUT={rewindUT.ToString("F1", ic)} " +
                $"changeClaims={changeClaims.Count.ToString(ic)} claimsSkipped={claimsSkipped.ToString(ic)} " +
                $"savedOnMarker={(marker != null)}");
        }

        /// <summary>Pure: the pids the invoke hold never takes (the re-flown vessel, the stripped vessels).</summary>
        internal static HashSet<uint> BuildInvokeHoldExcludedPids(PostLoadStripResult stripResult)
        {
            var excluded = new HashSet<uint>();
            if (stripResult.SelectedPid != 0u)
                excluded.Add(stripResult.SelectedPid);
            if (stripResult.StrippedPids != null)
            {
                for (int i = 0; i < stripResult.StrippedPids.Count; i++)
                    excluded.Add(stripResult.StrippedPids[i]);
            }
            return excluded;
        }

        /// <summary>
        /// Post-strip spawn-state reconcile for the Re-Fly load path.
        /// <para>
        /// Extracted from <see cref="RunStripActivateMarker"/> so the
        /// "identities minus stripped PIDs -> survivor set -> summary log ->
        /// reconcile" wiring is directly unit-testable. The Unity-only step
        /// (enumerating <c>HighLogic.CurrentGame.flightState.protoVessels</c>)
        /// stays at the call site because <c>ProtoVessel</c> cannot be
        /// constructed in xUnit; this method takes the pre-collected lists.
        /// </para>
        /// <para>
        /// Takes (pid, launch-guid) IDENTITIES rather than bare pids and routes to
        /// the guid-aware <c>ReconcileSpawnStateAfterStrip</c> overload. The bare-pid
        /// overload is wrong here for the reason CLAUDE.md pins: <c>persistentId</c>
        /// is craft-baked, not launch-unique, so a survivor that merely shares a
        /// recording's <c>SpawnedVesselPersistentId</c> may be a DIFFERENT launch of
        /// the same craft. Under a pid-only decision such a survivor keeps the
        /// recording at <c>VesselSpawned=true</c> pointing at a stranger, and
        /// <c>ShouldSpawnAtRecordingEnd</c>'s dedup gate then blocks the terminal
        /// ghost spawn permanently - nothing recovers it, because the liveness
        /// re-check is itself pid-only. That was masked while the Re-Fly load left
        /// exactly one vessel in scene; preserving unrelated vessels makes the
        /// collision reachable, and a relaunch of the same craft is the common case.
        /// </para>
        /// </summary>
        /// <returns>The number of committed recordings whose spawn state was reset.</returns>
        internal static int ReconcilePostStripSpawnState(
            IReadOnlyList<(uint pid, string guid)> protoVesselIdentities,
            IReadOnlyList<uint> strippedPids,
            IReadOnlyList<Recording> committed)
        {
            if (committed == null || committed.Count == 0)
                return 0;

            int protoCount = protoVesselIdentities != null ? protoVesselIdentities.Count : 0;
            int strippedCount = strippedPids != null ? strippedPids.Count : 0;

            // PostLoadStripper.Strip removes vessels via Vessel.Die() but does NOT
            // remove the matching ProtoVessel from flightState.protoVessels, so the
            // stripped pids must be subtracted explicitly or they read as survivors
            // and mask the very bug ShouldResetSpawnState detects.
            var strippedSet = new HashSet<uint>();
            if (strippedPids != null)
            {
                for (int i = 0; i < strippedPids.Count; i++)
                    strippedSet.Add(strippedPids[i]);
            }

            var survivors = new List<(uint pid, string guid)>();
            if (protoVesselIdentities != null)
            {
                for (int i = 0; i < protoVesselIdentities.Count; i++)
                {
                    var identity = protoVesselIdentities[i];
                    if (identity.pid == 0u) continue;
                    if (strippedSet.Contains(identity.pid)) continue;
                    survivors.Add(identity);
                }
            }

            int withGuid = 0;
            for (int i = 0; i < survivors.Count; i++)
            {
                if (!string.IsNullOrEmpty(survivors[i].guid))
                    withGuid++;
            }

            ParsekLog.Info(InvokeTag,
                $"Post-strip reconcile: strippedPids={strippedCount} " +
                $"protoVesselsRemaining={protoCount} survivorPidCount={survivors.Count} " +
                $"survivorsWithLaunchGuid={withGuid}");

            return ParsekScenario.ReconcileSpawnStateAfterStrip(survivors, committed);
        }

        /// <summary>
        /// After a successful Strip, cross-reference <see cref="PostLoadStripResult.LeftAlonePidNames"/>
        /// against the committed-recording vessel names in the scenario's
        /// trees. A match means the player has a pre-existing quicksave
        /// vessel sharing a name with a recording in the active tree — they
        /// will see both in-scene and almost always mistake the real vessel
        /// for a second ghost. WARN-log + ScreenMessage so the situation is
        /// diagnosable without reading KSP.log.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three playtest-driven bug fixes are layered into this helper:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <see cref="PostLoadStripper.FindTreeNameCollisions"/> dedupes by
        /// name, so 3 actual <c>Kerbal X Debris</c> instances were summarized
        /// as <c>Strip left 1 pre-existing vessel(s)</c> — wrong instance
        /// count. Fixed by reporting vessel-instance and unique-name counts
        /// separately.
        /// </description></item>
        /// <item><description>
        /// <see cref="StripPreExistingDebrisForInPlaceContinuation"/> runs
        /// before this WARN and may have killed every colliding vessel via
        /// <see cref="Vessel.Die"/>; the WARN still emitted because it read
        /// the original pre-kill <c>LeftAloneNames</c> list, falsely
        /// reporting collisions that no longer exist. Fixed by re-checking
        /// pid liveness against <see cref="FlightGlobals.Vessels"/> before
        /// counting.
        /// </description></item>
        /// <item><description>
        /// PR #577 P2 review: a name-only re-survey of <c>FlightGlobals.Vessels</c>
        /// counts EVERY live vessel whose name lands in the colliding set —
        /// including the actively re-flown vessel (<c>SelectedPid</c>),
        /// any vessel the strip just killed but whose death event hasn't
        /// drained from the list, and ghost ProtoVessels created by
        /// <see cref="GhostMapPresence"/>. Any of those producing a name
        /// collision would re-introduce the misleading WARN this very fix
        /// is trying to suppress. Fixed by scoping the resurvey to the
        /// (pid, name) pairs the stripper actually left alone, and
        /// belt-and-suspenders excluding <c>SelectedPid</c>,
        /// <c>StrippedPids</c>, and any pid that
        /// <see cref="GhostMapPresence.IsGhostMapVessel(uint)"/> reports.
        /// </description></item>
        /// </list>
        /// </remarks>
        /// <summary>
        /// Workaround for a stock-KSP bug where loading a vessel from a
        /// quicksave captured DURING a decoupling event leaves the
        /// <c>StageManager</c> stack in a state where the next <c>[Space]</c>
        /// press fires a no-op. <c>ProtoVessel.Load</c> already calls
        /// <see cref="Vessel.ResumeStaging"/> on initial load, but the
        /// `currentStage` value saved to disk references a slot whose
        /// decoupler is already gone from the post-decouple part list, so
        /// the rebuilt stack ends up empty for the player's "next" stage.
        /// Calling <c>ResumeStaging</c> again here re-runs the discovery
        /// against the now-stable post-load part hierarchy and is
        /// idempotent when the stack was already correct.
        ///
        /// <para>Observed in <c>logs/2026-05-06_2308_staging-broken-after-first-flight</c>:
        /// the upper-stage Re-Fly recorded 18.4 s of flight with zero engine
        /// or decoupler events because every <c>[Space]</c> press hit a no-op
        /// stage. KSPCommunityFixes has no fix for this; web search returned
        /// no upstream report. Tracked as the open todo entry "capsule
        /// upper-stage staging unresponsive after Re-Fly load".</para>
        /// </summary>
        private static void ForceStageManagerRebuildAfterReFlyLoad(
            PostLoadStripResult stripResult, string sessionId)
        {
            Vessel vessel = null;
            try
            {
                vessel = stripResult.SelectedVessel ?? FlightGlobals.ActiveVessel;
            }
            catch
            {
                vessel = null;
            }
            if (vessel == null)
            {
                ParsekLog.Verbose(InvokeTag,
                    $"ForceStageManagerRebuildAfterReFlyLoad: no live vessel — skipping " +
                    $"sess={sessionId ?? "<no-id>"}");
                return;
            }

            try
            {
                int priorStage = vessel.currentStage;
                vessel.ResumeStaging();
                ParsekLog.Info(InvokeTag,
                    $"ForceStageManagerRebuildAfterReFlyLoad: vessel.ResumeStaging() invoked " +
                    $"vesselPid={vessel.persistentId} priorCurrentStage={priorStage} " +
                    $"postCurrentStage={vessel.currentStage} sess={sessionId ?? "<no-id>"} " +
                    "(workaround for stock KSP staging-after-mid-decouple-quicksave bug)");
            }
            catch (Exception ex)
            {
                // Non-fatal: ResumeStaging is the workaround, not the
                // primary mechanism. If KSP throws here we still want the
                // Re-Fly to proceed; the player can re-stage manually.
                ParsekLog.Warn(InvokeTag,
                    $"ForceStageManagerRebuildAfterReFlyLoad: vessel.ResumeStaging() threw " +
                    $"{ex.GetType().Name}: {ex.Message} — continuing without rebuild " +
                    $"sess={sessionId ?? "<no-id>"}");
            }
        }
    }
}
