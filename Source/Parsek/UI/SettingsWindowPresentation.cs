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
        /// <para>A GUILayout window's new height is
        /// <c>Mathf.Clamp(passedHeight, contentMin, contentMax)</c>
        /// (`GUILayoutUtility.LayoutSingleGroup`, the <c>isWindow</c> branch). For this
        /// window <c>contentMax</c> sits well ABOVE the height a too-tall window already
        /// has, so passing the current height clamps straight back to itself: the window
        /// can only ever GROW (<c>contentMin</c> pushes it up when content is added) and
        /// never shrinks when content is removed. Dropping the <c>GUILayout.Height</c>
        /// option alone does not fix that - the passed RECT height is what the clamp
        /// reads.</para>
        ///
        /// <para>Releasing the height to zero makes the clamp resolve to
        /// <c>contentMin</c>, the true fit, in both directions. Zero is safe because only
        /// the Layout pass ever sees it: layout does not draw, and
        /// <see cref="KeepStoredHeightAcrossFitPass"/> keeps the collapsed height out of
        /// the caller's stored rect so no Repaint can draw the chrome at zero.</para>
        /// </summary>
        internal static Rect BuildHeightFitLayoutRect(Rect stored, bool releaseHeight)
        {
            if (!releaseHeight)
                return stored;

            return new Rect(stored.x, stored.y, stored.width, 0f);
        }

        /// <summary>
        /// Keeps the caller's stored height across a fit pass. The rect a fit pass returns
        /// is the zero-height rect that was passed in - GUILayout applies the fitted size
        /// after the call - so storing it verbatim would collapse the window and, worse,
        /// feed <c>GUILayout.Height(0)</c> back on the next non-fit pass, pinning it there
        /// permanently.
        /// <para>x / y / width come from the RETURNED rect so a drag landing on the same
        /// pass is not discarded.</para>
        /// </summary>
        internal static Rect KeepStoredHeightAcrossFitPass(Rect drawn, float storedHeight, bool releaseHeight)
        {
            if (!releaseHeight)
                return drawn;

            return new Rect(drawn.x, drawn.y, drawn.width, storedHeight);
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
