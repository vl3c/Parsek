using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// In-game canary for the Phase 4 cross-parent periodicity extractor (plan section 8.4). It
    /// hand-rolls a minimal Kerbin -> Sun -> Duna recording (body-name strings only; no part
    /// snapshots), runs the SAME builders + extractor the loop builder uses, and drives them against
    /// the LIVE Kerbol body graph via <see cref="FlightGlobalsBodyInfo"/>. This validates end-to-end
    /// that the seam reads the live Kerbin/Sun/Duna periods + reference-body hierarchy and the
    /// extractor resolves an interplanetary mission to Supported with a buildable schedule - which the
    /// fake-IBodyInfo xUnit tests cannot (they never touch FlightGlobals or the player's planet pack).
    /// </summary>
    public class CrossParentPeriodicityInGameTest
    {
        // A controlled member recording with one surface/atmospheric section (the ascent/launch body).
        private static Recording SurfaceLeg(string id, double start, double end, string body)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "CP",
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                StartBodyName = body,
                SegmentBodyName = body
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = start,
                endUT = end,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = start, bodyName = body, rotation = Quaternion.identity },
                    new TrajectoryPoint { ut = end, bodyName = body, rotation = Quaternion.identity }
                },
                checkpoints = new List<OrbitSegment>()
            });
            return rec;
        }

        // Adds a flat OrbitSegment (and matching checkpoint section) on a body - an orbit/SOI leg.
        private static void AddOrbit(Recording rec, double start, double end, string body)
        {
            var seg = new OrbitSegment
            {
                startUT = start,
                endUT = end,
                bodyName = body,
                semiMajorAxis = 700000.0
            };
            rec.OrbitSegments.Add(seg);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                source = TrackSectionSource.Checkpoint,
                startUT = start,
                endUT = end,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment> { seg }
            });
            if (end > rec.ExplicitEndUT)
                rec.ExplicitEndUT = end;
        }

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Kerbin->Duna cross-parent extractor resolves to Supported with a schedule, against live bodies")]
        public void CrossParent_KerbinToDuna_ResolvesSupportedWithSchedule()
        {
            // Only meaningful if the live system actually has Kerbin + Duna both orbiting the Sun
            // (stock / a pack that keeps those names). Skip cleanly otherwise rather than fail.
            IBodyInfo bodies = FlightGlobalsBodyInfo.Instance;
            string kerbinParent = bodies.ReferenceBodyName("Kerbin");
            string dunaParent = bodies.ReferenceBodyName("Duna");
            if (string.IsNullOrEmpty(kerbinParent) || string.IsNullOrEmpty(dunaParent))
            {
                InGameAssert.Fail("live body graph missing Kerbin/Duna reference bodies (non-stock?)");
                return;
            }

            // Kerbin ascent -> Kerbin orbit -> heliocentric (Sun) coast -> Duna SOI.
            var ascent = SurfaceLeg("cp_s", 1000.0, 1100.0, "Kerbin");
            var transfer = new Recording
            {
                RecordingId = "cp_o",
                VesselName = "CP",
                IsDebris = false,
                ExplicitStartUT = 1100.0,
                ExplicitEndUT = 1100.0,
                StartBodyName = "Kerbin",
                SegmentBodyName = "Kerbin"
            };
            AddOrbit(transfer, 1100.0, 5000.0, "Kerbin");
            AddOrbit(transfer, 5000.0, 1000000.0, "Sun");
            AddOrbit(transfer, 1000000.0, 1005000.0, "Duna");
            ascent.ChainId = "CP"; ascent.ChainIndex = 0;
            transfer.ChainId = "CP"; transfer.ChainIndex = 1;

            var tree = new RecordingTree { Id = "cp_tree", RootRecordingId = ascent.RecordingId };
            tree.Recordings[ascent.RecordingId] = ascent;
            tree.Recordings[transfer.RecordingId] = transfer;
            var committed = new List<Recording> { ascent, transfer };

            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            List<MissionCompositionNode> compRoots = MissionCompositionBuilder.Build(structure);

            bool prevSuppress = MissionPeriodicity.SuppressLogging;
            MissionPeriodicity.SuppressLogging = true;
            try
            {
                ConstraintExtraction ex = MissionPeriodicity.ExtractConstraints(
                    view, compRoots, committed, new HashSet<string>(), bodies);

                InGameAssert.AreEqual(Support.Supported, ex.Support,
                    "cross-parent Kerbin->Duna should resolve to Supported against the live bodies");
                InGameAssert.AreEqual("Kerbin", ex.LaunchBodyName, "launch body should be Kerbin");
                // Expect the pad rotation + Duna heliocentric Orbital + Kerbin heliocentric Orbital.
                InGameAssert.IsTrue(ex.Constraints.Count >= 3,
                    "expected at least 3 constraints (Rotation(Kerbin) + Orbital(Duna) + Orbital(Kerbin))");
                bool hasDuna = false, hasKerbinHelio = false;
                foreach (PhaseConstraint c in ex.Constraints)
                {
                    if (c.Kind == ConstraintKind.Orbital && c.BodyName == "Duna" && c.RelativeToParent)
                        hasDuna = true;
                    if (c.Kind == ConstraintKind.Orbital && c.BodyName == "Kerbin" && c.RelativeToParent)
                        hasKerbinHelio = true;
                }
                InGameAssert.IsTrue(hasDuna, "expected a cross-parent Orbital(Duna)");
                InGameAssert.IsTrue(hasKerbinHelio, "expected the launch body's heliocentric Orbital(Kerbin)");

                // The zero-drift schedule must build (it is a drifting multi-constraint config) and
                // resolve a finite first launch (bounded-best if no within-tolerance window in range).
                bool built = MissionPeriodicity.TryBuildRelaunchSchedule(
                    ex.Constraints, ex.Support, ex.UT0, ex.UT0, bodies,
                    out MissionRelaunchSchedule schedule, 0.0, ex.LaunchBodyName);
                InGameAssert.IsTrue(built, "the cross-parent schedule must build");
                InGameAssert.IsNotNull(schedule, "schedule must not be null");
                InGameAssert.IsTrue(!double.IsNaN(schedule.FirstLaunchUT) && !double.IsInfinity(schedule.FirstLaunchUT),
                    "the first scheduled launch UT must be finite");
            }
            finally
            {
                MissionPeriodicity.SuppressLogging = prevSuppress;
            }
        }
    }
}
