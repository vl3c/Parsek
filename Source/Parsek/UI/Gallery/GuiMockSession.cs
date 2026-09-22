using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// One cache-rebuild site a live gallery session suppresses, named once so the
    /// production call site and the source gate agree on a value rather than on a
    /// spelling.
    /// </summary>
    internal struct GuiMockSuppressionSite
    {
        /// <summary>The wire / log token for this site.</summary>
        internal string Site;

        /// <summary>The window token that owns it.</summary>
        internal string Window;
    }

    /// <summary>
    /// The live GUI-state-gallery mock scope: at most ONE at a time, for ONE window,
    /// held in process-lifetime statics on this automation-only type.
    ///
    /// <para><b>WHY A STATIC AND NOT A FIELD OF ANYTHING SERIALIZED.</b> The whole crash
    /// -safety argument of the gallery (design-gui-state-gallery.md section 7.5) rests on
    /// nothing injected being reachable from a save path. A session lives here and the
    /// values it injects live in UI-layer fields, so <c>ParsekScenario.OnSave</c> has
    /// nothing to write even while a mock is on screen, and a process that dies mid-mock
    /// leaves nothing behind - not because a finally block ran, but because there was
    /// never anything to persist.</para>
    ///
    /// <para><b>WHY THE SUPPRESSION IS A PREDICATE READ AND NOT A FLAG ON EACH WINDOW.</b>
    /// Three windows' caches would otherwise each need their own "do not rebuild" field,
    /// each settable from the seam, each a surface a player build carries. One predicate
    /// that can only answer true while an ARMED seam created a session is a single site to
    /// reason about, and <see cref="Owns"/> is false in every player build by
    /// construction: nothing outside <c>ParsekTestCommandAddon.UiMock</c> calls
    /// <see cref="Begin"/>.</para>
    ///
    /// <para>Unity-free on purpose: the frame number and the timestamp are passed in, so
    /// the whole lifecycle is xUnit-testable without KSP.</para>
    /// </summary>
    internal static class GuiMockSession
    {
        /// <summary>The <c>[Subsystem]</c> tag every session and applier line carries.
        /// The command echo stays on <c>[TestCommands]</c> and the dump on
        /// <c>[GuiTree]</c>, so a lane's log reads as one conversation.</summary>
        internal const string LogTag = "GuiMock";

        /// <summary>The catalogue contract token echoed on the wire, in the dump's
        /// <c>mock</c> block and on the describe line. Bumped only for a SHAPE change to
        /// what a mocked capture carries, never for adding states.</summary>
        internal const string CatalogueId = "gui-mock/1";

        // ----- the window tokens this phase supports -----
        //
        // Declared here rather than taken from TestCommandUiAction so the UI layer does
        // not reach into the seam layer for a string. GuiMockCatalogueTests pins each one
        // equal to the seam's own constant, so the two cannot drift.

        internal const string KerbalsWindow = "kerbals";
        internal const string CareerWindow = "career";
        internal const string StructureWindow = "structure";

        // ----- the suppression sites (design 7.4) -----

        /// <summary>Career State's <c>ShouldRebuildCachedVM</c>. Without it the UT-text
        /// compare rebuilds the mocked VM within one game-second.</summary>
        internal static readonly GuiMockSuppressionSite CareerVmRebuild =
            new GuiMockSuppressionSite { Site = "career-vm-rebuild", Window = CareerWindow };

        /// <summary>
        /// Career State's explicit <c>InvalidateCache</c>, reached from
        /// <c>LedgerOrchestrator.OnTimelineDataChanged</c> through
        /// <c>ParsekUI.OnTimelineDataChanged</c>.
        ///
        /// <para>The OTHER writer of that window's cached VM, and the one the first build
        /// missed: suppressing the rebuild PREDICATE alone let any ledger write null the
        /// mocked VM, after which the predicate answered "do not rebuild" and the draw
        /// dereferenced a null Nullable every frame.</para>
        /// </summary>
        internal static readonly GuiMockSuppressionSite CareerInvalidate =
            new GuiMockSuppressionSite { Site = "career-invalidate", Window = CareerWindow };

        /// <summary>Kerbals' explicit <c>InvalidateCache</c> (the timeline hook).</summary>
        internal static readonly GuiMockSuppressionSite KerbalsInvalidate =
            new GuiMockSuppressionSite { Site = "kerbals-invalidate", Window = KerbalsWindow };

        /// <summary>Kerbals' eight stock live-crew <c>GameEvents</c>, which funnel into
        /// one handler. <c>onVesselChange</c> alone fires often enough to clobber a
        /// mocked roster before a capture lands.</summary>
        internal static readonly GuiMockSuppressionSite KerbalsLiveCrew =
            new GuiMockSuppressionSite { Site = "kerbals-live-crew", Window = KerbalsWindow };

        /// <summary>Every declared site, in declaration order. The source gate
        /// (<c>GuiMockApplierSourceGateTests</c>) derives the production call sites from
        /// comment-stripped source and compares them against THIS table, so a site wired
        /// into a window and not declared here - or declared and never wired - reds
        /// locally instead of photographing a clobbered mock.</summary>
        internal static readonly GuiMockSuppressionSite[] SuppressionSites =
        {
            CareerVmRebuild,
            CareerInvalidate,
            KerbalsInvalidate,
            KerbalsLiveCrew,
        };

        // ----- live state -----

        private static string stateId;
        private static string window;
        private static int appliedFrame;
        private static string appliedUtc;
        private static Action restore;
        private static string brokenReason;
        private static readonly HashSet<string> notedSites =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Whether a mock scope is live right now.</summary>
        internal static bool IsLive { get { return stateId != null; } }

        /// <summary>The live scope's state id, or null.</summary>
        internal static string StateId { get { return stateId; } }

        /// <summary>The live scope's window token, or null.</summary>
        internal static string Window { get { return window; } }

        /// <summary>The frame the mock was installed in.</summary>
        internal static int AppliedFrame { get { return appliedFrame; } }

        /// <summary>The install timestamp, InvariantCulture, for the log and the dump.</summary>
        internal static string AppliedUtc { get { return appliedUtc; } }

        /// <summary>
        /// THE suppression predicate. False in every player build, because only the armed
        /// seam can create a session.
        ///
        /// <para>Deliberately window-scoped rather than a bare "is a mock live": two
        /// windows' suppressions interacting is a state nobody can reason about, which is
        /// also why <c>mock-refused-session-live</c> refuses a second scope outright.</para>
        /// </summary>
        internal static bool Owns(string windowToken)
        {
            return stateId != null
                   && string.Equals(window, windowToken, StringComparison.Ordinal);
        }

        /// <summary>Why the live scope is no longer showing its state, or null when it is
        /// intact. Read by the applier's settle and by the clear.</summary>
        internal static string BrokenReason { get { return brokenReason; } }

        /// <summary>True when the live scope has been observed to have lost its mocked
        /// model. A scope in this state must never report OK: the window is drawing
        /// something else under the state's label.</summary>
        internal static bool IsBroken { get { return stateId != null && brokenReason != null; } }

        /// <summary>
        /// Records that a window's mocked model is gone even though the scope is live -
        /// something outside the declared suppression set wrote the injected member.
        ///
        /// <para>It does NOT clear the scope, and that is deliberate: the observation
        /// happens inside a DRAW pass, where running a restore closure (which writes the
        /// window's open flag, rect and tab) is the wrong place to reach. The applier
        /// reads <see cref="IsBroken"/> on its next poll and answers
        /// <c>mock-scope-broken</c>, which both restores and tells the lane.</para>
        /// </summary>
        internal static void NoteScopeBroken(GuiMockSuppressionSite site, string reason)
        {
            if (!Owns(site.Window)) return;
            if (brokenReason != null) return;
            brokenReason = reason ?? "unknown";
            ParsekLog.Warn(LogTag,
                "mock scope broken state=" + (stateId ?? "-") + " window=" + (window ?? "-")
                + " site=" + site.Site + " reason=" + brokenReason
                + " (the window is no longer drawing the mocked model)");
        }

        /// <summary>
        /// The form every production site calls: true when this site must skip its
        /// rebuild, and it logs ONE Verbose line per site per session while it is at it
        /// (never per frame - these sites run on a poll).
        /// </summary>
        internal static bool Suppressed(GuiMockSuppressionSite site)
        {
            if (!Owns(site.Window)) return false;
            if (notedSites.Add(site.Site))
            {
                ParsekLog.Verbose(LogTag,
                    "mock suppression site=" + site.Site + " owner=" + (window ?? "-")
                    + " state=" + (stateId ?? "-"));
            }
            return true;
        }

        /// <summary>
        /// Opens a scope. The caller has already installed the injection members and
        /// captured their previous values into <paramref name="restoreAction"/>.
        /// </summary>
        /// <returns>False when a scope is already live (the caller answers
        /// <c>mock-refused-session-live</c>); the state is untouched in that case.</returns>
        internal static bool Begin(string id, string windowToken, int frame,
                                   string utc, Action restoreAction)
        {
            if (stateId != null) return false;
            stateId = id;
            window = windowToken;
            appliedFrame = frame;
            appliedUtc = utc ?? string.Empty;
            restore = restoreAction;
            brokenReason = null;
            notedSites.Clear();
            return true;
        }

        /// <summary>
        /// Closes the live scope: runs the restore closure, drops the session, logs. Safe
        /// and silent with no scope live, which is what makes it callable unconditionally
        /// from every exit path (scene change, level loaded, FlushAndQuit, an exception
        /// anywhere in apply / settle / capture).
        /// </summary>
        /// <param name="reason">Why the scope closed - echoed on the log line.</param>
        /// <param name="heldFrames">Frames the scope was live, or -1 when unknown.</param>
        /// <param name="failure">The restore exception, or null.</param>
        /// <returns>True when a scope was live AND its restore ran without throwing.</returns>
        internal static bool Clear(string reason, int heldFrames, out Exception failure)
        {
            failure = null;
            if (stateId == null) return false;

            string closedId = stateId;
            string closedWindow = window;
            Action toRun = restore;
            stateId = null;
            window = null;
            restore = null;
            appliedFrame = 0;
            appliedUtc = null;
            brokenReason = null;
            notedSites.Clear();

            if (toRun != null)
            {
                try
                {
                    toRun();
                }
                catch (Exception ex)
                {
                    failure = ex;
                    ParsekLog.Error(LogTag,
                        "mock restore failed state=" + closedId + " window=" + closedWindow
                        + " exception=" + ex.GetType().Name + " reason=" + reason);
                    return false;
                }
            }

            ParsekLog.Info(LogTag,
                "mock restored state=" + closedId + " window=" + closedWindow
                + " heldFrames=" + heldFrames.ToString(CultureInfo.InvariantCulture)
                + " reason=" + reason);
            return true;
        }

        /// <summary>Convenience overload for the call sites that do not read the
        /// exception (the unconditional exit paths).</summary>
        internal static bool Clear(string reason, int heldFrames)
        {
            Exception ignored;
            return Clear(reason, heldFrames, out ignored);
        }

        /// <summary>
        /// Drops the session WITHOUT running restore. Only for the unit suite and for the
        /// <c>mock-restore-failed</c> tail, where restore has already thrown.
        /// </summary>
        internal static void ResetForTesting()
        {
            stateId = null;
            window = null;
            restore = null;
            appliedFrame = 0;
            appliedUtc = null;
            brokenReason = null;
            notedSites.Clear();
        }
    }
}
