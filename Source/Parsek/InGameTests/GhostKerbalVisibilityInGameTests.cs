using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live half of <see cref="GhostKerbalVisibility"/>: samples every live flight-scene ghost
    /// on consecutive rendered frames for up to <see cref="MaxWaitSeconds"/> of real time and
    /// counts, per kerbal, how many ghosts draw that kerbal's part. A seated kerbal shown both
    /// in the seat and as an EVA ghost, or carried by a live ghost and drawn on none, for
    /// <see cref="GhostKerbalVisibility.MinBadRunFrames"/> frames in a row fails the cell; a
    /// sampling window in which no live ghost carried a kerbal part skips, naming the counts.
    /// </summary>
    public class GhostKerbalVisibilityInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "SeatGhostKerbal";

        /// <summary>Real-time budget for the sampling loop.</summary>
        internal const float MaxWaitSeconds = 6f;

        /// <summary>Give up early when no live ghost at all was seen for this many frames.</summary>
        internal const int NoGhostGiveUpFrames = 60;

        private struct KerbalPart
        {
            internal uint Pid;
            internal string Crew;
        }

        [InGameTest(Category = "SeatGhostKerbal", Scene = GameScenes.FLIGHT,
            Description = "Every kerbal a live flight ghost carries as a kerbal part (seated on a vehicle ghost, or an EVA ghost) is drawn exactly once: never in the seat and beside it, never on no ghost (test-gap list, command seat replay)")]
        public IEnumerator EveryGhostKerbalIsDrawnExactlyOnce()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || flight.Engine == null)
            {
                InGameAssert.Skip("needs a ParsekFlight instance with a playback engine (FLIGHT scene)");
                yield break;
            }

            var partsByIndex = new Dictionary<int, List<KerbalPart>>();
            var runs = new Dictionary<string, KeyValuePair<GhostKerbalVisibility.FrameVerdict, int>>();
            var kerbals = new SortedSet<string>(System.StringComparer.Ordinal);
            var failures = new List<string>();
            int frames = 0, framesWithKerbal = 0, once = 0, twice = 0, missing = 0;
            int longestTwice = 0, longestMissing = 0, framesWithoutGhost = 0;
            float started = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - started < MaxWaitSeconds)
            {
                yield return new WaitForEndOfFrame();
                if (ParsekFlight.Instance != flight || flight.Engine == null)
                    break;
                frames++;
                var occurrences = new List<GhostKerbalVisibility.Occurrence>();
                int liveGhosts = SampleFrame(flight.Engine, partsByIndex, occurrences);
                framesWithoutGhost = liveGhosts == 0 ? framesWithoutGhost + 1 : 0;
                if (framesWithoutGhost >= NoGhostGiveUpFrames && framesWithKerbal == 0)
                    break;
                if (occurrences.Count == 0)
                {
                    GhostKerbalVisibility.Accumulate(
                        new SortedDictionary<string, GhostKerbalVisibility.FrameVerdict>(),
                        runs, ref longestTwice, ref longestMissing);
                    continue;
                }
                framesWithKerbal++;
                var verdicts = GhostKerbalVisibility.ClassifyFrame(occurrences, out var drawn);
                foreach (var kv in verdicts)
                {
                    kerbals.Add(kv.Key);
                    switch (kv.Value)
                    {
                        case GhostKerbalVisibility.FrameVerdict.Once: once++; break;
                        case GhostKerbalVisibility.FrameVerdict.Twice: twice++; break;
                        default: missing++; break;
                    }
                    if (kv.Value != GhostKerbalVisibility.FrameVerdict.Once && failures.Count < 10)
                    {
                        failures.Add("frame=" + frames.ToString(IC) + " kerbal='" + kv.Key + "' verdict="
                            + kv.Value + " drawn=" + drawn[kv.Key].ToString(IC)
                            + " ghosts=" + DescribeCarriers(occurrences, kv.Key));
                    }
                }
                GhostKerbalVisibility.Accumulate(verdicts, runs, ref longestTwice, ref longestMissing);
            }

            ParsekLog.Info(Tag, GhostKerbalVisibility.FormatSummary(HighLogic.LoadedScene.ToString(), frames,
                framesWithKerbal, once, twice, missing, longestTwice, longestMissing,
                string.Join(",", new List<string>(kerbals).ToArray())));
            for (int i = 0; i < failures.Count; i++)
                ParsekLog.Info(Tag, "bad frame " + failures[i]);

            switch (GhostKerbalVisibility.DecideOutcome(once, longestTwice, longestMissing))
            {
                case GhostKerbalVisibility.CellOutcome.Fail:
                    InGameAssert.Fail("a ghost kerbal was drawn " + (longestTwice >= longestMissing
                        ? "twice for " + longestTwice.ToString(IC)
                        : "on no ghost for " + longestMissing.ToString(IC))
                        + " consecutive frames: " + string.Join(" | ", failures.ToArray()));
                    yield break;
                case GhostKerbalVisibility.CellOutcome.Skip:
                    InGameAssert.Skip("no live flight ghost carried a kerbal part in the sampling window (frames="
                        + frames.ToString(IC) + " framesWithKerbal=" + framesWithKerbal.ToString(IC) + ")");
                    yield break;
            }
        }

        private static int SampleFrame(GhostPlaybackEngine engine, Dictionary<int, List<KerbalPart>> partsByIndex,
            List<GhostKerbalVisibility.Occurrence> occurrences)
        {
            // [ERS-exempt] (folder allowlisted): engine ghost slots are raw committed-list indices.
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            Dictionary<int, GameObject> ghosts = engine.GetGhostGameObjects();
            if (ghosts == null || committed == null) return 0;
            int live = 0;
            foreach (int idx in ghosts.Keys)
            {
                if (idx < 0 || idx >= committed.Count || committed[idx] == null) continue;
                if (!engine.TryGetGhostState(idx, out GhostPlaybackState state) || state == null
                    || state.ghost == null || !state.ghost.activeInHierarchy || !state.positionedThisFrame)
                    continue;
                live++;
                if (!partsByIndex.TryGetValue(idx, out List<KerbalPart> parts))
                {
                    parts = ReadKerbalParts(GhostVisualBuilder.GetGhostSnapshot(committed[idx]));
                    partsByIndex[idx] = parts;
                }
                for (int i = 0; i < parts.Count; i++)
                {
                    Transform t = GhostVisualBuilder.FindGhostPartTransform(state.ghost, parts[i].Pid);
                    occurrences.Add(new GhostKerbalVisibility.Occurrence
                    {
                        Crew = parts[i].Crew,
                        GhostIndex = idx,
                        Drawn = t != null && t.gameObject.activeInHierarchy,
                    });
                }
            }
            return live;
        }

        private static List<KerbalPart> ReadKerbalParts(ConfigNode snapshot)
        {
            var list = new List<KerbalPart>();
            if (snapshot == null) return list;
            ConfigNode[] partNodes = snapshot.GetNodes("PART");
            for (int i = 0; i < partNodes.Length; i++)
            {
                ConfigNode p = partNodes[i];
                if (!GhostKerbalVisibility.IsKerbalPartName(p.GetValue("name"))) continue;
                if (!uint.TryParse(p.GetValue("persistentId"), NumberStyles.Integer, IC, out uint pid)) continue;
                string[] crew = p.GetValues("crew");
                for (int c = 0; c < crew.Length; c++)
                    if (!string.IsNullOrEmpty(crew[c]))
                        list.Add(new KerbalPart { Pid = pid, Crew = crew[c] });
            }
            return list;
        }

        private static string DescribeCarriers(List<GhostKerbalVisibility.Occurrence> occurrences, string crew)
        {
            var parts = new List<string>();
            for (int i = 0; i < occurrences.Count; i++)
                if (occurrences[i].Crew == crew)
                    parts.Add("#" + occurrences[i].GhostIndex.ToString(IC) + (occurrences[i].Drawn ? ":drawn" : ":hidden"));
            return string.Join(",", parts.ToArray());
        }
    }
}
