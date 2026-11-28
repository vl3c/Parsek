using System.Collections.Generic;

namespace Parsek
{
    /// <summary>A blast as stock's FXMonger would queue it after its same-frame merge.</summary>
    internal struct FxMongerPlannedBlast
    {
        public Vector3d Position;
        /// <summary>The first merged source's power after the <c>minPower</c> floor (a merge never adds power).</summary>
        public double Power;
        /// <summary>How many sources merged into this blast.</summary>
        public int Sources;
    }

    /// <summary>
    /// Stock FXMonger's same-frame blast merge, pure (IL of <c>FXMonger.explode(Part, Vector3d,
    /// double)</c>, KSP 1.12.5): a power below <c>minPower</c> (0.001) is raised to it; the new
    /// blast is compared with each blast queued this frame in queue order, and the FIRST whose
    /// squared distance is below <c>minSqrBlast</c> (<c>minDistanceFromOtherBlasts</c> 10 m,
    /// squared in float) takes it: its position becomes the power-weighted mean
    /// <c>(q.position * q.power + pos * howHard) / (q.power + howHard)</c> (the plain midpoint when
    /// that sum is 0), its power is left as it was, and the new blast is dropped; otherwise the
    /// blast is queued. <c>LateUpdate</c> spawns every queued blast at
    /// <c>explosions[(int)(Clamp01(power) * (n - 1))]</c> and clears the queue, so blasts of
    /// different frames never merge. X0 U1 measured the same (two merged 0.7 blasts draw the
    /// prefab of 0.7).
    /// </summary>
    internal static class FxMongerBlastMerge
    {
        internal const double MinPower = 0.001;
        internal const float MinDistanceFromOtherBlastsM = 10f;
        /// <summary><c>minSqrBlast</c>: 10f * 10f, exact in float.</summary>
        internal const double MinSqrBlast = 100.0;

        /// <summary>Stock's <c>if (howHard &lt; minPower) howHard = minPower</c>.</summary>
        internal static double FloorPower(double howHard)
        {
            return howHard < MinPower ? MinPower : howHard;
        }

        /// <summary>The index of the first queued blast <paramref name="blastPos"/> merges into, or -1.</summary>
        internal static int FindMergeTarget(IList<Vector3d> queuedPositions, Vector3d blastPos)
        {
            if (queuedPositions == null) return -1;
            for (int i = 0; i < queuedPositions.Count; i++)
            {
                if ((queuedPositions[i] - blastPos).sqrMagnitude < MinSqrBlast)
                    return i;
            }
            return -1;
        }

        /// <summary>The merged blast's new position (its power stays <paramref name="queuedPower"/>).</summary>
        internal static Vector3d MergedPosition(Vector3d queuedPosition, double queuedPower,
            Vector3d blastPos, double howHard)
        {
            double sum = queuedPower + howHard;
            if (sum != 0.0)
                return (queuedPosition * queuedPower + blastPos * howHard) / sum;
            return (queuedPosition + blastPos) / 2.0;
        }

        /// <summary>
        /// The blasts FXMonger spawns for <paramref name="powers"/> exploded at
        /// <paramref name="positions"/> in this order within one frame.
        /// </summary>
        internal static List<FxMongerPlannedBlast> Plan(IList<Vector3d> positions, IList<double> powers)
        {
            var blasts = new List<FxMongerPlannedBlast>();
            if (positions == null || powers == null) return blasts;
            int n = positions.Count < powers.Count ? positions.Count : powers.Count;
            var queued = new List<Vector3d>(n);
            for (int i = 0; i < n; i++)
            {
                double howHard = FloorPower(powers[i]);
                int target = FindMergeTarget(queued, positions[i]);
                if (target >= 0)
                {
                    FxMongerPlannedBlast b = blasts[target];
                    b.Position = MergedPosition(b.Position, b.Power, positions[i], howHard);
                    b.Sources++;
                    blasts[target] = b;
                    queued[target] = b.Position;
                    continue;
                }
                blasts.Add(new FxMongerPlannedBlast { Position = positions[i], Power = howHard, Sources = 1 });
                queued.Add(positions[i]);
            }
            return blasts;
        }
    }

    /// <summary>
    /// The queue of one frame's blasts for a spawner that draws each blast at once (the silent
    /// stock copies): FXMonger's merge applied as blasts arrive, cleared when the frame number
    /// changes, as <c>LateUpdate</c> clears stock's queue. <see cref="Offer"/> answers whether a blast
    /// merges (and where the merged blast now sits); the caller spawns a blast that does not and
    /// then <see cref="Add"/>s it.
    /// </summary>
    internal sealed class FxMongerFrameQueue<T> where T : class
    {
        private int frame = int.MinValue;
        private readonly List<Vector3d> positions = new List<Vector3d>();
        private readonly List<double> powers = new List<double>();
        private readonly List<T> payloads = new List<T>();

        internal int Count => positions.Count;

        /// <summary>
        /// The index of the queued blast this one merges into, with its new position (its power
        /// unchanged) and the payload the caller queued with it, or -1 when the caller spawns a new
        /// blast. A new frame number empties the queue.
        /// </summary>
        internal int Offer(int frameNumber, Vector3d position, double power,
            out Vector3d mergedPosition, out T payload)
        {
            StartFrame(frameNumber);
            double howHard = FxMongerBlastMerge.FloorPower(power);
            int target = FxMongerBlastMerge.FindMergeTarget(positions, position);
            if (target < 0)
            {
                mergedPosition = position;
                payload = null;
                return -1;
            }
            payload = payloads[target];
            mergedPosition = FxMongerBlastMerge.MergedPosition(positions[target], powers[target], position, howHard);
            positions[target] = mergedPosition;
            return target;
        }

        /// <summary>Queues a blast the caller spawned this frame; returns its index.</summary>
        internal int Add(int frameNumber, Vector3d position, double power, T payload)
        {
            StartFrame(frameNumber);
            positions.Add(position);
            powers.Add(FxMongerBlastMerge.FloorPower(power));
            payloads.Add(payload);
            return positions.Count - 1;
        }

        private void StartFrame(int frameNumber)
        {
            if (frameNumber == frame) return;
            frame = frameNumber;
            positions.Clear();
            powers.Clear();
            payloads.Clear();
        }
    }
}
