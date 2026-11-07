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
