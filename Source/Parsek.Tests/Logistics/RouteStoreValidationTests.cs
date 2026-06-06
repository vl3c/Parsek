using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 5 of the Route store plan: ERS-driven source-ref validation.
    /// Each test pins one transition rule from
    /// <see cref="RouteStore.RevalidateSources"/> — names state the regression
    /// the test protects against.
    ///
    /// <para>
    /// Driving real Phase-3 supersede / rewind-retirement plumbing through
    /// the ParsekScenario lifecycle requires Unity GameEvents that the
    /// xUnit harness cannot synthesize, so these tests use
    /// <c>RecordingStore.AddRecordingWithTreeForTesting</c> + an installed
    /// scenario carrying <c>RecordingSupersedes</c> / <c>RewindRetirements</c>
    /// to drive <see cref="EffectiveState.ComputeERS"/> directly. This is the
    /// canonical pattern used by <c>UnfinishedFlightsMembershipTests</c>.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteStoreValidationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool? priorVerbose;

        public RouteStoreValidationTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorVerbose = ParsekLog.VerboseOverrideForTesting;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RouteStore.ResetForTesting();

            logLines.Clear();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ParsekLog.VerboseOverrideForTesting = priorVerbose;

            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // -----------------------------------------------------------------
        // Fixture helpers
        // -----------------------------------------------------------------

        private static ParsekScenario InstallScenario(
            List<RecordingSupersedeRelation> supersedes = null)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
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

        private static Recording BuildRouteSourceRecording(string id, int sidecarEpoch = 1)
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
                SidecarEpoch = sidecarEpoch,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 500.0,
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "win-" + id,
                        DockUT = 150.0,
                        UndockUT = 450.0,
                        TransferTargetVesselPid = 9999u,
                        TransferKind = RouteConnectionKind.DockingPort,
                        DockTransportResources = new Dictionary<string, ResourceAmount>
                        {
                            { "LiquidFuel", new ResourceAmount { amount = 1000.0, maxAmount = 1000.0 } }
                        }
                    }
                },
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 7777u
                }
            };
        }

        /// <summary>
        /// Builds a SourceRef matching <paramref name="rec"/>'s current state.
        /// Use this then call <see cref="MutateRecording"/> to author drift.
        /// </summary>
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

        private static RouteEndpoint BuildKscOrigin()
        {
            return new RouteEndpoint
            {
                BodyName = "Kerbin",
                Latitude = -0.0972,
                Longitude = -74.5577,
                Altitude = 75.2,
                VesselPersistentId = 0,
                IsSurface = true
            };
        }

        private static RouteStop BuildStop()
        {
            return new RouteStop
            {
                Endpoint = new RouteEndpoint
                {
                    BodyName = "Mun",
                    Latitude = 3.2001,
                    Longitude = -45.1234,
                    Altitude = 612.5,
                    VesselPersistentId = 67890,
                    IsSurface = true
                },
                ConnectionKind = RouteConnectionKind.DockingPort,
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } }
            };
        }

        private static Route BuildRoute(
            string id, RouteStatus status, params RouteSourceRef[] sourceRefs)
        {
            var builder = new RouteFixtureBuilder()
                .WithId(id)
                .WithName(id)
                .WithStatus(status)
                .WithOrigin(BuildKscOrigin())
                .WithStop(BuildStop());
            for (int i = 0; i < sourceRefs.Length; i++)
            {
                builder.WithRecordingId(sourceRefs[i].RecordingId);
                builder.WithSourceRef(sourceRefs[i]);
            }
            return builder.Build();
        }

        // -----------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------

        // catches: route silently keeping Active after recording deletion —
        // §10.15 contract.
        [Fact]
        public void Revalidate_MissingSource_TransitionsToMissingSourceRecording()
        {
            // Recording exists nowhere (never added to RecordingStore).
            var sourceRef = new RouteSourceRef
            {
                RecordingId = "rec-deleted",
                RouteProofHash = "deadbeef00000000"
            };
            // Install scenario BEFORE adding the route: ParsekScenario.
            // BumpSupersedeStateVersion now drives RouteStore.RevalidateSources
            // via the central seam (PR #875 P2-1), so adding the route first
            // would let InstallScenario's internal bump pre-transition the
            // route before this test's explicit RevalidateSources("test") call.
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-deleted-src", RouteStatus.Active, sourceRef));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-deleted-src", out Route route));
            Assert.Equal(RouteStatus.MissingSourceRecording, route.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("Active")
                && l.Contains("MissingSourceRecording")
                && l.Contains("source-not-in-ers"));
        }

        // catches: optimizer rewrite silently honored (SidecarEpoch is the
        // canonical "this sidecar was rewritten" signal — design §4.7).
        [Fact]
        public void Revalidate_FingerprintDrift_SidecarEpoch_TransitionsToSourceChanged()
        {
            var rec = BuildRouteSourceRecording("rec-drift-sidecar", sidecarEpoch: 1);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var sourceRef = BuildMatchingSourceRef(rec);

            // Now drift: bump the recording's SidecarEpoch as if an
            // optimizer rewrote the sidecar.
            rec.SidecarEpoch = 7;
            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();

            // InstallScenario before AddRoute — see Revalidate_MissingSource_… note.
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-sidecar-drift", RouteStatus.Active, sourceRef));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-sidecar-drift", out Route route));
            Assert.Equal(RouteStatus.SourceChanged, route.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("SourceChanged")
                && l.Contains("sidecar-epoch"));
        }

        // logistics-recovery-credit section 5.4: a route that flips INTO
        // MissingSourceRecording stops crossing, so its last dispatched cycle's
        // deferred recovery credit must be flushed (or its stale marker cleared) at
        // the transition, never stranded forever. In the xUnit harness the live
        // env / Planetarium cannot resolve, so the defensive flush no-ops on the
        // Career gate and CLEARS the marker; that is the observable proof the flush
        // call is wired into the into-missing edge.
        // catches: a pending recovery-credit marker stranded on a route deleted
        // while MissingSourceRecording.
        [Fact]
        public void Revalidate_IntoMissingSource_FlushesPendingRecoveryCredit_ClearsMarker()
        {
            var sourceRef = new RouteSourceRef
            {
                RecordingId = "rec-deleted-pending",
                RouteProofHash = "deadbeef00000000"
            };
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-pending-missing", RouteStatus.Active, sourceRef));
            Assert.True(RouteStore.TryGetRoute("route-pending-missing", out Route armed));
            armed.PendingRecoveryCreditCycleId = "cycle-7";
            armed.PendingRecoveryCreditDispatchUT = 1234.0;
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-pending-missing", out Route route));
            Assert.Equal(RouteStatus.MissingSourceRecording, route.Status);
            // The owed credit's marker is flushed/cleared at the transition.
            Assert.Null(route.PendingRecoveryCreditCycleId);
            Assert.Equal(-1.0, route.PendingRecoveryCreditDispatchUT);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("flushing owed recovery credit before source-problem transition"));
        }

        // logistics-recovery-credit section 5.4: SourceChanged never auto-recovers
        // (design 7.4 requires recreation), so a pending credit owed when the route
        // flips into SourceChanged would leak permanently. Flush it at the edge.
        // catches: a pending recovery-credit marker stranded on a SourceChanged route.
        [Fact]
        public void Revalidate_IntoSourceChanged_FlushesPendingRecoveryCredit_ClearsMarker()
        {
            var rec = BuildRouteSourceRecording("rec-drift-pending", sidecarEpoch: 1);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var sourceRef = BuildMatchingSourceRef(rec);

            rec.SidecarEpoch = 9; // drift -> SourceChanged
            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();

            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-pending-changed", RouteStatus.Active, sourceRef));
            Assert.True(RouteStore.TryGetRoute("route-pending-changed", out Route armed));
            armed.PendingRecoveryCreditCycleId = "cycle-3";
            armed.PendingRecoveryCreditDispatchUT = 555.0;
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-pending-changed", out Route route));
            Assert.Equal(RouteStatus.SourceChanged, route.Status);
            Assert.Null(route.PendingRecoveryCreditCycleId);
            Assert.Equal(-1.0, route.PendingRecoveryCreditDispatchUT);
        }

        // logistics-recovery-credit section 5.4: a route with NO owed credit flips
        // into a source-problem state without paying the live UT/env resolution
        // cost (the fast-path early return) and emits no flush log.
        // catches: the flush firing (and logging) when nothing is owed.
        [Fact]
        public void Revalidate_IntoMissingSource_NoPendingCredit_NoFlushLog()
        {
            var sourceRef = new RouteSourceRef
            {
                RecordingId = "rec-deleted-nopending",
                RouteProofHash = "deadbeef00000000"
            };
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-nopending-missing", RouteStatus.Active, sourceRef));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-nopending-missing", out Route route));
            Assert.Equal(RouteStatus.MissingSourceRecording, route.Status);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("flushing owed recovery credit before source-problem transition"));
        }

        // catches: a coarse fingerprint missing route-relevant proof changes.
        // The recording's SidecarEpoch is unchanged but a connection-window
        // UndockUT was rewritten under us — that has to flip the route.
        [Fact]
        public void Revalidate_FingerprintDrift_RouteProofHash_TransitionsToSourceChanged()
        {
            var rec = BuildRouteSourceRecording("rec-drift-hash");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var sourceRef = BuildMatchingSourceRef(rec);

            // Drift the hash by mutating a fingerprint-bearing field.
            // SidecarEpoch / TreeOrder / format version all unchanged.
            rec.RouteConnectionWindows[0].UndockUT = 999.0;
            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();

            // InstallScenario before AddRoute — see Revalidate_MissingSource_… note.
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-hash-drift", RouteStatus.Active, sourceRef));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-hash-drift", out Route route));
            Assert.Equal(RouteStatus.SourceChanged, route.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("SourceChanged")
                && l.Contains("route-proof-hash"));
        }

        // catches: spurious no-op transitions. Active+matching must not emit
        // any transition log; only the summary line is allowed.
        [Fact]
        public void Revalidate_AllMatch_LeavesActiveUntouched()
        {
            var rec = BuildRouteSourceRecording("rec-happy");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var sourceRef = BuildMatchingSourceRef(rec);

            RouteStore.AddRoute(BuildRoute("route-happy", RouteStatus.Active, sourceRef));
            InstallScenario();
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(0, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-happy", out Route route));
            Assert.Equal(RouteStatus.Active, route.Status);

            // No transition log line of the "Active->X" or "X->Active" shape.
            foreach (string line in logLines)
            {
                if (line.Contains("[INFO]")
                    && line.Contains("[Route]")
                    && line.Contains("Route route-hap")
                    && line.Contains("->"))
                {
                    Assert.True(false, "Unexpected transition log: " + line);
                }
                if (line.Contains("[INFO]")
                    && line.Contains("[Route]")
                    && line.Contains("Route route-hap")
                    && line.Contains("→"))
                {
                    Assert.True(false, "Unexpected transition log: " + line);
                }
            }
        }

        // catches: stuck-disabled routes after save-with-recording-restored.
        [Fact]
        public void Revalidate_MissingRecovers_TransitionsBackToActive()
        {
            var rec = BuildRouteSourceRecording("rec-recover");
            // Build the sourceRef from the CURRENT shape so when the
            // recording is added back, fingerprints match.
            var sourceRef = BuildMatchingSourceRef(rec);

            // Route is parked as MissingSourceRecording with no recording in
            // store. First pass would just confirm Missing; instead simulate
            // the "save restored" world by adding the recording before the
            // revalidate pass.
            //
            // InstallScenario before AddRoute — see Revalidate_MissingSource_… note.
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-recover", RouteStatus.MissingSourceRecording, sourceRef));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-recover", out Route route));
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("MissingSourceRecording")
                && l.Contains("Active")
                && l.Contains("source-restored"));
        }

        // catches: §7.4 explicit-recreate signal lost. SourceChanged routes
        // must NOT auto-flip back to Active even when fingerprints match.
        [Fact]
        public void Revalidate_SourceChanged_DoesNotAutoRecover()
        {
            var rec = BuildRouteSourceRecording("rec-stays-changed");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var sourceRef = BuildMatchingSourceRef(rec);

            RouteStore.AddRoute(BuildRoute("route-stays-changed", RouteStatus.SourceChanged, sourceRef));
            InstallScenario();
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(0, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-stays-changed", out Route route));
            Assert.Equal(RouteStatus.SourceChanged, route.Status);
        }

        // catches: re-fly supersede silently leaving the source live to the
        // route system. The recording exists in the raw committed list BUT
        // is filtered out by ERS via a supersede relation — this proves
        // RouteStore consumes ERS rather than CommittedRecordings.
        [Fact]
        public void Revalidate_SupersededRecording_TransitionsToMissingSourceRecording()
        {
            var oldRec = BuildRouteSourceRecording("rec-superseded");
            var newRec = BuildRouteSourceRecording("rec-superseding");
            RecordingStore.AddRecordingWithTreeForTesting(oldRec);
            RecordingStore.AddRecordingWithTreeForTesting(newRec);

            var sourceRef = BuildMatchingSourceRef(oldRec);

            // InstallScenario before AddRoute — see Revalidate_MissingSource_… note.
            // Install a supersede relation that points "rec-superseded"
            // forward to "rec-superseding". ERS filters out the old id.
            InstallScenario(new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    OldRecordingId = "rec-superseded",
                    NewRecordingId = "rec-superseding"
                }
            });
            RouteStore.AddRoute(BuildRoute("route-superseded", RouteStatus.Active, sourceRef));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-superseded", out Route route));
            Assert.Equal(RouteStatus.MissingSourceRecording, route.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("MissingSourceRecording")
                && l.Contains("source-not-in-ers"));
        }

        // catches: a premature scheduler hook in the validation phase.
        // Recovery (Missing -> Active) must not touch any scheduling field.
        [Fact]
        public void Revalidate_DoesNotMutateNextDispatchUT()
        {
            var rec = BuildRouteSourceRecording("rec-noschedule");
            var sourceRef = BuildMatchingSourceRef(rec);

            var route = BuildRoute("route-noschedule", RouteStatus.MissingSourceRecording, sourceRef);
            route.NextDispatchUT = 4242.42;
            route.CurrentSegmentIndex = 3;
            route.CompletedCycles = 12;
            RouteStore.AddRoute(route);

            RecordingStore.AddRecordingWithTreeForTesting(rec);
            InstallScenario();

            RouteStore.RevalidateSources("test");

            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(4242.42, route.NextDispatchUT);
            Assert.Equal(3, route.CurrentSegmentIndex);
            Assert.Equal(12, route.CompletedCycles);
        }

        // catches: the summary log line drift. Three routes -> two
        // transitions (one happy + two problems).
        [Fact]
        public void Revalidate_MultipleRoutes_LogsCountAndTransitions()
        {
            // Route 1: Active, matches.
            var recHappy = BuildRouteSourceRecording("rec-multi-happy");
            RecordingStore.AddRecordingWithTreeForTesting(recHappy);
            var refHappy = BuildMatchingSourceRef(recHappy);

            // Route 2: missing source.
            var refMissing = new RouteSourceRef
            {
                RecordingId = "rec-multi-missing",
                RouteProofHash = "deadbeef00000000"
            };

            // Route 3: source-changed (sidecar drift).
            var recDrift = BuildRouteSourceRecording("rec-multi-drift", sidecarEpoch: 1);
            RecordingStore.AddRecordingWithTreeForTesting(recDrift);
            var refDrift = BuildMatchingSourceRef(recDrift);
            recDrift.SidecarEpoch = 99;
            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();

            // InstallScenario before AddRoute — see Revalidate_MissingSource_… note.
            InstallScenario();
            RouteStore.AddRoute(BuildRoute("route-multi-1", RouteStatus.Active, refHappy));
            RouteStore.AddRoute(BuildRoute("route-multi-2", RouteStatus.Active, refMissing));
            RouteStore.AddRoute(BuildRoute("route-multi-3", RouteStatus.Active, refDrift));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test-multi");

            Assert.Equal(2, transitioned);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("RevalidateSources")
                && l.Contains("reason=test-multi")
                && l.Contains("routes=3")
                && l.Contains("transitioned=2"));
        }

        // catches: a refactor that scans all refs and overwrites the drift field with
        // the LAST mismatching one, or short-circuits on (anyMissing || anyDrift) before
        // reaching ref[1]. The inner loop MUST break on the FIRST issue so the audit log
        // names the specific cause/recording that triggered the transition.
        //
        // Three source-refs with DISTINCT drift causes:
        //   ref[0] -> rec-ok-1, matches exactly (no drift)
        //   ref[1] -> rec-sidecar-drift, sidecar-epoch drift
        //   ref[2] -> rec-hash-drift,    route-proof-hash drift
        // Break-on-first means the cause string reflects ref[1] (sidecar-epoch on rec-side*).
        // If the inner loop changed `break;` to `continue;`, the cause would reflect ref[2]
        // (route-proof-hash on rec-hash*) — that distinction is what this test pins.
        [Fact]
        public void Revalidate_MultiSourceRefSecondDrifts_TransitionsToSourceChanged()
        {
            // ref[0]: matches exactly.
            var recOk = BuildRouteSourceRecording("rec-ok-1");
            RecordingStore.AddRecordingWithTreeForTesting(recOk);
            var refOk = BuildMatchingSourceRef(recOk);

            // ref[1]: capture matching ref, then drift the live recording's SidecarEpoch.
            var recSidecar = BuildRouteSourceRecording("rec-sidecar-drift", sidecarEpoch: 1);
            RecordingStore.AddRecordingWithTreeForTesting(recSidecar);
            var refSidecar = BuildMatchingSourceRef(recSidecar);
            recSidecar.SidecarEpoch = 42;

            // ref[2]: capture matching ref, then drift the live recording's
            // route-proof-bearing field (UndockUT inside the connection window).
            var recHash = BuildRouteSourceRecording("rec-hash-drift");
            RecordingStore.AddRecordingWithTreeForTesting(recHash);
            var refHash = BuildMatchingSourceRef(recHash);
            recHash.RouteConnectionWindows[0].UndockUT = 8888.0;

            RecordingStore.BumpStateVersion();
            EffectiveState.ResetCachesForTesting();

            // InstallScenario before AddRoute — see Revalidate_MissingSource_… note.
            InstallScenario();
            RouteStore.AddRoute(BuildRoute(
                "route-multi-second-drifts",
                RouteStatus.Active,
                refOk, refSidecar, refHash));
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("test-break-on-first");

            Assert.Equal(1, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-multi-second-drifts", out Route route));
            // Status flips to SourceChanged (NOT MissingSourceRecording — ref[0] is in ERS,
            // proving the inner-loop saw and ACCEPTED ref[0] before reaching ref[1]).
            Assert.Equal(RouteStatus.SourceChanged, route.Status);

            // Genuine break-on-first assertion: the cause string names ref[1]'s drift
            // (sidecar-epoch on rec-side*), NOT ref[2]'s (route-proof-hash on rec-hash*).
            // If the inner loop is broken (scans all refs without break), this would
            // instead emit `route-proof-hash-drift id=rec-hash` — the test would fail.
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("Active→SourceChanged")
                && l.Contains("sidecar-epoch-drift")
                && l.Contains("id=rec-side"));

            // Negative pin: there should be NO transition log naming ref[2]'s drift cause.
            // (A broken inner loop would emit this instead.)
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("Active→SourceChanged")
                && l.Contains("route-proof-hash-drift"));
        }

        // catches: NRE on an edge case — route with empty SourceRefs must
        // not crash and must not mutate status.
        [Fact]
        public void Revalidate_NoSourceRefs_SkipsWithVerbose()
        {
            // Build a route by hand so we can leave SourceRefs empty after
            // construction (RouteFixtureBuilder.WithSourceRef appends).
            var route = new Route
            {
                Id = "route-no-refs",
                Name = "No Refs",
                Status = RouteStatus.Active,
                Origin = BuildKscOrigin(),
                Stops = new List<RouteStop> { BuildStop() }
            };
            Assert.Empty(route.SourceRefs);
            RouteStore.AddRoute(route);

            InstallScenario();
            logLines.Clear();

            int transitioned = RouteStore.RevalidateSources("edge");

            Assert.Equal(0, transitioned);
            Assert.True(RouteStore.TryGetRoute("route-no-refs", out Route resolved));
            Assert.Equal(RouteStatus.Active, resolved.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[Route]")
                && l.Contains("route route-no")
                && l.Contains("no SourceRefs"));
        }
    }
}
