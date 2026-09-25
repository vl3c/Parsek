using System.Reflection;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;

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
                    "KerbalRoster.Remove(ProtoCrewMember) not found - patch will not apply");

            return method;
        }

        static bool Prefix(ProtoCrewMember crew)
        {
            if (crew == null) return true;
            return ShouldAllowDismissal(crew.name, "KerbalRoster.Remove");
        }

        /// <summary>
        /// The dismissal click-block, shared by <c>KerbalRoster.Remove</c> (this patch),
        /// <c>KerbalRoster.SackAvailable</c> and the Astronaut Complex dismiss button
        /// (<see cref="KerbalSackPatch"/>, <see cref="AstronautComplexDismissPatch"/>). The
        /// Astronaut Complex greys the same button through the same predicate
        /// (<see cref="DescribeDismissalRefusal"/>). Returns false when refused, after the
        /// log line and the blocked dialog.
        /// </summary>
        internal static bool ShouldAllowDismissal(string kerbalName, string path)
        {
            if (string.IsNullOrEmpty(kerbalName)) return true;

            // Allow Parsek's own cleanup calls
            if (GameStateRecorder.SuppressCrewEvents)
            {
                ParsekLog.Verbose("KerbalDismissal",
                    $"bypass for '{kerbalName}' via {path} - Parsek crew-event suppression active");
                return true;
            }
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("KerbalDismissal",
                    $"bypass for '{kerbalName}' via {path} - action replay in progress");
                return true;
            }

            var kerbals = LedgerOrchestrator.Kerbals;
            string refusal = DescribeDismissalRefusal(kerbals, kerbalName);
            if (refusal == null) return true;

            ParsekLog.Info("KerbalDismissal",
                $"Blocked dismissal of '{kerbalName}' via {path} - managed by Parsek or named by a committed flight");
            // The same Action Blocked dialog its four sibling blocks (hire, contract
            // accept, facility upgrade, tech research) raise, so a refused dismissal
            // stops being the one silent refusal. A kerbal a committed flight holds
            // gets the same explanation the Astronaut Complex tooltip shows.
            CommittedActionDialog.ShowBlocked(
                "Cannot dismiss \"" + kerbalName + "\"",
                refusal,
                "");
            return false;
        }

        /// <summary>
        /// Why dismissing this kerbal is refused, or null when it is allowed. The single
        /// predicate behind the dismissal block and the Astronaut Complex's disabled
        /// dismiss button.
        /// </summary>
        internal static string DescribeDismissalRefusal(KerbalsModule kerbals, string kerbalName)
        {
            if (kerbals == null || string.IsNullOrEmpty(kerbalName)) return null;
            if (!kerbals.ShouldBlockDismissal(kerbalName)) return null;
            return DescribeHeldKerbal(kerbals, kerbalName)
                ?? DescribeDismissalBlock(kerbals.GetReservationKind(kerbalName),
                    kerbals.IsNamedByCommittedFlight(kerbalName));
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
    /// <summary>
    /// The stock Astronaut Complex dismiss button (<c>Xbutton_AvailableCrew</c>) calls
    /// <c>KerbalRoster.SackAvailable</c>, which turns the kerbal back into an applicant and
    /// never reaches <c>KerbalRoster.Remove</c>, so <see cref="KerbalDismissalPatch"/> alone
    /// does not refuse it. This prefix is the data-side backstop with the same predicate.
    /// </summary>
    [HarmonyPatch]
    internal static class KerbalSackPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("KerbalDismissal",
                    "KerbalRoster.SackAvailable(ProtoCrewMember) not found - the Astronaut Complex dismiss backstop will not apply");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(KerbalRoster), "SackAvailable", new[] { typeof(ProtoCrewMember) });
        }

        static bool Prefix(ProtoCrewMember ap)
        {
            if (ap == null) return true;
            return KerbalDismissalPatch.ShouldAllowDismissal(ap.name, "KerbalRoster.SackAvailable");
        }
    }

    /// <summary>
    /// Stock removes the dismissed row from the Available list BEFORE calling
    /// <c>KerbalRoster.SackAvailable</c>, so the refusal runs at the button handler, before
    /// stock mutates the open list (the same reason <see cref="AstronautComplexHireRecruitPatch"/>
    /// exists). The button is normally already disabled by the Astronaut Complex
    /// annotation; this is the click-time half of the same predicate.
    /// </summary>
    [HarmonyPatch]
    internal static class AstronautComplexDismissPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("KerbalDismissal",
                    "AstronautComplex.Xbutton_AvailableCrew(ButtonTypes, CrewListItem) not found - the stock dismiss pre-block will not apply. " +
                    "KerbalRoster.SackAvailable backup patch remains active.");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(AstronautComplex), "Xbutton_AvailableCrew",
                new[] { typeof(CrewListItem.ButtonTypes), typeof(CrewListItem) });
        }

        static bool Prefix(CrewListItem clickItem)
        {
            string name = StockUiAstronautDecoration.SafeName(clickItem);
            if (string.IsNullOrEmpty(name))
            {
                ParsekLog.Verbose("KerbalDismissal",
                    "AstronautComplex dismiss pre-block bypass - the clicked row names no kerbal");
                return true;
            }
            return KerbalDismissalPatch.ShouldAllowDismissal(name, "AstronautComplex.Xbutton_AvailableCrew");
        }
    }
}
