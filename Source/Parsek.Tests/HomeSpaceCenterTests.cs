using Xunit;

namespace Parsek.Tests
{
    public class HomeSpaceCenterTests
    {
        [Fact]
        public void Current_IsStock()
        {
            Assert.Same(HomeSpaceCenter.Stock, HomeSpaceCenter.Current);
        }

        [Fact]
        public void Stock_VersionIsZero()
        {
            Assert.Equal(0, HomeSpaceCenter.Stock.Version);
        }

        [Fact]
        public void Stock_FieldsEqualTheStockConstants()
        {
            HomeSpaceCenter stock = HomeSpaceCenter.Stock;
            Assert.Equal(HomeSpaceCenter.StockPadLatitude, stock.PadLatitude);
            Assert.Equal(HomeSpaceCenter.StockPadLongitude, stock.PadLongitude);
            Assert.Equal(HomeSpaceCenter.StockRunwayThresholdLatitude, stock.RunwayThresholdLatitude);
            Assert.Equal(HomeSpaceCenter.StockRunwayThresholdLongitude, stock.RunwayThresholdLongitude);
        }

        [Fact]
        public void Stock_FieldsEqualTheSpawnCollisionDetectorConstants()
        {
            HomeSpaceCenter stock = HomeSpaceCenter.Stock;
            Assert.Equal(SpawnCollisionDetector.KscPadLatitude, stock.PadLatitude);
            Assert.Equal(SpawnCollisionDetector.KscPadLongitude, stock.PadLongitude);
            Assert.Equal(SpawnCollisionDetector.KscRunwayLatitude, stock.RunwayThresholdLatitude);
            Assert.Equal(SpawnCollisionDetector.KscRunwayLongitude, stock.RunwayThresholdLongitude);
        }

        [Fact]
        public void Stock_ValuesAreTheStockKscPositions()
        {
            // The literal stock positions, so a change to the constants themselves reds here
            // and not only in the equality cells above.
            Assert.Equal(-0.0972, HomeSpaceCenter.StockPadLatitude);
            Assert.Equal(-74.5575, HomeSpaceCenter.StockPadLongitude);
            Assert.Equal(-0.0502, HomeSpaceCenter.StockRunwayThresholdLatitude);
            Assert.Equal(-74.7300, HomeSpaceCenter.StockRunwayThresholdLongitude);
        }

        [Fact]
        public void ClassifyKscExclusionZone_ReadsTheHomeCenterPositions()
        {
            const double kerbinRadius = 600000.0;
            HomeSpaceCenter home = HomeSpaceCenter.Current;
            Assert.Equal(KscExclusionZone.Pad,
                SpawnCollisionDetector.ClassifyKscExclusionZone(
                    home.PadLatitude, home.PadLongitude, kerbinRadius,
                    SpawnCollisionDetector.DefaultKscExclusionRadiusMeters));
            Assert.Equal(KscExclusionZone.Runway,
                SpawnCollisionDetector.ClassifyKscExclusionZone(
                    home.RunwayThresholdLatitude, home.RunwayThresholdLongitude, kerbinRadius,
                    SpawnCollisionDetector.DefaultKscExclusionRadiusMeters));
        }
    }
}
