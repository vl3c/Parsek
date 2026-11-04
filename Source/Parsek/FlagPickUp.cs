using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// A kerbal's flag pick-up as a recorded event (owner ruling 2026-11-04, todo
    /// LOOP-REPLAY-REPLANTS-REMOVED-FLAG): a <see cref="SegmentEventType.FlagPickedUp"/> segment
    /// event on the recording of the EVA kerbal that took the flag down, carrying where the flag
    /// stood. Replay takes the flag down again at that UT (<c>GhostPlaybackLogic.ApplyFlagPickUps</c>),
    /// so after a rewind a flag that was picked up and planted elsewhere ends in one place.
    ///
    /// <para>
    /// Capture: stock <c>FlagSite.PickUp</c> (also behind <c>TakeDown</c>) checks the active
    /// vessel is an EVA kerbal that is not ragdolled, gives the kerbal the flag back and calls the
    /// flag vessel's <c>Die()</c> (decompiled KSP 1.12.5), which fires <c>onVesselWillDestroy</c>
    /// inside the call. <c>Patches.FlagPickUpCapturePatch</c> marks the flag and the picker for
    /// the duration of that call, and <c>ParsekFlight.OnVesselWillDestroy</c> turns a marked
    /// flag's death into the event.
    /// </para>
    /// </summary>
    internal static class FlagPickUp
    {
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>The flag vessel a <c>FlagSite.PickUp</c> call is taking down, 0 outside one.</summary>
        internal static uint PendingFlagPid;

        /// <summary>The EVA kerbal vessel doing the pick-up, 0 outside one.</summary>
        internal static uint PendingPickerPid;

        internal static void ResetForTesting()
        {
            PendingFlagPid = 0u;
            PendingPickerPid = 0u;
        }

        /// <summary>Pure: is the dying flag the one a pick-up in progress is taking down?</summary>
        internal static bool IsPendingPickUp(uint dyingFlagPid)
        {
            return dyingFlagPid != 0u && PendingFlagPid == dyingFlagPid && PendingPickerPid != 0u;
        }

        /// <summary>
        /// Pure: the event details. The name goes last because it is free text: everything after
        /// <c>name=</c> is the name, so a ';' or '=' in it survives.
        /// </summary>
        internal static string FormatDetails(string body, double lat, double lon, double alt, string name)
        {
            return "body=" + (body ?? "")
                + ";lat=" + lat.ToString("R", ic)
                + ";lon=" + lon.ToString("R", ic)
                + ";alt=" + alt.ToString("R", ic)
                + ";name=" + (name ?? "");
        }

        /// <summary>Pure: reads <see cref="FormatDetails"/>; false when a field is missing or not finite.</summary>
        internal static bool TryParseDetails(string details, out string body, out double lat, out double lon,
            out double alt, out string name)
        {
            body = null; name = null; lat = lon = alt = double.NaN;
            if (string.IsNullOrEmpty(details))
                return false;
            int nameAt = details.IndexOf(";name=", StringComparison.Ordinal);
            string head = nameAt >= 0 ? details.Substring(0, nameAt) : details;
            name = nameAt >= 0 ? details.Substring(nameAt + ";name=".Length) : "";
            string[] parts = head.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = parts[i].Substring(0, eq);
                string value = parts[i].Substring(eq + 1);
                switch (key)
                {
                    case "body": body = value; break;
                    case "lat": double.TryParse(value, NumberStyles.Float, ic, out lat); break;
                    case "lon": double.TryParse(value, NumberStyles.Float, ic, out lon); break;
                    case "alt": double.TryParse(value, NumberStyles.Float, ic, out alt); break;
                }
            }
            return !string.IsNullOrEmpty(body) && IsFinite(lat) && IsFinite(lon) && IsFinite(alt);
        }

        /// <summary>Pure: the segment event for a pick-up at <paramref name="ut"/>.</summary>
        internal static SegmentEvent BuildEvent(double ut, string body, double lat, double lon, double alt, string name)
        {
            return new SegmentEvent
            {
                ut = ut,
                type = SegmentEventType.FlagPickedUp,
                details = FormatDetails(body, lat, lon, alt, name),
            };
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
