using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents dismissal of Parsek-managed kerbals (reserved, stand-ins, retired)
    /// from the Astronaut Complex. Uses TargetMethod to resolve the ambiguous
    /// KerbalRoster.Remove overloads (same pattern as GhostVesselSwitchPatch).
    /// </summary>
    [HarmonyPatch]
    internal static class KerbalDismissalPatch
    {
        static MethodBase TargetMethod()
        {
            var method = typeof(KerbalRoster).GetMethod(
                nameof(KerbalRoster.Remove),
                new[] { typeof(ProtoCrewMember) });

            if (method == null)
                ParsekLog.Warn("KerbalDismissal",
                    "KerbalRoster.Remove(ProtoCrewMember) not found — patch will not apply");

            return method;
        }

        static bool Prefix(ProtoCrewMember crew)
        {
            if (crew == null) return true;

            // Allow Parsek's own cleanup calls
            if (GameStateRecorder.SuppressCrewEvents) return true;
            if (GameStateRecorder.IsReplayingActions) return true;

            var kerbals = LedgerOrchestrator.Kerbals;
            if (kerbals?.ShouldBlockDismissal(crew.name) ?? false)
            {
                ParsekLog.Info("KerbalDismissal",
                    $"Blocked dismissal of '{crew.name}' - managed by Parsek or named by a committed flight");
                // The same Action Blocked dialog its four sibling blocks (hire, contract
                // accept, facility upgrade, tech research) raise, so a refused dismissal
                // stops being the one silent refusal. A kerbal a committed flight holds
                // gets the same explanation the Astronaut Complex hover shows.
                string heldReason = DescribeHeldKerbal(kerbals, crew.name);
                CommittedActionDialog.ShowBlocked(
                    "Cannot dismiss \"" + crew.name + "\"",
                    heldReason ?? DescribeDismissalBlock(kerbals.GetReservationKind(crew.name),
                        kerbals.IsNamedByCommittedFlight(crew.name)),
                    "");
                return false;
            }
            return true;
        }

        /// <summary>
        /// The reservation explanation (<see cref="ReservationExplanation.KerbalOnFlight"/>
        /// or <see cref="ReservationExplanation.KerbalLost"/>) for a kerbal a committed
        /// flight holds, or null for every other managed kind.
        /// </summary>
        internal static string DescribeHeldKerbal(KerbalsModule kerbals, string kerbalName)
        {
            if (kerbals == null || string.IsNullOrEmpty(kerbalName)) return null;
            if (kerbals.GetReservationKind(kerbalName) != KerbalReservationKind.ReservedActive)
                return null;
            var context = StockUiOverlayController.BuildLiveAstronautContext(null);
            var text = StockUiReservationPredicates.ExplainKerbalReservation(
                CommittedFutureIndexCache.Current,
                kerbalName,
                context.Reservation(kerbalName),
                context.SlotOwner(kerbalName),
                context.IsLoopingRecording,
                ReservationExplanation.DefaultDateFormatter);
            return text.Body;
        }

        /// <summary>
        /// Why a managed kerbal cannot be dismissed, in the Kerbals window's vocabulary.
        /// <c>IsManaged</c> is true for three kinds: a RESERVED kerbal (a committed flight
        /// holds him), a RETIRED stand-in (he flew a committed flight and his seat went
        /// back to its owner), and an active STAND-IN (neither of the above, but a slot
        /// chain lists him, so he is covering someone's seat).
        /// </summary>
        internal static string DescribeDismissalBlock(
            KerbalReservationKind kind, bool namedByCommittedFlight)
        {
            // A returned owner (his hold ended with his recovery) is no longer reserved,
            // retired or a stand-in, but a committed flight still names him.
            if (kind == KerbalReservationKind.NotManaged && namedByCommittedFlight)
                return "This kerbal flew a committed flight on your timeline.";
            return DescribeDismissalBlock(kind);
        }

        internal static string DescribeDismissalBlock(KerbalReservationKind kind)
        {
            switch (kind)
            {
                case KerbalReservationKind.ReservedActive:
                    return "This kerbal is reserved by a committed flight on your timeline.";
                case KerbalReservationKind.ReservedRetired:
                    return "This retired stand-in flew a committed flight on your timeline.";
                default:
                    return "This kerbal is a stand-in in a reserved kerbal's replacement chain.";
            }
        }
    }
}
