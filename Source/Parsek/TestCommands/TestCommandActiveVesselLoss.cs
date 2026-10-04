using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure fail-fast decision for a pending two-phase step whose completion acts on the
    /// vessel that was ACTIVE when the step began (the EVA kerbal). When that vessel dies
    /// mid-step the completion poll ends the command at once with
    /// <c>ERROR msg=active-vessel-lost</c> instead of waiting out the verb's whole budget.
    ///
    /// <para>Why a DEAD state and not only a null reference: stock <c>Vessel.Die</c>
    /// (decompiled, KSP 1.12.5) destroys the GameObject of a NON-active vessel, but for
    /// the ACTIVE vessel it only sets <c>state = DEAD</c>, zeroes the velocities and
    /// leaves the object in place as <c>FlightGlobals.ActiveVessel</c>. A dead active
    /// kerbal therefore never reads Unity-null, which is exactly why the EVA-8 step-move
    /// runs (2026-09-29) waited their full 120 s <c>step-timeout</c> after Jeb died.
    /// DEAD is one-way, so no debounce is needed.</para>
    ///
    /// <para>The watched set is deliberately small. It names only the verbs whose wait
    /// is about that same kerbal and that never end, switch away from or destroy the
    /// active vessel on their own success path. Excluded on purpose: EvaChuteDeploy (owns
    /// a debounced <c>eva-chute-kerbal-lost</c> terminal already), EvaBoard (boarding
    /// removes the EVA vessel by design), EvaExit (switches focus to the new kerbal),
    /// Recover / ExitToSpaceCenter / LoadGame / InvokeRewind (leave or reload the scene),
    /// and WarpToUT (its OK is about the clock, and what the warp did to the vessel is
    /// asserted from log lines, so a crash under warp is a spec's own subject).</para>
    /// </summary>
    internal static class TestCommandActiveVesselLoss
    {
        /// <summary>The ERROR terminal's msg when the watched vessel is lost mid-step.</summary>
        internal const string Reason = "active-vessel-lost";

        private static readonly HashSet<string> WatchedVerbs =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "EvaGroundScience",
                "PlantFlag",
            };

        /// <summary>Read-only view of the watched verb set (for coverage tests).</summary>
        internal static IEnumerable<string> WatchedVerbList => WatchedVerbs;

        /// <summary>True when a pending <paramref name="verb"/> should capture the active
        /// vessel at initiation and fail fast if it is lost.</summary>
        internal static bool WatchesActiveVessel(string verb)
            => verb != null && WatchedVerbs.Contains(verb);

        /// <summary>True when the captured vessel is gone: the reference reads null
        /// (GameObject destroyed) or the vessel reached stock's one-way DEAD state.</summary>
        internal static bool IsLost(bool referenceAlive, bool stateDead)
            => !referenceAlive || stateDead;

        /// <summary>The whole decision: a watched verb, a watch that actually captured a
        /// vessel at initiation, and that vessel now lost. A watch that captured nothing
        /// (no active vessel at initiation) never fails a step.</summary>
        internal static bool ShouldFailFast(string verb, bool watchArmed, bool referenceAlive, bool stateDead)
            => watchArmed && WatchesActiveVessel(verb) && IsLost(referenceAlive, stateDead);

        /// <summary>The loss kind token for the log line.</summary>
        internal static string LossKind(bool referenceAlive, bool stateDead)
        {
            if (!referenceAlive) return "destroyed";
            return stateDead ? "dead" : "alive";
        }

        /// <summary>The one Warn line written when the guard ends a step.</summary>
        internal static string FormatLossLine(string id, string verb, string vesselName, uint pid,
            bool referenceAlive, bool stateDead, double elapsedSeconds)
        {
            return "active-vessel lost id=" + (id ?? string.Empty)
                + " cmd=" + (verb ?? string.Empty)
                + " vessel=" + (vesselName ?? string.Empty)
                + " pid=" + pid.ToString(CultureInfo.InvariantCulture)
                + " state=" + LossKind(referenceAlive, stateDead)
                + " elapsed=" + elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s"
                + " reason=" + Reason;
        }
    }
}
