using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure decision / payload half of the automation-only <c>UiAction op=clone
    /// window=missions [mission=&lt;id&gt;]</c>: the Missions tab's Clone button.
    ///
    /// <para><b>THE BUTTON'S BODY, NOT A COPY OF IT.</b> The mission header's Clone button
    /// is a single call, <c>MissionStore.Clone(mission)</c>, and the applier makes exactly
    /// that call: a new Mission over the same tree, carrying the source's include set
    /// (<c>ExcludedIntervalKeys</c>, the partner-journey links) and loop configuration,
    /// inserted directly after its source, with the production
    /// <c>Cloned mission '...' -&gt; '...'</c> line. The button is hidden in Basic
    /// (loop authoring); the op does not read the complexity mode, the same stance
    /// <c>op=select</c> takes for the include checkboxes, because it drives the model the
    /// button writes and not the button's visibility.</para>
    ///
    /// <para><b>IT EDITS THE SAVE.</b> A Mission is serialized by the Mission codec, so a
    /// lane using this op runs on a THROWAWAY STAGED SAVE - the <c>op=select</c> lane
    /// rule. The payload reports the store count and the copy's carried include state so
    /// the edit is visible in the answer rather than only on disk.</para>
    ///
    /// <para><b>WHICH MISSION.</b> <c>mission=</c> names one exactly (the R10 capture of an
    /// earlier step's payload); absent, the FIRST mission over a committed tree - the
    /// <c>op=select</c> selector verbatim, because a Mission id is save-specific and a
    /// committed spec cannot name one. The payload echoes the resolved source id.</para>
    /// </summary>
    internal static class TestCommandUiClone
    {
        /// <summary><c>op=clone</c> against a window with no Clone button.</summary>
        internal const string CloneUnsupportedWindowReason = "clone-unsupported-window";

        /// <summary>No mission to clone: no Mission in the store has a committed tree, or
        /// the named <c>mission=</c> does not resolve to one. REJECTED rather than a no-op,
        /// the <c>select-no-mission</c> rationale.</summary>
        internal const string CloneNoMissionReason = "clone-no-mission";

        /// <summary>POST-SETTLE terminal: the store call returned a copy and, after a drawn
        /// frame, no Mission carries its id (or the store call returned none). ERROR - we
        /// acted and the game did not follow.</summary>
        internal const string CloneNotAppliedReason = "clone-not-applied";

        /// <summary>The windows this op drives, comma-joined for the reject message.</summary>
        internal static string CloneableWindowNames => TestCommandUiAction.MissionsWindow;

        /// <summary>Whether <c>op=clone</c> is defined for this window.</summary>
        internal static bool WindowHasCloneAffordance(string window)
            => window == TestCommandUiAction.MissionsWindow;

        /// <summary>
        /// Whether a settled clone is the clone that was asked for: the copy is in the store,
        /// the store grew by exactly one, and the copy points at the source's tree. Each half
        /// catches a different failure - a copy removed by a same-frame reaper, a second
        /// writer racing the frame, and a store call that built a copy of the wrong mission.
        /// </summary>
        internal static bool CloneSettledAsRequested(bool copyFound, int countBefore,
                                                     int countAfter, string sourceTreeId,
                                                     string copyTreeId)
            => copyFound
               && countAfter == countBefore + 1
               && string.Equals(sourceTreeId ?? string.Empty, copyTreeId ?? string.Empty,
                                System.StringComparison.Ordinal);

        /// <summary>
        /// OK payload for <c>clone</c>:
        /// <c>op=clone window= mission= copy= name= missions= excluded= links= loop=</c>.
        ///
        /// <para><c>mission</c> / <c>copy</c> are the source and new ids (the copy id is what
        /// a later step captures as <c>${label.copy}</c>); <c>name</c> is the copy's display
        /// name; <c>missions</c> the store count AFTER the clone; <c>excluded</c> /
        /// <c>links</c> the include set the copy CARRIED, which is what makes a clone a second
        /// include set over the same recordings; <c>loop</c> the copy's loop flag, always
        /// false since <c>MissionStore.Clone</c> disarms the copy of a looping mission (one
        /// loop per tree).</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildClonePayload(
            string window, string sourceId, string copyId, string copyName, int missions,
            int excluded, int links, bool loop)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.CloneOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("mission", sourceId ?? string.Empty),
                new KeyValuePair<string, string>("copy", copyId ?? string.Empty),
                new KeyValuePair<string, string>("name", copyName ?? string.Empty),
                new KeyValuePair<string, string>("missions", missions.ToString(ic)),
                new KeyValuePair<string, string>("excluded", excluded.ToString(ic)),
                new KeyValuePair<string, string>("links", links.ToString(ic)),
                new KeyValuePair<string, string>("loop", loop ? "true" : "false"),
            };
        }
    }
}
