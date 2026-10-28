using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The ledger's held-through UT (todo REWIND-LEAD-WINDOW-KSC-ACTIONS-LAND-IN-THE-FUTURE).
    /// A Rewind-to-Launch loads the save written at the launch and winds the clock back
    /// <see cref="RecordingStore.RewindToLaunchLeadTimeSeconds"/>. The rewind save's world already
    /// holds every ledger row in (wound-back clock, rewind save UT]: a strategy activated, a
    /// building demolished, a part bought in those seconds is in the loaded world, but its row
    /// lies ahead of the clock. While the clock is behind this UT those rows are in the past for
    /// the ledger: the walk cutoff, the committed-future index (stock-screen blocks and marks),
    /// the clock crossings, the KSP state patches and the pool reads all judge "now" as
    /// <see cref="ResolveLedgerNowUT"/>, never the raw clock. Rows after it stay committed
    /// future. No existing row is rewritten: the floor changes which rows count as reached,
    /// never a held row's UT. A NEW Space Center row captured while the clock is behind the
    /// floor is dated at it (<see cref="StampKscOriginRow"/>), so it comes after the held rows
    /// the player already sees; a row tagged to a recording keeps its trajectory UT.
    ///
    /// <para>Lifecycle, the ledger analog of the route state floor
    /// (<c>Logistics.RouteStore.StateFloorUT</c>): a plain rewind sets it from the rewind save
    /// (<see cref="ResolveAtRewind"/>); every other load installs what the loaded save carries,
    /// kept only while the loaded clock is behind it (<see cref="ResolveAfterLoad"/>); saves
    /// carry it in the PARSEK node (<see cref="ValueName"/>, sparse) so a save or a quickload
    /// inside the window keeps it; it expires once a trusted clock reaches it
    /// (<see cref="ExpireIfClockPassed"/>). Once the clock is past it, it has no effect.</para>
    /// </summary>
    internal static class LedgerHeldWindow
    {
        internal const string Tag = "LedgerHeldWindow";

        /// <summary>The PARSEK node value a save carries the held-through UT in. Written only
        /// while one is alive, so every other save stays byte-identical.</summary>
        internal const string ValueName = "ledgerHeldThroughUT";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The held-through UT in memory; NaN when none.</summary>
        internal static double HeldThroughUT { get; private set; } = double.NaN;

        internal static bool IsSet => IsUsableHeldUT(HeldThroughUT);

        /// <summary>A finite positive UT.</summary>
        internal static bool IsUsableHeldUT(double ut)
        {
            return !double.IsNaN(ut) && !double.IsInfinity(ut) && ut > 0.0;
        }

        /// <summary>
        /// Pure: the ledger's "now" for a clock reading <paramref name="ut"/>: raised to
        /// <paramref name="heldThroughUT"/> when that lies ahead of it. A reading that is not a
        /// finite positive UT stays as it is: NaN is an unknown clock and 0 a clock that is not
        /// ready (a cold load before the save's UT is set), neither of them a held one, and
        /// <c>double.MaxValue</c> is already past every floor.
        /// </summary>
        internal static double ApplyHeldFloor(double ut, double heldThroughUT)
        {
            if (double.IsNaN(ut) || double.IsInfinity(ut) || ut <= 0.0)
                return ut;
            if (!IsUsableHeldUT(heldThroughUT))
                return ut;
            return heldThroughUT > ut ? heldThroughUT : ut;
        }

        /// <summary>The ledger's "now" for <paramref name="clockUT"/> under the floor in memory.</summary>
        internal static double ResolveLedgerNowUT(double clockUT)
        {
            return ApplyHeldFloor(clockUT, HeldThroughUT);
        }

        /// <summary>
        /// A walk cutoff under the floor in memory: a cutoff behind the floor rises to it (the
        /// rows in between are in the loaded world); null (a full walk) stays null.
        /// </summary>
        internal static double? ResolveLedgerCutoffUT(double? cutoffUT)
        {
            if (!cutoffUT.HasValue)
                return cutoffUT;
            return ResolveLedgerNowUT(cutoffUT.Value);
        }

        /// <summary>
        /// Pure: the UT a Space Center row captured at <paramref name="capturedUT"/> is dated
        /// at under <paramref name="heldThroughUT"/>: the ledger's now
        /// (<see cref="ApplyHeldFloor"/>), so a row the player makes while the clock is behind
        /// the floor comes after every held row the loaded world already shows. Once the clock
        /// is past the floor (or none is set) it is the capture UT itself.
        /// </summary>
        internal static double ResolveKscRowUT(double capturedUT, double heldThroughUT)
        {
            return ApplyHeldFloor(capturedUT, heldThroughUT);
        }

        /// <summary>
        /// Dates a Space Center row (an untagged row of the KSC, strategy-conversion or
        /// live-only-income door) at <see cref="ResolveKscRowUT"/> under the floor in memory,
        /// keeping its capture UT in <see cref="GameAction.CapturedUT"/> for the readers that
        /// pair it with a game-state event or a stock clock (<see cref="CaptureMomentUT"/>).
        /// A row tagged to a recording is never moved: it belongs to its trajectory's UTs.
        /// Returns true when the row's UT was raised. Call before the row reaches the ledger:
        /// its capture sequence (issued at capture, above every held row's) then orders it
        /// after the held rows at that UT.
        /// </summary>
        internal static bool StampKscOriginRow(GameAction action, string door)
        {
            if (action == null || !string.IsNullOrEmpty(action.RecordingId))
                return false;
            double captured = action.UT;
            double dated = ResolveKscRowUT(captured, HeldThroughUT);
            if (!(dated > captured))
                return false;
            action.CapturedUT = captured;
            action.UT = dated;
            ParsekLog.Verbose(Tag,
                (door ?? "ksc") + ": " + action.Type + " id=" + (action.ActionId ?? "(none)")
                + " captured at ut=" + captured.ToString("R", IC)
                + " dated at the ledger held-through UT " + dated.ToString("R", IC)
                + " (after the held rows the loaded world shows)");
            return true;
        }

        /// <summary>
        /// Pure: the clock <paramref name="row"/> was captured at: its
        /// <see cref="GameAction.CapturedUT"/> when <see cref="StampKscOriginRow"/> dated it
        /// later, else its UT. Readers that pair a row with a game-state event, a stock clock
        /// or a loaded save's clock read this; the walk order and "reached" read the UT.
        /// </summary>
        internal static double CaptureMomentUT(GameAction row)
        {
            if (row == null)
                return double.NaN;
            double captured = row.CapturedUT;
            return double.IsNaN(captured) || double.IsInfinity(captured) ? row.UT : captured;
        }

        /// <summary>
        /// Pure: the floor a plain rewind sets. The rewind save's world is as of its own UT
        /// (<paramref name="rewindSaveOwnUT"/>, before the lead-time windback), raised to the
        /// held-through UT the save itself carries (<paramref name="carriedHeldUT"/>, a save
        /// written inside an earlier window). It is a floor only when it lies ahead of the
        /// wound-back clock (<paramref name="rewindAdjustedUT"/>); NaN otherwise (no windback, or
        /// either UT unknown), which keeps every row after the clock committed future.
        /// </summary>
        internal static double ResolveAtRewind(double rewindAdjustedUT, double rewindSaveOwnUT, double carriedHeldUT)
        {
            if (double.IsNaN(rewindAdjustedUT) || double.IsInfinity(rewindAdjustedUT) || rewindAdjustedUT < 0.0)
                return double.NaN;
            if (!IsUsableHeldUT(rewindSaveOwnUT))
                return double.NaN;
            double world = ApplyHeldFloor(rewindSaveOwnUT, carriedHeldUT);
            return world > rewindAdjustedUT ? world : double.NaN;
        }

        /// <summary>
        /// Pure: the floor to keep after a load whose save's clock is
        /// <paramref name="loadedClockUT"/> and which carried <paramref name="carriedHeldUT"/>:
        /// the carried floor while the clock is still behind it, NaN otherwise (none carried, the
        /// clock has passed it, or the clock is unknown).
        /// </summary>
        internal static double ResolveAfterLoad(double loadedClockUT, double carriedHeldUT)
        {
            if (double.IsNaN(loadedClockUT) || double.IsInfinity(loadedClockUT) || loadedClockUT < 0.0)
                return double.NaN;
            if (!IsUsableHeldUT(carriedHeldUT))
                return double.NaN;
            return carriedHeldUT > loadedClockUT ? carriedHeldUT : double.NaN;
        }

        /// <summary>Sets the floor a plain rewind computes (<see cref="ResolveAtRewind"/>) and logs it.</summary>
        internal static void SetAtRewind(double rewindAdjustedUT, double rewindSaveOwnUT, double carriedHeldUT)
        {
            double before = HeldThroughUT;
            double after = ResolveAtRewind(rewindAdjustedUT, rewindSaveOwnUT, carriedHeldUT);
            HeldThroughUT = after;
            ParsekLog.Info(Tag,
                "Rewind: ledger held-through UT " + Format(before) + " -> " + Format(after) +
                " (woundBackClock=" + rewindAdjustedUT.ToString("R", IC) +
                " rewindSaveUT=" + rewindSaveOwnUT.ToString("R", IC) +
                " carried=" + Format(carriedHeldUT) + ")" +
                (IsUsableHeldUT(after)
                    ? ": rows up to it are in the rewind save's world and count as reached until the clock passes it"
                    : ": no lead window, rows after the clock stay committed future"));
        }

        /// <summary>Installs the floor a loaded save carries (<see cref="ResolveAfterLoad"/>); logs a change.</summary>
        internal static void InstallAfterLoad(double loadedClockUT, double carriedHeldUT, string site)
        {
            double before = HeldThroughUT;
            double after = ResolveAfterLoad(loadedClockUT, carriedHeldUT);
            HeldThroughUT = after;
            if (IsUsableHeldUT(before) || IsUsableHeldUT(after) || IsUsableHeldUT(carriedHeldUT))
            {
                ParsekLog.Info(Tag,
                    (site ?? "Load") + ": ledger held-through UT " + Format(before) + " -> " + Format(after) +
                    " (loadedClock=" + loadedClockUT.ToString("R", IC) +
                    " carried=" + Format(carriedHeldUT) + ")");
            }
        }

        /// <summary>Drops the floor (a load that owns it elsewhere); logs when one was set.</summary>
        internal static void Clear(string reason)
        {
            if (IsUsableHeldUT(HeldThroughUT))
            {
                ParsekLog.Info(Tag,
                    "Ledger held-through UT " + Format(HeldThroughUT) + " cleared: reason=" + (reason ?? "unspecified"));
            }
            HeldThroughUT = double.NaN;
        }

        /// <summary>
        /// Drops the floor once a trusted clock (<paramref name="clockUT"/>) has reached it: from
        /// then on every row up to it is behind the clock anyway. Callers pass a clock only
        /// outside <c>ParsekScenario.OnLoad</c> and outside a rewind's pending UT adjustment
        /// (the Planetarium still reads the pre-load clock there). Returns true when it expired.
        /// </summary>
        internal static bool ExpireIfClockPassed(double clockUT, string site)
        {
            if (!IsUsableHeldUT(HeldThroughUT))
                return false;
            if (double.IsNaN(clockUT) || double.IsInfinity(clockUT) || clockUT < HeldThroughUT)
                return false;
            ParsekLog.Info(Tag,
                (site ?? "clock") + ": the clock " + clockUT.ToString("R", IC) +
                " reached the ledger held-through UT " + Format(HeldThroughUT) + "; floor expired");
            HeldThroughUT = double.NaN;
            return true;
        }

        /// <summary>
        /// Writes the floor into <paramref name="parent"/> (a PARSEK scenario node), replacing any
        /// value already there; writes nothing when no floor is alive.
        /// </summary>
        internal static void SaveInto(ConfigNode parent)
        {
            WriteValue(parent, HeldThroughUT);
        }

        /// <summary>Pure over a node: replaces <see cref="ValueName"/> with <paramref name="heldThroughUT"/>
        /// (removed when it is not a usable UT).</summary>
        internal static void WriteValue(ConfigNode parent, double heldThroughUT)
        {
            if (parent == null)
                return;
            parent.RemoveValues(ValueName);
            if (!IsUsableHeldUT(heldThroughUT))
                return;
            parent.AddValue(ValueName, heldThroughUT.ToString("R", IC));
            ParsekLog.Verbose(Tag, "Saved ledger held-through UT " + heldThroughUT.ToString("R", IC));
        }

        /// <summary>The floor a saved PARSEK node carries; NaN when none or it does not parse.</summary>
        internal static double ReadSaved(ConfigNode parent)
        {
            string raw = parent?.GetValue(ValueName);
            if (string.IsNullOrEmpty(raw)
                || !double.TryParse(raw, NumberStyles.Float, IC, out double ut)
                || !IsUsableHeldUT(ut))
                return double.NaN;
            return ut;
        }

        /// <summary><see cref="ReadSaved"/> over a parsed game's own <c>ParsekScenario</c> proto; NaN without one.</summary>
        internal static double ReadSavedFromScenarios(IList<ProtoScenarioModule> scenarios)
        {
            if (scenarios == null)
                return double.NaN;
            for (int i = 0; i < scenarios.Count; i++)
            {
                ProtoScenarioModule proto = scenarios[i];
                if (proto != null
                    && string.Equals(proto.moduleName, nameof(ParsekScenario), StringComparison.Ordinal))
                {
                    return ReadSaved(proto.GetData());
                }
            }
            return double.NaN;
        }

        /// <summary>Test seam: sets the floor directly.</summary>
        internal static void SetForTesting(double heldThroughUT)
        {
            HeldThroughUT = heldThroughUT;
        }

        internal static void ResetForTesting()
        {
            HeldThroughUT = double.NaN;
        }

        private static string Format(double ut)
        {
            return IsUsableHeldUT(ut) ? ut.ToString("R", IC) : "none";
        }
    }
}
