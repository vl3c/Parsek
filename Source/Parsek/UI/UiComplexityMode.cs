using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// UI complexity mode (design `docs/dev/design-ui-basic-advanced.md` section 6.1).
    /// Explicit int values because the value is persisted through
    /// `ParsekSettings.uiComplexityMode` (phase 3), matching the existing
    /// `samplingDensity` / `autoLoopTimeUnit` int-backed convention.
    /// </summary>
    internal enum UiComplexityMode
    {
        /// <summary>Reduced surface set. Default for new installs only (design 7.3).</summary>
        Basic = 0,

        /// <summary>Today's full UI, unchanged.</summary>
        Advanced = 1,
    }

    /// <summary>
    /// The enumerated gate keys, one per surface the mode can hide (design 6.1).
    /// Explicit int values are deliberately NOT assigned: these are never persisted
    /// and are resolved by name at each call site, so their ordering carries no
    /// contract.
    /// </summary>
    /// <remarks>
    /// Two naming notes that the member names alone do not carry:
    /// <list type="bullet">
    /// <item><description><see cref="MainButtonRecordings"/> is the MISSIONS launcher.
    /// Phase 1 renamed the main-window button, the window title, and the first/default
    /// tab to "Missions" in BOTH modes (design 4.1); the gate key keeps the
    /// Recordings-era name because it identifies the window whose code still lives in
    /// `UI/RecordingsTableUI.cs`.</description></item>
    /// <item><description>The Timeline `GoTo` cross-link button reuses
    /// <see cref="TabRecordings"/> as its gate key (design 4.1a): a control whose sole
    /// purpose is navigating to a hidden surface is gated by the TARGET surface's key,
    /// not by its host window's. No dedicated surface value exists for it.</description></item>
    /// </list>
    /// </remarks>
    internal enum UiSurface
    {
        /// <summary>Real Spawn Control launcher.</summary>
        MainButtonSpawnControl,

        /// <summary>Timeline launcher.</summary>
        MainButtonTimeline,

        /// <summary>Missions launcher (the window formerly labelled "Recordings").</summary>
        MainButtonRecordings,

        /// <summary>Logistics launcher.</summary>
        MainButtonLogistics,

        /// <summary>Kerbals launcher.</summary>
        MainButtonKerbals,

        /// <summary>Career State launcher.</summary>
        MainButtonCareer,

        /// <summary>Gloops Flight Recorder launcher.</summary>
        MainButtonGloops,

        /// <summary>Settings launcher.</summary>
        MainButtonSettings,

        /// <summary>The raw per-recording table tab. Also the gate key for the Timeline GoTo button.</summary>
        TabRecordings,

        /// <summary>The mission abstraction tab.</summary>
        TabMissions,

        /// <summary>Settings section: verbose logging, tracing toggles, Test Runner.</summary>
        SettingsSectionDiagnostics,

        /// <summary>Settings section: recorder fidelity tuning.</summary>
        SettingsSectionSampleDensity,
    }

    /// <summary>
    /// The single pure decision point for UI complexity gating (design philosophy 4,
    /// section 6.1). Unity-free and side-effect-free so every rule is unit tested
    /// directly, mirroring <see cref="LogisticsButtonState"/> and
    /// <see cref="SelectiveSpawnUI"/>.
    /// <para>Invariant (design section 5): this class feeds LAUNCHER / CONTENT draw
    /// sites and the mode-change close handler ONLY. No recorder, playback, dispatch,
    /// or ledger path may read it.</para>
    /// </summary>
    internal static class UiSurfaceVisibility
    {
        /// <summary>
        /// True when <paramref name="surface"/> is drawn in <paramref name="mode"/>.
        /// Advanced returns true for EVERY surface, so Advanced rendering stays
        /// identical to today. Basic decides per surface through an exhaustive switch.
        /// <para>Contract: an unhandled <see cref="UiSurface"/> value THROWS
        /// <see cref="ArgumentOutOfRangeException"/> rather than defaulting silently, so
        /// adding a surface without a Basic decision fails the
        /// `EverySurfaceIsDecided` test (design 6.1, 13.1) instead of shipping an
        /// accidental hide or an accidental show.</para>
        /// </summary>
        internal static bool IsVisible(UiSurface surface, UiComplexityMode mode)
        {
            // One exhaustive switch, no reflection: this runs on per-frame IMGUI draw
            // paths, so the unknown-value check must not cost an Enum.IsDefined call.
            bool visibleInBasic;
            switch (surface)
            {
                // --- Basic keeps the core loop (design section 4) ---
                case UiSurface.MainButtonTimeline:       // only access to rewind / FF / warp-to
                case UiSurface.MainButtonRecordings:     // the Missions launcher
                case UiSurface.MainButtonLogistics:      // only supply-route surface; kept for discoverability
                case UiSurface.MainButtonSettings:       // hosts the mode toggle itself
                case UiSurface.TabMissions:              // the player-facing mission abstraction
                    visibleInBasic = true;
                    break;

                // --- Basic hides these (design section 4) ---
                case UiSurface.MainButtonSpawnControl:       // advanced staging tool
                case UiSurface.MainButtonKerbals:            // read-only roster reference
                case UiSurface.MainButtonCareer:             // read-only career reference
                case UiSurface.MainButtonGloops:             // manual ghost-only recorder
                case UiSurface.TabRecordings:                // raw per-recording table (also gates Timeline GoTo)
                case UiSurface.SettingsSectionDiagnostics:   // developer instrumentation
                case UiSurface.SettingsSectionSampleDensity: // recorder fidelity tuning
                    visibleInBasic = false;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(surface),
                        surface,
                        "UiSurface has no Basic-mode visibility decision. Add one to " +
                        "UiSurfaceVisibility.IsVisible; the gate never defaults silently.");
            }

            // Advanced is today's UI: every known surface draws. The switch above still
            // runs in Advanced so an undecided addition throws in BOTH modes rather
            // than hiding behind an Advanced fast path.
            return mode == UiComplexityMode.Advanced || visibleInBasic;
        }

        /// <summary>
        /// Every surface hidden in <paramref name="mode"/>, empty for
        /// <see cref="UiComplexityMode.Advanced"/>. Consumed by the mode-change close
        /// handler (design 7.2) and by tests.
        /// <para>Derived by walking the <see cref="UiSurface"/> values through
        /// <see cref="IsVisible"/> so there is exactly ONE decision point and no second
        /// list that can drift out of step with it.</para>
        /// </summary>
        internal static IEnumerable<UiSurface> HiddenSurfaces(UiComplexityMode mode)
        {
            var hidden = new List<UiSurface>();
            foreach (UiSurface surface in (UiSurface[])Enum.GetValues(typeof(UiSurface)))
            {
                if (!IsVisible(surface, mode))
                    hidden.Add(surface);
            }
            return hidden;
        }

        /// <summary>
        /// Resolves the effective mode from the two inputs of design 7.3. Pure: it
        /// performs no I/O and never reads the settings store; both the stored value
        /// and the install footprint are supplied by the caller.
        /// <list type="bullet">
        /// <item><description>Stored value present -> use it, ALWAYS. This wins
        /// unconditionally over the footprint; an out-of-range stored int clamps to
        /// <see cref="UiComplexityMode.Advanced"/> (fail-open per design 6.2: showing
        /// everything is the safe wrong answer, hiding windows is not).</description></item>
        /// <item><description>No stored value AND no footprint -> Basic. A genuinely
        /// new player, nothing to disrupt.</description></item>
        /// <item><description>No stored value AND a footprint -> Advanced. An existing
        /// install updating into this feature keeps every window it used.</description></item>
        /// </list>
        /// </summary>
        /// <param name="storedValue">
        /// The persisted `uiComplexityMode` int, or null when the settings store holds
        /// no value for the key. Taken as an INPUT (rather than read inside) so the
        /// stored-wins-over-footprint precedence lives in this pure seam and the
        /// `StoredValueAlwaysWinsOverFootprint` test can genuinely fail if it inverts.
        /// </param>
        /// <param name="installHasParsekFootprint">
        /// True when this INSTALL has run Parsek before: any `saves/&lt;name&gt;/Parsek/`
        /// directory, a populated `SCENARIO{name=ParsekScenario}` node in the current
        /// save, or any stored settings key (design 7.3).
        /// </param>
        internal static UiComplexityMode ResolveMode(int? storedValue, bool installHasParsekFootprint)
        {
            if (storedValue.HasValue)
                return FromStoredInt(storedValue.Value);

            return installHasParsekFootprint ? UiComplexityMode.Advanced : UiComplexityMode.Basic;
        }

        /// <summary>
        /// Clamping int-to-mode conversion following the `SamplingDensityLevel`
        /// precedent (`ParsekSettings.cs`): any value outside the enum resolves to
        /// <see cref="UiComplexityMode.Advanced"/> (fail-open, design 6.2).
        /// </summary>
        internal static UiComplexityMode FromStoredInt(int storedValue)
        {
            if (!Enum.IsDefined(typeof(UiComplexityMode), storedValue))
                return UiComplexityMode.Advanced;
            return (UiComplexityMode)storedValue;
        }
    }
}
