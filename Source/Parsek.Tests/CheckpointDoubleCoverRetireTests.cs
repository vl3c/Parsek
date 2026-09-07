using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Analyzer;
using Parsek.Analyzer.Rules;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// INTERBODY-SAVE-CARRIES-INV2-DOUBLE-COVER: the RETIRE half.
    ///
    /// <para>The producer's anti-double-cover guard is preventive only, so a save written
    /// before it landed keeps a coarse OrbitalCheckpoint envelope [T0,T2] alongside the
    /// finer sections [T0,T1] + [T1,T2] that tile it, and INV2-NO-DOUBLE-COVER reds it
    /// forever. These cells pin <see cref="CheckpointDoubleCoverRetire"/>: what it drops,
    /// what it refuses to touch, that the coverage union and the optimizer's split
    /// decisions and a route's proof hash all stay put, and that a second pass drops
    /// nothing.</para>
    ///
    /// <para>The three T0/T1/T2 triples are the MEASURED values from the todo entry's
    /// table (the `interbody-route-recorded` recordings 041770246..., 36c7688b... and
    /// 58130506...), not invented ones.</para>
    /// </summary>
    [Collection("Sequential")]
    public class CheckpointDoubleCoverRetireTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool prevSuppress;

        public CheckpointDoubleCoverRetireTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = prevSuppress;
        }

        // --- fixture helpers -------------------------------------------------

        /// <summary>
        /// One conic. A re-clip of a conic differs from its parent in startUT/endUT ONLY
        /// (OrbitSegmentCheckpointBridge.TryTrimOrbitSegmentToRange copies every element
        /// verbatim), so the fixtures build re-clips exactly that way.
        /// </summary>
        private static OrbitSegment Conic(double startUT, double endUT,
            string body = "Kerbin", double sma = 682465.25560889882)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 0.47436829303103406,
                eccentricity = 0.092210616537702336,
                semiMajorAxis = sma,
                longitudeOfAscendingNode = 88.265807776990584,
                argumentOfPeriapsis = 143.78730369454365,
                meanAnomalyAtEpoch = 1.3040035555390415,
                epoch = 6653165.433635856,
                bodyName = body,
                isPredicted = false
            };
        }

        private static TrackSection Checkpoint(OrbitSegment segment)
        {
            return OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(segment);
        }

        /// <summary>A real per-frame Absolute section, the kind that brackets an on-rails run.</summary>
        private static TrackSection Physical(double startUT, double endUT,
            SegmentEnvironment env = SegmentEnvironment.ExoBallistic, string body = "Kerbin")
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT, bodyName = body },
                    new TrajectoryPoint { ut = endUT, bodyName = body }
                },
                checkpoints = new List<OrbitSegment>()
            };
        }

        /// <summary>
        /// The residue shape from the entry, bracketed by per-frame sections the way the
        /// real recordings bracket it: [n] T0..T1, [n+1] T0..T2 (the coarse envelope),
        /// [n+2] T1..T2 - all three OrbitalCheckpoint / src=Checkpoint.
        /// </summary>
        private static Recording TripleResidue(string id, double t0, double t1, double t2)
        {
            return new Recording
            {
                RecordingId = id,
                TrackSections = new List<TrackSection>
                {
                    Physical(t0 - 10.0, t0),
                    Checkpoint(Conic(t0, t1)),
                    Checkpoint(Conic(t0, t2)),
                    Checkpoint(Conic(t1, t2)),
                    Physical(t2, t2 + 10.0)
                },
                OrbitSegments = new List<OrbitSegment>()
            };
        }

        private static List<Finding> Inv2Overlaps(Recording rec)
        {
            var model = new AnalyzerModel
            {
                SaveName = "retire-tests",
                Recordings = new List<Recording> { rec }
            };
            return new Inv2NoDoubleCover().Evaluate(model)
                .Where(f => f.RuleId == Inv2NoDoubleCover.OverlapRuleId)
                .ToList();
        }

        private static List<(double, double)> Union(Recording rec)
        {
            return CheckpointDoubleCoverRetire.CoverageUnion(rec.TrackSections);
        }

        private static string Spans(Recording rec)
        {
            return string.Join(" ", rec.TrackSections.Select(s => string.Format(
                CultureInfo.InvariantCulture, "{0}[{1:R},{2:R}]", s.referenceFrame, s.startUT, s.endUT)));
        }

        // --- the measured triples --------------------------------------------

        public static IEnumerable<object[]> MeasuredTriples()
        {
            // todo INTERBODY-SAVE-CARRIES-INV2-DOUBLE-COVER, the T0/T1/T2 table.
            yield return new object[] { "041770246260406ab85b59495eb51f45", 6737626.584, 6737631.955, 6738050.344 };
            yield return new object[] { "36c7688b8e5141f7809e2d4dbe9dc094", 72353071.380, 72353162.546, 72353168.180 };
            yield return new object[] { "58130506e8f84025b78a95d2497534ab", 6623968.028, 6625335.495, 6625978.889 };
        }

        [Theory]
        [MemberData(nameof(MeasuredTriples))]
        public void MeasuredTriple_RedsInv2BeforeRetire_AndGreenAfter(
            string id, double t0, double t1, double t2)
        {
            Recording rec = TripleResidue(id, t0, t1, t2);
            Assert.NotEmpty(Inv2Overlaps(rec));

            List<(double, double)> before = Union(rec);
            int dropped = CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec);

            Assert.Equal(1, dropped);
            Assert.Empty(Inv2Overlaps(rec));
            Assert.Equal(before, Union(rec));
        }

        [Theory]
        [MemberData(nameof(MeasuredTriples))]
        public void MeasuredTriple_RetiresTheEnvelope_AndKeepsTheFinerTiling(
            string id, double t0, double t1, double t2)
        {
            Recording rec = TripleResidue(id, t0, t1, t2);
            CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec);

            List<TrackSection> checkpoints = rec.TrackSections
                .Where(s => s.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                .ToList();

            Assert.Equal(2, checkpoints.Count);
            Assert.Equal(t0, checkpoints[0].startUT);
            Assert.Equal(t1, checkpoints[0].endUT);
            Assert.Equal(t1, checkpoints[1].startUT);
            Assert.Equal(t2, checkpoints[1].endUT);

            // The per-frame brackets are untouched, in place and in order.
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[0].referenceFrame);
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[3].referenceFrame);
            Assert.Equal(4, rec.TrackSections.Count);
        }

        [Fact]
        public void MeasuredTriple_LogsOneSummaryLineNamingTheDroppedSpan()
        {
            Recording rec = TripleResidue("041770246260406ab85b59495eb51f45",
                6737626.584, 6737631.955, 6738050.344);
            logLines.Clear();

            CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("Checkpoint double-cover retired:")
                && l.Contains("rec=041770246260406ab85b59495eb51f45")
                && l.Contains("dropped=1")
                && l.Contains("kept=4")
                && l.Contains("6738050.344"));
        }

        // --- the shapes it must NOT touch -------------------------------------

        [Fact]
        public void PartialOverlap_IsNeverRetired()
        {
            var rec = new Recording
            {
                RecordingId = "partial",
                TrackSections = new List<TrackSection>
                {
                    Checkpoint(Conic(100.0, 200.0)),
                    Checkpoint(Conic(150.0, 250.0))
                }
            };

            Assert.Empty(CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections));
            Assert.Equal(0, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.Equal(2, rec.TrackSections.Count);
            // The finding stands, correctly: a partial overlap is a different defect and
            // this pass deliberately does not paper over it.
            Assert.NotEmpty(Inv2Overlaps(rec));
        }

        [Fact]
        public void SingleCheckpointSection_IsANoOp()
        {
            var rec = new Recording
            {
                RecordingId = "single",
                TrackSections = new List<TrackSection>
                {
                    Physical(90.0, 100.0),
                    Checkpoint(Conic(100.0, 200.0)),
                    Physical(200.0, 210.0)
                }
            };

            logLines.Clear();
            Assert.Equal(0, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.Equal(3, rec.TrackSections.Count);
            Assert.DoesNotContain(logLines, l => l.Contains("Checkpoint double-cover"));
        }

        [Fact]
        public void InterleavedAbsoluteSections_AreUntouchedEvenWhenTheyCoverTheCheckpointSpan()
        {
            // An Absolute per-frame section spanning the same window is NOT a coverer:
            // it is a different producer's recorded surface, not a duplicate conic. And
            // it is never itself a candidate.
            var rec = new Recording
            {
                RecordingId = "interleaved",
                TrackSections = new List<TrackSection>
                {
                    Physical(100.0, 300.0),
                    Checkpoint(Conic(150.0, 250.0))
                }
            };

            string before = Spans(rec);
            Assert.Equal(0, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.Equal(before, Spans(rec));
        }

        [Fact]
        public void PayloadLessCheckpointShell_IsLeftToTheProducersEmptyShellReconcile()
        {
            TrackSection shell = OrbitSegmentCheckpointBridge.BuildOpenCheckpointSection(100.0);
            shell.endUT = 200.0;

            var rec = new Recording
            {
                RecordingId = "shell",
                TrackSections = new List<TrackSection>
                {
                    Checkpoint(Conic(100.0, 200.0)),
                    shell
                }
            };

            Assert.Empty(CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections));
            Assert.Equal(0, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
        }

        [Fact]
        public void CoveredSpanCarryingADifferentConic_IsNeverRetired()
        {
            // Same span, different orbit: dropping it would lose payload, so it stays and
            // INV2 keeps reporting it.
            var rec = new Recording
            {
                RecordingId = "different-conic",
                TrackSections = new List<TrackSection>
                {
                    Checkpoint(Conic(100.0, 300.0)),
                    Checkpoint(Conic(150.0, 250.0, sma: 1234567.0))
                }
            };

            Assert.Empty(CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections));
        }

        [Fact]
        public void BoundarySeamSection_IsNeverRetired()
        {
            TrackSection seam = Checkpoint(Conic(100.0, 300.0));
            seam.isBoundarySeam = true;

            var rec = new Recording
            {
                RecordingId = "seam",
                TrackSections = new List<TrackSection>
                {
                    Checkpoint(Conic(100.0, 200.0)),
                    seam,
                    Checkpoint(Conic(200.0, 300.0))
                }
            };

            Assert.Empty(CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections));
        }

        // --- the other retirable shapes ---------------------------------------

        [Fact]
        public void EnvelopePlusTwoTiles_RetiresOnlyTheEnvelope()
        {
            var rec = new Recording
            {
                RecordingId = "envelope",
                TrackSections = new List<TrackSection>
                {
                    Checkpoint(Conic(100.0, 200.0)),
                    Checkpoint(Conic(100.0, 300.0)),
                    Checkpoint(Conic(200.0, 300.0))
                }
            };

            List<int> drop = CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections);
            Assert.Equal(new List<int> { 1 }, drop);
        }

        [Fact]
        public void ExactSpanDuplicatePair_RetiresTheSecondOccurrence()
        {
            var rec = new Recording
            {
                RecordingId = "exact-dup",
                TrackSections = new List<TrackSection>
                {
                    Checkpoint(Conic(100.0, 200.0)),
                    Checkpoint(Conic(100.0, 200.0))
                }
            };

            List<int> drop = CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections);
            Assert.Equal(new List<int> { 1 }, drop);

            Assert.Equal(1, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.Single(rec.TrackSections);
            Assert.Empty(Inv2Overlaps(rec));
        }

        [Fact]
        public void MutualCoverCannotRetireEverySection()
        {
            // Three identical sections: exactly two go, never all three - each drop is
            // tested against the sections that are STILL kept.
            var rec = new Recording
            {
                RecordingId = "triple-identical",
                TrackSections = new List<TrackSection>
                {
                    Checkpoint(Conic(100.0, 200.0)),
                    Checkpoint(Conic(100.0, 200.0)),
                    Checkpoint(Conic(100.0, 200.0))
                }
            };

            Assert.Equal(2, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.Single(rec.TrackSections);
            Assert.Equal(100.0, rec.TrackSections[0].startUT);
            Assert.Equal(200.0, rec.TrackSections[0].endUT);
        }

        [Fact]
        public void TilesThatLeaveAGap_KeepTheEnvelopeAndGoThemselves()
        {
            // [100,190] + [200,300] do NOT tile [100,300] - only the envelope covers
            // [190,200]. So the envelope is NOT retirable (dropping it would open a hole),
            // and the two sub-spans are, each being fully contained by it. "Prefer the
            // finer tiling" only applies where a finer TILING exists; with a gap there is
            // none, and containment is what keeps the union still.
            var rec = new Recording
            {
                RecordingId = "gapped-tiles",
                TrackSections = new List<TrackSection>
                {
                    Checkpoint(Conic(100.0, 190.0)),
                    Checkpoint(Conic(100.0, 300.0)),
                    Checkpoint(Conic(200.0, 300.0))
                }
            };

            List<(double, double)> before = Union(rec);
            Assert.Equal(new List<int> { 0, 2 },
                CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections));

            Assert.Equal(2, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.Single(rec.TrackSections);
            Assert.Equal(100.0, rec.TrackSections[0].startUT);
            Assert.Equal(300.0, rec.TrackSections[0].endUT);
            Assert.Equal(before, Union(rec));
            Assert.Empty(Inv2Overlaps(rec));
        }

        // --- determinism / idempotence ----------------------------------------

        [Fact]
        public void SecondPassDropsZero()
        {
            Recording rec = TripleResidue("idempotent", 100.0, 150.0, 400.0);
            Assert.Equal(1, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));

            logLines.Clear();
            Assert.Equal(0, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.Equal(0, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.DoesNotContain(logLines, l => l.Contains("Checkpoint double-cover"));
        }

        [Fact]
        public void TheDecisionIsDeterministicAcrossRepeatedEvaluation()
        {
            Recording rec = TripleResidue("deterministic", 100.0, 150.0, 400.0);
            List<int> first = CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(first,
                    CheckpointDoubleCoverRetire.FindRedundantCheckpointSections(rec.TrackSections));
            }
        }

        // --- consumer safety ---------------------------------------------------

        [Fact]
        public void OptimizerSplitDecisionsAreIdenticalBeforeAndAfterTheRetire()
        {
            // A recording that carries real split-worthy boundaries on both sides of the
            // residue: Atmospheric -> ExoBallistic on the way up, and back down again.
            var rec = new Recording
            {
                RecordingId = "optimizer-invariance",
                TrackSections = new List<TrackSection>
                {
                    Physical(0.0, 400.0, SegmentEnvironment.Atmospheric),
                    Physical(400.0, 800.0),
                    Checkpoint(Conic(800.0, 900.0)),
                    Checkpoint(Conic(800.0, 2000.0)),
                    Checkpoint(Conic(900.0, 2000.0)),
                    Physical(2000.0, 2400.0),
                    Physical(2400.0, 2800.0, SegmentEnvironment.Atmospheric)
                }
            };

            List<double> before = CheckpointDoubleCoverRetire.SplittableBoundaryUTs(rec);
            Assert.NotEmpty(before);

            Assert.Equal(1, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));
            Assert.Equal(before, CheckpointDoubleCoverRetire.SplittableBoundaryUTs(rec));
        }

        [Fact]
        public void RouteProofHashIsUnchangedByTheRetire()
        {
            Recording rec = TripleResidue("route-source", 100.0, 150.0, 400.0);
            rec.RouteConnectionWindows = new List<RouteConnectionWindow>
            {
                new RouteConnectionWindow
                {
                    WindowId = "win-1",
                    DockUT = 120.0,
                    UndockUT = 380.0,
                    TransferTargetVesselPid = 4242u,
                    TransferKind = RouteConnectionKind.DockingPort,
                    TransportPartPersistentIds = new List<uint> { 11u, 22u },
                    EndpointPartPersistentIds = new List<uint> { 33u }
                }
            };

            string before = RouteProofHasher.ComputeRouteProofHashFromRecording(rec);
            Assert.NotEqual(RouteProofHasher.NoRouteProofSentinel, before);
            int sidecarEpochBefore = rec.SidecarEpoch;

            Assert.Equal(1, CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec));

            Assert.Equal(before, RouteProofHasher.ComputeRouteProofHashFromRecording(rec));
            // The whole persistence decision in one assertion: nothing is dirtied, so the
            // next FlushDirtyFiles cannot advance the epoch a route captured as proof.
            Assert.False(rec.FilesDirty);
            Assert.Equal(sidecarEpochBefore, rec.SidecarEpoch);
        }

        // --- the load-time pass -------------------------------------------------

        [Fact]
        public void RetireAcrossRecordings_ReportsOneSummaryAndSkipsCleanRecordings()
        {
            var clean = new Recording
            {
                RecordingId = "clean",
                TrackSections = new List<TrackSection>
                {
                    Physical(0.0, 100.0),
                    Checkpoint(Conic(100.0, 200.0))
                }
            };
            Recording dirty = TripleResidue("dirty", 1000.0, 1100.0, 1400.0);

            logLines.Clear();
            int retired = CheckpointDoubleCoverRetire.RetireAcrossRecordings(
                new List<Recording> { clean, dirty });

            Assert.Equal(1, retired);
            Assert.Contains(logLines, l =>
                l.Contains("Checkpoint double-cover retire pass:")
                && l.Contains("recordings=2")
                && l.Contains("affected=1")
                && l.Contains("droppedSections=1"));
            Assert.DoesNotContain(logLines, l => l.Contains("rec=clean"));
        }

        [Fact]
        public void RetireAcrossRecordings_LogsNothingWhenEveryRecordingIsClean()
        {
            var clean = new Recording
            {
                RecordingId = "clean",
                TrackSections = new List<TrackSection>
                {
                    Physical(0.0, 100.0),
                    Checkpoint(Conic(100.0, 200.0)),
                    Physical(200.0, 300.0)
                }
            };

            logLines.Clear();
            Assert.Equal(0, CheckpointDoubleCoverRetire.RetireAcrossRecordings(
                new List<Recording> { clean }));
            Assert.DoesNotContain(logLines, l => l.Contains("Checkpoint double-cover"));
        }

        /// <summary>
        /// The OnLoad wiring, read from the source rather than asserted about a method the
        /// headless suite cannot call: ParsekScenario.OnLoad must invoke the retire AFTER
        /// LoadTimeSweep.Run() on BOTH of its branches (the FLIGHT-&gt;FLIGHT scene-change
        /// branch and the cold-start branch).
        /// </summary>
        [Fact]
        public void ParsekScenarioOnLoad_CallsTheRetireAfterTheLoadTimeSweep_OnBothBranches()
        {
            string path = Path.Combine(RepoRoot(), "Source", "Parsek", "ParsekScenario.cs");
            Assert.True(File.Exists(path), "ParsekScenario.cs must exist at " + path);
            string[] lines = File.ReadAllLines(path);

            var sweepLines = new List<int>();
            var retireLines = new List<int>();
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t == "LoadTimeSweep.Run();")
                    sweepLines.Add(i);
                if (t == "RetireCheckpointDoubleCoverOnLoad();")
                    retireLines.Add(i);
            }

            Assert.Equal(2, sweepLines.Count);
            Assert.Equal(2, retireLines.Count);
            for (int b = 0; b < 2; b++)
            {
                Assert.True(retireLines[b] > sweepLines[b],
                    "the retire must run after the load-time sweep on branch " + b);
                Assert.True(retireLines[b] - sweepLines[b] < 10,
                    "the retire must sit in the same OnLoad step block as the sweep on branch " + b);
            }
        }

        // --- the committed fixture corpus ---------------------------------------

        internal static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }

        /// <summary>
        /// Drive the pure decision over every committed `.prec.txt` in the harness fixture
        /// corpus, through the PRODUCTION text codec.
        ///
        /// <para>THE EXPECTED DROP COUNT IS ZERO, and that is the point: `duna-one-recorded`,
        /// `depot-route-recorded` and `interbody-route-recorded` were each repaired at
        /// BUILD time by the shared containment dedupe in
        /// `harness/tools/build_duna_one_recorded.py` (the before/after analyzer readings
        /// live in `test_saveparse.RECORDED_FIXTURES`), so the committed bytes are already
        /// clean. This cell is the tripwire in the other direction: a producer change that
        /// starts re-creating the shape, or a re-harvest that lands unrepaired bytes, reds
        /// here with the offending save named. Fixture bytes are never edited by this
        /// test.</para>
        /// </summary>
        [Fact]
        public void FixtureCorpus_CarriesNoRetirableResidue_AndTheCoverageUnionNeverMoves()
        {
            string savesRoot = Path.Combine(RepoRoot(), "harness", "fixtures", "saves");
            Assert.True(Directory.Exists(savesRoot), "fixture corpus must exist at " + savesRoot);

            int sidecars = 0;
            int checkpointSections = 0;
            var drops = new List<string>();

            foreach (string saveDir in Directory.GetDirectories(savesRoot)
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
                if (!Directory.Exists(recordingsDir))
                    continue;

                foreach (string file in Directory.GetFiles(recordingsDir, "*.prec.txt")
                             .OrderBy(x => x, StringComparer.Ordinal))
                {
                    ConfigNode node = ConfigNode.Load(file);
                    if (node == null)
                        continue;

                    var rec = new Recording
                    {
                        RecordingId = Path.GetFileName(file).Replace(".prec.txt", string.Empty)
                    };
                    TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec);
                    if (rec.TrackSections == null || rec.TrackSections.Count == 0)
                        continue;

                    sidecars++;
                    checkpointSections += rec.TrackSections.Count(
                        s => s.referenceFrame == ReferenceFrame.OrbitalCheckpoint);

                    List<(double, double)> before = Union(rec);
                    int dropped = CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec);
                    // The union invariant is asserted on EVERY recording, retired or not.
                    Assert.Equal(before, Union(rec));

                    if (dropped > 0)
                    {
                        drops.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0}/{1} dropped={2}",
                            Path.GetFileName(saveDir), rec.RecordingId, dropped));
                    }
                }
            }

            // Corpus size floor: a path bug that silently read nothing must not pass.
            Assert.True(sidecars >= 200, "expected the whole corpus, read " + sidecars + " sidecars");
            Assert.True(checkpointSections >= 700,
                "expected the corpus's checkpoint sections, read " + checkpointSections);
            Assert.True(drops.Count == 0,
                "committed fixture bytes carry retirable checkpoint double-cover residue: "
                + string.Join(" | ", drops));
        }
    }
}
