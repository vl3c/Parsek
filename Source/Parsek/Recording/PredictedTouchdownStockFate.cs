using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// What <see cref="PredictedTouchdownStockFate.Decide"/> concluded for one committed
    /// recording that ends in a chute touchdown Parsek predicted (Landed / Splashed with
    /// <see cref="Recording.TerminalPredicted"/>).
    /// </summary>
    internal enum PredictedTouchdownOutcome
    {
        // Not a predicted Landed / Splashed ending: nothing to reconcile.
        NotPredictedTouchdown,
        // The recording carries no launch guid; a bare pid never identifies the vessel.
        NoLaunchGuid,
        // A live session owns the recording (its tree is the active or pending tree, a Re-Fly
        // runs, or a live tree docked / boarded the vessel): that session's commit decides.
        OwnedElsewhere,
        // The replay already spawned the recording's vessel (a Parsek spawn, not the adoption
        // of the recorded vessel): the spawned vessel's fate is its own.
        MaterializedBySpawn,
        // The scene has not run long enough for stock's deletion check to have had its chance.
        SceneNotSettled,
        // The player flies the vessel: the takeover path (owner ruling Q1b) owns it.
        VesselIsActive,
        // (a) The vessel is down (landed or splashed): the prediction agrees with stock.
        Consistent,
        // Still in the air before the predicted touchdown (plus the margin): stock decides.
        StockStillDeciding,
        // In the air past the touchdown, loaded and unpacked: stock physics decides right now.
        StockStillSimulating,
        // In the air past the touchdown, on rails, still falling toward stock's deletion line.
        StockMayStillRemove,
        // Gone, and the player recovered or terminated it (a ledger row says so, Q13).
        RemovedByPlayer,
        // Gone, but stock was never seen holding it in this timeline (a rewind to before it,
        // an older recording): the replay materializes it at the touchdown as usual.
        NotInThisTimeline,
        // Contradicted on this pass; acted on once a later pass sees the same again.
        AwaitingConfirmation,
        // (b) Stock held the vessel in this timeline and it is gone with no recovery, merge or
        // termination: stock deleted it (Vessel.CheckKill, crew killed). Owner ruling Q4d.
        StockDeleted,
        // (c) Stock kept the vessel in the air past the touchdown: the prediction was wrong.
        Contradicted,
    }

    /// <summary>The recording half of a predicted-touchdown decision (no live reads).</summary>
    internal struct PredictedTouchdownCandidateFacts
    {
        public bool IsPredictedTouchdown;
        public bool HasLaunchGuid;
        public bool OwnedElsewhere;
        public bool MaterializedBySpawn;
        public bool RemovedByPlayer;
        public double TouchdownUT;
        public double StockHeldUT;
    }

    /// <summary>One <see cref="PredictedTouchdownStockFate.EvaluatePass"/> pass.</summary>
    internal sealed class PredictedTouchdownPassResult
    {
        public readonly List<Recording> StockDeleted = new List<Recording>();
        public readonly List<Recording> Contradicted = new List<Recording>();
        // Candidates whose vessel stock holds right now (the host stamps StockHeldVesselUT).
        public readonly List<Recording> Sighted = new List<Recording>();
        public readonly Dictionary<PredictedTouchdownOutcome, int> Counts =
            new Dictionary<PredictedTouchdownOutcome, int>();
        public int Candidates;
        // A vessel still in the air, or a sighting awaiting confirmation: look again soon.
        public bool NeedsRecheck;
        public bool AwaitingConfirmation;

        internal int Count(PredictedTouchdownOutcome outcome)
        {
            return Counts.TryGetValue(outcome, out int n) ? n : 0;
        }
    }

    /// <summary>
    /// Owner rulings P-STOCK / Q4c / Q4d: a chute descent Parsek predicted to a touchdown while
    /// the craft was still in the game is only a forecast; stock decides in the background.
    /// When stock deletes the craft (<c>Vessel.CheckKill</c>: packed, in the air, above 1 kPa
    /// outside the active vessel's pack range; MurderCrew + Die; no <c>onVesselTerminated</c>)
    /// the committed recording must say what stock did (Destroyed, crew dead, the ghost comes
    /// down under its canopies and ends quietly, nothing spawns), and the end-of-recording
    /// spawn must never bring the deleted craft back.
    /// <para>The discriminator between "stock deleted it" and "this timeline has not reached
    /// it yet" (a rewind to before the flight, whose replay must still spawn the craft at its
    /// touchdown) is <see cref="Recording.StockHeldVesselUT"/>: stamped only from a live read
    /// of the current stock state (the commit while the craft is in the game, and every
    /// reconciler pass that finds it), and cleared by every time-travel load that loads a
    /// stock state from before the stamp (<see cref="ForgetStockHeldAfterTimeTravel"/>). A
    /// stamp therefore means "stock held this vessel in the timeline the game is in now", so
    /// its absence from stock is stock's decision unless a recovery / termination row or a
    /// live merge explains it.</para>
    /// <para>Pure decisions plus two guarded live reads (the guid lookup and the clock), each
    /// behind a test override; the committed-history change is
    /// <c>PredictedCrashReconciler.ApplyStockDeletion</c>, the scene host is
    /// <c>PredictedCrashReconcilerHost</c>.</para>
    /// </summary>
    internal static class PredictedTouchdownStockFate
    {
        internal const string LogTag = "PredictedTouchdownReconcile";

        /// <summary>Game seconds past the predicted touchdown before a craft still in the air
        /// can contradict it (the crash rule's margin).</summary>
        internal const double TouchdownMarginSeconds = 10.0;

        /// <summary>Real seconds between the two passes that must both see the same verdict.</summary>
        internal const double ConfirmRealSeconds = 2.0;

        private const double UtEpsilon = 1e-6;

        /// <summary>Test seam replacing the live lookup: guid (N form) -> live (true), gone
        /// (false) or unknown (null).</summary>
        internal static Func<string, bool?> LiveVesselByGuidOverrideForTesting;

        /// <summary>Test seam replacing the live clock (NaN = unknown).</summary>
        internal static Func<double> NowUTOverrideForTesting;

        internal static void ResetForTesting()
        {
            LiveVesselByGuidOverrideForTesting = null;
            NowUTOverrideForTesting = null;
        }

        /// <summary>Pure: a Landed / Splashed ending that is Parsek's own prediction.</summary>
        internal static bool IsPredictedTouchdown(Recording rec)
        {
            return rec != null
                && rec.TerminalPredicted
                && (rec.TerminalStateValue == TerminalState.Landed
                    || rec.TerminalStateValue == TerminalState.Splashed);
        }

        /// <summary>Pure: the recording's launch guid in N form, null when unknown or empty.</summary>
        internal static string UsableLaunchGuid(Recording rec)
        {
            string guid = VesselLaunchIdentity.NormalizeGuid(rec?.RecordedVesselGuid);
            if (guid == null
                || string.Equals(guid, Guid.Empty.ToString("N", CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase))
                return null;
            return guid;
        }

        /// <summary>
        /// Pure: the replay spawned a vessel for this recording (a Parsek spawn pid). The
        /// adoption of the recorded vessel itself (<c>SpawnedVesselPersistentId ==
        /// VesselPersistentId</c>) is not a spawn: the adopted vessel is the one stock decides
        /// about.
        /// </summary>
        internal static bool IsMaterializedBySpawn(Recording rec)
        {
            return rec != null
                && rec.SpawnedVesselPersistentId != 0
                && rec.SpawnedVesselPersistentId != rec.VesselPersistentId;
        }

        /// <summary>Pure: the recording half of the decision.</summary>
        internal static PredictedTouchdownCandidateFacts BuildCandidate(
            Recording rec, Func<Recording, bool> isOwnedElsewhere, Func<Recording, bool> isRemovedByPlayer)
        {
            var facts = new PredictedTouchdownCandidateFacts
            {
                TouchdownUT = double.NaN,
                StockHeldUT = double.NaN,
            };
            if (!IsPredictedTouchdown(rec))
                return facts;
            facts.IsPredictedTouchdown = true;
            facts.HasLaunchGuid = UsableLaunchGuid(rec) != null;
            facts.OwnedElsewhere = isOwnedElsewhere != null && isOwnedElsewhere(rec);
            facts.MaterializedBySpawn = IsMaterializedBySpawn(rec);
            facts.RemovedByPlayer = isRemovedByPlayer != null && isRemovedByPlayer(rec);
            facts.TouchdownUT = rec.EndUT;
            facts.StockHeldUT = rec.StockHeldVesselUT;
            return facts;
        }

        /// <summary>
        /// Pure: the decision for one recording, rows checked in the order of
        /// <see cref="PredictedTouchdownOutcome"/>. A vanished vessel needs no clock: stock
        /// decides at the deletion, which is before the predicted touchdown.
        /// </summary>
        internal static PredictedTouchdownOutcome Decide(
            PredictedTouchdownCandidateFacts candidate,
            PredictedCrashLiveFacts live,
            double nowUT,
            bool sceneSettled,
            bool observedBefore,
            double secondsSinceFirstObservation)
        {
            if (!candidate.IsPredictedTouchdown)
                return PredictedTouchdownOutcome.NotPredictedTouchdown;
            if (!candidate.HasLaunchGuid)
                return PredictedTouchdownOutcome.NoLaunchGuid;
            if (candidate.OwnedElsewhere)
                return PredictedTouchdownOutcome.OwnedElsewhere;
            if (candidate.MaterializedBySpawn)
                return PredictedTouchdownOutcome.MaterializedBySpawn;
            if (!sceneSettled)
                return PredictedTouchdownOutcome.SceneNotSettled;

            bool confirmed = observedBefore && secondsSinceFirstObservation >= ConfirmRealSeconds;
            if (live.Found)
            {
                if (live.IsActiveVessel)
                    return PredictedTouchdownOutcome.VesselIsActive;
                if (live.LandedOrSplashed)
                    return PredictedTouchdownOutcome.Consistent;
                if (!IsFinite(candidate.TouchdownUT) || !IsFinite(nowUT)
                    || nowUT <= candidate.TouchdownUT + TouchdownMarginSeconds)
                    return PredictedTouchdownOutcome.StockStillDeciding;
                if (live.InPhysics)
                    return PredictedTouchdownOutcome.StockStillSimulating;
                if (TrajectoryReachesStockRemoval(
                        live.Eccentricity, live.PeriapsisAltitude, live.Descending, live.StockRemovalAltitude))
                    return PredictedTouchdownOutcome.StockMayStillRemove;
                return confirmed
                    ? PredictedTouchdownOutcome.Contradicted
                    : PredictedTouchdownOutcome.AwaitingConfirmation;
            }

            if (candidate.RemovedByPlayer)
                return PredictedTouchdownOutcome.RemovedByPlayer;
            if (!IsFinite(candidate.StockHeldUT))
                return PredictedTouchdownOutcome.NotInThisTimeline;
            return confirmed
                ? PredictedTouchdownOutcome.StockDeleted
                : PredictedTouchdownOutcome.AwaitingConfirmation;
        }

        /// <summary>
        /// Pure over its inputs: one pass over <paramref name="recordings"/> (the effective
        /// committed set). <paramref name="liveLookup"/> is called once per settled, identified
        /// candidate no session owns. <paramref name="firstObserved"/> holds, per recording and
        /// verdict kind, the real time a pass first saw it; a verdict acts on a later pass at
        /// least <see cref="ConfirmRealSeconds"/> on, and a sighting no longer seen is dropped.
        /// Recordings in <paramref name="skipIds"/> (failed to apply this scene) are left alone.
        /// </summary>
        internal static PredictedTouchdownPassResult EvaluatePass(
            IReadOnlyList<Recording> recordings,
            Func<Recording, bool> isOwnedElsewhere,
            Func<Recording, bool> isRemovedByPlayer,
            Func<Recording, PredictedCrashLiveFacts> liveLookup,
            double nowUT,
            bool sceneSettled,
            double realtimeNow,
            Dictionary<string, double> firstObserved,
            ICollection<string> skipIds = null)
        {
            var result = new PredictedTouchdownPassResult();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    Recording rec = recordings[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;
                    if (skipIds != null && skipIds.Contains(rec.RecordingId))
                        continue;
                    PredictedTouchdownCandidateFacts candidate =
                        BuildCandidate(rec, isOwnedElsewhere, isRemovedByPlayer);
                    if (!candidate.IsPredictedTouchdown)
                        continue;
                    result.Candidates++;

                    bool lookupDue = sceneSettled && candidate.HasLaunchGuid && !candidate.OwnedElsewhere
                        && !candidate.MaterializedBySpawn && liveLookup != null;
                    PredictedCrashLiveFacts live = lookupDue ? liveLookup(rec) : default(PredictedCrashLiveFacts);
                    string key = rec.RecordingId + (live.Found ? "|contradicted" : "|gone");
                    bool observed = firstObserved != null && firstObserved.ContainsKey(key);
                    double since = observed ? realtimeNow - firstObserved[key] : double.NaN;

                    PredictedTouchdownOutcome outcome = Decide(
                        candidate, live, nowUT, sceneSettled, observed, since);
                    result.Counts[outcome] = result.Count(outcome) + 1;
                    if (live.Found)
                        result.Sighted.Add(rec);

                    switch (outcome)
                    {
                        case PredictedTouchdownOutcome.StockStillDeciding:
                        case PredictedTouchdownOutcome.StockStillSimulating:
                        case PredictedTouchdownOutcome.StockMayStillRemove:
                            result.NeedsRecheck = true;
                            break;
                        case PredictedTouchdownOutcome.AwaitingConfirmation:
                            result.NeedsRecheck = true;
                            result.AwaitingConfirmation = true;
                            seen.Add(key);
                            if (firstObserved != null && !observed)
                                firstObserved[key] = realtimeNow;
                            break;
                        case PredictedTouchdownOutcome.StockDeleted:
                            seen.Add(key);
                            result.StockDeleted.Add(rec);
                            break;
                        case PredictedTouchdownOutcome.Contradicted:
                            seen.Add(key);
                            result.Contradicted.Add(rec);
                            break;
                    }
                }
            }

            if (firstObserved != null && firstObserved.Count > 0)
            {
                var stale = new List<string>();
                foreach (string k in firstObserved.Keys)
                    if (!seen.Contains(k))
                        stale.Add(k);
                for (int i = 0; i < stale.Count; i++)
                    firstObserved.Remove(stale[i]);
            }
            return result;
        }

        /// <summary>One summary line for a pass.</summary>
        internal static string FormatPassSummary(
            PredictedTouchdownPassResult pass, string scene, double nowUT, int deleted, int contradicted, int failed)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Touchdown pass: scene={0} nowUT={1} candidates={2} stockDeleted={3} contradicted={4} " +
                "failed={5} consistent={6} stillDeciding={7} stillSimulating={8} mayStillRemove={9} " +
                "awaitingConfirmation={10} removedByPlayer={11} notInThisTimeline={12} spawned={13} " +
                "active={14} ownedElsewhere={15} noLaunchGuid={16} recheck={17}",
                scene ?? "(none)",
                FormatUT(nowUT),
                pass.Candidates,
                deleted,
                contradicted,
                failed,
                pass.Count(PredictedTouchdownOutcome.Consistent),
                pass.Count(PredictedTouchdownOutcome.StockStillDeciding),
                pass.Count(PredictedTouchdownOutcome.StockStillSimulating),
                pass.Count(PredictedTouchdownOutcome.StockMayStillRemove),
                pass.Count(PredictedTouchdownOutcome.AwaitingConfirmation),
                pass.Count(PredictedTouchdownOutcome.RemovedByPlayer),
                pass.Count(PredictedTouchdownOutcome.NotInThisTimeline),
                pass.Count(PredictedTouchdownOutcome.MaterializedBySpawn),
                pass.Count(PredictedTouchdownOutcome.VesselIsActive),
                pass.Count(PredictedTouchdownOutcome.OwnedElsewhere),
                pass.Count(PredictedTouchdownOutcome.NoLaunchGuid),
                pass.NeedsRecheck ? "true" : "false");
        }

        /// <summary>
        /// Pure: does a vessel on this orbit still reach the altitude under which stock deletes
        /// it? A low point at or under the line does on a bound orbit (it comes round again) and
        /// on an unbound one before its periapsis; an unreadable orbit counts as reaching it, so
        /// a prediction stays until the evidence is clear. Shared with the crash reconciler.
        /// </summary>
        internal static bool TrajectoryReachesStockRemoval(
            double eccentricity, double periapsisAltitude, bool descending, double stockRemovalAltitude)
        {
            if (!IsFinite(eccentricity) || !IsFinite(periapsisAltitude))
                return true;
            double line = IsFinite(stockRemovalAltitude) && stockRemovalAltitude > 0.0
                ? stockRemovalAltitude
                : 0.0;
            if (periapsisAltitude > line)
                return false;
            return eccentricity < 1.0 || descending;
        }

        // ------------------------------------------------------------------ the stamp

        /// <summary>
        /// Records that stock held the recording's vessel at <paramref name="ut"/> in the
        /// current timeline (keeps the later of the two). Returns true when it moved.
        /// </summary>
        internal static bool NoteStockHeld(Recording rec, double ut)
        {
            if (rec == null || !IsFinite(ut))
                return false;
            if (IsFinite(rec.StockHeldVesselUT) && rec.StockHeldVesselUT >= ut)
                return false;
            rec.StockHeldVesselUT = ut;
            return true;
        }

        /// <summary>
        /// Commit-time stamp: a predicted touchdown whose vessel stock holds right now (the
        /// craft was still in the game when the player committed, exited or abandoned) records
        /// that fact at the current UT. A commit after a revert finds no vessel and stamps
        /// nothing, so the reverted flight's replay spawns the craft at its touchdown. Returns
        /// true when it stamped.
        /// </summary>
        internal static bool StampIfStockHoldsVesselNow(Recording rec, string context)
        {
            if (!IsPredictedTouchdown(rec))
                return false;
            string guid = UsableLaunchGuid(rec);
            if (guid == null)
                return false;
            bool? live = IsVesselWithGuidLive(guid);
            if (live != true)
            {
                ParsekLog.Verbose(LogTag,
                    $"{context ?? "(none)"}: predicted touchdown rec={rec.RecordingId ?? "(null)"} " +
                    $"vessel='{rec.VesselName ?? ""}' not stamped (live={(live.HasValue ? live.Value.ToString() : "unknown")}): " +
                    "stock does not hold the vessel now");
                return false;
            }
            double now = ReadNowUT();
            if (!NoteStockHeld(rec, now))
                return false;
            ParsekLog.Info(LogTag,
                $"{context ?? "(none)"}: predicted touchdown rec={rec.RecordingId ?? "(null)"} " +
                $"vessel='{rec.VesselName ?? ""}' committed while stock holds the vessel: " +
                $"stockHeldUT={FormatUT(now)} touchdownUT={FormatUT(rec.EndUT)}");
            return true;
        }

        /// <summary>
        /// A time-travel load (rewind, Re-Fly start or discard, stock revert, quickload) put
        /// stock back at <paramref name="loadedUT"/>: every stamp later than that describes a
        /// stock state the game no longer holds, so it is cleared (the replay then spawns the
        /// vessel at its touchdown as for any recording of the future). Returns the number
        /// cleared; one Info line when any was.
        /// </summary>
        internal static int ForgetStockHeldAfterTimeTravel(
            IEnumerable<Recording> recordings, double loadedUT, string kind)
        {
            if (recordings == null || !IsFinite(loadedUT))
                return 0;
            int cleared = 0;
            var seen = new HashSet<Recording>();
            foreach (Recording rec in recordings)
            {
                if (rec == null || !seen.Add(rec))
                    continue;
                if (!IsFinite(rec.StockHeldVesselUT) || rec.StockHeldVesselUT <= loadedUT + UtEpsilon)
                    continue;
                rec.StockHeldVesselUT = double.NaN;
                cleared++;
            }
            if (cleared > 0)
                ParsekLog.Info(LogTag,
                    $"Time-travel load kind={kind ?? "(none)"} loadedUT={FormatUT(loadedUT)}: cleared " +
                    $"{cleared.ToString(CultureInfo.InvariantCulture)} stock-held stamp(s) newer than the " +
                    "loaded state (those vessels are not in this timeline yet)");
            return cleared;
        }

        // ------------------------------------------------------------------ the spawn gate

        /// <summary>
        /// The end-of-recording spawn gate: true (refuse) for a predicted touchdown whose
        /// vessel stock held in this timeline and no longer holds. Recovery and termination
        /// are refused by the gates before this one. The reconciler converts the recording to
        /// stock's verdict; until then nothing may spawn or re-spawn the craft stock deleted.
        /// An unknown live answer never refuses.
        /// </summary>
        internal static bool ShouldRefuseSpawnAsStockDeleted(Recording rec, out string reason)
        {
            if (!IsStockDeletedTouchdown(rec, out reason))
                return false;
            ParsekLog.InfoRateLimited(LogTag, "spawn-refused|" + (rec.RecordingId ?? "(null)"),
                $"Spawn refused: rec={rec.RecordingId ?? "(null)"} vessel='{rec.VesselName ?? ""}' - " + reason +
                "; the reconciler records stock's verdict (Destroyed, crew dead)");
            return true;
        }

        /// <summary>
        /// The question behind <see cref="ShouldRefuseSpawnAsStockDeleted"/>, without its log
        /// line: a predicted touchdown whose vessel stock held in this timeline
        /// (<see cref="Recording.StockHeldVesselUT"/>) and no longer holds. The spawn-death check
        /// asks it before counting a death, so an adopted craft stock deleted is never counted
        /// (or reset for a re-spawn the gate would refuse). An unknown live answer is false.
        /// </summary>
        internal static bool IsStockDeletedTouchdown(Recording rec, out string reason)
        {
            reason = null;
            if (!IsPredictedTouchdown(rec) || !IsFinite(rec.StockHeldVesselUT))
                return false;
            string guid = UsableLaunchGuid(rec);
            if (guid == null)
                return false;
            if (IsVesselWithGuidLive(guid) != false)
                return false;
            reason = "predicted touchdown of a vessel stock deleted (held at ut=" +
                FormatUT(rec.StockHeldVesselUT) + ", now gone with no recovery)";
            return true;
        }

        // ------------------------------------------------------------------ the conversion

        /// <summary>
        /// Pure over the recording: turns a predicted touchdown into what stock decided when
        /// it deleted the craft (owner ruling Q4d): Destroyed, not predicted (stock confirmed
        /// it), crew dead through the terminal stamp, and, when the recording carries the
        /// predicted chute descent, a quiet end (the ghost comes down under its canopies to the
        /// touchdown and ends with no explosion). The end UT and the descent stay. Returns
        /// false (untouched) when the recording is not a predicted touchdown.
        /// </summary>
        internal static bool ConvertToStockDeleted(Recording rec, string context)
        {
            if (!IsPredictedTouchdown(rec))
                return false;
            bool descent = HasPredictedDescentSection(rec);
            rec.StampTerminalState(TerminalState.Destroyed, context ?? "PredictedTouchdownStockFate",
                predicted: false, quietEnd: descent);
            rec.TerminalPosition = null;
            rec.TerrainHeightAtEnd = double.NaN;
            RecordingEndpointResolver.RefreshEndpointDecision(rec, context, logDecision: false);
            rec.MarkFilesDirty();
            return true;
        }

        internal static bool HasPredictedDescentSection(Recording rec)
        {
            if (rec?.TrackSections == null)
                return false;
            for (int i = 0; i < rec.TrackSections.Count; i++)
                if (rec.TrackSections[i].source == TrackSectionSource.Predicted)
                    return true;
            return false;
        }

        // ------------------------------------------------------------------ leaving the flight scene

        internal const string SceneExitChuteLogPrefix = "Chute descent at scene exit:";
        internal const string SceneExitChuteLogTag = "Extrapolator";

        /// <summary>
        /// Owner ruling Q14: a commit that takes the player out of the flight scene (a scene
        /// exit to the Space Center, the Tracking Station or the main menu, or the far Switch-To
        /// that reloads FLIGHT) decides every chuted craft still in the air as stock's "mission
        /// will be lost" case: each predicted touchdown in <paramref name="recordings"/> (the
        /// finalized tree's) becomes
        /// Destroyed, not predicted, crew dead, ending quietly after its descent
        /// (<see cref="ConvertToStockDeleted"/>), whatever the player does next. A craft whose
        /// live vessel is already on the surface (<paramref name="liveVesselOnSurface"/>) keeps
        /// its ending. A real landing is never a predicted touchdown, and a too-fast descent is
        /// already a Destroyed crash, so neither is touched. Returns the number converted.
        /// </summary>
        internal static int ApplyFlightSceneLeave(
            IEnumerable<Recording> recordings,
            double commitUT,
            string exitKind,
            Func<Recording, bool> liveVesselOnSurface)
        {
            if (recordings == null)
                return 0;
            int converted = 0;
            foreach (Recording rec in recordings)
            {
                if (!IsPredictedTouchdown(rec))
                    continue;
                TerminalState before = rec.TerminalStateValue.Value;
                if (liveVesselOnSurface != null && liveVesselOnSurface(rec))
                {
                    ParsekLog.Info(SceneExitChuteLogTag,
                        SceneExitChuteLogPrefix + " rec=" + (rec.RecordingId ?? "(null)")
                        + " pid=" + rec.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                        + " outcome=kept-touched-down terminal=" + before
                        + " exit=" + (exitKind ?? "(none)")
                        + " - the craft is already on the surface; it keeps its ending");
                    continue;
                }
                double touchdownUT = rec.EndUT;
                if (!ConvertToStockDeleted(rec, "SceneExitChuteDescent"))
                    continue;
                converted++;
                ParsekLog.Info(SceneExitChuteLogTag,
                    SceneExitChuteLogPrefix + " rec=" + (rec.RecordingId ?? "(null)")
                    + " pid=" + rec.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                    + " vessel='" + (rec.VesselName ?? "") + "'"
                    + " outcome=mission-lost terminal=" + before + "->Destroyed"
                    + " touchdownUT=" + FormatUT(touchdownUT)
                    + " commitUT=" + FormatUT(commitUT)
                    + " predicted=False quietEnd=" + (rec.QuietDestroyedEnd ? "True" : "False")
                    + " exit=" + (exitKind ?? "(none)")
                    + " - the player left the flight scene with the craft under its chutes "
                    + "(stock: the mission will be lost); crew dead, no spawn");
            }
            return converted;
        }

        // ------------------------------------------------------------------ live reads

        /// <summary>
        /// Is a vessel (or a stored proto vessel) with this launch guid in the current stock
        /// game? Null when the game cannot be read (no game loaded, a headless host).
        /// </summary>
        internal static bool? IsVesselWithGuidLive(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return null;
            Func<string, bool?> over = LiveVesselByGuidOverrideForTesting;
            if (over != null)
                return over(guid);
            try
            {
                return IsVesselWithGuidLiveCore(guid);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(LogTag, "live-read-failed",
                    $"Live vessel lookup unavailable ({ex.GetType().Name}); treated as unknown");
                return null;
            }
        }

        internal static double ReadNowUT()
        {
            Func<double> over = NowUTOverrideForTesting;
            if (over != null)
                return over();
            try
            {
                return ReadNowUTCore();
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool? IsVesselWithGuidLiveCore(string guid)
        {
            if (HighLogic.CurrentGame == null)
                return null;
            bool any = false;
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                any = true;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null && v.id != Guid.Empty
                        && string.Equals(v.id.ToString("N", CultureInfo.InvariantCulture), guid,
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            var protos = HighLogic.CurrentGame.flightState?.protoVessels;
            if (protos != null)
            {
                any = true;
                for (int i = 0; i < protos.Count; i++)
                {
                    ProtoVessel pv = protos[i];
                    if (pv != null && pv.vesselID != Guid.Empty
                        && string.Equals(pv.vesselID.ToString("N", CultureInfo.InvariantCulture), guid,
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return any ? (bool?)false : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadNowUTCore()
        {
            return Planetarium.GetUniversalTime();
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static string FormatUT(double v)
        {
            return IsFinite(v) ? v.ToString("F3", CultureInfo.InvariantCulture) : "NaN";
        }
    }
}
