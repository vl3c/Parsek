using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// The <c>relative-loop</c> injection preset: ONE looped recording whose every
    /// TrackSection is RELATIVE to a LIVE vessel named by <see cref="Recording.LoopAnchorVesselId"/>
    /// (the live-PID loop contract, <c>loopAnchorPid</c> on disk). Consumer: the
    /// <c>RL-1-relative-loop-live-anchor</c> harness lane, which claims D3 <c>relative-loop</c>.
    ///
    /// <para><b>Why synthetic.</b> Production sets <c>LoopAnchorVesselId</c> only at load, from
    /// the <c>loopAnchorPid</c> key (<c>RecordingTreeRecordCodec.LoadRecordingPlaybackAndLinkage</c>);
    /// no recorder path writes it, so no flown lane can reach the live-PID loop positioner
    /// (todo D3-RELATIVE-LOOP-HAS-NO-PRODUCTION-PATH-CELL).</para>
    ///
    /// <para><b>The anchor is a NON-active vessel, deliberately.</b> The host save is
    /// <c>pad-runway-pair</c>: the focused vessel is <c>Logi Cargo Rig</c> on the pad and
    /// <see cref="AnchorVesselName"/> sits on the runway about 1.8 km away. The engine only plays a
    /// loop-anchored ghost once the anchor pid is in <c>loadedAnchorVessels</c>, and the one
    /// production writer of that set is <c>ParsekFlight.OnVesselLoaded</c> (<c>onVesselLoaded</c>).
    /// The ACTIVE vessel is loaded by <c>FlightGlobals.SetActiveVessel -&gt; Vessel.MakeActive -&gt;
    /// Vessel.Load</c> inside <c>FlightDriver.Start</c>, before <c>ParsekFlight.Start</c>
    /// subscribes, so an active-vessel anchor never enters the set on a scene load. A nearby
    /// vessel is loaded later by <c>Vessel.Update</c>'s distance check (gated on
    /// <c>FlightGlobals.ready</c>), after the subscription.</para>
    ///
    /// <para><b>Shape.</b> Two RELATIVE sections of equal length, both anchored to
    /// <see cref="AnchorPid"/>: section A holds the anchor-local offset (0,0,0), so the placed ghost
    /// must sit EXACTLY on the live anchor's transform; section B holds (0,
    /// <see cref="SectionBOffsetY"/>,0), so the offset is also applied. The step is kept under
    /// <c>GhostRenderTrace.LargePoseDeltaMeters</c> (25 m) so the loop wrap raises no large-delta
    /// window. Environment is Atmospheric (not a "boring" environment), so neither the tail trim nor
    /// the auto loop range shortens the loop. The flat point list is exactly the concatenated
    /// section frames, so the section-authoritative and flat read paths agree. No vessel snapshot:
    /// the recording has nothing to spawn, only a ghost to replay.</para>
    /// </summary>
    internal static class RelativeLoopAnchorFixture
    {
        internal const string RecordingId = "relloopanchor00000000000000000d3";
        internal const string VesselName = "Relative Loop Anchor Probe";
        internal const string GroupName = "Synthetic-RelativeLoop";

        /// <summary><c>rover fuel 0</c>, VESSEL index 0 of <c>pad-runway-pair</c>.</summary>
        internal const uint AnchorPid = 95298807u;
        internal const string AnchorVesselName = "rover fuel 0";
        internal const string AnchorBodyName = "Kerbin";
        internal const double AnchorLatitude = -0.048684738931822846;
        internal const double AnchorLongitude = -74.724506116829559;
        internal const double AnchorAltitude = 70.215823083068244;

        internal const double SectionSeconds = 10.0;
        internal const double LoopSeconds = 2.0 * SectionSeconds;
        internal const double SectionBOffsetY = 20.0;

        /// <summary>How far before the save's own UT the recording ends: it is a past, committed
        /// recording whose loop is already running when the scene loads.</summary>
        internal const double EndBeforeSaveSeconds = 2.0;

        internal const uint GhostPartPid = 100000u;

        internal static double StartUTFor(double saveUT)
        {
            return Math.Max(1.0, saveUT - EndBeforeSaveSeconds - LoopSeconds);
        }

        internal static RecordingBuilder BuildRecording(double saveUT)
        {
            double t0 = StartUTFor(saveUT);
            double tMid = t0 + SectionSeconds;
            double tEnd = t0 + LoopSeconds;

            var sectionA = new List<TrajectoryPoint>
            {
                Offset(t0, 0.0),
                Offset(t0 + SectionSeconds * 0.5, 0.0),
                Offset(tMid - 0.1, 0.0),
            };
            var sectionB = new List<TrajectoryPoint>
            {
                Offset(tMid, SectionBOffsetY),
                Offset(tMid + SectionSeconds * 0.5, SectionBOffsetY),
                Offset(tEnd, SectionBOffsetY),
            };

            var builder = new RecordingBuilder(VesselName)
                .WithRecordingId(RecordingId)
                .WithRecordingGroup(GroupName)
                .WithSegmentBodyName(AnchorBodyName)
                .WithLoopPlayback(true, LoopSeconds)
                .WithLoopAnchorVesselId(AnchorPid, AnchorBodyName);

            foreach (TrajectoryPoint p in sectionA)
                AddFlatPoint(builder, p);
            foreach (TrajectoryPoint p in sectionB)
                AddFlatPoint(builder, p);

            builder.AddTrackSection(
                SegmentEnvironment.Atmospheric, ReferenceFrame.Relative, TrackSectionSource.Active,
                t0, tMid, frames: sectionA, anchorVesselId: AnchorPid, sampleRateHz: 0.2f);
            builder.AddTrackSection(
                SegmentEnvironment.Atmospheric, ReferenceFrame.Relative, TrackSectionSource.Active,
                tMid, tEnd, frames: sectionB, anchorVesselId: AnchorPid, sampleRateHz: 0.2f);

            var snap = VesselSnapshotBuilder.ProbeShip(VesselName, GhostPartPid)
                .AsLanded(AnchorLatitude, AnchorLongitude, AnchorAltitude)
                .Build();
            builder.WithGhostVisualSnapshot(snap);
            return builder;
        }

        internal static void PopulateWriter(ScenarioWriter writer, double saveUT)
        {
            writer.AddRecordingAsTree(BuildRecording(saveUT));
        }

        private static TrajectoryPoint Offset(double ut, double dy)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = dy,
                altitude = 0.0,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
                bodyName = AnchorBodyName,
                recordedGroundClearance = double.NaN,
            };
        }

        private static void AddFlatPoint(RecordingBuilder builder, TrajectoryPoint p)
        {
            // RELATIVE contract: latitude/longitude/altitude carry the anchor-local
            // metre offset (dx, dy, dz), never body-fixed coordinates.
            builder.AddPoint(p.ut, p.latitude, p.longitude, p.altitude, p.bodyName);
        }
    }
}
