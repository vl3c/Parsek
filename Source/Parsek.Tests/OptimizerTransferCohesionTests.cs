using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// OPTIMIZER-SPLIT-DEFEATS-REAIM-CLASSIFIER, Phase 0: pin the measurement
    /// through the REAL optimizer code before anything about it changes.
    ///
    /// The finding (V9-dres-player-loop, runs 2026-08-12_0150/_0153): the
    /// committed Kerbin->Sun->Dres recording classifies FAITHFUL because the
    /// load-time optimizer splits it, leaving no member that holds parking +
    /// heliocentric coast + arrival. These cells establish EXACTLY which
    /// boundary splits and why, so the Phase-1 predicate change can be shown to
    /// move that boundary and nothing else.
    ///
    /// The plan's measurements came from a standalone predicate mirror. These
    /// cells re-derive them through `RecordingOptimizer` itself, which is the
    /// point: a mirror that agrees with itself proves nothing.
    ///
    /// Fixture paths follow the CLAUDE.md rule -- xUnit runs from
    /// Source/Parsek.Tests/bin/Debug/net472/, so five '..' segments reach the
    /// repo root.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerTransferCohesionTests : IDisposable
    {
        // The two fixture mains the plan measured, by recording id.
        private const string DresMainId = "bc4a3a6d361549d2a7cdd9d4eb5574c1";
        private const string EveMainId = "75a6ab25a0f445219a82b7b841e44ba8";

        public OptimizerTransferCohesionTests()
        {
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        // ---------------------------------------------------------------
        // Fixture loading
        // ---------------------------------------------------------------

        internal static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }

        internal static string PrecPath(string fixture, string recordingId)
        {
            return Path.Combine(RepoRoot(), "harness", "fixtures", "saves", fixture,
                "Parsek", "Recordings", recordingId + ".prec.txt");
        }

        /// <summary>
        /// Load a committed `.prec.txt` sidecar into a Recording through the
        /// PRODUCTION text codec -- not a hand-rolled parser, so the sections
        /// this test reasons about are byte-for-byte the ones the game builds.
        /// </summary>
        internal static Recording LoadFixtureRecording(string fixture, string recordingId)
        {
            string path = PrecPath(fixture, recordingId);
            Assert.True(File.Exists(path), $"committed fixture sidecar must exist at {path}");

            ConfigNode node = ConfigNode.Load(path);
            Assert.NotNull(node);

            var rec = new Recording { RecordingId = recordingId };
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec);
            return rec;
        }

        /// <summary>Load every committed recording sidecar of a fixture.</summary>
        internal static List<Recording> LoadWholeFixture(string fixture)
        {
            string dir = Path.Combine(RepoRoot(), "harness", "fixtures", "saves", fixture,
                "Parsek", "Recordings");
            Assert.True(Directory.Exists(dir), "fixture recordings dir must exist at " + dir);

            var recs = new List<Recording>();
            foreach (string f in Directory.GetFiles(dir, "*.prec.txt").OrderBy(x => x, StringComparer.Ordinal))
            {
                ConfigNode node = ConfigNode.Load(f);
                if (node == null) continue;
                var rec = new Recording
                {
                    RecordingId = Path.GetFileName(f).Replace(".prec.txt", string.Empty)
                };
                TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec);
                recs.Add(rec);
            }
            return recs;
        }

        /// <summary>A compact, greppable description of a boundary.</summary>
        private static string Describe(Recording rec, int i)
        {
            TrackSection a = rec.TrackSections[i];
            TrackSection b = rec.TrackSections[i + 1];
            return string.Format(CultureInfo.InvariantCulture,
                "{0}->{1} @ut {2:F1} : {3}({4})/{5} -> {6}({7})/{8}",
                i, i + 1, b.startUT,
                a.environment, a.referenceFrame, SectionBody(a),
                b.environment, b.referenceFrame, SectionBody(b));
        }

        private static string SectionBody(TrackSection s)
        {
            if (s.checkpoints != null && s.checkpoints.Count > 0) return s.checkpoints[0].bodyName;
            if (s.frames != null && s.frames.Count > 0) return s.frames[0].bodyName;
            return "?";
        }

        /// <summary>
        /// Every boundary the §3 predicate calls splittable, with its reason --
        /// the raw classification, BEFORE the optimizer's own acceptance rules
        /// (minimum-half-length, ghost triggers) filter it.
        /// </summary>
        private static List<(int Index, RecordingOptimizer.SplitBoundaryReason Reason)>
            ClassifyBoundaries(Recording rec)
        {
            var outp = new List<(int, RecordingOptimizer.SplitBoundaryReason)>();
            for (int i = 0; i + 1 < rec.TrackSections.Count; i++)
            {
                RecordingOptimizer.SplitBoundaryReason reason;
                // The predicate indexes the SECOND section of the pair.
                bool splittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, i + 1, out reason);
                if (splittable || reason == RecordingOptimizer.SplitBoundaryReason.SuppressedExoCoastBodyChange)
                    outp.Add((i, reason));
            }
            return outp;
        }

        // ===============================================================
        // E1 - where the Dres main actually splits, and which rule fires
        // ===============================================================
        //
        // METHOD NOTE, and it corrects a detail of the plan. The plan's mirror
        // said FindSplitCandidatesForOptimizer "accepts two" candidates on the
        // Dres main. It does not, and it never could: the production consumer
        // (RecordingStore.RunOptimizationSplitPass) is a while-loop that calls
        // the finder, splits ONE chosen candidate, and re-scans. So a single
        // call returning one candidate is correct, and the measured "+2" is the
        // loop's cumulative result. These cells therefore assert the PREDICATE
        // classification of every boundary -- the load-bearing fact, which
        // matches the plan's table exactly -- and drive the real loop for the
        // end-to-end count.

        /// <summary>Every boundary the section-3 predicate has an opinion about.</summary>
        private static List<(int Index, bool Split, RecordingOptimizer.SplitBoundaryReason Reason)>
            AllClassifiedBoundaries(Recording rec)
        {
            var outp = new List<(int, bool, RecordingOptimizer.SplitBoundaryReason)>();
            for (int i = 1; i < rec.TrackSections.Count; i++)
            {
                RecordingOptimizer.SplitBoundaryReason reason;
                bool split = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, i, out reason);
                if (reason == RecordingOptimizer.SplitBoundaryReason.NotABoundary) continue;
                outp.Add((i - 1, split, reason));
            }
            return outp;
        }

        [Fact]
        public void E1_DresMain_TheKerbinToSunHandoffIsKeptCohesive()
        {
            Recording dres = LoadFixtureRecording("dres-orbit-recorded", DresMainId);
            Assert.Equal(57, dres.TrackSections.Count);

            var classified = AllClassifiedBoundaries(dres);
            string detail = string.Join(" || ", classified.Select(c =>
                Describe(dres, c.Index) + " split=" + c.Split + " reason=" + c.Reason));

            // Exactly the plan's table: four boundaries with an opinion.
            Assert.True(classified.Count == 4,
                "expected 4 classified boundaries on the Dres main, got "
                + classified.Count + ": " + detail);

            // POST-FIX: the on-rails Kerbin->Sun handoff is now KEPT COHESIVE.
            // Before Phase 1 this boundary classified BodyChange and split the
            // interplanetary recording in two; it is the whole defect, and the
            // assertion is inverted here rather than deleted so the file still
            // names the boundary that moved.
            var body = classified.Single(c =>
                c.Reason == RecordingOptimizer.SplitBoundaryReason.SuppressedExoCoastBodyChange
                && SectionBody(dres.TrackSections[c.Index]) == "Kerbin");
            TrackSection b0 = dres.TrackSections[body.Index];
            TrackSection b1 = dres.TrackSections[body.Index + 1];
            Assert.False(body.Split);
            Assert.Equal(SegmentEnvironment.ExoPropulsive, b0.environment);
            Assert.Equal(SegmentEnvironment.ExoBallistic, b1.environment);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, b0.referenceFrame);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, b1.referenceFrame);
            Assert.Equal("Kerbin", SectionBody(b0));
            Assert.Equal("Sun", SectionBody(b1));
            Assert.True(Math.Abs(b1.startUT - 8490936.2) < 5.0,
                "the body split must sit at the on-rails Kerbin->Sun handoff ut ~8,490,936.2, got "
                + b1.startUT.ToString("F1", CultureInfo.InvariantCulture));
        }

        [Fact]
        public void E1_DresMain_SunToDresCrossingIsAlreadySuppressed()
        {
            // The correction to the filed record: only ONE body boundary splits.
            // Sun->Dres presents ExoBallistic|ExoBallistic and rule 3 suppresses it,
            // so member#2 held the Sun AND Dres legs together all along.
            Recording dres = LoadFixtureRecording("dres-orbit-recorded", DresMainId);

            var suppressed = AllClassifiedBoundaries(dres)
                .Where(c => c.Reason == RecordingOptimizer.SplitBoundaryReason.SuppressedExoCoastBodyChange)
                .ToList();

            // POST-FIX there are TWO rule-3 suppressions: the Sun->Dres crossing
            // (which was always suppressed) and the Kerbin->Sun handoff (which
            // Phase 1 moved). Both legs of the transfer now stay in one recording.
            Assert.True(suppressed.Count == 2,
                "expected two rule-3 suppressions (Kerbin->Sun and Sun->Dres), got " + suppressed.Count);
            Assert.All(suppressed, s => Assert.False(s.Split));

            var bodies = suppressed
                .Select(s => SectionBody(dres.TrackSections[s.Index]) + "->"
                             + SectionBody(dres.TrackSections[s.Index + 1]))
                .ToList();
            Assert.Contains("Kerbin->Sun", bodies);
            Assert.Contains("Sun->Dres", bodies);
        }

        [Fact]
        public void E1_DresMain_OnlyTheAscentSplitIsAcceptedNow()
        {
            // WHY NOT AN END-TO-END SPLIT COUNT HERE: RecordingOptimizer.SplitAtSection
            // calls UnityEngine.Quaternion.Slerp, a Unity ECall that throws
            // SecurityException under headless xUnit, so the 5 -> 7 result cannot be
            // driven in this suite. The IN-GAME truth for that number is the V9 lane
            // (it measured exactly 7), and Phase 3 re-measures it as 6.
            //
            // What IS checkable headlessly is the part that decides it: both
            // boundaries pass the acceptance filter the production loop applies, so
            // the loop takes two splits. The ascent boundary and the body boundary
            // are accepted; the SurfaceInvolved boundary at ut 31.0 is REJECTED by
            // the minimum-half-length floor, which is why the total is 2 and not 3.
            Recording dres = LoadFixtureRecording("dres-orbit-recorded", DresMainId);

            var classified = AllClassifiedBoundaries(dres);
            var splittable = classified.Where(c => c.Split).ToList();

            // POST-FIX: two splittable boundaries, not three -- the body change is
            // gone from this list entirely.
            Assert.True(splittable.Count == 2,
                "expected 2 splittable boundaries (surface, ascent), got " + splittable.Count);
            Assert.DoesNotContain(splittable, c =>
                c.Reason == RecordingOptimizer.SplitBoundaryReason.BodyChange);

            var accepted = splittable
                .Where(c => RecordingOptimizer.CanAutoSplitIgnoringGhostTriggers(dres, c.Index + 1))
                .ToList();

            string detail = string.Join(" || ", accepted.Select(c =>
                Describe(dres, c.Index) + " reason=" + c.Reason));

            // Exactly ONE accepted split now: the ordinary ascent env split, the
            // same "+1" every other fixture takes. That is the 7 -> 6 the V9 lane
            // must measure in game.
            Assert.True(accepted.Count == 1,
                "exactly 1 boundary must be accepted post-fix (the ascent split), got "
                + accepted.Count + ": " + detail);
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.PersistedPhaseChange,
                accepted[0].Reason);

            // And the surface boundary still loses on the minimum-half floor.
            var rejected = splittable.Except(accepted).Single();
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.SurfaceInvolved, rejected.Reason);
        }

        [Fact]
        public void E1_EveMain_HasNoBodySplitAtAll()
        {
            // The contrast fixture: V8's ENGAGED unit. All four of its crossings
            // present ExoBallistic|ExoBallistic, so rule 3 suppresses every one and
            // the surviving member holds parking + coast + arrival. That is WHY V8
            // classifies ENGAGED and V9 did not.
            Recording eve = LoadFixtureRecording("eve-orbit-recorded", EveMainId);

            var classified = AllClassifiedBoundaries(eve);
            var bodySplits = classified
                .Where(c => c.Reason == RecordingOptimizer.SplitBoundaryReason.BodyChange)
                .ToList();

            Assert.True(bodySplits.Count == 0,
                "the Eve main must have NO rule-4 body split; found: "
                + string.Join(" || ", bodySplits.Select(s => Describe(eve, s.Index))));

            int suppressedCrossings = classified
                .Count(c => c.Reason == RecordingOptimizer.SplitBoundaryReason.SuppressedExoCoastBodyChange);
            Assert.True(suppressedCrossings == 4,
                "expected Eve's four crossings to be rule-3 suppressed, got " + suppressedCrossings);
        }

        // ===============================================================
        // E2 - THE PREMISE: does cohesion actually buy a Supported plan?
        // ===============================================================
        //
        // This is the gate the whole fix rests on. Restoring cohesion is only
        // worth doing if the UNSPLIT chain then classifies Supported; if it
        // still declines, the binding constraint is somewhere else and the
        // optimizer change would be pointless (the plan's stop rule).
        //
        // The stub carries only the parentage the classifier reads. Real
        // KSP bodies are not available headlessly and are not needed: the
        // classifier's Supported test is about SHAPE (a strict-ancestor leg, a
        // parking orbit, a direct-child arrival), not about gravity.

        private sealed class SolBodies : IBodyInfo
        {
            private static readonly Dictionary<string, string> Parents =
                new Dictionary<string, string>
                {
                    { "Sun", null },
                    { "Kerbin", "Sun" },
                    { "Mun", "Kerbin" },
                    { "Minmus", "Kerbin" },
                    { "Dres", "Sun" },
                    { "Eve", "Sun" },
                    { "Duna", "Sun" },
                };

            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b)
                => Parents.TryGetValue(b, out string parent) ? parent : null;
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b) => double.NaN;
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        [Fact]
        public void E2_UnsplitDresChain_ClassifiesSupportedWithDresAsTheTarget()
        {
            Recording dres = LoadFixtureRecording("dres-orbit-recorded", DresMainId);

            // The whole recording's segments -- i.e. exactly what the classifier
            // would see if the optimizer had NOT split the chain in two.
            List<OrbitSegment> segs = dres.OrbitSegments;
            Assert.NotNull(segs);
            int nonPredicted = segs.Count(s => !s.isPredicted && !string.IsNullOrEmpty(s.bodyName));
            Assert.True(nonPredicted == 19,
                "the unsplit Dres chain must carry the measured 19 non-predicted segments, got "
                + nonPredicted);

            ReaimMissionPlan plan = ReaimClassifier.Classify(segs, new SolBodies());

            Assert.True(plan.Supported,
                "COHESION MUST BE SUFFICIENT -- if this declines, the fix direction pivots to "
                + "classifier work (Design C) and the optimizer change is pointless. Reason: "
                + (plan.Reason ?? "(none)"));
            Assert.Equal("Kerbin", plan.LaunchBody);
            Assert.Equal("Dres", plan.TargetBody);
            Assert.Equal("Sun", plan.CommonAncestor);

            // The departure is the on-rails Kerbin SOI exit -- the very boundary
            // the optimizer splits at today.
            Assert.True(Math.Abs(plan.RecordedDepartureUT - 8490936.2) < 5.0,
                "departure must be the Kerbin SOI exit ut ~8,490,936.2, got "
                + plan.RecordedDepartureUT.ToString("F1", CultureInfo.InvariantCulture));
            Assert.True(Math.Abs(plan.RecordedTransferTofSeconds - 11885902.0) < 5000.0,
                "tof must be ~11,885,902 s, got "
                + plan.RecordedTransferTofSeconds.ToString("F1", CultureInfo.InvariantCulture));
        }

        [Fact]
        public void E2_TheSplitHalvesAreWhatDecline_NotTheChain()
        {
            // The counterpart, and the actual statement of the defect: feed the
            // classifier the SAME data cut at the body boundary and both halves
            // decline with the reason V9 logged in game.
            Recording dres = LoadFixtureRecording("dres-orbit-recorded", DresMainId);
            const double SplitUT = 8490936.2;

            var head = dres.OrbitSegments.Where(s => s.startUT < SplitUT).ToList();
            var tail = dres.OrbitSegments.Where(s => s.startUT >= SplitUT).ToList();
            Assert.True(head.Count > 0 && tail.Count > 0);

            ReaimMissionPlan headPlan = ReaimClassifier.Classify(head, new SolBodies());
            Assert.False(headPlan.Supported);
            Assert.Contains("heliocentric", headPlan.Reason ?? string.Empty);

            // The tail starts AT the Sun, so it has no launch-body parking orbit
            // to depart from -- it declines too, which is why V9 read
            // transferMemberSegs=0 rather than "one good member".
            ReaimMissionPlan tailPlan = ReaimClassifier.Classify(tail, new SolBodies());
            Assert.False(tailPlan.Supported);
        }

        // ===============================================================
        // E3 - the predicate, in isolation, on synthetic sections
        // ===============================================================
        //
        // These are the cells Phase 1 flips. They state the rule change as a
        // one-boundary-class statement: checkpoint-framed Exo body changes stop
        // splitting; PHYSICS-FRAME (Absolute) ones keep splitting, forever.

        private static TrackSection Section(
            SegmentEnvironment env, ReferenceFrame frame, string body, double startUT, double endUT)
        {
            var s = new TrackSection
            {
                environment = env,
                referenceFrame = frame,
                startUT = startUT,
                endUT = endUT,
                // `checkpoints` is the OrbitSegment list the optimizer reads the
                // section body from (RecordingOptimizer.GetSectionBody).
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = startUT, endUT = endUT, bodyName = body }
                }
            };
            return s;
        }

        [Fact]
        public void E3_CheckpointFramedExoBodyChange_IsNowKeptCohesive()
        {
            // THE PHASE-1 CHANGE, stated as one boundary class. Before the fix this
            // asserted SPLIT / BodyChange (an ExoPropulsive label on a packed
            // section defeated rule 3's raw-env test); it now suppresses, because
            // both sides are checkpoint-framed and a packed vessel cannot thrust.
            var sections = new List<TrackSection>
            {
                Section(SegmentEnvironment.ExoPropulsive, ReferenceFrame.OrbitalCheckpoint, "Kerbin", 100, 200),
                Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, "Sun", 200, 300),
            };

            RecordingOptimizer.SplitBoundaryReason reason;
            var rec = new Recording { RecordingId = "synthetic", TrackSections = sections };
            bool splittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out reason);

            Assert.False(splittable);
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.SuppressedExoCoastBodyChange, reason);
        }

        [Fact]
        public void E3_MixedFraming_StillSplits()
        {
            // The rule needs BOTH sides on rails. One checkpoint side and one
            // physics side is not an on-rails traversal, so it keeps splitting --
            // this is what stops the widening from leaking into partially-packed
            // shapes.
            var sections = new List<TrackSection>
            {
                Section(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, "Kerbin", 100, 200),
                Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, "Sun", 200, 300),
            };

            RecordingOptimizer.SplitBoundaryReason reason;
            var rec = new Recording { RecordingId = "synthetic-mixed", TrackSections = sections };
            bool splittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out reason);

            Assert.True(splittable);
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.BodyChange, reason);
        }

        [Fact]
        public void E3_PhysicsFramedExoBodyChange_SplitsAndMustKeepSplitting()
        {
            // THE PRESERVED CONTRACT, and after Phase 1 it is the ONLY form of
            // Exo-class body change that still splits. A genuine powered SOI
            // crossing recorded in the PHYSICS frame is a real gameplay event at
            // the boundary -- the calibration row "SOI traversal while burning ->
            // split". Widening rule 3 must never reach it.
            var sections = new List<TrackSection>
            {
                Section(SegmentEnvironment.ExoPropulsive, ReferenceFrame.Absolute, "Kerbin", 100, 200),
                Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute, "Sun", 200, 300),
            };

            RecordingOptimizer.SplitBoundaryReason reason;
            var rec = new Recording { RecordingId = "synthetic", TrackSections = sections };
            bool splittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out reason);

            Assert.True(splittable);
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.BodyChange, reason);
        }

        [Fact]
        public void E3_BallisticCheckpointBodyChange_IsSuppressedTodayAndStaysSuppressed()
        {
            // Today's rule-3 case, unchanged by Phase 1.
            var sections = new List<TrackSection>
            {
                Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, "Sun", 100, 200),
                Section(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint, "Dres", 200, 300),
            };

            RecordingOptimizer.SplitBoundaryReason reason;
            var rec = new Recording { RecordingId = "synthetic", TrackSections = sections };
            bool splittable = RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out reason);

            Assert.False(splittable);
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.SuppressedExoCoastBodyChange, reason);
        }

        // ===============================================================
        // E4 - fixture-inventory sweep (blast-radius ground truth)
        // ===============================================================

        [Fact]
        public void E4_FixtureInventory_NoBodySplitSurvivesAnywhere()
        {
            string savesRoot = Path.Combine(RepoRoot(), "harness", "fixtures", "saves");
            Assert.True(Directory.Exists(savesRoot), $"fixture root must exist at {savesRoot}");

            var bodySplits = new List<string>();
            int sidecarsScanned = 0;

            foreach (string precTxt in Directory.GetFiles(savesRoot, "*.prec.txt", SearchOption.AllDirectories))
            {
                ConfigNode node;
                try { node = ConfigNode.Load(precTxt); }
                catch { continue; }
                if (node == null) continue;

                var rec = new Recording { RecordingId = Path.GetFileName(precTxt) };
                try { TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec); }
                catch { continue; }
                if (rec.TrackSections == null || rec.TrackSections.Count < 2) continue;
                sidecarsScanned++;

                foreach (var b in ClassifyBoundaries(rec))
                {
                    if (b.Reason == RecordingOptimizer.SplitBoundaryReason.BodyChange)
                        bodySplits.Add(Path.GetFileName(precTxt) + " :: " + Describe(rec, b.Index));
                }
            }

            Assert.True(sidecarsScanned > 0, "the sweep must have scanned at least one sidecar");

            // PRE-FIX this found exactly one rule-4 body split in the whole
            // inventory -- the Dres Kerbin->Sun handoff -- which was the measured
            // blast radius of Phase 1. POST-FIX there are NONE, and that pair of
            // readings is the blast-radius proof: the only boundary that moved is
            // the one the fix targeted, on the one fixture that had it.
            Assert.True(bodySplits.Count == 0,
                "expected NO rule-4 body split in the fixture inventory after the cohesion fix, "
                + "found " + bodySplits.Count + " after scanning " + sidecarsScanned
                + " sidecars: " + string.Join(" || ", bodySplits));
        }
    }
}
