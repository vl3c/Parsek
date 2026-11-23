using System;
using System.Collections.Generic;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// The Mission Profile window's whole mocked target: the built profile plus the title.
    /// </summary>
    internal sealed class GuiMockProfile
    {
        /// <summary>The window title's tail (the window draws "Parsek - Profile: " + this).</summary>
        internal string Title;

        /// <summary>The profile the REAL builder produced over the state's detached inputs.</summary>
        internal MissionProfile Profile;
    }

    /// <summary>
    /// Catalogue states for the Mission Profile window (docs/dev/design-mission-profile.md
    /// section 7).
    ///
    /// <para><b>Every row here is real-builder output.</b> A state assembles a DETACHED
    /// <see cref="Recording"/> (track sections, orbit checkpoints, engine part events, the
    /// terminal state and orbit) inside a detached <see cref="RecordingTree"/>, and runs
    /// <c>MissionProfileBuilder.BuildProfile</c> over it with the stock body table - the
    /// same analyzer, line builder and row formatter the window runs over a committed
    /// tree. Not one cell is typed. Nothing built here reaches a store: the recording and
    /// the tree are plain objects held only by the state's closure, and the write-set gate
    /// (<c>scripts/grep-audit-gui-mock-writeset.ps1</c>) fails the build on any store or
    /// writer reference.</para>
    ///
    /// <para>The request carries no date formatter, so the first row's Time cell is the
    /// builder's own headless fallback rather than a calendar rendering.</para>
    /// </summary>
    internal static class GuiMockProfileStates
    {
        // The window's first-open size, which shows a Mun return's rows with a scrollbar
        // for the tail only.
        private const int RectW = 960;
        private const int RectH = 420;

        internal const string MunReturnStateId = "profile.mission.mun-return";
        internal const string EmptyStateId = "profile.mission.empty";

        // Stock Kerbin / Mun shapes (radius 600 km / 200 km).
        private const double LkoSma = 680000, LkoEcc = 0.005;
        private const double FallingSma = 590000, FallingEcc = 0.1525;
        private const double MunTransferSma = 6400000, MunTransferEcc = 0.894;

        internal static void Append(List<GuiMockState> into)
        {
            into.Add(New(MunReturnStateId,
                "A crewed Mun return in one recording: launch, Kerbin orbit, transfer, Mun "
                + "capture and orbit, return burn, reentry under parachutes and recovery, with "
                + "the recorded delta-v spent per burn in the house orange and the delta-v left.",
                new[] { "MissionProfileRow.SpentOrange" },
                "Mun Return", BuildMunReturn));

            into.Add(New(EmptyStateId,
                "A mission with no recorded flight: the one muted sentence under the headers.",
                new[] { "MissionProfileRow.Empty" },
                "Empty Mission", BuildEmpty));
        }

        private static GuiMockState New(string id, string note, string[] covers, string title,
            Func<MissionProfile> build)
        {
            return new GuiMockState
            {
                Id = id,
                Window = GuiMockSession.ProfileWindow,
                Tab = null,
                RectW = RectW,
                RectH = RectH,
                Covers = covers,
                Note = note,
                Build = () => new GuiMockPayload
                {
                    Window = GuiMockSession.ProfileWindow,
                    Profile = new GuiMockProfile { Title = title, Profile = build() },
                },
            };
        }

        // ----- the states -----

        /// <summary>The Mun return, built fresh per call (the builder's inputs are mutable).</summary>
        internal static MissionProfile BuildMunReturn()
        {
            Recording rec = new PieceInputs("gallery-munreturn", "Mun Return", 1001)
                .Sec(SegmentEnvironment.SurfaceStationary, 0, 5, "Kerbin", 70, 70)
                .Sec(SegmentEnvironment.Atmospheric, 5, 180, "Kerbin", 100, 70000).Engine(4, 150)
                .Sec(SegmentEnvironment.ExoBallistic, 180, 200, "Kerbin", 70000, 75000)
                .Cp(200, 260, "Kerbin", FallingSma, FallingEcc)
                .Sec(SegmentEnvironment.ExoBallistic, 260, 270, "Kerbin", 79000, 79500)
                .Sec(SegmentEnvironment.ExoPropulsive, 270, 290, "Kerbin", 79500, 80000).Engine(269, 289)
                .Sec(SegmentEnvironment.ExoBallistic, 290, 300, "Kerbin", 80000, 80000)
                .Cp(300, 2000, "Kerbin", LkoSma, LkoEcc)
                .Sec(SegmentEnvironment.ExoPropulsive, 2000, 2030, "Kerbin", 80000, 80000).Engine(1999, 2029)
                .Cp(2030, 6000, "Kerbin", MunTransferSma, MunTransferEcc)
                .Cp(6000, 9000, "Mun", -500000, 1.7)
                .Sec(SegmentEnvironment.ExoPropulsive, 9000, 9020, "Mun", 150000, 150000).Engine(8999, 9019)
                .Cp(9020, 20000, "Mun", 350000, 0.01)
                .Sec(SegmentEnvironment.ExoPropulsive, 20000, 20030, "Mun", 150000, 150000).Engine(19999, 20029)
                .Cp(20030, 23000, "Mun", -500000, 1.7)
                .Cp(23000, 40000, "Kerbin", 6000000, 0.89)
                .Sec(SegmentEnvironment.Atmospheric, 40000, 40300, "Kerbin", 70000, 100).Chute(40200)
                .Sec(SegmentEnvironment.SurfaceStationary, 40300, 40330, "Kerbin", 70, 70)
                .Launch("Launch Pad")
                .Ends(TerminalState.Recovered, "Shores")
                // The game's own delta-v readout at each step (design 8.1), so Spent and Left
                // show recorded figures rather than the estimate.
                .Dv(0, DeltaVTrigger.Start, 5200).Dv(4, DeltaVTrigger.BurnStart, 5200)
                .Dv(150, DeltaVTrigger.BurnEnd, 1990).Dv(269, DeltaVTrigger.BurnStart, 1990)
                .Dv(289, DeltaVTrigger.BurnEnd, 1810).Dv(1999, DeltaVTrigger.BurnStart, 1810)
                .Dv(2029, DeltaVTrigger.BurnEnd, 950).Dv(8999, DeltaVTrigger.BurnStart, 950)
                .Dv(9019, DeltaVTrigger.BurnEnd, 640).Dv(19999, DeltaVTrigger.BurnStart, 640)
                .Dv(20029, DeltaVTrigger.BurnEnd, 330).Dv(40330, DeltaVTrigger.End, 330)
                .Build();
            return BuildOver(rec, "Mun Return");
        }

        /// <summary>A mission whose tree holds no flight: the builder's empty state.</summary>
        internal static MissionProfile BuildEmpty()
        {
            var tree = new RecordingTree { Id = "gallery-profile-empty", TreeName = "Empty Mission" };
            return MissionProfileBuilder.BuildProfile(new MissionProfileRequest
            {
                Mission = new Mission("gallery-profile-empty-m", tree.Id, "Empty Mission"),
                Tree = tree,
                BodyFacts = MissionProfileBuilder.StockBodyFacts,
                DateFormatter = MissionProfileWindowUI.FormatDate,
            });
        }

        private static MissionProfile BuildOver(Recording rec, string name)
        {
            var tree = new RecordingTree
            {
                Id = "gallery-profile-tree",
                RootRecordingId = rec.RecordingId,
                TreeName = name,
            };
            tree.Recordings[rec.RecordingId] = rec;
            return MissionProfileBuilder.BuildProfile(new MissionProfileRequest
            {
                Mission = new Mission("gallery-profile-m", tree.Id, name),
                Tree = tree,
                BodyFacts = MissionProfileBuilder.StockBodyFacts,
                DateFormatter = MissionProfileWindowUI.FormatDate,
            });
        }

        /// <summary>
        /// One detached recording under construction: track sections with two frame points
        /// each, on-rails checkpoint sections, engine firings and a terminal. Holds INPUTS
        /// only; every rendered string comes out of the builder.
        /// </summary>
        internal sealed class PieceInputs
        {
            private readonly Recording rec;
            private uint nextPid = 1;

            internal PieceInputs(string id, string vessel, uint vesselPid)
            {
                rec = new Recording
                {
                    RecordingId = id,
                    VesselName = vessel,
                    VesselPersistentId = vesselPid,
                    StartBodyName = "Kerbin",
                    Controllers = new List<ControllerInfo> { new ControllerInfo { type = "CrewedPod" } },
                };
            }

            internal PieceInputs Sec(SegmentEnvironment env, double t0, double t1, string body,
                double alt0, double alt1)
            {
                var frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = t0, altitude = alt0, bodyName = body },
                    new TrajectoryPoint { ut = t1, altitude = alt1, bodyName = body, longitude = 0.01 },
                };
                rec.TrackSections.Add(new TrackSection
                {
                    environment = env, referenceFrame = ReferenceFrame.Absolute, source = TrackSectionSource.Active,
                    startUT = t0, endUT = t1, frames = frames, checkpoints = new List<OrbitSegment>(),
                    minAltitude = (float)Math.Min(alt0, alt1), maxAltitude = (float)Math.Max(alt0, alt1),
                    sampleRateHz = 1f,
                });
                rec.Points.AddRange(frames);
                Extend(t0, t1);
                return this;
            }

            internal PieceInputs Cp(double t0, double t1, string body, double sma, double ecc)
            {
                rec.TrackSections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic, referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                    source = TrackSectionSource.Checkpoint, startUT = t0, endUT = t1,
                    frames = new List<TrajectoryPoint>(),
                    checkpoints = new List<OrbitSegment>
                    {
                        new OrbitSegment
                        {
                            startUT = t0, endUT = t1, bodyName = body, semiMajorAxis = sma, eccentricity = ecc,
                            epoch = t0,
                        },
                    },
                    minAltitude = float.NaN, maxAltitude = float.NaN, sampleRateHz = 0.1f,
                });
                Extend(t0, t1);
                return this;
            }

            internal PieceInputs Engine(double on, double off)
            {
                uint pid = nextPid++;
                rec.PartEvents.Add(new PartEvent
                {
                    ut = on, partPersistentId = pid, eventType = PartEventType.EngineIgnited,
                    partName = "liquidEngine2", value = 1f,
                });
                rec.PartEvents.Add(new PartEvent
                {
                    ut = off, partPersistentId = pid, eventType = PartEventType.EngineShutdown,
                    partName = "liquidEngine2", value = 0f,
                });
                return this;
            }

            internal PieceInputs Chute(double ut)
            {
                rec.PartEvents.Add(new PartEvent
                {
                    ut = ut, partPersistentId = 900, eventType = PartEventType.ParachuteDeployed,
                    partName = "parachuteSingle",
                });
                return this;
            }

            internal PieceInputs Dv(double ut, DeltaVTrigger trigger, double vacuum)
            {
                if (rec.DeltaVSnapshots == null) rec.DeltaVSnapshots = new List<DeltaVSnapshot>();
                rec.DeltaVSnapshots.Add(new DeltaVSnapshot(ut, trigger, DeltaVSnapshotStatus.Ok,
                    vacuum, vacuum, vacuum, new[] { new DeltaVStageReading(0, vacuum, 0.0) }));
                return this;
            }

            internal PieceInputs Launch(string site)
            {
                rec.LaunchSiteName = site;
                rec.StartSituation = "Prelaunch";
                rec.StartBiome = "LaunchPad";
                return this;
            }

            internal PieceInputs Ends(TerminalState terminal, string endBiome)
            {
                rec.TerminalStateValue = terminal;
                rec.EndBiome = endBiome;
                return this;
            }

            internal Recording Build()
            {
                rec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
                return rec;
            }

            private void Extend(double t0, double t1)
            {
                if (double.IsNaN(rec.ExplicitStartUT) || t0 < rec.ExplicitStartUT) rec.ExplicitStartUT = t0;
                if (double.IsNaN(rec.ExplicitEndUT) || t1 > rec.ExplicitEndUT) rec.ExplicitEndUT = t1;
            }
        }
    }
}
