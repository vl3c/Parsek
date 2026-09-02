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
                + "outcome=no-external-coupling partnerPid=4242 bound=False",
                line);
        }

        [Fact]
        public void ProbeLine_BoundTokenIsAppended_SoThePreP12PrefixIsUnchanged()
        {
            // P12: proofCaptured now means "a PAIR was captured", which no longer implies an
            // origin exists - the undock binds that. The bound= token carries the second half
            // and is APPENDED, so a spec regex only has to grow at the end.
            string line = RouteDockCaptureMath.FormatOriginProofProbeLine(
                0, true, 1, "captured", 4242u, bound: true);
            Assert.Equal(
                "OriginProofProbe: externalParentParts=0 proofCaptured=True situation=1 "
                + "outcome=captured partnerPid=4242 bound=True",
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
        //  FormatStartDockedOriginLine
        // -------------------------------------------------------------

        [Fact]
        public void StartDockedOriginLine_HasTheFixedFieldOrderTheSpecPins()
        {
            string line = RouteDockCaptureMath.FormatStartDockedOriginLine(
                "start-docked", "routedock-start-docked-abcd1234", true, 90210u,
                "Kerbin", true, 1, 1, "rec=r1;delivered=20.00");
            Assert.Equal(
                "StartDockedOrigin: cell=start-docked run=routedock-start-docked-abcd1234 "
                + "proofCaptured=True originPid=90210 body=Kerbin surface=1 situation=1 "
                + "windows=1 detail=rec=r1;delivered=20.00",
                line);
        }

        [Fact]
        public void StartDockedOriginLine_NegativeControlShapeStillCarriesEveryField()
        {
            // FAILS IF: the negative control's line stops being distinguishable from the
            // subject's. Both cells emit this shape; only the VALUES differ, which is why
            // the lane pins `proofCaptured=` per cell rather than pinning the line's
            // presence.
            string line = RouteDockCaptureMath.FormatStartDockedOriginLine(
                "undocked-before-start", "routedock-undocked-before-start-0000ffff", false,
                0u, null, false, 1, 0, "outcome=no-external-coupling");
            Assert.Contains("cell=undocked-before-start", line);
            Assert.Contains("proofCaptured=False", line);
            Assert.Contains("originPid=0", line);
            Assert.Contains("body=<none>", line);
            Assert.Contains("surface=0", line);
            Assert.Contains("windows=0", line);
        }

        [Fact]
        public void StartDockedOriginLine_EmptyRunAndDetailAreNamedRatherThanBlank()
        {
            string line = RouteDockCaptureMath.FormatStartDockedOriginLine(
                "c", null, false, 0u, "", false, -1, 0, "");
            Assert.Contains("run=<none>", line);
            Assert.Contains("body=<none>", line);
            Assert.Contains("detail=<none>", line);
            Assert.Contains("situation=-1", line);
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
