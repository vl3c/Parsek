using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Everything one profile build reads (docs/dev/design-mission-profile.md section 10). The
    /// UI fills it: the Missions module never reads a store, ERS or a live body itself, so the
    /// build adds no headless leak. Only <see cref="Tree"/> and <see cref="BodyFacts"/> are
    /// required; every other field has a pure fallback.
    /// </summary>
    internal sealed class MissionProfileRequest
    {
        /// <summary>The viewed mission (name, id, include set, loop). Null: the whole tree.</summary>
        public Mission Mission;
        public RecordingTree Tree;
        /// <summary>The window's cached structure (GetMissionView); built when null.</summary>
        public MissionStructure Structure;
        /// <summary>The window's cached through line (GetMissionView); built when null.</summary>
        public MissionThroughLineView ThroughLine;
        /// <summary>The Missions tab's vessel rows of the tree (GetVesselRows); built the same
        /// way when null.</summary>
        public List<MissionVesselRow> VesselRows;
        /// <summary>MissionVesselNaming's names (numbered vessels, partner phrases).</summary>
        public IReadOnlyDictionary<string, string> VesselNames;
        /// <summary>MissionVesselNaming's partner legs (another mission's vessel).</summary>
        public ICollection<string> PartnerLegIds;
        /// <summary>The effective recording ids (ERS, computed by the UI with the session
        /// suppression ignored, section 5). Null: every recording of the tree.</summary>
        public ICollection<string> EffectiveRecordingIds;
        /// <summary>The supersede rows (old -> new) the Re-Fly bridge follows.</summary>
        public IList<RecordingSupersedeRelation> SupersedeRelations;
        /// <summary>Body facts (the live body in game, the stock table headless).</summary>
        public Func<string, RecordingOptimizer.BodyFacts> BodyFacts;
        /// <summary>The first row's date (the Log's formatter; KSPUtil in game).</summary>
        public Func<double, string> DateFormatter;
        /// <summary>Internal part name -> player title (the Log's resolver).</summary>
        public Func<string, string> PartTitleResolver;
        /// <summary>The other side of a Dock / Board for a viewer recording (the Log's resolver).</summary>
        public Func<BranchPoint, string, string> MergePartnerResolver;
        /// <summary>Science / funds / reputation of the mission's ledger rows (ELS, by the UI).</summary>
        public MissionProfileCareerTotals Career;
    }

    /// <summary>
    /// The Mission Profile builder: the one seam the Profile window calls
    /// (<see cref="BuildProfile"/>). Lines from the Missions tab's vessel rows, a phase plan
    /// per line from <see cref="MissionPhaseAnalyzer"/>, then the window's rows with their
    /// cells, hovers and flags, the Summary block first. Pure apart from one Verbose summary
    /// log line per build (and one per line), never per frame: the window caches the result
    /// by <c>RecordingStore.StateVersion</c> and <c>DisplayNames.Version</c>.
    /// </summary>
    internal static partial class MissionProfileBuilder
    {
        internal const string Tag = "Profile";

        private static CultureInfo Ic => CultureInfo.InvariantCulture;

        /// <summary>Builds the profile of one mission. Never throws on malformed data.</summary>
        internal static MissionProfile BuildProfile(MissionProfileRequest req)
        {
            var watch = Stopwatch.StartNew();
            var profile = new MissionProfile
            {
                MissionId = req?.Mission?.Id,
                TreeId = req?.Tree?.Id ?? req?.Mission?.TreeId,
                MissionName = req?.Mission?.Name ?? req?.Tree?.TreeName,
            };
            profile.Title = ProfileLabels.WindowTitle(profile.MissionName);
            if (req?.Tree == null)
            {
                AddEmptyRow(profile);
                LogSummary(profile, watch);
                return profile;
            }

            MissionStructure structure = req.Structure ?? MissionStructureBuilder.Build(req.Tree);
            List<MissionVesselRow> rows = req.VesselRows ?? BuildVesselRows(req, structure);
            profile.Lines.AddRange(MissionProfileLineBuilder.Build(req, structure, rows, out MissionProfileLineTally tally));
            string launchBody = LaunchBody(profile.Lines);
            foreach (MissionProfileLine line in MissionProfileLineBuilder.All(profile.Lines))
                if (line.IsDrawn) line.Plan = MissionPhaseAnalyzer.Analyze(ToInput(req, line, launchBody));

            var ctx = new ProfileRowContext { Request = req, Profile = profile, LaunchBody = launchBody };
            EmitRows(ctx);
            Count(profile);
            watch.Stop();
            profile.BuildMilliseconds = watch.Elapsed.TotalMilliseconds;
            LogLines(profile);
            LogSummary(profile, watch, tally);
            return profile;
        }

        /// <summary>The Missions tab's vessel rows of a tree, built exactly as the tab builds
        /// them (MissionsWindowUI.GetVesselRows).</summary>
        internal static List<MissionVesselRow> BuildVesselRows(MissionProfileRequest req, MissionStructure structure)
        {
            MissionThroughLineView view = req.ThroughLine ?? MissionThroughLineBuilder.Build(structure);
            IReadOnlyDictionary<string, string> names = req.VesselNames;
            return MissionVesselRowBuilder.Build(
                MissionCompositionBuilder.Build(structure),
                (head, ut) => MissionPresentation.ResolveSameTreeDockPartnerVesselName(structure, view, head, ut, names),
                names,
                req.PartnerLegIds,
                (head, ut) => MissionPresentation.ResolveTerminalDockPartnerVesselName(structure, head, ut, names),
                MissionVesselRowBuilder.ResolveUndockSides(structure, req.PartnerLegIds));
        }

        private static string LaunchBody(List<MissionProfileLine> lines)
        {
            foreach (MissionProfileLine l in lines)
                if (l.IsDrawn && l.Pieces.Count > 0 && !string.IsNullOrEmpty(l.Pieces[0].StartBodyName))
                    return l.Pieces[0].StartBodyName;
            foreach (MissionProfileLine l in lines)
                if (l.Pieces.Count > 0) return MissionPhaseAnalyzerBodyOf(l.Pieces[0]);
            return null;
        }

        private static string MissionPhaseAnalyzerBodyOf(Recording r)
        {
            if (r?.TrackSections == null) return null;
            foreach (TrackSection s in r.TrackSections)
            {
                string b = MissionPhaseAnalyzer.SectionBody(s);
                if (!string.IsNullOrEmpty(b)) return b;
            }
            return null;
        }

        private static VesselLineInput ToInput(MissionProfileRequest req, MissionProfileLine line, string launchBody)
        {
            var input = new VesselLineInput
            {
                LineId = line.LineId,
                IsEva = line.IsPerson,
                LaunchBodyName = launchBody,
                BodyFacts = req.BodyFacts,
            };
            input.Pieces.AddRange(line.Pieces);
            input.Anchors.AddRange(line.Anchors);
            input.ExcludedSpans.AddRange(line.ExcludedSpans);
            return input;
        }

        private static void AddEmptyRow(MissionProfile profile)
        {
            profile.IsEmpty = true;
            profile.Rows.Add(new MissionProfileRow
            {
                Kind = MissionProfileRowKind.Empty, Key = "empty", Element = ProfileLabels.EmptyText,
                Hover = ProfileLabels.EmptyText,
            });
        }

        private static void Count(MissionProfile profile)
        {
            foreach (MissionProfileLine l in MissionProfileLineBuilder.All(profile.Lines))
            {
                if (l.Plan == null) continue;
                profile.LineCount++;
                PhasePlan p = l.Plan;
                profile.Phases += CountPhases(p.Phases);
                profile.Events += p.Events;
                profile.Burns += p.Burns;
                profile.DvEstimated += p.DvEstimated;
                profile.DvRecorded += p.DvRecorded;
                profile.Inferred += p.Inferred;
                profile.OrbitUnknown += p.OrbitUnknown;
                profile.Unclassified += p.Unclassified;
                profile.Reflown += p.Reflown;
                profile.ClippedByInclude += p.ClippedByInclude + p.DroppedByInclude;
                profile.Gaps += p.Gaps.Count;
            }
        }

        private static int CountPhases(List<ProfilePhase> phases)
        {
            int n = 0;
            foreach (ProfilePhase p in phases)
                n += (p.Kind == ProfilePhaseKind.Prelaunch ? 0 : 1) + CountPhases(p.SubPhases);
            return n;
        }

        // ------------------------------------------------------------------ logging

        private static void LogSummary(MissionProfile p, Stopwatch watch,
            MissionProfileLineTally tally = default(MissionProfileLineTally))
        {
            ParsekLog.Verbose(Tag, string.Format(Ic,
                "Profile build: mission={0} tree={1} lines={2} phases={3} events={4} burns={5} " +
                "dvEstimated={6}/{7} dvRecorded={8} inferred={9} orbitUnknown={10} unclassified={11} " +
                "reflown={12} clippedByInclude={13} gaps={14} partners={15} collapsedEva={16} rows={17} ms={18:F2}",
                p.MissionId ?? "<none>", p.TreeId ?? "<none>", p.LineCount, p.Phases, p.Events, p.Burns,
                p.DvEstimated, p.Burns, p.DvRecorded, p.Inferred, p.OrbitUnknown, p.Unclassified,
                p.Reflown, p.ClippedByInclude, p.Gaps, tally.Partners, tally.Collapsed, p.Rows.Count,
                watch.Elapsed.TotalMilliseconds));
        }

        private static void LogLines(MissionProfile profile)
        {
            foreach (MissionProfileLine l in MissionProfileLineBuilder.All(profile.Lines))
            {
                if (l.Plan == null) continue;
                ParsekLog.Verbose(Tag, string.Format(Ic,
                    "Profile line: id={0} name='{1}' pieces={2} ut=[{3:F1},{4:F1}] phases={5} gaps={6} tiling={7}",
                    l.LineId, l.Name, l.Pieces.Count, l.Plan.StartUT, l.Plan.EndUT,
                    PhaseTrace(l.Plan.Phases), GapTrace(l.Plan.Gaps), l.Plan.TilingProblem ?? "ok"));
                foreach (ProfilePhase ph in l.Plan.Phases)
                {
                    if (ph.Kind != ProfilePhaseKind.Unclassified) continue;
                    string reason = ph.UnclassifiedReason ?? "unknown";
                    ParsekLog.InfoRateLimited(Tag, "profile-unclassified-" + l.LineId + "-" + reason,
                        string.Format(Ic, "Profile line has an In space phase: line={0} ut=[{1:F1},{2:F1}] reason={3}",
                            l.LineId, ph.StartUT, ph.EndUT, reason));
                }
            }
        }

        private static string PhaseTrace(List<ProfilePhase> phases)
        {
            var sb = new StringBuilder();
            foreach (ProfilePhase p in phases)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(p.Kind).Append('(').Append(p.Rule ?? "-").Append(')');
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static string GapTrace(List<ProfileDataGap> gaps)
        {
            if (gaps.Count == 0) return "none";
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (ProfileDataGap g in gaps)
            {
                if (!counts.ContainsKey(g.Reason)) { counts[g.Reason] = 0; order.Add(g.Reason); }
                counts[g.Reason]++;
            }
            var parts = new List<string>();
            foreach (string r in order) parts.Add(r + " x" + counts[r].ToString(Ic));
            return string.Join("; ", parts.ToArray());
        }
    }
}
