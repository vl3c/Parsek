namespace Parsek
{
    internal static class SegmentPhaseClassifier
    {
        internal static bool TryClassify(Vessel vessel, out string phase, out string bodyName)
        {
            phase = null;
            bodyName = null;

            if (vessel == null || vessel.mainBody == null)
                return false;

            bodyName = vessel.mainBody.name;
            double approachAltitude = FlightRecorder.ComputeApproachAltitude(vessel.mainBody);
            phase = ClassifyFromValues(
                vessel.situation,
                vessel.mainBody.atmosphere,
                vessel.altitude,
                vessel.mainBody.atmosphereDepth,
                approachAltitude);
            return true;
        }

        internal static string ClassifyFromValues(
            Vessel.Situations situation,
            bool hasAtmosphere,
            double altitude,
            double atmosphereDepth,
            double approachAltitude)
        {
            if (situation == Vessel.Situations.LANDED
                || situation == Vessel.Situations.SPLASHED
                || situation == Vessel.Situations.PRELAUNCH)
            {
                return "surface";
            }

            if (hasAtmosphere)
                return altitude < atmosphereDepth ? "atmo" : "exo";

            return altitude < approachAltitude ? "approach" : "exo";
        }

        internal static string EnvironmentToPhase(SegmentEnvironment environment)
        {
            switch (environment)
            {
                case SegmentEnvironment.Atmospheric:
                    return "atmo";
                case SegmentEnvironment.SurfaceMobile:
                case SegmentEnvironment.SurfaceStationary:
                    return "surface";
                case SegmentEnvironment.Approach:
                    return "approach";
                default:
                    return "exo";
            }
        }
    }
}
