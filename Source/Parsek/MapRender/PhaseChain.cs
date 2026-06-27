using System.Collections.Generic;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §6: the <see cref="GhostRenderChain"/> SUCCESSOR — the per-(member,
    /// cycle-instance) ordered list of <see cref="TrajectoryPhase"/>s for one ghost.
    ///
    /// <para><b>NEW, additive, NOT a replacement of <see cref="GhostRenderChain"/> yet.</b> It mirrors
    /// the chain's shape (keying by recording id + committed index + instance key, O(log n) locate,
    /// three-valued coverage) so Phase 3 can swap the spine to consume phases; the actual swap is a
    /// later phase, so this coexists with <see cref="GhostRenderChain"/> in Phase 1.</para>
    ///
    /// <para>It is PER MEMBER (one <see cref="CommittedIndex"/>), not per whole mission: a looped
    /// mission's launch→transfer→landing spans several committed members sequenced by the span clock
    /// (design §6 / §11.3). Phases are ordered by <see cref="TrajectoryPhase.StartUt"/>; interior gaps
    /// are allowed (a FlexibleSoi seam UT, or a <see cref="HoldPhase"/> which itself covers the span).</para>
    /// </summary>
    internal sealed class PhaseChain
    {
        internal string RecordingId { get; }
        /// <summary>Positional index into the committed-recordings list (the LoopUnitSet contract).</summary>
        internal int CommittedIndex { get; }
        /// <summary>Cycle/instance discriminator — distinguishes overlapping self-loop instances (design §10.8).</summary>
        internal int InstanceKey { get; }
        /// <summary>Phases ordered by <see cref="TrajectoryPhase.StartUt"/>.</summary>
        internal IReadOnlyList<TrajectoryPhase> Phases { get; }
        internal double WindowStartUt { get; }
        internal double WindowEndUt { get; }
        /// <summary>A producer declined re-aim for this window → assembled from the recorded trajectory as-is (design §6.9 / §7).</summary>
        internal bool IsFaithfulFallback { get; }

        internal PhaseChain(
            string recordingId,
            int committedIndex,
            int instanceKey,
            IReadOnlyList<TrajectoryPhase> phases,
            double windowStartUt,
            double windowEndUt,
            bool isFaithfulFallback = false)
        {
            RecordingId = recordingId;
            CommittedIndex = committedIndex;
            InstanceKey = instanceKey;
            Phases = phases ?? System.Array.Empty<TrajectoryPhase>();
            WindowStartUt = windowStartUt;
            WindowEndUt = windowEndUt;
            IsFaithfulFallback = isFaithfulFallback;
        }

        internal int PhaseCount => Phases.Count;

        /// <summary>
        /// O(log n) locate: the index of the phase containing <paramref name="ut"/> (assembled-chain
        /// clock), or -1 if <paramref name="ut"/> falls in a gap / outside all phases. A non-last phase
        /// owns <c>[StartUt, EndUt)</c>; the LAST phase owns <c>[StartUt, EndUt]</c> (inclusive end), so
        /// a boundary UT shared by two adjacent phases belongs to the LATER one — mirroring
        /// <see cref="GhostRenderChain.LocateSegmentIndex"/> exactly.
        /// </summary>
        internal int LocatePhaseIndex(double ut)
        {
            var phases = Phases;
            int n = phases.Count;
            if (n == 0) return -1;
            if (double.IsNaN(ut) || double.IsInfinity(ut)) return -1;

            // rightmost phase with StartUt <= ut
            int lo = 0, hi = n - 1, found = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (phases[mid].StartUt <= ut) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found < 0) return -1; // ut is before the first phase

            var p = phases[found];
            bool isLast = found == n - 1;
            // The last phase gets the inclusive end; non-last phases use the phase's own (half-open)
            // CoversUt so a subclass with special coverage (HoldPhase) is honoured.
            bool contains = isLast ? (ut >= p.StartUt && ut <= p.EndUt) : p.CoversUt(ut);
            return contains ? found : -1; // -1 = a gap after phases[found]
        }

        internal bool TryGetPhase(double ut, out TrajectoryPhase phase, out int index)
        {
            index = LocatePhaseIndex(ut);
            if (index >= 0) { phase = Phases[index]; return true; }
            phase = null;
            return false;
        }

        /// <summary>
        /// Classify a UT (already mapped into the assembled-chain clock by the sampler) into the
        /// three-valued <see cref="Coverage"/> — reusing the existing live enum. Outside the window →
        /// <see cref="Coverage.OutsideWindow"/>; inside the window but in no phase →
        /// <see cref="Coverage.InInteriorGap"/> (hold prior intent); otherwise
        /// <see cref="Coverage.InSegment"/>.
        ///
        /// <para>design §11.1: a mid-window TERMINAL retire (crash/impact mid-window) is NOT modelled
        /// here as an interior gap — the producer is expected to end the phase list at the recorded
        /// end-of-data so the post-terminal UT falls OUTSIDE the window → Hidden, not held. This method
        /// only classifies coverage against the supplied window + phases; the terminal trigger is the
        /// factory's concern.</para>
        /// </summary>
        internal Coverage ClassifyCoverage(double ut, out TrajectoryPhase phase, out int index)
        {
            phase = null;
            index = -1;
            if (double.IsNaN(ut) || double.IsInfinity(ut))
                return Coverage.OutsideWindow;
            if (ut < WindowStartUt || ut > WindowEndUt)
                return Coverage.OutsideWindow;
            index = LocatePhaseIndex(ut);
            if (index >= 0)
            {
                phase = Phases[index];
                return Coverage.InSegment;
            }
            return Coverage.InInteriorGap;
        }
    }
}
