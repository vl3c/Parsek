using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M3 near-miss surfacing: the pure
    /// <see cref="LogisticsRejectPresentation.DescribeNearMiss"/> reason mapping
    /// (the 5 <see cref="RouteAnalysisStatus"/> reject strings delegated to
    /// <see cref="RouteCreationFormatters.FormatRejectMessage"/> plus the one new
    /// not-fully-sealed string) and the
    /// <see cref="RouteCandidateFinder.DeriveNearMisses"/> collector (a committed
    /// tree that is unsealed or sealed-but-ineligible is a near-miss; an eligible
    /// tree, promoted or not, is not). Runs Sequential because DeriveNearMisses logs
    /// through the global static sink.
    /// </summary>
    [Collection("Sequential")]
    public class LogisticsRejectPresentationTests : IDisposable
    {
        public LogisticsRejectPresentationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- DescribeNearMiss: sealed-but-ineligible (delegates to FormatRejectMessage) ----

        // catches: the near-miss text diverging from the canonical reject message
        // for any of the 5 statuses. DescribeNearMiss must NOT duplicate the strings;
        // for a sealed tree it returns exactly FormatRejectMessage(status).
        [Theory]
        [InlineData((int)RouteAnalysisStatus.MissingRouteProof)]
        [InlineData((int)RouteAnalysisStatus.MultipleConnectionWindows)]
        [InlineData((int)RouteAnalysisStatus.NoDeliveryManifest)]
        [InlineData((int)RouteAnalysisStatus.MixedPickupDelivery)]
        [InlineData((int)RouteAnalysisStatus.MissingEndpointProof)]
        [InlineData((int)RouteAnalysisStatus.UndockedStartOrigin)]
        public void DescribeNearMiss_Sealed_DelegatesToRejectMessage(int statusOrdinal)
        {
            var status = (RouteAnalysisStatus)statusOrdinal;
            string expected = RouteCreationFormatters.FormatRejectMessage(status);

            Assert.Equal(expected, LogisticsRejectPresentation.DescribeNearMiss(
                status, notSealed: false, reflyableCount: 0));
        }

        // catches (M1, D7): the new undocked-start workflow rejection not passing
        // through to the near-miss row verbatim (a near-miss with the new status
        // must render the canonical workflow-guidance text, never a blank or the
        // generic fallback).
        [Fact]
        public void DescribeNearMiss_UndockedStartOrigin_PassesThrough()
        {
            string text = LogisticsRejectPresentation.DescribeNearMiss(
                RouteAnalysisStatus.UndockedStartOrigin, notSealed: false, reflyableCount: 0);

            Assert.Equal(
                RouteCreationFormatters.FormatRejectMessage(RouteAnalysisStatus.UndockedStartOrigin),
                text);
            Assert.Contains("starts undocked", text);
        }

        // ---- DescribeNearMiss: not-fully-sealed (the one new string) ----

        // catches: the singular noun for a single re-flyable recording.
        [Fact]
        public void DescribeNearMiss_NotSealed_Singular()
        {
            Assert.Equal("not fully sealed (1 recording still re-flyable)",
                LogisticsRejectPresentation.DescribeNearMiss(
                    RouteAnalysisStatus.Eligible, notSealed: true, reflyableCount: 1));
        }

        // catches: the plural noun for multiple re-flyable recordings.
        [Fact]
        public void DescribeNearMiss_NotSealed_Plural()
        {
            Assert.Equal("not fully sealed (3 recordings still re-flyable)",
                LogisticsRejectPresentation.DescribeNearMiss(
                    RouteAnalysisStatus.Eligible, notSealed: true, reflyableCount: 3));
        }

        // catches: the not-sealed branch reading the status argument. notSealed=true
        // ignores the status entirely (the same string regardless of the status).
        [Fact]
        public void DescribeNearMiss_NotSealed_IgnoresStatus()
        {
            string viaEligible = LogisticsRejectPresentation.DescribeNearMiss(
                RouteAnalysisStatus.Eligible, notSealed: true, reflyableCount: 2);
            string viaReject = LogisticsRejectPresentation.DescribeNearMiss(
                RouteAnalysisStatus.MixedPickupDelivery, notSealed: true, reflyableCount: 2);
            Assert.Equal(viaEligible, viaReject);
            Assert.Equal("not fully sealed (2 recordings still re-flyable)", viaEligible);
        }

        // ---- DeriveNearMisses ----

        // catches: a not-fully-sealed tree not surfacing as a NotSealed near-miss
        // with the right re-flyable count. Flipping one of three recordings to a
        // re-flyable state yields one near-miss with ReflyableCount == 1.
        [Fact]
        public void DeriveNearMisses_UnsealedTree_NotSealedNearMiss()
        {
            RecordingTree tree = BuildEligibleTree("t-unsealed");
            tree.Recordings["mid"].MergeState = MergeState.CommittedProvisional;

            var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree });

            Assert.Single(nearMisses);
            Assert.True(nearMisses[0].NotSealed);
            Assert.Equal(1, nearMisses[0].ReflyableCount);
            Assert.Same(tree, nearMisses[0].Tree);
        }

        // catches: the re-flyable count not summing every non-Immutable recording.
        // Two re-flyable recordings (one NotCommitted, one CommittedProvisional) ->
        // ReflyableCount == 2.
        [Fact]
        public void DeriveNearMisses_TwoReflyable_CountsBoth()
        {
            RecordingTree tree = BuildEligibleTree("t-two");
            tree.Recordings["root"].MergeState = MergeState.NotCommitted;
            tree.Recordings["mid"].MergeState = MergeState.CommittedProvisional;

            var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree });

            Assert.Single(nearMisses);
            Assert.True(nearMisses[0].NotSealed);
            Assert.Equal(2, nearMisses[0].ReflyableCount);
        }

        // catches: a sealed-but-ineligible tree not surfacing the analysis status. A
        // sealed tree with no route connection window is ineligible (MissingRouteProof)
        // and must surface as a non-NotSealed near-miss carrying that status.
        [Fact]
        public void DeriveNearMisses_SealedIneligible_CarriesStatus()
        {
            var tree = new RecordingTree { Id = "t-empty", RootRecordingId = "root" };
            tree.AddOrReplaceRecording(new Recording { RecordingId = "root", TreeId = "t-empty" });

            var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree });

            Assert.Single(nearMisses);
            Assert.False(nearMisses[0].NotSealed);
            Assert.Equal(RouteAnalysisStatus.MissingRouteProof, nearMisses[0].Status);
        }

        // catches: an eligible, unpromoted tree wrongly showing as a near-miss. It is
        // an open CANDIDATE, not a near-miss, so DeriveNearMisses skips it.
        [Fact]
        public void DeriveNearMisses_EligibleUnpromoted_NotANearMiss()
        {
            RecordingTree tree = BuildEligibleTree("t-eligible");

            var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree });

            Assert.Empty(nearMisses);
        }

        // catches: an eligible tree (whether or not a route already owns it) wrongly
        // showing as "not eligible". Near-miss derivation skips ALL eligible trees, so
        // promotion is structurally irrelevant (DeriveNearMisses takes no route list).
        [Fact]
        public void DeriveNearMisses_EligiblePromoted_NotANearMiss()
        {
            RecordingTree tree = BuildEligibleTree("t-promoted"); // source recording id == "mid"

            var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree });

            Assert.Empty(nearMisses);
        }

        [Fact]
        public void DeriveNearMisses_NoTrees_ReturnsEmpty()
        {
            Assert.Empty(RouteCandidateFinder.DeriveNearMisses(null));
            Assert.Empty(RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree>()));
        }

        // catches: an empty committed tree (no recordings) being reported as a near-miss
        // with the self-contradictory "not fully sealed (0 recordings still re-flyable)".
        // An empty tree is not a meaningful near-miss and is skipped entirely.
        [Fact]
        public void DeriveNearMisses_EmptyTree_NotANearMiss()
        {
            var emptyTree = new RecordingTree { Id = "t-empty", RootRecordingId = "root" };

            var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { emptyTree });

            Assert.Empty(nearMisses);
        }

        // catches: end-to-end consistency of the collector + the pure describe. The
        // unsealed near-miss's count drives the rendered string.
        [Fact]
        public void DeriveNearMisses_ThenDescribe_RendersReflyableCount()
        {
            RecordingTree tree = BuildEligibleTree("t-render");
            tree.Recordings["mid"].MergeState = MergeState.CommittedProvisional;

            var nearMisses = RouteCandidateFinder.DeriveNearMisses(
                new List<RecordingTree> { tree });

            RouteNearMiss nm = Assert.Single(nearMisses);
            string text = LogisticsRejectPresentation.DescribeNearMiss(
                nm.Status, nm.NotSealed, nm.ReflyableCount);
            Assert.Equal("not fully sealed (1 recording still re-flyable)", text);
        }

        // ------------------------------------------------------------------
        // Helpers (mirror RouteCandidateFinderTests): a sealed tree carrying one
        // eligible dock-deliver-undock window on its "mid" recording. All recordings
        // default to MergeState.Immutable, so the tree is fully sealed unless a test
        // flips one.
        // ------------------------------------------------------------------

        private static RecordingTree BuildEligibleTree(string treeId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = "root",
                ActiveRecordingId = null
            };
            tree.BranchPoints = new List<BranchPoint>
            {
                new BranchPoint { Id = "bp-dock", ParentRecordingIds = new List<string> { "root" } },
                new BranchPoint { Id = "bp-undock", ParentRecordingIds = new List<string> { "mid" } }
            };
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "root",
                TreeId = treeId,
                ParentBranchPointId = null,
                // Root = origin recording for the M1 undocked-start gate; a KSC
                // origin keeps the tree eligible.
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad"
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "mid",
                TreeId = treeId,
                ParentBranchPointId = "bp-dock",
                RouteConnectionWindows = new List<RouteConnectionWindow> { BuildDeliveryWindow() }
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "post",
                TreeId = treeId,
                ParentBranchPointId = "bp-undock"
            });
            return tree;
        }

        private static RouteConnectionWindow BuildDeliveryWindow()
        {
            return new RouteConnectionWindow
            {
                WindowId = "window",
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                DockTransportResources = Manifest(80.0, 100.0),
                UndockTransportResources = Manifest(30.0, 100.0),
                DockEndpointResources = Manifest(0.0, 200.0),
                UndockEndpointResources = Manifest(50.0, 200.0),
                EndpointAtDock = new RouteEndpoint
                {
                    VesselPersistentId = 9001,
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 3.0,
                    IsSurface = true
                },
                TransferEndpointSituation = 4
            };
        }

        private static Dictionary<string, ResourceAmount> Manifest(double amount, double maxAmount)
        {
            return new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = amount, maxAmount = maxAmount }
            };
        }
    }
}
