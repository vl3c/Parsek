using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.Patches;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure half of the automation-only <c>CycleVessel</c> seam verb: one press of stock's
    /// <c>[</c> / <c>]</c> vessel cycle keys (<c>GameSettings.FOCUS_PREV_VESSEL</c> /
    /// <c>FOCUS_NEXT_VESSEL</c>), the GC-4 hole a ghost map marker inside physics range opened.
    ///
    /// <para><b>Why a call and not a key.</b> No seam can press a key, and the only reader of
    /// the cycle keys is stock's <c>VesselSwitching.Update</c>, which Parsek's
    /// <c>VesselCycleReFlySwitchPatch</c> Prefix fronts. The applier runs the Prefix's own
    /// body after its key read (<c>VesselCycleReFlySwitchPatch.RunCycle</c>), after the same
    /// game-mode gate, so every press the Prefix handles (a ghost marker or a Re-Fly-refused
    /// candidate dropped) switches through exactly the product code a key press runs. A press
    /// the Prefix leaves to stock (nothing dropped, or one of stock's early returns) moves
    /// nothing under the seam, because stock's Update finds no key down; the answer then names
    /// the vessel stock's final step would have taken (<c>stockWouldPick=</c>) and says
    /// <c>leftToStock=true</c>.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=CycleVessel direction=next|prev [forceGhost=true|false]</c>.
    /// <c>direction=</c> REQUIRED (<c>next</c> is <c>]</c>, <c>prev</c> is <c>[</c>).
    /// <c>forceGhost=true</c> also probes the backstop after the press: it calls
    /// <c>FlightGlobals.ForceSetActiveVessel</c> on the first ghost map marker among the
    /// press's candidates (Parsek's <c>GhostVesselForceSwitchPatch</c> must refuse it). FLIGHT
    /// only; SINGLE-PHASE on the default budget (a switch between loaded vessels sets the
    /// active vessel inside the call, decompiled <c>FlightGlobals.setActiveVessel</c>).</para>
    ///
    /// <para><b>Answer.</b> OK payload, in wire order: <c>direction= outcome= detail= before=
    /// after= switched= candidates= candidatePids= ghostPids= ghostsDropped= refusedDropped=
    /// leftToStock= picked= stockWouldPick= stockWouldPickGhost= forceGhost= forceGhostPid=</c>.
    /// Pid lists are comma-joined in stock's candidate order, <c>none</c> when empty; an unset
    /// pid is <c>none</c>. <c>forceGhost=</c> is <c>skipped</c>, <c>no-ghost</c>,
    /// <c>refused</c> (the call returned false and the active vessel stayed), <c>switched</c>
    /// (a ghost marker became the active vessel: the hole) or <c>unexpected</c>. Line:
    /// <c>cyclevessel result</c> followed by the payload.</para>
    /// </summary>
    internal static class TestCommandCycleVessel
    {
        internal const string Verb = "CycleVessel";
        internal const string DirectionKey = "direction";
        internal const string ForceGhostKey = "forceGhost";
        internal const string DirectionNext = "next";
        internal const string DirectionPrev = "prev";

        internal const string DirectionArgMissingReason = "cyclevessel-direction-arg-missing";
        internal const string DirectionArgInvalidReason = "cyclevessel-direction-arg-invalid";
        internal const string ForceGhostArgInvalidReason = "cyclevessel-forceghost-arg-invalid";
        internal const string NoActiveVesselReason = "cyclevessel-no-active-vessel";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.CYCLEVESSEL_REASONS mirrors it
        /// (CycleVesselSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "cyclevessel-direction-arg-missing",
            "cyclevessel-direction-arg-invalid",
            "cyclevessel-forceghost-arg-invalid",
            "cyclevessel-no-active-vessel",
        };

        /// <summary>The <c>outcome=</c> values, one per
        /// <see cref="VesselCycleReFlySwitchPatch.CycleOutcome"/> member in declaration order,
        /// then <c>inert</c> (the game-mode gate). hlib.CYCLEVESSEL_OUTCOMES mirrors it.</summary>
        internal static readonly string[] Outcomes =
        {
            "stock-early-return",
            "no-active-vessel",
            "no-candidates",
            "left-to-stock",
            "only-ghosts",
            "only-refused",
            "switched",
            "set-active-fallback",
            "inert",
        };

        internal const string ForceGhostSkipped = "skipped";
        internal const string ForceGhostNoGhost = "no-ghost";
        internal const string ForceGhostRefused = "refused";
        internal const string ForceGhostSwitched = "switched";
        internal const string ForceGhostUnexpected = "unexpected";

        /// <summary>Parses the args. Returns null on success or the refusal reason (direction
        /// first). <c>direction=</c> is exact and lower-case; <c>forceGhost=</c> is
        /// <c>true</c> / <c>false</c> (case-insensitive), absent means false.</summary>
        internal static string ParseArgs(string rawDirection, string rawForceGhost,
            out bool previous, out bool forceGhost)
        {
            previous = false;
            forceGhost = false;
            if (string.IsNullOrWhiteSpace(rawDirection))
                return DirectionArgMissingReason;
            if (string.Equals(rawDirection, DirectionPrev, StringComparison.Ordinal))
                previous = true;
            else if (!string.Equals(rawDirection, DirectionNext, StringComparison.Ordinal))
                return DirectionArgInvalidReason;
            if (rawForceGhost != null)
            {
                if (string.Equals(rawForceGhost, "true", StringComparison.OrdinalIgnoreCase))
                    forceGhost = true;
                else if (!string.Equals(rawForceGhost, "false", StringComparison.OrdinalIgnoreCase))
                    return ForceGhostArgInvalidReason;
            }
            return null;
        }

        /// <summary>The wire token of a press outcome.</summary>
        internal static string OutcomeToken(VesselCycleReFlySwitchPatch.CycleOutcome outcome)
        {
            int i = (int)outcome;
            return i >= 0 && i < Outcomes.Length - 1 ? Outcomes[i] : "unknown";
        }

        /// <summary>The first ghost map marker among the press's candidates (stock order), or 0.</summary>
        internal static uint FirstGhostCandidate(IList<uint> candidatePids, IList<bool> candidateIsGhost)
        {
            if (candidatePids == null || candidateIsGhost == null) return 0u;
            int n = Math.Min(candidatePids.Count, candidateIsGhost.Count);
            for (int i = 0; i < n; i++)
                if (candidateIsGhost[i])
                    return candidatePids[i];
            return 0u;
        }

        /// <summary>
        /// What the backstop probe saw: <c>switched</c> when the ghost became the active vessel
        /// (whatever the call answered), <c>refused</c> when the call answered false and the
        /// active vessel stayed, else <c>unexpected</c>.
        /// </summary>
        internal static string DecideForceGhostResult(bool callReturned, uint activeBefore,
            uint activeAfter, uint ghostPid)
        {
            if (ghostPid != 0u && activeAfter == ghostPid) return ForceGhostSwitched;
            if (!callReturned && activeAfter == activeBefore) return ForceGhostRefused;
            return ForceGhostUnexpected;
        }

        internal static string PidOrNone(uint pid)
            => pid == 0u ? "none" : pid.ToString(CultureInfo.InvariantCulture);

        /// <summary>Comma-joined pids (optionally only those whose flag is set), or <c>none</c>.</summary>
        internal static string JoinPids(IList<uint> pids, IList<bool> onlyWhere)
        {
            if (pids == null) return "none";
            var sb = new StringBuilder();
            for (int i = 0; i < pids.Count; i++)
            {
                if (onlyWhere != null && (i >= onlyWhere.Count || !onlyWhere[i])) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(pids[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            bool previous, string outcome, string detail, uint before, uint after,
            VesselCycleReFlySwitchPatch.CycleReport report, string forceGhostResult, uint forceGhostPid)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            var r = report ?? new VesselCycleReFlySwitchPatch.CycleReport();
            bool leftToStock = string.Equals(outcome, "left-to-stock", StringComparison.Ordinal)
                || string.Equals(outcome, "stock-early-return", StringComparison.Ordinal)
                || string.Equals(outcome, "no-active-vessel", StringComparison.Ordinal)
                || string.Equals(outcome, "no-candidates", StringComparison.Ordinal)
                || string.Equals(outcome, "inert", StringComparison.Ordinal);
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("direction", previous ? DirectionPrev : DirectionNext),
                new KeyValuePair<string, string>("outcome", outcome ?? "unknown"),
                new KeyValuePair<string, string>("detail", string.IsNullOrEmpty(detail) ? "none" : detail),
                new KeyValuePair<string, string>("before", PidOrNone(before)),
                new KeyValuePair<string, string>("after", PidOrNone(after)),
                new KeyValuePair<string, string>("switched", before != after ? "true" : "false"),
                new KeyValuePair<string, string>("candidates", r.CandidatePids.Count.ToString(ic)),
                new KeyValuePair<string, string>("candidatePids", JoinPids(r.CandidatePids, null)),
                new KeyValuePair<string, string>("ghostPids", JoinPids(r.CandidatePids, r.CandidateIsGhost)),
                new KeyValuePair<string, string>("ghostsDropped", r.GhostsDropped.ToString(ic)),
                new KeyValuePair<string, string>("refusedDropped", r.RefusedDropped.ToString(ic)),
                new KeyValuePair<string, string>("leftToStock", leftToStock ? "true" : "false"),
                new KeyValuePair<string, string>("picked", PidOrNone(r.PickedPid)),
                new KeyValuePair<string, string>("stockWouldPick", PidOrNone(r.StockWouldPickPid)),
                new KeyValuePair<string, string>("stockWouldPickGhost", r.StockWouldPickGhost ? "true" : "false"),
                new KeyValuePair<string, string>("forceGhost", forceGhostResult ?? ForceGhostSkipped),
                new KeyValuePair<string, string>("forceGhostPid", PidOrNone(forceGhostPid)),
            };
        }

        /// <summary>The grep-stable line logged on OK: <c>cyclevessel result</c> and the payload.</summary>
        internal static string FormatResultLine(List<KeyValuePair<string, string>> payload)
        {
            var sb = new StringBuilder("cyclevessel result");
            if (payload != null)
                for (int i = 0; i < payload.Count; i++)
                    sb.Append(' ').Append(payload[i].Key).Append('=').Append(payload[i].Value);
            return sb.ToString();
        }
    }
}
