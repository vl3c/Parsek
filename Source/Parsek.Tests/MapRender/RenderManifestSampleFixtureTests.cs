using System;
using System.IO;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests.MapRender
{
    /// <summary>
    /// M-A7 RECONCILIATION ANCHOR. Builds a small but SECTION-COMPLETE sample accumulation (at least
    /// one record of every node kind in the schema, all values fixed and synthetic), serializes it the
    /// way the recorder does, and byte-compares against the committed fixture at
    /// <c>Source/Parsek.Tests/Fixtures/RenderManifest/sample-manifest.txt</c>.
    ///
    /// <para>That fixture is the file the Python side (<c>harness/lib/rendercompose.py</c>) pins its
    /// parser against: one of its test fixtures is byte-copied from THIS output, so a node shape that
    /// moves on the C# side reds here first, naming the regeneration step, instead of silently
    /// degrading every Python window to "unparsed".</para>
    ///
    /// <para><b>To regenerate</b> after a deliberate schema change: run this cell, take the
    /// <c>.actual</c> path it names in the failure message, and copy it over the fixture - then update
    /// the Python fixture in the same change.</para>
    /// </summary>
    public class RenderManifestSampleFixtureTests
    {
        internal const string FixtureRelPath = "Fixtures/RenderManifest/sample-manifest.txt";

        [Fact]
        public void SampleManifest_MatchesTheCommittedFixtureByteForByte()
        {
            string actual = SerializeSampleManifest();
            string fixturePath = ResolveFixturePath();

            if (!File.Exists(fixturePath))
            {
                string dumped = DumpActual(actual);
                Assert.True(false,
                    "Render-manifest sample fixture missing at " + fixturePath
                    + ". The freshly serialized sample was written to " + dumped
                    + " - copy it over the fixture path to commit it.");
            }

            string expected = Normalize(File.ReadAllText(fixturePath));
            string normalizedActual = Normalize(actual);
            if (!string.Equals(expected, normalizedActual, StringComparison.Ordinal))
            {
                string dumped = DumpActual(actual);
                Assert.True(false,
                    "Render-manifest sample drifted from the committed fixture. The Python parser "
                    + "pins node shapes against this file, so regenerate BOTH sides deliberately: "
                    + "copy " + dumped + " over " + fixturePath
                    + " and update the byte-copied fixture in harness/lib/test_rendercompose.py.");
            }
        }

        [Fact]
        public void SampleManifest_ContainsOneRecordOfEveryNodeKind()
        {
            ConfigNode root = BuildSampleManifest()
                .BuildFileNode(SampleHeader())
                .GetNode(RenderCompositionManifest.RootNodeName);

            Assert.NotNull(root.GetNode("CONSTANTS"));

            ConfigNode unit = Assert.Single(root.GetNode("PLAN").GetNodes("UNIT"));
            Assert.NotEmpty(unit.GetNodes("MEMBER"));
            Assert.NotEmpty(unit.GetNodes("LOITER_CUT"));
            Assert.NotNull(unit.GetNode("REAIM_SCHEDULE"));
            Assert.NotNull(unit.GetNode("ROUTE"));

            ConfigNode chain = Assert.Single(root.GetNode("CHAIN").GetNodes("CHAIN_BUILD"));
            Assert.NotEmpty(chain.GetNodes("PHASE"));
            Assert.NotEmpty(chain.GetNodes("SEAM"));

            ConfigNode obs = root.GetNode("OBSERVED");
            foreach (string kind in new[]
            {
                "DWELL", "TRANSITION", "SEAM_TANGENT", "SEAM_ENDPOINT", "CLOCK_EVENT", "LINE_BRANCH",
                "OWNERSHIP_CHANGE", "RATIFIED_SKIP", "CLOCK_DEFER", "ANOMALY_ECHO", "ROUTE_LINE_BUILD",
                "ROUTE_LEG_DEFER", "ROUTE_CODRAW_VIOLATION", "TRUNCATED",
            })
            {
                Assert.True(obs.GetNodes(kind).Length > 0,
                    "The sample fixture must carry at least one " + kind + " node - it is the "
                    + "reconciliation anchor for the Python parser.");
            }

            // The DWELL-nested aggregation and the STANDALONE record family are different node
            // populations that happen to share a node NAME; the fixture must pin both, because a
            // consumer that looked only at OBSERVED level (or only inside dwells) would silently see
            // half the anomaly evidence.
            Assert.NotEmpty(obs.GetNodes("DWELL")[0].GetNodes("ANOMALY_ECHO"));
            ConfigNode[] standalone = obs.GetNodes("ANOMALY_ECHO");
            Assert.Equal(2, standalone.Length);
            Assert.Equal("100000", standalone[0].GetValue("pidKey"));       // a numeric pid key
            Assert.Equal("route-0001", standalone[1].GetValue("pidKey"));   // a NON-numeric key
            Assert.Equal("rec-descent", standalone[1].GetValue("recId"));
        }

        /// <summary>
        /// The schema v1.1 ADDITIVE keys. Every one is optional, so a fixture that quietly stopped
        /// carrying one would still parse on both sides - which is exactly why the sample must pin
        /// each of them by name here, not just by byte-comparison.
        /// </summary>
        [Fact]
        public void SampleManifest_CarriesEverySchemaV11OptionalKey()
        {
            ConfigNode root = BuildSampleManifest()
                .BuildFileNode(SampleHeader())
                .GetNode(RenderCompositionManifest.RootNodeName);

            // Decision 1: the body NAMES behind the two rotation periods.
            ConfigNode unit = root.GetNode("PLAN").GetNodes("UNIT")[0];
            Assert.Equal("Kerbin", unit.GetValue("launchBodyName"));
            Assert.Equal("Duna", unit.GetValue("destinationBodyName"));

            ConfigNode obs = root.GetNode("OBSERVED");

            // Decisions 4 + 5: the recorded-clock endpoints + the owning unit on a DWELL.
            ConfigNode dwell = obs.GetNodes("DWELL")[0];
            Assert.Equal("4", dwell.GetValue("ownerIndex"));
            Assert.Equal("1000", dwell.GetValue("openLoopUT"));
            Assert.Equal("1010", dwell.GetValue("closeLoopUT"));

            // Decision 3: the resolved descent head on a descent-phase event.
            ConfigNode descent = FindClockEvent(obs, RenderCompositionManifest.ClockDescentPhase);
            Assert.Equal("7250", descent.GetValue("detailD"));

            // The observation-derived hold pairs, with the CURRENT detail slots: detailA is the run's
            // 0-based ordinal within its (owner, cycle) on BOTH events, and the two pairs below are
            // two stalls inside the SAME cycle - the shape the wave-1 convention (detailA = cycleIndex
            // on release) could not represent at all, because both pairs collapsed into one debounce
            // key.
            ConfigNode[] engages = ClockEventsOfKind(obs, RenderCompositionManifest.ClockHoldEngage);
            ConfigNode[] releases = ClockEventsOfKind(obs, RenderCompositionManifest.ClockHoldRelease);
            Assert.Equal(2, engages.Length);
            Assert.Equal(2, releases.Length);

            Assert.Equal("4", engages[0].GetValue("ownerIndex"));
            Assert.Equal("1", engages[0].GetValue("cycleIndex"));
            Assert.Equal("0", engages[0].GetValue("detailA"));    // run ordinal, NOT the cycle index
            Assert.Equal("5000", engages[0].GetValue("detailB")); // frozen loopUT
            Assert.Equal("1", releases[0].GetValue("cycleIndex"));
            Assert.Equal("0", releases[0].GetValue("detailA"));   // SAME ordinal as its engage
            Assert.Equal("5000", releases[0].GetValue("detailB"));
            Assert.Equal("3600", releases[0].GetValue("detailC")); // accumulated held seconds

            Assert.Equal("1", engages[1].GetValue("cycleIndex")); // same cycle, second stall
            Assert.Equal("1", engages[1].GetValue("detailA"));
            Assert.Equal("6200", engages[1].GetValue("detailB"));
            Assert.Equal("1", releases[1].GetValue("detailA"));
            Assert.Equal("900", releases[1].GetValue("detailC"));
        }

        private static ConfigNode[] ClockEventsOfKind(ConfigNode obs, string kind)
        {
            var hits = new System.Collections.Generic.List<ConfigNode>();
            foreach (ConfigNode n in obs.GetNodes("CLOCK_EVENT"))
            {
                if (string.Equals(n.GetValue("kind"), kind, StringComparison.Ordinal))
                    hits.Add(n);
            }
            return hits.ToArray();
        }

        /// <summary>
        /// detailD is OPTIONAL and must stay absent from the kinds that do not measure one, so a
        /// consumer can tell "not measured" from "measured zero".
        /// </summary>
        [Fact]
        public void SampleManifest_OmitsDetailDOnEveryKindThatDoesNotMeasureOne()
        {
            ConfigNode obs = BuildSampleManifest()
                .BuildFileNode(SampleHeader())
                .GetNode(RenderCompositionManifest.RootNodeName)
                .GetNode("OBSERVED");

            foreach (ConfigNode n in obs.GetNodes("CLOCK_EVENT"))
            {
                bool isDescent = string.Equals(
                    n.GetValue("kind"), RenderCompositionManifest.ClockDescentPhase,
                    StringComparison.Ordinal);
                Assert.Equal(isDescent, n.HasValue("detailD"));
            }
        }

        private static ConfigNode FindClockEvent(ConfigNode obs, string kind)
        {
            foreach (ConfigNode n in obs.GetNodes("CLOCK_EVENT"))
            {
                if (string.Equals(n.GetValue("kind"), kind, StringComparison.Ordinal))
                    return n;
            }
            Assert.True(false, "The sample fixture carries no CLOCK_EVENT of kind '" + kind + "'.");
            return null;
        }

        // =====================================================================================
        //  The deterministic sample
        // =====================================================================================

        internal static RenderCompositionManifest.ManifestHeader SampleHeader()
            => new RenderCompositionManifest.ManifestHeader(
                123456.75, "verb", "TRACKSTATION", "Sample Save", true, false, true);

        internal static RenderCompositionManifest BuildSampleManifest()
        {
            var m = new RenderCompositionManifest();

            // ---- PLAN ----
            var unit = new RenderCompositionManifest.PlanUnitRecord
            {
                Host = "TrackingStation",
                SignatureHash = RenderCompositionManifest.StableHash("sample-builder-signature"),
                OwnerIndex = 4,
                SpanStartUT = 1000.0,
                SpanEndUT = 9000.0,
                CadenceSeconds = 8000.0,
                OverlapCadenceSeconds = 8000.0,
                PhaseAnchorUT = 1000.0,
                IsReaim = true,
                HasRelaunchSchedule = false,
                ArrivalHoldSeconds = 1200.0,
                ArrivalHoldAtUT = 6000.0,
                ArrivalAlignPeriodSeconds = 21549.425,
                ArrivalJointSecondaryPeriodSeconds = 5400.0,
                ArrivalJointSecondaryToleranceSeconds = 60.0,
                ArrivalJointMaxWholeHoldPeriods = 64,
                ArrivalAmberReason = "joint-residual-above-tolerance",
                LaunchBodyName = "Kerbin",
                DestinationBodyName = "Duna",
                LaunchBodyRotationPeriodSeconds = 21549.425,
                LaunchHoldEngaged = true,
                RecordedSoiExitUT = 2500.0,
                DescentMemberIndices = "6,7",
                RecordedDeorbitUT = 7200.0,
                DescentEndUT = 8100.0,
                DestinationBodyRotationPeriodSeconds = 88642.6,
                LoiterPeriodSeconds = 1800.0,
                CaptureShiftSeconds = -450.5,
                ParkingConicEndUT = 6749.5,
                TransferMemberIndex = 5,
                FirstDeorbitLegStartUT = 7100.0,
                TransferMemberRecordingId = "rec-transfer",
            };
            unit.Members.Add(new RenderCompositionManifest.PlanMemberRecord
            { Index = 4, RecId = "rec-launch", StartUT = 1000.0, EndUT = 2600.0 });
            unit.Members.Add(new RenderCompositionManifest.PlanMemberRecord
            { Index = 5, RecId = "rec-transfer", StartUT = 2600.0, EndUT = 7200.0 });
            unit.Members.Add(new RenderCompositionManifest.PlanMemberRecord
            { Index = 6, RecId = "rec-descent", StartUT = 7200.0, EndUT = 8100.0 });
            unit.LoiterCuts.Add(new RenderCompositionManifest.PlanCutRecord
            { StartUT = 3000.0, LengthSeconds = 3600.0 });
            unit.ReaimSchedule = new RenderCompositionManifest.PlanReaimScheduleRecord
            {
                FirstDepartureUT = 1200.0,
                SynodicPeriodSeconds = 19367900.0,
                TofSeconds = 4600.0,
                PhaseAnchorUT = 1000.0,
                CadenceSeconds = 19367900.0,
                Prograde = true,
            };
            unit.Route = new RenderCompositionManifest.PlanRouteRecord
            {
                RouteId = "route-0001",
                BackingMissionTreeId = "tree-0001",
                RecordedDockUT = 6800.0,
                RecordedOriginUndockUT = -1.0,
                DispatchWindowPeriod = 0.0,
                Scope = "SameBody",
                ExcludedIntervalKeys = "keyA;keyB",
            };
            m.AppendPlanUnit(unit);

            // ---- CHAIN ----
            var chain = new RenderCompositionManifest.ChainBuildRecord
            {
                Pid = 100000u,
                RecId = "rec-launch",
                CommittedIndex = 4,
                UT = 1500.0,
                Signature = "rec-launch|1000|2600|3|420|w2",
                WindowIndex = 2,
                Provenance = "spine",
                HasReaimedSegments = true,
                SeamSource = "assembler",
            };
            chain.Phases.Add(new RenderCompositionManifest.ChainPhaseRecord
            { Kind = "ascent", Provenance = "recorded", Body = "Kerbin", StartUT = 1000.0, EndUT = 1400.0 });
            chain.Phases.Add(new RenderCompositionManifest.ChainPhaseRecord
            { Kind = "soi-departure", Provenance = "recorded", Body = "Kerbin", StartUT = 1400.0, EndUT = 2500.0 });
            chain.Phases.Add(new RenderCompositionManifest.ChainPhaseRecord
            {
                Kind = "heliocentric-transfer", Provenance = "synthesized", Body = "Sun",
                StartUT = 2500.0, EndUT = 7100.0,
            });
            chain.Seams.Add(new RenderCompositionManifest.ChainSeamRecord
            { BoundaryIndex = 1, Kind = "rigid" });
            chain.Seams.Add(new RenderCompositionManifest.ChainSeamRecord
            { BoundaryIndex = 2, Kind = "flexible-soi" });
            m.AppendChainBuild(chain);

            // ---- OBSERVED: dwells + transition ----
            var s = default(RenderCompositionManifest.DwellSample);
            s.Pid = 100000u;
            s.RecId = "rec-launch";
            s.CommittedIndex = 4;
            s.ChainSignature = "rec-launch|1000|2600|3|420|w2";
            s.SegmentIndex = 0;
            s.PhaseKind = "ascent";
            s.Treatment = "TracedPath";
            s.Visible = true;
            s.Coverage = "InSegment";
            s.FrameBody = "Kerbin";
            s.CurrentUT = 1000.0;
            s.HeadUT = 1000.0;
            s.WarpRate = 1.0;
            s.PhysicsWarp = false;
            s.MarkerDecision = true;
            s.MarkerTracedPath = true;
            s.MarkerPolyline = false;
            s.MarkerIconSuppressed = false;
            s.HasTruth = true;
            s.TruthBody = "Kerbin";
            s.TruthX = 100.5;
            s.TruthY = -200.25;
            s.TruthZ = 300.125;
            // Schema v1.1: the owning unit + the RECORDED clock at this frame. Both ride the dwell so
            // the verifier can ask cut-containment questions the LIVE openUT/closeUT cannot answer.
            s.HasOwnerIndex = true;
            s.OwnerIndex = 4;
            s.HasLoopUT = true;
            s.LoopUT = 1000.0;
            m.ObserveDwellFrame(in s);

            s.CurrentUT = 1010.0;
            s.HeadUT = 1010.0;
            s.WarpRate = 50.0;
            s.TruthX = 150.5;
            s.LoopUT = 1010.0;
            m.ObserveDwellFrame(in s);
            m.NoteAnomalyEcho(100000u, "rigid-seam-tangent-discontinuity");

            s.CurrentUT = 1020.0;
            s.HeadUT = 1020.0;
            s.WarpRate = 1000.0;
            s.SegmentIndex = 1;
            s.PhaseKind = "soi-departure";
            s.Treatment = "StockConic";
            s.TruthX = 900.5;
            s.LoopUT = 1020.0;
            m.ObserveDwellFrame(in s);
            m.CloseAllOpenDwells(1030.0);

            // ---- OBSERVED: seams ----
            m.AppendSeamTangent(new RenderCompositionManifest.SeamTangentRecord
            {
                Pid = 100000u,
                RecId = "rec-descent",
                LegIndex = 3,
                UT = 7250.0,
                Continuous = false,
                AngleRadians = 0.2345,
                ToleranceRadians = 0.1,
            });
            m.AppendSeamEndpoint(new RenderCompositionManifest.SeamEndpointRecord
            {
                Pid = 100000u,
                RecId = "rec-transfer",
                UT = 7100.0,
                Sampled = true,
                SkipReason = "",
                Ratio = 1.0125,
                EndpointDistanceMeters = 8.4e7,
                SoiRadiusMeters = 8.29e7,
                RatioTolerance = 1.005,
                OutsideSoi = true,
                FromBody = "Sun",
                ToBody = "Duna",
                RecordedSeamUT = 7100.0,
                SeamUT = 7098.5,
                ClockConvention = "baked",
                SeedKind = "matched",
            });

            // ---- OBSERVED: clock events (one of every kind) ----
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 4, 0, 1000.0, 0.0, 1000.0, 0.0, null);
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockInterCycleTail, 4, 0, 8900.0, 0.0, 9000.0, 0.0, null);
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockBoundaryOverlapSecondary, 4, 0, 8950.0,
                1.0, 1005.0, 0.0, "secondary-live");
            // detailD (schema v1.1) = the resolved descent head:
            // recordedDeorbitUT 7200 + (currentUT 7300 - triggerUT 7250) = 7250.
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockDescentPhase, 6, 0, 7300.0,
                7250.0, 6800.0, 0.0004, "Descent", hasDetailD: true, detailD: 7250.0);
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockRouteDockCrossing, 4, 0, 6800.0,
                0.0, 0.0, 0.0, "route-0001");
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockReaimWindow, 4, 2, 1500.0,
                2.0, 6749.5, -450.5, "rec-transfer");
            // The OBSERVED hold pairs. detailA is the run's 0-based ORDINAL within its (owner, cycle)
            // on BOTH events; cycleIndex is the cycle at ENGAGE on both. Cycle 1 (not 0) so ordinal 0
            // is visibly NOT the cycle index.
            //
            // detailC on the first pair (3600 s) is the value the verifier's own per-cycle
            // recomputation produces for this unit's primitives after the clock clamp (W_0 1200 ->
            // joint solve -> clamped to cadence 8000 - compressed span 4400), so the sample
            // demonstrates a hold whose observation and recomputation AGREE.
            //
            // The SECOND pair is a second stall inside the SAME cycle, carrying ordinal 1 and its own
            // frozen loopUT. It exists because the wave-1 convention could not represent this shape at
            // all - both pairs keyed on (owner, cycle) alone, so the second collapsed into the
            // debounce and a cycle that held twice reported one hold.
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockHoldEngage, 4, 1, 8000.0,
                0.0, 5000.0, 0.0, null);
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockHoldRelease, 4, 1, 11600.0,
                0.0, 5000.0, 3600.0, null);
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockHoldEngage, 4, 1, 12800.0,
                1.0, 6200.0, 0.0, null);
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockHoldRelease, 4, 1, 13700.0,
                1.0, 6200.0, 900.0, null);

            // ---- OBSERVED: line branch / ownership / ratified skip / clock defer ----
            m.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
            {
                Pid = 100000u,
                RecId = "rec-launch",
                UT = 1000.0,
                Reason = "director-traced-path-suppress",
                LineActive = false,
                DrawIcons = 0,
                IconSuppressed = true,
                Coverage = "Unknown",
            });
            m.AppendLineBranchIfChanged(new RenderCompositionManifest.LineBranchRecord
            {
                Pid = 100000u,
                RecId = "rec-launch",
                UT = 1020.0,
                Reason = "director-stockconic-visible",
                LineActive = true,
                DrawIcons = 2,
                IconSuppressed = false,
                Coverage = "Inside",
            });
            m.AppendOwnershipChange("rec-launch", 1000.0, appeared: true);
            m.AppendOwnershipChange("rec-launch", 1020.0, appeared: false);
            m.NoteRatifiedSkip(100000u, 2600.0, "reaim-segment-skip");
            m.NoteRatifiedSkip(100000u, 2700.0, "reaim-segment-skip");
            m.NoteClockDefer(0.5);
            m.NoteClockDefer(0.75);

            // ---- OBSERVED: standalone anomaly echoes (one per tracer raise) ----
            // Two shapes on purpose. The FIRST has a numeric pid key and is the same raise the dwell
            // above aggregated, so the fixture shows the two surfaces side by side. The SECOND is the
            // population the dwell-embedded aggregation can never hold: a NON-numeric pid key, with
            // no dwell open for it. Before the standalone family existed that raise was dropped
            // outright, and a verifier could not tell it from "no anomaly was raised".
            m.AppendAnomalyEchoRecord(
                "100000", "rec-launch", "rigid-seam-tangent-discontinuity", 1010.0);
            m.AppendAnomalyEchoRecord(
                "route-0001", "rec-descent", "polyline-orbit-overlap", 7300.0);

            // ---- OBSERVED: route overview line ----
            m.AppendRouteLineBuild(new RenderCompositionManifest.RouteLineBuildRecord
            {
                RouteId = "route-0001",
                Signature = 8123456789L,
                DockClipUT = 6800.0,
                DispatchWindowPeriod = 0.0,
                Scope = "SameBody",
                ResolvableMembers = 3,
                Groups = 3,
                TotalLegs = 11,
                TransferLegsDropped = 0,
                UT = 1500.0,
            });
            m.NoteRouteLegDeferred("route-0001", "rec-launch");
            m.NoteRouteLegDeferred("route-0001", "rec-launch");
            m.AppendRouteCoDrawViolation("route-0001", "rec-descent", 7300.0, 4242);

            // ---- OBSERVED: an explicit truncation marker ----
            m.NoteTruncationForTesting("DWELL", 100001u, "dwell");

            return m;
        }

        internal static string SerializeSampleManifest()
            => SerializeConfigNode(
                BuildSampleManifest().BuildFileNode(SampleHeader()), "parsek-render-manifest-sample-");

        /// <summary>
        /// THE shared ConfigNode-to-text helper for every render-manifest cell. It round-trips through
        /// <c>ConfigNode.Save</c> to a temp file deliberately: that is the exact writer the recorder's
        /// safe-write uses, so the bytes a cell measures are the bytes a run produces. Any cell that
        /// re-implemented this would be measuring its own formatting instead.
        /// </summary>
        internal static string SerializeConfigNode(ConfigNode file, string tempPrefix)
        {
            string tmp = Path.Combine(Path.GetTempPath(),
                tempPrefix + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                file.Save(tmp);
                return File.ReadAllText(tmp);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (IOException) { }
            }
        }

        private static string Normalize(string text)
            => (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

        private static string DumpActual(string actual)
        {
            string path = Path.Combine(Path.GetTempPath(), "parsek-render-manifest-sample.actual.txt");
            File.WriteAllText(path, actual);
            return path;
        }

        internal static string ResolveFixturePath()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(root, "Source", "Parsek.Tests",
                FixtureRelPath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
