using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Flag planting and pick-up capture into the active tree.
    public partial class ParsekFlight
    {
        void OnAfterFlagPlanted(FlagSite flagSite)
        {
            if (flagSite == null || flagSite.vessel == null || flagSite.part == null) return;

            // Always stamp the planting date onto the plaque text
            double ut = Planetarium.GetUniversalTime();
            string dateStr = KSPUtil.PrintDate(ut, includeTime: true);
            flagSite.PlaqueText = FormatPlaqueWithDate(flagSite.PlaqueText, dateStr);

            ParsekLog.Verbose("Flight",
                $"Flag planted: '{flagSite.vessel.vesselName}' by '{flagSite.placedBy}' — date stamped");

            // Recording-specific: capture FlagEvent. The planting kerbal may be the
            // foreground recorder's vessel OR a background-tracked tree member: an EVA
            // kerbal re-controlled after a vessel switch stays in the tree's
            // BackgroundMap with the foreground recorder idle, yet can still plant a
            // flag. Capture into whichever recording owns the planting vessel so the
            // flag persists to the sidecar and replays during watch/playback.
            string placedBy = flagSite.placedBy ?? "";

            Vessel flagVessel = flagSite.vessel;
            CelestialBody body = flagVessel.mainBody;

            // Compute surface-relative rotation
            Quaternion worldRot = flagVessel.transform.rotation;
            Quaternion surfRot = Quaternion.Inverse(body.bodyTransform.rotation) * worldRot;

            var fe = new FlagEvent
            {
                ut = ut,
                flagSiteName = flagVessel.vesselName ?? "",
                placedBy = placedBy,
                plaqueText = flagSite.PlaqueText ?? "",
                flagURL = flagSite.part.flagURL ?? "",
                latitude = flagVessel.latitude,
                longitude = flagVessel.longitude,
                altitude = flagVessel.altitude,
                rotX = surfRot.x,
                rotY = surfRot.y,
                rotZ = surfRot.z,
                rotW = surfRot.w,
                bodyName = body.name
            };

            // 1) Foreground recorder owns the planting vessel.
            if (recorder != null && recorder.IsRecording
                && ShouldRecordFlagEvent(placedBy,
                    FlightRecorder.FindVesselByPid(recorder.RecordingVesselId)))
            {
                recorder.FlagEvents.Add(fe);
                ParsekLog.Info("Flight",
                    $"Flag event captured (foreground recorder pid={recorder.RecordingVesselId}): " +
                    $"'{fe.flagSiteName}' by '{fe.placedBy}' at " +
                    $"({fe.latitude:F4},{fe.longitude:F4},{fe.altitude:F1}) on {fe.bodyName}");
                return;
            }

            // 2) A background-tracked tree member owns the planting vessel.
            uint bgOwnerPid = ResolveBackgroundFlagOwnerPid(
                placedBy,
                activeTree?.BackgroundMap,
                pid => FlightRecorder.FindVesselByPid(pid)?.GetVesselCrew());
            if (bgOwnerPid != 0
                && activeTree != null
                && activeTree.BackgroundMap.TryGetValue(bgOwnerPid, out string bgRecId)
                && activeTree.Recordings.TryGetValue(bgRecId, out Recording bgRec)
                && bgRec != null)
            {
                AppendFlagEventToTreeRecording(bgRec, fe);
                ParsekLog.Info("Flight",
                    $"Flag event captured (background tree member pid={bgOwnerPid} rec={bgRecId}): " +
                    $"'{fe.flagSiteName}' by '{fe.placedBy}' at " +
                    $"({fe.latitude:F4},{fe.longitude:F4},{fe.altitude:F1}) on {fe.bodyName}");
                return;
            }

            ParsekLog.Verbose("Flight",
                $"Flag planted by '{placedBy}' but no foreground or background recording owns " +
                $"the planting vessel - skipping (recorderPid={(recorder != null ? recorder.RecordingVesselId : 0)}, " +
                $"hasTree={activeTree != null})");
        }

        /// <summary>
        /// A kerbal took a flag down (<see cref="FlagPickUp"/>): record it on the recording that
        /// owns the picking kerbal, the foreground recorder's vessel or a background tree member,
        /// so a replay takes the flag down at the same moment.
        /// </summary>
        void TryCaptureFlagPickUp(Vessel flag, uint pickerPid)
        {
            if (flag == null || pickerPid == 0u)
                return;
            CelestialBody body = flag.mainBody;
            double ut = Planetarium.GetUniversalTime();
            SegmentEvent evt = FlagPickUp.BuildEvent(
                ut, body != null ? body.name : null, flag.latitude, flag.longitude, flag.altitude, flag.vesselName);

            if (recorder != null && recorder.IsRecording && recorder.RecordingVesselId == pickerPid)
            {
                // Straight onto the active tree recording when it exists: the mid-flight
                // serialization flush (FlushRecorderIntoActiveTreeForSerialization) does not move
                // recorder.SegmentEvents, so an event left there would miss an F5 and be lost on F9.
                string activeRecId = activeTree?.ActiveRecordingId;
                if (activeRecId != null
                    && activeTree.Recordings.TryGetValue(activeRecId, out Recording activeRec)
                    && activeRec != null)
                {
                    AppendSegmentEventToTreeRecording(activeRec, evt);
                }
                else
                {
                    recorder.SegmentEvents.Add(evt);
                }
                ParsekLog.Info("Flight",
                    $"Flag pick-up captured (foreground recorder pid={pickerPid} rec={activeRecId ?? "(recorder buffer)"}): " +
                    $"'{flag.vesselName}' on {(body != null ? body.name : "?")} ut={ut.ToString("R", CultureInfo.InvariantCulture)}");
                return;
            }

            if (activeTree != null
                && activeTree.BackgroundMap.TryGetValue(pickerPid, out string bgRecId)
                && activeTree.Recordings.TryGetValue(bgRecId, out Recording bgRec)
                && bgRec != null)
            {
                AppendSegmentEventToTreeRecording(bgRec, evt);
                ParsekLog.Info("Flight",
                    $"Flag pick-up captured (background tree member pid={pickerPid} rec={bgRecId}): " +
                    $"'{flag.vesselName}' ut={ut.ToString("R", CultureInfo.InvariantCulture)}");
                return;
            }

            ParsekLog.Verbose("Flight",
                $"Flag '{flag.vesselName}' picked up by pid={pickerPid} but no foreground or background " +
                $"recording owns the kerbal - not recorded (recorderPid={(recorder != null ? recorder.RecordingVesselId : 0)}, " +
                $"hasTree={activeTree != null})");
        }

        /// <summary>
        /// Appends the planting date to the plaque text, right-aligned via TMP rich text.
        /// </summary>
        internal static string FormatPlaqueWithDate(string originalText, string formattedDate)
        {
            if (string.IsNullOrEmpty(formattedDate))
                return originalText ?? "";

            if (string.IsNullOrEmpty(originalText))
                return formattedDate;

            return originalText + " - " + formattedDate;
        }

        /// <summary>
        /// Checks whether a flag planting event should be recorded for the given vessel.
        /// The planting kerbal must be the recorded vessel (EVA kerbal) or crew on the recorded vessel.
        /// </summary>
        internal static bool ShouldRecordFlagEvent(string placedBy, Vessel recordedVessel)
        {
            if (string.IsNullOrEmpty(placedBy) || recordedVessel == null) return false;

            return CrewContainsKerbalNamed(recordedVessel.GetVesselCrew(), placedBy);
        }

        internal static bool CrewContainsKerbalNamed(List<ProtoCrewMember> crew, string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName) || crew == null)
                return false;

            for (int i = 0; i < crew.Count; i++)
            {
                if (crew[i]?.name == kerbalName)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the background-tracked vessel pid whose live crew contains the flag
        /// planter, so a flag planted by a background tree member (e.g. an EVA kerbal
        /// re-controlled after a vessel switch) can be captured into its recording.
        /// Returns 0 when no background vessel matches. Pure decision helper (crew
        /// lookup injected) so it is unit-testable without a live tree.
        /// </summary>
        internal static uint ResolveBackgroundFlagOwnerPid(
            string placedBy,
            IEnumerable<KeyValuePair<uint, string>> backgroundMap,
            Func<uint, List<ProtoCrewMember>> crewLookup)
        {
            if (string.IsNullOrEmpty(placedBy) || backgroundMap == null || crewLookup == null)
                return 0;

            foreach (var entry in backgroundMap)
            {
                if (entry.Key == 0)
                    continue;
                if (CrewContainsKerbalNamed(crewLookup(entry.Key), placedBy))
                    return entry.Key;
            }

            return 0;
        }

        /// <summary>
        /// Appends a captured flag to a tree recording's FlagEvents, keeping the list
        /// in stable UT order and marking the recording's sidecar dirty so the new
        /// event persists. Used for background-tracked tree members whose FlagEvents
        /// are written straight to the recording; the foreground recorder sorts and
        /// marks dirty at flush time instead. The MarkFilesDirty call is load-bearing:
        /// without it the .prec sidecar is skipped on the next OnSave and the flag is
        /// lost on scene reload (see Recording.MarkFilesDirty).
        /// </summary>
        internal static void AppendFlagEventToTreeRecording(Recording rec, FlagEvent fe)
        {
            if (rec == null)
                return;

            rec.FlagEvents.Add(fe);

            // Stable UT order: ApplyFlagEvents/SpawnFlagVesselsUpToUT cursor-walk the
            // list and break on the first future-UT event, so a recording reused
            // across rewind/re-fly that already holds a higher-UT flag would otherwise
            // shadow the new one. Mirrors the foreground flush sort (#287).
            var sorted = FlightRecorder.StableSortByUT(rec.FlagEvents, e => e.ut);
            rec.FlagEvents.Clear();
            rec.FlagEvents.AddRange(sorted);

            rec.MarkFilesDirty();
        }
    }
}
