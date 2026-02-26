namespace Parsek
{
    /// <summary>
    /// A segment of on-rails (Keplerian) orbit captured during recording.
    /// Replaces thousands of sampled TrajectoryPoints with ~8 orbital parameters.
    /// </summary>
    public struct OrbitSegment
    {
        public double startUT, endUT;
        public double inclination, eccentricity, semiMajorAxis;
        public double longitudeOfAscendingNode, argumentOfPeriapsis;
        public double meanAnomalyAtEpoch, epoch;
        public string bodyName;

        public override string ToString()
        {
            return $"UT={startUT:F1}-{endUT:F1} body={bodyName ?? "?"} inc={inclination:F2} " +
                   $"ecc={eccentricity:F4} sma={semiMajorAxis:F1}";
        }
    }
}
