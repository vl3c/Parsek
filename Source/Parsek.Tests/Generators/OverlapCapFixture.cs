using System;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// The <c>overlap-cap</c> injection preset: ONE committed single-recording tree whose
    /// recording carries its OWN loop toggle and a loop period below its duration / 20, so
    /// the flight engine's per-recording overlap cap
    /// (<c>GhostPlayback.MaxOverlapGhostsPerRecording</c>, applied by
    /// <c>GhostPlaybackEngine.UpdateOverlapPlayback</c> through
    /// <c>GhostPlaybackLogic.ComputeEffectiveLaunchCadence</c>) has to raise the cadence.
    /// Consumer: the <c>OC-1-overlap-cap-per-recording</c> lane.
    ///
    /// <para><b>Why synthetic.</b> A mission loop never reaches the engine cap: the mission
    /// loop-unit builder caps the period at mission granularity first, so the engine reads
    /// <c>no adjustment</c> by construction (GS-12, V8F). A per-recording loop is set only
    /// by the Recordings tab's Loop toggle and Period cell, which the command seam does not
    /// drive, so the loop fields are authored here, exactly as the tab writes them
    /// (<c>loopPlayback</c> + <c>loopIntervalSeconds</c>, <c>LoopTimeUnit.Sec</c>).</para>
    ///
    /// <para><b>Shape.</b> A <see cref="DurationSeconds"/> climb 150 m north of the
    /// <c>pad-runway-pair</c> pad, well inside the flight zone of the focused vessel on the
    /// pad, with a crewless ProbeShip ghost-visual snapshot and no vessel snapshot (a loop
    /// replays a ghost; nothing spawns). The period <see cref="LoopPeriodSeconds"/> sits at
    /// <c>LoopTiming.MinCycleDuration</c>, so <c>ResolveLoopInterval</c> does not clamp it,
    /// and below <see cref="DurationSeconds"/> / 20, so the engine raises it to exactly
    /// <see cref="ExpectedEffectiveCadenceSeconds"/> (cycles = 20).</para>
    ///
    /// <para><b>Timing.</b> The recording STARTS <see cref="StartAfterSaveSeconds"/> after
    /// the host save's clock. The loop schedule starts at the recording's own start, so no
    /// copy exists at scene load, and every copy spawns after the lane has turned
    /// <c>ghostRenderTracing</c> on: each copy then writes its own MeshSpawned line, which
    /// is what makes the ghostlife <c>peakLive</c> count exact rather than a lower bound.</para>
    /// </summary>
    internal static class OverlapCapFixture
    {
        internal const string RecordingId = "overlapcap000000000000000000d6oc";
        internal const string VesselName = "Overlap Cap Probe";
        internal const string GroupName = "Synthetic-OverlapCap";

        /// <summary><c>pad-runway-pair</c>'s own clock (persistent.sfs FLIGHTSTATE UT).</summary>
        internal const double HostSaveUT = 25.25999999999955;

        internal const double StartAfterSaveSeconds = 35.0;
        internal const double DurationSeconds = 120.0;
        internal const double SampleSpacingSeconds = 10.0;

        /// <summary>The user period: <c>LoopTiming.MinCycleDuration</c>, below span/20.</summary>
        internal const double LoopPeriodSeconds = 5.0;

        /// <summary>span / cap: the cadence the engine must raise the period to.</summary>
        internal const double ExpectedEffectiveCadenceSeconds = DurationSeconds / 20.0;

        // The pad (the focused Logi Cargo Rig sits on it) and a point 150 m north of it
        // (1 deg of latitude on Kerbin is ~10472 m).
        internal const double PadLatitude = -0.0972;
        internal const double PadLongitude = -74.5577;
        internal const double NorthOffsetDegrees = 0.0143;
        internal const double StartAltitude = 90.0;
        internal const double ClimbRateMetersPerSecond = 5.0;

        internal const uint GhostPartPid = 100000u;

        internal static double StartUTFor(double saveUT)
        {
            return saveUT + StartAfterSaveSeconds;
        }

        internal static RecordingBuilder BuildRecording(double saveUT)
        {
            double t0 = StartUTFor(saveUT);
            double lat = PadLatitude + NorthOffsetDegrees;
            var b = new RecordingBuilder(VesselName)
                .WithRecordingId(RecordingId)
                .WithRecordingGroup(GroupName)
                // An atmospheric flying phase is what Recording.IsLoopableRecording
                // admits; without it the load-time SanitizeNonLoopableLoopPlayback pass
                // clears the loop toggle before the engine ever sees it.
                .WithSegmentPhase("atmo")
                .WithLoopPlayback(true, LoopPeriodSeconds);

            int samples = (int)Math.Round(DurationSeconds / SampleSpacingSeconds);
            for (int i = 0; i <= samples; i++)
            {
                double dt = i * SampleSpacingSeconds;
                b.AddPoint(t0 + dt, lat, PadLongitude, StartAltitude + ClimbRateMetersPerSecond * dt);
            }

            b.WithGhostVisualSnapshot(
                VesselSnapshotBuilder.ProbeShip(VesselName, GhostPartPid)
                    .AsLanded(lat, PadLongitude, StartAltitude));
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
