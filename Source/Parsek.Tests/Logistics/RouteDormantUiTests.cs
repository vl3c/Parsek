using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Dormant-route UI + auto-pause/auto-resume timeline markers (branch
    /// <c>logistics-dormant-ui</c>): the caller-gated
    /// <see cref="RouteStore.RevalidateSources(string, double)"/> marker
    /// emission (OnLoad-silent default, live-emit, one-row-per-flip,
    /// recovery-to-Paused-is-not-a-resume), the re-added
    /// <see cref="RouteStore.RemoveDormantRoute"/>, and the pure
    /// <see cref="LogisticsDormantPresentation"/> helpers behind the Logistics
    /// window's collapsed Dormant Routes disclosure.
    /// [Collection("Sequential")] + full static reset per the shared-static rule.
    /// </summary>
    [Collection("Sequential")]
    public class RouteDormantUiTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteDormantUiTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RouteStore.ResetForTesting();

            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures (mirrors RouteStoreValidationTests)
        // ------------------------------------------------------------------

        private static ParsekScenario InstallScenario()
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = null
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static Recording BuildRouteSourceRecording(string id)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                TreeId = "tree-" + id,
                TreeOrder = 0,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 1,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 500.0,
            };
        }

        private static RouteSourceRef BuildMatchingSourceRef(Recording rec)
        {
            return new RouteSourceRef
            {
                RecordingId = rec.RecordingId,
                TreeId = rec.TreeId,
                TreeOrder = rec.TreeOrder,
                RecordingFormatVersion = rec.RecordingFormatVersion,
                RecordingSchemaGeneration = rec.RecordingSchemaGeneration,
                SidecarEpoch = rec.SidecarEpoch,
                StartUT = rec.StartUT,
                EndUT = rec.EndUT,
                RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec)
            };
        }

        private static Route BuildRoute(string id, RouteStatus status, params RouteSourceRef[] sourceRefs)
        {
            var builder = new RouteFixtureBuilder()
                .WithId(id)
                .WithName(id)
                .WithStatus(status)
                .WithOrigin(new RouteEndpoint
                {
                    BodyName = "Kerbin",
                    Latitude = -0.09,
                    Longitude = -74.55,
                    Altitude = 75.0,
                    VesselPersistentId = 0,
                    IsSurface = true
                })
                .WithStop(new RouteStop
                {
                    Endpoint = new RouteEndpoint
                    {
                        BodyName = "Mun",
                        Latitude = 3.2,
                        Longitude = -45.1,
                        Altitude = 612.0,
                        VesselPersistentId = 67890,
                        IsSurface = true
                    },
                    ConnectionKind = RouteConnectionKind.DockingPort,
                    SegmentIndexBefore = 0,
                    DeliveryOffsetSeconds = 0.0,
                    DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } }
                });
            for (int i = 0; i < sourceRefs.Length; i++)
            {
                builder.WithRecordingId(sourceRefs[i].RecordingId);
                builder.WithSourceRef(sourceRefs[i]);
            }
            return builder.Build();
        }

        private static RouteSourceRef MissingSourceRef(string recId = "rec-gone")
        {
            return new RouteSourceRef { RecordingId = recId, RouteProofHash = "deadbeef00000000" };
        }

        // ------------------------------------------------------------------
        // Pure marker decision: TryDecideAutoLifecycleMarker
        // ------------------------------------------------------------------

        // catches: OnLoad revalidation passes writing ledger rows (the
        // load-context ledger-write + status-flicker hazard the caller gate exists for).
        [Theory]
        [InlineData(-1.0)]
        [InlineData(0.0)]
        public void Decide_NonLiveUT_NeverEmits(double liveEmitUT)
        {
            bool emit = RouteStore.TryDecideAutoLifecycleMarker(
                RouteStatus.Active, RouteStatus.MissingSourceRecording, liveEmitUT,
                out _, out _);
            Assert.False(emit);
        }

        [Fact]
        public void Decide_IntoMissing_EmitsAutoPause()
        {
            bool emit = RouteStore.TryDecideAutoLifecycleMarker(
                RouteStatus.Active, RouteStatus.MissingSourceRecording, 1000.0,
                out GameActionType type, out string reason);
            Assert.True(emit);
            Assert.Equal(GameActionType.RoutePaused, type);
            Assert.Equal("AutoPause:MissingSourceRecording", reason);
        }

        [Fact]
        public void Decide_IntoSourceChanged_EmitsAutoPause()
        {
            bool emit = RouteStore.TryDecideAutoLifecycleMarker(
                RouteStatus.Paused, RouteStatus.SourceChanged, 1000.0,
                out GameActionType type, out string reason);
            Assert.True(emit);
            Assert.Equal(GameActionType.RoutePaused, type);
            Assert.Equal("AutoPause:SourceChanged", reason);
        }

        // catches: a self-edge writing a row (one row per FLIP, never per pass).
        [Fact]
        public void Decide_NoFlip_NeverEmits()
        {
            bool emit = RouteStore.TryDecideAutoLifecycleMarker(
                RouteStatus.MissingSourceRecording, RouteStatus.MissingSourceRecording, 1000.0,
                out _, out _);
            Assert.False(emit);
        }

        // catches: double-pausing an already-auto-paused route on a
        // Missing <-> SourceChanged shuffle.
        [Fact]
        public void Decide_MissingToSourceChanged_NeverEmits()
        {
            bool emit = RouteStore.TryDecideAutoLifecycleMarker(
                RouteStatus.MissingSourceRecording, RouteStatus.SourceChanged, 1000.0,
                out _, out _);
            Assert.False(emit);
        }

        [Fact]
        public void Decide_RecoveryToActive_EmitsAutoResume()
        {
            bool emit = RouteStore.TryDecideAutoLifecycleMarker(
                RouteStatus.MissingSourceRecording, RouteStatus.Active, 1000.0,
                out GameActionType type, out string reason);
            Assert.True(emit);
            Assert.Equal(GameActionType.RouteResumed, type);
            Assert.Equal("AutoResume:SourcesRestored", reason);
        }

        // catches: a recovery that faithfully restores a deliberate Paused
        // stamping a resume row (nothing resumed).
        [Fact]
        public void Decide_RecoveryToPaused_NeverEmits()
        {
            bool emit = RouteStore.TryDecideAutoLifecycleMarker(
                RouteStatus.MissingSourceRecording, RouteStatus.Paused, 1000.0,
                out _, out _);
            Assert.False(emit);
        }

        // catches: narrowing "Active-family" to literally Active - a restored
        // wait-state route IS running (the Active section holds it).
        [Fact]
        public void Decide_RecoveryToWaitState_EmitsAutoResume()
        {
            bool emit = RouteStore.TryDecideAutoLifecycleMarker(
                RouteStatus.MissingSourceRecording, RouteStatus.WaitingForResources, 1000.0,
                out GameActionType type, out string reason);
            Assert.True(emit);
            Assert.Equal(GameActionType.RouteResumed, type);
            Assert.Equal("AutoResume:SourcesRestored", reason);
        }

        [Fact]
        public void IsActiveFamilyStatus_Matrix()
        {
            // RouteStatus is internal, so an InlineData theory cannot carry it;
            // walk the full matrix in one fact instead.
            var expected = new Dictionary<RouteStatus, bool>
            {
                { RouteStatus.Active, true },
                { RouteStatus.InTransit, true },
                { RouteStatus.WaitingForResources, true },
                { RouteStatus.WaitingForFunds, true },
                { RouteStatus.DestinationFull, true },
                { RouteStatus.EndpointLost, true },
                { RouteStatus.MissingSourceRecording, false },
                { RouteStatus.SourceChanged, false },
                { RouteStatus.Paused, false },
            };
            foreach (KeyValuePair<RouteStatus, bool> kv in expected)
                Assert.Equal(kv.Value, RouteStore.IsActiveFamilyStatus(kv.Key));
            // Guard against a new enum member silently missing from this matrix.
            Assert.Equal(Enum.GetValues(typeof(RouteStatus)).Length, expected.Count);
        }

        // ------------------------------------------------------------------
        // RevalidateSources integration: caller-gated emission
        // ------------------------------------------------------------------

        // catches: a live revalidation flip into MissingSourceRecording leaving
        // no trace on the timeline.
        [Fact]
        public void Revalidate_LiveEmit_MissingSource_WritesRoutePausedRow()
        {
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-live-missing", RouteStatus.Active, MissingSourceRef()));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test-live", 1234.5);

            Assert.Equal(1, transitioned);
            GameAction row = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.RoutePaused);
            Assert.NotNull(row);
            Assert.Equal("AutoPause:MissingSourceRecording", row.RouteEndpointReason);
            Assert.Equal("route-live-missing", row.RouteId);
            Assert.Equal(1234.5, row.UT);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("LifecycleMarker")
                && l.Contains("AutoPause:MissingSourceRecording"));
        }

        // catches: the OnLoad-shaped default pass writing ledger rows (current
        // behavior must stay byte-identical when no liveEmitUT is passed).
        [Fact]
        public void Revalidate_DefaultOnLoadShape_MissingSource_WritesNoRow()
        {
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-load-missing", RouteStatus.Active, MissingSourceRef()));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("OnLoad");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-load-missing", out Route route));
            Assert.Equal(RouteStatus.MissingSourceRecording, route.Status);
            Assert.Empty(Ledger.Actions);
        }

        // catches: a repeated live pass over an already-flipped route stamping
        // a second row (one row per flip).
        [Fact]
        public void Revalidate_SecondLivePass_NoAdditionalRow()
        {
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-repeat", RouteStatus.Active, MissingSourceRef()));

            Assert.Equal(1, RouteStore.RevalidateSources("test-live", 1000.0));
            Assert.Equal(0, RouteStore.RevalidateSources("test-live-again", 2000.0));

            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RoutePaused));
        }

        // catches: a live recovery back to Active leaving no resume marker.
        [Fact]
        public void Revalidate_LiveRecovery_RestoredActive_WritesRouteResumedRow()
        {
            var rec = BuildRouteSourceRecording("rec-back");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            InstallScenario();
            Route route = BuildRoute("route-recover", RouteStatus.MissingSourceRecording,
                BuildMatchingSourceRef(rec));
            RouteStore.AddRoute(route);
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test-live", 5000.0);

            Assert.Equal(1, transitioned);
            Assert.Equal(RouteStatus.Active, route.Status);
            GameAction row = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.RouteResumed);
            Assert.NotNull(row);
            Assert.Equal("AutoResume:SourcesRestored", row.RouteEndpointReason);
            Assert.Equal(5000.0, row.UT);
            Assert.Empty(Ledger.Actions.Where(a => a.Type == GameActionType.RoutePaused));
        }

        // catches: a recovery that restores the remembered Paused stamping a
        // bogus resume row (the route never resumed).
        [Fact]
        public void Revalidate_LiveRecovery_RestoredPaused_WritesNoResumeRow()
        {
            var rec = BuildRouteSourceRecording("rec-back-paused");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            InstallScenario();
            Route route = BuildRoute("route-recover-paused", RouteStatus.MissingSourceRecording,
                BuildMatchingSourceRef(rec));
            route.PreMissingStatus = RouteStatus.Paused;
            RouteStore.AddRoute(route);
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test-live", 5000.0);

            Assert.Equal(1, transitioned);
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.Empty(Ledger.Actions);
        }

        // catches: the SupersedeCommit live seam regressing to the silent shape
        // (FlipMergeStateAndClearTransient routes through
        // BumpSupersedeStateVersion(liveUT); this pins the pass-through).
        [Fact]
        public void BumpSupersedeStateVersion_WithLiveUT_EmitsRow_DefaultStaysSilent()
        {
            ParsekScenario scenario = InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-bump", RouteStatus.Active, MissingSourceRef()));

            // Parameterless bump (every load-context / bookkeeping site): the
            // flip happens but no row is written.
            scenario.BumpSupersedeStateVersion();
            Assert.True(RouteStore.TryGetRoute("route-bump", out Route route));
            Assert.Equal(RouteStatus.MissingSourceRecording, route.Status);
            Assert.Empty(Ledger.Actions);

            // Live bump over a fresh flip emits exactly one row.
            RouteStore.AddRoute(BuildRoute("route-bump-2", RouteStatus.Active, MissingSourceRef("rec-gone-2")));
            scenario.BumpSupersedeStateVersion(7777.0);
            GameAction row = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.RoutePaused);
            Assert.NotNull(row);
            Assert.Equal("route-bump-2", row.RouteId);
            Assert.Equal(7777.0, row.UT);
        }

        // catches: the dormant-materialize revalidation running silent - a
        // route materializing onto superseded sources must stamp its auto-pause
        // at the live materialize UT.
        [Fact]
        public void Materialize_LivePass_MissingSources_EmitsAutoPauseAtCurrentUT()
        {
            InstallScenario();
            Route dormant = BuildRoute("route-dormant", RouteStatus.Active, MissingSourceRef());
            dormant.CreatedUT = 100.0;
            RouteStore.InstallRoutesAtRewind(new List<Route>(), new List<Route> { dormant });
            logLines.Clear();

            int materialized = RouteStore.MaterializeDueDormantRoutes(200.0);

            Assert.Equal(1, materialized);
            Assert.True(RouteStore.TryGetRoute("route-dormant", out Route route));
            Assert.Equal(RouteStatus.MissingSourceRecording, route.Status);
            GameAction row = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.RoutePaused);
            Assert.NotNull(row);
            Assert.Equal("AutoPause:MissingSourceRecording", row.RouteEndpointReason);
            Assert.Equal(200.0, row.UT);
        }

        // ------------------------------------------------------------------
        // RemoveDormantRoute (re-added for the Dormant-section Delete)
        // ------------------------------------------------------------------

        [Fact]
        public void RemoveDormantRoute_RemovesAndLogs()
        {
            Route dormant = BuildRoute("route-del", RouteStatus.Paused);
            dormant.CreatedUT = 100.0;
            RouteStore.InstallRoutesAtRewind(new List<Route>(), new List<Route> { dormant });
            logLines.Clear();

            Assert.True(RouteStore.RemoveDormantRoute("route-del"));

            Assert.Empty(RouteStore.DormantRoutes);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") && l.Contains("[Route]")
                && l.Contains("Dormant route") && l.Contains("removed"));
        }

        [Fact]
        public void RemoveDormantRoute_MissAndNull_ReturnFalseWithWarn()
        {
            Assert.False(RouteStore.RemoveDormantRoute(null));
            Assert.False(RouteStore.RemoveDormantRoute(""));
            Assert.False(RouteStore.RemoveDormantRoute("no-such-route"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("RemoveDormantRoute") && l.Contains("not found"));
        }

        // catches: the dormant delete reaching into the committed list (a
        // committed route sharing the id must survive).
        [Fact]
        public void RemoveDormantRoute_DoesNotTouchCommittedRoutes()
        {
            RouteStore.AddRoute(BuildRoute("route-committed", RouteStatus.Active));
            Route dormant = BuildRoute("route-dorm-2", RouteStatus.Paused);
            dormant.CreatedUT = 100.0;
            RouteStore.InstallRoutesAtRewind(
                new List<Route> { BuildRoute("route-committed", RouteStatus.Active) },
                new List<Route> { dormant });

            Assert.False(RouteStore.RemoveDormantRoute("route-committed"));
            Assert.True(RouteStore.RemoveDormantRoute("route-dorm-2"));
            Assert.True(RouteStore.TryGetRoute("route-committed", out _));
            Assert.Empty(RouteStore.DormantRoutes);
        }

        // ------------------------------------------------------------------
        // Pure presentation helpers (LogisticsDormantPresentation)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(5, true)]
        public void ShouldShowDormantSection_Matrix(int count, bool expected)
        {
            Assert.Equal(expected, LogisticsDormantPresentation.ShouldShowDormantSection(count));
        }

        [Fact]
        public void DormantSectionTitle_CarriesCount()
        {
            Assert.Equal("Dormant Routes (3)", LogisticsDormantPresentation.DormantSectionTitle(3));
        }

        [Fact]
        public void DormantRouteDisplayName_FallbackChain()
        {
            Assert.Equal("Fuel Run", LogisticsDormantPresentation.DormantRouteDisplayName(
                "Fuel Run", "abcdefgh-1234"));
            Assert.Equal("abcdefgh", LogisticsDormantPresentation.DormantRouteDisplayName(
                null, "abcdefgh-1234"));
            Assert.Equal("<unnamed>", LogisticsDormantPresentation.DormantRouteDisplayName(null, null));
        }

        [Fact]
        public void DormantAppearsLabel_KnownAndUnknown()
        {
            Assert.Equal("appears at Y1, D5",
                LogisticsDormantPresentation.DormantAppearsLabel(1000.0, "Y1, D5"));
            // Defensive shapes: unset CreatedUT, or the formatter unavailable.
            Assert.Equal("appears at <unknown>",
                LogisticsDormantPresentation.DormantAppearsLabel(-1.0, "Y1, D5"));
            Assert.Equal("appears at <unknown>",
                LogisticsDormantPresentation.DormantAppearsLabel(1000.0, null));
        }

        [Fact]
        public void BuildDeleteDormantConfirmBody_NamesRouteAndDormantState()
        {
            string body = LogisticsDormantPresentation.BuildDeleteDormantConfirmBody("Fuel Run");
            Assert.Contains("Delete dormant route 'Fuel Run'?", body);
            Assert.Contains("never re-materialize", body);
            Assert.Contains("cannot be undone", body);

            Assert.Contains("'<unnamed>'",
                LogisticsDormantPresentation.BuildDeleteDormantConfirmBody(null));
        }
    }
}
