using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Parsek.InGameTests.Spikes;

namespace Parsek.Tests.Spikes
{
    // Spike S10a (branch spike/mp-r0 only). Counts every Relative track section of a
    // save and its body-fixed copy (TrackSection.bodyFixedFrames). Pure measuring and
    // classifying here; the save walk at the bottom reads only through the production
    // tree loader (RecordingTree.Load) and trajectory codec
    // (RecordingSidecarStore.LoadTrajectorySidecarForTesting).

    internal enum RelativeCopyClass
    {
        None = 0,      // no body-fixed sample
        Single = 1,    // exactly one sample: playback never draws from it
        Partial = 2,   // two or more samples, but the section's UT range is not covered
        Complete = 3,  // two or more samples covering [startUT, endUT]
    }

    internal struct RelativeCopyMeasure
    {
        public int Frames;
        public int BodyFixedFrames;
        public double StartUT;
        public double EndUT;
        public double DurationSeconds;
        public double FramesFirstUT;   // NaN without frames
        public double FramesLastUT;
        public double BodyFixedFirstUT; // NaN without body-fixed frames
        public double BodyFixedLastUT;
        public double CoveredSeconds;   // overlap of [bfFirst, bfLast] with [startUT, endUT]; 0 below two samples
        public double GapStartSeconds;  // startUT .. first body-fixed sample (duration when below two samples)
        public double GapEndSeconds;    // last body-fixed sample .. endUT (duration when below two samples)
        public double CoverageFraction; // CoveredSeconds / duration (zero-length section: 1 when complete)
        public double FramesGapStartSeconds; // startUT .. first relative frame (duration without frames)
        public double FramesGapEndSeconds;   // last relative frame .. endUT (duration without frames)
        public bool BodyFixedMatchesFrames;  // same sample count and the same first / last UT as the frames
        public RelativeCopyClass Class;
    }

    internal static class RelativeCopyCensus
    {
        // A copy whose ends sit within this many seconds of the section's ends counts as
        // covering it. Playback itself is exact (BodyFixedPrimaryCoversPlaybackUT uses
        // 1e-6), and the recorder writes a shadow for the same UT as each frame, so a
        // copy that matches the frames is exact to the bit; the gap columns keep the raw
        // numbers for any other tolerance.
        internal const double CoverageToleranceSeconds = 1e-3;

        internal const int MinBodyFixedSamples = 2;

        internal const string ClassEva = "eva";
        internal const string ClassParentAnchored = "parent-anchored";
        internal const string ClassDocking = "docking";
        internal const string ClassActive = "active";
        internal const string ClassBackground = "background";
        internal const string ClassCheckpoint = "checkpoint";

        internal static RelativeCopyMeasure Measure(TrackSection section)
        {
            var m = new RelativeCopyMeasure
            {
                Frames = section.frames?.Count ?? 0,
                BodyFixedFrames = section.bodyFixedFrames?.Count ?? 0,
                StartUT = section.startUT,
                EndUT = section.endUT,
                FramesFirstUT = double.NaN,
                FramesLastUT = double.NaN,
                BodyFixedFirstUT = double.NaN,
                BodyFixedLastUT = double.NaN,
            };
            m.DurationSeconds = Math.Max(0.0, section.endUT - section.startUT);

            m.FramesGapStartSeconds = m.DurationSeconds;
            m.FramesGapEndSeconds = m.DurationSeconds;
            if (m.Frames > 0)
            {
                m.FramesFirstUT = section.frames[0].ut;
                m.FramesLastUT = section.frames[m.Frames - 1].ut;
                m.FramesGapStartSeconds = Math.Max(0.0, m.FramesFirstUT - section.startUT);
                m.FramesGapEndSeconds = Math.Max(0.0, section.endUT - m.FramesLastUT);
            }
            if (m.BodyFixedFrames > 0)
            {
                // First and last sample, as playback reads them.
                m.BodyFixedFirstUT = section.bodyFixedFrames[0].ut;
                m.BodyFixedLastUT = section.bodyFixedFrames[m.BodyFixedFrames - 1].ut;
            }
            m.BodyFixedMatchesFrames = m.Frames > 0
                && m.BodyFixedFrames == m.Frames
                && Math.Abs(m.BodyFixedFirstUT - m.FramesFirstUT) <= CoverageToleranceSeconds
                && Math.Abs(m.BodyFixedLastUT - m.FramesLastUT) <= CoverageToleranceSeconds;

            if (m.BodyFixedFrames < MinBodyFixedSamples)
            {
                m.Class = m.BodyFixedFrames == 0 ? RelativeCopyClass.None : RelativeCopyClass.Single;
                m.CoveredSeconds = 0.0;
                m.GapStartSeconds = m.DurationSeconds;
                m.GapEndSeconds = m.DurationSeconds;
                m.CoverageFraction = 0.0;
                return m;
            }

            double lo = Math.Max(m.BodyFixedFirstUT, section.startUT);
            double hi = Math.Min(m.BodyFixedLastUT, section.endUT);
            m.CoveredSeconds = Math.Max(0.0, hi - lo);
            m.GapStartSeconds = Math.Max(0.0, m.BodyFixedFirstUT - section.startUT);
            m.GapEndSeconds = Math.Max(0.0, section.endUT - m.BodyFixedLastUT);

            bool covers = m.GapStartSeconds <= CoverageToleranceSeconds
                && m.GapEndSeconds <= CoverageToleranceSeconds;
            m.Class = covers ? RelativeCopyClass.Complete : RelativeCopyClass.Partial;
            if (m.DurationSeconds > 0.0)
                m.CoverageFraction = Math.Min(1.0, m.CoveredSeconds / m.DurationSeconds);
            else
                m.CoverageFraction = covers ? 1.0 : 0.0;
            return m;
        }

        /// <summary>
        /// The recorder source class of one Relative section, first match wins:
        /// eva (the recording is a kerbal on EVA), parent-anchored (the recording carries
        /// ParentAnchorRecordingId: debris or a controlled-decoupled child), docking (the
        /// recording is a parent or child of a Dock or Undock branch point), then the
        /// section's own producer (active, background, checkpoint).
        /// </summary>
        internal static string ClassifySource(
            bool isEva, bool isParentAnchored, bool hasDockLink, TrackSectionSource sectionSource)
        {
            if (isEva) return ClassEva;
            if (isParentAnchored) return ClassParentAnchored;
            if (hasDockLink) return ClassDocking;
            switch (sectionSource)
            {
                case TrackSectionSource.Active: return ClassActive;
                case TrackSectionSource.Background: return ClassBackground;
                case TrackSectionSource.Checkpoint: return ClassCheckpoint;
                default: return "source-" + ((int)sectionSource).ToString(CultureInfo.InvariantCulture);
            }
        }

        internal static bool HasDockLink(ICollection<BranchPointType> links)
        {
            return links != null
                && (links.Contains(BranchPointType.Dock) || links.Contains(BranchPointType.Undock));
        }

        /// <summary>Branch point types a recording takes part in, sorted, joined by '|'.</summary>
        internal static string FormatLinks(ICollection<BranchPointType> links)
        {
            if (links == null || links.Count == 0) return "";
            var names = new List<string>();
            foreach (BranchPointType t in links) names.Add(t.ToString());
            names.Sort(StringComparer.Ordinal);
            return string.Join("|", names.ToArray());
        }

        internal static string CopyClassName(RelativeCopyClass c)
        {
            switch (c)
            {
                case RelativeCopyClass.Complete: return "complete";
                case RelativeCopyClass.Partial: return "partial";
                case RelativeCopyClass.Single: return "single";
                default: return "none";
            }
        }

        // No player-typed text: no vessel, kerbal, tree or save name beyond the label the
        // caller passes (the copy folder's name). Recording ids are generated GUIDs.
        internal static readonly string[] Header =
        {
            "save_label", "tree_index", "tree_active", "recording_id", "section_index",
            "source_class", "section_source", "environment", "copy_class",
            "start_ut", "end_ut", "duration_s",
            "frames", "frames_first_ut", "frames_last_ut",
            "bf_frames", "bf_first_ut", "bf_last_ut",
            "bf_covered_s", "bf_gap_start_s", "bf_gap_end_s", "bf_coverage_frac",
            "frames_gap_start_s", "frames_gap_end_s", "bf_matches_frames",
            "is_eva", "is_debris", "parent_anchored", "bp_links",
            "has_anchor_recording", "anchor_vessel_set", "is_boundary_seam",
        };

        internal static object[] Row(
            string saveLabel, int treeIndex, bool treeActive, string recordingId, int sectionIndex,
            string sourceClass, TrackSection s, RelativeCopyMeasure m,
            bool isEva, bool isDebris, bool parentAnchored, string bpLinks)
        {
            return new object[]
            {
                saveLabel, treeIndex, treeActive, recordingId, sectionIndex,
                sourceClass, s.source.ToString(), s.environment.ToString(), CopyClassName(m.Class),
                m.StartUT, m.EndUT, m.DurationSeconds,
                m.Frames, NullIfNaN(m.FramesFirstUT), NullIfNaN(m.FramesLastUT),
                m.BodyFixedFrames, NullIfNaN(m.BodyFixedFirstUT), NullIfNaN(m.BodyFixedLastUT),
                m.CoveredSeconds, m.GapStartSeconds, m.GapEndSeconds, m.CoverageFraction,
                m.FramesGapStartSeconds, m.FramesGapEndSeconds, m.BodyFixedMatchesFrames,
                isEva, isDebris, parentAnchored, bpLinks,
                !string.IsNullOrEmpty(s.anchorRecordingId), s.anchorVesselId != 0, s.isBoundarySeam,
            };
        }

        private static object NullIfNaN(double v) => double.IsNaN(v) ? null : (object)v;

        // ---------------- save walk (file I/O) ----------------

        internal sealed class SaveCensus
        {
            public string Label;
            public int Trees;
            public int TreesActive;
            public int Recordings;
            public int SidecarsMissing;
            public int SidecarsFailed;
            public int SidecarsLoaded;
            public int Sections;
            public int RelativeSections;
            public readonly List<object[]> Rows = new List<object[]>();
            public readonly List<string> Faults = new List<string>();
        }

        internal static readonly string[] SummaryHeader =
        {
            "save_label", "trees", "trees_active", "recordings", "sidecars_loaded",
            "sidecars_missing", "sidecars_failed", "sections", "relative_sections", "faults",
        };

        internal static object[] SummaryRow(SaveCensus c)
        {
            return new object[]
            {
                c.Label, c.Trees, c.TreesActive, c.Recordings, c.SidecarsLoaded,
                c.SidecarsMissing, c.SidecarsFailed, c.Sections, c.RelativeSections,
                string.Join(" ; ", c.Faults.ToArray()),
            };
        }

        /// <summary>
        /// Null when the folder is safe to read; else why not. A save inside a KSP install
        /// (<c>&lt;install&gt;/saves/&lt;name&gt;</c>, a sibling <c>GameData</c> two levels up) is
        /// refused: the census reads copies only.
        /// </summary>
        internal static string RefuseReason(string saveDir)
        {
            if (string.IsNullOrEmpty(saveDir)) return "empty path";
            if (!Directory.Exists(saveDir)) return "no such folder";
            if (!File.Exists(Path.Combine(saveDir, "persistent.sfs"))) return "no persistent.sfs";
            string trimmed = saveDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            DirectoryInfo parent = Directory.GetParent(trimmed);
            if (parent != null && parent.Parent != null
                && string.Equals(parent.Name, "saves", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(parent.Parent.FullName, "GameData")))
                return "folder is a live save inside a KSP install; copy it out first";
            return null;
        }

        internal static string LabelFor(string saveDir)
        {
            string trimmed = saveDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? "save" : name;
        }

        internal static SaveCensus Run(string saveDir, string label)
        {
            var census = new SaveCensus { Label = label };
            ConfigNode root = ConfigNode.Load(Path.Combine(saveDir, "persistent.sfs"));
            if (root == null)
            {
                census.Faults.Add("persistent.sfs did not parse");
                return census;
            }
            ConfigNode game = root.GetNode("FLIGHTSTATE") != null ? root : (root.GetNode("GAME") ?? root);
            ConfigNode scenario = null;
            foreach (ConfigNode n in game.GetNodes("SCENARIO"))
            {
                if (string.Equals(n.GetValue("name"), "ParsekScenario", StringComparison.Ordinal))
                {
                    scenario = n;
                    break;
                }
            }
            if (scenario == null)
            {
                census.Faults.Add("no ParsekScenario node");
                return census;
            }

            ConfigNode[] treeNodes = scenario.GetNodes("RECORDING_TREE");
            for (int t = 0; t < treeNodes.Length; t++)
            {
                RecordingTree tree = RecordingTree.Load(treeNodes[t]);
                bool active = string.Equals(treeNodes[t].GetValue("isActive"), "True",
                    StringComparison.OrdinalIgnoreCase);
                census.Trees++;
                if (active) census.TreesActive++;

                var links = new Dictionary<string, HashSet<BranchPointType>>(StringComparer.Ordinal);
                foreach (BranchPoint bp in tree.BranchPoints)
                {
                    AddLinks(links, bp.ParentRecordingIds, bp.Type);
                    AddLinks(links, bp.ChildRecordingIds, bp.Type);
                }

                var ids = new List<string>(tree.Recordings.Keys);
                ids.Sort(StringComparer.Ordinal);
                foreach (string id in ids)
                {
                    Recording rec = tree.Recordings[id];
                    census.Recordings++;
                    if (!RecordingPaths.ValidateRecordingId(rec.RecordingId, RecordingIdValidationLogContext.Test))
                    {
                        census.Faults.Add("invalid recording id");
                        continue;
                    }
                    string prec = Path.Combine(saveDir, RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                    if (!File.Exists(prec))
                    {
                        census.SidecarsMissing++;
                        continue;
                    }
                    bool loaded;
                    try
                    {
                        loaded = RecordingSidecarStore.LoadTrajectorySidecarForTesting(prec, rec);
                    }
                    catch (Exception ex)
                    {
                        loaded = false;
                        census.Faults.Add(rec.RecordingId + ": " + ex.GetType().Name);
                    }
                    if (!loaded)
                    {
                        census.SidecarsFailed++;
                        continue;
                    }
                    census.SidecarsLoaded++;

                    links.TryGetValue(rec.RecordingId, out HashSet<BranchPointType> recLinks);
                    bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
                    bool parentAnchored = !string.IsNullOrEmpty(rec.ParentAnchorRecordingId);
                    bool dock = HasDockLink(recLinks);
                    string linkText = FormatLinks(recLinks);

                    List<TrackSection> sections = rec.TrackSections;
                    for (int i = 0; sections != null && i < sections.Count; i++)
                    {
                        census.Sections++;
                        TrackSection s = sections[i];
                        if (s.referenceFrame != ReferenceFrame.Relative) continue;
                        census.RelativeSections++;
                        RelativeCopyMeasure m = Measure(s);
                        string cls = ClassifySource(isEva, parentAnchored, dock, s.source);
                        census.Rows.Add(Row(label, t, active, rec.RecordingId, i, cls, s, m,
                            isEva, rec.IsDebris, parentAnchored, linkText));
                    }
                }
            }
            return census;
        }

        private static void AddLinks(
            Dictionary<string, HashSet<BranchPointType>> links, List<string> ids, BranchPointType type)
        {
            if (ids == null) return;
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!links.TryGetValue(id, out HashSet<BranchPointType> set))
                {
                    set = new HashSet<BranchPointType>();
                    links[id] = set;
                }
                set.Add(type);
            }
        }

        /// <summary>Writes (replacing) <c>&lt;outDir&gt;/relative_census.csv</c> and <c>load_summary.csv</c>.</summary>
        internal static void Write(string outDir, SaveCensus census)
        {
            Directory.CreateDirectory(outDir);
            var sb = new StringBuilder();
            sb.Append(SpikeCsv.FormatRow(Header)).Append('\n');
            foreach (object[] row in census.Rows)
                sb.Append(SpikeCsv.FormatRow(row)).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "relative_census.csv"), sb.ToString(), new UTF8Encoding(false));

            var sum = new StringBuilder();
            sum.Append(SpikeCsv.FormatRow(SummaryHeader)).Append('\n');
            sum.Append(SpikeCsv.FormatRow(SummaryRow(census))).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "load_summary.csv"), sum.ToString(), new UTF8Encoding(false));
        }

        /// <summary>The save list: one path, or several separated by ';'.</summary>
        internal static List<string> ParseSaveList(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string part in raw.Split(';'))
            {
                string p = part.Trim();
                if (p.Length > 0) list.Add(p);
            }
            return list;
        }
    }
}
