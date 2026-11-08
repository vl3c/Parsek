using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The Rewind / Forward and Re-Fly columns: cell draws, visibility rules, enclosing-row suppression, launch rewind, rewind-point slot checks and the two confirmations.
    internal partial class RecordingsTableUI
    {
        // internal so the Missions tab (MissionsWindowUI) can reuse the exact R/FF cell for a
        // mission's root (launch) recording. Passing rec == null draws a blank ColW_Rewind cell.
        internal void DrawLegacyRewindForwardCell(
            Recording rec, int ri, double now, ParsekFlight flight)
        {
            if (rec == null)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
                return;
            }

            bool hasRewindSave = !string.IsNullOrEmpty(
                RecordingStore.GetRewindSaveFileName(rec));
            bool isRecording = parentUI.InFlightMode
                && flight != null
                && flight.IsRecording;

            if (ShouldShowForwardButton(rec, now))
            {
                // Shown once: the folder / block row this row is drawn under already
                // offers FF to this very recording, so the child cell stays blank.
                bool ffShownAbove = IsTimeTargetShownByEnclosingRow(ri, enclosingForwardTargetIdx);
                LogEnclosingTimeSuppressionTransition(
                    "row:" + ri.ToString(CultureInfo.InvariantCulture) + ":ff",
                    ffShownAbove, $"Forward #{ri} \"{rec.VesselName}\"");
                if (ffShownAbove)
                {
                    GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
                    return;
                }

                // Future recording: Forward button advances UT to recording start.
                string ffReason;
                bool canFF = RewindLauncher.CanFastForward(
                    rec, out ffReason, isRecording: isRecording);
                bool prevFF;
                if (!lastCanFF.TryGetValue(ri, out prevFF) || prevFF != canFF)
                {
                    lastCanFF[ri] = canFF;
                    ParsekLog.Verbose("UI",
                        $"Forward #{ri} \"{rec.VesselName}\": {(canFF ? "enabled" : "disabled — " + ffReason)}");
                }

                GUI.enabled = canFF;
                string tooltip = canFF
                    ? "Fast-forward to this launch"
                    : ffReason;
                if (DrawRewindColumnButton(new GUIContent(FastForwardActionLabel, tooltip)))
                {
                    ParsekLog.Info("UI",
                        $"Forward button clicked: #{ri} \"{rec.VesselName}\"");
                    ShowFastForwardConfirmation(rec);
                }
                GUI.enabled = true;
                return;
            }

            if (ShouldShowLegacyRewindButton(rec, now))
            {
                // Past/active recording with save AND we are the rewind owner.
                // The helper suppresses tree branches and rows whose own
                // unfinished-flight action lives in the Re-Fly column.
                ClearLegacyRewindSuppressionForOwnerRow(rec, ri);

                // Shown once: the folder / block row this row is drawn under already
                // offers R to this very launch, so the child cell stays blank.
                bool rewindShownAbove = IsTimeTargetShownByEnclosingRow(ri, enclosingRewindTargetIdx);
                LogEnclosingTimeSuppressionTransition(
                    "row:" + ri.ToString(CultureInfo.InvariantCulture) + ":r",
                    rewindShownAbove, $"Rewind #{ri} \"{rec.VesselName}\"");
                if (rewindShownAbove)
                {
                    GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
                    return;
                }

                string rewindReason;
                bool canRewind = RewindLauncher.CanRewind(
                    rec, out rewindReason, isRecording: isRecording);
                bool prevR;
                if (!lastCanRewind.TryGetValue(ri, out prevR) || prevR != canRewind)
                {
                    lastCanRewind[ri] = canRewind;
                    ParsekLog.Verbose("UI",
                        $"Rewind #{ri} \"{rec.VesselName}\": {(canRewind ? "enabled" : "disabled — " + rewindReason)}");
                }

                GUI.enabled = canRewind;
                string tooltip = canRewind
                    ? "Rewind to this launch"
                    : rewindReason;
                if (DrawRewindColumnButton(new GUIContent(RewindActionLabel, tooltip)))
                {
                    ParsekLog.Info("UI",
                        $"Rewind button clicked: #{ri} \"{rec.VesselName}\"");
                    ShowRewindConfirmation(rec);
                }
                GUI.enabled = true;
                return;
            }

            // Tree-branch suppression: log once when this row first becomes a
            // non-owner with a (resolved) rewind save, and again whenever it
            // flips back. hasRewindSave is true via the tree root, but
            // GetRewindRecording != rec, so the R button would be redundant.
            var owner = RecordingStore.GetRewindRecording(rec);
            bool suppressedTreeBranch = hasRewindSave
                && owner != null
                && !ReferenceEquals(owner, rec)
                && !EffectiveState.IsChainMemberOfUnfinishedFlight(rec);
            bool prevSuppressed;
            if (!lastSuppressedTreeBranch.TryGetValue(ri, out prevSuppressed)
                || prevSuppressed != suppressedTreeBranch)
            {
                lastSuppressedTreeBranch[ri] = suppressedTreeBranch;
                if (suppressedTreeBranch)
                {
                    ParsekLog.Verbose("UI",
                        $"Rewind #{ri} \"{rec.VesselName}\": suppressed — tree branch, use root recording's Rewind button");
                }
                else
                {
                    ParsekLog.Verbose("UI",
                        $"Rewind #{ri} \"{rec.VesselName}\": no longer suppressed");
                }
            }
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Rewind));
        }

        // Missions-tab header-bar variant of the legacy rewind/forward control: a plain fixed-width
        // button labelled "Rewind" / "Forward" (so it matches the mission header's other buttons),
        // over the mission's root (launch) recording. Reuses the SAME rewind/forward decision
        // (ShouldShow* + RewindLauncher.CanRewind/CanFastForward) and confirmation dialogs as the
        // recordings-tab R/FF cell; only the rendering (plain button, full-word labels, caller-set
        // width) differs. rec == null draws a blank cell of the given width.
        internal void DrawMissionRewindForwardButton(
            Recording rec, int ri, double now, ParsekFlight flight, float width,
            GUIStyle buttonStyle = null)
        {
            if (rec == null)
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(width));
                return;
            }
            GUIStyle style = buttonStyle ?? GUI.skin.button;

            bool isRecording = parentUI.InFlightMode && flight != null && flight.IsRecording;

            if (ShouldShowForwardButton(rec, now))
            {
                bool canFF = RewindLauncher.CanFastForward(rec, out string ffReason, isRecording: isRecording);
                GUI.enabled = canFF;
                if (GUILayout.Button(
                        new GUIContent("Forward", canFF ? "Fast-forward to this launch" : ffReason),
                        style, GUILayout.Width(width)))
                {
                    ParsekLog.Info("UI", $"Mission Forward button clicked: #{ri} \"{rec.VesselName}\"");
                    ShowFastForwardConfirmation(rec);
                }
                GUI.enabled = true;
                return;
            }

            if (ShouldShowLegacyRewindButton(rec, now))
            {
                bool canRewind = RewindLauncher.CanRewind(rec, out string rewindReason, isRecording: isRecording);
                ClearLegacyRewindSuppressionForOwnerRow(rec, ri);
                GUI.enabled = canRewind;
                if (GUILayout.Button(
                        new GUIContent("Rewind", canRewind ? "Rewind to this launch" : rewindReason),
                        style, GUILayout.Width(width)))
                {
                    ParsekLog.Info("UI", $"Mission Rewind button clicked: #{ri} \"{rec.VesselName}\"");
                    ShowRewindConfirmation(rec);
                }
                GUI.enabled = true;
                return;
            }

            GUILayout.Label("", bodyCellLabel, GUILayout.Width(width));
        }

        /// <summary>
        /// Whether <see cref="DrawMissionRewindForwardButton"/> draws a button (Rewind or
        /// Forward) for this recording right now, rather than its blank cell - the same two
        /// predicates, in the same order. The Missions tab's Interact column reads it before
        /// drawing Watch, so Watch takes the full button width when it is alone.
        /// </summary>
        internal bool MissionRewindForwardVisible(Recording rec, double now)
        {
            return rec != null
                && (ShouldShowForwardButton(rec, now) || ShouldShowLegacyRewindButton(rec, now));
        }

        // internal so the Missions tab (MissionsWindowUI) can reuse the exact Re-Fly (Fly / Seal)
        // cell for a vessel row's recording. Shows Fly / Seal only for unfinished-flight recordings;
        // otherwise a blank ColW_ReFly cell.
        internal void DrawReFlyColumnCell(Recording rec, int ri, double now)
        {
            ReFlyColumnAction action = ResolveReFlyColumnAction(rec);
            SetStashSealInReFlyColumnState(rec, ri, action == ReFlyColumnAction.StashSeal);

            if (action == ReFlyColumnAction.FlySeal
                && DrawUnfinishedFlightRewindButton(rec, ri, now))
                return;

            if (action == ReFlyColumnAction.StashSeal
                && DrawStashSealUnfinishedFlightButtons(rec, ri))
                return;

            GUILayout.Label("", bodyCellLabel, GUILayout.Width(reFlyCellWidth));
        }

        /// <summary>
        /// The Re-Fly cell drawn at a caller-set width and left inset, for the Missions tab's
        /// Interact column (whose buttons all share one width and one left edge). The same
        /// decision, dialogs and log lines as <see cref="DrawReFlyColumnCell(Recording, int, double)"/>;
        /// only the geometry differs, and it is restored before this returns.
        /// </summary>
        internal void DrawReFlyColumnCell(Recording rec, int ri, double now,
            float cellWidth, float leftInset)
        {
            float prevWidth = reFlyCellWidth;
            float prevInset = reFlyCellLeftInset;
            reFlyCellWidth = cellWidth;
            reFlyCellLeftInset = leftInset;
            try
            {
                DrawReFlyColumnCell(rec, ri, now);
            }
            finally
            {
                reFlyCellWidth = prevWidth;
                reFlyCellLeftInset = prevInset;
            }
        }

        private void ClearLegacyRewindSuppressionForOwnerRow(Recording rec, int ri)
        {
            bool prevSuppressed;
            if (lastSuppressedTreeBranch.TryGetValue(ri, out prevSuppressed) && prevSuppressed)
            {
                lastSuppressedTreeBranch[ri] = false;
                ParsekLog.Verbose("UI",
                    $"Rewind #{ri} \"{rec.VesselName}\": no longer suppressed — owner row");
            }
        }

        private void SetStashSealInReFlyColumnState(Recording rec, int ri, bool active)
        {
            bool previous;
            if (lastStashSealInReFlyColumn.TryGetValue(ri, out previous)
                && previous == active)
                return;

            lastStashSealInReFlyColumn[ri] = active;
            // Only the activation edge is useful in playtest logs: it proves
            // the new layout reached a row that used to hide Rewind behind
            // Stash. Deactivation just means the row scrolled away or no
            // longer satisfies the stash predicate.
            if (!active)
                return;

            ParsekLog.Info("UI",
                $"Re-Fly #{ri} \"{rec?.VesselName ?? "<unnamed>"}\": rendering Stash and Seal in Re-Fly column");
        }

        internal enum ReFlyColumnAction
        {
            None,
            FlySeal,
            StashSeal
        }

        internal static ReFlyColumnAction ResolveReFlyColumnAction(Recording rec)
        {
            RewindPoint rp;
            int slotListIndex;
            string reason;
            UnfinishedFlightRewindRoute route = ResolveUnfinishedFlightRewindRoute(
                rec, out rp, out slotListIndex, out reason);
            if (route != UnfinishedFlightRewindRoute.NotUnfinishedFlight)
                return ReFlyColumnAction.FlySeal;

            return TryResolveStashableUnfinishedFlightRewindPoint(
                rec, out rp, out slotListIndex)
                ? ReFlyColumnAction.StashSeal
                : ReFlyColumnAction.None;
        }

        /// <summary>
        /// Decides whether a row should render the Forward button in the
        /// Rewind/Forward column.
        /// </summary>
        internal static bool ShouldShowForwardButton(Recording rec, double now)
        {
            if (rec == null) return false;

            Recording launchOwner;
            double launchStartUT;
            if (TryResolveLaunchRewindSurface(rec, out launchOwner, out launchStartUT))
                return now < launchStartUT;

            return now < rec.StartUT;
        }

        /// <summary>
        /// Decides whether a row should render the legacy Rewind
        /// (Rewind-to-launch) button. The button belongs on the row that
        /// represents the mission launch: usually the recording that owns the
        /// quicksave, and after Re-Fly supersede, the visible effective
        /// replacement for that hidden original owner. Other tree branches
        /// (debris, decouple children, EVA splits) inherit the same launch save
        /// through <see cref="RecordingStore.GetRewindRecording"/> but remain
        /// suppressed so the table does not draw duplicate buttons that all
        /// rewind to the same root launch. Future rows take the Forward path
        /// instead, and rows that ARE THEMSELVES an unfinished flight render the
        /// Re-Fly-column button drawn separately by
        /// <c>DrawUnfinishedFlightRewindButton</c>; the chain HEAD (or its
        /// effective Re-Fly replacement) keeps Rewind-to-launch even when a
        /// sibling chain TIP is the unfinished flight, so the player can always
        /// rewind a mission to the pad.
        /// </summary>
        internal static bool ShouldShowLegacyRewindButton(Recording rec, double now)
        {
            if (rec == null) return false;
            if (!ShouldSurfaceLaunchRewindButton(rec, now)) return false;
            // Suppress only when THIS row is itself an unfinished flight: the
            // Rewind-to-Staging button (DrawUnfinishedFlightRewindButton) takes
            // over and offering rewind-to-launch alongside it would be a
            // footgun. We must NOT suppress when only some OTHER chain member
            // is the unfinished flight — that incorrectly hides Rewind on the
            // launch row of a multi-segment chain whose destroyed continuation
            // (TIP) carries the BP link, leaving the player no way to rewind
            // the mission to the pad. See bug: launch root recording with
            // chainIndex=0 + destroyed chain TIP at chainIndex=1.
            if (EffectiveState.IsUnfinishedFlight(rec)) return false;
            return true;
        }

        internal static bool ShouldShowGroupLegacyRewindButton(Recording rec, double now)
        {
            return ShouldSurfaceLaunchRewindButton(rec, now);
        }

        /// <summary>
        /// "Shown once" for the Rewind column: true when the nearest DRAWN enclosing row
        /// already offers the same R (or FF) target - the same committed index - that this
        /// row's own button would, so this row draws a blank cell. Only an equal target
        /// suppresses: a child whose button would go somewhere else keeps it, and a row with
        /// no target of its own (-1) is never "suppressed". Pure for unit testing.
        /// </summary>
        internal static bool IsTimeTargetShownByEnclosingRow(int ownTargetIdx, int enclosingTargetIdx)
        {
            return ownTargetIdx >= 0 && ownTargetIdx == enclosingTargetIdx;
        }

        /// <summary>
        /// The enclosing R / FF targets a row hands to the rows drawn under it. A row that
        /// DREW its own button passes that target down (FF replaces the forward target, R the
        /// rewind one); a row that drew a blank cell - suppressed, or with no target - passes
        /// the targets it inherited through unchanged, because the row that drew them is still
        /// the enclosing one for its grandchildren. Pure for unit testing.
        /// </summary>
        internal static void ResolveChildEnclosingTimeTargets(
            int ownForwardIdx, int ownRewindIdx, bool ownSuppressed,
            int inheritedForwardIdx, int inheritedRewindIdx,
            out int childForwardIdx, out int childRewindIdx)
        {
            childForwardIdx = inheritedForwardIdx;
            childRewindIdx = inheritedRewindIdx;
            if (ownSuppressed) return;
            if (ownForwardIdx >= 0)
                childForwardIdx = ownForwardIdx;
            else if (ownRewindIdx >= 0)
                childRewindIdx = ownRewindIdx;
        }

        /// <summary>
        /// Transition gate for the shown-once suppression log: true when a key flips, or the
        /// first time a key is seen already suppressed. A key first seen UNsuppressed is
        /// recorded silently, so the ordinary state of every row never logs. Updates the
        /// cache. Pure over the dictionary for unit testing.
        /// </summary>
        internal static bool UpdateEnclosingSuppressionCache(
            Dictionary<string, bool> cache, string key, bool suppressed)
        {
            if (cache == null || string.IsNullOrEmpty(key)) return false;
            bool prev;
            if (!cache.TryGetValue(key, out prev))
            {
                cache[key] = suppressed;
                return suppressed;
            }
            if (prev == suppressed) return false;
            cache[key] = suppressed;
            return true;
        }

        internal void LogEnclosingTimeSuppressionTransition(string key, bool suppressed, string subject)
        {
            if (!UpdateEnclosingSuppressionCache(lastSuppressedByEnclosingRow, key, suppressed))
                return;
            ParsekLog.Verbose("UI", suppressed
                ? $"{subject}: time button suppressed - the enclosing row shows the same target (key={key})"
                : $"{subject}: time button no longer suppressed by the enclosing row (key={key})");
        }

        /// <summary>
        /// Is this row the LAUNCH row of its rewind save - the recording that owns the
        /// quicksave, or the visible effective replacement for a hidden original owner?
        /// <para>The "one Rewind button per launch" half of
        /// <see cref="ShouldShowLegacyRewindButton"/>, without its UT check or its
        /// this-row-is-itself-an-unfinished-flight suppression. Exposed because the Timeline
        /// needs exactly this half (finding P12): every branch of a tree - debris, decouple
        /// children, EVA splits - resolves the SAME root launch save through
        /// <see cref="RecordingStore.GetRewindRecording"/>, so a predicate built on "a save
        /// resolves" puts an R on all of them and each one rewinds the PARENT launch. This
        /// table has suppressed that since the multi-segment-chain fix; the Timeline had
        /// not.</para>
        /// </summary>
        internal static bool IsLaunchRewindRow(Recording rec)
        {
            return TryResolveLaunchRewindSurface(rec, out _, out _);
        }

        private static bool ShouldSurfaceLaunchRewindButton(Recording rec, double now)
        {
            Recording owner;
            double launchStartUT;
            if (!TryResolveLaunchRewindSurface(rec, out owner, out launchStartUT))
                return false;
            return now >= launchStartUT;
        }

        private static bool TryResolveLaunchRewindSurface(
            Recording rec,
            out Recording owner,
            out double launchStartUT)
        {
            owner = null;
            launchStartUT = 0.0;
            if (rec == null) return false;

            owner = RecordingStore.GetRewindRecording(rec);
            if (owner == null) return false;
            if (ReferenceEquals(owner, rec))
            {
                launchStartUT = rec.StartUT;
                return true;
            }

            if (!IsEffectiveReplacementForLaunchRewindOwner(
                    rec,
                    owner,
                    CurrentRecordingSupersedesForDisplay(),
                    CurrentRecordingRewindRetirementsForDisplay()))
            {
                owner = null;
                return false;
            }

            // Effective Re-Fly replacements surface the hidden launch owner's
            // rewind save. In normal play this row appears only after the
            // launch UT has passed, so the Forward action branch is unreachable
            // for replacements.
            launchStartUT = owner.StartUT;
            return true;
        }

        internal static bool IsEffectiveReplacementForLaunchRewindOwner(
            Recording rec,
            Recording owner,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyList<RecordingRewindRetirement> retirements = null)
        {
            if (rec == null || owner == null) return false;
            if (string.IsNullOrEmpty(rec.RecordingId)
                || string.IsNullOrEmpty(owner.RecordingId))
                return false;
            // Cascade overload: parent-anchored debris of a retired recording
            // must never be picked as a launch-rewind replacement target.
            // The child's lineage is gone with its retired parent.
            if (EffectiveState.IsRewindRetired(rec, RecordingStore.CommittedRecordings, retirements))
                return false;
            if (supersedes == null || supersedes.Count == 0)
                return false;

            string effectiveOwnerId =
                EffectiveState.EffectiveRecordingId(owner.RecordingId, supersedes);
            return string.Equals(effectiveOwnerId, rec.RecordingId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Resolves the RewindPoint + child-slot list index for a recording with
        /// a parent branch point. This helper does not apply the unfinished
        /// flight classifier; callers that expose user actions should decide the
        /// eligibility predicate first.
        /// </summary>
        internal static bool TryResolveRewindPointForRecording(
            Recording rec, out RewindPoint rp, out int slotListIndex)
        {
            return UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                rec, out rp, out slotListIndex);
        }

        /// <summary>
        /// Slot-aware precondition for an RP invocation. The global RP
        /// precondition can pass while a specific child slot is disabled
        /// because the split-time vessel could not be correlated into the
        /// quicksave; keep that disabled row from starting an invocation that
        /// would fail only after a scene load.
        /// </summary>
        internal static bool CanInvokeRewindPointSlot(
            RewindPoint rp, int slotListIndex, out string reason)
        {
            if (rp == null)
            {
                reason = "rewind point is null";
                LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke: false, reason: reason, slot: null);
                return false;
            }
            if (rp.ChildSlots == null || slotListIndex < 0 || slotListIndex >= rp.ChildSlots.Count)
            {
                reason = "rewind slot missing";
                LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke: false, reason: reason, slot: null);
                return false;
            }

            var slot = rp.ChildSlots[slotListIndex];
            if (slot == null)
            {
                reason = "rewind slot missing";
                LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke: false, reason: reason, slot: null);
                return false;
            }
            if (slot.Disabled)
            {
                reason = !string.IsNullOrEmpty(slot.DisabledReason)
                    ? "rewind slot disabled: " + slot.DisabledReason
                    : "rewind slot disabled";
                LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke: false, reason: reason, slot: slot);
                return false;
            }

            bool canInvoke = RewindInvoker.CanInvoke(rp, slot, out reason);
            LogRewindSlotCanInvokeDecision(rp, slotListIndex, canInvoke, reason, slot);
            return canInvoke;
        }

        private static void LogRewindSlotCanInvokeDecision(
            RewindPoint rp,
            int slotListIndex,
            bool canInvoke,
            string reason,
            ChildSlot slot)
        {
            string rpId = rp == null || string.IsNullOrEmpty(rp.RewindPointId)
                ? "<null>"
                : rp.RewindPointId;
            string normalizedReason = string.IsNullOrEmpty(reason) ? "<none>" : reason;
            string identity = $"CanInvokeSlot|{rpId}|{slotListIndex}";
            bool slotDisabled = slot != null && slot.Disabled;
            string blockedKind = slotDisabled ? "slot-disabled" : "global-blocked";
            string stateKey = canInvoke ? "slot-ok" : blockedKind + "|" + normalizedReason;

            if (slotCanInvokeLastStateKey.TryGetValue(identity, out string lastKey)
                && string.Equals(lastKey, stateKey, StringComparison.Ordinal))
                return;
            slotCanInvokeLastStateKey[identity] = stateKey;

            string slotOrigin = slot == null || string.IsNullOrEmpty(slot.OriginChildRecordingId)
                ? "<none>"
                : slot.OriginChildRecordingId;
            int slotId = slot != null ? slot.SlotIndex : -1;

            ParsekLog.Verbose(
                "RewindUI",
                canInvoke
                    ? $"CanInvokeSlot: slot-ok rp={rpId} slot={slotId} listIndex={slotListIndex} origin={slotOrigin}"
                    : $"CanInvokeSlot: {blockedKind} rp={rpId} slot={slotId} listIndex={slotListIndex} " +
                      $"origin={slotOrigin} reason='{normalizedReason}'");
        }

        internal static int ClearRewindSlotCanInvokeLogState(string rewindPointId)
        {
            if (string.IsNullOrEmpty(rewindPointId))
                return 0;

            string prefix = $"CanInvokeSlot|{rewindPointId}|";
            int removed = 0;
            List<string> toRemove = null;
            foreach (string key in slotCanInvokeLastStateKey.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (toRemove == null) toRemove = new List<string>();
                    toRemove.Add(key);
                }
            }
            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++)
                    slotCanInvokeLastStateKey.Remove(toRemove[i]);
                removed = toRemove.Count;
            }

            removed += ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix(
                "RewindUI",
                prefix);
            return removed;
        }

        internal static int ClearAllRewindSlotCanInvokeLogState()
        {
            int removed = slotCanInvokeLastStateKey.Count;
            slotCanInvokeLastStateKey.Clear();
            removed += ParsekLog.ClearVerboseOnChangeIdentitiesWithPrefix(
                "RewindUI",
                "CanInvokeSlot|");
            return removed;
        }

        /// <summary>
        /// Resolves the child-slot list index inside <paramref name="rp"/> whose
        /// <see cref="ChildSlot.OriginChildRecordingId"/> matches
        /// <paramref name="rec"/> (or the forward-walked effective recording
        /// id so that a re-fly that produced another crash still maps back to
        /// its slot).
        /// </summary>
        internal static int ResolveSlotListIndexForRecording(RewindPoint rp, Recording rec)
        {
            return UnfinishedFlightClassifier.ResolveSlotListIndexForRecording(rp, rec);
        }

        internal void ShowRewindConfirmation(Recording rec)
        {
            // Resolve the rewind save owner — may be the tree root for branch recordings.
            var owner = RecordingStore.GetRewindRecording(rec);
            if (owner == null) return;
            bool isTreeBranch = owner != rec;

            int futureCount = RecordingStore.CountFutureRecordings(owner.StartUT);
            string futureText = futureCount > 0
                ? $"\n\n{futureCount} recording(s) after this launch will replay as ghosts."
                : "";

            string launchDate = KSPUtil.PrintDateCompact(owner.StartUT, true);
            string branchNote = isTreeBranch
                ? $"\n(from branch \"{rec.VesselName}\")"
                : "";
            string message = $"Rewind to \"{owner.VesselName}\" launch at {launchDate}?" +
                branchNote +
                futureText +
                "\n\nThis flight replays as a ghost; its recorded vessel reappears at the end" +
                " if you do not re-fly it. Watch the ghost to bring it back." +
                "\n\nAny uncommitted progress will be lost.";

            var capturedOwner = owner;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekRewindConfirm",
                    message,
                    "Confirm: Rewind",
                    HighLogic.UISkin,
                    new DialogGUIButton("Rewind", () =>
                    {
                        ParsekLog.Info("Rewind",
                            $"User confirmed Rewind-to-Launch for \"{capturedOwner.VesselName}\" at UT {capturedOwner.StartUT} (not a Re-Fly)");
                        RewindLauncher.InitiateRewind(capturedOwner);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info("Rewind", "User cancelled rewind confirmation");
                    })
                ),
                false, HighLogic.UISkin);
        }

        internal void ShowFastForwardConfirmation(Recording rec)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double now = Planetarium.GetUniversalTime();
            // Land RewindToLaunchLeadTimeSeconds before launch, matching Rewind's pre-launch lead.
            double leadTarget = TimeJumpManager.ApplyJumpLead(rec.StartUT, now);
            double delta = leadTarget - now;
            string launchDate = KSPUtil.PrintDateCompact(rec.StartUT, true);
            string message = string.Format(ic,
                "Fast-forward to just before \"{0}\" launch at {1}?\n\nTime will advance by {2:F0} seconds.",
                rec.VesselName, launchDate, delta);

            var flight = parentUI.Flight;
            var capturedRec = rec;
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekFastForwardConfirm",
                    message,
                    "Confirm: Fast-Forward",
                    HighLogic.UISkin,
                    new DialogGUIButton("Fast-Forward", () =>
                    {
                        ParsekLog.Info("FastForward",
                            string.Format(ic,
                                "User confirmed fast-forward to \"{0}\" at UT {1:F1}",
                                capturedRec.VesselName, capturedRec.StartUT));
                        if (parentUI.InFlightMode && flight != null)
                            flight.FastForwardToRecording(capturedRec);
                        else
                        {
                            // KSC or other non-flight scene: advance UT directly.
                            // Intentionally duplicates jump+message from FastForwardToRecording --
                            // flight path has additional steps (NotifyRecorder, recorder state)
                            // that don't apply outside flight.
                            double preJumpUT = Planetarium.GetUniversalTime();
                            if (!RewindLauncher.CanFastForwardAtUT(
                                    capturedRec,
                                    preJumpUT,
                                    out string ffReason,
                                    isRecording: false))
                            {
                                ParsekLog.Warn("FastForward",
                                    $"Non-flight FF blocked for \"{capturedRec.VesselName}\" " +
                                    $"id={capturedRec.RecordingId ?? "<none>"}: {ffReason}");
                                return;
                            }
                            // Land RewindToLaunchLeadTimeSeconds before launch, matching Rewind's pre-launch lead.
                            double ffTarget = TimeJumpManager.ApplyJumpLead(capturedRec.StartUT, preJumpUT);
                            double jumpDelta = ffTarget - preJumpUT;
                            ParsekLog.Info("FastForward",
                                string.Format(ic,
                                    "Non-flight FF to UT={0:F1} for '{1}' (delta={2:F1}s, launchUT={3:F1})",
                                    ffTarget, capturedRec.VesselName, jumpDelta, capturedRec.StartUT));
                            TimeJumpManager.ExecuteForwardJump(ffTarget);
                            ParsekLog.ScreenMessage(
                                string.Format(ic,
                                    "Fast-forwarded to \"{0}\" ({1:F0}s)",
                                    capturedRec.VesselName, jumpDelta), 3f);
                        }
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info("FastForward", "User cancelled fast-forward confirmation");
                    })
                ),
                false, HighLogic.UISkin);
        }
    }
}
