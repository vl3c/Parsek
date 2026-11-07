using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Past-end completion, loop first-run spawn and the spawn-at-recording-end decision.
    internal static partial class GhostPlaybackLogic
    {
        /// <summary>
        /// Bug #433: decide whether a skipped-ghost trajectory should still fire
        /// PlaybackCompleted at past-end. Only the PlaybackEnabled=false cause is
        /// career-neutral (visibility toggle) and must drive the policy's spawn
        /// branch; !hasData / externalVesselSuppressed are structural and must
        /// silently skip as before.
        ///
        /// Mirrors the visible-path contract in GhostPlaybackEngine at two points:
        ///   - "has renderable data" matches HasRenderableGhostData (Points OR
        ///     OrbitSegments OR SurfacePos) so orbit-only and surface-only
        ///     recordings still complete when hidden.
        ///   - past-end comparisons are strict (`&gt;`), same as the visible path
        ///     at GhostPlaybackEngine.cs `pastEnd = ctx.currentUT &gt; traj.EndUT`
        ///     and `pastEffectiveEnd = ctx.currentUT &gt; f.chainEndUT`, so the
        ///     toggle does not shift completion timing by a frame.
        ///
        /// Pure predicate — accepts pre-collected set-membership booleans so the
        /// engine can pass `HashSet.Contains` results without exposing the set.
        /// </summary>
        internal static bool ShouldFireHiddenPastEndCompletion(
            IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags,
            double currentUT,
            bool completionAlreadyFired,
            bool earlyDebrisCompletion)
        {
            if (traj == null) return false;
            if (completionAlreadyFired || earlyDebrisCompletion) return false;
            if (traj.PlaybackEnabled) return false; // only the visibility-hidden cause
            if (!GhostPlaybackEngine.HasRenderableGhostData(traj)) return false;
            bool pastEnd = currentUT > traj.EndUT;
            bool pastEffectiveEnd = currentUT > flags.chainEndUT;
            return pastEnd || pastEffectiveEnd;
        }

        /// <summary>
        /// Loop first-run-is-real (design 2.2 principle 9, 12.7; operator ruling
        /// 2026-09-23 on LOOP-ARMED-REWIND-LEAVES-ZERO-VESSELS): the first chronological
        /// run of a looping recording is the real run, so when the live playhead crosses
        /// the recording's own EndUT its terminal vessel spawns exactly as a non-looping
        /// recording's would. A looping index (a mission loop-unit member, or a
        /// per-recording LoopPlayback) is routed to its loop renderer ABOVE the ordinary
        /// past-end completion in flight and at the Space Center, so this is the only
        /// spawn seam it has, and loop cycles never reach it.
        ///
        /// "First" is the recording's timeline position, not a cycle count: the check
        /// keys on the real UT against the recording's EndUT, never on a loop clock.
        /// Every other gate is the normal one, folded into <paramref name="spawnEligible"/>
        /// by the caller (VesselSpawned, the replay-scope history gate, the chain rules,
        /// the #573 rewind block), so after the one spawn
        /// VesselSpawned=true keeps every later cycle ghost-only, and a Rewind-to-Launch
        /// strip (which resets VesselSpawned) re-arms exactly one spawn.
        /// <paramref name="alreadyAttempted"/> is the caller's one-shot latch, cleared
        /// when the playhead returns before the recording.
        ///
        /// Returns false for a non-looping index: the ordinary past-end completion owns it.
        /// </summary>
        internal static bool ShouldAttemptLoopFirstRunSpawn(
            bool loopDriven,
            bool spawnEligible,
            double currentUT,
            double recordingEndUT,
            bool alreadyAttempted)
        {
            if (!loopDriven) return false;
            if (alreadyAttempted) return false;
            if (!spawnEligible) return false;
            return currentUT > recordingEndUT;
        }

        /// <summary>
        /// The one Info line for a chain-with-a-looped-phase tip spawn (design 12.7, operator
        /// ruling 2026-09-27 on LOOPING-CHAIN-FIRST-RUN-TIP-NEVER-SPAWNS): the chain's first
        /// run is real, so its tip spawns once and later loop replays stay ghost-only.
        /// Returns null when <paramref name="chainLooping"/> is false (an ordinary spawn,
        /// which its scene logs on its own). Invariant culture: tests read it.
        /// </summary>
        internal static string FormatChainLoopFirstRunSpawn(
            string scene, int index, Recording rec, double currentUT, bool chainLooping)
        {
            if (!chainLooping || rec == null) return null;
            var ic = CultureInfo.InvariantCulture;
            return "Chain loop first-run spawn: #" + index.ToString(ic)
                + " \"" + (rec.VesselName ?? "?") + "\" id=" + (rec.RecordingId ?? "(none)")
                + " chain=" + (rec.ChainId ?? "(none)")
                + " tipLooping=" + (rec.LoopPlayback ? "true" : "false")
                + " scene=" + (scene ?? "?")
                + " UT=" + currentUT.ToString("F2", ic)
                + " endUT=" + rec.EndUT.ToString("F2", ic)
                + " (a chain with a looped phase keeps its first run real: the tip spawns once,"
                + " later loop replays stay ghost-only)";
        }

        /// <summary>
        /// Logs <see cref="FormatChainLoopFirstRunSpawn"/> when it applies. Rate-limited per
        /// recording so a spawn that retries (a blocked or deferred spawn) prints once.
        /// </summary>
        internal static void LogChainLoopFirstRunSpawn(
            string subsystem, string scene, int index, Recording rec, double currentUT, bool chainLooping)
        {
            string line = FormatChainLoopFirstRunSpawn(scene, index, rec, currentUT, chainLooping);
            if (line == null) return;
            ParsekLog.InfoRateLimited(
                subsystem, "chain-loop-first-run|" + (rec.RecordingId ?? "(none)"), line);
        }

        /// <summary>
        /// BUG-B replay-scope gate, split by purpose. A loop member (explicit live-replay
        /// opt-in) RENDERS regardless of replay scope, but its terminal SPAWN is the first
        /// run's real spawn and obeys the same scope gate as any recording
        /// (<see cref="ShouldAttemptLoopFirstRunSpawn"/>). An active re-fly session is
        /// exempt from both. <paramref name="scopeHistorical"/> is
        /// <c>PlaybackScopeTracker.IsHistoricalNeverReplayed</c> for the recording.
        /// </summary>
        internal static bool ResolveHistoricalNeverReplayed(
            bool loopingLike,
            bool reFlyActive,
            bool scopeHistorical,
            bool forSpawn)
        {
            if (reFlyActive) return false;
            if (loopingLike && !forSpawn) return false;
            return scopeHistorical;
        }

        #region Spawn-at-Recording-End Decision

        /// <summary>
        /// Determines whether spawn should be suppressed because the recording
        /// is an intermediate link in a ghost chain (not the chain tip), or
        /// because the chain is terminated (vessel destroyed/recovered).
        /// Returns (suppressed, reason). Called BEFORE ShouldSpawnAtRecordingEnd
        /// by the playback controller (Phase 6b).
        /// </summary>
        internal static (bool suppressed, string reason) ShouldSuppressSpawnForChain(
            Dictionary<uint, GhostChain> chains, Recording rec)
        {
            if (chains == null || chains.Count == 0)
                return (false, "");

            if (GhostChainWalker.IsIntermediateChainLink(chains, rec))
            {
                // Per-frame per-recording — rate-limit to avoid log spam
                ParsekLog.VerboseRateLimited("ChainWalker", $"chain-suppress-{rec.RecordingId}",
                    $"Intermediate spawn suppressed: rec={rec.RecordingId} vessel={rec.VesselName}");
                return (true, "intermediate ghost chain link");
            }

            var chain = GhostChainWalker.FindChainForVessel(chains, rec.VesselPersistentId);
            if (chain != null && chain.IsTerminated && chain.TipRecordingId == rec.RecordingId)
            {
                ParsekLog.VerboseRateLimited("ChainWalker",
                    "terminated-spawn-" + rec.VesselPersistentId,
                    string.Format(CultureInfo.InvariantCulture,
                        "Terminated chain spawn suppressed: rec={0} vessel={1} vesselPid={2}",
                        rec.RecordingId, rec.VesselName, rec.VesselPersistentId));
                return (true, "terminated ghost chain");
            }

            return (false, "");
        }

        /// <summary>
        /// Terminal states that produce a real vessel via ShouldSpawnAtRecordingEnd
        /// when no other suppression applies. The set is intentionally narrow:
        /// every other terminal (SubOrbital, Destroyed, Recovered, Docked, Boarded,
        /// and any future addition) means the ghost playback is the final visible
        /// trajectory — nothing replaces it.
        ///
        /// Used both here (gating spawn) and in
        /// RecordingOptimizer.TailPreservesTerminalSpawnState (gating tail trim):
        /// trimming the boring tail is only safe when a spawned vessel takes over
        /// from the trim UT onward. For non-spawnable terminals the tail IS the
        /// playback the player sees, so it must be preserved.
        /// </summary>
        internal static bool IsSpawnableTerminal(TerminalState ts)
        {
            switch (ts)
            {
                case TerminalState.Orbiting:
                case TerminalState.Landed:
                case TerminalState.Splashed:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Maps a snapshot's <c>sit</c> value (a KSP <c>Vessel.Situations</c> NAME, which
        /// is what <c>ProtoVessel.Save</c> writes) to the <see cref="TerminalState"/> the
        /// finalize path would have stamped for that situation. Returns false when the
        /// value is missing or is not a situation this build knows.
        ///
        /// <para><b>This table is a MIRROR of
        /// <c>RecordingTree.DetermineTerminalState(int)</c> and must not drift from it.</b>
        /// That method is the one the Finalized route runs, keyed by the situation's
        /// integer value; this one is keyed by the NAME because an un-finalized recording
        /// has no live vessel left to ask — only its snapshot. The tie is enforced
        /// MECHANICALLY rather than by this comment:
        /// <c>SpawnSafetyNetTests.SituationNameMapping_MirrorsDetermineTerminalState</c>
        /// walks every <c>Vessel.Situations</c> value and asserts the two agree, so adding
        /// a situation to one and not the other reds. The int-keyed method additionally
        /// Warn-logs its unknown default; this one stays silent and returns false, because
        /// it is called from a per-frame spawn predicate.</para>
        ///
        /// <para>The caller decides what an unmappable value means.
        /// <see cref="ShouldSpawnAtRecordingEnd"/> treats it as NO EVIDENCE and leaves
        /// its answer unchanged — see the scope-boundary comment there. Note this
        /// differs from the int-keyed method, which Warn-logs and defaults unknown to
        /// <c>SubOrbital</c>; it has a live vessel and so a real reading to fall back
        /// on, where an absent snapshot field is the absence of a reading.</para>
        /// </summary>
        internal static bool TryMapSituationNameToTerminalState(
            string situationName, out TerminalState terminal)
        {
            terminal = TerminalState.SubOrbital;
            if (string.IsNullOrEmpty(situationName))
                return false;

            switch (situationName.Trim().ToUpperInvariant())
            {
                case "ORBITING":
                    terminal = TerminalState.Orbiting;
                    return true;
                case "LANDED":
                case "PRELAUNCH":
                    terminal = TerminalState.Landed;
                    return true;
                case "SPLASHED":
                    terminal = TerminalState.Splashed;
                    return true;
                case "FLYING":
                case "SUB_ORBITAL":
                case "ESCAPING":
                    terminal = TerminalState.SubOrbital;
                    return true;
                case "DOCKED":
                    terminal = TerminalState.Docked;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure decision logic for whether a recording's vessel should be spawned
        /// at the end of its ghost playback (the "spawn-at-recording-end" feature).
        /// Extracted from ParsekFlight.UpdateTimelinePlayback for testability.
        ///
        /// Returns (needsSpawn, reason) where reason explains why spawn was suppressed.
        /// Empty reason means spawn is allowed.
        /// </summary>
        /// <param name="rec">The recording to evaluate.</param>
        /// <param name="isActiveChainMember">True if the recording belongs to the chain currently being built.</param>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtRecordingEnd(
            Recording rec,
            bool isActiveChainMember)
        {
            return ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember,
                treeContext: null);
        }

        /// <param name="liveSameLaunchVesselPresent">
        /// True when a live (non-ghost) vessel of the recording's craft is currently
        /// in the scene. Only the flight-scene caller can evaluate this, so it defaults
        /// to true (the conservative value that keeps the #573 same-recording block
        /// absolute) for every other caller. It gates the standalone Rewind-to-Launch
        /// target lift in <see cref="ShouldBlockSpawnForRewindSuppression"/>.
        /// </param>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtRecordingEnd(
            Recording rec,
            bool isActiveChainMember,
            RecordingTree treeContext,
            bool liveSameLaunchVesselPresent = true)
        {
            if (!string.IsNullOrEmpty(rec.TerminalSpawnSupersededByRecordingId))
            {
                return (false,
                    "terminal spawn superseded by recording " +
                    rec.TerminalSpawnSupersededByRecordingId);
            }

            // Plain Rewind-to-Launch source protection (#573). The same-recording
            // marker blocks the rewound target so its old vessel cannot respawn next
            // to a live re-flight of that launch. The block now lifts for a STANDALONE
            // target when no live same-craft vessel is present (the player rewound to
            // launch and then did NOT re-fly it) so the recorded vessel still
            // materializes at its terminal. Future same-tree recordings are never
            // marked (#589); chain targets and active re-flights stay blocked.
            if (ShouldBlockSpawnForRewindSuppression(
                    rec, liveSameLaunchVesselPresent, out string rewindSuppressionReason))
            {
                return (false, rewindSuppressionReason);
            }

            // Preserve the existing "already spawned" precedence, but make destroyed
            // recordings win over the generic missing-snapshot diagnostic.
            if (rec.VesselSpawned)
            {
                return (false, "already spawned (VesselSpawned=true)");
            }
            if (rec.VesselDestroyed)
            {
                return (false, "vessel destroyed");
            }
            // Base condition: must have a vessel snapshot to materialize a real
            // vessel. The in-memory copy is a transient cache that several sites
            // null out in-session (vessel-gone debris, the crew-unreserve pass);
            // the durable copy lives in the _vessel.craft sidecar. Re-hydrate it
            // from disk for genuinely-spawnable, non-debris recordings only. The
            // checks below reject debris / non-spawnable terminals / branch > 0 /
            // non-leaf recordings regardless, so they skip the disk probe and keep
            // the cheap early-out (no per-frame I/O). Without this, a spawnable
            // leaf whose snapshot was dropped (e.g. an orbital payload re-flown
            // after a Rewind-to-Launch) would silently fail to re-materialize.
            if (rec.VesselSnapshot == null)
            {
                bool worthHydrating = !rec.IsDebris
                    && rec.TerminalStateValue.HasValue
                    && IsSpawnableTerminal(rec.TerminalStateValue.Value);
                if (!worthHydrating
                    || !RecordingSidecarStore.TryHydrateVesselSnapshotFromSidecar(rec))
                {
                    return (false, "no vessel snapshot");
                }
            }

            // Branch > 0 recordings are ghost-only (undock continuations) — never spawn
            if (rec.ChainBranch > 0)
            {
                return (false, "branch > 0 (ghost-only)");
            }

            // Suppress spawning for recordings belonging to a chain currently being built
            if (isActiveChainMember)
            {
                return (false, "active chain being built");
            }

            // Looping recordings: first playthrough spawns the vessel (so it exists in the world),
            // subsequent loops are visual-only. The VesselSpawned/SpawnedVesselPersistentId checks
            // above handle this — after first spawn, VesselSpawned=true prevents re-spawning.
            // No blanket LoopPlayback suppression needed here. The looping render paths
            // reach this decision through ShouldAttemptLoopFirstRunSpawn.
            //
            // The same holds for a chain with a looped phase (design 12.7, operator ruling
            // 2026-09-27): its first run is real, so its tip spawns once at the end of that
            // run and later loop replays stay ghost-only. There is deliberately no
            // "chain looping" refusal here; which segment spawns is the tip rule (mid-chain
            // segments are refused by each scene's chain-tip gate).

            // Breakup-continuous check: the foreground recording continued past a breakup
            // (ProcessBreakupEvent sets ChildBranchPointId without creating a same-PID
            // continuation). If no child shares this vessel's PID, the recording IS the
            // effective leaf and should be spawnable. Only applies to non-debris recordings
            // with a spawnable terminal state (Landed/Splashed/Orbiting). (#224)
            bool hasSpawnableTerminal = rec.TerminalStateValue.HasValue
                && IsSpawnableTerminal(rec.TerminalStateValue.Value);
            bool effectiveLeaf = rec.ChildBranchPointId != null
                && !rec.IsDebris
                && hasSpawnableTerminal
                && IsEffectiveLeafForVessel(rec, treeContext);

            // Non-leaf tree recordings should never spawn — they branched into a
            // same-vessel continuation that carries the correct snapshot.
            if (rec.ChildBranchPointId != null && !effectiveLeaf)
            {
                return (false, "non-leaf tree recording");
            }

            // Safety net: even if ChildBranchPointId is null, check the resolved tree
            // for recordings that are parents of a branch point. Covers edge cases where
            // ChildBranchPointId was not set (e.g., serialization gaps). (#114)
            // Skip for effective-leaf recordings — the branch point exists but the recording
            // is still the leaf for its vessel.
            if (!effectiveLeaf && IsNonLeafInTree(rec, treeContext))
            {
                return (false, "non-leaf in tree (safety net)");
            }

            // Debris recordings are visual-only (short TTL, no meaningful vessel to persist)
            if (rec.IsDebris)
            {
                return (false, "debris recording (visual-only)");
            }

            // Terminal states: destroyed/recovered/docked/boarded/suborbital should not spawn
            // SubOrbital includes FLYING and ESCAPING — vessel would materialize mid-air and crash (#45)
            if (rec.TerminalStateValue.HasValue
                && !IsSpawnableTerminal(rec.TerminalStateValue.Value))
            {
                return (false, $"terminal state {rec.TerminalStateValue.Value}");
            }

            // Recovered after commit (operator ruling 2026-10-01): a recording whose
            // vessel was recovered ends with NO vessel, exactly like a Recovered
            // terminal, even when it was committed before the recovery fired and so
            // still reads Landed / Splashed / Orbiting (a Tracking Station or KSC-marker
            // recovery of an older flight, or a save from before the in-flight Recover
            // committed Recovered). The recovery row credits the payout once at its UT;
            // spawning the vessel too would let the player recover it a second time.
            if (EffectiveState.IsRecoveredByLedgerRowLive(rec))
            {
                return (false,
                    "recovered after commit (recovery ledger row, terminal " +
                    (rec.TerminalStateValue.HasValue ? rec.TerminalStateValue.Value.ToString() : "<none>") +
                    ")");
            }

            // Snapshot situation check: if the snapshot's sit field is FLYING or SUB_ORBITAL,
            // KSP's on-rails aero check (101.3 kPa) immediately destroys spawned vessels.
            // This catches cases where TerminalState is null/Landed but the snapshot was
            // captured mid-flight. (#114)
            // Override: if the terminal is spawnable (Landed/Splashed/Orbiting), the
            // vessel DID reach a safe state — the snapshot's sit field may be stale
            // from recording start. Orbiting: vessel captured during ascent (FLYING)
            // but achieved orbit. The spawn path corrects the snapshot situation
            // before spawning. (#169, #EVA-spawn)
            bool terminalOverridesUnsafe = rec.TerminalStateValue.HasValue
                && IsSpawnableTerminal(rec.TerminalStateValue.Value);
            if (!terminalOverridesUnsafe && IsSnapshotSituationUnsafe(rec.VesselSnapshot))
            {
                return (false, "snapshot situation unsafe (FLYING/SUB_ORBITAL)");
            }

            // COMPLETING the check above for un-finalized recordings. The
            // IsSpawnableTerminal rejection ~30 lines up sits inside
            // `rec.TerminalStateValue.HasValue`, so a recording with NO terminal state
            // never reaches it — and the only other situation gate is the one above,
            // which knows FLYING/SUB_ORBITAL and nothing else. That left ESCAPING and
            // DOCKED (and an unreadable `sit`) reading as spawnable on the un-finalized
            // path while the Finalized route ghost-onlys them by design: ESCAPING maps
            // to SubOrbital, DOCKED to Docked, neither of which IsSpawnableTerminal
            // admits. Un-finalized recordings became a live population when the
            // outside-FLIGHT auto-commit started committing Limbo resume-stashes at
            // fidelity (ParsekScenario.ClassifyAutoCommitFidelity), so the gap is
            // reachable: an interplanetary probe on an escape trajectory, quickloaded
            // and then left in FLIGHT, would otherwise be spawn-eligible.
            //
            // The snapshot's own situation is the only evidence left of what the
            // finalize path would have decided, so mirror that decision exactly rather
            // than inventing a second policy. NOT REACHED for FLYING/SUB_ORBITAL (the
            // check above already returned, keeping its pinned reason string) — those
            // stay in the mapping table only so the table is complete and can be
            // drift-guarded against RecordingTree.DetermineTerminalState.
            //
            // This rejects SPAWNING, not the snapshot: the auto-commit's promise is
            // that a silent commit never DESTROYS a snapshot that exists, not that
            // every snapshot spawns. A ghost-only'd leaf keeps its GhostVisualSnapshot
            // and can still be spawned deliberately later.
            //
            // DELIBERATE SCOPE BOUNDARY: an ABSENT or unrecognised `sit` is left on
            // today's behaviour (allowed) rather than tightened to "reject on no
            // evidence". Every real snapshot carries the field — they are written by
            // FlightRecorder.TryBackupSnapshot -> ProtoVessel.Save — so the population
            // without one is synthetic test fixtures, of which 16 cells across 5 files
            // pin the current answer, `MergeDialogVesselTests.CanPersistVessel_-
            // NullTerminalState_ReturnsTrue` by name. Tightening it is a separable
            // change with its own blast radius and no demonstrated reachable case;
            // folding it in here would flip a named contract as a side effect of a
            // targeted fix. Pinned as unchanged-by-design by
            // SpawnSafetyNetTests.UnfinalizedRecording_AbsentSit_IsUnchangedByDesign.
            if (!rec.TerminalStateValue.HasValue)
            {
                TerminalState mirroredTerminal;
                if (TryMapSituationNameToTerminalState(
                        rec.VesselSnapshot.GetValue("sit"), out mirroredTerminal)
                    && !IsSpawnableTerminal(mirroredTerminal))
                {
                    return (false,
                        "unfinalized recording, snapshot situation maps to " +
                        $"terminal {mirroredTerminal}");
                }
            }

            // PID dedup: if vessel was already spawned (PID recorded), never re-spawn.
            // On revert, SpawnedVesselPersistentId resets to 0 from quicksave so reverts still work.
            if (rec.SpawnedVesselPersistentId != 0)
            {
                return (false, $"already spawned (pid={rec.SpawnedVesselPersistentId})");
            }

            return (true, "");
        }

        internal const string LiveFlownRecordingSpawnReason =
            "live-flown recording (re-fly session / live recorder)";

        /// <summary>
        /// True when <paramref name="recordingId"/> names the recording the player is
        /// flying right now: the active Re-Fly session's recording
        /// (<c>ReFlySessionMarker.ActiveReFlyRecordingId</c>) or the recording the live
        /// recorder writes. That recording's vessel IS the live vessel, so its terminal
        /// spawn must never fire. A save during a Re-Fly flushes the live points into the
        /// session's committed provisional, which then reads as ended, and the hidden
        /// past-end completion would otherwise spawn a real duplicate of the vessel being
        /// flown (todo REFLY-FORK-SELF-SPAWN-AFTER-QUICKSAVE). Null / empty ids never match.
        /// </summary>
        internal static bool IsLiveFlownRecording(
            string recordingId,
            string activeReFlyRecordingId,
            string liveRecorderRecordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            return string.Equals(recordingId, activeReFlyRecordingId, StringComparison.Ordinal)
                || string.Equals(recordingId, liveRecorderRecordingId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Spawner-side decision over <see cref="IsLiveFlownRecording"/>: refuse the
        /// end-of-recording spawn of the recording being flown, naming which id matched.
        /// </summary>
        internal static bool ShouldRefuseLiveFlownSpawn(
            string recordingId,
            string activeReFlyRecordingId,
            string liveRecorderRecordingId,
            out string reason)
        {
            reason = null;
            if (!IsLiveFlownRecording(recordingId, activeReFlyRecordingId, liveRecorderRecordingId))
                return false;
            bool reFly = string.Equals(recordingId, activeReFlyRecordingId, StringComparison.Ordinal);
            bool recorder = string.Equals(recordingId, liveRecorderRecordingId, StringComparison.Ordinal);
            reason = "recording is being flown (" +
                (reFly && recorder ? "re-fly session + live recorder"
                    : reFly ? "re-fly session" : "live recorder") +
                ") - its vessel is the live vessel, not spawning a duplicate";
            return true;
        }

        /// <summary>
        /// The flight scene's final terminal-spawn answer for one committed recording:
        /// <see cref="ShouldSpawnAtRecordingEnd"/>'s verdict
        /// (<paramref name="spawnEligible"/>) minus every scene-level hold. Pure so the
        /// composition, the live-flown hold included, is pinned by unit tests.
        /// </summary>
        internal static bool ComposeFlightFinalNeedsSpawn(
            bool spawnEligible,
            bool chainSuppressed,
            bool supersededByRelation,
            bool rewindRetired,
            bool spawnHistoricalNeverReplayed,
            bool liveFlownRecording)
        {
            return spawnEligible
                && !chainSuppressed
                && !supersededByRelation
                && !rewindRetired
                && !spawnHistoricalNeverReplayed
                && !liveFlownRecording;
        }

        /// <summary>
        /// Structural "this recording is where its vessel ends its WHOLE flight" test: the
        /// segment-shape gates of <see cref="ShouldSpawnAtRecordingEnd"/> without its
        /// spawn-STATE gates (already spawned, destroyed, snapshot presence, rewind
        /// suppression, PID dedup), so the answer is stable before, during and after the
        /// end-of-recording spawn is settled. False for a recording whose terminal spawn a
        /// later continuation owns, a debris / branch &gt; 0 recording, an
        /// intermediate chain segment, and a non-leaf tree
        /// recording (it branched into a same-vessel continuation: a later chain or
        /// switch-continuation segment carries the flight on). A breakup-continuous
        /// recording that is the effective leaf for its vessel counts as final. Used by the
        /// crew side of the KSC retirement ruling so it and the spawn side agree on which
        /// stop is the final one.
        /// </summary>
        internal static bool IsFinalSpawnSegment(Recording rec, RecordingTree treeContext = null)
        {
            if (rec == null)
                return false;
            if (!string.IsNullOrEmpty(rec.TerminalSpawnSupersededByRecordingId))
                return false;
            if (rec.IsDebris || rec.ChainBranch > 0)
                return false;
            // A looped phase does not make the chain's tip non-final: the first run is
            // real (design 12.7), the same answer ShouldSpawnAtRecordingEnd gives.
            if (!string.IsNullOrEmpty(rec.ChainId) && RecordingChainQueries.IsChainMidSegment(rec))
                return false;

            bool hasSpawnableTerminal = rec.TerminalStateValue.HasValue
                && IsSpawnableTerminal(rec.TerminalStateValue.Value);
            bool effectiveLeaf = rec.ChildBranchPointId != null
                && hasSpawnableTerminal
                && IsEffectiveLeafForVessel(rec, treeContext);
            if (rec.ChildBranchPointId != null && !effectiveLeaf)
                return false;
            if (!effectiveLeaf && IsNonLeafInTree(rec, treeContext))
                return false;
            return true;
        }

        /// <summary>
        /// Pure predicate: returns true when a recording must NOT spawn at its
        /// terminal end because plain Rewind-to-Launch scoped a #573 active/source
        /// suppression marker onto it. The only marker reason produced today is
        /// <see cref="ParsekScenario.RewindSpawnSuppressionReasonSameRecording"/>
        /// (the rewind-target recording stripped during rewind).
        ///
        /// The block exists so the rewound vessel cannot respawn next to a live
        /// re-flight of the same launch (#573). It is NOT unconditional: it lifts for
        /// a STANDALONE target (<see cref="Recording.ChainId"/> empty) when
        /// <paramref name="liveSameLaunchVesselPresent"/> is false — i.e. the player
        /// rewound to launch and then flew something else, so there is no re-flight to
        /// collide with and the recorded vessel should still materialize at its
        /// terminal. Chain targets stay blocked (a continuation tip can resurrect via
        /// the chain-tip spawn path, which is the #573 phantom class) and any target
        /// stays blocked while a live same-craft vessel is present (a genuine re-fly).
        /// Other clearing paths remain: the explicit watch-entry lift
        /// (<see cref="ParsekScenario.TryClearSpawnSuppressionOnWatchEntry"/>) and the
        /// next rewind/revert reset. This is a query: it does not mutate the recording
        /// or log.
        /// </summary>
        private static bool ShouldBlockSpawnForRewindSuppression(
            Recording rec,
            bool liveSameLaunchVesselPresent,
            out string reason)
        {
            reason = "";
            if (rec == null || !rec.SpawnSuppressedByRewind)
                return false;

            if (string.Equals(rec.SpawnSuppressedByRewindReason,
                    ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                    StringComparison.Ordinal))
            {
                // Lift for a standalone rewind target the player did not re-fly: no
                // live same-craft vessel exists, so spawning the recorded terminal
                // cannot duplicate a re-flight. Fall through to the normal spawn gates.
                bool standalone = string.IsNullOrEmpty(rec.ChainId);
                if (standalone && !liveSameLaunchVesselPresent)
                    return false;

                reason = "spawn suppressed post-rewind (same-recording active/source protection, #573)";
                return true;
            }

            return false;
        }

        internal static void SetLiveSameCraftOverrideForTesting(Func<Recording, bool> finder)
        {
            liveSameCraftOverride = finder;
        }

        internal static void ResetLiveSameCraftOverrideForTesting()
        {
            liveSameCraftOverride = null;
        }

        /// <summary>
        /// True when a live, non-ghost vessel of the recording's craft is currently in the
        /// scene. Uses the craft-baked persistentId deliberately: this is a "would a spawn
        /// collide with a live re-flight of this craft" check, not a same-launch identity
        /// claim, so it must also catch a relaunch of the same craft (which carries the
        /// baked pid but a fresh launch Guid — do NOT route this through
        /// <see cref="VesselLaunchIdentity"/>, which is guid-gated and would wrongly lift
        /// during an active re-fly). Parsek's own map-presence ghosts are excluded via
        /// <see cref="GhostMapPresence.IsGhostMapVessel"/>. Scene-agnostic: it scans
        /// <c>FlightGlobals.Vessels</c>, which lists live vessels in Flight and the
        /// Tracking Station; at the Space Center that list may be empty or null, and the
        /// null guard then reports no live craft (the correct outcome there, since a
        /// genuine re-flight only exists in the flight scene and can never collide at KSC).
        /// Deliberately an uncached fresh scan (unlike <see cref="RealVesselExists"/>); it
        /// runs at most once per frame because
        /// <see cref="ResolveRewindSuppressionLiveLaunchPresence"/> short-circuits before
        /// calling it for every non-marked recording.
        ///
        /// <para>
        /// RE-CONFIRMED as pid-only against the preserved-fleet world (#16 triage): now that
        /// Re-Fly preserves unrelated vessels, a same-craft STRANGER can hold the block closed
        /// and a standalone Rewind-to-Launch target then never materializes. That is the
        /// accepted side, not a bug to gate away, because the live re-flight this block exists
        /// for IS a same-craft stranger by construction — a fresh launch of the rewound craft
        /// carries the baked pid and a NEW Guid, so a Guid gate here would lift the block during
        /// exactly the re-fly it must hold for and resurrect the #573 duplicate. The narrower
        /// failure (one un-materialized terminal while a same-craft vessel is loaded) is
        /// recoverable through the watch-entry lift and the next rewind/revert reset; the
        /// duplicate is not. The block-kept case is logged (see
        /// <see cref="ResolveRewindSuppressionLiveLaunchPresence"/>) so it is diagnosable in
        /// KSP.log rather than silent.
        /// </para>
        /// </summary>
        internal static bool AnyLiveRealVesselSharesRecordedCraft(Recording rec)
        {
            if (rec == null)
                return false;
            // Consult the test seam before the pid / FlightGlobals guards so a test can
            // assert any recording's outcome without a live Unity vessel list.
            if (liveSameCraftOverride != null)
                return liveSameCraftOverride(rec);
            if (rec.VesselPersistentId == 0)
                return false;
            var vessels = FlightGlobals.Vessels;
            if (vessels == null)
                return false;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null)
                    continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    continue;
                if (v.persistentId == rec.VesselPersistentId)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Scene-agnostic resolver for whether the #573 same-recording spawn-suppression
        /// block should stay absolute for <paramref name="rec"/>. Returns true
        /// (conservative: keep blocking) for everything except a standalone
        /// Rewind-to-Launch target with no live same-craft vessel in the scene; for that
        /// case it returns false to authorize the lift in
        /// <see cref="ShouldSpawnAtRecordingEnd"/> so the recorded vessel materializes at
        /// its terminal. The Flight, Tracking Station and Space Center spawn-at-end paths
        /// all route through this so they behave identically (the original fix was
        /// flight-only). The marker rides at most one recording, so the FlightGlobals scan
        /// runs only for that recording. Never mutates the recording; emits one
        /// VerboseRateLimited line on a lift so it is observable in every scene's KSP.log.
        /// </summary>
        internal static bool ResolveRewindSuppressionLiveLaunchPresence(Recording rec)
        {
            if (rec == null
                || !rec.SpawnSuppressedByRewind
                || !string.Equals(
                        rec.SpawnSuppressedByRewindReason,
                        ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                        StringComparison.Ordinal)
                || !string.IsNullOrEmpty(rec.ChainId))
            {
                // Not a liftable standalone same-recording target — the predicate does
                // not consult the value (no block, or a non-liftable block), so keep it
                // conservative without paying for a vessel scan.
                return true;
            }

            bool present = AnyLiveRealVesselSharesRecordedCraft(rec);
            if (!present)
            {
                ParsekLog.VerboseRateLimited(
                    "Rewind",
                    rec.RecordingId,
                    $"same-recording spawn suppression lifted for standalone rewind target " +
                    $"rec={rec.RecordingId} vessel=\"{rec.VesselName}\" pid={rec.VesselPersistentId} — " +
                    "no live same-craft vessel present (plain Rewind-to-Launch not re-flown); " +
                    "recorded terminal will materialize (#573/#589)");
            }
            else
            {
                // The block stays closed. Deliberately pid-only (see
                // AnyLiveRealVesselSharesRecordedCraft): the live same-craft vessel may be the
                // genuine re-flight this block exists for, OR — since Re-Fly preserves the
                // fleet — an unrelated launch of the same craft that merely reuses the baked
                // pid. Both keep the block; this line is what makes the second case
                // diagnosable when a player reports "my rewound vessel never came back".
                ParsekLog.VerboseRateLimited(
                    "Rewind",
                    "suppression-held|" + (rec.RecordingId ?? "(none)"),
                    $"same-recording spawn suppression HELD for standalone rewind target " +
                    $"rec={rec.RecordingId} vessel=\"{rec.VesselName}\" pid={rec.VesselPersistentId} — " +
                    "a live vessel shares this craft's persistentId (re-flight, or a preserved " +
                    "same-craft launch); recorded terminal stays unspawned (#573)",
                    5.0);
            }
            return present;
        }

        /// <summary>
        /// Spawn eligibility for Real Spawn Control's nearby-candidate list: the ghost's
        /// recording has not ended yet, it would spawn at its end, and no ghost chain
        /// suppresses it. The #573 rewind lift is resolved the same way as the playback
        /// spawn (<see cref="ResolveRewindSuppressionLiveLaunchPresence"/>), so a standalone
        /// Rewind-to-Launch target the player did not re-fly is listed, not only spawned
        /// once its ghost runs out.
        /// </summary>
        internal static bool IsNearbySpawnCandidateEligible(
            Recording rec, double currentUT, Dictionary<uint, GhostChain> chains)
        {
            if (rec == null || rec.EndUT <= currentUT)
                return false;
            // The spawner skips these outright (VesselSpawner.SpawnOrRecoverIfTooClose), so a
            // Warp to them could only move the clock.
            if (IsSpawnSettledUnspawnable(rec))
                return false;
            var (needsSpawn, _) = ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, treeContext: null,
                ResolveRewindSuppressionLiveLaunchPresence(rec));
            if (!needsSpawn)
                return false;
            var (suppressed, _) = ShouldSuppressSpawnForChain(chains, rec);
            return !suppressed;
        }

        /// <summary>
        /// True when the spawner will refuse the recording before trying: the terminal orbit
        /// is latched as unable to spawn safely, or the load already failed
        /// <see cref="VesselSpawner.MaxSpawnAttempts"/> times. Neither is part of
        /// <see cref="ShouldSpawnAtRecordingEnd"/>, which keeps them out of the list.
        /// </summary>
        internal static bool IsSpawnSettledUnspawnable(Recording rec)
        {
            return rec != null
                && (rec.TerminalSpawnCannotSpawnSafely
                    || rec.SpawnAttempts >= VesselSpawner.MaxSpawnAttempts);
        }

        /// <summary>
        /// KSC-specific spawn eligibility check. Simplified version of the Flight scene's
        /// spawn decision: at KSC there is no active chain being built, so isActiveChainMember
        /// is always false. Chain looping/disabled state is derived from RecordingStore.
        /// Returns (needsSpawn, reason) — same semantics as ShouldSpawnAtRecordingEnd.
        /// </summary>
        internal static (bool needsSpawn, string reason) ShouldSpawnAtKscEnd(Recording rec)
        {
            // During rewind, Planetarium UT is still the pre-rewind future value until
            // the deferred coroutine fires. Block all spawns to prevent future vessels
            // from being re-created before the clock is wound back.
            if (RecordingStore.RewindUTAdjustmentPending)
                return (false, "rewind UT adjustment pending — Planetarium UT not yet corrected");

            return ShouldSpawnAtKscEnd(rec, Planetarium.GetUniversalTime());
        }

        internal static (bool needsSpawn, string reason) ShouldSpawnAtKscEnd(Recording rec, double currentUT)
        {
            // Don't spawn vessels whose recording hasn't finished yet at the current UT (#rewind-persistence)
            if (currentUT < rec.EndUT)
                return (false, $"current UT {currentUT:F0} before recording end {rec.EndUT:F0}");

            // Orbiting/Docked vessels cannot survive pv.Load() in the Space Center scene —
            // KSP crashes them through terrain within frames. Defer to flight scene spawn
            // where SpawnAtPosition can place them correctly. (#171)
            if (rec.TerminalStateValue == TerminalState.Orbiting
                || rec.TerminalStateValue == TerminalState.Docked)
                return (false, $"orbital vessel deferred to flight scene (terminal={rec.TerminalStateValue})");

            // At KSC, no chain is being built → isActiveChainMember = false
            // Intermediate chain segments should not spawn — only the chain tip spawns.
            // In Flight, ShouldSuppressSpawnForChain handles this via runtime GhostChain
            // state, but at KSC there are no GhostChain objects. Use the committed data.
            if (RecordingChainQueries.IsChainMidSegment(rec))
                return (false, "intermediate chain segment (not tip)");

            // Scene-agnostic #573 rewind lift: a standalone Rewind-to-Launch target the
            // player did not re-fly (no live same-craft vessel present) still spawns its
            // recorded terminal at KSC, identical to Flight and the Tracking Station.
            return ShouldSpawnAtRecordingEnd(
                rec, false,
                treeContext: null,
                ResolveRewindSuppressionLiveLaunchPresence(rec));
        }

        /// <summary>
        /// Safety-net check: determines whether a recording is a non-leaf node in a
        /// committed tree by scanning the tree's branch points for parent references.
        /// This catches cases where ChildBranchPointId was not set on the recording
        /// (e.g., serialization gaps, edge-case commit paths) but the tree structure
        /// shows the recording has children. (#114)
        /// Static method, testable via RecordingStore.CommittedTrees setup.
        /// </summary>
        internal static bool IsNonLeafInCommittedTree(Recording rec)
        {
            return IsNonLeafInTree(rec, treeContext: null);
        }

        internal static bool IsNonLeafInTree(Recording rec, RecordingTree treeContext)
        {
            RecordingTree tree;
            if (!TryResolveTreeContext(rec, treeContext, out tree))
                return false;

            // Check if any branch point lists this recording as a parent. A kerbal side
            // branch (a ground-part placement, a foreign vessel's inventory transfer) is
            // skipped: its parent (the kerbal) keeps recording past it and stays the leaf of
            // its own flight (design sections 4.11 and 4.12).
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                var bp = tree.BranchPoints[b];
                if (bp == null || RecordingTree.IsKerbalSideBranch(bp.Type))
                    continue;
                // A branch point a split moved onto a later chain segment that the vessel flew
                // on past does not end the segment either.
                if (bp.ParentRecordingIds != null && bp.ParentRecordingIds.Contains(rec.RecordingId)
                    && !SplitParentLinks.IsFlownPastOnLaterSegment(rec, bp))
                {
                    string treeLabel = !string.IsNullOrEmpty(tree.Id) ? tree.Id : (tree.TreeName ?? "(pending)");
                    ParsekLog.VerboseRateLimited("Spawner",
                        $"safety-net-{rec.RecordingId}",
                        string.Format(CultureInfo.InvariantCulture,
                            "IsNonLeafInTree: recording {0} is parent of branch point {1} " +
                            "in tree {2} (ChildBranchPointId was null — safety net triggered)",
                            rec.RecordingId, bp.Id, treeLabel), 30.0);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true when a recording with ChildBranchPointId is the effective leaf
        /// for its vessel — no child recording of that branch point shares the same
        /// VesselPersistentId. This happens for breakup-continuous foreground recordings
        /// where ProcessBreakupEvent sets ChildBranchPointId without creating a same-PID
        /// continuation (debris-only breakups on splashdown/landing). (#224)
        /// </summary>
        internal static bool IsEffectiveLeafForVessel(Recording rec)
        {
            return IsEffectiveLeafForVessel(rec, treeContext: null);
        }

        internal static bool IsEffectiveLeafForVessel(Recording rec, RecordingTree treeContext)
        {
            if (string.IsNullOrEmpty(rec.ChildBranchPointId))
                return false;

            RecordingTree tree;
            if (!TryResolveTreeContext(rec, treeContext, out tree))
                return false;

            // Find the branch point
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                var bp = tree.BranchPoints[b];
                if (bp.Id != rec.ChildBranchPointId) continue;

                // Check if any child recording shares the same vessel PID
                for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                {
                    Recording childRec;
                    if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out childRec))
                    {
                        if (childRec.VesselPersistentId == rec.VesselPersistentId)
                            return false; // Same-PID continuation exists — NOT effective leaf
                    }
                }

                // No child shares this vessel PID — recording IS the effective leaf
                ParsekLog.VerboseRateLimited("Spawner",
                    rec.RecordingId,
                    string.Format(CultureInfo.InvariantCulture,
                        "IsEffectiveLeafForVessel: recording {0} vessel={1} is effective leaf " +
                        "(breakup-continuous, no same-PID continuation child)",
                        rec.RecordingId, rec.VesselPersistentId));
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="rec"/> is the last recording of its vessel even though it
        /// carries <c>ChildBranchPointId</c>: the branch point is in <paramref name="tree"/>, no
        /// child of it shares the recording's vessel PID, and the recording kept recording past
        /// the split (<see cref="RecordingContinuedPastBranchPoint"/>). That is the
        /// breakup-continuous shape <c>ParsekFlight.WireBreakupIntoTree</c> writes for a focused
        /// breakup or decouple, and the post-switch Launch branch whose parent kept recording in
        /// the background. A split that closes its parent (same-PID child, or a parent trimmed to
        /// the branch UT) answers false, as does any branch type other than Breakup /
        /// JointBreak / Launch (<see cref="RecordingTree.BranchTypeCanLeaveParentRecording"/>)
        /// and anything unresolvable.
        /// </summary>
        internal static bool RecordingContinuesPastChildBranch(Recording rec, RecordingTree tree)
        {
            BranchPoint bp;
            return TryGetContinuedPastChildBranch(rec, tree, out bp);
        }

        internal static bool TryGetContinuedPastChildBranch(
            Recording rec, RecordingTree tree, out BranchPoint childBranchPoint)
        {
            childBranchPoint = null;
            if (rec == null || tree == null || tree.BranchPoints == null
                || string.IsNullOrEmpty(rec.ChildBranchPointId)
                || rec.VesselPersistentId == 0)
                return false;

            BranchPoint bp = null;
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                if (tree.BranchPoints[b] != null && tree.BranchPoints[b].Id == rec.ChildBranchPointId)
                {
                    bp = tree.BranchPoints[b];
                    break;
                }
            }
            if (bp == null || !RecordingTree.BranchTypeCanLeaveParentRecording(bp.Type))
                return false;

            if (bp.ChildRecordingIds != null && tree.Recordings != null)
            {
                for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                {
                    Recording childRec;
                    if (tree.Recordings.TryGetValue(bp.ChildRecordingIds[c], out childRec)
                        && childRec != null
                        && childRec.VesselPersistentId == rec.VesselPersistentId)
                        return false;
                }
            }

            if (!RecordingContinuedPastBranchPoint(rec, bp))
                return false;

            childBranchPoint = bp;
            return true;
        }

        private static bool TryResolveTreeContext(
            Recording rec,
            RecordingTree treeContext,
            out RecordingTree tree)
        {
            tree = null;
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId) || !rec.IsTreeRecording)
                return false;

            if (treeContext != null)
            {
                bool sameTreeId = !string.IsNullOrEmpty(treeContext.Id) && treeContext.Id == rec.TreeId;
                bool containsRecording = treeContext.Recordings != null
                    && treeContext.Recordings.ContainsKey(rec.RecordingId);
                if (sameTreeId || containsRecording)
                {
                    tree = treeContext;
                    return true;
                }
            }

            var trees = RecordingStore.CommittedTrees;
            for (int t = 0; t < trees.Count; t++)
            {
                if (trees[t].Id == rec.TreeId)
                {
                    tree = trees[t];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a vessel snapshot's situation is unsafe for spawning.
        /// FLYING and SUB_ORBITAL vessels are immediately killed by KSP's on-rails
        /// atmospheric pressure check (101.3 kPa at sea level). (#114)
        /// Pure static method for testability.
        /// </summary>
        internal static bool IsSnapshotSituationUnsafe(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null) return false;

            string sit = vesselSnapshot.GetValue("sit");
            if (string.IsNullOrEmpty(sit)) return false;

            // KSP situation strings: LANDED, SPLASHED, PRELAUNCH, FLYING,
            // SUB_ORBITAL, ORBITING, ESCAPING, DOCKED
            return sit.Equals("FLYING", System.StringComparison.OrdinalIgnoreCase)
                || sit.Equals("SUB_ORBITAL", System.StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
