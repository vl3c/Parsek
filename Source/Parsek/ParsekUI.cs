using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // No TrackingStation member: the Tracking Station hosts no Parsek window (its OnGUI
    // draws markers only, ParsekTrackingStation.cs), so nothing ever constructed one and
    // the only branch that read it was unreachable (GUI census D11). Adding a TS surface
    // starts from zero, and would add the member back with a real constructor call.
    public enum UIMode { Flight, KSC }

    /// <summary>
    /// UI rendering for the Parsek window and map view markers.
    /// Receives a ParsekFlight reference for accessing flight state.
    /// </summary>
    public class ParsekUI
    {
        private readonly UIMode mode;
        private bool InFlight => mode == UIMode.Flight;

        private readonly ParsekFlight flight;

        // Opaque window style (replaces KSP's semi-transparent default)
        private GUIStyle opaqueWindowStyle;
        private GameScenes opaqueWindowStyleScene;
        private bool hasOpaqueWindowStyleScene;

        // The MAIN window's title style: the shared opaque style with a larger, bold
        // title. Derived from (and rebuilt with) opaqueWindowStyle, whose backgrounds it
        // shares by reference, so it owns no texture of its own and needs no destroy.
        private GUIStyle mainWindowTitleStyle;
        private GUIStyle mainWindowTitleStyleSource;

        // Shared header styles, so every window renders section bars and column
        // headers the same way. Lazy-initialized
        // via EnsureSharedHeaderStyles because GUIStyle construction requires a
        // valid GUI.skin — only available during draw.
        private GUIStyle sharedSectionHeaderStyle;
        private GUIStyle sharedColumnHeaderStyle;
        private GUIStyle sharedTableRowStyle;
        private GUIStyle sharedTableHeaderRowStyle;
        private GUIStyle sharedTableBodyBoxStyle;
        private GUIStyle sharedTableSectionHeaderStyle;
        private GUIStyle sharedTableCellStyle;
        private GUIStyle sharedTableScrollViewStyle;
        private GUIStyle sharedEmptyStateStyle;
        private GUIStyle sharedEmptyStateStyleSource;

        // Map view markers: icon atlas, fallback texture, label style, hover/sticky
        // state all live in MapMarkerRenderer (shared with ParsekTrackingStation).

        // Recordings table window (extracted to RecordingsTableUI)
        private RecordingsTableUI recordingsTableUI;

        // Missions window (extracted to MissionsWindowUI)
        private MissionsWindowUI missionsUI;

        private const float ResizeHandleSize = 16f;

        // Settings window (extracted to SettingsWindowUI)
        private SettingsWindowUI settingsUI;

        // Test runner window (extracted to TestRunnerUI)
        private TestRunnerUI testRunnerUI;

        // Timeline window (extracted to TimelineWindowUI, replaces ActionsWindowUI)
        private TimelineWindowUI timelineUI;

        // Kerbals window (reserved crew, active stand-ins, retired stand-ins; #385)
        private KerbalsWindowUI kerbalsUI;

        // Window drag tracking for position logging
        private Rect lastMainWindowRect;
        // Spawn Control window (extracted to SpawnControlUI)
        private SpawnControlUI spawnControlUI;

        // Structure-list window (one reusable instance, retargeted per mission / route)
        private StructureListWindowUI structureListUI;
        // The Route History: a second instance of the log-table window, opened from a
        // supply route's Log button (its own id, rect, lock and open state).
        private StructureListWindowUI routeHistoryUI;

        // Logistics window (v0 — Supply Routes tab + Send Once button)
        private LogisticsWindowUI logisticsUI;

        private static readonly string VersionLabel = "v" +
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private GUIStyle versionStyle;

        // Shared time-range filter state — read by TimelineWindowUI and RecordingsTableUI
        internal readonly TimeRangeFilterState TimeRangeFilter = new TimeRangeFilterState();

        internal ParsekFlight Flight => flight;
        internal UIMode Mode => mode;
        internal bool InFlightMode => InFlight;
        internal RecordingsTableUI GetRecordingsTableUI() => recordingsTableUI;
        internal SettingsWindowUI GetSettingsWindowUI() => settingsUI;
        internal TimelineWindowUI GetTimelineUI() => timelineUI;

        // The remaining gated, lock-owning sub-windows (design 7.2 close set). Exposed the
        // same way as the accessors above so the unit and in-game mode tests can open them
        // and assert the close handler shut them again.
        internal KerbalsWindowUI GetKerbalsUI() { return kerbalsUI; }
        internal SpawnControlUI GetSpawnControlUI() { return spawnControlUI; }
        internal TestRunnerUI GetTestRunnerUI() { return testRunnerUI; }

        // The two remaining sub-windows, exposed for the automation-only UiAction seam verb
        // (the GUI census opens every window in turn and photographs it). Neither is in the
        // Advanced -> Basic close set, which is why neither needed an accessor before.
        internal LogisticsWindowUI GetLogisticsUI() { return logisticsUI; }
        internal StructureListWindowUI GetStructureListUI() { return structureListUI; }
        internal StructureListWindowUI GetRouteHistoryUI() { return routeHistoryUI; }

        /// <summary>
        /// Why the Real Spawn Control launcher is greyed out. The window turns a recorded
        /// craft that is passing close by into a real vessel, so with nothing in range
        /// there is nothing for it to act on. Written to the main window's 62-character
        /// help strip, the narrowest in the mod. Pure for unit testing.
        /// </summary>
        internal static string SpawnControlLauncherDisabledReason(int nearbyCount)
        {
            return nearbyCount > 0 ? string.Empty : "No recorded craft is passing nearby";
        }

        // Runtime-only empty groups — delegated to RecordingsTableUI
        internal List<string> KnownEmptyGroups => recordingsTableUI.KnownEmptyGroups;

        // Host-supplied callback invoked when the user clicks the main window Close
        // button. Host (ParsekFlight / ParsekKSC) wires this to hide the window and
        // un-press the toolbar button.
        internal Action CloseMainWindow { get; set; }

        public ParsekUI(ParsekFlight flight)
        {
            this.flight = flight;
            this.mode = UIMode.Flight;
            this.recordingsTableUI = new RecordingsTableUI(this);
            this.missionsUI = new MissionsWindowUI(this);
            this.spawnControlUI = new SpawnControlUI(this);
            this.timelineUI = new TimelineWindowUI(this);
            this.kerbalsUI = new KerbalsWindowUI(this);
            this.testRunnerUI = new TestRunnerUI(this);
            this.settingsUI = new SettingsWindowUI(this);
            this.logisticsUI = new LogisticsWindowUI(this);
            this.structureListUI = new StructureListWindowUI(this);
            this.routeHistoryUI = new StructureListWindowUI(this, routeHistory: true);
            activeInstance = this;
            InitializeAppliedUiComplexityModeFromSettings();
            timelineUI.SubscribeTimelineDataChanged();
            kerbalsUI.SubscribeTimelineDataChanged();
            DisplayNames.DisplayNamesChanged += OnDisplayNamesChanged;
            kerbalsUI.SubscribeLiveCrewEvents();
        }

        public ParsekUI(UIMode mode)
        {
            this.flight = null;
            this.mode = mode;
            this.recordingsTableUI = new RecordingsTableUI(this);
            this.missionsUI = new MissionsWindowUI(this);
            this.spawnControlUI = new SpawnControlUI(this);
            this.timelineUI = new TimelineWindowUI(this);
            this.kerbalsUI = new KerbalsWindowUI(this);
            this.testRunnerUI = new TestRunnerUI(this);
            this.settingsUI = new SettingsWindowUI(this);
            this.logisticsUI = new LogisticsWindowUI(this);
            this.structureListUI = new StructureListWindowUI(this);
            this.routeHistoryUI = new StructureListWindowUI(this, routeHistory: true);
            activeInstance = this;
            InitializeAppliedUiComplexityModeFromSettings();
            timelineUI.SubscribeTimelineDataChanged();
            kerbalsUI.SubscribeTimelineDataChanged();
            DisplayNames.DisplayNamesChanged += OnDisplayNamesChanged;
            kerbalsUI.SubscribeLiveCrewEvents();
        }

        // The Timeline and Kerbals view models bake vessel and mission names; a rename moves
        // DisplayNames.Version and nothing they otherwise listen to.
        private void OnDisplayNamesChanged()
        {
            timelineUI.InvalidateCache();
            kerbalsUI.InvalidateCache();
        }

        internal const float SpacingSmall = 3f;
        private const float SpacingLarge = 10f;

        // Bottom "hovered control help text" strip. See TooltipEchoBox for why it is a
        // permanently visible box of constant height. The main window is the mod's entry
        // point and every launcher below is a bare noun ("Timeline", "Kerbals"), so this
        // is the first surface a new player can read by hovering.
        // One instance per ParsekUI, and ParsekUI is constructed per scene by
        // ParsekFlight / ParsekKSC, so the skin-scoped style never outlives its scene and
        // no ResetStyles call is needed (unlike the DDOL TestRunnerShortcut).
        private readonly TooltipEchoBox tooltipEcho = new TooltipEchoBox(SpacingSmall);

        internal static string GetKerbalsMainButtonLabel() => "Kerbals";

        // --- Frame-latched Basic / Advanced UI complexity mode (design 7.2) ---
        // Static because the setter seam is static and the setting is global; both
        // scenes construct their own ParsekUI and re-seed the latch from settings.
        private static UiComplexityMode appliedUiComplexityMode = UiComplexityMode.Advanced;
        private static UiComplexityMode? pendingUiComplexityMode;

        /// <summary>
        /// The live <see cref="ParsekUI"/> for the current scene. The mode-apply hook is
        /// static (the setter seam and the setting are both global), but the work it must do
        /// - clamping the Missions window's transient tab here in phase 5, closing the gated
        /// windows and releasing their input locks in phase 7 - is INSTANCE state.
        /// <para>Each scene constructs exactly one <see cref="ParsekUI"/>, so the most
        /// recently constructed instance is the live one. <see cref="Cleanup"/> clears this
        /// only when it still points at itself, so a scene handover that constructs the next
        /// instance before tearing down the previous one cannot null out the live
        /// reference.</para>
        /// </summary>
        private static ParsekUI activeInstance;

        /// <summary>
        /// The frame-latched mode EVERY <see cref="UiSurfaceVisibility.IsVisible"/> call
        /// site must read (design 7.1, 7.2). Never read
        /// <c>ParsekSettings.uiComplexityMode</c> from a draw site: the toggle click lands
        /// mid-event, and a raw read would let the IMGUI control count differ between one
        /// frame's Layout and Repaint passes (`ArgumentException: Getting control N's
        /// position in a group with only M controls`).
        /// <para>Exception, by design: the Settings window's Interface section still reads
        /// the settings field for WHICH of its two options renders as the selected box.
        /// That is a style swap on the same two controls, not a count change.</para>
        /// </summary>
        internal static UiComplexityMode AppliedUiComplexityMode => appliedUiComplexityMode;

        /// <summary>
        /// True when the player can actually reach the Real Spawn Control window right now.
        /// <para>Exists so NON-UI code can ask "is this window reachable?" without naming the
        /// complexity-mode vocabulary: the gate read stays here, in the UI coordinator that
        /// already owns every other one. Its consumer is
        /// <c>ParsekFlight.NotifyNewProximityCandidates</c>, whose screen message tells the
        /// player to open that window - guidance that is worse than useless when Basic has
        /// removed the only launcher.</para>
        /// <para>This is presentation only, and stays that way: it may decide what a message
        /// SAYS, never whether proximity is detected, whether a candidate is listed, or what
        /// spawning does. Anything past the wording is a behavior change the mode is not
        /// allowed to make (design section 5).</para>
        /// </summary>
        internal static bool IsSpawnControlReachable =>
            UiSurfaceVisibility.IsVisible(UiSurface.MainButtonSpawnControl, appliedUiComplexityMode);

        /// <summary>
        /// Seeds the latch from the persisted setting. Called from both constructors, so
        /// entering a scene never needs a pending apply - the very first frame's gates
        /// already read the player's mode. Fails open to Advanced when no settings object
        /// exists (design 6.2: showing everything is the safe wrong answer).
        /// </summary>
        private static void InitializeAppliedUiComplexityModeFromSettings()
        {
            ParsekSettings settings = ParsekSettings.Current;
            UiComplexityMode seeded = settings != null
                ? settings.UiComplexityModeLevel
                : UiComplexityMode.Advanced;

            appliedUiComplexityMode = seeded;
            pendingUiComplexityMode = null;
            ParsekLog.Verbose("UI",
                $"Applied UI mode seeded from settings: uiComplexityMode={seeded}" +
                (settings == null ? " (no active ParsekSettings, failed open)" : ""));
        }

        /// <summary>
        /// The SINGLE setter seam for the Basic / Advanced UI complexity mode
        /// (design `docs/dev/design-ui-basic-advanced.md` section 6.2). Every mode write
        /// - the Settings toggle, tests, any future "Defaults" path - goes through here.
        /// Nothing may write <c>ParsekSettings.uiComplexityMode</c> directly: window
        /// BODIES are deliberately never gated, so a writer that bypasses this seam would
        /// leave hidden windows drawing and holding input locks in Basic.
        ///
        /// <para>The setting is written and persisted IMMEDIATELY, but the value the draw
        /// sites read is only PENDING here: it is latched by
        /// <see cref="ApplyPendingUiComplexityModeIfAny"/> from the controller's
        /// <c>Update()</c>, outside OnGUI (design 7.2). The visible change therefore lands
        /// one frame after the click, imperceptibly.</para>
        /// </summary>
        internal static void SetUiComplexityMode(UiComplexityMode next)
        {
            ParsekSettings settings = ParsekSettings.Current;
            if (settings == null)
            {
                ParsekLog.Warn("UI",
                    $"Mode change to {next} ignored: no active ParsekSettings");
                return;
            }

            UiComplexityMode previous = settings.UiComplexityModeLevel;
            if (next == previous)
            {
                ParsekLog.Verbose("UI", $"Mode change to {next} is a no-op (already active)");
                return;
            }

            settings.UiComplexityModeLevel = next;
            ParsekSettingsPersistence.RecordUiComplexityMode((int)next);
            pendingUiComplexityMode = next;
            ParsekLog.Verbose("UI",
                $"Mode change queued: uiComplexityMode={previous}->{next} " +
                $"(applies next Update, applied={appliedUiComplexityMode})");
        }

        /// <summary>
        /// Latches a mode queued by <see cref="SetUiComplexityMode"/> (design 7.2). MUST be
        /// called from the controllers' <c>Update()</c> (`ParsekFlight`, `ParsekKSC`) and
        /// never from OnGUI: Unity runs Update before OnGUI, so every gate in the frame
        /// that follows - Layout and Repaint alike - reads one stable mode.
        /// <para>Idempotent: the pending value is consumed on the first call, so a second
        /// call in the same frame (both controllers alive during a scene handover) is a
        /// no-op.</para>
        /// </summary>
        internal static void ApplyPendingUiComplexityModeIfAny()
        {
            if (!pendingUiComplexityMode.HasValue)
                return;

            UiComplexityMode next = pendingUiComplexityMode.Value;
            // Consume BEFORE acting so a throwing hook cannot re-run the apply forever.
            pendingUiComplexityMode = null;

            UiComplexityMode previous = appliedUiComplexityMode;
            if (next == previous)
            {
                // Reachable when the player toggles back and forth before the latch runs.
                ParsekLog.Verbose("UI",
                    $"Pending UI mode {next} already applied, nothing to latch");
                return;
            }

            appliedUiComplexityMode = next;
            OnUiComplexityModeApplied(previous, next);
            // Logged AFTER the apply, deliberately. FormatHiddenSurfaces walks every
            // UiSurface through UiSurfaceVisibility.IsVisible, which THROWS on a surface the
            // gate table does not decide, and the apply is what force-closes the gated
            // windows and releases their input locks. Between the latch above and that
            // release is the one window where a throw would strand a locked window with the
            // mode already flipped; the log line is the only statement in it, so it goes
            // after.
            //
            // The hidden set is named in the line, not just the mode: it is the ONE place
            // the gate table is observable at runtime, and "which surfaces did that flip
            // take away" is the first question a mode-related report raises. This is also
            // UiSurfaceVisibility.HiddenSurfaces' production consumer - it had none, which
            // is how a key with zero IsVisible call sites stayed invisible (the census's
            // third surprise). Harness H22 pins the "Mode changed: uiComplexityMode=X->Y"
            // prefix, so the hidden=[...] suffix stays on the SAME line.
            ParsekLog.Info("UI",
                $"Mode changed: uiComplexityMode={previous}->{next} "
                + $"hidden=[{FormatHiddenSurfaces(next)}]");
        }

        /// <summary>
        /// The hidden-surface set of <paramref name="mode"/> as a stable, comma-separated
        /// name list, for the mode-change log line. Ordered by
        /// <see cref="UiSurface"/> declaration order (what
        /// <see cref="UiSurfaceVisibility.HiddenSurfaces"/> walks), so two runs of the same
        /// build print the same string and a diff in the list means a real gate change.
        /// Pure, so the log contract is unit-testable.
        /// </summary>
        internal static string FormatHiddenSurfaces(UiComplexityMode mode)
        {
            return string.Join(", ", UiSurfaceVisibility.HiddenSurfaces(mode)
                .Select(s => s.ToString())
                .ToArray());
        }

        /// <summary>
        /// The work that must happen WHEN the mode takes effect. Runs from the deferred
        /// latch in <see cref="ApplyPendingUiComplexityModeIfAny"/>, never mid-OnGUI.
        /// <para>Advanced -> Basic: force-closes every gated window and releases its input
        /// lock (design 7.2 step 2, edge case 2), closes the group picker (edge case 4), and
        /// clamps the Missions window's transient tab (design 7.4).</para>
        /// <para>Basic -> Advanced: no window is closed. Advanced only ever reveals surfaces,
        /// and every window reopens on demand with its state intact (philosophy 2).</para>
        /// <para>BOTH directions: the Settings window re-measures its height, because it is
        /// the one window whose own CONTENT changes with the mode (it hosts the two gated
        /// sections).</para>
        /// </summary>
        private static void OnUiComplexityModeApplied(UiComplexityMode previous, UiComplexityMode next)
        {
            // Both directions, before the Basic-only work below: Basic drops the Diagnostics
            // and Sample Density sections and Advanced restores them, so the Settings
            // window's fixed height no longer fits its content either way. Requesting the
            // re-measure here - from the Update-side latch, never mid-OnGUI - only flips a
            // flag the next Layout pass consumes, so no control count changes mid-frame.
            activeInstance?.settingsUI?.RequestHeightRemeasure();

            if (next != UiComplexityMode.Basic)
            {
                ParsekLog.Verbose("UI",
                    $"UI mode apply hook: {previous}->{next} (revealing surfaces, nothing to close)");
                return;
            }

            ParsekUI ui = activeInstance;
            if (ui == null)
            {
                // No live window set to act on (the mode was changed between scenes). The
                // next scene's ParsekUI constructs every sub-window closed, and KSP clears
                // all input locks across a scene transition, so there is nothing to leak.
                ParsekLog.Verbose("UI",
                    "UI mode apply: no live ParsekUI, close handler and tab clamp skipped");
                return;
            }

            CloseGatedWindowsForBasic(ui);

            // Design 7.4: clamp the moment the mode takes effect rather than waiting for
            // the Missions window's next draw. The on-draw clamp is the backstop, not
            // the primary: the window may be closed for many frames after the switch,
            // and reopening it should never land on a tab Basic does not draw.
            ui.recordingsTableUI?.ClampTabForBasic();

            ParsekLog.Verbose("UI", $"UI mode apply hook: {previous}->{next} complete");
        }

        /// <summary>
        /// One entry in the Advanced -> Basic close set (design 7.2 step 2). The handler and
        /// both test layers (`CloseHandlerCoversEveryGatedLockOwner` headless,
        /// `BasicModeReleasesInputLocks` in-game) walk this SAME list, so the list cannot
        /// drift away from what the handler actually closes - the failure mode the existing
        /// <see cref="Cleanup"/> sweep exhibits, where three windows were simply forgotten.
        /// </summary>
        internal sealed class GatedWindowCloseTarget
        {
            /// <summary>Diagnostic name, used in the close and warn log lines.</summary>
            internal readonly string Name;

            /// <summary>
            /// The KSP input lock id this surface owns, or null when it owns none (the group
            /// picker). Carried here so the in-game lock-leak test can assert straight
            /// against <c>InputLockManager</c> without duplicating the id strings.
            /// </summary>
            internal readonly string InputLockId;

            internal readonly Func<bool> IsOpen;
            internal readonly Func<bool> HeldInputLock;

            /// <summary>
            /// Sets <c>IsOpen = false</c> and releases the input lock. Called
            /// UNCONDITIONALLY, even for an already-closed surface: every
            /// <c>ReleaseInputLock</c> is an idempotent no-op when the lock is not held, and
            /// releasing a lock we do not own is far cheaper than reasoning about whether a
            /// closed window could still be holding one.
            /// </summary>
            internal readonly Action CloseAndReleaseLock;

            internal GatedWindowCloseTarget(
                string name,
                string inputLockId,
                Func<bool> isOpen,
                Func<bool> heldInputLock,
                Action closeAndReleaseLock)
            {
                Name = name;
                InputLockId = inputLockId;
                IsOpen = isOpen;
                HeldInputLock = heldInputLock;
                CloseAndReleaseLock = closeAndReleaseLock;
            }

            /// <summary>True when this surface owns a KSP input lock of its own.</summary>
            internal bool OwnsInputLock => InputLockId != null;
        }

        /// <summary>
        /// The EXPLICIT design 7.2 close set, in close order. Deliberately NOT derived from
        /// <see cref="Cleanup"/> (which omits logistics and the test runner) and not
        /// from <c>HiddenSurfaces(Basic)</c> alone, because three entries map to no
        /// <see cref="UiSurface"/>:
        /// <list type="bullet">
        ///   <item><description><c>TestRunner</c> - its launcher lives in the hidden
        ///     Diagnostics settings section, so an open instance would have no reopen path in
        ///     Basic. The SEPARATE global Ctrl+Shift+T <c>ParsekTestRunnerGlobal</c> window
        ///     has its own lock and is never gated (edge case 13).</description></item>
        ///   <item><description><c>GroupPicker</c> - reachable only from the hidden
        ///     Recordings tab, but an already-open picker keeps drawing from
        ///     <c>RecordingsTableUI.DrawIfOpen</c> regardless of tab (edge case 4). It owns
        ///     no input lock.</description></item>
        ///   <item><description><c>LogisticsLinkPicker</c> - the round-trip link picker is
        ///     armed from the Logistics detail block's Link control, which Basic hides
        ///     (<see cref="UiSurface.LogisticsRouteTuning"/>), but an already-open picker
        ///     keeps drawing from <c>LogisticsWindowUI.DrawIfOpen</c>. It owns no input
        ///     lock; the Logistics window itself stays open.</description></item>
        /// </list>
        /// <para>Deliberately ABSENT: <c>recordingsTableUI</c> (survives as the Missions
        /// window), <c>structureListUI</c> (the Mission Log, reachable from the Missions
        /// rows), <c>routeHistoryUI</c> (the Route History, reachable from every Logistics
        /// route's Log in both modes), <c>kerbalsUI</c> (drawn in Basic since the 2026-09-22 owner
        /// re-ruling: it is the only surface that explains why a reserved kerbal is
        /// missing from stock crew assignment), and the ungated <c>timelineUI</c> /
        /// <c>logisticsUI</c> / <c>settingsUI</c> / <c>missionsUI</c>.</para>
        /// </summary>
        internal IReadOnlyList<GatedWindowCloseTarget> BuildGatedWindowCloseSet()
        {
            return new List<GatedWindowCloseTarget>
            {
                new GatedWindowCloseTarget(
                    "SpawnControl",
                    SpawnControlUI.SpawnControlInputLockId,
                    () => spawnControlUI.IsOpen,
                    () => spawnControlUI.HasInputLock,
                    () => { spawnControlUI.IsOpen = false; spawnControlUI.ReleaseInputLock(); }),
                new GatedWindowCloseTarget(
                    "TestRunner",
                    TestRunnerUI.TestRunnerInputLockId,
                    () => testRunnerUI.IsOpen,
                    () => testRunnerUI.HasInputLock,
                    () => { testRunnerUI.IsOpen = false; testRunnerUI.ReleaseInputLock(); }),
                new GatedWindowCloseTarget(
                    "GroupPicker",
                    null, // owns no input lock: reachability rule, not a lock rule
                    () => recordingsTableUI.IsGroupPickerOpen,
                    () => false,
                    () => recordingsTableUI.CloseGroupPickerForModeChange()),
                new GatedWindowCloseTarget(
                    "LogisticsLinkPicker",
                    null, // owns no input lock: the Link control that opens it is Advanced-only
                    () => logisticsUI.IsLinkPickerOpen,
                    () => false,
                    () => logisticsUI.CloseLinkPickerForModeChange()),
            };
        }

        /// <summary>
        /// Force-closes the design 7.2 close set and releases each window's KSP input lock.
        /// <para>Every entry gets its OWN try/catch: <c>InputLockManager.RemoveControlLock</c>
        /// fires <c>GameEvents.onInputLocksModified</c>, and a third-party listener that
        /// throws must not abort the loop and strand the windows after it still holding
        /// locks. A swallowed exception is logged at Warn with the window name and the
        /// exception type + message (design 12.2).</para>
        /// <para>Blast radius if a release is nevertheless missed: one frame. Every window's
        /// <c>DrawIfOpen</c> prologue is <c>if (!IsOpen) { ReleaseInputLock(); return; }</c>
        /// and those call sites are deliberately never gated (design 7.1), so the next frame
        /// self-heals; KSP also clears all locks on scene transition. Neither excuses this
        /// handler - a leak is the highest-risk defect in this feature (edge case 2).</para>
        /// </summary>
        private static void CloseGatedWindowsForBasic(ParsekUI ui)
        {
            IReadOnlyList<GatedWindowCloseTarget> targets = ui.BuildGatedWindowCloseSet();
            int closed = 0;
            int alreadyClosed = 0;
            int failed = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                GatedWindowCloseTarget target = targets[i];
                try
                {
                    bool wasOpen = target.IsOpen();
                    bool heldLock = target.HeldInputLock();

                    target.CloseAndReleaseLock();

                    if (wasOpen || heldLock)
                    {
                        closed++;
                        // Per closed window only (design 12.2). Logging the untouched ones too
                        // would put four no-op lines in the log on every single mode switch.
                        ParsekLog.Verbose("UI",
                            $"Window auto-closed on mode change: window={target.Name} " +
                            $"wasOpen={wasOpen} heldLock={heldLock}");
                    }
                    else
                    {
                        alreadyClosed++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    ParsekLog.Warn("UI",
                        $"Close-handler exception swallowed: window={target.Name} " +
                        $"{ex.GetType().Name}: {ex.Message}; continuing with the remaining windows");
                }
            }

            ParsekLog.Verbose("UI",
                $"UI mode close handler: closed={closed} alreadyClosed={alreadyClosed} " +
                $"failed={failed} of {targets.Count} gated surfaces");
        }

        /// <summary>
        /// Test seam: clears the latch + pending mode back to their startup state, and drops
        /// the live-instance reference so a <see cref="ParsekUI"/> constructed by an earlier
        /// test cannot receive this test's mode-apply hook.
        /// </summary>
        internal static void ResetUiComplexityModeForTesting()
        {
            appliedUiComplexityMode = UiComplexityMode.Advanced;
            pendingUiComplexityMode = null;
            activeInstance = null;
        }

        /// <summary>Test seam: the queued-but-not-yet-latched mode, null when none.</summary>
        internal static UiComplexityMode? PendingUiComplexityModeForTesting => pendingUiComplexityMode;

        /// <summary>
        /// The live <see cref="ParsekUI"/> for the current scene, or null outside FLIGHT /
        /// SPACECENTER. Exposed for the in-game `UiComplexityMode` tests, which must drive
        /// the REAL windows the mode-change close handler acts on.
        /// </summary>
        internal static ParsekUI ActiveInstance => activeInstance;

        /// <summary>
        /// The PERSISTED interface mode - what <see cref="SetUiComplexityMode"/> compares
        /// against when it decides a request is a no-op. Fails open to Advanced with no
        /// settings object, exactly as the latch seed does.
        /// <para>Exists because the setting and the LATCH can legitimately disagree - a
        /// <see cref="ParsekUI"/> constructed before <c>ParsekSettings.Current</c> exists
        /// seeds the latch to Advanced while the save carries Basic - and a caller that
        /// reads only <see cref="AppliedUiComplexityMode"/> cannot tell "already there"
        /// from "the setter is about to decline to queue anything". The automation-only
        /// <c>UiAction op=complexity</c> seam op is that caller.</para>
        /// </summary>
        internal static UiComplexityMode PersistedUiComplexityMode
        {
            get
            {
                ParsekSettings settings = ParsekSettings.Current;
                return settings != null
                    ? settings.UiComplexityModeLevel
                    : UiComplexityMode.Advanced;
            }
        }

        /// <summary>
        /// Re-queues the PERSISTED mode for the latch when the latch has drifted away from
        /// it. Returns true when something was queued.
        /// <para>Deliberately NOT a general "queue this mode" setter: it can only ever queue
        /// the value the settings object already holds, so it cannot apply a mode the save
        /// does not carry. It closes the one gap <see cref="SetUiComplexityMode"/>
        /// leaves by design: that setter no-ops when the requested mode equals the SETTING,
        /// so a drifted latch could never be corrected by asking for the mode the save
        /// already has.</para>
        /// </summary>
        internal static bool TryRequeuePersistedUiComplexityMode()
        {
            ParsekSettings settings = ParsekSettings.Current;
            if (settings == null) return false;
            if (!TryDecidePersistedUiComplexityRequeue(
                    appliedUiComplexityMode, settings.UiComplexityModeLevel,
                    out UiComplexityMode queue))
                return false;
            ParsekLog.Verbose("UI",
                "Persisted UI mode re-queued for the latch: uiComplexityMode="
                + $"{appliedUiComplexityMode}->{queue} (setting and latch had drifted)");
            pendingUiComplexityMode = queue;
            return true;
        }

        /// <summary>
        /// The pure decision inside <see cref="TryRequeuePersistedUiComplexityMode"/>: given
        /// the LATCH's current value and the PERSISTED setting, is there anything to queue,
        /// and what? True with <paramref name="queue"/> set iff the two have DRIFTED.
        ///
        /// <para>The queued value can only ever be <paramref name="persisted"/>, and that is
        /// the helper's safety property rather than an implementation detail: it cannot
        /// apply a mode the save does not carry.</para>
        ///
        /// <para>THE REQUESTED MODE IS NOT AN INPUT, deliberately. The one caller that has
        /// a request - the <c>UiAction op=complexity</c> seam - calls
        /// <see cref="SetUiComplexityMode"/> FIRST, so the setting already equals the
        /// request by the time it gets here and a `requested` parameter could not change
        /// the answer. Taking one would state a dependency that does not exist, and invite
        /// a future caller to queue a mode the settings object never held.</para>
        /// </summary>
        internal static bool TryDecidePersistedUiComplexityRequeue(
            UiComplexityMode applied, UiComplexityMode persisted, out UiComplexityMode queue)
        {
            queue = persisted;
            return persisted != applied;
        }

        public void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            GUILayout.Space(SpacingLarge);

            // Button order (separator groups requested by the player):
            //   1. Real Spawn Control  (InFlight-only; its trailing separator is inside the block)
            //   2. Timeline / Recordings
            //   3. Kerbals
            //   4. Settings
            //
            // Basic / Advanced gating (design 7.1): EVERY launcher is wrapped in an
            // IsVisible check reading the FRAME-LATCHED mode below, never the settings
            // field, so the control count is identical in this frame's Layout and Repaint
            // passes. Timeline / Missions / Logistics / Settings answer constant-true in
            // both modes, so their gates hide nothing today - they are wired anyway
            // because an unwired key is a LIE in the gate table: all four had zero
            // IsVisible call sites, so the table said they were gated and the launchers
            // drew unconditionally, and the two agreeing by coincidence is exactly what
            // made it invisible (the census's third surprise). Re-keying one of these to
            // hidden is now a one-line decision rather than a hunt for the draw site.
            // Separators live INSIDE the block of the buttons they separate, or Basic
            // shows a double gap where the hidden group used to be.
            UiComplexityMode complexity = AppliedUiComplexityMode;

            // --- Real Spawn Control (InFlight-only, top of the button column) ---
            if (InFlight && flight != null
                && UiSurfaceVisibility.IsVisible(UiSurface.MainButtonSpawnControl, complexity))
            {
                int spawnCount = flight.NearbySpawnCandidates.Count;
                GUI.enabled = spawnCount > 0;
                bool spawnControlClicked = GUILayout.Button(new GUIContent(
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Real Spawn Control ({0})", spawnCount),
                    "Turn a recorded craft passing nearby into a real vessel."));
                DisabledHoverEcho.CarryLastControl(
                    spawnCount > 0, SpawnControlLauncherDisabledReason(spawnCount));
                if (spawnControlClicked)
                {
                    spawnControlUI.IsOpen = !spawnControlUI.IsOpen;
                    ParsekLog.Verbose("UI",
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Real Spawn Control window toggled: {0}",
                            spawnControlUI.IsOpen ? "open" : "closed"));
                }
                GUI.enabled = true;
                GUILayout.Space(SpacingLarge);
            }

            if (UiSurfaceVisibility.IsVisible(UiSurface.MainButtonTimeline, complexity)
                && GUILayout.Button(new GUIContent(
                    "Timeline",
                    "Every recorded flight and career event on one clock.")))
            {
                timelineUI.IsOpen = !timelineUI.IsOpen;
                ParsekLog.Verbose("UI", $"Timeline window toggled: {(timelineUI.IsOpen ? "open" : "closed")}");
            }

            // Top-level Missions button. The per-state count lives inside the
            // window; the launch-surface label stays short. Missions is the primary
            // identity of this window; the raw Recordings table is its second tab
            // (no separate button). The label is constant in both UI modes.
            if (UiSurfaceVisibility.IsVisible(UiSurface.MainButtonRecordings, complexity)
                && GUILayout.Button(new GUIContent(
                    "Missions",
                    "Your missions, and the recordings they are built from.")))
                ToggleRecordingsWindow();

            // --- Supply Route candidate banner + Logistics launcher ---
            // Drawn by the Logistics window, which owns the route prompt it reads.
            logisticsUI.DrawMainWindowSection(complexity);

            // --- Kerbals, with its LEADING separator ---
            // Kerbals draws in both modes (owner re-ruling 2026-09-22). The separator that
            // opens this group is gated on the group being non-empty, so a hidden Kerbals
            // launcher never leaves two gaps in a row (design 7.1).
            bool showKerbalsButton =
                UiSurfaceVisibility.IsVisible(UiSurface.MainButtonKerbals, complexity);

            if (showKerbalsButton)
                GUILayout.Space(SpacingLarge);

            // Keep top-level launch-surface labels short; detailed counts stay inside the window.
            if (showKerbalsButton)
            {
                if (GUILayout.Button(new GUIContent(
                    GetKerbalsMainButtonLabel(),
                    "Who is reserved, flying or retired on timeline.")))
                {
                    kerbalsUI.IsOpen = !kerbalsUI.IsOpen;
                    ParsekLog.Verbose("UI", $"Kerbals window toggled: {(kerbalsUI.IsOpen ? "open" : "closed")}");
                }
            }

            GUILayout.Space(SpacingLarge);

            // --- Settings ---
            // The tooltip names only what the window ALWAYS draws: Interface (the
            // Basic / Advanced toggle), Ghosts, and Data Management. The Recording section
            // it used to advertise was retired by the 2026-08-27 simplification, and
            // Looping / Diagnostics / Sample Density are Advanced-only, so three of the
            // four topics the old wording promised could be absent (finding P18).
            if (UiSurfaceVisibility.IsVisible(UiSurface.MainButtonSettings, complexity)
                && GUILayout.Button(new GUIContent(
                    "Settings",
                    "Interface, ghosts and data - plus more in Advanced.")))
                ToggleSettingsWindow();

            // --- Version footer (version on the left, Close button fills the rest) ---
            GUILayout.Space(SpacingLarge);
            if (versionStyle == null)
            {
                versionStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 10,
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.4f) },
                    contentOffset = new Vector2(0f, 3f)
                };
            }
            // Bottom "hovered control help text" strip (shared house helper). Fixed
            // two-line height, always present, drawn directly above the footer row that
            // carries Close - the house ordering every Parsek window uses, so Close stays
            // the window's last content row and never swaps places with the box. Nothing
            // below carries a tooltip, so the live GUI.tooltip read misses nothing.
            tooltipEcho.Draw();

            GUILayout.BeginHorizontal();
            GUILayout.Label(VersionLabel, versionStyle, GUILayout.ExpandWidth(false));
            GUILayout.Space(10f);
            if (GUILayout.Button("Close", GUILayout.ExpandWidth(true)))
            {
                ParsekLog.Verbose("UI", "Main window closed via button");
                CloseMainWindow?.Invoke();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow();
        }

        /// <summary>
        /// Call after each window's GUILayoutWindow to log position/size changes (rate-limited).
        /// </summary>
        internal void LogWindowPosition(string windowName, ref Rect lastRect, Rect currentRect)
        {
            if (lastRect.x != currentRect.x || lastRect.y != currentRect.y ||
                lastRect.width != currentRect.width || lastRect.height != currentRect.height)
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited("UI", $"window.{windowName}",
                    $"{windowName} window position: x={currentRect.x.ToString("F0", ic)} y={currentRect.y.ToString("F0", ic)} " +
                    $"w={currentRect.width.ToString("F0", ic)} h={currentRect.height.ToString("F0", ic)}", 2.0);
                lastRect = currentRect;
            }
        }

        public void LogMainWindowPosition(Rect currentRect)
        {
            LogWindowPosition("Main", ref lastMainWindowRect, currentRect);
        }

        public void DrawTimelineWindowIfOpen(Rect mainWindowRect)
        {
            timelineUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawKerbalsWindowIfOpen(Rect mainWindowRect)
        {
            kerbalsUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawLogisticsWindowIfOpen(Rect mainWindowRect)
        {
            logisticsUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawStructureWindowIfOpen(Rect mainWindowRect)
        {
            structureListUI.DrawIfOpen(mainWindowRect);
            routeHistoryUI.DrawIfOpen(mainWindowRect);
        }

        /// <summary>Opens the Route History window on one supply route (the route's Log
        /// button). It can stand beside an open Mission Log.</summary>
        internal void OpenRouteHistoryWindow(string routeId, string routeName)
        {
            routeHistoryUI.OpenForRoute(routeId, routeName);
        }

        /// <summary>Opens the Mission Log on one Mission (the Missions tab's Log button).
        /// <paramref name="missionId"/> null opens the whole tree.</summary>
        internal void OpenStructureWindowForMission(string treeId, string missionId, string title)
        {
            structureListUI.OpenForMission(treeId, missionId, title);
        }

        // ════════════════════════════════════════════════════════════════
        //  Recordings table (extracted to RecordingsTableUI)
        // ════════════════════════════════════════════════════════════════

        public void DrawRecordingsWindowIfOpen(Rect mainWindowRect)
        {
            recordingsTableUI.DrawIfOpen(mainWindowRect);
        }

        // The Missions view is drawn as a tab inside the Recordings window
        // (RecordingsTableUI dispatches to it); it is no longer a standalone window.
        internal MissionsWindowUI GetMissionsUI() => missionsUI;

        internal void ToggleRecordingsWindow()
        {
            recordingsTableUI.IsOpen = !recordingsTableUI.IsOpen;
            ParsekLog.Verbose("UI", $"Recordings window toggled: {(recordingsTableUI.IsOpen ? "open" : "closed")}");
        }

        internal void ToggleSettingsWindow()
        {
            settingsUI.IsOpen = !settingsUI.IsOpen;
            ParsekLog.Verbose("UI", $"Settings window toggled: {(settingsUI.IsOpen ? "open" : "closed")}");
        }

        // ════════════════════════════════════════════════════════════════
        //  Opaque window style (shared by all sub-windows)
        // ════════════════════════════════════════════════════════════════

        private bool EnsureOpaqueWindowStyle(GUISkin skin)
        {
            if (opaqueWindowStyle != null)
            {
                if (hasOpaqueWindowStyleScene
                    && opaqueWindowStyleScene == HighLogic.LoadedScene
                    && AreAllOpaqueStyleBackgroundsPresent(opaqueWindowStyle))
                    return true;

                ParsekLog.VerboseRateLimited("UI", "opaque-style-cache-stale",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "ParsekUI: dropping cached opaque style before rebuild (built={0}, current={1})",
                        opaqueWindowStyleScene, HighLogic.LoadedScene), 1f);
                ResetCachedWindowStylesForSceneChange();
            }

            if (!IsOpaqueStyleSkinReady(skin))
            {
                ParsekLog.VerboseRateLimited("UI", "opaque-style-skin-not-ready",
                    "ParsekUI: skin not ready, deferring opaque style rebuild", 1f);
                return false;
            }

            opaqueWindowStyle = BuildOpaqueWindowStyleFromSource(skin.window);
            opaqueWindowStyleScene = HighLogic.LoadedScene;
            hasOpaqueWindowStyleScene = true;
            return true;
        }

        private static bool IsOpaqueStyleSkinReady(GUISkin skin)
        {
            return skin != null
                && skin.window != null
                && skin.window.normal.background != null;
        }

        private static bool AreAllOpaqueStyleBackgroundsPresent(GUIStyle style)
        {
            return style != null
                && style.normal.background != null
                && style.onNormal.background != null
                && style.focused.background != null
                && style.onFocused.background != null
                && style.active.background != null
                && style.onActive.background != null
                && style.hover.background != null
                && style.onHover.background != null;
        }

        internal static GUIStyle BuildOpaqueWindowStyleFromSource(GUIStyle sourceStyle)
        {
            Texture2D normalSource = sourceStyle.normal.background;
            var style = new GUIStyle(sourceStyle);
            style.normal.background = MakeOpaqueCopy(normalSource);
            style.onNormal.background = MakeOpaqueCopy(sourceStyle.onNormal.background ?? normalSource);
            style.focused.background = MakeOpaqueCopy(sourceStyle.focused.background ?? normalSource);
            style.onFocused.background = MakeOpaqueCopy(sourceStyle.onFocused.background ?? normalSource);
            style.active.background = MakeOpaqueCopy(sourceStyle.active.background ?? normalSource);
            style.onActive.background = MakeOpaqueCopy(sourceStyle.onActive.background ?? normalSource);
            style.hover.background = MakeOpaqueCopy(sourceStyle.hover.background ?? normalSource);
            style.onHover.background = MakeOpaqueCopy(sourceStyle.onHover.background ?? normalSource);
            NormalizeOpaqueWindowTitleTextColors(style, sourceStyle);

            // Bigger title font + more breathing room above/below the title text
            // so the title bar is not cramped. Applied here so every caller
            // (main UI, Test Runner, any future window using this builder) gets
            // the same title bar without having to remember to re-tweak.
            int baseFontSize = sourceStyle.fontSize;
            if (baseFontSize <= 0) baseFontSize = 12;       // GUI.skin.window often reports 0
            style.fontSize = baseFontSize + 2;
            var p = style.padding;
            style.padding = new RectOffset(p.left, p.right, p.top + 10, p.bottom + 4);

            return style;
        }

        internal static void NormalizeOpaqueWindowTitleTextColors(
            GUIStyle style,
            GUIStyle sourceStyle)
        {
            if (style == null || sourceStyle == null)
                return;

            Color baseTextColor = ResolveReadableWindowTitleTextColor(
                sourceStyle.normal.textColor,
                sourceStyle.onNormal.textColor);
            Color toggledTextColor = ResolveReadableWindowTitleTextColor(
                sourceStyle.onNormal.textColor,
                sourceStyle.normal.textColor);

            style.normal.textColor = baseTextColor;
            style.hover.textColor = baseTextColor;
            style.focused.textColor = baseTextColor;
            style.active.textColor = baseTextColor;

            style.onNormal.textColor = toggledTextColor;
            style.onHover.textColor = toggledTextColor;
            style.onFocused.textColor = toggledTextColor;
            style.onActive.textColor = toggledTextColor;
        }

        internal static void NormalizeOpaqueWindowTitleTextColors(
            Color sourceNormalTextColor,
            Color sourceOnNormalTextColor,
            ref Color normalTextColor,
            ref Color hoverTextColor,
            ref Color focusedTextColor,
            ref Color activeTextColor,
            ref Color onNormalTextColor,
            ref Color onHoverTextColor,
            ref Color onFocusedTextColor,
            ref Color onActiveTextColor)
        {
            Color baseTextColor = ResolveReadableWindowTitleTextColor(
                sourceNormalTextColor,
                sourceOnNormalTextColor);
            Color toggledTextColor = ResolveReadableWindowTitleTextColor(
                sourceOnNormalTextColor,
                sourceNormalTextColor);

            normalTextColor = baseTextColor;
            hoverTextColor = baseTextColor;
            focusedTextColor = baseTextColor;
            activeTextColor = baseTextColor;

            onNormalTextColor = toggledTextColor;
            onHoverTextColor = toggledTextColor;
            onFocusedTextColor = toggledTextColor;
            onActiveTextColor = toggledTextColor;
        }

        internal static Color ResolveReadableWindowTitleTextColor(
            Color preferred,
            Color fallback)
        {
            if (IsReadableWindowTitleTextColor(preferred))
                return preferred;
            if (IsReadableWindowTitleTextColor(fallback))
                return fallback;
            return Color.white;
        }

        internal static bool IsReadableWindowTitleTextColor(Color color)
        {
            if (color.a < 0.5f)
                return false;

            float luminance = 0.2126f * color.r
                + 0.7152f * color.g
                + 0.0722f * color.b;
            return luminance >= 0.55f;
        }

        private void ClearOpaqueWindowStyle()
        {
            if (opaqueWindowStyle == null)
                return;

            try
            {
                var destroyedBackgrounds = new HashSet<int>();
                DestroyOpaqueBackground(opaqueWindowStyle.normal.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.onNormal.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.focused.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.onFocused.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.active.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.onActive.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.hover.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.onHover.background, destroyedBackgrounds);
            }
            catch (Exception ex)
            {
                if (!IsHeadlessUiObjectFailure(ex))
                    throw;
            }
            opaqueWindowStyle = null;
            hasOpaqueWindowStyleScene = false;
        }

        private void ResetCachedWindowStylesForSceneChange()
        {
            ClearOpaqueWindowStyle();
            mainWindowTitleStyle = null;
            mainWindowTitleStyleSource = null;
            sharedSectionHeaderStyle = null;
            sharedColumnHeaderStyle = null;
            sharedEmptyStateStyle = null;
            sharedEmptyStateStyleSource = null;
            versionStyle = null;
        }

        private static void DestroyOpaqueBackground(
            Texture2D background,
            HashSet<int> destroyedBackgrounds)
        {
            if (background == null)
                return;

            try
            {
                int id = background.GetInstanceID();
                if (!destroyedBackgrounds.Add(id))
                    return;

                UnityEngine.Object.Destroy(background);
            }
            catch (Exception ex)
            {
                if (!IsHeadlessUiObjectFailure(ex))
                    throw;
            }
        }

        private static bool IsHeadlessUiObjectFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException || current is MissingMethodException)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Creates a readable copy of a texture with all pixel alpha set to 1.
        /// Uses RenderTexture blit to handle non-readable source textures (KSP skin).
        /// </summary>
        private static Texture2D MakeOpaqueCopy(Texture2D source)
        {
            if (source == null) return null;

            // Blit to RenderTexture (works even for non-readable textures)
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            // Max out alpha on all pixels
            Color[] pixels = copy.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a = 1f;
            copy.SetPixels(pixels);
            copy.Apply();
            copy.filterMode = source.filterMode;
            return copy;
        }

        /// <summary>
        /// Returns the opaque window style for use by external callers (ParsekFlight, ParsekKSC).
        /// </summary>
        public GUIStyle GetOpaqueWindowStyle()
        {
            if (!EnsureOpaqueWindowStyle(GUI.skin))
                return null;
            return opaqueWindowStyle;
        }

        /// <summary>
        /// How many pixels the MAIN window's title is drawn larger than the shared
        /// opaque window title (which is itself the skin's window size + 2).
        /// </summary>
        internal const int MainWindowTitleExtraFontSize = 2;

        /// <summary>
        /// The main window's title font size given the shared opaque style's size. A
        /// non-positive source size means "the font's default", which the opaque builder
        /// never leaves behind, but a defensive floor keeps the title from shrinking.
        /// </summary>
        internal static int ResolveMainWindowTitleFontSize(int opaqueTitleFontSize)
        {
            int baseSize = opaqueTitleFontSize > 0 ? opaqueTitleFontSize : 14;
            return baseSize + MainWindowTitleExtraFontSize;
        }

        /// <summary>
        /// The style the MAIN "Parsek" window (flight and KSC) is drawn with: the shared
        /// opaque window style with a bold title a little larger than every other
        /// Parsek window's. Only the title changes - padding and content offset are
        /// copied untouched, and the opaque style's 10 px of extra top padding already
        /// leaves room for the larger glyphs, so the window body does not move. Cached
        /// and rebuilt only when the opaque style itself is rebuilt.
        /// </summary>
        public GUIStyle GetMainWindowStyle()
        {
            GUIStyle opaque = GetOpaqueWindowStyle();
            if (opaque == null)
                return null;
            if (mainWindowTitleStyle == null
                || !ReferenceEquals(mainWindowTitleStyleSource, opaque))
            {
                mainWindowTitleStyle = new GUIStyle(opaque)
                {
                    fontSize = ResolveMainWindowTitleFontSize(opaque.fontSize),
                    fontStyle = FontStyle.Bold,
                };
                mainWindowTitleStyleSource = opaque;
                ParsekLog.Verbose("UI", string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "ParsekUI: main window title style built (fontSize={0} bold=true)",
                    mainWindowTitleStyle.fontSize));
            }
            return mainWindowTitleStyle;
        }

        internal static void ResetWindowGuiColors(
            out Color previousColor,
            out Color previousBackgroundColor,
            out Color previousContentColor)
        {
            previousColor = GUI.color;
            previousBackgroundColor = GUI.backgroundColor;
            previousContentColor = GUI.contentColor;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
        }

        internal static void RestoreWindowGuiColors(
            Color previousColor,
            Color previousBackgroundColor,
            Color previousContentColor)
        {
            GUI.color = previousColor;
            GUI.backgroundColor = previousBackgroundColor;
            GUI.contentColor = previousContentColor;
        }

        internal static T RunWithNormalizedWindowGuiColors<T>(Func<T> callback)
        {
            ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                return callback();
            }
            finally
            {
                RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Shared header styles (section bars + column headers)
        // ════════════════════════════════════════════════════════════════

        // ────────────────────────────────────────────────────────────────
        //  Table row inset (GUI-TABLE-HEADERS-OFFSET-FROM-CELLS)
        // ────────────────────────────────────────────────────────────────
        //
        // KSP's skin reports box/label/button/toggle/textField margin = L4/R4 and
        // box padding = L4/R4 (logged once per draw by RecordingsTableUI as
        // "Rec table skin margins"). Unity places a child at
        //   parentRect.x + max(parentStyle.padding.left, childStyle.margin.left)
        // and a layout group's first cell at groupRect.x + groupStyle.padding.left,
        // where a group opened with NO style inherits its margin from its first
        // child. So a header row opened as BeginHorizontal() outside a scroll view
        // and body rows opened the same way inside one do NOT share an origin: the
        // scroll view contributes the row's own 4px margin, and a GUI.skin.box
        // wrapper contributes another 4px on top of that. That is the 4px
        // (Structure) / 8px (Real Spawn Control) cell-vs-header offset the
        // 2026-09-11 GUI census measured.
        //
        // The fix is one shared inset for both rows: every table row - header and
        // body - opens with an EXPLICIT style whose horizontal margin and padding
        // are TableRowHorizontalInsetPx, so both rows land on their parent's own
        // content origin and every column starts from the same x. Vertical margin
        // and padding are left at the skin's 4px so row pitch is unchanged.
        internal const int TableRowHorizontalInsetPx = 0;

        // The gap a Parsek window leaves between its title bar and its first box or row:
        // the Missions, Logistics, Kerbals, Settings and Log windows each open their
        // content with a GUILayout.Space of this height, and the pickers
        // (PickerWindowLayout.DrawTitleGap) reuse it. Pinned equal across them by
        // PickerWindowLayoutTests.
        internal const float WindowContentTopGapPx = 5f;

        // Fallback footprint when GUI.skin.verticalScrollbar is unavailable: KSP's
        // 15px bar plus its 1px left margin, as measured by the 2026-09-11 census
        // (run 2026-09-11_1706_GUI-6-census-flight-playback).
        internal const float DefaultVerticalScrollbarFootprintWidth = 16f;

        // Fallback cell margin when GUI.skin.label is unavailable. KSP's skin reports
        // box/label/button/toggle/textField margin = L4/R4 (logged once per draw by
        // RecordingsTableUI as "Rec table skin margins", which also prints the
        // scrollbar terms and the derived gutter below).
        internal const int DefaultTableCellHorizontalMarginPx = 4;

        /// <summary>
        /// Width a forced vertical scrollbar actually costs the CONTENT of the scroll
        /// view it belongs to - the bar's own <c>fixedWidth</c> PLUS its left margin,
        /// not <c>fixedWidth</c> alone. Decompiled <c>GUIScrollGroup.SetHorizontal</c>
        /// (UnityEngine.IMGUIModule, KSP 1.12.5) lays the content group out at
        /// <c>width - verticalScrollbar.fixedWidth - verticalScrollbar.margin.left</c>.
        /// KSP's skin: fixedWidth 15 + margin.left 1 = 16, which is exactly the
        /// outer-vs-inner width step the 2026-09-11 census measured (Real Spawn
        /// Control 730 -> 714, Structure 980 -> 964) against a bar whose own rect is
        /// 15 wide - reserving <c>fixedWidth</c> alone is 1px short before anything
        /// else is counted.
        /// </summary>
        internal static float VerticalScrollbarFootprintWidth()
        {
            if (GUI.skin == null || GUI.skin.verticalScrollbar == null)
                return DefaultVerticalScrollbarFootprintWidth;

            float w = GUI.skin.verticalScrollbar.fixedWidth;
            if (w <= 0f) return DefaultVerticalScrollbarFootprintWidth;
            return w + GUI.skin.verticalScrollbar.margin.left;
        }

        /// <summary>
        /// The horizontal margin one table cell carries on each side (4 in KSP's skin
        /// for every cell style these tables use).
        /// </summary>
        internal static int TableCellHorizontalMarginPx()
        {
            if (GUI.skin == null || GUI.skin.label == null)
                return DefaultTableCellHorizontalMarginPx;

            int m = GUI.skin.label.margin.right;
            return m > 0 ? m : DefaultTableCellHorizontalMarginPx;
        }

        /// <summary>
        /// Width of the vertical-scrollbar gutter a table header must reserve when
        /// it is pinned OUTSIDE a body scroll view that forces a vertical bar:
        /// <see cref="VerticalScrollbarFootprintWidth"/> PLUS one
        /// <see cref="TableCellHorizontalMarginPx"/> (20 in KSP's skin).
        ///
        /// <para><b>Why the extra cell margin.</b> Decompiled
        /// <c>GUILayoutGroup.SetHorizontal</c> reduces a styled group's content width by
        /// <c>max(style.padding.right, lastChild.margin.right)</c> - a MAX, not a sum.
        /// A body row carries no right padding, so its content already ends one cell
        /// margin inside its group; a header's reserved padding REPLACES that margin
        /// rather than adding to it, so the padding has to cover both terms. The
        /// Recordings tab takes the same number through the same pinned header row style
        /// (<see cref="GetTableHeaderRowStyle"/>) since 2026-09-26; before that it reserved
        /// it as a trailing <c>GUILayout.Space</c> (a Space entry carries
        /// <c>GUIStyle.none</c> and consumes exactly its width at the group's right edge,
        /// and the header's last cell has margin R0, so both forms end that header at the
        /// same x). The Missions tab's box spends no such padding, so it owes the bare
        /// footprint and is deliberately NOT routed here
        /// (GUI-MISSIONS-WINDOW-MERGED-FIRST-HEADER-CELL).</para>
        ///
        /// <para>The 5px the 2026-09-11 re-flight measured after PR #1679 (Real Spawn
        /// Control header Craft 139 vs row 134, cells 1..5 five px left of their
        /// headers) is exactly those two terms the old reservation missed: 1px of
        /// scrollbar margin the footprint adds, and the 4px cell margin the max()
        /// replaces.</para>
        /// </summary>
        internal static float VerticalScrollbarGutterWidth()
        {
            return ComposeScrollbarGutterWidth(
                VerticalScrollbarFootprintWidth(), TableCellHorizontalMarginPx());
        }

        /// <summary>
        /// The gutter rule on its own, over already-read skin terms: the scrollbar
        /// footprint PLUS one cell margin (a sum, because the header padding replaces
        /// the body's trailing cell margin rather than adding to it). Split out so the
        /// rule can be checked against census-measured widths without a live skin.
        /// </summary>
        internal static float ComposeScrollbarGutterWidth(float scrollbarFootprint,
            int cellHorizontalMargin)
        {
            return scrollbarFootprint + cellHorizontalMargin;
        }

        private void EnsureSharedHeaderStyles()
        {
            if (sharedSectionHeaderStyle != null && sharedColumnHeaderStyle != null
                && sharedTableRowStyle != null && sharedTableHeaderRowStyle != null
                && sharedTableBodyBoxStyle != null
                && sharedTableSectionHeaderStyle != null
                && sharedTableCellStyle != null
                && sharedTableScrollViewStyle != null) return;

            // Section header - bold label in a box, left-aligned, stretches full width.
            sharedSectionHeaderStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                stretchWidth = true
            };

            // Column header - bold label in a box with slightly-lighter textColor.
            sharedColumnHeaderStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            // Table row container - no background of its own. Horizontal margin and
            // padding are the shared inset; vertical 4px keeps the skin's row pitch.
            sharedTableRowStyle = new GUIStyle
            {
                margin = new RectOffset(
                    TableRowHorizontalInsetPx, TableRowHorizontalInsetPx, 4, 4),
                padding = new RectOffset(
                    TableRowHorizontalInsetPx, TableRowHorizontalInsetPx, 0, 0)
            };

            // Header variant: the same left inset, plus a right padding equal to the
            // gutter a pinned header owes its body (the scroll view's real scrollbar
            // footprint PLUS the one cell margin this padding replaces - see
            // VerticalScrollbarGutterWidth), so the expanding column is as wide here as
            // in the body. Reserved as the row's own padding rather than a trailing
            // GUILayout.Space at each call site, so a pinned header cannot reserve a
            // width the scroll view does not actually claim.
            sharedTableHeaderRowStyle = new GUIStyle(sharedTableRowStyle)
            {
                padding = new RectOffset(
                    TableRowHorizontalInsetPx,
                    TableRowHorizontalInsetPx + (int)VerticalScrollbarGutterWidth(),
                    0, 0)
            };

            // Dark list-area background for a table body. Horizontal margin AND
            // padding zeroed so the box contributes no inset of its own; vertical
            // kept at the skin's 4px. The Recordings tab's list area uses it too.
            sharedTableBodyBoxStyle = new GUIStyle(GUI.skin.box)
            {
                margin = new RectOffset(
                    TableRowHorizontalInsetPx, TableRowHorizontalInsetPx, 4, 4),
                padding = new RectOffset(
                    TableRowHorizontalInsetPx, TableRowHorizontalInsetPx, 4, 4)
            };

            // Body cell label for a table whose header row uses the boxed column-header
            // style: the header box insets its text by its own padding.left (KSP box
            // padding L4), a bare GUI.skin.label does not, so a plain label cell drew its
            // text 4px left of the header text above it even with both cell RECTS at the
            // same x. The cell takes the header's HORIZONTAL padding and keeps the
            // label's vertical padding, so row pitch is unchanged.
            RectOffset hdrPad = sharedColumnHeaderStyle.padding;
            RectOffset lblPad = GUI.skin.label.padding;
            int[] cellPad = ComposeTableCellPadding(
                hdrPad.left, hdrPad.right, lblPad.top, lblPad.bottom);
            sharedTableCellStyle = new GUIStyle(GUI.skin.label)
            {
                padding = new RectOffset(cellPad[0], cellPad[1], cellPad[2], cellPad[3])
            };
            ParsekLog.Verbose("UI", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Table cell style built: colHdr.padding=L{0}/R{1} label.padding=L{2}/R{3}/T{4}/B{5} "
                + "cell.padding=L{6}/R{7}/T{8}/B{9}",
                hdrPad.left, hdrPad.right, lblPad.left, lblPad.right, lblPad.top, lblPad.bottom,
                cellPad[0], cellPad[1], cellPad[2], cellPad[3]));

            // Background for a table's body scroll view when it sits INSIDE the body box
            // together with the pinned header row: horizontal margin zeroed so the scroll
            // view starts at the box's content edge exactly as the header row does
            // (inside a zero-padding group a child's own margin.left would inset it).
            sharedTableScrollViewStyle = new GUIStyle(GUI.skin.scrollView)
            {
                margin = new RectOffset(
                    TableRowHorizontalInsetPx, TableRowHorizontalInsetPx,
                    GUI.skin.scrollView.margin.top, GUI.skin.scrollView.margin.bottom)
            };

            // Section bar sitting directly above a table: the shared section-header
            // style with the same zeroed horizontal margin the table row and body box
            // carry, so the bar spans exactly the table it labels.
            sharedTableSectionHeaderStyle = new GUIStyle(sharedSectionHeaderStyle)
            {
                margin = new RectOffset(
                    TableRowHorizontalInsetPx, TableRowHorizontalInsetPx,
                    sharedSectionHeaderStyle.margin.top,
                    sharedSectionHeaderStyle.margin.bottom)
            };
        }

        /// <summary>
        /// Shared table-row container: use for BOTH a table's column-header row and
        /// its body rows so they share one horizontal inset
        /// (<see cref="TableRowHorizontalInsetPx"/>) and every column starts at the
        /// same x. Must be called during draw (requires a valid GUI.skin).
        /// </summary>
        public GUIStyle GetTableRowStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedTableRowStyle;
        }

        /// <summary>
        /// Column-header row container for a header pinned OUTSIDE a body scroll view
        /// that forces a vertical scrollbar: <see cref="GetTableRowStyle"/> plus the
        /// scrollbar gutter as right padding. A header drawn INSIDE the same scroll
        /// view as its rows must use <see cref="GetTableRowStyle"/> instead - it
        /// shrinks with the body already.
        /// </summary>
        public GUIStyle GetTableHeaderRowStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedTableHeaderRowStyle;
        }

        /// <summary>
        /// Dark list-area background for a table body that contributes no horizontal
        /// inset, so body rows keep the header row's origin.
        /// </summary>
        public GUIStyle GetTableBodyBoxStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedTableBodyBoxStyle;
        }

        /// <summary>
        /// Body cell label for a table whose column headers use
        /// <see cref="GetColumnHeaderStyle"/>: a label whose horizontal padding is the
        /// header style's, so cell text starts at the same x as the header text above it
        /// (the two cell rects already share an x through the shared row containers).
        /// Tint it with <c>GUI.contentColor</c>, or derive a coloured copy from it; a bare
        /// <c>GUI.skin.label</c> in a boxed-header table draws its text one box padding
        /// left of the header text.
        /// </summary>
        public GUIStyle GetTableCellStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedTableCellStyle;
        }

        /// <summary>
        /// Scroll-view background for a table body drawn inside the same
        /// <see cref="GetTableBodyBoxStyle"/> box as its pinned header row: the skin's
        /// scroll view with no horizontal margin, so the rows start where the header does.
        /// </summary>
        public GUIStyle GetTableScrollViewStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedTableScrollViewStyle;
        }

        /// <summary>
        /// The body-cell padding rule, over already-read skin terms: left and right come
        /// from the boxed column header (so cell text and header text share one text
        /// inset), top and bottom from the label the cell is built on (so the row pitch
        /// does not change). Returned as {left, right, top, bottom}. Split out so the rule
        /// can be checked without a live skin.
        /// </summary>
        internal static int[] ComposeTableCellPadding(int headerPaddingLeft,
            int headerPaddingRight, int labelPaddingTop, int labelPaddingBottom)
        {
            return new[]
            {
                headerPaddingLeft, headerPaddingRight, labelPaddingTop, labelPaddingBottom
            };
        }

        /// <summary>
        /// Horizontal distance from a column header's text to the text of the body cell
        /// under it: each text starts at its cell's x plus its style's left padding (both
        /// left-aligned). Zero is aligned; negative is body text left of the header text.
        /// </summary>
        internal static int HeaderToCellTextDeltaPx(int headerCellX, int headerPaddingLeft,
            int bodyCellX, int bodyPaddingLeft)
        {
            return (bodyCellX + bodyPaddingLeft) - (headerCellX + headerPaddingLeft);
        }

        /// <summary>
        /// Section bar for a bar drawn directly above a table: like
        /// <see cref="GetSectionHeaderStyle"/> but with the same zeroed horizontal
        /// margin the table row and body box carry, so the bar spans exactly the
        /// table it labels instead of sitting an inset inside it.
        /// </summary>
        public GUIStyle GetTableSectionHeaderStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedTableSectionHeaderStyle;
        }

        /// <summary>
        /// Shared section-header style: bold label in a box, stretches full width.
        /// Must be called during draw (requires a valid GUI.skin).
        /// </summary>
        public GUIStyle GetSectionHeaderStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedSectionHeaderStyle;
        }

        /// <summary>
        /// Shared column-header style: bold label in a box with slightly-lighter textColor.
        /// Must be called during draw (requires a valid GUI.skin).
        /// </summary>
        public GUIStyle GetColumnHeaderStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedColumnHeaderStyle;
        }

        // ============================================================
        //  Shared status-text color palette (L4)
        // ============================================================

        /// <summary>
        /// The canonical status-text color slots shared across windows that color
        /// status labels (the house palette). Windows keep their own GUIStyle objects
        /// (padding / wordWrap differ) but pull the text color from one source so the
        /// literals live in a single place.
        /// </summary>
        public enum StatusColorKind
        {
            /// <summary>Good / delivering / active (0.55, 1, 0.55).</summary>
            Green = 0,

            /// <summary>Caution / flying-but-not-delivering (1, 1, 0.4).</summary>
            Yellow = 1,

            /// <summary>Broken / hard error (1, 0.4, 0.4).</summary>
            Red = 2,

            /// <summary>Inert / paused (0.7, 0.7, 0.7).</summary>
            Grey = 3,

            /// <summary>New / informational / eligible (0.65, 0.85, 1).</summary>
            Cyan = 4,

            /// <summary>Soft violet #b39ddb: the Logistics Paused Routes section accent.</summary>
            Violet = 5,
        }

        /// <summary>
        /// The single source of truth for the house status-text palette. Pure (no GUI
        /// side effects), so it is unit-testable directly and both windows resolve the
        /// same RGBA. Values are taken verbatim from the prior Logistics palette so no
        /// rendered color changes when the windows route through here.
        /// </summary>
        internal static Color StatusColor(StatusColorKind kind)
        {
            switch (kind)
            {
                case StatusColorKind.Green:
                    return new Color(0.55f, 1f, 0.55f);
                case StatusColorKind.Yellow:
                    return new Color(1f, 1f, 0.4f);
                case StatusColorKind.Red:
                    return new Color(1f, 0.4f, 0.4f);
                case StatusColorKind.Cyan:
                    return new Color(0.65f, 0.85f, 1f);
                case StatusColorKind.Violet:
                    return new Color(179f / 255f, 157f / 255f, 219f / 255f);
                case StatusColorKind.Grey:
                default:
                    return new Color(0.7f, 0.7f, 0.7f);
            }
        }

        /// <summary>
        /// Public accessor for the shared status-text palette. Windows assign the
        /// returned color to their own GUIStyle's <c>normal.textColor</c> during draw.
        /// </summary>
        public Color GetStatusColor(StatusColorKind kind)
        {
            return StatusColor(kind);
        }

        /// <summary>
        /// The house countdown amber (#ffcc66,
        /// <see cref="MissionPresentation.SummaryCountdownColorHex"/>): every
        /// <see cref="ParsekTimeFormat.FormatCountdown"/> cell is drawn in it (Missions
        /// summary, Logistics Next, Real Spawn Control, the Timeline's next-row time,
        /// the Recordings Status of a flight still ahead).
        /// </summary>
        internal static readonly Color CountdownTextColor = new Color(1f, 0.8f, 0.4f);

        /// <summary>
        /// The house muted text grey: second lines of a two-line row, detail lines and
        /// empty-list sentences. Set on a style's text colour, never through GUI.color
        /// (which would also dim rich-text colour runs).
        /// </summary>
        internal static readonly Color MutedTextColor = new Color(0.78f, 0.78f, 0.78f);

        /// <summary>
        /// The house dimming for a row that is not live yet or no longer live (a Missions
        /// row that cannot act, a Timeline row still ahead): white at 45% alpha.
        /// </summary>
        internal static readonly Color DimTextColor = new Color(1f, 1f, 1f, 0.45f);

        /// <summary>
        /// The empty-list sentence ("No missions recorded yet."): one plain label in
        /// <see cref="MutedTextColor"/>, the same in every window. Must be called during
        /// draw (requires a valid GUI.skin).
        /// </summary>
        public GUIStyle GetEmptyStateStyle()
        {
            GUIStyle source = GUI.skin.label;
            if (sharedEmptyStateStyle == null || !ReferenceEquals(sharedEmptyStateStyleSource, source))
            {
                sharedEmptyStateStyle = new GUIStyle(source)
                {
                    normal = { textColor = MutedTextColor }
                };
                sharedEmptyStateStyleSource = source;
            }
            return sharedEmptyStateStyle;
        }

        // ════════════════════════════════════════════════════════════════
        //  Sortable header helper (shared by RecordingsTableUI + SpawnControlUI)
        // ════════════════════════════════════════════════════════════════

        /// <summary>The house rule-line grey (the Timeline's "now" divider, a bit darker
        /// than its grey label text).</summary>
        internal static readonly Color RuleLineColor = new Color(0.4f, 0.4f, 0.4f, 1f);

        /// <summary>
        /// A thin rule: a 1x1 texture of <paramref name="color"/> stretched by a Box
        /// (callers give the height and ExpandWidth). The Timeline's "now" divider and the
        /// Logistics section separators and accent bars are drawn with it.
        /// </summary>
        internal static GUIStyle CreateRuleLineStyle(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            var style = new GUIStyle();
            style.normal.background = tex;
            style.padding = new RectOffset(0, 0, 0, 0);
            return style;
        }

        internal void DrawSortableHeaderCore<TCol>(
            string label, TCol col, ref TCol currentCol, ref bool ascending,
            float width, bool expand, Action onChanged, float height = 0f,
            string tooltip = null)
            where TCol : struct
        {
            string arrow = EqualityComparer<TCol>.Default.Equals(currentCol, col)
                ? (ascending ? " \u25b2" : " \u25bc") : "";
            // Reuse the shared column-header style so clickable sort headers match the
            // static column headers in the same row — otherwise the header row looks
            // half bold-boxed and half plain.
            GUIStyle headerStyle = GetColumnHeaderStyle();
            var content = new GUIContent(label + arrow, tooltip);
            bool clicked;
            if (expand)
                clicked = height > 0f
                    ? GUILayout.Button(content, headerStyle, GUILayout.ExpandWidth(true), GUILayout.Height(height))
                    : GUILayout.Button(content, headerStyle, GUILayout.ExpandWidth(true));
            else
                clicked = height > 0f
                    ? GUILayout.Button(content, headerStyle, GUILayout.Width(width), GUILayout.Height(height))
                    : GUILayout.Button(content, headerStyle, GUILayout.Width(width));

            if (clicked)
            {
                if (EqualityComparer<TCol>.Default.Equals(currentCol, col))
                    ascending = !ascending;
                else
                {
                    currentCol = col;
                    ascending = true;
                }
                onChanged?.Invoke();
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Loop time unit helpers (shared — used by SettingsWindowUI + RecordingsTableUI)
        // ════════════════════════════════════════════════════════════════

        internal static string UnitLabel(LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? "min"
             : unit == LoopTimeUnit.Hour ? "hr"
             : unit == LoopTimeUnit.Auto ? "auto"
             : "sec";

        internal static LoopTimeUnit CycleDisplayUnit(LoopTimeUnit u)
            => u == LoopTimeUnit.Sec ? LoopTimeUnit.Min
             : u == LoopTimeUnit.Min ? LoopTimeUnit.Hour
             : LoopTimeUnit.Sec;

        // Auto is resolved separately by callers; falls through to seconds if called directly.
        internal static double ConvertFromSeconds(double seconds, LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? seconds / 60.0
             : unit == LoopTimeUnit.Hour ? seconds / 3600.0
             : seconds;

        // Auto is resolved separately by callers; falls through to seconds if called directly.
        internal static double ConvertToSeconds(double value, LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? value * 60.0
             : unit == LoopTimeUnit.Hour ? value * 3600.0
             : value;

        internal static string FormatLoopValue(double value, LoopTimeUnit unit)
        {
            if (unit == LoopTimeUnit.Min || unit == LoopTimeUnit.Hour)
                return value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            return ((long)System.Math.Truncate(value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool TryParseLoopInput(string text, LoopTimeUnit unit, out double value)
        {
            if (unit == LoopTimeUnit.Min || unit == LoopTimeUnit.Hour)
                return double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            int intVal;
            bool ok = int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out intVal);
            value = intVal;
            return ok;
        }

        // ════════════════════════════════════════════════════════════════
        //  Forwarding methods for backward compatibility with tests
        //  (canonical implementations are on RecordingsTableUI)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Forwards to RecordingsTableUI.BuildGroupTreeData for backward compatibility with tests.
        /// </summary>
        internal static void BuildGroupTreeData(
            IReadOnlyList<Recording> committed, int[] sortedIndices,
            List<string> KnownEmptyGroups,
            out Dictionary<string, List<int>> grpToRecs,
            out Dictionary<string, List<int>> chainToRecs,
            out Dictionary<string, List<string>> grpChildren,
            out List<string> rootGrps,
            out HashSet<string> rootChainIds)
        {
            RecordingsTableUI.BuildGroupTreeData(committed, sortedIndices, KnownEmptyGroups,
                out grpToRecs, out chainToRecs, out grpChildren, out rootGrps, out rootChainIds);
        }

        internal enum SortColumn { Index, Phase, Name, LaunchTime, Duration, Status, LaunchSite }

        internal static int GetStatusOrder(Recording rec, double now)
            => RecordingsTableUI.GetStatusOrder(rec, now);

        internal static double GetRecordingSortKey(Recording rec, SortColumn column, double now, int rowFallback)
            => RecordingsTableUI.GetRecordingSortKey(rec, (RecordingsTableUI.SortColumn)(int)column, now, rowFallback);

        internal static double GetChainSortKey(List<int> members, IReadOnlyList<Recording> committed,
            SortColumn column, double now)
            => RecordingsTableUI.GetChainSortKey(members, committed, (RecordingsTableUI.SortColumn)(int)column, now);

        internal static int CompareRecordings(
            Recording ra, Recording rb,
            SortColumn column, bool ascending, double now)
            => RecordingsTableUI.CompareRecordings(ra, rb, (RecordingsTableUI.SortColumn)(int)column, ascending, now);

        internal static int[] BuildSortedIndices(
            IReadOnlyList<Recording> committed, SortColumn column, bool ascending, double now)
            => RecordingsTableUI.BuildSortedIndices(committed, (RecordingsTableUI.SortColumn)(int)column, ascending, now);

        internal static string FormatDuration(double seconds)
            => RecordingsTableUI.FormatDuration(seconds);

        internal static string FormatAltitude(double meters)
            => RecordingsTableUI.FormatAltitude(meters);

        internal static string FormatSpeed(double mps)
            => RecordingsTableUI.FormatSpeed(mps);

        internal static string FormatDistance(double meters)
            => RecordingsTableUI.FormatDistance(meters);

        internal static double GetGroupEarliestStartUT(HashSet<int> descendants, IReadOnlyList<Recording> committed)
            => RecordingsTableUI.GetGroupEarliestStartUT(descendants, committed);

        internal static double GetGroupSpanDuration(HashSet<int> descendants, IReadOnlyList<Recording> committed)
            => RecordingsTableUI.GetGroupSpanDuration(descendants, committed);

        internal static int FindGroupMainRecordingIndex(
            HashSet<int> descendants, IReadOnlyList<Recording> committed)
            => RecordingsTableUI.FindGroupMainRecordingIndex(descendants, committed);

        internal static void GetGroupStatus(HashSet<int> descendants, IReadOnlyList<Recording> committed,
            double now, out string statusText, out int statusOrder)
            => RecordingsTableUI.GetGroupStatus(descendants, committed, now, out statusText, out statusOrder);

        internal static double GetGroupSortKey(HashSet<int> descendants, IReadOnlyList<Recording> committed,
            SortColumn column, double now)
            => RecordingsTableUI.GetGroupSortKey(descendants, committed, (RecordingsTableUI.SortColumn)(int)column, now);

        internal static string UnitSuffix(LoopTimeUnit unit)
            => RecordingsTableUI.UnitSuffix(unit);

        internal static LoopTimeUnit CycleRecordingUnit(LoopTimeUnit u)
            => RecordingsTableUI.CycleRecordingUnit(u);

        internal static void ApplyAutoLoopRange(Recording rec, bool loopEnabled)
            => RecordingsTableUI.ApplyAutoLoopRange(rec, loopEnabled);

        // ════════════════════════════════════════════════════════════════
        //  Settings / Spawn Control / Test Runner (extracted)
        // ════════════════════════════════════════════════════════════════

        public void DrawSettingsWindowIfOpen(Rect mainWindowRect)
        {
            settingsUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawSpawnControlWindowIfOpen(Rect mainWindowRect)
        {
            spawnControlUI.DrawIfOpen(mainWindowRect, flight, InFlight);
        }

        public void DrawTestRunnerWindowIfOpen(Rect mainWindowRect, MonoBehaviour host)
        {
            testRunnerUI.DrawIfOpen(mainWindowRect, host);
        }

        internal bool IsMouseOverOpenAuxiliaryWindows(Vector2 mousePosition)
        {
            return recordingsTableUI.IsMouseOverOpenWindow(mousePosition)
                || settingsUI.IsMouseOverOpenWindow(mousePosition)
                || testRunnerUI.IsMouseOverOpenWindow(mousePosition);
        }

        internal static bool IsPointerOverOpenWindow(bool isOpen, Rect windowRect, Vector2 mousePosition)
        {
            return isOpen
                && windowRect.width > 0f
                && windowRect.height > 0f
                && windowRect.Contains(mousePosition);
        }

        internal void ToggleTestRunner()
        {
            testRunnerUI.IsOpen = !testRunnerUI.IsOpen;
            ParsekLog.Verbose("UI", $"Test runner window toggled: {(testRunnerUI.IsOpen ? "open" : "closed")}");
        }

        public void Cleanup()
        {
            // Only drop the live-instance reference when it still points at THIS instance:
            // a scene handover constructs the next ParsekUI before tearing down the previous
            // one, and nulling it there would leave the mode-apply hook with no window to
            // clamp / close.
            if (ReferenceEquals(activeInstance, this))
                activeInstance = null;

            timelineUI.UnsubscribeTimelineDataChanged();
            kerbalsUI.UnsubscribeTimelineDataChanged();
            DisplayNames.DisplayNamesChanged -= OnDisplayNamesChanged;
            // The Kerbals tab reads LIVE crew state as well as the ledger, so it carries
            // its own stock-event subscriptions alongside the ledger hook above; they are
            // added in both constructors and dropped here, together.
            kerbalsUI.UnsubscribeLiveCrewEvents();
            recordingsTableUI.ReleaseInputLock();
            timelineUI.ReleaseInputLock();
            kerbalsUI.ReleaseInputLock();
            settingsUI.ReleaseInputLock();
            spawnControlUI.ReleaseInputLock();
            structureListUI.ReleaseInputLock();
            routeHistoryUI.ReleaseInputLock();
            ResetCachedWindowStylesForSceneChange();
            // Map marker resources (icon atlas, fallback diamond, label style) are
            // owned by MapMarkerRenderer and reset per scene via ResetForSceneChange.
        }

        /// <summary>
        /// Handles resize drag for a window, then fits the rect to the screen. Call before
        /// the window function. Group Popup passes null for windowName to suppress the
        /// resize logging.
        /// </summary>
        internal static void HandleResizeDrag(ref Rect windowRect, ref bool isResizing,
            float minWidth, float minHeight, string windowName)
        {
            if (isResizing)
                ApplyResizeDrag(ref windowRect, ref isResizing, minWidth, minHeight, windowName);
            FitWindowToScreen(ref windowRect, minWidth, windowName);
        }

        /// <summary>
        /// Fits a window that cannot fit the screen at its own width (see
        /// <see cref="WideWindowLayout.FitToScreen"/>): capped to the screen width and moved
        /// onto it, and grown back to its minimum once a wider screen allows. Runs before
        /// every draw of every window that has a resize handle. A window that fits the
        /// screen is never touched, wherever the player put it. Logged only when it
        /// moves the rect, rate-limited per window because a player dragging a window
        /// against the edge moves it every frame.
        /// </summary>
        internal static void FitWindowToScreen(ref Rect windowRect, float minWidth,
            string windowName)
        {
            // Scaled-root units: inside a UI-scale host scope the screen is
            // Screen.width / scale wide (ParsekUiScale).
            float sw = ParsekUiScale.ScaledScreenWidth;
            float sh = ParsekUiScale.ScaledScreenHeight;
            Rect before = windowRect;
            if (!WideWindowLayout.FitToScreen(ref windowRect, minWidth, sw, sh)) return;
            string name = windowName ?? "Popup window";
            ParsekLog.VerboseRateLimited("UI", "window-screen-fit:" + name,
                WideWindowLayout.FormatFitLog(name, before, windowRect, sw, sh));
        }

        private static void ApplyResizeDrag(ref Rect windowRect, ref bool isResizing,
            float minWidth, float minHeight, string windowName)
        {
            if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseUp)
            {
                // Never wider than the screen, and a screen narrower than the window's own
                // minimum lowers that minimum to the screen width.
                float newW = WideWindowLayout.ResizeDragWidth(
                    Event.current.mousePosition.x, windowRect.x, minWidth, ParsekUiScale.ScaledScreenWidth);
                float newH = Mathf.Max(minHeight, Event.current.mousePosition.y - windowRect.y);
                windowRect.width = newW;
                windowRect.height = newH;
            }
            if (Event.current.type == EventType.MouseUp)
            {
                isResizing = false;
                if (windowName != null)
                {
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    ParsekLog.Verbose("UI",
                        $"{windowName} resize ended: w={windowRect.width.ToString("F0", ic)} h={windowRect.height.ToString("F0", ic)}");
                }
            }
            if (Event.current.type == EventType.MouseDrag)
                Event.current.Use();
        }

        /// <summary>
        /// Draws a resize handle triangle and starts resize on mouse down.
        /// Call at the end of the window draw function.
        /// </summary>
        internal static void DrawResizeHandle(Rect windowRect, ref bool isResizing, string windowName)
        {
            Rect handleRect = new Rect(
                windowRect.width - ResizeHandleSize,
                windowRect.height - ResizeHandleSize,
                ResizeHandleSize, ResizeHandleSize);
            GUI.Label(handleRect, "\u25e2");
            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                isResizing = true;
                if (windowName != null)
                    ParsekLog.Verbose("UI", $"{windowName} resize started");
                Event.current.Use();
            }
        }
    }
}
