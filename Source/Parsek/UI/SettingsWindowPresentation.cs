using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pure rules shared by the Settings window edit fields and Defaults button.
    /// Keeps IMGUI code focused on layout, persistence, and logging.
    /// </summary>
    internal static class SettingsWindowPresentation
    {
        internal struct AutoLoopEditResolution
        {
            internal double RequestedSeconds;
            internal float AppliedSeconds;
            internal bool WasClamped;
        }

        internal struct SettingsDefaults
        {
            internal bool AutoRecordOnLaunch;
            internal bool AutoRecordOnEva;
            internal bool AutoRecordOnFirstModificationAfterSwitch;
            internal bool AutoMerge;
            internal bool VerboseLogging;
            internal bool WriteReadableSidecarMirrors;
            internal bool AutoBackupExistingSaves;
            internal bool ShowRouteLines;
            internal SamplingDensity SamplingDensityLevel;
            internal float AutoLoopIntervalSeconds;
            internal LoopTimeUnit AutoLoopDisplayUnit;
            internal bool ShowCommittedFutureOverlays;
            internal bool BlockCommittedActions;
        }

        internal static bool TryResolveAutoLoopEdit(
            string text,
            LoopTimeUnit unit,
            out AutoLoopEditResolution resolution)
        {
            resolution = default;

            if (!ParsekUI.TryParseLoopInput(text, unit, out double parsed) || parsed < 0)
                return false;

            double requestedSeconds = ParsekUI.ConvertToSeconds(parsed, unit);
            bool wasClamped = requestedSeconds < LoopTiming.MinCycleDuration;

            resolution = new AutoLoopEditResolution
            {
                RequestedSeconds = requestedSeconds,
                AppliedSeconds = (float)(wasClamped ? LoopTiming.MinCycleDuration : requestedSeconds),
                WasClamped = wasClamped
            };
            return true;
        }

        internal static SettingsDefaults BuildDefaults()
        {
            return new SettingsDefaults
            {
                AutoRecordOnLaunch = true,
                AutoRecordOnEva = true,
                AutoRecordOnFirstModificationAfterSwitch = true,
                AutoMerge = false,
                VerboseLogging = true,
                WriteReadableSidecarMirrors = true,
                AutoBackupExistingSaves = true,
                ShowRouteLines = true,
                SamplingDensityLevel = SamplingDensity.Medium,
                AutoLoopIntervalSeconds = (float)LoopTiming.DefaultLoopIntervalSeconds,
                AutoLoopDisplayUnit = LoopTimeUnit.Sec,
                ShowCommittedFutureOverlays = true,
                BlockCommittedActions = true
            };
        }

        /// <summary>
        /// What the one-shot height-fit log should say this pass (see
        /// <c>SettingsWindowUI.DrawIfOpen</c>). The fitted height is applied by GUILayout
        /// AFTER the measuring call returns, so the fit can only be reported once a LATER
        /// pass has carried it back into the stored rect - never on the measuring pass
        /// itself, which is what the old log did (it printed the stale height and read as
        /// a success on a switch that had not resized anything).
        /// </summary>
        internal enum HeightFitLogOutcome
        {
            /// <summary>Nothing to say yet: no fit in flight, or still waiting for one.</summary>
            None,

            /// <summary>The fitted height landed and differs from the pre-fit height.</summary>
            Applied,

            /// <summary>The wait budget expired with the height unchanged.</summary>
            NoChange
        }

        /// <summary>
        /// The rect to hand <c>GUILayout</c> on a height-fit LAYOUT pass.
        ///
        /// <para>A GUILayout window resolves to <c>Max(passedHeight, contentMin)</c>, so it
        /// can only ever GROW - dropping the <c>GUILayout.Height</c> option does NOT make it
        /// shrink. Verified by decompiling Unity's IMGUI module: on a Layout pass
        /// <c>GUI.CallWindowDelegate</c> seeds the window's layout group with
        /// <c>Width</c>/<c>Height</c> taken from the window's CURRENT rect, and the caller's
        /// own options are applied over that - so omitting Height simply leaves the seeded
        /// <c>minHeight = maxHeight = passedHeight</c> standing. <c>GUILayoutGroup.CalcHeight</c>
        /// then raises it with <c>minHeight = Max(minHeight, childMin)</c>, and
        /// <c>GUILayoutUtility.LayoutSingleGroup</c> (the <c>isWindow</c> branch) finishes with
        /// <c>Mathf.Clamp(passedHeight, minHeight, maxHeight)</c>. Both bounds are
        /// <c>Max(passedHeight, contentMin)</c> by then, so an over-tall window clamps straight
        /// back to itself. This is true of EVERY GUILayout window, not a quirk of this one's
        /// content.</para>
        ///
        /// <para>Releasing the height to zero collapses that to <c>contentMin</c> - the true
        /// fit - in both directions. It is the same trick the main Parsek window uses
        /// (<c>ParsekFlight</c> / <c>ParsekKSC</c> zero their <c>windowRect.height</c> before
        /// every draw); the difference is that this window is fixed-height the rest of the
        /// time, so it releases the height for ONE Layout pass rather than always.</para>
        ///
        /// <para>The zero is not entirely invisible: <c>ClickThruBlocker</c> passes
        /// <c>GUILayout.Window</c>'s RETURN to <c>CTBWin.PreventInFlightClickthrough</c>, and on
        /// a fit pass that return is the zero-height rect we handed in (see
        /// <see cref="KeepStoredHeightAcrossFitPass"/>), which it reads as "mouse not over the
        /// window" and frees its focus lock for that pass. The next pass returns the real rect
        /// and re-locks. Parsek's own CAMERACONTROLS lock is unaffected - it uses the stored
        /// rect, kept intact below.</para>
        /// </summary>
        internal static Rect BuildHeightFitLayoutRect(Rect stored, bool releaseHeight)
        {
            if (!releaseHeight)
                return stored;

            return new Rect(stored.x, stored.y, stored.width, 0f);
        }

        /// <summary>
        /// Keeps the caller's stored height across a fit pass.
        ///
        /// <para>A fit pass RETURNS the zero-height rect it was handed, not the fitted one.
        /// That is measured, not assumed: the pre-fix build logged this very return as
        /// <c>h=636</c> on a Layout pass whose content had already grown to 948, and the 948
        /// only appeared in the stored rect on a later pass. The fitted height therefore
        /// arrives on the NEXT event's return - in practice the same frame's Repaint - and
        /// that is the channel the whole scheme rides on.</para>
        ///
        /// <para>Handing the stale height back in the meantime is harmless because a Repaint
        /// never re-runs layout (<c>GUI.CallWindowDelegate</c> only calls
        /// <c>GUILayoutUtility.Layout</c> on a Layout event), so nothing re-forces the window
        /// before the fitted height has been stored. The same log proves the pickup beats the
        /// next Layout pass: that pass hands over <c>GUILayout.Height(stale)</c>, which WOULD
        /// force the window back - and the grown height stuck.</para>
        ///
        /// <para>What the stored rect must not become is zero, because it is not just a draw
        /// position: it is the hit rect behind the window's CAMERACONTROLS input lock and
        /// <c>ParsekUI.IsPointerOverOpenWindow</c>, which both require a positive height. x / y
        /// / width come from the RETURNED rect so nothing else the pass resolved is
        /// discarded.</para>
        /// </summary>
        internal static Rect KeepStoredHeightAcrossFitPass(Rect drawn, float storedHeight, bool releaseHeight)
        {
            if (!releaseHeight)
                return drawn;

            return new Rect(drawn.x, drawn.y, drawn.width, storedHeight);
        }

        /// <summary>
        /// Bookkeeping for the one-shot height-fit report: whether a fit is in flight, the
        /// height it started from, and how many passes are left to wait for it.
        /// </summary>
        internal struct HeightFitLogState
        {
            internal bool AwaitingFit;
            internal float HeightAtFitPass;
            internal int PassesRemaining;
        }

        /// <summary>The advanced state plus what to log for it.</summary>
        internal struct HeightFitLogStep
        {
            internal HeightFitLogState State;
            internal HeightFitLogOutcome Outcome;
        }

        /// <summary>
        /// Advances the height-fit report by one draw pass. Pure so the transition - not just
        /// the classification - is directly testable: getting the baseline captured a pass
        /// late would make every reported <c>was=</c> wrong, and dropping the awaiting guard
        /// would run the countdown forever.
        /// </summary>
        internal static HeightFitLogStep AdvanceHeightFitLog(
            HeightFitLogState state,
            bool fitPass,
            float currentHeight,
            int passBudget)
        {
            if (fitPass)
            {
                // The fit pass keeps the pre-fit height (see KeepStoredHeightAcrossFitPass),
                // so THIS height is the baseline the landed fit is compared against, and the
                // pass itself can never report anything.
                state.AwaitingFit = true;
                state.HeightAtFitPass = currentHeight;
                state.PassesRemaining = passBudget;
                return new HeightFitLogStep { State = state, Outcome = HeightFitLogOutcome.None };
            }

            if (!state.AwaitingFit)
                return new HeightFitLogStep { State = state, Outcome = HeightFitLogOutcome.None };

            state.PassesRemaining--;
            HeightFitLogOutcome outcome = ClassifyHeightFitLog(
                state.AwaitingFit, state.HeightAtFitPass, currentHeight, state.PassesRemaining);
            if (outcome != HeightFitLogOutcome.None)
                state.AwaitingFit = false;

            return new HeightFitLogStep { State = state, Outcome = outcome };
        }

        /// <summary>
        /// Decides what the pending height-fit should log this pass. <paramref name="passesRemaining"/>
        /// bounds the wait so a fit that changes nothing still reports once instead of
        /// leaving the request silently open.
        /// </summary>
        internal static HeightFitLogOutcome ClassifyHeightFitLog(
            bool awaitingFit,
            float heightAtFitPass,
            float currentHeight,
            int passesRemaining)
        {
            if (!awaitingFit)
                return HeightFitLogOutcome.None;

            if (!Mathf.Approximately(heightAtFitPass, currentHeight))
                return HeightFitLogOutcome.Applied;

            return passesRemaining <= 0
                ? HeightFitLogOutcome.NoChange
                : HeightFitLogOutcome.None;
        }
    }
}
