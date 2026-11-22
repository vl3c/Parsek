namespace Parsek.Tests
{
    /// <summary>
    /// Shared fixture values for the periapsis floor (<see cref="OrbitClearance"/>).
    /// </summary>
    internal static class OrbitClearanceTestValues
    {
        /// <summary>
        /// Illustrative Kerbin deletion altitude (metres): roughly where Kerbin's pressure falls
        /// to stock's 1 kPa packed-vessel deletion threshold. The live value is bisected from
        /// the body's pressure curve; headless cells state this fixture value instead.
        /// </summary>
        internal const double KerbinDeletionAltitude = 22000.0;
    }
}
