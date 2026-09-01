using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="RouteDockCaptureMath"/>, the pure core of the
    /// self-provisioning <c>RouteDockCapture</c> in-game category (the roadmap's
    /// Tier B supply-route subjects). No Unity or KSP types, so it runs headlessly.
    /// </summary>
    public class RouteDockCaptureMathTests
    {
        // -------------------------------------------------------------
        //  ResolveTransferAmount
        // -------------------------------------------------------------

        [Fact]
        public void Transfer_WhenBothSidesAllowIt_MovesTheRequestedAmount()
        {
            Assert.Equal(20.0, RouteDockCaptureMath.ResolveTransferAmount(100.0, 180.0, 20.0));
        }

        [Fact]
        public void Transfer_ClampsToWhatTheSourceHolds()
        {
            Assert.Equal(7.5, RouteDockCaptureMath.ResolveTransferAmount(7.5, 180.0, 20.0));
        }

        [Fact]
        public void Transfer_ClampsToTheDestinationFreeCapacity()
        {
            Assert.Equal(3.0, RouteDockCaptureMath.ResolveTransferAmount(100.0, 3.0, 20.0));
        }

        [Fact]
        public void Transfer_EmptySourceMovesNothing()
        {
            Assert.Equal(0.0, RouteDockCaptureMath.ResolveTransferAmount(0.0, 180.0, 20.0));
        }

        [Fact]
        public void Transfer_FullDestinationMovesNothing()
        {
            Assert.Equal(0.0, RouteDockCaptureMath.ResolveTransferAmount(100.0, 0.0, 20.0));
        }

        [Theory]
        [InlineData(double.NaN, 10.0, 5.0)]
        [InlineData(10.0, double.NaN, 5.0)]
        [InlineData(10.0, 10.0, double.NaN)]
        [InlineData(-5.0, 10.0, 5.0)]
        [InlineData(10.0, -5.0, 5.0)]
        [InlineData(10.0, 10.0, -5.0)]
        public void Transfer_DegenerateInputsClampToZero(double source, double free, double requested)
        {
            // A negative "transfer" would inflate one side of the window's
            // corner-difference manifest, which is the exact failure this
            // clamp exists to make unrepresentable.
            Assert.Equal(0.0, RouteDockCaptureMath.ResolveTransferAmount(source, free, requested));
        }

        // -------------------------------------------------------------
        //  IsExternallyParentedPart - the production predicate mirror
        // -------------------------------------------------------------

        [Fact]
        public void ExternalParent_TrueOnlyWhenTheParentVesselIsAnotherVessel()
        {
            Assert.True(RouteDockCaptureMath.IsExternallyParentedPart(true, true, false));
        }

        [Theory]
        // no parent at all (a vessel root)
        [InlineData(false, false, false)]
        // parent exists but its vessel does not resolve
        [InlineData(true, false, false)]
        // parent's vessel IS the active vessel - what a SETTLED couple leaves,
        // because Part.Couple ends in SetVessel over the whole absorbed subtree
        [InlineData(true, true, true)]
        public void ExternalParent_FalseOnEveryOtherShape(
            bool hasParent, bool parentVesselResolves, bool parentVesselIsSelf)
        {
            Assert.False(RouteDockCaptureMath.IsExternallyParentedPart(
                hasParent, parentVesselResolves, parentVesselIsSelf));
        }

        // -------------------------------------------------------------
        //  FormatOriginProofProbeLine
        // -------------------------------------------------------------

        [Fact]
        public void ProbeLine_CarriesEveryMeasuredFactInAStableOrder()
        {
            string line = RouteDockCaptureMath.FormatOriginProofProbeLine(
                0, false, 1, "no-external-coupling", 4242u);
            Assert.Equal(
                "OriginProofProbe: externalParentParts=0 proofCaptured=False situation=1 "
                + "outcome=no-external-coupling partnerPid=4242",
                line);
        }

        [Fact]
        public void ProbeLine_CapturedBranchReadsTrue()
        {
            string line = RouteDockCaptureMath.FormatOriginProofProbeLine(
                3, true, 2, "captured", 7u);
            Assert.Contains("externalParentParts=3", line);
            Assert.Contains("proofCaptured=True", line);
            Assert.Contains("outcome=captured", line);
        }

        [Fact]
        public void ProbeLine_MissingOutcomeStillFormats()
        {
            Assert.Contains("outcome=<none>",
                RouteDockCaptureMath.FormatOriginProofProbeLine(0, false, -1, null, 0u));
        }

        // -------------------------------------------------------------
        //  FormatPassLine
        // -------------------------------------------------------------

        [Fact]
        public void PassLine_HasTheFixedFieldOrderTheSpecPins()
        {
            string line = RouteDockCaptureMath.FormatPassLine(
                "delivery", "routedock-delivery-abcd1234", 1,
                RouteConnectionKind.DockingPort, true, 1, 1, 0, 0, "lfMoved=20.00");
            Assert.Equal(
                "DockCapture PASS: cell=delivery run=routedock-delivery-abcd1234 windows=1 "
                + "kind=DockingPort complete=True deliveryResources=1 deliveryInventory=1 "
                + "pickupResources=0 pickupInventory=0 detail=lfMoved=20.00",
                line);
        }

        [Fact]
        public void PassLine_EmptyDetailIsNamedRatherThanBlank()
        {
            Assert.Contains("detail=<none>",
                RouteDockCaptureMath.FormatPassLine(
                    "drift", "r", 1, RouteConnectionKind.DockingPort, true, 0, 0, 0, 0, null));
        }

        // -------------------------------------------------------------
        //  ClassifyDriftObservation
        // -------------------------------------------------------------

        [Fact]
        public void Drift_ContractHeldWhenWarnFiredWindowClosedAndPartUnmanifested()
        {
            Assert.Equal(
                "drift-warned-window-complete-part-unmanifested",
                RouteDockCaptureMath.ClassifyDriftObservation(true, true, false));
        }

        [Fact]
        public void Drift_IncompleteWindowIsNamedFirst()
        {
            // Ordering matters: an incomplete window means the disjoint-set
            // verifier refused the split, which is a different (and more
            // serious) finding than a missing warning.
            Assert.Equal(
                "window-did-not-complete",
                RouteDockCaptureMath.ClassifyDriftObservation(true, false, false));
        }

        [Fact]
        public void Drift_MissingWarningIsNamed()
        {
            Assert.Equal(
                "no-drift-warning",
                RouteDockCaptureMath.ClassifyDriftObservation(false, true, false));
        }

        [Fact]
        public void Drift_PartLeakingIntoAManifestIsNamed()
        {
            Assert.Equal(
                "moved-part-in-manifest",
                RouteDockCaptureMath.ClassifyDriftObservation(true, true, true));
        }
    }
}
