using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Why a switch-segment session was created. Conceptually distinct
    /// from <see cref="StockActionType"/>: an intent can fire and fail
    /// without ever becoming a session, and a session can outlive its
    /// triggering intent. The two enums are kept separate so the
    /// type system reflects that distinction.
    /// </summary>
    internal enum SwitchSegmentEntryReason
    {
        TrackingStationFly = 0,
        KscMarkerFly = 1,
        MapSwitchTo = 2,
    }

    /// <summary>
    /// Live segment-attempt marker armed when a stock Fly / Switch-To
    /// click actually starts a new switch segment in FLIGHT. Owned by
    /// <see cref="ParsekScenario"/> and serialized through OnSave / OnLoad
    /// so the scoped Discard path remains correct across save/reload.
    ///
    /// <para>This Phase A.3 type only defines the data shape, the
    /// serialization codec, and the scenario-static-state storage. The
    /// arming site (segment creation) and clearing sites (Merge,
    /// Discard, scene-exit-without-segment-work) land in later phases.
    /// See <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>
    /// (Proposed Data Model) for the full lifecycle contract.</para>
    /// </summary>
    internal sealed class SwitchSegmentSession
    {
        /// <summary>Stable GUID for this attempt.</summary>
        internal Guid SessionId;

        /// <summary>Tree id for the live activeTree / stashed pendingTree carrying this segment.</summary>
        internal string TreeId;

        /// <summary>Recording the new segment continues from, when known.</summary>
        internal string ParentRecordingId;

        /// <summary>New recording id created for this switch/Fly segment.</summary>
        internal string ActiveSegmentRecordingId;

        /// <summary>Vessel persistentId before the switch, when available.</summary>
        internal uint SourceVesselPersistentId;

        /// <summary>Focused live vessel persistentId after the switch / Fly.</summary>
        internal uint FocusedVesselPersistentId;

        /// <summary>Planetarium UT at which the segment was created.</summary>
        internal double SwitchUT;

        /// <summary>Why the session was started.</summary>
        internal SwitchSegmentEntryReason EntryReason;

        /// <summary>
        /// IntentId of the <see cref="StockActionIntentMarker"/> that
        /// authorized this session. Lets discard / log paths cross-link
        /// the session back to its triggering UI click.
        /// </summary>
        internal Guid IntentId;

        /// <summary>
        /// Branch points that existed before this segment was attached.
        /// Never null — empty list means "no pre-existing BPs". Mirrors
        /// the <see cref="ReFlySessionMarker.PreSessionBranchPointIds"/>
        /// shape so scoped discard / structural-mutation gates can
        /// distinguish session-authored from pre-existing branch points.
        /// </summary>
        internal List<string> PreSessionBranchPointIds = new List<string>();

        /// <summary>
        /// Original committed tree id when the segment was attached to a
        /// #866 clone-restore; null otherwise.
        /// </summary>
        internal string CommittedTreeId;

        internal const string NodeName = "SWITCH_SEGMENT_SESSION";

        /// <summary>Writes this session as a child node on <paramref name="parent"/>.</summary>
        internal void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("sessionId", SessionId.ToString("D", ic));
            node.AddValue("treeId", TreeId ?? "");
            node.AddValue("parentRecordingId", ParentRecordingId ?? "");
            node.AddValue("activeSegmentRecordingId", ActiveSegmentRecordingId ?? "");
            node.AddValue("sourceVesselPersistentId", SourceVesselPersistentId.ToString(ic));
            node.AddValue("focusedVesselPersistentId", FocusedVesselPersistentId.ToString(ic));
            node.AddValue("switchUT", SwitchUT.ToString("R", ic));
            node.AddValue("entryReason", EntryReason.ToString());
            node.AddValue("intentId", IntentId.ToString("D", ic));
            if (!string.IsNullOrEmpty(CommittedTreeId))
                node.AddValue("committedTreeId", CommittedTreeId);

            // Always emit the presence sentinel so absent-vs-empty round-trips
            // safely. Mirror of REFLY_SESSION_MARKER's preSessionBranchPointIdsPresent.
            node.AddValue("preSessionBranchPointIdsPresent", "true");
            if (PreSessionBranchPointIds != null)
            {
                for (int i = 0; i < PreSessionBranchPointIds.Count; i++)
                {
                    string id = PreSessionBranchPointIds[i];
                    if (!string.IsNullOrEmpty(id))
                        node.AddValue("preSessionBranchPointId", id);
                }
            }
        }

        /// <summary>
        /// Attempts to load a session from a node previously written by
        /// <see cref="SaveInto"/>. Returns false when required fields are
        /// missing or malformed.
        /// </summary>
        internal static bool TryLoadFrom(ConfigNode node, out SwitchSegmentSession session)
        {
            session = null;
            if (node == null)
                return false;
            var ic = CultureInfo.InvariantCulture;

            string sessionIdStr = node.GetValue("sessionId");
            Guid sessionId;
            if (string.IsNullOrEmpty(sessionIdStr)
                || !Guid.TryParseExact(sessionIdStr, "D", out sessionId))
            {
                return false;
            }

            string entryReasonStr = node.GetValue("entryReason");
            SwitchSegmentEntryReason entryReason;
            if (string.IsNullOrEmpty(entryReasonStr)
                || !TryParseEnum(entryReasonStr, out entryReason))
            {
                return false;
            }

            string intentIdStr = node.GetValue("intentId");
            Guid intentId;
            if (string.IsNullOrEmpty(intentIdStr)
                || !Guid.TryParseExact(intentIdStr, "D", out intentId))
            {
                return false;
            }

            string treeId = node.GetValue("treeId");
            string parentRecordingId = node.GetValue("parentRecordingId");
            string activeSegmentRecordingId = node.GetValue("activeSegmentRecordingId");
            string committedTreeId = node.GetValue("committedTreeId");

            uint sourcePid = 0u;
            string sourcePidStr = node.GetValue("sourceVesselPersistentId");
            if (!string.IsNullOrEmpty(sourcePidStr))
                uint.TryParse(sourcePidStr, NumberStyles.Integer, ic, out sourcePid);

            uint focusedPid = 0u;
            string focusedPidStr = node.GetValue("focusedVesselPersistentId");
            if (!string.IsNullOrEmpty(focusedPidStr))
                uint.TryParse(focusedPidStr, NumberStyles.Integer, ic, out focusedPid);

            double switchUT = 0.0;
            string switchUtStr = node.GetValue("switchUT");
            if (!string.IsNullOrEmpty(switchUtStr))
                double.TryParse(switchUtStr, NumberStyles.Float, ic, out switchUT);

            var s = new SwitchSegmentSession
            {
                SessionId = sessionId,
                TreeId = string.IsNullOrEmpty(treeId) ? null : treeId,
                ParentRecordingId = string.IsNullOrEmpty(parentRecordingId) ? null : parentRecordingId,
                ActiveSegmentRecordingId = string.IsNullOrEmpty(activeSegmentRecordingId) ? null : activeSegmentRecordingId,
                SourceVesselPersistentId = sourcePid,
                FocusedVesselPersistentId = focusedPid,
                SwitchUT = switchUT,
                EntryReason = entryReason,
                IntentId = intentId,
                CommittedTreeId = string.IsNullOrEmpty(committedTreeId) ? null : committedTreeId,
                PreSessionBranchPointIds = new List<string>(),
            };

            string presentFlag = node.GetValue("preSessionBranchPointIdsPresent");
            if (string.Equals(presentFlag, "true", StringComparison.OrdinalIgnoreCase))
            {
                string[] ids = node.GetValues("preSessionBranchPointId");
                if (ids != null)
                {
                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(ids[i]))
                            s.PreSessionBranchPointIds.Add(ids[i]);
                    }
                }
            }

            session = s;
            return true;
        }

        private static bool TryParseEnum<T>(string text, out T value) where T : struct
        {
            try
            {
                value = (T)Enum.Parse(typeof(T), text, ignoreCase: true);
                return Enum.IsDefined(typeof(T), value);
            }
            catch
            {
                value = default(T);
                return false;
            }
        }
    }
}
