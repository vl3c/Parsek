using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    public class RouteEntityTests
    {
        // catches: a default-value drift that makes the in-memory shape ambiguous
        // on construction. Pins every field that the rest of the codebase will
        // sentinel-check.
        [Fact]
        public void DefaultFields()
        {
            var route = new Route();

            Assert.Equal(RouteStatus.Active, route.Status);

            Assert.NotNull(route.Stops);
            Assert.Empty(route.Stops);

            Assert.NotNull(route.RecordingIds);
            Assert.Empty(route.RecordingIds);

            Assert.NotNull(route.SourceRefs);
            Assert.Empty(route.SourceRefs);

            Assert.Equal(-1, route.CurrentSegmentIndex);
            Assert.Equal(-1, route.PendingStopIndex);

            Assert.Null(route.CurrentCycleStartUT);
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.Null(route.PendingDeliveryUT);
        }
    }
}
