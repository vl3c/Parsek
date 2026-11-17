namespace Parsek
{
    /// <summary>
    /// The home space center's launch pad and runway threshold positions on the home world
    /// (degrees), the centres of the KSC end-of-flight exclusion circles
    /// (<c>SpawnCollisionDetector.ClassifyKscExclusionZone</c>).
    ///
    /// Immutable. <see cref="Current"/> is always <see cref="Stock"/>, the stock Kerbin KSC;
    /// <see cref="Version"/> is 0 for it. A reader that caches anything derived from the
    /// position keys the cache on <see cref="Version"/>, so a later non-stock home center can
    /// invalidate it.
    ///
    /// The position values and <see cref="Version"/> are readonly fields rather than
    /// properties, and <see cref="Stock"/> is built by the type initializer, so the type adds
    /// no accessor that only a test reaches.
    /// </summary>
    internal sealed class HomeSpaceCenter
    {
        /// <summary>Stock KSC launch pad latitude in degrees.</summary>
        internal const double StockPadLatitude = -0.0972;

        /// <summary>Stock KSC launch pad longitude in degrees.</summary>
        internal const double StockPadLongitude = -74.5575;

        /// <summary>Stock KSC runway start (west threshold) latitude in degrees.</summary>
        internal const double StockRunwayThresholdLatitude = -0.0502;

        /// <summary>Stock KSC runway start (west threshold) longitude in degrees.</summary>
        internal const double StockRunwayThresholdLongitude = -74.7300;

        /// <summary>The stock Kerbin KSC, version 0.</summary>
        internal static readonly HomeSpaceCenter Stock = new HomeSpaceCenter(
            StockPadLatitude, StockPadLongitude,
            StockRunwayThresholdLatitude, StockRunwayThresholdLongitude,
            0);

        /// <summary>The home space center in effect. Always <see cref="Stock"/>.</summary>
        internal static HomeSpaceCenter Current => Stock;

        /// <summary>Launch pad latitude in degrees.</summary>
        internal readonly double PadLatitude;

        /// <summary>Launch pad longitude in degrees.</summary>
        internal readonly double PadLongitude;

        /// <summary>Runway start (west threshold) latitude in degrees.</summary>
        internal readonly double RunwayThresholdLatitude;

        /// <summary>Runway start (west threshold) longitude in degrees.</summary>
        internal readonly double RunwayThresholdLongitude;

        /// <summary>Identity of this position set for cache keys; 0 for <see cref="Stock"/>.</summary>
        internal readonly int Version;

        private HomeSpaceCenter(
            double padLatitude, double padLongitude,
            double runwayThresholdLatitude, double runwayThresholdLongitude,
            int version)
        {
            PadLatitude = padLatitude;
            PadLongitude = padLongitude;
            RunwayThresholdLatitude = runwayThresholdLatitude;
            RunwayThresholdLongitude = runwayThresholdLongitude;
            Version = version;
        }
    }
}
