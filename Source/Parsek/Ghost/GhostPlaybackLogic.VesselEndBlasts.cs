using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // A Destroyed ending's explosion, the way stock's vessel crash makes it: one Part.explode per
    // part still on the ghost, merged by FXMonger (design-part-ghost-parity.md G.8 item 5, fix Y7).
    internal static partial class GhostPlaybackLogic
    {
        /// <summary>How many planned blasts the end-explosion summary line lists one by one.</summary>
        internal const int VesselEndBlastLogLimit = 20;

        /// <summary>
        /// The parts a Destroyed ending explodes, in the snapshot's PART order: every part still on
        /// the ghost (<paramref name="logicalPartIds"/>) whose position resolves, each with stock's
        /// power (its prefab potential plus the speed term at <paramref name="surfaceSpeedMps"/>,
        /// <see cref="StockPartExplosionPower"/>). Pure: positions and potentials come from the
        /// delegates. Returns the number of present parts skipped for having no position.
        /// </summary>
        internal static int CollectVesselEndBlastParts(
            ConfigNode snapshot,
            HashSet<uint> logicalPartIds,
            Func<uint, Vector3d?> positionOf,
            ExplosionPotentialLookup lookup,
            double surfaceSpeedMps,
            List<uint> pids,
            List<Vector3d> positions,
            List<double> powers)
        {
            int skipped = 0;
            if (snapshot == null || logicalPartIds == null || logicalPartIds.Count == 0 || positionOf == null)
                return skipped;
            ConfigNode[] partNodes = snapshot.GetNodes("PART");
            if (partNodes == null) return skipped;
            var potentialByName = new Dictionary<string, float>(StringComparer.Ordinal);
            var seen = new HashSet<uint>();
            for (int i = 0; i < partNodes.Length; i++)
            {
                string pidStr = partNodes[i].GetValue("persistentId");
                if (pidStr == null
                    || !uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid)
                    || pid == 0 || !logicalPartIds.Contains(pid) || !seen.Add(pid))
                    continue;
                Vector3d? pos = positionOf(pid);
                if (!pos.HasValue)
                {
                    skipped++;
                    continue;
                }
                string name = GhostVisualBuilder.TryExtractPartName(
                    partNodes[i].GetValue("name") ?? partNodes[i].GetValue("part")) ?? string.Empty;
                if (!potentialByName.TryGetValue(name, out float potential))
                {
                    potential = ResolvePartExplosionPotential(name.Length > 0 ? name : null, lookup);
                    potentialByName[name] = potential;
                }
                pids.Add(pid);
                positions.Add(pos.Value);
                powers.Add(StockPartExplosionPower(potential, surfaceSpeedMps));
            }
            return skipped;
        }

        /// <summary>
        /// The grep-stable summary of an end explosion: the inputs, then each planned blast (at most
        /// <see cref="VesselEndBlastLogLimit"/>) as <c>[i] power= prefabIndex= sources=</c>.
        /// </summary>
        internal static string FormatVesselEndBlastPlan(
            int parts, int skipped, double surfaceSpeedMps, string speedSource, string route,
            IList<FxMongerPlannedBlast> blasts)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            int count = blasts != null ? blasts.Count : 0;
            sb.Append("parts=").Append(parts.ToString(ic))
                .Append(" skipped=").Append(skipped.ToString(ic))
                .Append(" srfSpeed=").Append(surfaceSpeedMps.ToString("F1", ic))
                .Append(" speedSource=").Append(speedSource ?? "none")
                .Append(" speedOffset=").Append(StockPartExplosionSpeedOffset(surfaceSpeedMps).ToString("0.##", ic))
                .Append(" route=").Append(route ?? "-")
                .Append(" blasts=").Append(count.ToString(ic));
            for (int i = 0; i < count && i < VesselEndBlastLogLimit; i++)
            {
                FxMongerPlannedBlast b = blasts[i];
                sb.Append(" [").Append(i.ToString(ic)).Append("] power=").Append(b.Power.ToString("0.###", ic))
                    .Append(" prefabIndex=")
                    .Append(StockExplosionPrefabCache.ResolvePrefabIndex(b.Power, FallbackExplosionLook.StockPrefabCount).ToString(ic))
                    .Append(" sources=").Append(b.Sources.ToString(ic));
            }
            if (count > VesselEndBlastLogLimit)
                sb.Append(" ... ").Append((count - VesselEndBlastLogLimit).ToString(ic)).Append(" more");
            return sb.ToString();
        }

        /// <summary>
        /// Plays a Destroyed ending as stock's crash would: <c>Part.explode</c> for every part still
        /// on the ghost, at the part, in one frame, so FXMonger's own merge combines them. Under the
        /// pause menu, or with no live FXMonger, each part's blast is drawn as a silent stock copy
        /// (<see cref="GhostVisualBuilder.SpawnFallbackExplosionFx"/>), which merges the same way,
        /// or, before the session's first flight, the planned (merged) blasts hand-built. False, with nothing
        /// drawn, when no part resolves; the caller then plays its single length-sized blast.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryPlayVesselEndBlasts(
            GhostPlaybackState state, ConfigNode snapshot, float vesselLength, bool audioPaused, string context)
        {
            if (state == null || state.ghost == null) return false;
            GameObject ghost = state.ghost;
            double speed = PartExplosionSurfaceSpeed(state, out string speedSource);
            var pids = new List<uint>();
            var positions = new List<Vector3d>();
            var powers = new List<double>();
            int skipped = CollectVesselEndBlastParts(snapshot, state.logicalPartIds,
                pid => ResolveVisibleGhostPartPosition(ghost, pid), null, speed, pids, positions, powers);
            if (positions.Count == 0)
            {
                ParsekLog.VerboseRateLimited("ExplosionFx", $"vessel-end-no-parts-{context}",
                    $"End explosion for {context}: no ghost part to explode (snapshot={(snapshot != null)} " +
                    $"logicalParts={(state.logicalPartIds?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} " +
                    $"skipped={skipped.ToString(CultureInfo.InvariantCulture)}); playing the length-sized blast", 10.0);
                return false;
            }

            List<FxMongerPlannedBlast> plan = FxMongerBlastMerge.Plan(positions, powers);
            string route;
            if (!audioPaused && GhostVisualBuilder.IsFxMongerLive())
            {
                int queued = 0;
                string failure = null;
                for (int i = 0; i < positions.Count; i++)
                {
                    if (GhostVisualBuilder.TryTriggerStockExplosionFx((Vector3)positions[i], powers[i], out failure))
                        queued++;
                    else if (queued == 0)
                        break;
                }
                if (queued > 0)
                {
                    route = queued == positions.Count ? "fxmonger" : "fxmonger-partial";
                    if (queued < positions.Count)
                        ParsekLog.Warn("ExplosionFx",
                            $"End explosion for {context}: FXMonger queued {queued.ToString(CultureInfo.InvariantCulture)}/" +
                            $"{positions.Count.ToString(CultureInfo.InvariantCulture)} part blasts: {failure}");
                }
                else
                {
                    ParsekLog.Warn("ExplosionFx",
                        $"FXMonger.Explode did not queue the end explosion for {context}; drawing stock copies: {failure}");
                    route = "copy-after-failure";
                    route += SpawnFallbackBlasts(positions, powers, plan, vesselLength);
                }
            }
            else
            {
                route = audioPaused ? "copy-paused" : "copy-no-fxmonger";
                route += SpawnFallbackBlasts(positions, powers, plan, vesselLength);
            }

            ParsekLog.VerboseRateLimited("ExplosionFx", $"vessel-end-blasts-{context}",
                $"End explosion for {context}: " +
                FormatVesselEndBlastPlan(positions.Count, skipped, speed, speedSource, route, plan), 10.0);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3d? ResolveVisibleGhostPartPosition(GameObject ghost, uint pid)
        {
            Transform t = GhostVisualBuilder.FindGhostPartTransform(ghost, pid);
            if (t == null || !t.gameObject.activeSelf) return null;
            return (Vector3d)t.position;
        }

        // With stock copies kept, one copy per part, which the copy path merges as FXMonger does;
        // without them (no flight yet this session) the hand-built explosion per planned blast,
        // since it does not merge. Returns the route suffix.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SpawnFallbackBlasts(List<Vector3d> positions, List<double> powers,
            List<FxMongerPlannedBlast> plan, float vesselLength)
        {
            if (StockExplosionPrefabCache.HasCopies)
            {
                for (int i = 0; i < positions.Count; i++)
                    GhostVisualBuilder.SpawnFallbackExplosionFx((Vector3)positions[i], powers[i], vesselLength);
                return "";
            }
            for (int i = 0; i < plan.Count; i++)
            {
                double p = plan[i].Power;
                GhostVisualBuilder.SpawnFallbackExplosionFx((Vector3)plan[i].Position,
                    p < 0.0 ? 0.0 : (p > 1.0 ? 1.0 : p), vesselLength);
            }
            return "-handbuilt";
        }
    }
}
