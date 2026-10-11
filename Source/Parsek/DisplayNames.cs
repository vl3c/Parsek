using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The global version of every name Parsek displays for a vessel or a mission
    /// (design-vessel-and-mission-names.md 6.3 / 7.11). It moves whenever a name changes IN
    /// PLACE, without a committed-list mutation, so a cache that bakes a name keys on
    /// <see cref="Version"/> (or listens to <see cref="DisplayNamesChanged"/>) instead of on
    /// <see cref="RecordingStore.StateVersion"/>, which a rename does not move.
    ///
    /// <para>Bumped by: a vessel name decision (<see cref="VesselNameStore"/>), an apply pass
    /// that changed a leg's name, <see cref="MissionStore.RenameMission"/> /
    /// <see cref="MissionGroupLink.RenameMissionGroup"/>, and hydration repair copying a
    /// committed recording's name back. In-memory only, never persisted.</para>
    /// </summary>
    internal static class DisplayNames
    {
        /// <summary>Moves on every in-place name change. Starts at 0 each session.</summary>
        internal static int Version { get; private set; }

        /// <summary>
        /// Raised after every bump. Subscribers drop name caches that cannot key on
        /// <see cref="Version"/> (the Timeline and Kerbals view models). A throwing subscriber
        /// is logged and does not stop the others.
        /// </summary>
        internal static event Action DisplayNamesChanged;

        /// <summary>Bumps <see cref="Version"/>, logs why, raises <see cref="DisplayNamesChanged"/>.</summary>
        internal static void NoteChanged(string reason, string detail)
        {
            unchecked { Version++; }
            ParsekLog.Verbose("VesselNames",
                "Display names invalidated: reason=" + (reason ?? "<none>") +
                " detail=" + (detail ?? "<null>") +
                " version=" + Version.ToString(CultureInfo.InvariantCulture));
            Action handlers = DisplayNamesChanged;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)d)();
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("VesselNames",
                        "DisplayNamesChanged subscriber threw: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>Test seam: drops every subscriber. The version keeps counting so a cache
        /// primed before a reset still sees a later bump as a change.</summary>
        internal static void ResetSubscribersForTesting()
        {
            DisplayNamesChanged = null;
        }
    }
}
