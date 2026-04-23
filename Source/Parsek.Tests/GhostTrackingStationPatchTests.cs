using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostTrackingStationPatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostTrackingStationPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void BuildVesselsListFinalizer_KnownGhostMissingRendererNre_Suppresses()
        {
            var exception = new NullReferenceException("orbit renderer click handler");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 4,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: true,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Null(result);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Suppressed known ghost ProtoVessel")
                && line.Contains("ghostMissingOrbitRenderers=1"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_UnrelatedException_ReturnsOriginalAndWarns()
        {
            var exception = new InvalidOperationException("stock list build failed");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 4,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: true,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("InvalidOperationException")
                && line.Contains("stock list build failed"));
            Assert.DoesNotContain(logLines, line => line.Contains("Suppressed known ghost ProtoVessel"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreWithoutGhostMissingRenderer_ReturnsOriginalAndWarns()
        {
            var exception = new NullReferenceException("stock NRE");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 3,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 0,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: false,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x00063] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("ghostMissingOrbitRenderers=0"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreWithEarlierNonGhostMissingRenderer_ReturnsOriginalAndWarns()
        {
            var exception = new NullReferenceException("ambiguous missing renderer");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 5,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 1,
                firstMissingOrbitRendererIsGhost: false,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("nonGhostMissingOrbitRenderers=1"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreWithLaterNonGhostMissingRenderer_Suppresses()
        {
            var exception = new NullReferenceException("ghost missing renderer first");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 5,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 1,
                firstMissingOrbitRendererIsGhost: true,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Null(result);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostMap]")
                && line.Contains("Suppressed known ghost ProtoVessel")
                && line.Contains("ghostMissingOrbitRenderers=1"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreAtDifferentStockOffset_ReturnsOriginalAndWarns()
        {
            var exception = new NullReferenceException("unrelated stock NRE");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 4,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: true,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x00063] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("unrelated stock NRE")
                && line.Contains("firstMissingOrbitRenderer=ghost"));
        }

        [Fact]
        public void BuildVesselsListFinalizer_NreWithPriorStockNullCandidate_ReturnsOriginalAndWarns()
        {
            var exception = new NullReferenceException("earlier stock null");

            Exception result = GhostTrackingBuildVesselsListPatch.FinalizeBuildVesselsListExceptionForTesting(
                exception,
                totalVessels: 4,
                ghostVesselCount: 1,
                ghostMissingOrbitRendererCount: 1,
                nonGhostMissingOrbitRendererCount: 0,
                firstMissingOrbitRendererIsGhost: true,
                potentialEarlierStockNullCandidateCount: 1,
                exceptionStackTrace: "at KSP.UI.Screens.SpaceTracking.buildVesselsList () [0x000b9] in <filename unknown>:0");

            Assert.Same(exception, result);
            Assert.Contains(logLines, line =>
                line.Contains("[WARN][GhostMap]")
                && line.Contains("exception left visible")
                && line.Contains("priorStockNullCandidates=1"));
        }

        [Fact]
        public void TryClearSelectedVessel_WithPrivateSelection_ClearsAndReturnsPrevious()
        {
            var selected = new object();
            var tracking = new FakeTrackingStation(selected);

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string error);

            Assert.True(cleared);
            Assert.Same(selected, previousSelection);
            Assert.Null(tracking.SelectedForTesting);
            Assert.Null(error);
        }

        [Fact]
        public void TryClearSelectedVessel_WithSetVesselMethod_DoesNotCallSetVessel()
        {
            var selected = new object();
            var tracking = new FakeTrackingStationWithSetVessel(selected);

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string error);

            Assert.True(cleared);
            Assert.Same(selected, previousSelection);
            Assert.Null(tracking.SelectedForTesting);
            Assert.False(tracking.SetVesselCalled);
            Assert.Null(error);
        }

        [Fact]
        public void TryClearSelectedVessel_WithNoSelection_ReturnsFalseWithoutError()
        {
            var tracking = new FakeTrackingStation(null);

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string error);

            Assert.False(cleared);
            Assert.Null(previousSelection);
            Assert.Null(tracking.SelectedForTesting);
            Assert.Null(error);
        }

        [Fact]
        public void TryClearSelectedVessel_WithoutPrivateField_ReportsReflectionError()
        {
            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                new object(),
                out object previousSelection,
                out string error);

            Assert.False(cleared);
            Assert.Null(previousSelection);
            Assert.Equal("selectedVessel field not found", error);
        }

        private sealed class FakeTrackingStation
        {
            private object selectedVessel;

            public FakeTrackingStation(object selectedVessel)
            {
                this.selectedVessel = selectedVessel;
            }

            public object SelectedForTesting => selectedVessel;
        }

        private sealed class FakeTrackingStationWithSetVessel
        {
            private object selectedVessel;

            public FakeTrackingStationWithSetVessel(object selectedVessel)
            {
                this.selectedVessel = selectedVessel;
            }

            public bool SetVesselCalled { get; private set; }

            public object SelectedForTesting => selectedVessel;

            private void SetVessel(object vessel, bool keepFocus)
            {
                SetVesselCalled = true;
                throw new InvalidOperationException("TryClearSelectedVessel should not call SetVessel");
            }
        }
    }
}
