using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>One loaded vessel the <c>PickUpFlag</c> target search looked at.</summary>
    internal struct FlagPickUpCandidate
    {
        internal uint Pid;
        internal string Name;
        internal double DistanceMeters;
        internal bool IsFlag;
        internal bool IsGhostMapVessel;
        internal bool Loaded;
    }

    /// <summary>What one completion poll of a started <c>PickUpFlag</c> concludes.</summary>
    internal enum FlagPickUpDecision
    {
        /// <summary>The flag is still standing and nothing calls for a press: keep polling.</summary>
        KeepWaiting,

        /// <summary>Call stock's <c>FlagSite.PickUp()</c> on the target flag now.</summary>
        FirePickUp,

        /// <summary>The flag vessel is gone after our press and the scene settled: terminal OK.</summary>
        CompleteOk,

        /// <summary>The flag vessel went away before the seam pressed anything: terminal
        /// ERROR (<see cref="TestCommandPickUpFlag.FlagLostReason"/>).</summary>
        FlagLost,

        /// <summary>The budget expired with the flag still standing: terminal ERROR
        /// (<see cref="TestCommandPickUpFlag.TimeoutReason"/>).</summary>
        Timeout,
    }

    /// <summary>
    /// Pure half of the automation-only <c>PickUpFlag</c> seam verb: the active EVA kerbal
    /// takes down the nearest real flag, so a lane can record a flag pick-up
    /// (<c>SegmentEventType.FlagPickedUp</c>) and replay it after a rewind.
    ///
    /// <para><b>Stock ground truth (decompiled <c>FlagSite</c>, KSP 1.12.5).</b>
    /// <c>FlagSite.PickUp()</c> (the toppled flag's "Pick Up" event, and behind the standing
    /// flag's "Take Down" event) reads <c>FlightGlobals.ActiveVessel.GetComponent&lt;KerbalEVA&gt;()</c>,
    /// returns silently when there is none or the kerbal is ragdolled, and otherwise calls
    /// <c>KerbalEVA.AddFlag()</c> and the flag vessel's <c>Die()</c>, which fires
    /// <c>onVesselWillDestroy</c>, sets <c>state = DEAD</c> and removes a non-active vessel
    /// from <c>FlightGlobals</c> inside the call. Both events carry
    /// <c>unfocusedRange = 2</c>, a range on the right-click MENU only; the method has no
    /// distance check of its own, so the seam bounds the distance itself
    /// (<see cref="MaxReachMeters"/>). Calling the real method is the point: Parsek's
    /// <c>FlagPickUpCapturePatch</c> prefix marks the flag for exactly that call.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=PickUpFlag</c>, no args, FLIGHT only, the dispatcher's
    /// <c>not-eva</c> defer shared with <c>PlantFlag</c> / <c>EvaBoard</c>. The target is the
    /// nearest loaded <c>VesselType.Flag</c> vessel that is not a ghost map vessel.</para>
    ///
    /// <para><b>Completion.</b> TWO-PHASE on the default budget. Each poll waits out a
    /// ragdolled kerbal (stock would ignore the press), presses once, and re-presses at most
    /// <see cref="MaxFires"/> times in all when the flag still stands
    /// <see cref="RefirePolls"/> polls later. OK once the flag vessel is gone and the scene
    /// settled.</para>
    /// </summary>
    internal static class TestCommandPickUpFlag
    {
        internal const string Verb = "PickUpFlag";

        /// <summary>The seam's honesty bound (metres, inclusive) from the kerbal to the flag,
        /// over <c>PickUp</c>'s missing distance check: stock's 2 m menu range plus room for a
        /// kerbal standing beside the flag it just planted.</summary>
        internal const double MaxReachMeters = 5.0;

        /// <summary>Most presses one command makes.</summary>
        internal const int MaxFires = 3;

        /// <summary>Polls a press is given to take the flag down before the next one.</summary>
        internal const int RefirePolls = 30;

        internal const string NotEvaReason = "pickupflag-not-eva";
        internal const string NoFlagReason = "pickupflag-no-flag";
        internal const string NotNearFlagReason = "pickupflag-not-near-flag";
        internal const string ThrewReason = "pickupflag-threw";
        internal const string FlagLostReason = "pickupflag-flag-lost";
        internal const string TimeoutReason = "pickupflag-timeout";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.PICKUPFLAG_REASONS mirrors
        /// it (PickUpFlagSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "pickupflag-not-eva",
            "pickupflag-no-flag",
            "pickupflag-not-near-flag",
        };

        /// <summary>The post-start ERROR vocabulary. hlib.PICKUPFLAG_ERROR_REASONS mirrors it.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "pickupflag-threw",
            "pickupflag-flag-lost",
            "pickupflag-timeout",
        };

        /// <summary>
        /// Choose the flag: the nearest candidate that is a flag, loaded, and not a ghost map
        /// vessel. Returns the chosen index (or -1) and sets <paramref name="reason"/> to
        /// <see cref="NoFlagReason"/> when no candidate qualifies or
        /// <see cref="NotNearFlagReason"/> when the nearest is beyond
        /// <paramref name="reachMeters"/> (inclusive; NaN is never in reach).
        /// </summary>
        internal static int ChooseFlag(IList<FlagPickUpCandidate> candidates, double reachMeters,
            out double nearestMeters, out string reason)
        {
            int best = -1;
            nearestMeters = double.MaxValue;
            for (int i = 0; candidates != null && i < candidates.Count; i++)
            {
                FlagPickUpCandidate c = candidates[i];
                if (!c.IsFlag || c.IsGhostMapVessel || !c.Loaded || c.Pid == 0u) continue;
                if (double.IsNaN(c.DistanceMeters)) continue;
                if (c.DistanceMeters < nearestMeters)
                {
                    nearestMeters = c.DistanceMeters;
                    best = i;
                }
            }
            if (best < 0)
            {
                reason = NoFlagReason;
                return -1;
            }
            if (!(nearestMeters <= reachMeters))
            {
                reason = NotNearFlagReason;
                return -1;
            }
            reason = null;
            return best;
        }

        /// <summary>Convenience overload against <see cref="MaxReachMeters"/>.</summary>
        internal static int ChooseFlag(IList<FlagPickUpCandidate> candidates,
            out double nearestMeters, out string reason)
            => ChooseFlag(candidates, MaxReachMeters, out nearestMeters, out reason);

        /// <summary>The flag vessel no longer stands: the reference reads Unity-null, stock
        /// set it DEAD, or it left the vessel list.</summary>
        internal static bool IsFlagGone(bool referenceAlive, bool stateDead, bool inVesselList)
            => !referenceAlive || stateDead || !inVesselList;

        /// <summary>
        /// One completion poll. Order: a gone flag concludes (OK after our press once the
        /// scene settled, FlagLost before any press); then the budget; then a ragdolled kerbal
        /// waits (stock ignores the press); then the first press, and a re-press once
        /// <see cref="RefirePolls"/> polls passed with the flag still standing, up to
        /// <see cref="MaxFires"/> presses.
        /// </summary>
        internal static FlagPickUpDecision Decide(bool flagGone, int fireCount, int pollsSinceFire,
            bool kerbalRagdoll, bool sceneSettled, double elapsed, double budget)
        {
            if (flagGone)
            {
                if (fireCount <= 0) return FlagPickUpDecision.FlagLost;
                if (sceneSettled) return FlagPickUpDecision.CompleteOk;
                return elapsed >= budget ? FlagPickUpDecision.Timeout : FlagPickUpDecision.KeepWaiting;
            }
            if (elapsed >= budget) return FlagPickUpDecision.Timeout;
            if (kerbalRagdoll) return FlagPickUpDecision.KeepWaiting;
            if (fireCount <= 0) return FlagPickUpDecision.FirePickUp;
            if (fireCount < MaxFires && pollsSinceFire >= RefirePolls) return FlagPickUpDecision.FirePickUp;
            return FlagPickUpDecision.KeepWaiting;
        }

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            string flagName, uint flagPid, string kerbal, double distanceMeters, int fires)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("flag", flagName ?? string.Empty),
                new KeyValuePair<string, string>("flagPid", flagPid.ToString(ic)),
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("dist", distanceMeters.ToString("F2", ic)),
                new KeyValuePair<string, string>("fires", fires.ToString(ic)),
            };
        }

        /// <summary>The grep-stable Info line logged when the target is chosen.</summary>
        internal static string FormatStartLine(string kerbal, string flagName, uint flagPid, double distanceMeters)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "pickupflag start kerbal='" + (kerbal ?? string.Empty) + "'"
                + " flag='" + (flagName ?? string.Empty) + "'"
                + " flagPid=" + flagPid.ToString(ic)
                + " dist=" + distanceMeters.ToString("F2", ic);
        }

        /// <summary>The grep-stable Info line logged on the OK.</summary>
        internal static string FormatCompleteLine(string kerbal, string flagName, uint flagPid, int fires)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "pickupflag complete kerbal='" + (kerbal ?? string.Empty) + "'"
                + " flag='" + (flagName ?? string.Empty) + "'"
                + " flagPid=" + flagPid.ToString(ic)
                + " fires=" + fires.ToString(ic);
        }
    }
}
