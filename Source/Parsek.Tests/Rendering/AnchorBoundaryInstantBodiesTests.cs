using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.Rendering;
using Xunit;
using Tally = Parsek.Rendering.AnchorPropagator.BoundaryInstantBodyTally;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// The per-body tally and value of the walk's "DAG walk boundary-instant bodies:" line
    /// (todo REFLY-ANCHOR-EPSILON-1300KM live proof): the residual of the boundary-instant
    /// seeds split by the body whose frame they were compared in.
    /// </summary>
    public class AnchorBoundaryInstantBodiesTests
    {
        [Fact]
        public void Tally_CountsPerBody_AndKeepsTheLargestResidual()
        {
            var byBody = new Dictionary<string, Tally>();
            AnchorPropagator.TallyBoundaryInstantBody(byBody, "Kerbin", 2.5);
            AnchorPropagator.TallyBoundaryInstantBody(byBody, "Kerbin", 0.75);
            AnchorPropagator.TallyBoundaryInstantBody(byBody, "Mun", 4.0);

            Assert.Equal(2, byBody.Count);
            Assert.Equal(2, byBody["Kerbin"].Resolved);
            Assert.Equal(2.5, byBody["Kerbin"].MaxEpsilonM);
            Assert.Equal(1, byBody["Mun"].Resolved);
            Assert.Equal(4.0, byBody["Mun"].MaxEpsilonM);
        }

        [Fact]
        public void Tally_MissingBodyName_CountsUnderUnknown_AndNullDictionaryIsIgnored()
        {
            var byBody = new Dictionary<string, Tally>();
            AnchorPropagator.TallyBoundaryInstantBody(byBody, null, 1.0);
            AnchorPropagator.TallyBoundaryInstantBody(byBody, string.Empty, 3.0);
            Assert.Single(byBody);
            Assert.Equal(2, byBody["<unknown>"].Resolved);
            Assert.Equal(3.0, byBody["<unknown>"].MaxEpsilonM);

            AnchorPropagator.TallyBoundaryInstantBody(null, "Kerbin", 1.0);
        }

        [Fact]
        public void Format_EmptyOrNull_IsEmpty()
        {
            Assert.Equal(string.Empty, AnchorPropagator.FormatBoundaryInstantBodies(null));
            Assert.Equal(string.Empty, AnchorPropagator.FormatBoundaryInstantBodies(new Dictionary<string, Tally>()));
        }

        [Fact]
        public void Format_SortsBodiesOrdinal_AndPrintsF3()
        {
            var byBody = new Dictionary<string, Tally>();
            AnchorPropagator.TallyBoundaryInstantBody(byBody, "Mun", 0.4);
            AnchorPropagator.TallyBoundaryInstantBody(byBody, "Kerbin", 1.25);
            AnchorPropagator.TallyBoundaryInstantBody(byBody, "Kerbin", 0.5);
            Assert.Equal("Kerbin:resolved=2,maxEpsilonM=1.250;Mun:resolved=1,maxEpsilonM=0.400",
                AnchorPropagator.FormatBoundaryInstantBodies(byBody));
        }

        [Fact]
        public void Format_IsInvariantUnderACommaDecimalCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var byBody = new Dictionary<string, Tally>();
                AnchorPropagator.TallyBoundaryInstantBody(byBody, "Mun", 12345.678);
                Assert.Equal("Mun:resolved=1,maxEpsilonM=12345.678",
                    AnchorPropagator.FormatBoundaryInstantBodies(byBody));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
