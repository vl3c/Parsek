using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Which sub-action one <c>UiAction</c> call performs.</summary>
    internal enum UiActionOp
    {
        /// <summary>The <c>op=</c> arg did not parse (the command is a REJECTED).</summary>
        None = 0,

        /// <summary><c>op=open window=&lt;name&gt;</c>: raise a window's own open flag.</summary>
        Open = 1,

        /// <summary><c>op=close window=&lt;name&gt;</c>: lower it.</summary>
        Close = 2,

        /// <summary><c>op=tab window=&lt;name&gt; tab=&lt;name&gt;</c>: select one of a
        /// window's internal tabs / filter modes.</summary>
        Tab = 3,

        /// <summary><c>op=complexity mode=basic|advanced</c>: set the UI complexity
        /// mode through the production setter.</summary>
        Complexity = 4,

        /// <summary><c>op=rect window=&lt;name&gt; x= y= w= h=</c>: place and size a
        /// window so a capture shows more rows without scrolling.</summary>
        Rect = 5,

        /// <summary><c>op=describe</c>: the read-only inventory (scene, mode, and every
        /// window's availability / open state / rect / tabs).</summary>
        Describe = 6,
    }

    /// <summary>What one settle poll of a TWO-PHASE <c>UiAction</c> op concludes.</summary>
    internal enum UiActionSettleOutcome
    {
        /// <summary>Not enough frames have elapsed for an <c>OnGUI</c> pass to have run:
        /// poll again next frame.</summary>
        NotYet,

        /// <summary>A frame has been drawn since the write, so the read-back describes the
        /// SETTLED state and the terminal may be emitted.</summary>
        Settled,

        /// <summary>The budget ran out before a frame was drawn (a wedged renderer, or a
        /// pump that never reached another safe point): terminal ERROR.</summary>
        TimedOut,
    }

    /// <summary>A window rect, Unity-free so the parse and the payload stay pure.</summary>
    internal struct UiActionRect
    {
        internal float X;
        internal float Y;
        internal float W;
        internal float H;
    }

    /// <summary>
    /// One driveable window: its canonical wire name, the scenes that draw it, and its
    /// tab / filter-mode vocabulary.
    /// </summary>
    internal struct UiWindowSpec
    {
        /// <summary>The lower-case wire token. Stable: a spec names it and a capture label
        /// is built from it.</summary>
        internal string Name;

        /// <summary>Drawn by <c>ParsekFlight.OnGUI</c>.</summary>
        internal bool InFlight;

        /// <summary>Drawn by <c>ParsekKSC.OnGUI</c>.</summary>
        internal bool InSpaceCenter;

        /// <summary>Tab / filter-mode tokens in INDEX ORDER (the index is what the live
        /// selector field takes), or an empty array for a window with no selector.</summary>
        internal string[] Tabs;
    }

    /// <summary>One window's live state, as the describe payload reports it.</summary>
    internal struct UiWindowState
    {
        internal string Name;

        /// <summary>Whether the CURRENT scene draws this window at all.</summary>
        internal bool Available;

        /// <summary>The window's own open flag. False for an unavailable window.</summary>
        internal bool Open;

        /// <summary>True when <see cref="Rect"/> carries a measured rect. False before a
        /// window's first draw, when its rect field is still all-zero and the default is
        /// seeded from the main window's position on first open - reporting that zero as a
        /// position would send a reader hunting an off-screen window.</summary>
        internal bool RectKnown;

        internal UiActionRect Rect;

        /// <summary>The selected tab / filter-mode token, or null when the window has no
        /// selector (or is unavailable).</summary>
        internal string Tab;
    }

    /// <summary>
    /// Pure decision / payload half of the ADDITIVE, automation-only <c>UiAction</c> seam
    /// verb. The applier (<c>ParsekTestCommandAddon.UiAction.cs</c>) owns every touch of
    /// live <c>ParsekUI</c> state; the vocabulary, the arg parses, the scene-availability
    /// rule and the payload shapes live here so they are xUnit-covered without KSP.
    ///
    /// <para><b>WHY IT EXISTS.</b> A GUI review needs a picture of every Parsek window, and
    /// no seam verb could open one. The windows are IMGUI surfaces whose open flags are only
    /// ever written by a player click (the main window's toolbar callback, then a button in
    /// the main window per sub-window), so an unattended run draws NOTHING - a
    /// <c>CaptureScreenshot</c> on its own would photograph an empty scene. This verb is
    /// the other half of the census: it drives the EXISTING internal state
    /// (<c>IsOpen</c>, the tab fields, <c>ParsekUI.SetUiComplexityMode</c>) and adds no
    /// player-facing surface - no new window, no new button, nothing a player can reach.</para>
    ///
    /// <para><b>TWO OPS ARE TWO-PHASE; the rest are single-phase.</b>
    /// <see cref="OpIsTwoPhase"/> is the authority, and it names <c>open</c> and
    /// <c>rect</c> - the two whose read-back means nothing until a frame has been DRAWN.
    /// <c>open</c> writes a flag a drawing window can put straight back down
    /// (<c>SpawnControlUI.DrawIfOpen</c> force-closes itself with zero candidates in
    /// range, so a single-phase OK would have let a census photograph empty scenery under
    /// a plausible label), and <c>rect</c> writes a rect <c>GUILayout</c> only resolves
    /// during the draw - so an immediate read-back compared the field with the value just
    /// written to it, and <see cref="RectAppliedWithinTolerance"/> could never fire. Both
    /// hold the FIFO head for one frame and then read back;
    /// <see cref="DecideSettlePoll"/> owns that decision.</para>
    ///
    /// <para><c>close</c>, <c>tab</c>, <c>complexity</c> and <c>describe</c> stay
    /// SINGLE-PHASE - a synchronous write followed by a read-back, the
    /// <c>EnterMapView</c> / <c>DeleteRecording</c> shape. The one that LOOKS deferred is
    /// <c>complexity</c>: <c>ParsekUI.SetUiComplexityMode</c> only QUEUES the draw-visible
    /// value, which a later <c>Update</c> latches through
    /// <c>ApplyPendingUiComplexityModeIfAny</c>. The seam pump itself runs in <c>Update</c>,
    /// which is exactly where that latch is contractually allowed to be called (never from
    /// OnGUI), so the applier calls it directly and reads back
    /// <c>AppliedUiComplexityMode</c>. That keeps the op single-phase AND leaves the
    /// production latch as the only path that ever applies a mode.</para>
    ///
    /// <para><b>WHY THE WINDOW VOCABULARY IS A TABLE HERE.</b> A census spec names each
    /// window in a step and again in the capture label, so the token set must be stable and
    /// its scene availability must be a decision with a cell on it rather than a
    /// null-reference at run time. A window that the current scene does not draw is a
    /// REJECTED (<see cref="WindowNotInSceneReason"/>) naming the scene, not a silent OK:
    /// an "opened" Gloops recorder at the Space Center would produce a capture of the
    /// scene without it and read as a render defect.</para>
    /// </summary>
    internal static class TestCommandUiAction
    {
        // ----- op tokens -----

        internal const string OpenOpToken = "open";
        internal const string CloseOpToken = "close";
        internal const string TabOpToken = "tab";
        internal const string ComplexityOpToken = "complexity";
        internal const string RectOpToken = "rect";
        internal const string DescribeOpToken = "describe";

        // ----- complexity tokens (the wire spelling of UiComplexityMode) -----

        internal const string BasicModeToken = "basic";
        internal const string AdvancedModeToken = "advanced";

        // ----- window tokens -----

        internal const string MainWindow = "main";
        internal const string MissionsWindow = "missions";
        internal const string TimelineWindow = "timeline";
        internal const string KerbalsWindow = "kerbals";
        internal const string CareerWindow = "career";
        internal const string LogisticsWindow = "logistics";
        internal const string StructureWindow = "structure";
        internal const string SettingsWindow = "settings";
        internal const string SpawnControlWindow = "spawncontrol";
        internal const string GloopsWindow = "gloops";
        internal const string TestRunnerWindow = "testrunner";

        // ----- reject / error reasons -----

        /// <summary>No <c>op=</c> arg. REQUIRED, never defaulted: guessing an op would let a
        /// typo'd spec mutate the UI in a way it never asked for.</summary>
        internal const string OpArgMissingReason = "op-arg-missing";

        /// <summary>An <c>op=</c> value outside the closed set. Fail-closed and
        /// case-sensitive.</summary>
        internal const string OpArgInvalidReason = "op-arg-invalid";

        /// <summary>An op that needs <c>window=</c> did not get it.</summary>
        internal const string WindowArgMissingReason = "window-arg-missing";

        /// <summary>A <c>window=</c> value outside the table. The response MESSAGE carries
        /// the full valid list (<see cref="ValidWindowNames"/>) so a spec author does not
        /// have to read the source to find the spelling.</summary>
        internal const string WindowUnknownReason = "window-unknown";

        /// <summary>A known window the CURRENT scene does not draw (the two flight-only
        /// windows at the Space Center). REJECTED, and the message names the scene.</summary>
        internal const string WindowNotInSceneReason = "window-not-in-scene";

        /// <summary><c>op=tab</c> without a <c>tab=</c> arg.</summary>
        internal const string TabArgMissingReason = "tab-arg-missing";

        /// <summary>A <c>tab=</c> value the named window does not offer. The message carries
        /// that window's own tab list.</summary>
        internal const string TabUnknownReason = "tab-unknown";

        /// <summary><c>op=tab</c> against a window with no tab / filter selector at all.
        /// Distinct from <see cref="TabUnknownReason"/> on purpose: "this window has no
        /// tabs" and "this window has tabs but not that one" send an author to different
        /// fixes.</summary>
        internal const string WindowHasNoTabsReason = "window-has-no-tabs";

        /// <summary><c>op=complexity</c> without a <c>mode=</c> arg.</summary>
        internal const string ModeArgMissingReason = "mode-arg-missing";

        /// <summary>A <c>mode=</c> value outside {basic, advanced}.</summary>
        internal const string ModeArgInvalidReason = "mode-arg-invalid";

        /// <summary><c>op=rect</c> is missing one or more of <c>x/y/w/h</c>. All four are
        /// required: a partial rect would mix a commanded position with a stale size and the
        /// resulting capture would be unreproducible.</summary>
        internal const string RectArgMissingReason = "rect-arg-missing";

        /// <summary>An <c>x/y/w/h</c> value did not parse as an invariant-culture float in
        /// range. Notably a non-positive or absurd size: a zero-width window draws nothing
        /// and would photograph as a missing window.</summary>
        internal const string RectArgInvalidReason = "rect-arg-invalid";

        /// <summary>PRE-CALL gate: there is no live <c>ParsekUI</c> instance in this scene
        /// (the Tracking Station and the editor host none - see
        /// <c>ParsekTrackingStation.OnGUI</c>, which draws markers only). REJECTED, the
        /// <c>map-view-unavailable</c> class. NOT a defer: the dispatcher already waited for
        /// a loaded game, and a scene that hosts no Parsek UI is a state this verb does not
        /// drive rather than a not-yet.</summary>
        internal const string HostUnavailableReason = "ui-host-unavailable";

        /// <summary>PRE-CALL gate for the ONE production refusal
        /// (<c>ParsekUI.ShouldRefuseModeChange</c>): switching to Basic while a Gloops
        /// recording runs. Checked explicitly rather than inferred from a failed read-back
        /// so the response names the cause (the <c>EnterWatchMode</c> discipline).</summary>
        internal const string ComplexityRefusedRecordingReason = "complexity-refused-gloops-recording";

        /// <summary>POST-CALL terminal: the mode was set and latched and
        /// <c>AppliedUiComplexityMode</c> still disagrees. ERROR - we acted and the game
        /// did not follow.</summary>
        internal const string ComplexityNotAppliedReason = "complexity-not-applied";

        /// <summary>POST-CALL terminal, checked IMMEDIATELY after the write: the open flag
        /// reads back wrong before any frame has been drawn. Only reachable if a window's
        /// own setter refuses the value outright, which is why it is an ERROR rather than a
        /// silent OK. Distinct from <see cref="WindowSelfClosedReason"/> - see there.</summary>
        internal const string WindowNotToggledReason = "window-not-toggled";

        /// <summary>
        /// POST-SETTLE terminal for <c>op=open</c>: the flag WAS raised, and a window that
        /// drew itself put it back down.
        ///
        /// <para>This is why <c>op=open</c> is two-phase, and the case is real rather than
        /// hypothetical: <c>SpawnControlUI.DrawIfOpen</c> force-closes itself on its FIRST
        /// draw whenever <c>ResolveAutoCloseReason</c> fires (not in flight, no flight
        /// reference, or ZERO nearby spawn candidates - the last of which is the normal
        /// state of any craft with nothing recorded passing close by). A single-phase OK
        /// reported the flag it had just written, so a census would have captured a PNG of
        /// empty scenery under the label <c>flight-spawncontrol-advanced</c> and a reviewer
        /// would have read it as a render defect.</para>
        ///
        /// <para>Kept apart from <see cref="WindowNotToggledReason"/> because the two send
        /// an operator to different places: "the setter refused" is a Parsek-side defect in
        /// the window class, while "it drew and closed itself" is a statement about the
        /// SCENE the lane is flying, whose remedy is a different host.</para>
        /// </summary>
        internal const string WindowSelfClosedReason = "window-self-closed";

        /// <summary>POST-CALL terminal for the two-phase ops: the verb's budget expired
        /// before a single frame was drawn, so the read-back could never have described a
        /// settled state. ERROR, and it means the game stopped rendering or the pump never
        /// reached another safe point - not that the op was refused.</summary>
        internal const string NotSettledReason = "ui-action-not-settled";

        /// <summary>POST-CALL terminal: the tab index was written and reads back wrong.</summary>
        internal const string TabNotAppliedReason = "tab-not-applied";

        /// <summary>POST-CALL terminal: the rect was written and reads back different. Note
        /// the read-back is deliberately tolerant on HEIGHT for the windows GUILayout sizes
        /// from content - see <see cref="RectAppliedWithinTolerance"/>.</summary>
        internal const string RectNotAppliedReason = "rect-not-applied";

        /// <summary>POST-CALL terminal for the one thing a read-back cannot describe: a
        /// live-state touch THREW.</summary>
        internal const string ThrewReason = "ui-action-threw";

        // ----- rect bounds -----

        /// <summary>Smallest accepted window width. Below this the title bar itself clips
        /// and the capture shows a sliver.</summary>
        internal const float MinRectWidth = 120f;

        /// <summary>Smallest accepted window height.</summary>
        internal const float MinRectHeight = 60f;

        /// <summary>Largest accepted width / height. Far above any real screen; the bound
        /// exists so a mis-typed exponent cannot ask for a gigapixel layout pass.</summary>
        internal const float MaxRectSize = 8192f;

        /// <summary>Bound on <c>x</c> / <c>y</c>. Negative is ALLOWED (a deliberately
        /// off-left window is a legitimate way to park one), but not unboundedly.</summary>
        internal const float MaxRectOrigin = 8192f;

        /// <summary>
        /// Read-back tolerance, in pixels, for an applied rect.
        ///
        /// <para>It exists because the windows are <c>GUILayout</c> windows: each passes
        /// <c>GUILayout.Width/Height</c> options and the resolved height is
        /// <c>Max(passedHeight, contentMin)</c>, so a window whose content is taller than
        /// the commanded height legitimately GROWS - and the KSC main window resets its
        /// height to 0 every frame by design. A strict equality read-back would therefore
        /// ERROR on a rect that was applied exactly as asked. Position and width are the
        /// halves that hold, so the tolerance is only ever consulted for height (see
        /// <see cref="RectAppliedWithinTolerance"/>).</para>
        /// </summary>
        internal const float RectReadBackTolerance = 1f;

        // ----- the window table -----
        //
        // ORDER IS THE MAIN WINDOW'S OWN BUTTON ORDER (ParsekUI.DrawWindow), so a describe
        // payload reads down the same list a reviewer sees on screen, and a census spec's
        // capture labels sort into that order too. `main` leads because it hosts the rest.
        //
        // THE THREE DELIBERATE EXCLUSIONS, derived from a grep of every
        // ClickThruBlocker.GUILayoutWindow / GUILayout.Window host under Source/Parsek
        // rather than from memory, so "eleven windows" is a claim about the whole program:
        //   - GroupPickerUI (UI/GroupPickerUI.cs, "Set Parent Group" / "Manage Groups") is
        //     a real window with its own rect and its own input lock, and it IS in the
        //     Advanced -> Basic close set. It is excluded because it cannot be opened
        //     MEANINGFULLY: it is a popup over a SELECTION (a recording row, a group node),
        //     so raising its flag with no selection armed would photograph an empty picker
        //     - the GUI-CENSUS-STRUCTURE-WINDOW-HAS-NO-DRIVEABLE-TARGET shape, and worse.
        //   - LogisticsWindowUI's round-trip LINK PICKER (UI/LogisticsWindowUI.cs, its own
        //     GUILayoutWindow drawn from DrawIfOpen) is excluded for exactly that reason:
        //     it is armed from a Logistics ROW and carries that row's source state.
        //   - TestRunnerShortcut (InGameTests/TestRunnerShortcut.cs) shares the Test Runner
        //     title but is a SEPARATE MonoBehaviour with no accessor and no complexity
        //     gate; the `testrunner` token below is the Settings-launched TestRunnerUI.
        // Adding any of the three needs a way to drive its CONTEXT first, not just a table
        // row - that is the honest cost, and it is why they are named here rather than
        // silently absent.

        private static readonly UiWindowSpec[] WindowTable = new[]
        {
            // The host-owned toolbar window. No tabs; its own rect is content-sized
            // (BOTH hosts pass GUILayout.Width(250) and BOTH zero the height every
            // frame - ParsekFlight.cs's `windowRect.height = 0f` sits inside its showUI
            // gate exactly as ParsekKSC.cs's does), so an `op=rect` on it moves it and
            // does not resize it.
            NewSpec(MainWindow, true, true),

            // RecordingsTableUI. Titled "Parsek - Missions": the window IS the Missions
            // surface and the raw table is its second TAB, which is why the token is
            // `missions` and not `recordings`.
            NewSpec(MissionsWindow, true, true, "missions", "recordings"),

            // TimelineWindowUI. Its selector is not a GUILayout.Toolbar but a row of
            // filter buttons over TimelineTierFilterMode; treated as tabs here because it
            // is the same thing for a census - four mutually exclusive views of one
            // window, each of which has to be photographed.
            NewSpec(TimelineWindow, true, true, "overview", "details", "rewindff", "refly"),

            NewSpec(KerbalsWindow, true, true, "roster", "outcomes"),

            NewSpec(CareerWindow, true, true,
                "contracts", "strategies", "facilities", "milestones"),

            // LogisticsWindowUI. NO tabs: its Active / Paused / Dormant / Candidates /
            // Near-miss / Dismissed bubbles are EXPAND-COLLAPSE sections drawn in one
            // pass, not a selector, so one capture shows the window and `op=tab` on it is
            // a WindowHasNoTabsReason rather than a silently ignored arg.
            NewSpec(LogisticsWindow, true, true),

            // StructureListWindowUI. Opening it with no target shows the empty
            // "Parsek - Structure" chrome; the populated forms are reached from a
            // Missions / Logistics row, which no seam op drives today (noted in the
            // census spec's header as a follow-up rather than faked here).
            NewSpec(StructureWindow, true, true),

            // SettingsWindowUI. NO tabs: its six sections all draw in one pass, and three
            // of them are hidden in Basic - so the Basic/Advanced pair of captures IS the
            // section coverage.
            NewSpec(SettingsWindow, true, true),

            // Flight-only: ParsekKSC.OnGUI does not draw either, and SpawnControlUI
            // additionally self-closes out of flight (ResolveAutoCloseReason).
            NewSpec(SpawnControlWindow, true, false),
            NewSpec(GloopsWindow, true, false),

            // The Settings-launched TestRunnerUI. NOT the global Ctrl+Shift+T window
            // (TestRunnerShortcut), which shares the same title but is a separate
            // MonoBehaviour whose flag has no accessor and no complexity gate.
            NewSpec(TestRunnerWindow, true, true),
        };

        private static UiWindowSpec NewSpec(string name, bool inFlight, bool inKsc,
                                            params string[] tabs)
        {
            return new UiWindowSpec
            {
                Name = name,
                InFlight = inFlight,
                InSpaceCenter = inKsc,
                Tabs = tabs ?? new string[0],
            };
        }

        /// <summary>The window table, in describe order. Read-only view for the applier and
        /// for coverage cells.</summary>
        internal static IReadOnlyList<UiWindowSpec> Windows => WindowTable;

        /// <summary>Every valid window token, comma-joined in table order. This is what the
        /// <see cref="WindowUnknownReason"/> message carries.</summary>
        internal static string ValidWindowNames
        {
            get
            {
                var names = new List<string>(WindowTable.Length);
                foreach (UiWindowSpec spec in WindowTable) names.Add(spec.Name);
                return string.Join(",", names.ToArray());
            }
        }

        /// <summary>Every valid op token, comma-joined. Carried by the
        /// <see cref="OpArgInvalidReason"/> message.</summary>
        internal static string ValidOpNames => string.Join(",", new[]
        {
            OpenOpToken, CloseOpToken, TabOpToken, ComplexityOpToken, RectOpToken,
            DescribeOpToken,
        });

        /// <summary>A window's tab tokens, comma-joined, or the empty string when it has
        /// none. Carried by the <see cref="TabUnknownReason"/> message.</summary>
        internal static string TabNamesOf(UiWindowSpec spec)
            => spec.Tabs == null || spec.Tabs.Length == 0
                ? string.Empty
                : string.Join(",", spec.Tabs);

        // ----- arg parses -----

        /// <summary>Parses the required <c>op=</c> arg.</summary>
        internal static bool TryParseOp(string raw, out UiActionOp op, out string rejectReason)
        {
            op = UiActionOp.None;
            if (raw == null)
            {
                rejectReason = OpArgMissingReason;
                return false;
            }
            switch (raw)
            {
                case OpenOpToken: op = UiActionOp.Open; break;
                case CloseOpToken: op = UiActionOp.Close; break;
                case TabOpToken: op = UiActionOp.Tab; break;
                case ComplexityOpToken: op = UiActionOp.Complexity; break;
                case RectOpToken: op = UiActionOp.Rect; break;
                case DescribeOpToken: op = UiActionOp.Describe; break;
                default:
                    rejectReason = OpArgInvalidReason;
                    return false;
            }
            rejectReason = null;
            return true;
        }

        /// <summary>The wire token for a parsed op (what the payload echoes back).</summary>
        internal static string OpToken(UiActionOp op)
        {
            switch (op)
            {
                case UiActionOp.Open: return OpenOpToken;
                case UiActionOp.Close: return CloseOpToken;
                case UiActionOp.Tab: return TabOpToken;
                case UiActionOp.Complexity: return ComplexityOpToken;
                case UiActionOp.Rect: return RectOpToken;
                case UiActionOp.Describe: return DescribeOpToken;
                default: return string.Empty;
            }
        }

        /// <summary>True for the ops that require a <c>window=</c> arg.</summary>
        internal static bool OpNeedsWindow(UiActionOp op)
            => op == UiActionOp.Open || op == UiActionOp.Close
               || op == UiActionOp.Tab || op == UiActionOp.Rect;

        // ----- the two-phase ops -----

        /// <summary>Frames that must be DRAWN between the write and the read-back of a
        /// two-phase op. ONE is enough and is what the shape needs: Unity runs
        /// <c>Update</c> (where the seam pump lives) before <c>OnGUI</c>, so a single
        /// advanced frame guarantees at least one full IMGUI pass - the pass in which a
        /// self-closing window closes itself and in which <c>GUILayout</c> resolves a
        /// window's rect.</summary>
        internal const int SettleFrames = 1;

        /// <summary>
        /// True for the ops whose read-back is only meaningful AFTER a frame has been
        /// drawn, i.e. the ops that hold the FIFO head for one frame.
        ///
        /// <para><c>open</c> and <c>rect</c>, and neither is arbitrary. <c>open</c> writes
        /// a flag a drawing window can put back down
        /// (<see cref="WindowSelfClosedReason"/>), and <c>rect</c> writes a rect
        /// <c>GUILayout</c> resolves during the draw - so before a frame runs, both read
        /// back exactly the value just written and the read-back proves nothing at all.
        /// That was the defect: <see cref="RectAppliedWithinTolerance"/> was comparing a
        /// field with itself.</para>
        ///
        /// <para>The MIRROR DIRECTION, checked rather than assumed: <c>close</c> is
        /// deliberately NOT here. The asymmetry is real - a drawing window can lower its
        /// own flag (`showSpawnControlWindow = false` in
        /// <c>SpawnControlUI.DrawIfOpen</c>) and nothing in the mod RAISES one from a draw
        /// path, so there is no self-opening window for a settled close read-back to catch.
        /// <c>tab</c> and <c>complexity</c> are likewise single-phase: the tab clamp runs
        /// from the complexity latch (which the applier drives synchronously in
        /// <c>Update</c>, before any draw), not from a draw.</para>
        /// </summary>
        internal static bool OpIsTwoPhase(UiActionOp op)
            => op == UiActionOp.Open || op == UiActionOp.Rect;

        /// <summary>
        /// One settle poll of a two-phase op.
        ///
        /// <para>ORDER MATTERS, the <c>CaptureScreenshot.DecidePoll</c> rule: settled is
        /// decided BEFORE the budget, so a frame that landed on the very poll the budget
        /// expired is a success rather than an ERROR over state that is already correct.</para>
        /// </summary>
        /// <param name="framesElapsed">Frames drawn since the write (the applier passes
        /// <c>Time.frameCount</c> minus the frame it wrote in).</param>
        /// <param name="budgetExpired">Whether the verb's deferral budget has run out.</param>
        internal static UiActionSettleOutcome DecideSettlePoll(int framesElapsed,
                                                               bool budgetExpired)
        {
            if (framesElapsed >= SettleFrames) return UiActionSettleOutcome.Settled;
            if (budgetExpired) return UiActionSettleOutcome.TimedOut;
            return UiActionSettleOutcome.NotYet;
        }

        /// <summary>
        /// Whether <c>op=complexity</c> has nothing to do.
        ///
        /// <para>BOTH halves are required, and the second is the one that was missing:
        /// <c>ParsekUI.SetUiComplexityMode</c> NO-OPS when the requested mode already
        /// equals the PERSISTED setting, so on a save whose setting says Basic while the
        /// latch is the fail-open Advanced (a <c>ParsekUI</c> constructed before
        /// <c>ParsekSettings.Current</c> existed), a request for Basic looked "not already
        /// satisfied", called a setter that queued nothing, and terminated
        /// <see cref="ComplexityNotAppliedReason"/> on a mode the save actually
        /// carried.</para>
        /// </summary>
        internal static bool IsComplexityAlreadySatisfied(
            UiComplexityMode applied, UiComplexityMode persisted, UiComplexityMode want)
            => applied == want && persisted == want;

        /// <summary>
        /// Resolves a <c>window=</c> token against the table. A missing arg and an unknown
        /// value are DISTINCT rejects, for the same reason
        /// <see cref="TestCommandCaptureScreenshot.LabelArgMissingReason"/> is distinct from
        /// its invalid sibling.
        /// </summary>
        internal static bool TryResolveWindow(string raw, out UiWindowSpec spec,
                                              out string rejectReason)
        {
            spec = default(UiWindowSpec);
            if (raw == null)
            {
                rejectReason = WindowArgMissingReason;
                return false;
            }
            foreach (UiWindowSpec candidate in WindowTable)
            {
                if (candidate.Name == raw)
                {
                    spec = candidate;
                    rejectReason = null;
                    return true;
                }
            }
            rejectReason = WindowUnknownReason;
            return false;
        }

        /// <summary>
        /// Whether <paramref name="scene"/> draws <paramref name="spec"/>.
        ///
        /// <para>Only FLIGHT and SPACECENTER host Parsek windows at all; every other scene
        /// answers false and the applier's <see cref="HostUnavailableReason"/> gate normally
        /// fires first. Keeping the rule here rather than in the applier is what lets a cell
        /// pin that the two flight-only windows are exactly
        /// <c>spawncontrol</c> and <c>gloops</c>.</para>
        /// </summary>
        internal static bool IsAvailableInScene(UiWindowSpec spec, TestCommandScene scene)
        {
            if (scene == TestCommandScene.Flight) return spec.InFlight;
            if (scene == TestCommandScene.SpaceCenter) return spec.InSpaceCenter;
            return false;
        }

        /// <summary>
        /// Resolves a <c>tab=</c> token against a window's own vocabulary, yielding the
        /// INDEX the live selector field takes.
        /// </summary>
        internal static bool TryResolveTab(UiWindowSpec spec, string raw, out int index,
                                           out string rejectReason)
        {
            index = -1;
            if (spec.Tabs == null || spec.Tabs.Length == 0)
            {
                rejectReason = WindowHasNoTabsReason;
                return false;
            }
            if (raw == null)
            {
                rejectReason = TabArgMissingReason;
                return false;
            }
            for (int i = 0; i < spec.Tabs.Length; i++)
            {
                if (spec.Tabs[i] == raw)
                {
                    index = i;
                    rejectReason = null;
                    return true;
                }
            }
            rejectReason = TabUnknownReason;
            return false;
        }

        /// <summary>The token for a tab index, or null when the index is out of range (a
        /// window whose live field holds something the table does not model - reported as
        /// absent rather than guessed).</summary>
        internal static string TabTokenAt(UiWindowSpec spec, int index)
        {
            if (spec.Tabs == null || index < 0 || index >= spec.Tabs.Length) return null;
            return spec.Tabs[index];
        }

        /// <summary>Parses the required <c>mode=</c> arg. <paramref name="basic"/> is true
        /// for <c>basic</c>, false for <c>advanced</c>.</summary>
        internal static bool TryParseMode(string raw, out bool basic, out string rejectReason)
        {
            basic = false;
            if (raw == null)
            {
                rejectReason = ModeArgMissingReason;
                return false;
            }
            if (raw == BasicModeToken) { basic = true; rejectReason = null; return true; }
            if (raw == AdvancedModeToken) { basic = false; rejectReason = null; return true; }
            rejectReason = ModeArgInvalidReason;
            return false;
        }

        /// <summary>The wire token for a mode.</summary>
        internal static string ModeToken(bool basic)
            => basic ? BasicModeToken : AdvancedModeToken;

        /// <summary>
        /// Parses the four required rect args. All four must be present (a partial rect
        /// mixes a commanded position with a stale size, so the resulting capture would not
        /// be reproducible) and each must be a finite invariant-culture float in range.
        /// </summary>
        internal static bool TryParseRect(string rawX, string rawY, string rawW, string rawH,
                                          out UiActionRect rect, out string rejectReason)
        {
            rect = default(UiActionRect);
            if (rawX == null || rawY == null || rawW == null || rawH == null)
            {
                rejectReason = RectArgMissingReason;
                return false;
            }
            float x, y, w, h;
            if (!TryParseFinite(rawX, out x) || !TryParseFinite(rawY, out y)
                || !TryParseFinite(rawW, out w) || !TryParseFinite(rawH, out h))
            {
                rejectReason = RectArgInvalidReason;
                return false;
            }
            if (x < -MaxRectOrigin || x > MaxRectOrigin
                || y < -MaxRectOrigin || y > MaxRectOrigin
                || w < MinRectWidth || w > MaxRectSize
                || h < MinRectHeight || h > MaxRectSize)
            {
                rejectReason = RectArgInvalidReason;
                return false;
            }
            rect = new UiActionRect { X = x, Y = y, W = w, H = h };
            rejectReason = null;
            return true;
        }

        private static bool TryParseFinite(string raw, out float value)
        {
            value = 0f;
            float parsed;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out parsed))
                return false;
            if (float.IsNaN(parsed) || float.IsInfinity(parsed))
                return false;
            value = parsed;
            return true;
        }

        /// <summary>
        /// Read-back predicate for an applied rect.
        ///
        /// <para>Position and WIDTH must land within <see cref="RectReadBackTolerance"/>.
        /// HEIGHT is checked as a FLOOR (<c>observed &gt;= commanded - tolerance</c>) rather
        /// than as equality, because a <c>GUILayout</c> window resolves to
        /// <c>Max(passedHeight, contentMin)</c>: a window whose content is taller than the
        /// commanded height grows, which is the rect being applied correctly and not a
        /// failure. The asymmetry is the whole reason this predicate is a named, tested
        /// function instead of four inline comparisons.</para>
        ///
        /// <para>The MAIN window is exempt from the width check as well: BOTH hosts pass a
        /// fixed <c>GUILayout.Width(250)</c> and BOTH zero its height every frame
        /// (<c>ParsekFlight.OnGUI</c> and <c>ParsekKSC.OnGUI</c> each open with
        /// <c>windowRect.height = 0f</c>), so only its POSITION is commandable. The applier
        /// expresses that by passing <paramref name="sizeIsHostControlled"/>.</para>
        /// </summary>
        internal static bool RectAppliedWithinTolerance(
            UiActionRect commanded, UiActionRect observed, bool sizeIsHostControlled)
        {
            if (Math.Abs(commanded.X - observed.X) > RectReadBackTolerance) return false;
            if (Math.Abs(commanded.Y - observed.Y) > RectReadBackTolerance) return false;
            if (sizeIsHostControlled) return true;
            if (Math.Abs(commanded.W - observed.W) > RectReadBackTolerance) return false;
            if (observed.H < commanded.H - RectReadBackTolerance) return false;
            return true;
        }

        /// <summary>True for a window whose rect SIZE the scene host owns rather than the
        /// window itself: only <see cref="MainWindow"/> today.</summary>
        internal static bool SizeIsHostControlled(string windowName)
            => windowName == MainWindow;

        // ----- payload builders -----
        //
        // Every builder leads with `op` so a reader (and a ${step.field} substitution) can
        // tell which shape follows. Values are InvariantCulture; the response writer owns
        // the percent-encoding, so nothing here pre-encodes.

        /// <summary>Formats one rect coordinate. <c>F0</c>: window positions are whole
        /// pixels and a full round-trip "R" would put 9 digits of float noise on the wire
        /// for no reader.</summary>
        internal static string FormatCoord(float value)
            => value.ToString("F0", CultureInfo.InvariantCulture);

        /// <summary>The compact rect token: <c>x,y,w,h</c>. One key instead of four keeps a
        /// describe line over eleven windows scannable, and a comma needs no
        /// percent-encoding.</summary>
        internal static string FormatRect(UiActionRect rect)
            => FormatCoord(rect.X) + "," + FormatCoord(rect.Y) + ","
               + FormatCoord(rect.W) + "," + FormatCoord(rect.H);

        /// <summary>OK payload for <c>open</c> / <c>close</c>:
        /// <c>op=&lt;op&gt; window=&lt;name&gt; open=&lt;bool&gt; already=&lt;bool&gt;</c>.
        /// <c>already</c> is always present (the <c>EnterMapView alreadyOpen</c> rule): a
        /// census needs to know whether ITS step is what opened the window.</summary>
        internal static List<KeyValuePair<string, string>> BuildTogglePayload(
            UiActionOp op, string window, bool openAfter, bool already)
            => new List<KeyValuePair<string, string>>
            {
                Kv("op", OpToken(op)),
                Kv("window", window ?? string.Empty),
                Kv("open", Bool(openAfter)),
                Kv("already", Bool(already)),
            };

        /// <summary>OK payload for <c>tab</c>: <c>op=tab window= tab= index= already=</c>.</summary>
        internal static List<KeyValuePair<string, string>> BuildTabPayload(
            string window, string tab, int index, bool already)
            => new List<KeyValuePair<string, string>>
            {
                Kv("op", TabOpToken),
                Kv("window", window ?? string.Empty),
                Kv("tab", tab ?? string.Empty),
                Kv("index", index.ToString(CultureInfo.InvariantCulture)),
                Kv("already", Bool(already)),
            };

        /// <summary>OK payload for <c>complexity</c>: <c>op=complexity mode= already=</c>.</summary>
        internal static List<KeyValuePair<string, string>> BuildComplexityPayload(
            bool basic, bool already)
            => new List<KeyValuePair<string, string>>
            {
                Kv("op", ComplexityOpToken),
                Kv("mode", ModeToken(basic)),
                Kv("already", Bool(already)),
            };

        /// <summary>OK payload for <c>rect</c>: <c>op=rect window= rect=x,y,w,h</c>, where
        /// the rect is the READ-BACK and not the request - a height GUILayout grew is
        /// reported as it really is.</summary>
        internal static List<KeyValuePair<string, string>> BuildRectPayload(
            string window, UiActionRect observed)
            => new List<KeyValuePair<string, string>>
            {
                Kv("op", RectOpToken),
                Kv("window", window ?? string.Empty),
                Kv("rect", FormatRect(observed)),
            };

        /// <summary>
        /// OK payload for <c>describe</c>: the inventory the supervisor reads.
        ///
        /// <para><c>op=describe scene=&lt;token&gt; complexity=&lt;mode&gt;
        /// count=&lt;n&gt;</c> then, per window in table order,
        /// <c>w&lt;i&gt;=&lt;name&gt; w&lt;i&gt;avail= w&lt;i&gt;open= w&lt;i&gt;rect=
        /// w&lt;i&gt;tabs= w&lt;i&gt;tab=</c>.</para>
        ///
        /// <para>EVERY window gets a row, including the ones this scene does not draw, and
        /// EVERY row carries all six keys. An absent row would be indistinguishable from an
        /// older seam build, and a reader comparing a KSC describe with a FLIGHT one needs
        /// the flight-only rows present-and-unavailable rather than missing. A window with
        /// no tabs reports <c>tabs=-</c> / <c>tab=-</c>, and an unmeasured rect reports
        /// <c>rect=-</c>: a single-character sentinel rather than an empty value, because a
        /// trailing <c>key=</c> on the wire is easy to misread as a truncated line.</para>
        ///
        /// <para>The list is NOT capped. Unlike <c>ListHandles</c>, whose families are
        /// unbounded in the save, this one is bounded BY CONSTRUCTION at
        /// <see cref="Windows"/>.Count - the table is a compile-time literal, so the line
        /// length is a constant of the build.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildDescribePayload(
            string sceneToken, bool basic, IReadOnlyList<UiWindowState> rows)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            int count = rows != null ? rows.Count : 0;
            var payload = new List<KeyValuePair<string, string>>
            {
                Kv("op", DescribeOpToken),
                Kv("scene", sceneToken ?? string.Empty),
                Kv("complexity", ModeToken(basic)),
                Kv("count", count.ToString(ic)),
            };
            for (int i = 0; i < count; i++)
            {
                UiWindowState row = rows[i];
                string prefix = "w" + i.ToString(ic);
                payload.Add(Kv(prefix, row.Name ?? string.Empty));
                payload.Add(Kv(prefix + "avail", Bool(row.Available)));
                payload.Add(Kv(prefix + "open", Bool(row.Open)));
                payload.Add(Kv(prefix + "rect", row.RectKnown ? FormatRect(row.Rect) : "-"));
                string tabs = TabsTokenFor(row.Name);
                payload.Add(Kv(prefix + "tabs", tabs));
                payload.Add(Kv(prefix + "tab", string.IsNullOrEmpty(row.Tab) ? "-" : row.Tab));
            }
            return payload;
        }

        /// <summary>
        /// The names of the windows this describe found OPEN, comma-joined in table order,
        /// or <c>-</c> when none is.
        ///
        /// <para>It exists for the LOG line rather than for the payload. The per-window
        /// <c>w&lt;i&gt;open=</c> keys are on the wire, which never reaches
        /// <c>KSP.log</c> - so a census spec's <c>[expectations.logContracts]</c> could not
        /// assert "exactly this window is open" at all. Paired with the <c>open=&lt;n&gt;</c>
        /// count on the same line it is an EXACT claim: `open=2 openWindows=main,missions`
        /// cannot match a scene with a third window open, because the count would differ,
        /// which a bare per-window regex could never have expressed.</para>
        /// </summary>
        internal static string FormatOpenWindowList(IReadOnlyList<UiWindowState> rows)
        {
            if (rows == null) return "-";
            var open = new List<string>();
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Open) open.Add(rows[i].Name ?? string.Empty);
            return open.Count == 0 ? "-" : string.Join(",", open.ToArray());
        }

        /// <summary>The <c>tabs=</c> value for a window name: its comma-joined vocabulary,
        /// or <c>-</c> for a window with none (or an unknown name, which the applier cannot
        /// produce but a hand-built row could).</summary>
        internal static string TabsTokenFor(string windowName)
        {
            foreach (UiWindowSpec spec in WindowTable)
            {
                if (spec.Name != windowName) continue;
                string tabs = TabNamesOf(spec);
                return string.IsNullOrEmpty(tabs) ? "-" : tabs;
            }
            return "-";
        }

        /// <summary>The <c>scene=</c> token for a dispatch scene. Upper-case KSP-ish names
        /// so a reader sees the same word the log lines carry.</summary>
        internal static string SceneToken(TestCommandScene scene)
        {
            switch (scene)
            {
                case TestCommandScene.Flight: return "FLIGHT";
                case TestCommandScene.SpaceCenter: return "SPACECENTER";
                case TestCommandScene.TrackingStation: return "TRACKSTATION";
                case TestCommandScene.Editor: return "EDITOR";
                case TestCommandScene.MainMenu: return "MAINMENU";
                case TestCommandScene.Loading: return "LOADING";
                default: return "OTHER";
            }
        }

        private static KeyValuePair<string, string> Kv(string k, string v)
            => new KeyValuePair<string, string>(k, v);

        private static string Bool(bool b) => b ? "true" : "false";
    }
}
