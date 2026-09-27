using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Result of <see cref="EffectiveState.WalkSlotVessel"/>: the recording that
    /// carries a Rewind Point slot vessel's real ending, plus (when detail was
    /// requested) the history the walk crossed to reach it. Owner ruling
    /// 2026-09-27: a separation slot follows its own vessel through that vessel's
    /// EVA branch points and through a Board that re-merges the same vessel with its
    /// own EVA crew. Collections are allocated lazily so the tip-only callers (per
    /// slot, per frame) pay for none of them.
    /// </summary>
    internal sealed class SlotVesselWalk
    {
        internal struct KerbalEnd
        {
            internal string KerbalRecordingId;
            internal string BranchPointId;
            internal BranchPointType BranchPointType;
        }

        /// <summary>The recording the walk started from.</summary>
        internal Recording Start;

        /// <summary>The vessel's real tip: its terminal is the slot's terminal.</summary>
        internal Recording Tip;

        internal int SwitchHops;
        internal int EvaHops;
        internal int BoardHops;

        /// <summary>Why the walk stopped at a child branch point, or null when the tip
        /// has none (<c>notSwitchBranchPoint</c>, <c>danglingOrAmbiguousChild</c>,
        /// <c>evaNoVesselContinuation</c>, <c>boardNotSameVessel</c>,
        /// <c>boardForeignParent</c>, <c>childChainTipUnresolved</c>, <c>cycle</c>,
        /// <c>hopCap</c>).</summary>
        internal string StopReason;
        internal string StopBranchPointId;

        /// <summary>First own-EVA kerbal whose history joins a foreign vessel (rule 2).</summary>
        internal string ForeignJoinKerbalRecordingId;
        internal string ForeignJoinBranchPointId;
        internal BranchPointType? ForeignJoinBranchPointType;

        private List<string> vesselRecordingIds;
        private List<string> ownEvaBranchPointIds;
        private HashSet<string> hoppedBoardBranchPointIds;
        private HashSet<string> ownKerbalRecordingIds;
        private List<string> ownKerbalProductRecordingIds;
        private List<KerbalEnd> kerbalEnds;

        internal bool HasForeignJoin => !string.IsNullOrEmpty(ForeignJoinBranchPointId);

        /// <summary>The vessel recordings the walk stood on, start first (detail only).</summary>
        internal IReadOnlyList<string> VesselRecordingIds =>
            (IReadOnlyList<string>)vesselRecordingIds ?? Array.Empty<string>();

        internal IReadOnlyList<string> OwnEvaBranchPointIds =>
            (IReadOnlyList<string>)ownEvaBranchPointIds ?? Array.Empty<string>();

        /// <summary>Recordings of the vessel's own EVA crew: each kerbal's recording,
        /// chain siblings and switch-continuation segments.</summary>
        internal IReadOnlyCollection<string> OwnKerbalRecordingIds =>
            (IReadOnlyCollection<string>)ownKerbalRecordingIds ?? Array.Empty<string>();

        /// <summary>Ground parts the own EVA crew placed (detail only).</summary>
        internal IReadOnlyList<string> OwnKerbalProductRecordingIds =>
            (IReadOnlyList<string>)ownKerbalProductRecordingIds ?? Array.Empty<string>();

        internal List<KerbalEnd> KerbalEnds => kerbalEnds;

        internal void AddVesselRecordingId(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (vesselRecordingIds == null) vesselRecordingIds = new List<string>();
            if (!vesselRecordingIds.Contains(id)) vesselRecordingIds.Add(id);
        }

        internal void AddOwnEvaBranchPointId(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (ownEvaBranchPointIds == null) ownEvaBranchPointIds = new List<string>();
            ownEvaBranchPointIds.Add(id);
        }

        internal void AddHoppedBoardBranchPointId(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (hoppedBoardBranchPointIds == null)
                hoppedBoardBranchPointIds = new HashSet<string>(StringComparer.Ordinal);
            hoppedBoardBranchPointIds.Add(id);
        }

        internal bool HasHoppedBoard(string id)
            => !string.IsNullOrEmpty(id)
               && hoppedBoardBranchPointIds != null
               && hoppedBoardBranchPointIds.Contains(id);

        internal void AddOwnKerbalRecordingId(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (ownKerbalRecordingIds == null)
                ownKerbalRecordingIds = new HashSet<string>(StringComparer.Ordinal);
            ownKerbalRecordingIds.Add(id);
        }

        internal bool IsOwnKerbalRecording(string id)
            => !string.IsNullOrEmpty(id)
               && ownKerbalRecordingIds != null
               && ownKerbalRecordingIds.Contains(id);

        internal void AddOwnKerbalProductRecordingId(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (ownKerbalProductRecordingIds == null)
                ownKerbalProductRecordingIds = new List<string>();
            if (!ownKerbalProductRecordingIds.Contains(id))
                ownKerbalProductRecordingIds.Add(id);
        }

        internal void AddKerbalEnd(string kerbalRecordingId, BranchPoint bp)
        {
            if (bp == null) return;
            if (kerbalEnds == null) kerbalEnds = new List<KerbalEnd>();
            kerbalEnds.Add(new KerbalEnd
            {
                KerbalRecordingId = kerbalRecordingId,
                BranchPointId = bp.Id,
                BranchPointType = bp.Type,
            });
        }

        /// <summary>
        /// Every recording the walk crossed (vessel segments, own EVA crew, their
        /// placed parts) - the history a Re-Fly of this slot rewrites. Detail only.
        /// </summary>
        internal HashSet<string> CollectStretchRecordingIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (vesselRecordingIds != null)
                for (int i = 0; i < vesselRecordingIds.Count; i++) ids.Add(vesselRecordingIds[i]);
            if (ownKerbalRecordingIds != null)
                foreach (var id in ownKerbalRecordingIds) ids.Add(id);
            if (ownKerbalProductRecordingIds != null)
                for (int i = 0; i < ownKerbalProductRecordingIds.Count; i++)
                    ids.Add(ownKerbalProductRecordingIds[i]);
            return ids;
        }
    }
}
