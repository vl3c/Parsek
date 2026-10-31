using System;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// The <c>standalone-loop-map</c> injection preset: ONE committed single-recording tree on
    /// <c>pad-runway-pair</c> whose recording loops on its OWN Recordings-tab toggle (not a
    /// Mission loop unit) with a period LONGER than its duration, so the loop never overlaps
    /// itself. Consumer: the <c>SLM-1-standalone-loop-map-position</c> lane and its in-game
    /// <c>StandaloneLoopMap</c> category.
    ///
    /// <para><b>What it pins.</b> The map and Tracking Station sample a standalone loop through
    /// <c>GhostPlaybackLogic.ResolveTrackingStationSampleUT</c>, which once returned the live UT
    /// for every recording outside a Mission loop unit. Past the first run the map ghost then
    /// sat at the recording's end (or vanished) while the flight mesh replayed each cycle (todo
    /// RECORDINGS-TAB-LOOP-NEVER-REPLAYS-ON-MAP). The orbit here is short enough that a wrong
    /// sample UT moves the map ghost by hundreds of kilometres.</para>
    ///
    /// <para><b>Shape.</b> One trajectory point at <see cref="StartUTFor"/> plus one orbit
    /// segment for <see cref="DurationSeconds"/>: an equatorial circular Kerbin orbit at
    /// <see cref="SemiMajorAxisMeters"/> (period about 1958 s), in the pad vessel's orbit shape
    /// (LAN of the KSC meridian at the start, argPe 270, mean anomaly 0), so the point and the
    /// orbit agree: the craft starts over the KSC at 100 km. A pure orbital coast is not
    /// loopable, so a launch identity keeps the loop flag through
    /// <c>SanitizeNonLoopableLoopPlayback</c>. A crewless ProbeShip ghost-visual snapshot and
    /// no vessel snapshot: a loop replays a ghost, nothing spawns. Terminal Orbiting, which is
    /// what makes the recording eligible for map presence.</para>
    ///
    /// <para><b>Timing.</b> The recording starts <see cref="StartAfterSaveSeconds"/> after the
    /// host save's clock and loops every <see cref="LoopPeriodSeconds"/> (unit Sec, so no auto
    /// queue), leaving a <see cref="LoopPeriodSeconds"/> - <see cref="DurationSeconds"/> pause
    /// between runs in which neither the mesh nor the map ghost may show.</para>
    /// </summary>
    internal static class StandaloneLoopMapFixture
    {
        internal const string RecordingId = "standaloneloopmap00000000000slm1";
        internal const string VesselName = "Standalone Loop Probe";
        internal const string GroupName = "Synthetic-StandaloneLoopMap";

        /// <summary><c>pad-runway-pair</c>'s own clock (persistent.sfs FLIGHTSTATE UT).</summary>
        internal const double HostSaveUT = 25.25999999999955;

        internal const double StartAfterSaveSeconds = 30.0;
        internal const double DurationSeconds = 1500.0;
        internal const double LoopPeriodSeconds = 4000.0;
        internal const double SemiMajorAxisMeters = 700000.0;
        internal const double ArgumentOfPeriapsisDegrees = 270.0;

        internal const uint GhostPartPid = 100000u;

        internal static double StartUTFor(double saveUT)
        {
            return saveUT + StartAfterSaveSeconds;
        }

        internal static RecordingBuilder BuildRecording(double saveUT)
        {
            double t0 = StartUTFor(saveUT);
            double end = t0 + DurationSeconds;
            double lan = SyntheticRecordingTests.GhostCommNetKscLanAt(t0);
            double alt = SemiMajorAxisMeters - SyntheticRecordingTests.KerbinRadiusMeters;
            string guid = ScenarioWriter.DeriveVesselLaunchGuid(RecordingId);

            var b = new RecordingBuilder(VesselName)
                .WithRecordingId(RecordingId)
                .WithRecordedVesselGuid(guid)
                .WithRecordingGroup(GroupName)
                .WithLaunchIdentity("LaunchPad")
                .WithLoopPlayback(true, LoopPeriodSeconds);
            b.AddPoint(t0, 0.0, SyntheticRecordingTests.GhostCommNetKscLon, alt);
            b.AddOrbitSegment(t0, end,
                inc: 0.0, ecc: 0.0, sma: SemiMajorAxisMeters, lan: lan,
                argPe: ArgumentOfPeriapsisDegrees, mna: 0.0, epoch: t0);
            b.WithTerminalState((int)TerminalState.Orbiting);
            b.WithTerminalOrbit("Kerbin", SemiMajorAxisMeters, 0.0, 0.0, lan,
                ArgumentOfPeriapsisDegrees, 0.0, t0);
            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.ProbeShip(VesselName, GhostPartPid)
                    .WithLaunchGuid(guid)
                    .AsOrbiting(SemiMajorAxisMeters, 0.0, 0.0,
                        lan: lan, argPe: ArgumentOfPeriapsisDegrees, mna: 0.0, epoch: t0));
            return b;
        }

        internal static void PopulateWriter(ScenarioWriter writer, double saveUT)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            writer.AddRecordingAsTree(BuildRecording(saveUT));
        }
    }
}
