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
            if (ParsekGameModeGate.CheckInert("KerbalDismissalPatch.Prefix")) return true; // S9 game-mode gate
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
                    kerbals.IsNamedByCommittedFlight(kerbalName),
                    GatherDismissalFacts(kerbals, CommittedFutureIndexCache.Current, kerbalName),
                    ReservationExplanation.DefaultDateFormatter);
        }

        /// <summary>
        /// The flights and the owner a dismissal refusal names, read from the ledger:
        /// the owner whose seat he covers (active stand-in) or whose chain lists him, that
        /// owner's holding flight and hold end, and the kerbal's own committed flight that
        /// ends last (<c>CommittedFutureIndex.ResolveHoldAssignment</c>).
        /// </summary>
        internal static DismissalFacts GatherDismissalFacts(
            KerbalsModule kerbals, CommittedFutureIndex index, string kerbalName)
        {
            var facts = new DismissalFacts
            {
                OwnerReleaseUT = double.NaN,
                FlightEndUT = double.NaN
            };
            if (kerbals == null || string.IsNullOrEmpty(kerbalName)) return facts;
            facts.StandInOwner = kerbals.FindActiveStandInOwner(kerbalName);
            facts.ChainOwner = kerbals.FindChainOwner(kerbalName);
            string covered = !string.IsNullOrEmpty(facts.StandInOwner) ? facts.StandInOwner : facts.ChainOwner;
            if (!string.IsNullOrEmpty(covered)
                && kerbals.GetReservationKind(covered) == KerbalReservationKind.ReservedActive)
            {
                facts.OwnerReleaseUT = ReleaseUT(kerbals, covered);
                bool openEnded = double.IsPositiveInfinity(facts.OwnerReleaseUT) || double.IsNaN(facts.OwnerReleaseUT);
                facts.OwnerFlightName = index?.ResolveHoldAssignment(covered, openEnded)?.RecordingName;
            }
            var own = index?.ResolveHoldAssignment(kerbalName, false);
            if (own != null)
            {
                facts.FlightName = own.RecordingName;
                facts.FlightEndUT = own.EndUT;
            }
            return facts;
        }

        private static double ReleaseUT(KerbalsModule kerbals, string kerbalName)
        {
            KerbalsModule.KerbalReservation reservation;
            return kerbals.Reservations != null && kerbals.Reservations.TryGetValue(kerbalName, out reservation)
                && reservation != null
                ? reservation.ReservedUntilUT
                : double.PositiveInfinity;
        }

        /// <summary>The display name of the flight on timeline that holds
        /// <paramref name="kerbalName"/>, or null (not held, or the flight has no name).</summary>
        internal static string HoldingFlightName(KerbalsModule kerbals, string kerbalName)
        {
            if (kerbals == null || string.IsNullOrEmpty(kerbalName)) return null;
            if (kerbals.GetReservationKind(kerbalName) != KerbalReservationKind.ReservedActive) return null;
            double release = ReleaseUT(kerbals, kerbalName);
            bool openEnded = double.IsPositiveInfinity(release) || double.IsNaN(release);
            return CommittedFutureIndexCache.Current?.ResolveHoldAssignment(kerbalName, openEnded)?.RecordingName;
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
                ReservationExplanation.DefaultDateFormatter);
            return text.Body;
        }

        /// <summary>
        /// Why a managed kerbal cannot be dismissed, in the Kerbals window's vocabulary and
        /// the participle-first wording, naming the flight and its date when known.
        /// <c>ShouldBlockDismissal</c> refuses four kinds: a RESERVED kerbal (a committed
        /// flight holds him; normally <see cref="DescribeHeldKerbal"/> answers first), a
        /// RETIRED stand-in (he flew a committed flight and his seat went back to its owner),
        /// a chain STAND-IN (active, or displaced by an earlier one), and a returned owner a
        /// committed flight still names.
        /// </summary>
        internal static string DescribeDismissalBlock(
            KerbalReservationKind kind, bool namedByCommittedFlight, DismissalFacts facts,
            System.Func<double, string> formatDate)
        {
            // A returned owner (his hold ended with his recovery) is no longer reserved,
            // retired or a stand-in, but a flight on timeline still names him.
            if (kind == KerbalReservationKind.NotManaged && namedByCommittedFlight)
                return ReservationExplanation.FlownDismissal(facts.FlightName, facts.FlightEndUT, formatDate);
            switch (kind)
            {
                case KerbalReservationKind.ReservedActive:
                    return ReservationExplanation.ReservedByTimeline + ".";
                case KerbalReservationKind.ReservedRetired:
                    return ReservationExplanation.RetiredStandInDismissal(
                        facts.ChainOwner, facts.FlightName, facts.FlightEndUT, formatDate);
                default:
                    if (!string.IsNullOrEmpty(facts.StandInOwner))
                        return ReservationExplanation.StandingIn(
                            facts.StandInOwner, facts.OwnerFlightName, facts.OwnerReleaseUT, formatDate);
                    return ReservationExplanation.KeptAsStandIn(
                        facts.ChainOwner, facts.OwnerFlightName, facts.OwnerReleaseUT, formatDate);
            }
        }

        /// <summary>The refusal with only the active stand-in's owner and his holding flight
        /// known (no dates, no chain owner).</summary>
        internal static string DescribeDismissalBlock(
            KerbalReservationKind kind, bool namedByCommittedFlight, string standInOwner = null,
            string ownerFlightName = null)
        {
            return DescribeDismissalBlock(kind, namedByCommittedFlight, new DismissalFacts
            {
                StandInOwner = standInOwner,
                OwnerFlightName = ownerFlightName,
                OwnerReleaseUT = double.NaN,
                FlightEndUT = double.NaN
            }, null);
        }

        internal static string DescribeDismissalBlock(
            KerbalReservationKind kind, string standInOwner = null, string ownerFlightName = null)
        {
            return DescribeDismissalBlock(kind, false, standInOwner, ownerFlightName);
        }
    }

    /// <summary>
    /// What a dismissal refusal names (<see cref="KerbalDismissalPatch.GatherDismissalFacts"/>).
    /// Unknown names are null and unknown times NaN; each is then left out of the sentence.
    /// </summary>
    internal struct DismissalFacts
    {
        /// <summary>The owner whose seat this kerbal covers now (the active stand-in), or null.</summary>
        internal string StandInOwner;
        /// <summary>The owner whose replacement chain lists this kerbal, or null.</summary>
        internal string ChainOwner;
        /// <summary>The committed flight that holds the covered owner, or null.</summary>
        internal string OwnerFlightName;
        /// <summary>The covered owner's hold end; NaN or +inf when open-ended or unknown.</summary>
        internal double OwnerReleaseUT;
        /// <summary>The kerbal's own committed flight that ends last, or null.</summary>
        internal string FlightName;
        /// <summary>That flight's end, or NaN.</summary>
        internal double FlightEndUT;
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
            if (ParsekGameModeGate.CheckInert("KerbalSackPatch.Prefix")) return true; // S9 game-mode gate
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
            if (ParsekGameModeGate.CheckInert("AstronautComplexDismissPatch.Prefix")) return true; // S9 game-mode gate
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
