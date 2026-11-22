using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The player words of a mission profile (docs/dev/design-mission-profile.md sections 2.3,
    /// 3.1, 3.2, 4.4): the labels of the analyzer's kinds and the number formats of the Profile
    /// window's cells. Pure, culture-free (InvariantCulture throughout; a unit test reads every
    /// string here).
    /// </summary>
    internal static class ProfileLabels
    {
        internal const string WindowTitlePrefix = "Parsek - Profile";
        internal const string EmptyText = "This mission has no recorded flight.";
        internal const string Missing = "-";
        internal const string EstimateMark = "~";
        internal const string Arrow = " -> ";
        // The in-game cell prints the degree sign as stock KSP does.
        internal const string Degree = "\u00B0";

        private static CultureInfo Ic => CultureInfo.InvariantCulture;

        /// <summary>"Parsek - Profile: Mun Lander", or the bare prefix with no name.</summary>
        internal static string WindowTitle(string missionName)
            => string.IsNullOrEmpty(missionName) ? WindowTitlePrefix : WindowTitlePrefix + ": " + missionName;

        // ------------------------------------------------------------------ phases

        internal static string PhaseLabel(ProfilePhase p, string launchBody, string partner)
        {
            switch (p.Kind)
            {
                case ProfilePhaseKind.Prelaunch: return "Prelaunch";
                case ProfilePhaseKind.Ascent: return "Ascent";
                case ProfilePhaseKind.Circularization: return "Circularization";
                case ProfilePhaseKind.Injection: return "Injection";
                case ProfilePhaseKind.Orbit: return OrbitName(p.Body);
                case ProfilePhaseKind.Transfer: return TransferLabel(p, launchBody);
                case ProfilePhaseKind.Flyby: return (p.Body ?? "Body") + " flyby";
                case ProfilePhaseKind.Rendezvous:
                    return string.IsNullOrEmpty(partner) ? "Rendezvous" : "Rendezvous with " + partner;
                case ProfilePhaseKind.Docked: return DockedLabel(p, partner);
                case ProfilePhaseKind.Deorbit: return "Deorbit";
                case ProfilePhaseKind.Descent: return "Descent";
                case ProfilePhaseKind.Reentry: return "Reentry";
                case ProfilePhaseKind.Aerocapture: return "Aerocapture";
                case ProfilePhaseKind.Aerobraking: return "Aerobraking";
                case ProfilePhaseKind.AtmosphericFlyby: return "Atmospheric flyby";
                case ProfilePhaseKind.AtmospherePass: return "Atmosphere pass";
                case ProfilePhaseKind.SurfaceStay: return "On the surface";
                case ProfilePhaseKind.Drive: return "Drive";
                case ProfilePhaseKind.Flight: return "Flight";
                case ProfilePhaseKind.SuborbitalHop: return "Suborbital flight";
                case ProfilePhaseKind.Eva: return "EVA";
                case ProfilePhaseKind.EvaSurface: return "On the surface (EVA)";
                case ProfilePhaseKind.JetpackFlight: return "Jetpack flight";
                default: return "In space";
            }
        }

        internal static string OrbitName(string body) => string.IsNullOrEmpty(body) ? "Orbit" : body + " orbit";

        private static string TransferLabel(ProfilePhase p, string launchBody)
        {
            string target = p.TargetBody;
            if (string.IsNullOrEmpty(target)) return "Transfer";
            return string.Equals(target, launchBody, StringComparison.Ordinal)
                ? "Return to " + target : "Transfer to " + target;
        }

        private static string DockedLabel(ProfilePhase p, string partner)
        {
            if (string.IsNullOrEmpty(partner)) return "Docked";
            bool ownAbsorbed = p.Anchor != null && p.Anchor.OwnShipAbsorbed;
            return ownAbsorbed ? "Docked to " + partner : "Docked: " + partner + " aboard";
        }

        // ------------------------------------------------------------------ burns and events

        internal static string BurnLabel(ProfileBurnKind kind, int merged)
        {
            switch (kind)
            {
                case ProfileBurnKind.Circularization: return "Circularization burn";
                case ProfileBurnKind.Injection: return "Injection burn";
                case ProfileBurnKind.Transfer: return "Transfer burn";
                case ProfileBurnKind.Return: return "Return burn";
                case ProfileBurnKind.Escape: return "Escape burn";
                case ProfileBurnKind.Correction: return "Correction";
                case ProfileBurnKind.Capture: return "Capture burn";
                case ProfileBurnKind.CaptureAerobraking: return "Capture burn (aerobraking orbit)";
                case ProfileBurnKind.PeriapsisRaise: return "Periapsis raise";
                case ProfileBurnKind.OrbitChange: return "Orbit change";
                case ProfileBurnKind.PlaneChange: return "Plane change";
                case ProfileBurnKind.Phasing: return "Phasing burn";
                case ProfileBurnKind.Approach:
                    return merged > 1 ? "Approach burns (" + merged.ToString(Ic) + ")" : "Approach burn";
                case ProfileBurnKind.Deorbit: return "Deorbit burn";
                case ProfileBurnKind.Descent: return "Descent burn";
                case ProfileBurnKind.Landing: return "Landing burn";
                default: return "Burn";
            }
        }

        /// <summary>The label of one event row. <paramref name="name"/> is the vessel, kerbal or
        /// partner text the line builder resolved for it (null when none).</summary>
        internal static string EventLabel(ProfileEvent e, string name, string endBody)
        {
            switch (e.Kind)
            {
                case ProfileEventKind.Launch: return "Launch";
                case ProfileEventKind.Liftoff: return "Liftoff";
                case ProfileEventKind.StageSeparation: return "Stage separation (" + Pieces(e.Count) + ")";
                case ProfileEventKind.Separation: return Named("Separated: ", name, "Separation");
                case ProfileEventKind.Breakup:
                    return "Breakup (" + Pieces(e.Count) + ", " + Count(e.Anchor?.Vessels ?? 0, "vessel") + ")";
                case ProfileEventKind.Burn: return BurnLabel(e.BurnKind, e.MergedBurns);
                case ProfileEventKind.AtmosphereExit: return "Atmosphere exit";
                case ProfileEventKind.AtmosphereEntry: return "Atmosphere entry";
                case ProfileEventKind.SoiEntry: return "Entered " + (e.Body ?? "an") + " SOI";
                case ProfileEventKind.SoiExit: return "Left " + (e.Body ?? "the") + " SOI";
                case ProfileEventKind.Docking: return Named("Docked: ", name, "Docked");
                case ProfileEventKind.Undocking: return Named("Undocked: ", name, "Undocked");
                case ProfileEventKind.SwitchedTo: return Named("Switched to ", name, "Switched");
                case ProfileEventKind.Eva: return EvaLabel(e, name);
                case ProfileEventKind.Board: return Named("Boarded: ", name, "Boarded");
                case ProfileEventKind.PartPlaced: return Named("Part placed: ", name, "Part placed");
                case ProfileEventKind.InventoryTransfer: return "Inventory transfer";
                case ProfileEventKind.ParachutesDeployed: return "Parachutes deployed";
                case ProfileEventKind.Touchdown: return "Touchdown";
                case ProfileEventKind.Splashdown: return "Splashdown";
                case ProfileEventKind.Skip: return "Skip";
                case ProfileEventKind.Pass: return "Pass " + Math.Max(1, e.Count).ToString(Ic);
                case ProfileEventKind.Reflown: return "Flown again from here";
                case ProfileEventKind.Recovered: return "Recovered";
                default: return EndLabel(e.Anchor?.Terminal, endBody);
            }
        }

        private static string EvaLabel(ProfileEvent e, string name)
        {
            string head = Named("EVA: ", name, "EVA");
            if (e.Anchor == null || !e.Anchor.Collapsed || double.IsNaN(e.Anchor.EndUT)) return head;
            return head + " (" + Duration(e.Anchor.EndUT - e.UT) + "), boarded";
        }

        private static string Named(string head, string name, string bare)
            => string.IsNullOrEmpty(name) ? bare : head + name;

        /// <summary>"End: Orbiting Kerbin", "End: Landed", "End: Destroyed" (the Log's words, the
        /// body added to an orbit).</summary>
        internal static string EndLabel(TerminalState? terminal, string body)
        {
            string label = MissionStructureListBuilder.FormatEndLabel(terminal);
            if ((terminal == TerminalState.Orbiting || terminal == TerminalState.SubOrbital) && !string.IsNullOrEmpty(body))
                return label + " " + body;
            return label;
        }

        /// <summary>The outcome a line header shows: "Recovered", "Landed, Mun", "Orbiting Kerbin".</summary>
        internal static string Outcome(TerminalState? terminal, string body)
        {
            string word = MissionCompositionBuilder.TerminalName(terminal);
            if (string.IsNullOrEmpty(word)) return "";
            switch (terminal)
            {
                case TerminalState.Orbiting:
                case TerminalState.SubOrbital:
                    return string.IsNullOrEmpty(body) ? word : word + " " + body;
                case TerminalState.Landed:
                case TerminalState.Splashed:
                    return string.IsNullOrEmpty(body) ? word : word + ", " + body;
                default:
                    return word;
            }
        }

        internal static string Pieces(int n) => Count(n, "piece");

        private static string Count(int n, string unit)
            => n.ToString(Ic) + " " + unit + (n == 1 ? "" : "s");

        // ------------------------------------------------------------------ numbers

        /// <summary>km above 10 km, m below, whole numbers with thousands separators.</summary>
        internal static string Distance(double meters)
        {
            if (double.IsNaN(meters) || double.IsInfinity(meters)) return Missing;
            if (Math.Abs(meters) >= 10000) return Math.Round(meters / 1000.0).ToString("N0", Ic) + " km";
            return Math.Round(meters).ToString("N0", Ic) + " m";
        }

        /// <summary>A whole number of m/s with thousands separators.</summary>
        internal static string DeltaV(double mps)
            => double.IsNaN(mps) ? Missing : Math.Round(mps).ToString("N0", Ic);

        /// <summary>"18s", "3m 52s", "2h 26m", "4d 2h" (KSP 6-hour days), floored.</summary>
        internal static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return Missing;
            long s = (long)Math.Floor(Math.Max(0, seconds));
            if (s < 60) return s.ToString(Ic) + "s";
            if (s < 3600) return (s / 60).ToString(Ic) + "m " + (s % 60).ToString(Ic) + "s";
            const long day = 6 * 3600;
            if (s < day) return (s / 3600).ToString(Ic) + "h " + ((s / 60) % 60).ToString(Ic) + "m";
            return (s / day).ToString(Ic) + "d " + ((s % day) / 3600).ToString(Ic) + "h";
        }

        internal static string Inclination(double deg)
            => double.IsNaN(deg) ? "" : deg.ToString("0.0", Ic) + Degree;

        /// <summary>"83 x 77 km, 0.1 deg" for a closed orbit (Ap x Pe above the surface).</summary>
        internal static string OrbitShape(ProfileOrbit o)
        {
            string shape = DistanceNoUnit(o.ApoapsisAltitude, o.PeriapsisAltitude);
            string inc = Inclination(o.InclinationDeg);
            return string.IsNullOrEmpty(inc) ? shape : shape + ", " + inc;
        }

        // "83 x 77 km": both in the unit of the larger.
        private static string DistanceNoUnit(double ap, double pe)
        {
            bool km = Math.Max(Math.Abs(ap), Math.Abs(pe)) >= 10000;
            double div = km ? 1000.0 : 1.0;
            string unit = km ? " km" : " m";
            return Math.Round(ap / div).ToString("N0", Ic) + " x " + Math.Round(pe / div).ToString("N0", Ic) + unit;
        }

        /// <summary>"Ap 11,524 km, Pe 82 km" (an orbit drawn by its ends).</summary>
        internal static string ApPe(ProfileOrbit o)
            => "Ap " + Distance(o.ApoapsisAltitude) + ", Pe " + Distance(o.PeriapsisAltitude);

        /// <summary>"about 140 km" for an orbit inferred from a held altitude.</summary>
        internal static string About(double altitude) => "about " + Distance(altitude);

        /// <summary>The Orbit / result cell for the orbit a burn produced or a coast holds.
        /// <paramref name="encounterPe"/> is the periapsis around the encountered body when one
        /// is stored (NaN otherwise).</summary>
        internal static string OrbitResult(ProfileOrbit o, double encounterPe)
        {
            if (!o.IsKnown) return Missing;
            if (o.Tier == ProfileEvidenceTier.Inferred) return About(o.HeldAltitude);
            if (o.EncounterIsExit && o.HasElements)
                return "escape, Pe " + Distance(o.PeriapsisAltitude);
            if (!string.IsNullOrEmpty(o.Encounter) && o.DirectEncounter && !o.EncounterIsExit)
                return o.Encounter + " encounter" + (double.IsNaN(encounterPe) ? "" : ", Pe " + Distance(encounterPe));
            if (!o.HasElements)
            {
                if (o.Kind == ProfileOrbitKind.Falling) return "falling";
                if (o.Kind == ProfileOrbitKind.Leaving) return "leaving";
                return Missing;
            }
            if (o.Energy == ProfileOrbitEnergy.Escaping && double.IsInfinity(o.ApoapsisAltitude))
                return "escape, Pe " + Distance(o.PeriapsisAltitude);
            if (o.Kind == ProfileOrbitKind.InOrbit) return OrbitShape(o);
            return ApPe(o);
        }
    }
}
