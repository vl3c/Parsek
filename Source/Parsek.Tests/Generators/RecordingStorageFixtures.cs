using System.Collections.Generic;
using UnityEngine;

namespace Parsek.Tests.Generators
{
    public static class RecordingStorageFixtures
    {
        public sealed class FixtureCase
        {
            internal FixtureCase(string name, RecordingBuilder builder)
            {
                Name = name;
                Builder = builder;
            }

            public string Name { get; }
            public RecordingBuilder Builder { get; }

            public override string ToString() => Name;
        }

        internal static IEnumerable<object[]> RepresentativeCases()
        {
            yield return new object[] { AtmosphericActiveMultiSection() };
            yield return new object[] { OrbitalCheckpointTransition() };
            yield return new object[] { MixedActiveBackground() };
            yield return new object[] { OptimizerBoundarySeed() };
        }

        internal static Recording MaterializeTrajectory(RecordingBuilder builder)
        {
            var rec = new Recording
            {
                RecordingId = builder.GetRecordingId(),
                RecordingFormatVersion = builder.GetFormatVersion(),
                VesselName = builder.GetVesselName()
            };

            RecordingStore.DeserializeTrajectoryFrom(builder.BuildTrajectoryNode(), rec);
            rec.VesselSnapshot = builder.GetVesselSnapshot()?.CreateCopy();
            rec.GhostVisualSnapshot = builder.GetGhostVisualSnapshot()?.CreateCopy();
            rec.GhostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(rec);
            return rec;
        }

        internal static FixtureCase AtmosphericActiveMultiSection()
        {
            const double t0 = 17000.0;
            TrajectoryPoint sharedBoundary = MakePoint(t0 + 20, -0.0940, -74.5512, 900.0,
                velY: 145f, rotZ: 0.22f, rotW: 0.97f, funds: 32000.0);

            var builder = new RecordingBuilder("Fixture Atmospheric Ascent")
                .WithRecordingId("fixture-atmo-boundary")
                .WithFormatVersion(RecordingStore.CurrentRecordingFormatVersion)
                .AddPoint(t0 + 0, -0.0972, -74.5575, 77.0, funds: 31000.0)
                .AddPoint(t0 + 10, -0.0963, -74.5550, 320.0, funds: 31000.0)
                .AddPoint(sharedBoundary.ut, sharedBoundary.latitude, sharedBoundary.longitude, sharedBoundary.altitude,
                    funds: sharedBoundary.funds)
                .AddPoint(t0 + 30, -0.0905, -74.5450, 1750.0, funds: 32000.0, science: 2.5f)
                .AddPoint(t0 + 40, -0.0860, -74.5380, 2600.0, funds: 32000.0, science: 2.5f)
                .AddPartEvent(t0 + 10, 410001u, (int)PartEventType.EngineIgnited, "liquidEngine", value: 1f)
                .AddPartEvent(t0 + 30, 410002u, (int)PartEventType.LightOn, "navLight")
                .AddFlagEvent(t0 + 40, "Ascent Marker", "Valentina Kerman",
                    "storage fixture", "Squad/Flags/default",
                    -0.0860, -74.5380, 2600.0, body: "Kerbin")
                .AddSegmentEvent(SegmentEventType.ControllerChange, t0 + 20, "gravity turn")
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    startUT = t0,
                    endUT = t0 + 20,
                    sampleRateHz = 10.0f,
                    boundaryDiscontinuityMeters = 0f,
                    minAltitude = 77f,
                    maxAltitude = 900f,
                    frames = new List<TrajectoryPoint>
                    {
                        MakePoint(t0 + 0, -0.0972, -74.5575, 77.0, velY: 6f, funds: 31000.0),
                        MakePoint(t0 + 10, -0.0963, -74.5550, 320.0, velY: 88f, rotZ: 0.10f, rotW: 0.99f,
                            funds: 31000.0),
                        sharedBoundary
                    },
                    checkpoints = new List<OrbitSegment>()
                })
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    startUT = t0 + 20,
                    endUT = t0 + 40,
                    sampleRateHz = 10.0f,
                    boundaryDiscontinuityMeters = 0.25f,
                    minAltitude = 900f,
                    maxAltitude = 2600f,
                    frames = new List<TrajectoryPoint>
                    {
                        sharedBoundary,
                        MakePoint(t0 + 30, -0.0905, -74.5450, 1750.0, velY: 155f, rotZ: 0.31f, rotW: 0.95f,
                            funds: 32000.0, science: 2.5f),
                        MakePoint(t0 + 40, -0.0860, -74.5380, 2600.0, velX: 5f, velY: 120f, rotZ: 0.38f,
                            rotW: 0.92f, funds: 32000.0, science: 2.5f)
                    },
                    checkpoints = new List<OrbitSegment>()
                })
                .WithGhostVisualSnapshot(
                    VesselSnapshotBuilder.FleaRocket("Fixture Atmospheric Ascent", "Valentina Kerman", pid: 410000u)
                        .AsLanded(-0.0860, -74.5380, 77.0));

            return new FixtureCase("Atmospheric Active Multi-Section", builder);
        }

        internal static FixtureCase OrbitalCheckpointTransition()
        {
            const double t0 = 18200.0;
            OrbitSegment checkpoint = MakeOrbitSegment(t0 + 120, t0 + 1620,
                body: "Kerbin", inc: 28.5, ecc: 0.003, sma: 705000.0, lan: 92.0, argPe: 47.0,
                mna: 0.18, epoch: t0 + 120);

            var builder = new RecordingBuilder("Fixture Orbital Transition")
                .WithRecordingId("fixture-orbital-checkpoint")
                .WithFormatVersion(RecordingStore.CurrentRecordingFormatVersion)
                .AddPoint(t0 + 0, -0.0972, -74.5575, 77.0)
                .AddPoint(t0 + 60, -0.0560, -74.4500, 28000.0)
                .AddPoint(t0 + 120, -0.0120, -74.2100, 71000.0)
                .AddOrbitSegment(checkpoint.startUT, checkpoint.endUT, checkpoint.inclination,
                    checkpoint.eccentricity, checkpoint.semiMajorAxis, checkpoint.longitudeOfAscendingNode,
                    checkpoint.argumentOfPeriapsis, checkpoint.meanAnomalyAtEpoch, checkpoint.epoch,
                    checkpoint.bodyName)
                .AddPartEvent(t0 + 120, 420002u, (int)PartEventType.EngineShutdown, "orbitalEngine")
                .AddSegmentEvent(SegmentEventType.TimeJump, t0 + 120, "checkpoint handoff")
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.ExoPropulsive,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    startUT = t0,
                    endUT = t0 + 120,
                    sampleRateHz = 2.0f,
                    minAltitude = 77f,
                    maxAltitude = 71000f,
                    frames = new List<TrajectoryPoint>
                    {
                        MakePoint(t0 + 0, -0.0972, -74.5575, 77.0, velY: 8f),
                        MakePoint(t0 + 60, -0.0560, -74.4500, 28000.0, velY: 910f, rotZ: 0.52f, rotW: 0.85f),
                        MakePoint(t0 + 120, -0.0120, -74.2100, 71000.0, velY: 2200f, rotZ: 0.70f, rotW: 0.71f)
                    },
                    checkpoints = new List<OrbitSegment>()
                })
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                    source = TrackSectionSource.Checkpoint,
                    startUT = checkpoint.startUT,
                    endUT = checkpoint.endUT,
                    sampleRateHz = 0.1f,
                    minAltitude = 71000f,
                    maxAltitude = 107000f,
                    frames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment> { checkpoint }
                })
                .WithVesselSnapshot(
                    VesselSnapshotBuilder.CrewedShip("Fixture Orbital Transition", "Bill Kerman", pid: 420000u)
                        .AddPart("fuelTank", position: "0,-0.75,0")
                        .AddPart("orbitalEngine", position: "0,-1.5,0")
                        .AsOrbiting(705000.0, 0.003, 28.5, lan: 92.0, argPe: 47.0, mna: 0.18,
                            epoch: checkpoint.startUT));

            return new FixtureCase("Orbital Checkpoint Transition", builder);
        }

        internal static FixtureCase MixedActiveBackground()
        {
            const double t0 = 19400.0;

            var builder = new RecordingBuilder("Fixture Mixed Sources")
                .WithRecordingId("fixture-mixed-sources")
                .WithFormatVersion(RecordingStore.CurrentRecordingFormatVersion)
                .AddPoint(t0 + 0, -0.1010, -74.6200, 69.0, funds: 15000.0)
                .AddPoint(t0 + 60, -0.1010, -74.6200, 69.0, funds: 15000.0)
                .AddPoint(t0 + 120, -0.0985, -74.6160, 240.0, funds: 14950.0)
                .AddPoint(t0 + 180, -0.0960, -74.6100, 620.0, funds: 14950.0)
                .AddPartEvent(t0 + 120, 430001u, (int)PartEventType.RCSActivated, "rcsBlock", value: 0.65f)
                .AddSegmentEvent(SegmentEventType.ControllerEnabled, t0 + 60, "background wake")
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.SurfaceStationary,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Background,
                    startUT = t0,
                    endUT = t0 + 60,
                    sampleRateHz = 0.5f,
                    minAltitude = 69f,
                    maxAltitude = 69f,
                    frames = new List<TrajectoryPoint>
                    {
                        MakePoint(t0 + 0, -0.1010, -74.6200, 69.0, body: "Kerbin"),
                        MakePoint(t0 + 60, -0.1010, -74.6200, 69.0, body: "Kerbin")
                    },
                    checkpoints = new List<OrbitSegment>()
                })
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.Atmospheric,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    startUT = t0 + 60,
                    endUT = t0 + 180,
                    sampleRateHz = 4.0f,
                    boundaryDiscontinuityMeters = 3.5f,
                    minAltitude = 69f,
                    maxAltitude = 620f,
                    frames = new List<TrajectoryPoint>
                    {
                        MakePoint(t0 + 60, -0.1010, -74.6200, 69.0, velY: 0.5f, funds: 15000.0),
                        MakePoint(t0 + 120, -0.0985, -74.6160, 240.0, velY: 36f, funds: 14950.0),
                        MakePoint(t0 + 180, -0.0960, -74.6100, 620.0, velX: 6f, velY: 52f, funds: 14950.0)
                    },
                    checkpoints = new List<OrbitSegment>()
                })
                .WithVesselSnapshot(
                    VesselSnapshotBuilder.ProbeShip("Fixture Mixed Sources", pid: 430000u)
                        .AsLanded(-0.0960, -74.6100, 69.0))
                .WithGhostVisualSnapshot(
                    VesselSnapshotBuilder.FleaRocket("Fixture Mixed Sources Ghost", "Jebediah Kerman", pid: 430999u)
                        .AsLanded(-0.0960, -74.6100, 69.0));

            return new FixtureCase("Mixed Active And Background", builder);
        }

        internal static FixtureCase OptimizerBoundarySeed()
        {
            const double t0 = 20500.0;
            TrajectoryPoint seededBoundary = MakePoint(t0 + 10, -0.0200, -67.0000, 1800.0,
                body: "Mun", velY: 42f, rotZ: 0.26f, rotW: 0.96f, funds: 9800.0, science: 4.0f);

            var builder = new RecordingBuilder("Fixture Optimizer Boundary")
                .WithRecordingId("fixture-optimizer-boundary")
                .WithFormatVersion(RecordingStore.CurrentRecordingFormatVersion)
                .WithChainId("fixture-chain")
                .WithChainIndex(2)
                .WithChainBranch(1)
                .AddPoint(t0 + 0, -0.0240, -67.0060, 900.0, body: "Mun", funds: 9800.0)
                .AddPoint(t0 + 5, -0.0220, -67.0030, 1250.0, body: "Mun", funds: 9800.0)
                .AddPoint(seededBoundary.ut, seededBoundary.latitude, seededBoundary.longitude,
                    seededBoundary.altitude, body: seededBoundary.bodyName, funds: seededBoundary.funds,
                    science: seededBoundary.science)
                .AddPoint(t0 + 15, -0.0170, -66.9960, 2400.0, body: "Mun", funds: 9750.0, science: 4.2f)
                .AddPoint(t0 + 20, -0.0140, -66.9900, 3200.0, body: "Mun", funds: 9750.0, science: 4.2f)
                .AddPartEvent(t0 + 10, 440001u, (int)PartEventType.Decoupled, "separator")
                .AddPartEvent(t0 + 15, 440002u, (int)PartEventType.ParachuteDeployed, "radialChute")
                .AddSegmentEvent(SegmentEventType.ControllerChange, t0 + 10, "optimizer split")
                .AddSegmentEvent(SegmentEventType.PartDestroyed, t0 + 15, "separator:440001")
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.Approach,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    startUT = t0,
                    endUT = t0 + 10,
                    sampleRateHz = 5.0f,
                    minAltitude = 900f,
                    maxAltitude = 1800f,
                    frames = new List<TrajectoryPoint>
                    {
                        MakePoint(t0 + 0, -0.0240, -67.0060, 900.0, body: "Mun", velY: 18f, funds: 9800.0),
                        MakePoint(t0 + 5, -0.0220, -67.0030, 1250.0, body: "Mun", velY: 31f, funds: 9800.0),
                        seededBoundary
                    },
                    checkpoints = new List<OrbitSegment>()
                })
                .AddTrackSection(new TrackSection
                {
                    environment = SegmentEnvironment.Approach,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                    startUT = t0 + 10,
                    endUT = t0 + 20,
                    sampleRateHz = 5.0f,
                    boundaryDiscontinuityMeters = 0.5f,
                    minAltitude = 1800f,
                    maxAltitude = 3200f,
                    frames = new List<TrajectoryPoint>
                    {
                        seededBoundary,
                        MakePoint(t0 + 15, -0.0170, -66.9960, 2400.0, body: "Mun", velY: 58f,
                            rotZ: 0.34f, rotW: 0.94f, funds: 9750.0, science: 4.2f),
                        MakePoint(t0 + 20, -0.0140, -66.9900, 3200.0, body: "Mun", velY: 64f,
                            rotZ: 0.40f, rotW: 0.91f, funds: 9750.0, science: 4.2f)
                    },
                    checkpoints = new List<OrbitSegment>()
                })
                .WithGhostVisualSnapshot(
                    VesselSnapshotBuilder.ProbeShip("Fixture Optimizer Boundary", pid: 440000u)
                        .AsLanded(-0.0140, -66.9900, 0.0));

            return new FixtureCase("Optimizer Boundary Seed", builder);
        }

        private static TrajectoryPoint MakePoint(double ut, double lat, double lon, double alt,
            string body = "Kerbin",
            float velX = 0f, float velY = 0f, float velZ = 0f,
            float rotX = 0f, float rotY = 0f, float rotZ = 0f, float rotW = 1f,
            double funds = 0d, float science = 0f, float reputation = 0f)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = body,
                velocity = new Vector3(velX, velY, velZ),
                rotation = new Quaternion(rotX, rotY, rotZ, rotW),
                funds = funds,
                science = science,
                reputation = reputation
            };
        }

        private static OrbitSegment MakeOrbitSegment(double startUT, double endUT,
            string body = "Kerbin",
            double inc = 28.5, double ecc = 0.01, double sma = 700000.0,
            double lan = 90.0, double argPe = 45.0, double mna = 0.0, double epoch = 0.0)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = inc,
                eccentricity = ecc,
                semiMajorAxis = sma,
                longitudeOfAscendingNode = lan,
                argumentOfPeriapsis = argPe,
                meanAnomalyAtEpoch = mna,
                epoch = epoch,
                bodyName = body
            };
        }
    }
}
